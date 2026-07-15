using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>Small helpers shared by the query commands.</summary>
internal static class CommandSupport
{
    /// <summary>Resolves the repository's query-time token calibration (ADR 0012).</summary>
    public static TokenScale ScaleFor(RepoLayout layout) =>
        TokenScale.From(ConfigStore.Load(layout.ConfigPath));

    /// <summary>
    /// Verifies the opened index matches the current on-disk schema version.
    /// An index written by an older tool version cannot be queried; the caller
    /// should return <see cref="ExitCode.NoIndex"/> when this is false.
    /// </summary>
    public static bool EnsureSchemaCurrent(IndexStore store)
    {
        if (store.IsSchemaCurrent)
        {
            return true;
        }

        Console.Error.WriteLine("Index schema is outdated. Run 'repoctx index' to rebuild it.");
        return false;
    }

    /// <summary>Writes rendered output with exactly one trailing newline.</summary>
    public static void WriteRendered(string rendered)
    {
        Console.Out.Write(rendered);
        if (!rendered.EndsWith('\n'))
        {
            Console.Out.Write('\n');
        }
    }
}
