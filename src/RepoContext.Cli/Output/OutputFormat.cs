namespace RepoContext.Cli.Output;

/// <summary>Supported output formats.</summary>
public enum OutputFormat
{
    Text,
    Json,
    Md,
}

/// <summary>Parses the <c>--format</c> option value.</summary>
public static class OutputFormatParser
{
    public static bool TryParse(string? value, out OutputFormat format)
    {
        switch (value?.ToLowerInvariant())
        {
            case null or "" or "text":
                format = OutputFormat.Text;
                return true;
            case "json":
                format = OutputFormat.Json;
                return true;
            case "md" or "markdown":
                format = OutputFormat.Md;
                return true;
            default:
                format = OutputFormat.Text;
                return false;
        }
    }
}
