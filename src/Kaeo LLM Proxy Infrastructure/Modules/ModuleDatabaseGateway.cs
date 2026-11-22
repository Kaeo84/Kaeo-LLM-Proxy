using System.Data.Common;
using Kaeo.LlmProxy.Core.Modules;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Implements the contracts-library <see cref="IModuleDatabase"/> gateway over the shared
/// <see cref="AppDatabase"/> so module data lives in the existing database file. Commands run
/// on dedicated connections outside the host lock; WAL and the busy timeout serialize writers.
/// </summary>
/// <remarks>
/// L26: sharing/busy/locked failures (concurrent app instances, WAL contention) are logged as a
/// warning at the gateway and then rethrown unchanged. The original exception type is preserved so
/// module code that catches <see cref="SqliteException"/> or <see cref="IOException"/> keeps working;
/// tool-call failures still degrade to MCP tool errors (the SDK catches them) and schema failures
/// still mark the module errored. The gateway adds observability, not a new swallow-and-default path.
/// </remarks>
internal sealed class ModuleDatabaseGateway(AppDatabase database) : IModuleDatabase
{
    private readonly AppDatabase _database = database;

    public void ExecuteSchemaScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        Run("schema script", () => _database.ExecuteModuleSchemaScript(script));
    }

    public int Execute(string commandText, Action<DbCommand>? configureCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        return Run("execute", () => _database.ExecuteModuleNonQuery(commandText, configureCommand));
    }

    public object? ExecuteScalar(string commandText, Action<DbCommand>? configureCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        return Run("scalar", () => _database.ExecuteModuleScalar(commandText, configureCommand));
    }

    public IReadOnlyList<T> Query<T>(
        string commandText,
        Func<DbDataReader, T> map,
        Action<DbCommand>? configureCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        ArgumentNullException.ThrowIfNull(map);
        return Run("query", () => _database.ExecuteModuleQuery(commandText, map, configureCommand));
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside a single transaction on a dedicated connection,
    /// committing on return and rolling back if it throws. Part of the Core
    /// <see cref="IModuleDatabase"/> contract so modules can apply related writes atomically.
    /// </summary>
    public void ExecuteInTransaction(Action<DbConnection, DbTransaction> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Run("transaction", () => _database.ExecuteModuleTransaction(work));
    }

    private static void Run(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsSharingViolation(ex))
        {
            Log.Warning(ex, "Module database {Operation} hit a sharing/busy/locked condition; rethrowing for the caller to handle", operation);
            throw;
        }
    }

    private static T Run<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (IsSharingViolation(ex))
        {
            Log.Warning(ex, "Module database {Operation} hit a sharing/busy/locked condition; rethrowing for the caller to handle", operation);
            throw;
        }
    }

    private static bool IsSharingViolation(Exception ex) =>
        ex is IOException
        || (ex is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6); // SQLITE_BUSY / SQLITE_LOCKED
}
