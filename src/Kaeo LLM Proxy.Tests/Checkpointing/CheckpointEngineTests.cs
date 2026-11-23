using System.Diagnostics;
using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the shadow-git checkpoint engine against real git in temp directories:
/// create/list/diff/rollback semantics, new-file handling, excludes, retention,
/// and that the user's real .git is never touched.
/// </summary>
public class CheckpointEngineTests : IDisposable
{
    private readonly string _root;
    private readonly string _work;
    private readonly string _store;

    public CheckpointEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kaeo-ckpt-tests", Guid.NewGuid().ToString("N"));
        _work = Path.Combine(_root, "work");
        _store = Path.Combine(_root, "store");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_store);
    }

    public void Dispose()
    {
        try
        {
            // Git marks object files read-only; clear attributes before deleting.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<GitRunResult> RunGitAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = _work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new GitRunResult(proc.ExitCode, stdout, stderr);
    }

    private CheckpointEngine NewEngine(int retention = 20, IEnumerable<string>? excludes = null) =>
        new(_store, _work, RunGitAsync, retention, excludes);

    private void Write(string rel, string content) =>
        File.WriteAllText(Path.Combine(_work, rel), content);

    private string Read(string rel) =>
        File.ReadAllText(Path.Combine(_work, rel));

    [Fact]
    public async Task Create_FirstSnapshot_CommitsFiles()
    {
        Write("a.txt", "one");
        var result = await NewEngine().CreateAsync("first");
        Assert.True(result.Success, result.Error);
        Assert.False(result.Unchanged);
        Assert.NotNull(result.Checkpoint);
        Assert.Equal("first", result.Checkpoint!.Label);
    }

    [Fact]
    public async Task Create_NoChanges_ReturnsUnchangedSameId()
    {
        Write("a.txt", "one");
        var engine = NewEngine();
        var first = await engine.CreateAsync("first");
        Assert.True(first.Success, first.Error);

        var second = await engine.CreateAsync("second");
        Assert.True(second.Success, second.Error);
        Assert.True(second.Unchanged);
        Assert.Equal(first.Checkpoint!.Id, second.Checkpoint!.Id);
    }

    [Fact]
    public async Task Create_AfterEdit_NewCheckpoint()
    {
        Write("a.txt", "one");
        var engine = NewEngine();
        var first = await engine.CreateAsync("first");
        Write("a.txt", "two");
        var second = await engine.CreateAsync("edit");
        Assert.True(second.Success, second.Error);
        Assert.False(second.Unchanged);
        Assert.NotEqual(first.Checkpoint!.Id, second.Checkpoint!.Id);
    }

    [Fact]
    public async Task List_NewestFirst()
    {
        Write("a.txt", "one");
        var engine = NewEngine();
        await engine.CreateAsync("first");
        Write("a.txt", "two");
        await engine.CreateAsync("second");

        var list = await engine.ListAsync();
        Assert.True(list.Success, list.Error);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("second", list.Items[0].Label);
        Assert.Equal("first", list.Items[1].Label);
    }

    [Fact]
    public async Task Diff_ShowsWorktreeChanges()
    {
        Write("a.txt", "one");
        var engine = NewEngine();
        var first = await engine.CreateAsync("first");
        Write("a.txt", "two");

        var diff = await engine.DiffAsync(first.Checkpoint!.Id);
        Assert.True(diff.Success, diff.Error);
        Assert.Contains("a.txt", diff.DiffStat);
    }

    [Fact]
    public async Task Rollback_RestoresModifiedAndDeletedFiles_KeepsNewFiles()
    {
        Write("a.txt", "original");
        Write("b.txt", "keep");
        var engine = NewEngine();
        var first = await engine.CreateAsync("first");

        Write("a.txt", "changed");
        File.Delete(Path.Combine(_work, "b.txt"));
        Write("new.txt", "created after snapshot");

        var rollback = await engine.RollbackAsync(first.Checkpoint!.Id);
        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal("original", Read("a.txt"));
        Assert.Equal("keep", Read("b.txt"));
        Assert.True(File.Exists(Path.Combine(_work, "new.txt")));
        Assert.Contains("new.txt", rollback.NewFiles);
        Assert.Empty(rollback.DeletedNewFiles);
    }

    [Fact]
    public async Task Rollback_DeleteNewFiles_RemovesThem()
    {
        Write("a.txt", "original");
        var engine = NewEngine();
        var first = await engine.CreateAsync("first");
        Write("new.txt", "created after snapshot");

        var rollback = await engine.RollbackAsync(first.Checkpoint!.Id, deleteNewFiles: true);
        Assert.True(rollback.Success, rollback.Error);
        Assert.False(File.Exists(Path.Combine(_work, "new.txt")));
        Assert.Contains("new.txt", rollback.DeletedNewFiles);
    }

    [Fact]
    public async Task Rollback_RecordsBookkeepingCheckpoint()
    {
        Write("a.txt", "original");
        var engine = NewEngine();
        var first = await engine.CreateAsync("first");
        Write("a.txt", "changed");
        await engine.RollbackAsync(first.Checkpoint!.Id);

        var list = await engine.ListAsync();
        Assert.True(list.Success);
        Assert.StartsWith("rollback to", list.Items[0].Label);
    }

    [Fact]
    public async Task Excludes_BinObjNotSnapshotted()
    {
        Write("a.txt", "one");
        Directory.CreateDirectory(Path.Combine(_work, "bin"));
        Write(Path.Combine("bin", "out.dll"), "v1");
        var engine = NewEngine();
        var first = await engine.CreateAsync("first");

        // Only an excluded file changed: nothing to checkpoint.
        Write(Path.Combine("bin", "out.dll"), "v2");
        var second = await engine.CreateAsync("bin-only");
        Assert.True(second.Success, second.Error);
        Assert.True(second.Unchanged);
        Assert.Equal(first.Checkpoint!.Id, second.Checkpoint!.Id);
    }

    [Fact]
    public async Task ExtraExcludes_Honored()
    {
        Write("a.txt", "one");
        Write("scratch.tmp", "v1");
        var engine = NewEngine(excludes: new[] { "*.tmp" });
        var first = await engine.CreateAsync("first");

        Write("scratch.tmp", "v2");
        var second = await engine.CreateAsync("tmp-only");
        Assert.True(second.Success, second.Error);
        Assert.True(second.Unchanged);
        Assert.Equal(first.Checkpoint!.Id, second.Checkpoint!.Id);
    }

    [Fact]
    public async Task Retention_WipesOldHistory()
    {
        var engine = NewEngine(retention: 2);
        Write("a.txt", "1");
        await engine.CreateAsync("c1");
        Write("a.txt", "2");
        await engine.CreateAsync("c2");
        Write("a.txt", "3");
        var third = await engine.CreateAsync("c3");

        Assert.True(third.Success, third.Error);
        Assert.True(third.PrunedOlder);
        var list = await engine.ListAsync();
        Assert.Single(list.Items);
        Assert.Equal("c3", list.Items[0].Label);
    }

    [Fact]
    public async Task InvalidId_Rejected()
    {
        Write("a.txt", "one");
        var engine = NewEngine();
        await engine.CreateAsync("first");

        var diff = await engine.DiffAsync("HEAD; rm -rf /");
        Assert.False(diff.Success);
        var rollback = await engine.RollbackAsync("--work-tree=/elsewhere");
        Assert.False(rollback.Success);
    }

    [Fact]
    public async Task UnknownId_Rejected()
    {
        Write("a.txt", "one");
        var engine = NewEngine();
        await engine.CreateAsync("first");
        var diff = await engine.DiffAsync("deadbeef");
        Assert.False(diff.Success);
        Assert.Contains("Unknown checkpoint", diff.Error);
    }

    [Fact]
    public async Task RealGit_NeverTouched()
    {
        // Give the worktree a real git repo with its own commit.
        var init = await RunGitInAsync(_work, "init");
        Assert.Equal(0, init.ExitCode);
        await RunGitInAsync(_work, "config user.name tester");
        await RunGitInAsync(_work, "config user.email t@t.local");
        Write("a.txt", "one");
        await RunGitInAsync(_work, "add -A");
        await RunGitInAsync(_work, "commit -m real");
        var realHead = (await RunGitInAsync(_work, "rev-parse HEAD")).StdOut.Trim();

        var engine = NewEngine();
        var first = await engine.CreateAsync("shadow");
        Write("a.txt", "two");
        await engine.CreateAsync("shadow2");
        await engine.RollbackAsync(first.Checkpoint!.Id);

        var afterHead = (await RunGitInAsync(_work, "rev-parse HEAD")).StdOut.Trim();
        var status = (await RunGitInAsync(_work, "status --porcelain")).StdOut.Trim();
        Assert.Equal(realHead, afterHead);
        Assert.Equal(string.Empty, status);
    }

    private static async Task<GitRunResult> RunGitInAsync(string dir, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return new GitRunResult(proc.ExitCode, stdout, stderr);
    }
}