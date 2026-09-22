using System.Text;

namespace RepoContext.Cli.Output;

/// <summary>
/// Makes repository text safe to show on an interactive terminal.
/// </summary>
/// <remarks>
/// <para>
/// Query output embeds repository content - source spans, headings, paths,
/// patch hunks - and a checkout is untrusted. Written raw to a terminal, an
/// embedded escape sequence is not text but a command to the terminal: it can
/// retitle the window, write the clipboard (OSC 52), forge a hyperlink, or move
/// the cursor to paint over lines so a human reviewing the output sees
/// something other than what an agent receives.
/// </para>
/// <para>
/// Only a terminal gets the neutralized form. A pipe - which is how agents and
/// scripts read the output - keeps the exact bytes, so the determinism contract
/// and every recorded token count are untouched.
/// </para>
/// </remarks>
public static class TerminalText
{
    /// <summary>
    /// Replaces every control character except tab, newline and a carriage
    /// return that ends a line with a visible stand-in: C0 controls with their
    /// Unicode control picture (ESC becomes <c>␛</c>), DEL with <c>␡</c>, and C1
    /// controls with U+FFFD. Returns <paramref name="text"/> itself when nothing
    /// needs replacing.
    /// </summary>
    public static string Neutralize(string text)
    {
        int first = 0;
        while (first < text.Length && !NeedsReplacement(text, first))
        {
            first++;
        }

        if (first == text.Length)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (!NeedsReplacement(text, i))
            {
                sb.Append(c);
            }
            else if (c < ' ')
            {
                sb.Append((char)(0x2400 + c));
            }
            else if (c == '\u007F')
            {
                sb.Append('␡');
            }
            else
            {
                sb.Append('�');
            }
        }

        return sb.ToString();
    }

    private static bool NeedsReplacement(string text, int index)
    {
        char c = text[index];
        return c switch
        {
            '\t' or '\n' => false,
            // CRLF line endings are ordinary text; a lone CR returns the cursor
            // to the start of the line and lets what follows overwrite it.
            '\r' => index + 1 >= text.Length || text[index + 1] != '\n',
            _ => c < ' ' || c is >= '\u007F' and <= '\u009F',
        };
    }
}
