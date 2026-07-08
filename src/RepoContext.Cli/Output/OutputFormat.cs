namespace RepoContext.Cli.Output;

/// <summary>Supported output formats. <c>md</c> is added in M4.</summary>
public enum OutputFormat
{
    Text,
    Json,
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
            default:
                format = OutputFormat.Text;
                return false;
        }
    }
}
