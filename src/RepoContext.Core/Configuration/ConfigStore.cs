using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoContext.Core.Configuration;

/// <summary>Reads and writes <c>repoctx.config.json</c> and hashes it.</summary>
public static class ConfigStore
{
    /// <summary>Serializes the config to its canonical JSON text (LF line endings).</summary>
    public static string Serialize(RepoctxConfig config) =>
        JsonSerializer.Serialize(config, RepoctxConfig.SerializerOptions);

    /// <summary>Deserializes config text, falling back to defaults for missing members.</summary>
    public static RepoctxConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<RepoctxConfig>(json, RepoctxConfig.SerializerOptions)
        ?? RepoctxConfig.CreateDefault();

    /// <summary>Loads the config from <paramref name="path"/>.</summary>
    public static RepoctxConfig Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Writes the config to <paramref name="path"/> with a trailing newline.</summary>
    public static void Save(string path, RepoctxConfig config) =>
        File.WriteAllText(path, Serialize(config) + "\n");

    /// <summary>
    /// A stable content hash of the effective config, stored in the index meta
    /// table so a changed config can be detected (and trigger a full rebuild).
    /// </summary>
    public static string ComputeHash(RepoctxConfig config)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Serialize(config));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
