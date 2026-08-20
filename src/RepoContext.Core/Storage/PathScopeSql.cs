using Microsoft.Data.Sqlite;
using RepoContext.Core.Query;

namespace RepoContext.Core.Storage;

/// <summary>
/// Renders a <see cref="PathScope"/> into the SQL that evaluates it. Keeping
/// the translation in one place is what guarantees that every scoped command -
/// search, context, trace - narrows in exactly the same way.
/// </summary>
internal static class PathScopeSql
{
    private const string ParameterPrefix = "$scope";

    /// <summary>
    /// The <c>AND (...)</c> fragment restricting <paramref name="alias"/> to the
    /// scope, or an empty string when there is no scope.
    /// </summary>
    public static string Filter(PathScope? scope, string alias)
    {
        if (scope is null)
        {
            return string.Empty;
        }

        IEnumerable<string> terms = Enumerable
            .Range(0, scope.Patterns.Count)
            .Select(i => $"{alias}.path GLOB {ParameterPrefix}{i}");
        return " AND (" + string.Join(" OR ", terms) + ")";
    }

    /// <summary>Binds the parameters referenced by <see cref="Filter"/>.</summary>
    public static void Bind(SqliteCommand command, PathScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        for (int i = 0; i < scope.Patterns.Count; i++)
        {
            command.Parameters.AddWithValue(ParameterPrefix + i.ToString(
                System.Globalization.CultureInfo.InvariantCulture), scope.Patterns[i]);
        }
    }
}
