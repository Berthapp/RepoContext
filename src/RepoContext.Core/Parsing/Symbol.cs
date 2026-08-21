namespace RepoContext.Core.Parsing;

/// <summary>The kind of an extracted symbol.</summary>
public enum SymbolKind
{
    Class,
    Interface,
    Struct,
    Record,
    Enum,
    Function,
    Method,
    Property,
    TypeAlias,
    Route,

    /// <summary>A document section: a Markdown/AsciiDoc/HTML heading, or a Gherkin feature.</summary>
    Section,

    /// <summary>A structural key of a data artifact: YAML/JSON path, TOML/INI section.</summary>
    Key,

    /// <summary>A Gherkin scenario, background or example block.</summary>
    Scenario,

    /// <summary>A declared database object (table, view, procedure, ...).</summary>
    Table,
}

/// <summary>A symbol extracted from a source file (M2).</summary>
public sealed record Symbol
{
    public required string Name { get; init; }

    public required SymbolKind Kind { get; init; }

    /// <summary>1-based inclusive start line of the declaration.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based inclusive end line of the declaration.</summary>
    public required int EndLine { get; init; }

    /// <summary>The raw declaration signature (first line, whitespace-collapsed).</summary>
    public required string Signature { get; init; }

    /// <summary>Documentation from JSDoc / XML doc comments, if any.</summary>
    public string? Doc { get; init; }
}
