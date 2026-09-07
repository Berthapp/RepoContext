namespace RepoContext.Core.Graph;

/// <summary>A local dependency target, or the reason it could not be established.</summary>
internal readonly record struct ImportResolution(string? Path, string? Reason = null);

/// <summary>An indexed dependency reference for which no reliable local edge exists.</summary>
public sealed record UnresolvedReference(string Kind, string Value, int Line, string Reason);
