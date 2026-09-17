namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// What the client asked for in its top-level <c>stream_options</c> block, captured while the request
/// body is normalized. The block itself is stripped from the upstream-bound request because many
/// local OpenAI-compatible servers reject or ignore it, but the client is still owed what it asked
/// for — so the response path reads this and synthesizes the missing pieces itself.
/// </summary>
/// <param name="Present">True when the inbound body actually carried a <c>stream_options</c> member.</param>
/// <param name="IncludeUsage">
/// True when <c>stream_options.include_usage</c> was set, meaning the client expects a terminal chunk
/// carrying token usage. Drives whether <see cref="OpenAiStreamTerminator"/> synthesizes one.
/// </param>
/// <param name="Stripped">
/// True when Copilot compatibility applied and the member was removed from the upstream-bound body.
/// False when the mapping opted out and the member was forwarded to the upstream unchanged, in which
/// case the upstream remains responsible for reporting usage.
/// </param>
internal readonly record struct StreamOptionsInfo(bool Present, bool IncludeUsage, bool Stripped)
{
    /// <summary>Nothing was learned about <c>stream_options</c> — the body carried no such member.</summary>
    public static StreamOptionsInfo None { get; } = new(false, false, false);

    /// <summary>
    /// True when the proxy took over responsibility for the terminal usage chunk: the client asked for
    /// it and the block that would have made the upstream produce it was removed.
    /// </summary>
    public bool MustSynthesizeUsage => IncludeUsage && Stripped;
}
