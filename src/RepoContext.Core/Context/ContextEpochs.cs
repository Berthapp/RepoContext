using System.Globalization;
using System.Text.Json;
using RepoContext.Core.Identity;

namespace RepoContext.Core.Context;

/// <summary>Why a context epoch was started.</summary>
public static class EpochReasons
{
    /// <summary>A new conversation.</summary>
    public const string Startup = "startup";

    /// <summary>A resumed conversation: what the model still holds is unproven.</summary>
    public const string Resume = "resume";

    /// <summary>The context was compacted; earlier evidence may be gone.</summary>
    public const string Compact = "compact";

    /// <summary>The conversation was cleared.</summary>
    public const string Clear = "clear";

    /// <summary>The agent forked; the fork is a second holder, not the same one.</summary>
    public const string Fork = "fork";

    /// <summary>The client said nothing the adapter recognizes.</summary>
    public const string Unknown = "unknown";
}

/// <summary>One agent's current context epoch in one repository.</summary>
/// <param name="AgentKey">Stable per client + client session, never the raw session id.</param>
/// <param name="Epoch">Monotonic counter; a new epoch invalidates the previous one's claims.</param>
/// <param name="Reason">One of <see cref="EpochReasons"/>.</param>
/// <param name="UpdatedUtc">When the epoch started.</param>
public sealed record ContextEpoch(string AgentKey, int Epoch, string Reason, DateTimeOffset UpdatedUtc)
{
    /// <summary>The session name this epoch owns.</summary>
    public string SessionName => ContextEpochs.SessionName(AgentKey, Epoch);
}

/// <summary>
/// Binds session evidence to the agent context that actually holds it
/// (milestone 3 of the same-quality, lower-cost plan).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionStore"/> records what was <i>delivered</i>. That is only
/// worth suppressing again while the conversation that received it still holds
/// it. A compaction, a resumed conversation, a cleared one or a fork all break
/// that link, and none of them touch the session file — so without this, a local
/// file would keep claiming possession on behalf of a model that no longer
/// remembers anything.
/// </para>
/// <para>
/// The rule is fail-closed: anything that might have dropped evidence starts a
/// new epoch, and a new epoch means a new session name and no inherited claims.
/// Keeping a receipt file is not proof of retention, so the adapter never
/// carries claims across an event it cannot verify. The cost of being wrong in
/// this direction is one repeated read; the cost of being wrong in the other
/// direction is an answer built on evidence the model never saw.
/// </para>
/// <para>
/// State is namespaced by repository (the file lives in that repository's own
/// <c>.repoctx/</c>, so a second worktree has a second file), by client and by
/// client session id. Two agents therefore never share possession claims, which
/// is the failure class ADR 0015 closed and this must not reopen.
/// </para>
/// </remarks>
public static class ContextEpochs
{
    private const int StoreLockTimeoutMilliseconds = 3_000;

    /// <summary>How many agents are remembered before the oldest is forgotten.</summary>
    private const int MaxTrackedAgents = 64;

    /// <summary>The file holding every tracked agent's current epoch.</summary>
    public static string PathFor(RepoLayout layout) =>
        Path.Combine(layout.IndexDirectory, "sessions", "epochs.json");

    /// <summary>
    /// The stable key for one agent instance: a client id plus a digest of the
    /// client's session id.
    /// </summary>
    /// <remarks>
    /// The raw session id is never stored. It identifies a conversation, it can
    /// appear in transcript paths, and the guard's local files must stay free of
    /// anything worth redacting before sharing.
    /// </remarks>
    public static string AgentKey(string clientId, string clientSessionId)
    {
        string digest = Canonical.Hash(clientId, clientSessionId);
        string prefix = Sanitize(clientId);
        return $"{prefix}-{digest[..12]}";
    }

    /// <summary>The session name an epoch owns: the agent key plus the epoch number.</summary>
    public static string SessionName(string agentKey, int epoch) =>
        $"{agentKey}-e{epoch.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Splits an epoch-bound session name again. Manual names (<c>--session
    /// review</c>) are not epoch-bound and return false, which is what keeps
    /// their existing behaviour intact.
    /// </summary>
    public static bool TryParseSessionName(string? name, out string agentKey, out int epoch)
    {
        agentKey = string.Empty;
        epoch = 0;
        if (name is null)
        {
            return false;
        }

        int marker = name.LastIndexOf("-e", StringComparison.Ordinal);
        if (marker <= 0 || marker + 2 >= name.Length)
        {
            return false;
        }

        string suffix = name[(marker + 2)..];
        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out epoch)
            || epoch <= 0)
        {
            return false;
        }

        agentKey = name[..marker];
        return true;
    }

    /// <summary>The agent's current epoch, or null when it is not tracked here.</summary>
    public static ContextEpoch? Current(RepoLayout layout, string agentKey)
    {
        EpochFile file = Read(PathFor(layout));
        return file.Sessions.TryGetValue(agentKey, out EpochEntry? entry)
            ? new ContextEpoch(agentKey, entry.Epoch, entry.Reason, entry.Updated)
            : null;
    }

    /// <summary>
    /// Starts the next epoch for an agent and returns it. Every lifecycle event
    /// the adapter cannot prove harmless goes through here.
    /// </summary>
    public static ContextEpoch Advance(RepoLayout layout, string agentKey, string reason)
    {
        string path = PathFor(layout);
        var started = new ContextEpoch(agentKey, 1, reason, DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using PathScopedMutex? lease = PathScopedMutex.TryAcquire(
                "Epochs", path, StoreLockTimeoutMilliseconds);

            EpochFile file = Read(path);
            int next = file.Sessions.TryGetValue(agentKey, out EpochEntry? existing)
                ? existing.Epoch + 1
                : 1;
            started = new ContextEpoch(agentKey, next, reason, DateTimeOffset.UtcNow);
            file.Sessions[agentKey] = new EpochEntry
            {
                Epoch = next,
                Reason = reason,
                Updated = started.UpdatedUtc,
            };

            Write(path, file);
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or JsonException
                or WaitHandleCannotBeOpenedException)
        {
            // A lifecycle store that cannot be written must not break the agent.
            // The caller then simply does not get reuse, which is the safe side.
        }

        return started;
    }

    /// <summary>Forgets an agent, e.g. when its session ends.</summary>
    public static void Retire(RepoLayout layout, string agentKey)
    {
        string path = PathFor(layout);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using PathScopedMutex? lease = PathScopedMutex.TryAcquire(
                "Epochs", path, StoreLockTimeoutMilliseconds);
            EpochFile file = Read(path);
            if (file.Sessions.Remove(agentKey))
            {
                Write(path, file);
            }
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or JsonException
                or WaitHandleCannotBeOpenedException)
        {
        }
    }

    /// <summary>
    /// Whether a session name belongs to a context epoch that has been replaced.
    /// </summary>
    /// <remarks>
    /// A manual session name is never superseded — <c>--session</c> keeps working
    /// exactly as before. An epoch-bound name whose agent has moved on is: the
    /// evidence in it was delivered to a context that no longer exists.
    /// </remarks>
    public static bool IsSuperseded(RepoLayout layout, string? sessionName)
    {
        if (!TryParseSessionName(sessionName, out string agentKey, out int epoch))
        {
            return false;
        }

        ContextEpoch? current = Current(layout, agentKey);
        return current is not null && current.Epoch != epoch;
    }

    private static string Sanitize(string clientId)
    {
        var sb = new System.Text.StringBuilder(clientId.Length);
        foreach (char c in clientId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        string cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 ? "agent" : cleaned[..Math.Min(cleaned.Length, 16)];
    }

    private static EpochFile Read(string path)
    {
        if (!File.Exists(path))
        {
            return new EpochFile();
        }

        try
        {
            EpochFile? file = JsonSerializer.Deserialize<EpochFile>(
                File.ReadAllText(path), SerializerOptions);
            // An unknown version is discarded, never migrated on a guess: a wrong
            // guess here would resurrect possession claims.
            return file is null || file.V != EpochFile.CurrentVersion ? new EpochFile() : file;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new EpochFile();
        }
    }

    private static void Write(string path, EpochFile file)
    {
        foreach (string stale in file.Sessions
            .OrderByDescending(pair => pair.Value.Updated)
            .Skip(MaxTrackedAgents)
            .Select(pair => pair.Key)
            .ToList())
        {
            file.Sessions.Remove(stale);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, SerializerOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record EpochFile
    {
        public const int CurrentVersion = 1;

        public int V { get; init; } = CurrentVersion;

        public Dictionary<string, EpochEntry> Sessions { get; init; } =
            new(StringComparer.Ordinal);
    }

    private sealed record EpochEntry
    {
        public int Epoch { get; init; }

        public string Reason { get; init; } = EpochReasons.Unknown;

        public DateTimeOffset Updated { get; init; }
    }
}
