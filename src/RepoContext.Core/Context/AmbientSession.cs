namespace RepoContext.Core.Context;

/// <summary>
/// The session name taken from the environment when the caller passes none
/// (ADR 0018).
/// </summary>
/// <remarks>
/// <para>
/// <c>--session</c> is the cheapest reuse RepoContext offers: it costs no output
/// tokens at all, because the caller echoes nothing. Its weakness is that it has
/// to be remembered on every call, and an agent that forgets it silently pays
/// full price for evidence it already holds. Reading
/// <c>REPOCTX_SESSION</c> makes reuse a property of how the tool was launched
/// rather than of what the agent remembered to type — an MCP server entry, a
/// shell profile or a CI job sets it once.
/// </para>
/// <para>
/// It stays opt-in, and deliberately is not written into generated MCP
/// configuration. A session records what was <i>delivered</i>, so two agents
/// sharing one session name in one repository would let the second receive reuse
/// markers for evidence it never saw — the exact false-cache-hit class ADR 0015
/// closed. A session name must therefore identify one agent instance, which only
/// whoever launches the agent can know.
/// </para>
/// <para>
/// Determinism is unaffected: the resolved name selects a caller-supplied input
/// file, exactly as an explicit <c>--session</c> does.
/// </para>
/// </remarks>
public static class AmbientSession
{
    /// <summary>The environment variable read when no session is passed explicitly.</summary>
    public const string VariableName = "REPOCTX_SESSION";

    /// <summary>
    /// Reads the ambient session name. Returns false when the variable is unset
    /// or empty; the value is returned unvalidated so the caller can report a
    /// malformed one against the variable rather than against <c>--session</c>.
    /// </summary>
    public static bool TryGetName(out string? name)
    {
        string? value = Environment.GetEnvironmentVariable(VariableName);
        name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return name is not null;
    }
}
