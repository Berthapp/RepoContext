using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx context</c> command (spec F5, pipeline chapter 6).</summary>
public static class ContextCommand
{
    public static Command Build()
    {
        var task = new Argument<string>("task")
        {
            Description = "A natural-language description of what you want to do.",
        };
        var top = new Option<int>("--top")
        {
            Description = "Maximum number of files.",
            DefaultValueFactory = _ => 8,
        };
        var budget = new Option<int?>("--budget-tokens")
        {
            Description = "Approximate token budget for the returned files.",
        };
        var snippets = new Option<bool>("--snippets")
        {
            Description = "Include a code snippet for each file.",
        };
        var format = new Option<string>("--format")
        {
            Description = "Output format: text or json.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");

        var command = new Command("context",
            "Return a compact, explained context bundle for a natural-language task.")
        {
            task,
            top,
            budget,
            snippets,
            format,
        };

        command.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'text' or 'json'.");
                return ExitCode.InvalidArguments;
            }

            int topN = parseResult.GetValue(top);
            if (topN <= 0)
            {
                Console.Error.WriteLine("--top must be greater than zero.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            string query = parseResult.GetValue(task) ?? string.Empty;
            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            var engine = new ContextEngine(store, config);
            ContextResult result = engine.Run(query, new ContextOptions
            {
                Top = topN,
                BudgetTokens = parseResult.GetValue(budget),
                Snippets = parseResult.GetValue(snippets),
            });

            string rendered = ContextOutput.Render(result, outputFormat);
            Console.Out.Write(rendered);
            if (!rendered.EndsWith('\n'))
            {
                Console.Out.Write('\n');
            }

            return ExitCode.Success;
        });

        return command;
    }
}
