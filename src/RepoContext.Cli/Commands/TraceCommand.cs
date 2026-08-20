using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Graph;
using RepoContext.Core.Indexing;
using RepoContext.Core.Query;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx trace</c> command (ADR 0019): resolve one term - a ticket or
/// requirement key, a link, a symbol name or a path - to every place in the
/// repository that declares or mentions it.
/// </summary>
public static class TraceCommand
{
    public static Command Build()
    {
        var term = new Argument<string>("ref")
        {
            Description = "A work-item key (ABC-123), a link, a symbol name or a repository path.",
        };
        var top = new Option<int>("--top")
        {
            Description = "Maximum number of files to list.",
            DefaultValueFactory = _ => 20,
        };
        var path = CommandSupport.PathScopeOption();
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");

        var command = new Command("trace",
            "Find every file that declares or mentions a key, link, symbol or path.")
        {
            term,
            top,
            path,
            format,
        };

        command.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'text', 'json' or 'md'.");
                return ExitCode.InvalidArguments;
            }

            int topN = parseResult.GetValue(top);
            if (topN <= 0)
            {
                Console.Error.WriteLine("--top must be greater than zero.");
                return ExitCode.InvalidArguments;
            }

            string value = parseResult.GetValue(term) ?? string.Empty;
            if (value.Trim().Length == 0)
            {
                Console.Error.WriteLine("Provide a key, link, symbol or path to trace.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureIndexUsable(store, config))
            {
                return ExitCode.NoIndex;
            }

            PathScope? scope = PathScope.From(parseResult.GetValue(path));
            TraceResult result = Trace.Query(store, value, topN, scope, TokenScale.From(config));
            string rendered = TraceOutput.Render(result, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(
                layout, "trace", UsageSources.Cli, CommandSupport.CliSurfaceText(rendered),
                scale: TokenScale.From(config));
            return ExitCode.Success;
        });

        return command;
    }
}
