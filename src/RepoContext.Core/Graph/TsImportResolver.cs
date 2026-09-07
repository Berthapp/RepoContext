using System.Text.Json;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Graph;

/// <summary>Resolves local TS/JS modules against the indexed snapshot only.</summary>
internal sealed class TsImportResolver(IndexStore store, HashSet<string> paths)
{
    private readonly Dictionary<string, Config?> _configs = new(StringComparer.Ordinal);
    private static readonly string[] Extensions = [".ts", ".tsx", ".d.ts", ".js", ".jsx"];

    public string? Resolve(string from, string specifier)
    {
        if (specifier.StartsWith('.')) return ResolveFile(Combine(Directory(from), specifier));
        Config? config = NearestConfig(from);
        if (config is null) return null;
        foreach ((string pattern, string[] targets) in config.Paths
            .OrderBy(p => p.Key.Contains('*') ? 1 : 0)
            .ThenByDescending(p => p.Key.Split('*')[0].Length)
            .ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            int star = pattern.IndexOf('*');
            string capture = "";
            if (star < 0)
            {
                if (pattern != specifier) continue;
            }
            else
            {
                string suffix = pattern[(star + 1)..];
                if (!specifier.StartsWith(pattern[..star], StringComparison.Ordinal)
                    || !specifier.EndsWith(suffix, StringComparison.Ordinal)
                    || specifier.Length < pattern.Length - 1) continue;
                capture = specifier[star..(specifier.Length - suffix.Length)];
            }
            foreach (string target in targets)
            {
                string candidate = Combine(config.BaseUrl ?? config.PathsBase, target.Replace("*", capture, StringComparison.Ordinal));
                if (ResolveFile(candidate) is { } resolved) return resolved;
            }
            return null; // A matched but missing alias never falls through to another mapping.
        }
        return config.BaseUrl is { } baseUrl ? ResolveFile(Combine(baseUrl, specifier)) : null;
    }

    private string? ResolveFile(string path)
    {
        string ext = Path.GetExtension(path);
        string stem = ext.Length == 0 ? path : path[..^ext.Length];
        string[] substitutions = ext switch
        {
            ".js" => Extensions,
            ".jsx" => [".tsx", ".d.ts", ".jsx"],
            ".mjs" => [".mts", ".d.mts", ".mjs"],
            ".cjs" => [".cts", ".d.cts", ".cjs"],
            "" => Extensions,
            _ => [],
        };
        foreach (string extension in substitutions)
            if (paths.Contains(stem + extension)) return stem + extension;
        if (paths.Contains(path)) return path;
        if (ext.Length == 0)
            foreach (string extension in Extensions)
                if (paths.Contains(path + "/index" + extension)) return path + "/index" + extension;
        return null;
    }

    private Config? NearestConfig(string from)
    {
        string dir = Directory(from);
        while (true)
        {
            foreach (string name in new[] { "tsconfig.json", "jsconfig.json" })
            {
                string path = Combine(dir, name);
                if (paths.Contains(path)) return Load(path, []);
            }
            if (dir.Length == 0) return null;
            dir = Directory(dir);
        }
    }

    private Config? Load(string path, HashSet<string> visiting)
    {
        if (_configs.TryGetValue(path, out Config? cached)) return cached;
        if (!visiting.Add(path) || visiting.Count > 32) return null;
        try
        {
            if (store.GetSourceSlice(path, 1, int.MaxValue)?.Text is not { } content) return null;
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            Config config = new(null, Directory(path), new(StringComparer.Ordinal));
            if (root.TryGetProperty("extends", out JsonElement extends))
            {
                IEnumerable<JsonElement> bases = extends.ValueKind == JsonValueKind.Array ? extends.EnumerateArray() : [extends];
                foreach (JsonElement item in bases)
                {
                    // Only indexed local config files; never open excluded packages or leave the repo.
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value || !value.StartsWith('.')) continue;
                    string parent = Combine(Directory(path), value);
                    if (!paths.Contains(parent)) parent += ".json";
                    if (Load(parent, visiting) is { } inherited)
                        config = new(inherited.BaseUrl ?? config.BaseUrl,
                            inherited.HasPaths ? inherited.PathsBase : config.PathsBase,
                            inherited.HasPaths ? inherited.Paths : config.Paths, inherited.HasPaths || config.HasPaths);
                }
            }
            if (root.TryGetProperty("compilerOptions", out JsonElement options) && options.ValueKind == JsonValueKind.Object)
            {
                if (options.TryGetProperty("baseUrl", out JsonElement baseUrl) && baseUrl.ValueKind == JsonValueKind.String)
                    config = config with { BaseUrl = Combine(Directory(path), baseUrl.GetString()!) };
                if (options.TryGetProperty("paths", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Object)
                {
                    var mappings = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    foreach (JsonProperty alias in aliases.EnumerateObject())
                        if (alias.Value.ValueKind == JsonValueKind.Array && alias.Name.Count(c => c == '*') <= 1)
                            mappings[alias.Name] = alias.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!).ToArray();
                    config = config with { PathsBase = Directory(path), Paths = mappings, HasPaths = true };
                }
            }
            _configs[path] = config;
            return config;
        }
        catch (JsonException) { _configs[path] = null; return null; }
        finally { visiting.Remove(path); }
    }

    private static string Directory(string path) => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "";
    private static string Combine(string dir, string value)
    {
        var segments = new List<string>();
        foreach (string part in (dir + "/" + value).Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0) return "../" + value;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(part);
        }
        return string.Join('/', segments);
    }
    private sealed record Config(string? BaseUrl, string PathsBase, Dictionary<string, string[]> Paths, bool HasPaths = false);
}
