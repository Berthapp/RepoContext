namespace RepoContext.Core.Guard;

/// <summary>
/// One file read the guard was asked to judge, in the client's own terms.
/// </summary>
/// <param name="Path">The path exactly as the caller wrote it; resolution happens later.</param>
/// <param name="StartLine">First requested line (1-based) when the caller named one.</param>
/// <param name="LineLimit">How many lines were requested; null means the whole file.</param>
/// <param name="Origin">Where the request came from, e.g. <c>read-tool</c> or <c>shell:head</c>.</param>
public sealed record GuardReadRequest(
    string Path, int? StartLine, int? LineLimit, string Origin)
{
    /// <summary>Whether the caller asked for a bounded part of the file.</summary>
    public bool IsPartial => LineLimit is > 0;
}
