using RepoContext.Core;
using RepoContext.Core.Memory;
using System.Text.Json;

namespace RepoContext.Core.Tests.Memory;

/// <summary>
/// The JSONL memory store (ADR 0013): content-addressed add/update, tombstone
/// removal, deterministic ordering, lenient reads and the entry cap.
/// </summary>
public class MemoryStoreTests : IDisposable
{
    private readonly string _root;
    private readonly RepoLayout _layout;

    public MemoryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "repoctx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _layout = RepoLayout.For(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static MemoryEntry Entry(
        string text, string kind = MemoryKinds.Note, string? session = null,
        IReadOnlyDictionary<string, string>? files = null, IReadOnlyList<string>? tags = null)
    {
        files ??= new Dictionary<string, string>(StringComparer.Ordinal);
        return new MemoryEntry
        {
            Id = MemoryEntry.ComputeId(kind, text, session, files.Keys),
            Kind = kind,
            Text = text,
            Files = files,
            Tags = tags ?? [],
            Session = session,
            Created = "2026-07-20",
        };
    }

    [Fact]
    public void Add_ThenLoad_RoundTripsAllFields()
    {
        var entry = Entry("JWT chosen over cookies: mobile clients.", MemoryKinds.Decision,
            session: "s1",
            files: new Dictionary<string, string> { ["src/auth/login.ts"] = "abcdef123456" },
            tags: ["auth", "jwt"]);

        bool updated = MemoryStore.Add(_layout, entry);
        IReadOnlyList<MemoryEntry> loaded = MemoryStore.Load(_layout);

        Assert.False(updated);
        MemoryEntry roundTripped = Assert.Single(loaded);
        Assert.Equal(entry.Id, roundTripped.Id);
        Assert.Equal(MemoryKinds.Decision, roundTripped.Kind);
        Assert.Equal(entry.Text, roundTripped.Text);
        Assert.Equal("abcdef123456", roundTripped.Files["src/auth/login.ts"]);
        Assert.Equal(["auth", "jwt"], roundTripped.Tags);
        Assert.Equal("s1", roundTripped.Session);
        Assert.Equal("2026-07-20", roundTripped.Created);
    }

    [Fact]
    public void ComputeId_IsDeterministic_AndSensitiveToIdentityFields()
    {
        string a = MemoryEntry.ComputeId("note", "text", null, ["a.ts"]);
        Assert.Equal(a, MemoryEntry.ComputeId("note", "text", null, ["a.ts"]));
        Assert.Equal(MemoryEntry.IdLength, a.Length);

        Assert.NotEqual(a, MemoryEntry.ComputeId("decision", "text", null, ["a.ts"]));
        Assert.NotEqual(a, MemoryEntry.ComputeId("note", "other", null, ["a.ts"]));
        Assert.NotEqual(a, MemoryEntry.ComputeId("note", "text", "s1", ["a.ts"]));
        Assert.NotEqual(a, MemoryEntry.ComputeId("note", "text", null, ["b.ts"]));

        // File order must not matter — the path set is sorted before hashing.
        Assert.Equal(
            MemoryEntry.ComputeId("note", "text", null, ["b.ts", "a.ts"]),
            MemoryEntry.ComputeId("note", "text", null, ["a.ts", "b.ts"]));
    }

    [Fact]
    public void Add_SameId_UpdatesInPlace_KeepingPosition()
    {
        MemoryStore.Add(_layout, Entry("first"));
        MemoryStore.Add(_layout, Entry("second"));

        var refreshed = Entry("first") with { Created = "2026-07-21" };
        bool updated = MemoryStore.Add(_layout, refreshed);

        Assert.True(updated);
        IReadOnlyList<MemoryEntry> loaded = MemoryStore.Load(_layout);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("first", loaded[0].Text);
        Assert.Equal("2026-07-21", loaded[0].Created);
        Assert.Equal("second", loaded[1].Text);
    }

    [Fact]
    public void Remove_Tombstones_AndUnknownIdReturnsFalse()
    {
        var entry = Entry("to be removed");
        MemoryStore.Add(_layout, entry);

        Assert.True(MemoryStore.Remove(_layout, entry.Id));
        Assert.Empty(MemoryStore.Load(_layout));
        Assert.False(MemoryStore.Remove(_layout, entry.Id));
        Assert.False(MemoryStore.Remove(_layout, "00000000"));
    }

    [Fact]
    public async Task ParallelMutations_DuringCompaction_PreserveEveryUpdate()
    {
        const int removeCount = 24;
        const int addCount = 48;
        var removable = Enumerable.Range(0, removeCount)
            .Select(i => Entry($"remove {i:D2}"))
            .ToArray();
        foreach (MemoryEntry entry in removable)
        {
            MemoryStore.Add(_layout, entry);
        }

        // Force the next mutation to compact. Repeating a valid line creates
        // superseded history without changing the live set.
        string path = MemoryStore.PathFor(_layout);
        string duplicate = File.ReadLines(path).First();
        File.AppendAllText(path, string.Concat(
            Enumerable.Repeat(duplicate + "\n", 1_001)));

        var additions = Enumerable.Range(0, addCount)
            .Select(i => Entry($"parallel add {i:D2}"))
            .ToArray();
        using var start = new ManualResetEventSlim(initialState: false);
        Task[] removals = removable.Select(entry => Task.Run(() =>
        {
            start.Wait();
            Assert.True(MemoryStore.Remove(_layout, entry.Id));
        })).ToArray();
        Task[] adds = additions.Select(entry => Task.Run(() =>
        {
            start.Wait();
            Assert.False(MemoryStore.Add(_layout, entry));
        })).ToArray();

        start.Set();
        await Task.WhenAll(removals.Concat(adds));

        IReadOnlyList<MemoryEntry> loaded = MemoryStore.Load(_layout);
        Assert.Equal(addCount, loaded.Count);
        Assert.Equal(
            additions.Select(e => e.Id).Order(StringComparer.Ordinal),
            loaded.Select(e => e.Id).Order(StringComparer.Ordinal));

        // Writers never interleave JSON objects, including at the compaction
        // boundary. Every surviving physical line is independently valid.
        Assert.All(
            File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)),
            line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            });
    }

    [Fact]
    public void Load_SkipsCorruptAndUnknownLines()
    {
        MemoryStore.Add(_layout, Entry("valid"));
        File.AppendAllText(MemoryStore.PathFor(_layout),
            "not json at all\n{\"id\":\"x1\",\"kind\":\"bogus\",\"text\":\"bad kind\"}\n{}\n");

        MemoryEntry survivor = Assert.Single(MemoryStore.Load(_layout));
        Assert.Equal("valid", survivor.Text);
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        Assert.Empty(MemoryStore.Load(_layout));
    }

    [Fact]
    public void Add_BeyondCap_Throws_ButUpdatesStillPass()
    {
        for (int i = 0; i < MemoryStore.MaxEntries; i++)
        {
            MemoryStore.Add(_layout, Entry($"entry {i}"));
        }

        Assert.Throws<InvalidOperationException>(() => MemoryStore.Add(_layout, Entry("one too many")));

        // Refreshing an existing entry is not growth and must still work.
        Assert.True(MemoryStore.Add(_layout, Entry("entry 0") with { Created = "2027-01-01" }));
        Assert.Equal(MemoryStore.MaxEntries, MemoryStore.Load(_layout).Count);
    }
}
