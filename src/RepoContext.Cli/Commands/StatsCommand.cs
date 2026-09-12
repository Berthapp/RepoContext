using System.CommandLine;
using System.Diagnostics;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Stats;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx stats</c> command (ADR 0011): the token-savings dashboard.
/// Aggregates the local usage log (<c>.repoctx/stats.jsonl</c>) into calls,
/// response cost, replaced reads and net savings — overall, per command and
/// per recent day. Renders as text/json/md, or as a self-contained HTML page
/// (<c>--format html</c> on stdout; no server involved, in keeping with the
/// no-network constraint).
/// <para>
/// Plain <c>repoctx stats</c> at an interactive terminal prints the summary
/// <em>and</em> opens the visual dashboard, because that is what a human
/// asking for it wants to see. Anything that signals a machine on the other
/// end — a redirected stream, an explicit <c>--format</c>, <c>--no-open</c> —
/// keeps it to stdout, so pipes, scripts and CI are unaffected.
/// </para>
/// Reads only; a stats call is never recorded itself.
/// </summary>
public static class StatsCommand
{
    /// <summary>
    /// Set (non-empty) to suppress every browser launch; the page is still
    /// written and its path still reported (headless runs, CI).
    /// </summary>
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
            Description = "Always write the HTML dashboard to .repoctx/stats.html and open it in "
                        + "the default browser, even when output is redirected.",
        };
        var noOpen = new Option<bool>("--no-open")
        {
            Description = "Never open the browser; print the dashboard to stdout. Wins over "
                        + "--open if both are given.",
        };

        var command = new Command("stats",
            "Show the token-savings dashboard for this repository's repoctx usage.")
        {
            format,
            open,
            noOpen,
        };

        command.SetAction(parseResult =>
        {
            string formatRaw = parseResult.GetValue(format) ?? "text";
            // An explicitly chosen format means the caller wants the output
            // itself, not a browser window.
            bool formatGiven = parseResult.GetResult(format) is not (null or { Implicit: true });
            bool html = string.Equals(formatRaw, "html", StringComparison.OrdinalIgnoreCase);
            OutputFormat outputFormat = default;
            if (!html && !OutputFormatParser.TryParse(formatRaw, out outputFormat))
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
            TokenPricing pricing = TokenPricing.From(ConfigStore.Load(layout.ConfigPath));
            if (ShouldOpenDashboard(
                    openRequested: parseResult.GetValue(open),
                    noOpen: parseResult.GetValue(noOpen),
                    formatGiven: formatGiven,
                    interactive: !Console.IsInputRedirected && !Console.IsOutputRedirected,
                    hasUsage: report.Totals.Calls > 0))
            {
                // The terminal keeps the numbers, the browser gets the picture.
                CommandSupport.WriteRendered(StatsOutput.Render(report, OutputFormat.Text, pricing));
                string path = Path.Combine(layout.IndexDirectory, "stats.html");
                Directory.CreateDirectory(layout.IndexDirectory);
                File.WriteAllText(path, StatsHtmlOutput.Render(report, pricing));
                Console.Out.WriteLine($"Dashboard written to {path}");
                if (!TryOpenInBrowser(path))
                {
                    Console.Out.WriteLine("Could not launch a browser; open the file manually.");
                }

                return ExitCode.Success;
            }

            string rendered = html
                ? StatsHtmlOutput.Render(report, pricing)
                : StatsOutput.Render(report, outputFormat, pricing);
            CommandSupport.WriteRendered(rendered);
            return ExitCode.Success;
        });

        return command;
    }

    /// <summary>
    /// Whether this invocation opens the visual dashboard. The default is the
    /// human case — a plain <c>stats</c> typed at a terminal with something to
    /// show — because the dashboard is the point of the command. Every signal
    /// that a machine is reading the output (a redirected stream, an explicit
    /// format) keeps it to stdout, so no pipeline ever grows a browser window
    /// or a stray path line; <c>--open</c> forces it, <c>--no-open</c> forbids
    /// it. An empty ledger opens nothing on its own: there is no page worth
    /// looking at, and the stdout hint says what to do instead.
    /// </summary>
    public static bool ShouldOpenDashboard(
        bool openRequested, bool noOpen, bool formatGiven, bool interactive, bool hasUsage)
    {
        if (noOpen)
        {
            return false;
        }

        return openRequested || (interactive && !formatGiven && hasUsage);
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
