using RepoContext.Core.Parsing;

namespace RepoContext.Core.Tests.Parsing;

/// <summary>
/// Declarations in the languages no bundled grammar covers (ADR 0020). Before
/// this, a Python or Go file had no outline at all, which in a mixed repository
/// is a hole in the first place an agent looks.
/// </summary>
public class DeclarationExtractorTests
{
    private static (string Name, string Kind)[] Extract(string path, string content) =>
        [.. DeclarationExtractor.Extract(path, content)
            .Select(s => (s.Name, s.Kind.ToString().ToLowerInvariant()))];

    [Fact]
    public void Python_ClassesAndFunctions_WithTheDocstringAsSummary()
    {
        // Four-quote delimiters so the Python docstring can contain three.
        const string content = """"
            class LoginService:
                """Handles login."""

                def authenticate(self, user):
                    return True

            async def refresh(token):
                pass
            """";

        IReadOnlyList<Symbol> symbols = DeclarationExtractor.Extract("src/app.py", content);

        Assert.Equal(
            [("LoginService", "class"), ("authenticate", "function"), ("refresh", "function")],
            symbols.Select(s => (s.Name, s.Kind.ToString().ToLowerInvariant())));

        // The class closes at the next declaration of the same indentation.
        Symbol service = symbols[0];
        Assert.Equal(1, service.StartLine);
        Assert.Equal(6, service.EndLine);
        Assert.Equal("\"\"\"Handles login.\"\"\"", service.Doc);
    }

    [Fact]
    public void Go_NamedTypesCarryTheirUnderlyingKind()
    {
        const string content = """
            type Session struct {
            	ID string
            }

            type Store interface {
            	Get(id string) *Session
            }

            type ID = string

            // NewSession starts a session.
            func NewSession(id string) *Session {
            	return &Session{ID: id}
            }

            func (s *Session) Close() {}
            """;

        (string Name, string Kind)[] symbols = Extract("src/main.go", content);

        Assert.Equal(
            [
                ("Session", "struct"),
                ("Store", "interface"),
                ("ID", "typealias"),
                ("NewSession", "function"),
                ("Close", "function"),
            ],
            symbols);
        Assert.Equal(
            "NewSession starts a session.",
            DeclarationExtractor.Extract("src/main.go", content)
                .Single(s => s.Name == "NewSession").Doc);
    }

    [Fact]
    public void Java_TypesAndModifiedMethods()
    {
        const string content = """
            public class LoginController {
                private final Store store;

                public String login(String user) throws IOException {
                    return user;
                }

                void packagePrivate() {
                }
            }
            """;

        (string Name, string Kind)[] symbols = Extract("src/Login.java", content);

        // A method needs a modifier to be told apart from a call; the
        // package-private one is deliberately missed rather than guessed at.
        Assert.Equal([("LoginController", "class"), ("login", "method")], symbols);
    }

    [Fact]
    public void Kotlin_Rust_Ruby_Php_Swift_AreCovered()
    {
        Assert.Equal(
            [("Session", "class"), ("refresh", "function")],
            Extract("a.kt", "data class Session(val id: String)\nfun refresh() {}\n"));

        Assert.Equal(
            [("Session", "struct"), ("Store", "interface"), ("open", "function")],
            Extract("a.rs", "pub struct Session;\npub trait Store {}\npub async fn open() {}\n"));

        Assert.Equal(
            [("Session", "class"), ("refresh", "function")],
            Extract("a.rb", "class Session\n  def refresh\n  end\nend\n"));

        Assert.Equal(
            [("Session", "class"), ("refresh", "function")],
            Extract("a.php", "final class Session {\n  public function refresh() {}\n}\n"));

        Assert.Equal(
            [("Session", "struct"), ("refresh", "function")],
            Extract("a.swift", "public struct Session {\n  func refresh() {}\n}\n"));
    }

    [Fact]
    public void InterfaceLanguagesAndScripts_AreCovered()
    {
        Assert.Equal(
            [("Billing", "interface"), ("Refund", "function"), ("RefundRequest", "struct")],
            Extract(
                "api.proto",
                "service Billing {\n  rpc Refund (RefundRequest) returns (Reply);\n}\n"
                + "message RefundRequest {\n  string id = 1;\n}\n"));

        Assert.Equal(
            [("Payment", "typealias"), ("Query", "typealias")],
            Extract("schema.graphql", "type Payment {\n  id: ID!\n}\ntype Query {\n  a: Int\n}\n"));

        Assert.Equal(
            [("deploy", "function")],
            Extract("deploy.sh", "#!/bin/sh\ndeploy() {\n  echo hi\n}\n"));

        Assert.Equal(
            [("Get-Session", "function")],
            Extract("mod.psm1", "function Get-Session {\n}\n"));
    }

    [Fact]
    public void CFamily_EmitsTaggedTypesOnly()
    {
        // A one-line C function definition cannot be told from a call or a
        // prototype often enough to be worth the wrong answers.
        Assert.Equal(
            [("Session", "struct"), ("Kind", "enum")],
            Extract("a.c", "struct Session {\n  int id;\n};\nenum Kind { A, B };\nint main(void) {\n}\n"));
    }

    [Fact]
    public void UnknownLanguages_AndEmptyContent_YieldNothing()
    {
        Assert.False(DeclarationExtractor.Supports("src/app.ts"));
        Assert.Empty(DeclarationExtractor.Extract("src/app.ts", "export const a = 1;"));
        Assert.Empty(DeclarationExtractor.Extract("src/app.py", string.Empty));
    }

    [Fact]
    public void Extraction_IsDeterministic()
    {
        const string content = "class A:\n    def b(self):\n        pass\n";

        Assert.Equal(
            DeclarationExtractor.Extract("a.py", content),
            DeclarationExtractor.Extract("a.py", content));
    }
}
