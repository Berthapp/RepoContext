using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Indexing;
using RepoContext.Core.Memory;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx memory</c> command family (ADR 0013): <c>add</c> stores an
/// agent-authored insight, <c>search</c> recalls deterministically, <c>rm</c>
/// curates. RepoContext never writes memories itself — it is the explainable
/// store an agent deposits distilled knowledge into, so the next session pays
/// a recall instead of a re-discovery.
/// </summary>
public static class MemoryCommand
{
    public static Command Build()
    {
        var command = new Command("memory",
            "Store, recall and curate agent-authored project memory (notes, decisions, constraints).");
        command.Subcommands.Add(BuildAdd());
        command.Subcommands.Add(BuildSearch());
        command.Subcommands.Add(BuildRemove());
        return command;
    }

    private static Command BuildAdd()
    {
        var text = new Argument<string>("text")
        {
            Description = "The distilled insight to remember (keep it to 1-2 sentences; "
                + $"max {MemoryStore.MaxTextLength} characters).",
        };
        var kind = new Option<string>("--kind")
        {
            Description = "Memory kind: note (knowledge), decision (a why), constraint (a warning).",
            DefaultValueFactory = _ => MemoryKinds.Note,
        };
        var file = new Option<string[]>("--file")
        {
            Description = "A file this memory is about (repeatable). Its content hash is "
                + "recorded, so the memory is flagged stale when the file changes.",
        };
        var tag = new Option<string[]>("--tag")
        {
            Description = "A lowercase tag for recall (repeatable).",
        };
        var session = new Option<string?>("--session")
        {
            Description = "Scope the memory to a session (short-term); omit for long-term.",
        };
        var format = FormatOption();

        var add = new Command("add", "Store one distilled memory entry.")
        {
            text, kind, file, tag, session, format,
        };

        add.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                return InvalidFormat();
            }

            string kindValue = parseResult.GetValue(kind) ?? MemoryKinds.Note;
            if (!MemoryKinds.IsValid(kindValue))
            {
                Console.Error.WriteLine("Invalid --kind. Use 'note', 'decision' or 'constraint'.");
                return ExitCode.InvalidArguments;
            }

            string textValue = (parseResult.GetValue(text) ?? string.Empty).Trim();
            if (textValue.Length == 0)
            {
                Console.Error.WriteLine("Memory text must not be empty.");
                return ExitCode.InvalidArguments;
            }

            if (textValue.Length > MemoryStore.MaxTextLength)
            {
                Console.Error.WriteLine(
                    $"Memory text exceeds {MemoryStore.MaxTextLength} characters — distill it; "
                    + "a memory must stay cheaper than the reads it replaces.");
                return ExitCode.InvalidArguments;
            }

            string? sessionName = parseResult.GetValue(session);
            if (sessionName is not null && !SessionStore.IsValidName(sessionName))
            {
                return InvalidSession();
            }

            if (!TryNormalizeTags(parseResult.GetValue(tag), out List<string> tags))
            {
                return ExitCode.InvalidArguments;
            }

            string[] files = parseResult.GetValue(file) ?? [];
            if (files.Length > MemoryStore.MaxFiles)
            {
                Console.Error.WriteLine($"At most {MemoryStore.MaxFiles} --file links per memory.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                return NoIndex();
            }

            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureIndexUsable(store, config))
            {
                return ExitCode.NoIndex;
            }

            // Linked files must exist in the index: the recorded hash is what
            // makes staleness detectable later.
            var links = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string input in files)
            {
                string? relative = layout.ToRelativePath(input, Directory.GetCurrentDirectory());
                if (relative is null)
                {
                    Console.Error.WriteLine($"Path is outside the repository: {input}");
                    return ExitCode.InvalidArguments;
                }

                if (store.FindFile(relative) is not { } row)
                {
                    Console.Error.WriteLine($"File not found in index: {relative}");
                    return ExitCode.Error;
                }

                links[relative] = Hashes.Short(row.ContentHash);
            }

            var entry = new MemoryEntry
            {
                Id = MemoryEntry.ComputeId(kindValue, textValue, sessionName, links.Keys),
                Kind = kindValue,
                Text = textValue,
                Files = links,
                Tags = tags,
                Session = sessionName,
                Created = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            };

            bool updated;
            try
            {
                updated = MemoryStore.Add(layout, entry);
            }
            catch (Exception e) when (
                e is InvalidOperationException or IOException
                    or UnauthorizedAccessException or TimeoutException)
            {
                Console.Error.WriteLine(e.Message);
                return ExitCode.Error;
            }

            string rendered = MemoryOutput.RenderAdd(
                entry, updated, MemoryStore.Load(layout).Count, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(
                layout, "memory", UsageSources.Cli, CommandSupport.CliSurfaceText(rendered),
                scale: TokenScale.From(config));
            return ExitCode.Success;
        });

        return add;
    }

    private static Command BuildSearch()
    {
        var query = new Argument<string?>("query")
        {
            Description = "Free-text recall query; omit to list entries.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var top = new Option<int>("--top")
        {
            Description = "Maximum number of entries.",
            DefaultValueFactory = _ => 10,
        };
        var kind = new Option<string?>("--kind")
        {
            Description = "Restrict to one kind: note, decision or constraint.",
        };
        var file = new Option<string?>("--file")
        {
            Description = "Restrict to memories linked to this file.",
        };
        var session = new Option<string?>("--session")
        {
            Description = "Also include this session's short-term memories.",
        };
        var stale = new Option<bool>("--stale")
        {
            Description = "Return only stale entries (linked files changed since they were written).",
        };
        var format = FormatOption();

        var search = new Command("search", "Recall memories deterministically, with reasons and stale flags.")
        {
            query, top, kind, file, session, stale, format,
        };

        search.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                return InvalidFormat();
            }

            int topN = parseResult.GetValue(top);
            if (topN <= 0)
            {
                Console.Error.WriteLine("--top must be greater than zero.");
                return ExitCode.InvalidArguments;
            }

            string? kindValue = parseResult.GetValue(kind);
            if (kindValue is not null && !MemoryKinds.IsValid(kindValue))
            {
                Console.Error.WriteLine("Invalid --kind. Use 'note', 'decision' or 'constraint'.");
                return ExitCode.InvalidArguments;
            }

            string? sessionName = parseResult.GetValue(session);
            if (sessionName is not null && !SessionStore.IsValidName(sessionName))
            {
                return InvalidSession();
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                return NoIndex();
            }

            string? fileFilter = null;
            if (parseResult.GetValue(file) is { Length: > 0 } fileInput)
            {
                fileFilter = layout.ToRelativePath(fileInput, Directory.GetCurrentDirectory());
                if (fileFilter is null)
                {
                    Console.Error.WriteLine($"Path is outside the repository: {fileInput}");
                    return ExitCode.InvalidArguments;
                }
            }

            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureIndexUsable(store, config))
            {
                return ExitCode.NoIndex;
            }

            MemoryQueryResult result = MemoryEngine.Search(MemoryStore.Load(layout), new MemoryQueryOptions
            {
                Query = parseResult.GetValue(query),
                Top = topN,
                Kind = kindValue,
                File = fileFilter,
                Session = sessionName,
                StaleOnly = parseResult.GetValue(stale),
            }, config, store);

            string rendered = MemoryOutput.RenderSearch(result, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(
                layout, "memory", UsageSources.Cli, CommandSupport.CliSurfaceText(rendered),
                scale: TokenScale.From(config));
            return ExitCode.Success;
        });

        return search;
    }

    private static Command BuildRemove()
    {
        var id = new Argument<string>("id")
        {
            Description = "The memory id to remove (as printed by add/search).",
        };
        var format = FormatOption();

        var rm = new Command("rm", "Remove one memory entry by id.") { id, format };

        rm.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                return InvalidFormat();
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null)
            {
                return NoIndex();
            }

            string idValue = parseResult.GetValue(id) ?? string.Empty;
            bool removed;
            try
            {
                removed = MemoryStore.Remove(layout, idValue);
            }
            catch (Exception e) when (
                e is IOException or UnauthorizedAccessException or TimeoutException)
            {
                Console.Error.WriteLine(e.Message);
                return ExitCode.Error;
            }

            if (!removed)
            {
                Console.Error.WriteLine($"No memory with id '{idValue}'.");
                return ExitCode.Error;
            }

            string rendered = MemoryOutput.RenderRemove(idValue, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(
                layout, "memory", UsageSources.Cli, CommandSupport.CliSurfaceText(rendered),
                scale: CommandSupport.ScaleFor(layout));
            return ExitCode.Success;
        });

        return rm;
    }

    /// <summary>Tags are recall keys: lowercase them and keep them boring.</summary>
    private static bool TryNormalizeTags(string[]? raw, out List<string> tags)
    {
        tags = [];
        foreach (string input in raw ?? [])
        {
            string t = input.Trim().ToLowerInvariant();
            if (t.Length is 0 or > 32 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                Console.Error.WriteLine(
                    $"Invalid --tag '{input}'. Use 1-32 characters from a-z, 0-9, '-', '_'.");
                return false;
            }

            if (!tags.Contains(t, StringComparer.Ordinal))
            {
                tags.Add(t);
            }
        }

        if (tags.Count > MemoryStore.MaxTags)
        {
            Console.Error.WriteLine($"At most {MemoryStore.MaxTags} --tag values per memory.");
            return false;
        }

        return true;
    }

    private static Option<string> FormatOption()
    {
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");
        return format;
    }

    private static int InvalidFormat()
    {
        Console.Error.WriteLine("Invalid --format. Use 'text', 'json' or 'md'.");
        return ExitCode.InvalidArguments;
    }

    private static int InvalidSession()
    {
        Console.Error.WriteLine(
            "Invalid --session. Use 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.");
        return ExitCode.InvalidArguments;
    }

    private static int NoIndex()
    {
        Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
        return ExitCode.NoIndex;
    }
}
