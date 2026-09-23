using System.Security.Cryptography;
using System.Text;

namespace RepoContext.Core.Identity;

/// <summary>
/// The per-user secret that tells what RepoContext produced on this machine
/// apart from what arrived with a checkout (ADR 0025).
/// </summary>
/// <remarks>
/// <para>
/// <c>.repoctx/</c> is normally git-ignored, but a hostile repository can commit
/// one - or add files to it with a later pull. An index database built
/// elsewhere can hold "source" that exists in no file, and it survives an
/// incremental <c>index</c> because the content hashes it records match the
/// real files. A planted memory is folded into every matching
/// <c>context</c> answer as agent-authored knowledge. Neither is visible in a
/// code review of the repository. Everything RepoContext writes that it later
/// trusts therefore carries an HMAC under this key, which never leaves the
/// user's profile and is never written into a repository.
/// </para>
/// <para>
/// The key file lives at <c>REPOCTX_KEY_FILE</c> when set, else at
/// <c>RepoContext/machine.key</c> in the per-user local data directory. The
/// environment is trusted: whoever controls it controls the process already.
/// When no key can be read or created (no writable profile, or a file of the
/// wrong shape at that path, which is never overwritten), RepoContext degrades
/// to the pre-0.15.1 behaviour and trusts what it finds; <c>index</c> warns.
/// </para>
/// </remarks>
public static class MachineKey
{
    /// <summary>Overrides the key file location (read-only profiles, tests).</summary>
    public const string FileVariable = "REPOCTX_KEY_FILE";

    private const int KeyLength = 32;

    /// <summary>
    /// Resolved once per process: every memory line and session read verifies a
    /// signature, and re-resolving the profile path each time would dominate the
    /// cost of a read. The environment does not change under a running command.
    /// </summary>
    private static readonly Lazy<byte[]?> CachedKey =
        new(() => FilePath is { } path ? LoadOrCreate(path) : null, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Where the key is (or would be) stored, or null when this environment
    /// offers no per-user location at all.
    /// </summary>
    public static string? FilePath
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(FileVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            string data = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            return string.IsNullOrEmpty(data) ? null : Path.Combine(data, "RepoContext", "machine.key");
        }
    }

    /// <summary>Whether a key is available, creating one on first use.</summary>
    public static bool IsAvailable => Key() is not null;

    /// <summary>
    /// The HMAC-SHA256 of <paramref name="fields"/> under the machine key, as
    /// lowercase hex; null when no key is available. <paramref name="purpose"/>
    /// separates the stores, so a signature from one can never validate another.
    /// </summary>
    public static string? Sign(string purpose, params ReadOnlySpan<string> fields)
    {
        if (Key() is not { } key)
        {
            return null;
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Message(purpose, fields)));
    }

    /// <summary>
    /// Whether <paramref name="mac"/> is this machine's signature over
    /// <paramref name="fields"/>. Always true when no key is available (the
    /// documented degraded mode), never true for a missing or malformed mac
    /// otherwise. Compared in constant time.
    /// </summary>
    public static bool Verify(string? mac, string purpose, params ReadOnlySpan<string> fields)
    {
        if (Key() is not { } key)
        {
            return true;
        }

        if (mac is not { Length: 64 })
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(mac);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            presented, HMACSHA256.HashData(key, Message(purpose, fields)));
    }

    private static byte[] Message(string purpose, ReadOnlySpan<string> fields)
    {
        var records = new List<string>(fields.Length + 1) { purpose };
        foreach (string field in fields)
        {
            records.Add(field);
        }

        return Encoding.UTF8.GetBytes(Canonical.JoinRecords(records));
    }

    private static byte[]? Key() => CachedKey.Value;

    private static byte[]? LoadOrCreate(string path)
    {
        try
        {
            if (TryRead(path, out byte[]? existing))
            {
                return existing;
            }

            // Something is there but is not a key: never overwrite a file this
            // process did not create. The caller degrades and `index` warns.
            if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            {
                return null;
            }

            string directory = Path.GetDirectoryName(path)!;
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(
                    directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            string temporary = path + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp";
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(RandomNumberGenerator.GetBytes(KeyLength));
            }

            try
            {
                // Without overwrite, so two processes creating a key at the same
                // moment agree on whichever landed first.
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException)
            {
                File.Delete(temporary);
            }

            return TryRead(path, out byte[]? created) ? created : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static bool TryRead(string path, out byte[]? key)
    {
        key = null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != KeyLength)
        {
            return false;
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length != KeyLength)
        {
            return false;
        }

        key = bytes;
        return true;
    }
}
