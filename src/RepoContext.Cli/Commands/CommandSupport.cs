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

    /// <summary>
    /// Verifies both the on-disk schema and the stored analysis producers (Q4).
    /// A parser, chunker, tokenizer or graph change alters stored analysis
    /// without changing any source byte, so querying such an index — or honouring
    /// a receipt derived from it — would silently serve stale evidence. This
    /// fails closed and asks for a rebuild instead.
    /// </summary>
    public static bool EnsureIndexUsable(IndexStore store, RepoctxConfig config)
    {
        if (!EnsureSchemaCurrent(store))
        {
            return false;
        }

        if (store.IsProducerCurrent)
        {
            if (!store.HasValidStateHash)
            {
                Console.Error.WriteLine(
                    "Index state metadata is missing or invalid. Run 'repoctx index' to rebuild it.");
                return false;
            }

            if (!store.IsIndexConfigCurrent(config))
            {
                Console.Error.WriteLine(
                    "Index was built with different indexing settings. Run 'repoctx index' to refresh it.");
                return false;
            }

            return true;
        }

        Console.Error.WriteLine(
            "Index was produced by a different analysis version. Run 'repoctx index' to rebuild it.");
        return false;
    }

    /// <summary>Writes rendered output with exactly one trailing newline.</summary>
    public static void WriteRendered(string rendered) =>
        Console.Out.Write(CliSurfaceText(rendered));

    /// <summary>The exact stdout surface recorded and tokenized for CLI queries.</summary>
    public static string CliSurfaceText(string rendered)
    {
        return rendered.EndsWith('\n') ? rendered : rendered + "\n";
    }
}
