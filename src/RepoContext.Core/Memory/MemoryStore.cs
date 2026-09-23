using System.Text;
using System.Text.Json;
using RepoContext.Core.Identity;

namespace RepoContext.Core.Memory;

/// <summary>
/// Persists agent memories in <c>.repoctx/memory.jsonl</c> (ADR 0013): one
/// JSON object per line, append-only, the latest line per id wins and a
/// <c>deleted</c> line is a tombstone. The file is caller input exactly like a
/// session file, so the determinism contract holds: identical index + query +
/// memory file ⇒ identical output.
/// </summary>
/// <remarks>
/// JSONL keeps the store human-auditable (it lives inside the git-ignored
/// <c>.repoctx/</c> directory, alongside the equally sensitive index) and
/// keeps memory out of the SQLite schema — a re-index never touches learned
/// knowledge, and no schema migration is needed. Reading is lenient (a corrupt
/// line is skipped, a missing file is an empty store) so a damaged store can
/// never break a query; writing is strict, because silently dropping knowledge
/// an agent asked to keep would defeat the feature.
/// <para>
/// Every line carries an HMAC under the machine key (ADR 0025), and a line
/// without a valid one is ignored. A hostile checkout - or a later pull - can
/// otherwise place "constraints" in this file that every matching
/// <c>context</c> answer presents to an agent as its own team's knowledge,
/// where no code review of the repository would ever see them. Lines written
/// before signing existed are kept, reported, and become live again only when a
/// person runs <c>repoctx memory adopt</c>.
/// </para>
/// </remarks>
public static class MemoryStore
{
    private const int StoreLockTimeoutMilliseconds = 3_000;

    /// <summary>Live entries beyond this cap are refused; curation beats silent eviction.</summary>
    public const int MaxEntries = 500;

    /// <summary>Text cap (characters) — memories must stay cheaper than the reads they replace.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>Cap on linked files per entry.</summary>
    public const int MaxFiles = 8;

    /// <summary>Cap on tags per entry.</summary>
    public const int MaxTags = 8;

    /// <summary>Rewrite the log once dead lines outnumber this threshold.</summary>
    private const int CompactionSlack = 1000;

    private const string SignaturePurpose = "repoctx.memory.v1";

    /// <summary>The on-disk path of the memory log.</summary>
    public static string PathFor(RepoLayout layout) =>
        Path.Combine(layout.IndexDirectory, "memory.jsonl");

    /// <summary>
    /// Loads all live entries in first-added order. Lenient: a missing file is
    /// an empty store and unparsable lines are skipped, so a damaged store
    /// degrades to less memory instead of a failed query.
    /// </summary>
    public static IReadOnlyList<MemoryEntry> Load(RepoLayout layout)
    {
        string path = PathFor(layout);
        try
        {
            using PathScopedMutex? lease = PathScopedMutex.TryAcquire(
                "Memory", path, StoreLockTimeoutMilliseconds);
            if (lease is null)
            {
                // Appends are single locked writes and compaction uses atomic
                // replacement, so a lock-free reader still sees a valid
                // pre- or post-mutation snapshot. Do not silently hide all
                // memory merely because another agent held the lock briefly.
                return LoadUnlocked(path);
            }

            return LoadUnlocked(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Adds or updates <paramref name="entry"/> (same id ⇒ update in place —
    /// ids are content-addressed, so re-remembering refreshes file hashes
    /// instead of duplicating). Returns whether an existing entry was updated.
    /// Throws when the store is full; the caller reports that as an error.
    /// </summary>
    public static bool Add(RepoLayout layout, MemoryEntry entry)
    {
        string path = PathFor(layout);
        using PathScopedMutex lease = AcquireForMutation(path);
        IReadOnlyList<MemoryEntry> live = LoadUnlocked(path);
        bool update = live.Any(e => e.Id == entry.Id);
        if (!update && live.Count >= MaxEntries)
        {
            throw new InvalidOperationException(
                $"Memory is full ({MaxEntries} entries). Remove entries with 'repoctx memory rm <id>' first.");
        }

        Append(layout, path, Serialize(ToStored(entry)));
        return update;
    }

    /// <summary>
    /// Removes the entry with <paramref name="id"/> by appending a tombstone.
    /// Returns false when no live entry has that id.
    /// </summary>
    public static bool Remove(RepoLayout layout, string id)
    {
        string path = PathFor(layout);
        using PathScopedMutex lease = AcquireForMutation(path);
        if (LoadUnlocked(path).All(e => e.Id != id))
        {
            return false;
        }

        Append(layout, path, Serialize(new StoredLine { Id = id, Deleted = true }));
        return true;
    }

    /// <summary>
    /// Entries in the log that carry no valid signature from this machine, in
    /// file order. They take no part in recall until a person adopts them.
    /// </summary>
    public static IReadOnlyList<UnverifiedMemory> Unverified(RepoLayout layout)
    {
        string path = PathFor(layout);
        try
        {
            return File.Exists(path) ? Read(File.ReadAllLines(path)).Unverified : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Signs every unverified line as written on this machine, so it takes part
    /// in recall again. Returns how many lines were adopted.
    /// </summary>
    /// <remarks>
    /// For entries a person wrote before memories were signed. It is never
    /// reachable from the MCP surface, and the CLI only offers it at an
    /// interactive terminal after listing what it will adopt: an entry that
    /// arrived with a checkout must not become trusted because an agent asked.
    /// </remarks>
    public static int Adopt(RepoLayout layout)
    {
        string path = PathFor(layout);
        using PathScopedMutex lease = AcquireForMutation(path);
        if (!File.Exists(path))
        {
            return 0;
        }

        int adopted = 0;
        var rewritten = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (Parse(line) is { } stored && !IsAuthentic(stored))
            {
                rewritten.Add(Serialize(stored));
                adopted++;
            }
            else
            {
                rewritten.Add(line);
            }
        }

        if (adopted > 0)
        {
            layout.PrepareIndexFile(path);
            SafePaths.WriteAllTextAtomic(path, string.Concat(rewritten.Select(l => l + "\n")));
        }

        return adopted;
    }

    private static PathScopedMutex AcquireForMutation(string path) =>
        PathScopedMutex.TryAcquire("Memory", path, StoreLockTimeoutMilliseconds)
        ?? throw new TimeoutException("Timed out waiting to update local agent memory.");

    private static IReadOnlyList<MemoryEntry> LoadUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return Read(File.ReadAllLines(path)).Live;
    }

    private static void Append(RepoLayout layout, string path, string line)
    {
        layout.PrepareIndexFile(path);
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        using (var stream = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            // A process crash can leave a partial tail. Keep it as an
            // auditable corrupt line, but separate it from the new valid one.
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                {
                    stream.WriteByte((byte)'\n');
                }
            }

            stream.Seek(0, SeekOrigin.End);
            stream.Write(bytes);
        }

        TryCompact(path);
    }

    /// <summary>
    /// Rewrites the log without superseded lines once they pile up. Order of
    /// the surviving entries is preserved, so reads before and after a
    /// compaction see the same store.
    /// </summary>
    private static void TryCompact(string path)
    {
        try
        {
            string[] lines = File.ReadAllLines(path);
            LogContents contents = Read(lines);
            if (lines.Length <= contents.Live.Count + contents.UnverifiedLines.Count + CompactionSlack)
            {
                return;
            }

            // Unverified lines are carried over verbatim and first, so a person
            // can still review and adopt them after a compaction.
            string compacted = string.Join('\n', contents.UnverifiedLines
                .Concat(contents.Live.Select(e => Serialize(ToStored(e)))));
            if (compacted.Length > 0)
            {
                compacted += "\n";
            }

            SafePaths.WriteAllTextAtomic(path, compacted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Compaction is optional. The mutation was already successfully
            // appended, so leave the longer valid log for a later attempt.
        }
    }

    private static StoredLine ToStored(MemoryEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind,
        Text = entry.Text,
        Files = entry.Files.Count > 0 ? entry.Files.ToDictionary(StringComparer.Ordinal) : null,
        Tags = entry.Tags.Count > 0 ? entry.Tags.ToList() : null,
        Session = entry.Session,
        Created = entry.Created,
    };

    /// <summary>Serializes a line with this machine's signature over everything it says.</summary>
    private static string Serialize(StoredLine line) => JsonSerializer.Serialize(
        line with { Mac = MachineKey.Sign(SignaturePurpose, Signed(line)) }, SerializerOptions);

    private static bool IsAuthentic(StoredLine line) =>
        MachineKey.Verify(line.Mac, SignaturePurpose, Signed(line));

    /// <summary>
    /// The canonical fields a signature covers: every field that changes what
    /// recall returns, so no part of a signed line can be edited or moved to
    /// another entry. Null values - which RepoContext never writes, but a
    /// planted line can hold - encode as empty rather than failing the read.
    /// </summary>
    private static string[] Signed(StoredLine line) =>
    [
        line.Id ?? string.Empty,
        line.Kind ?? string.Empty,
        line.Text ?? string.Empty,
        Canonical.JoinRecords((line.Files ?? [])
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => new[] { pair.Key, pair.Value ?? string.Empty })),
        Canonical.JoinRecords((line.Tags ?? []).Select(tag => tag ?? string.Empty)),
        line.Session ?? string.Empty,
        line.Created ?? string.Empty,
        line.Deleted == true ? "deleted" : string.Empty,
    ];

    /// <summary>A line that parses as a memory line with an id, or null.</summary>
    private static StoredLine? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredLine>(line, SerializerOptions) is { Id.Length: > 0 } stored
                ? stored
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Signed lines folded into live entries, and the unsigned ones set aside.</summary>
    private static LogContents Read(IReadOnlyList<string> lines)
    {
        var signed = new List<string>(lines.Count);
        var unverifiedLines = new List<string>();
        var unverified = new List<UnverifiedMemory>();
        foreach (string line in lines)
        {
            if (Parse(line) is { } stored && !IsAuthentic(stored))
            {
                unverifiedLines.Add(line);
                unverified.Add(new UnverifiedMemory(
                    stored.Id!, stored.Kind, stored.Text, stored.Deleted == true));
            }
            else
            {
                signed.Add(line);
            }
        }

        return new LogContents(Fold(signed), unverifiedLines, unverified);
    }

    private sealed record LogContents(
        IReadOnlyList<MemoryEntry> Live,
        IReadOnlyList<string> UnverifiedLines,
        IReadOnlyList<UnverifiedMemory> Unverified);

    /// <summary>Folds raw lines into live entries: latest per id wins, tombstones delete.</summary>
    private static IReadOnlyList<MemoryEntry> Fold(IEnumerable<string> lines)
    {
        // Order is tracked explicitly (Dictionary order after removals is
        // undefined) so the store reads back deterministically: first-learned
        // first, updates in place.
        var byId = new Dictionary<string, MemoryEntry>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            StoredLine? stored;
            try
            {
                stored = JsonSerializer.Deserialize<StoredLine>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (stored?.Id is not { Length: > 0 } id)
            {
                continue;
            }

            if (stored.Deleted == true)
            {
                if (byId.Remove(id))
                {
                    order.Remove(id);
                }

                continue;
            }

            if (!MemoryKinds.IsValid(stored.Kind) || stored.Text is not { Length: > 0 })
            {
                continue;
            }

            var entry = new MemoryEntry
            {
                Id = id,
                Kind = stored.Kind!,
                Text = stored.Text,
                Files = new SortedDictionary<string, string>(
                    stored.Files ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                Tags = stored.Tags ?? [],
                Session = stored.Session,
                Created = stored.Created ?? string.Empty,
            };

            if (!byId.ContainsKey(id))
            {
                order.Add(id);
            }

            byId[id] = entry;
        }

        return order.Select(id => byId[id]).ToList();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private sealed record StoredLine
    {
        public string? Id { get; init; }

        public string? Kind { get; init; }

        public string? Text { get; init; }

        public Dictionary<string, string>? Files { get; init; }

        public List<string>? Tags { get; init; }

        public string? Session { get; init; }

        public string? Created { get; init; }

        public bool? Deleted { get; init; }

        /// <summary>HMAC under the machine key over <see cref="Signed"/> (ADR 0025).</summary>
        public string? Mac { get; init; }
    }
}

/// <summary>A memory line that carries no valid signature from this machine.</summary>
/// <param name="Id">The entry id the line claims.</param>
/// <param name="Kind">Its kind, when it has one.</param>
/// <param name="Text">Its text, when it has one.</param>
/// <param name="Deleted">Whether the line is a removal.</param>
public sealed record UnverifiedMemory(string Id, string? Kind, string? Text, bool Deleted);
