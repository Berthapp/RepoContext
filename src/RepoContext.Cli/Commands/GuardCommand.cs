using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Guard;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx guard</c> command: the client-side adapter of the opt-in
/// read-cost guard (milestones 2-4 of the same-quality, lower-cost plan).
/// </summary>
/// <remarks>
/// <para>
/// <c>guard hook</c> is what a client's pre-tool hook invokes. It reads one
/// event payload on standard input, answers on standard output and
/// <b>always exits 0</b>: a non-zero exit from a cost policy would turn an
/// optimization into an outage. Every failure path — malformed payload, missing
/// index, unreadable state, an unrecognized event — allows the client's normal
/// behaviour and is merely counted.
/// </para>
/// <para>
/// <c>guard check</c> is the same policy without a client, so the decision for
/// any path can be inspected, and <c>guard status</c> prints the local counters.
/// </para>
/// </remarks>
public static class GuardCommand
{
    /// <summary>
    /// Wall-clock budget for one hook evaluation. The client is waiting on a
    /// tool call, so the guard stops working and allows the read rather than
    /// making the user wait for a cheaper suggestion.
    /// </summary>
    private const int DefaultTimeoutMilliseconds = 400;

    public static Command Build()
    {
        var command = new Command("guard",
            "The opt-in read-cost guard: judge an expensive file read and point at cheaper "
            + "exact evidence. Never grants a read, never bypasses client permissions.");

        command.Subcommands.Add(BuildHook());
        command.Subcommands.Add(BuildCheck());
        command.Subcommands.Add(BuildStatus());
        return command;
    }

    // ---- repoctx guard hook --------------------------------------------------

    private static Command BuildHook()
    {
        var mode = ModeOption();
        var maxTokens = MaxTokensOption();
        var client = new Option<string>("--client")
        {
            Description = "The client whose hook payload arrives on stdin. Supported: claude-code.",
            DefaultValueFactory = _ => ClaudeCodeHook.ClientId,
        };
        var timeout = new Option<int>("--timeout-ms")
        {
            Description = "Give up and allow the read after this many milliseconds.",
            DefaultValueFactory = _ => DefaultTimeoutMilliseconds,
        };

        var hook = new Command("hook",
            "Evaluate one client hook event read from stdin. Always exits 0.")
        {
            mode,
            maxTokens,
            client,
            timeout,
        };

        hook.SetAction(parseResult =>
        {
            string clientId = parseResult.GetValue(client) ?? ClaudeCodeHook.ClientId;
            if (!string.Equals(clientId, ClaudeCodeHook.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                // An unsupported client keeps its existing behaviour, as the
                // capability matrix in the guide promises.
                Console.Error.WriteLine(
                    $"No hook contract is supported for '{clientId}'. Supported: "
                    + ClaudeCodeHook.ClientId + ".");
                return ExitCode.Success;
            }

            var stopwatch = Stopwatch.StartNew();
            string payload = ReadStandardInput();
            try
            {
                Handle(
                    payload,
                    ResolveMode(parseResult, mode),
                    ResolveMaxTokens(parseResult, maxTokens),
                    parseResult.GetValue(timeout),
                    stopwatch);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or JsonException or SqliteException)
            {
                // The guard failed; the client proceeds as if it were absent.
                TryRecordError(payload, stopwatch);
            }

            return ExitCode.Success;
        });

        return hook;
    }

    private static void Handle(
        string payload, GuardMode mode, int maxTokens, int timeoutMilliseconds, Stopwatch stopwatch)
    {
        if (!ClaudeCodeHook.TryParse(payload, out HookRequest request, out _))
        {
            TryRecordError(payload, stopwatch);
            return;
        }

        RepoLayout? layout = RepoLayout.Discover(
            request.Cwd is { Length: > 0 } cwd && Directory.Exists(cwd)
                ? cwd
                : Directory.GetCurrentDirectory());
        if (layout is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        string agentKey = ContextEpochs.AgentKey(
            ClaudeCodeHook.ClientId, request.SessionId ?? "unknown");

        switch (request.Kind)
        {
            case HookEventKind.SessionStart:
            case HookEventKind.PreCompact:
            {
                // Both events mean the same thing for evidence: what the model
                // still holds is no longer established. A new epoch is the only
                // safe answer, and it costs at most a repeated read.
                ContextEpoch epoch = ContextEpochs.Advance(
                    layout, agentKey, ClaudeCodeHook.EpochReason(request));
                // The new generation has no redirects. Old entries are bounded
                // separately and must never be reused as possession claims.
                if (request.Kind == HookEventKind.SessionStart)
                {
                    Console.Out.Write(ClaudeCodeHook.RenderSessionStart(epoch.SessionName, mode));
                }

                return;
            }

            case HookEventKind.SubagentStart:
            {
                // A child can inherit the parent's announced --session argument.
                // Retire it before that child can present the same receipt. The
                // parent also re-reads until its next lifecycle announcement;
                // this conservative fallback requires no shared MCP environment.
                ContextEpochs.Retire(layout, agentKey);
                return;
            }

            case HookEventKind.SessionEnd:
            {
                if (ContextEpochs.Current(layout, agentKey) is { } ending)
                {
                    GuardState.ForgetEpoch(layout, ending.SessionName);
                }

                ContextEpochs.Retire(layout, agentKey);
                return;
            }

            case HookEventKind.PreToolUse:
                break;

            default:
                return;
        }

        IReadOnlyList<GuardReadRequest> reads = ClaudeCodeHook.ReadsFrom(request, out _);
        if (reads.Count == 0)
        {
            GuardState.Record(
                layout,
                EpochKey(layout, agentKey),
                GuardDecision.Permit(
                    ClaudeCodeHook.IsUnsupportedShell(request)
                        ? GuardOutcome.Unsupported
                        : GuardOutcome.Allowed,
                    "nothing to judge"),
                stopwatch.ElapsedMilliseconds);
            return;
        }

        string epochKey = EpochKey(layout, agentKey);
        GuardDecision decision = Evaluate(
            layout, reads, mode, maxTokens, epochKey, timeoutMilliseconds, stopwatch,
            request.Cwd ?? layout.Root);

        if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
        {
            decision = GuardDecision.Permit(GuardOutcome.Error, "guard budget exhausted");
        }

        bool recorded = GuardState.Record(layout, epochKey, decision, stopwatch.ElapsedMilliseconds);
        if (recorded && stopwatch.ElapsedMilliseconds <= timeoutMilliseconds
            && ClaudeCodeHook.RenderPreToolUse(decision) is { } response)
        {
            Console.Out.Write(response);
        }
    }

    /// <summary>Evaluates reads against the index, allowing everything it cannot judge.</summary>
    private static GuardDecision Evaluate(
        RepoLayout layout,
        IReadOnlyList<GuardReadRequest> reads,
        GuardMode mode,
        int maxTokens,
        string epochKey,
        int timeoutMilliseconds,
        Stopwatch stopwatch,
        string workingDirectory)
    {
        if (mode == GuardMode.Off || !layout.HasIndex)
        {
            return GuardDecision.Permit(
                GuardOutcome.Allowed, layout.HasIndex ? "guard is off" : "no index");
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        // Read-only, and without the schema pass Open() performs: the guard sits
        // in front of a tool call somebody is waiting on.
        using IndexStore store = IndexStore.OpenReadOnly(layout.DatabasePath);
        if (!store.IsSchemaCurrent || !store.IsProducerCurrent
            || store.GetMeta(MetaKeys.ConfigHash) != ConfigStore.ComputeIndexHash(config))
        {
            // A stale index has stale sizes. Judging a read on them would be
            // guessing, and a wrong guess here blocks real work.
            return GuardDecision.Permit(GuardOutcome.Unsupported, "index needs rebuilding");
        }

        if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
        {
            return GuardDecision.Permit(GuardOutcome.Error, "guard budget exhausted");
        }

        if (!DateTimeOffset.TryParse(store.GetMeta(MetaKeys.IndexedAtUtc),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset indexedAt))
        {
            return GuardDecision.Permit(GuardOutcome.Unsupported, "index has no freshness metadata");
        }

        var policy = new ReadCostPolicy(
            new IndexedGuardMetrics(layout, store, indexedAt),
            new ReadCostPolicyOptions { MaxReadTokens = maxTokens },
            TokenScale.From(config),
            path => layout.ToRelativePath(path, workingDirectory));

        return policy.Evaluate(reads, mode, path => GuardState.Redirects(layout, epochKey, path));
    }

    /// <summary>
    /// The repository-relative metrics lookup, with a cheap staleness check.
    /// </summary>
    /// <remarks>
    /// Index rows describe the file as it was indexed. If the file on disk has a
    /// different size, the stored token count no longer describes it, and a
    /// decision built on it would be false evidence about cost. One
    /// <see cref="FileInfo"/> lookup is affordable inside a pre-tool hook; a
    /// re-index is not.
    /// </remarks>
    private sealed class IndexedGuardMetrics(
        RepoLayout layout, IndexStore store, DateTimeOffset indexedAt) : IGuardIndex
    {
        public bool TryGetMetrics(string relativePath, out GuardFileMetrics metrics)
        {
            metrics = default;
            FileRow? row = store.FindFile(relativePath);
            if (row is not { } file)
            {
                return false;
            }

            try
            {
                var info = new FileInfo(Path.Combine(
                    layout.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!info.Exists || info.Length != file.SizeBytes
                    || info.LastWriteTimeUtc > indexedAt.UtcDateTime
                    || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                for (DirectoryInfo? dir = info.Directory; dir is not null; dir = dir.Parent)
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }

                    foreach (string name in new[] { ".gitignore", ".repoctxignore" })
                    {
                        var ignore = new FileInfo(Path.Combine(dir.FullName, name));
                        if (ignore.Exists && ignore.LastWriteTimeUtc > indexedAt.UtcDateTime)
                        {
                            return false;
                        }
                    }

                    if (dir.FullName == layout.Root)
                    {
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                return false;
            }

            metrics = new GuardFileMetrics(file.TokenCount, file.LineCount);
            return true;
        }
    }

    // ---- repoctx guard check -------------------------------------------------

    private static Command BuildCheck()
    {
        var file = new Argument<string>("file") { Description = "The path a client would read." };
        var mode = ModeOption();
        var maxTokens = MaxTokensOption();
        var offset = new Option<int?>("--offset") { Description = "First line the client would read." };
        var limit = new Option<int?>("--limit") { Description = "How many lines the client would read." };

        var check = new Command("check",
            "Show the decision for one read without a client. Writes no state.")
        {
            file,
            mode,
            maxTokens,
            offset,
            limit,
        };

        check.SetAction(parseResult =>
        {
            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            var request = new GuardReadRequest(
                parseResult.GetValue(file) ?? string.Empty,
                parseResult.GetValue(offset),
                parseResult.GetValue(limit),
                "check");

            GuardDecision decision = Evaluate(
                layout, [request], ResolveMode(parseResult, mode),
                ResolveMaxTokens(parseResult, maxTokens), epochKey: "check",
                timeoutMilliseconds: int.MaxValue, Stopwatch.StartNew(),
                Directory.GetCurrentDirectory());

            Console.WriteLine($"outcome: {decision.Outcome.ToString().ToLowerInvariant()}");
            Console.WriteLine($"deny: {(decision.Deny ? "yes" : "no")}");
            Console.WriteLine($"reason: {decision.Reason}");
            foreach (GuardTarget target in decision.Targets)
            {
                string cost = target.Indexed
                    ? target.EstimatedTokens.ToString(CultureInfo.InvariantCulture) + " estimated tokens"
                    : "not indexed";
                Console.WriteLine($"  {target.Path}: {cost}");
            }

            foreach (string suggestion in decision.Suggestions)
            {
                Console.WriteLine($"  -> {suggestion}");
            }

            return ExitCode.Success;
        });

        return check;
    }

    // ---- repoctx guard status ------------------------------------------------

    private static Command BuildStatus()
    {
        var status = new Command("status",
            "Show the local guard counters and whether a client hook is installed.");

        status.SetAction(_ =>
        {
            string current = Directory.GetCurrentDirectory();
            RepoLayout layout = RepoLayout.Discover(current) ?? RepoLayout.For(current);
            GuardCounters counters = GuardState.ReadCounters(layout);

            Console.WriteLine($"repository: {layout.Root}");
            Console.WriteLine(
                "claude-code hook: "
                + (GuardInstallation.IsInstalled(layout.Root, out string? installedMode)
                    ? $"installed (mode {installedMode})"
                    : "not installed"));
            Console.WriteLine("counters: " + GuardState.Describe(counters));
            if (counters.Latency.Count > 0)
            {
                Console.WriteLine(
                    "latency: "
                    + $"n={counters.Latency.Count.ToString(CultureInfo.InvariantCulture)} "
                    + $"mean={(counters.Latency.TotalMs / (double)counters.Latency.Count).ToString("0.#", CultureInfo.InvariantCulture)}ms "
                    + $"max={counters.Latency.MaxMs.ToString(CultureInfo.InvariantCulture)}ms");
            }

            if (!UsageRecorderEnabled)
            {
                Console.WriteLine(
                    "note: REPOCTX_NO_STATS is set, so counters are not being updated.");
            }

            return ExitCode.Success;
        });

        return status;
    }

    private static bool UsageRecorderEnabled => Core.Stats.UsageRecorder.Enabled;

    // ---- shared options ------------------------------------------------------

    private static Option<string> ModeOption() => new("--mode")
    {
        Description = "off, observe (count only) or enforce (deny an expensive read once).",
        DefaultValueFactory = _ => GuardModes.DefaultName,
    };

    private static Option<int> MaxTokensOption() => new("--max-read-tokens")
    {
        Description = "Estimated tokens above which a read counts as expensive.",
        DefaultValueFactory = _ => new ReadCostPolicyOptions().MaxReadTokens,
    };

    private static GuardMode ResolveMode(ParseResult parseResult, Option<string> option) =>
        GuardModes.TryParse(parseResult.GetValue(option), out GuardMode mode)
            ? mode
            // An unreadable mode must not silently enforce.
            : GuardMode.Observe;

    private static int ResolveMaxTokens(ParseResult parseResult, Option<int> option)
    {
        int value = parseResult.GetValue(option);
        return value > 0 ? value : new ReadCostPolicyOptions().MaxReadTokens;
    }

    private static string EpochKey(RepoLayout layout, string agentKey) =>
        ContextEpochs.Current(layout, agentKey)?.SessionName
        ?? ContextEpochs.SessionName(agentKey, 1);

    private static string ReadStandardInput()
    {
        try
        {
            return Console.In.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void TryRecordError(string payload, Stopwatch stopwatch)
    {
        try
        {
            string? cwd = null;
            if (ClaudeCodeHook.TryParse(payload, out HookRequest request, out _))
            {
                cwd = request.Cwd;
            }

            RepoLayout? layout = RepoLayout.Discover(
                cwd is { Length: > 0 } directory && Directory.Exists(directory)
                    ? directory
                    : Directory.GetCurrentDirectory());
            if (layout is not null)
            {
                GuardState.Record(
                    layout,
                    "errors",
                    GuardDecision.Permit(GuardOutcome.Error, "guard evaluation failed"),
                    stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
