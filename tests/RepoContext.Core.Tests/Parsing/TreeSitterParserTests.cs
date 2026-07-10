using RepoContext.Core.Parsing;
using RepoContext.Core.Scanning;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Parsing;

public class TreeSitterParserTests
{
    private static IReadOnlyList<Symbol> Parse(SourceLanguage lang, string fixtureRelative)
    {
        using var parser = new TreeSitterParser();
        return parser.Parse(lang, fixtureRelative, Fixtures.Read(fixtureRelative));
    }

    private static (SymbolKind, string)[] Set(IReadOnlyList<Symbol> symbols) =>
        symbols.Select(s => (s.Kind, s.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public void TypeScript_Login_ExtractsExpectedSymbols()
    {
        var symbols = Parse(SourceLanguage.TypeScript, "sample-ts/src/auth/login.ts");
        Assert.Equal(
            [
                (SymbolKind.Interface, "Credentials"),
                (SymbolKind.Function, "loginUser"),
                (SymbolKind.Function, "logoutUser"),
            ],
            Set(symbols));
    }

    [Fact]
    public void TypeScript_Session_ExtractsInterfaceTypeAndFunctions()
    {
        var symbols = Parse(SourceLanguage.TypeScript, "sample-ts/src/auth/session.ts");
        Assert.Equal(
            [
                (SymbolKind.Function, "createSession"),
                (SymbolKind.Function, "getSession"),
                (SymbolKind.Interface, "Session"),
                (SymbolKind.TypeAlias, "SessionStore"),
            ],
            Set(symbols));
    }

    [Fact]
    public void TypeScript_Permissions_IncludesExportedArrowFunction()
    {
        var symbols = Parse(SourceLanguage.TypeScript, "sample-ts/src/auth/permissions.ts");
        Assert.Contains((SymbolKind.Function, "isAdmin"), Set(symbols));
        Assert.Contains((SymbolKind.Function, "checkPermission"), Set(symbols));
        Assert.Contains((SymbolKind.TypeAlias, "Action"), Set(symbols));
    }

    [Fact]
    public void TypeScript_RouteFile_ClassifiesHttpMethodsAsRoutes()
    {
        var symbols = Parse(SourceLanguage.Tsx, "sample-ts/app/api/users/route.ts");
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Route, Name: "GET" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Route, Name: "POST" });
    }

    [Fact]
    public void TypeScript_ExtractsJsDoc()
    {
        var symbols = Parse(SourceLanguage.TypeScript, "sample-ts/src/auth/login.ts");
        Symbol login = symbols.Single(s => s.Name == "loginUser");
        Assert.Contains("Authenticate", login.Doc ?? string.Empty);
    }

    [Fact]
    public void CSharp_Service_ExtractsClassAndMethods()
    {
        var symbols = Parse(SourceLanguage.CSharp, "sample-cs/Services/UserService.cs");
        Assert.Equal(
            [
                (SymbolKind.Method, "CreateUser"),
                (SymbolKind.Method, "GetUser"),
                (SymbolKind.Class, "UserService"),
            ],
            Set(symbols));
    }

    [Fact]
    public void CSharp_Controller_ClassifiesMethodsAsRoutes()
    {
        var symbols = Parse(SourceLanguage.CSharp, "sample-cs/Controllers/UsersController.cs");
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Route, Name: "GetById" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Route, Name: "Create" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Class, Name: "UsersController" });
    }

    [Fact]
    public void CSharp_ExtractsXmlDocSummary()
    {
        var symbols = Parse(SourceLanguage.CSharp, "sample-cs/Models/User.cs");
        Symbol user = symbols.Single(s => s.Name == "User");
        Assert.Equal("Represents an application user.", user.Doc);
    }
}
