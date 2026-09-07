using RepoContext.Core.Scanning;
using TreeSitter;

namespace RepoContext.Core.Parsing;

public sealed partial class TreeSitterParser
{
    public IReadOnlyList<(string Kind, string Name, int Line)> ExtractCSharpReferences(string content)
    {
        LanguageContext ctx = GetContext(SourceLanguage.CSharp);
        using Tree? tree = ctx.Parser.Parse(content);
        if (tree is null) return [];
        var facts = new List<(string Kind, string Name, int Line)>();
        string fileNamespace = tree.RootNode.NamedChildren
            .FirstOrDefault(n => n.Type == "file_scoped_namespace_declaration")?
            .GetChildForField("name")?.Text ?? string.Empty;
        Walk(tree.RootNode, fileNamespace, 0);
        return facts;

        void Walk(Node node, string currentNamespace, int depth)
        {
            if (depth > 256 || node.Type is "comment" or "string_literal" or "verbatim_string_literal"
                or "raw_string_literal" or "character_literal") return;
            if (node.Type == "namespace_declaration")
                currentNamespace = Join(currentNamespace, node.GetChildForField("name")?.Text ?? "");
            if (node.Type is "namespace_declaration" or "file_scoped_namespace_declaration")
                facts.Add(("namespace", currentNamespace, node.StartPosition.Row + 1));
            if (node.Type == "using_directive")
            {
                // Alias/static imports require semantic resolution, not name guessing.
                string text = node.Text.Trim();
                if (!text.Contains('=') && !text.Contains("static ", StringComparison.Ordinal))
                {
                    string name = text.Replace("global using ", "", StringComparison.Ordinal)
                        .Replace("using ", "", StringComparison.Ordinal).TrimEnd(';').Trim();
                    facts.Add(("namespace", name, node.StartPosition.Row + 1));
                }
                return;
            }
            if (node.Type is "class_declaration" or "interface_declaration" or "struct_declaration"
                or "record_declaration" or "enum_declaration")
                facts.Add(("type_definition", Join(currentNamespace, node.GetChildForField("name")?.Text ?? ""), node.StartPosition.Row + 1));
            string identifier = node.Type == "identifier" ? node.Text.TrimStart('@') : "";
            if (identifier.Length > 0 && char.IsUpper(identifier[0]))
            {
                Node? parent = node.Parent;
                bool declarationName = parent is not null
                    && (parent.Type.EndsWith("_declaration", StringComparison.Ordinal)
                        || parent.Type is "variable_declarator" or "parameter" or "type_parameter")
                    && parent.GetChildForField("name")?.Equals(node) == true;
                if (!declarationName && parent?.Type is not ("namespace_declaration" or "file_scoped_namespace_declaration" or "qualified_name"))
                    facts.Add(("type", identifier, node.StartPosition.Row + 1));
            }
            if (node.Type == "qualified_name" && node.Parent?.Type is not
                ("qualified_name" or "namespace_declaration" or "file_scoped_namespace_declaration"))
                facts.Add(("type", node.Text.Replace("global::", "", StringComparison.Ordinal), node.StartPosition.Row + 1));
            foreach (Node child in node.NamedChildren) Walk(child, currentNamespace, depth + 1);
        }
        static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
    }
}
