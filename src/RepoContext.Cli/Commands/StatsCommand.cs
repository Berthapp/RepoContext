using System.CommandLine;
using System.Diagnostics;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Stats;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx stats</c> command (ADR 0011): the token-savings dashboard.
/// Aggregates the local usage log (<c>.repoctx/stats.jsonl</c>) into calls,
/// response cost, replaced reads and net savings — overall, per command and
/// per recent day. Renders as text/json/md, or as a self-contained HTML page
/// (<c>--format html</c>, or <c>--open</c> to write <c>.repoctx/stats.html</c>
/// and launch the browser — no server involved, in keeping with the
/// no-network constraint). Reads only; a stats call is never recorded itself.
/// </summary>
public static class StatsCommand
{
    /// <summary>Set (non-empty) to suppress the browser launch of <c>--open</c>.</summary>
    public const string NoLaunchVariable = "REPOCTX_NO_LAUNCH";

    public static Command Build()
    {
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json, md or html.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");
        var open = new Option<bool>("--open")
        {
            Description = "Write the HTML dashboard to .repoctx/stats.html and open it in the "
                        + "default browser (overrides --format).",
        };

        var command = new Command("stats",
            "Show the token-savings dashboard for this repository's repoctx usage.")
        {
            format,
            open,
        };

        command.SetAction(parseResult =>
        {
            bool openDashboard = parseResult.GetValue(open);
            string formatRaw = parseResult.GetValue(format) ?? "text";
            bool html = string.Equals(formatRaw, "html", StringComparison.OrdinalIgnoreCase);
            OutputFormat outputFormat = default;
            if (!openDashboard && !html && !OutputFormatParser.TryParse(formatRaw, out outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'text', 'json', 'md' or 'html'.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null)
            {
                Console.Error.WriteLine("Not a RepoContext repository. Run 'repoctx init' first.");
                return ExitCode.NoIndex;
            }

            UsageReport report = UsageReport.Build(UsageLog.Read(UsageLog.PathFor(layout)));
            if (openDashboard)
            {
                string path = Path.Combine(layout.IndexDirectory, "stats.html");
                Directory.CreateDirectory(layout.IndexDirectory);
                File.WriteAllText(path, StatsHtmlOutput.Render(report));
                Console.Out.WriteLine($"Dashboard written to {path}");
                if (!TryOpenInBrowser(path))
                {
                    Console.Out.WriteLine("Could not launch a browser; open the file manually.");
                }

                return ExitCode.Success;
            }

            string rendered = html
                ? StatsHtmlOutput.Render(report)
                : StatsOutput.Render(report, outputFormat);
            CommandSupport.WriteRendered(rendered);
            return ExitCode.Success;
        });

        return command;
    }

    /// <summary>Opens the file with the OS default handler; failure is non-fatal.</summary>
    private static bool TryOpenInBrowser(string path)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NoLaunchVariable)))
        {
            return true;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
