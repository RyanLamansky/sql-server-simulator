using System.Buffers;
using System.Text;

namespace SqlServerSimulator;

/// <summary>
/// The statements whose text SQL Server keeps from every surface that reports
/// statement or batch text, because they carry a password or secret: their
/// statement events read <c>-- CREATE LOGIN</c> (or the statement's other
/// label), a batch event's text puts <c>--*CREATE LOGIN</c> padded with
/// dashes to the statement's own length in its place, and the batch's text
/// reaches no session surface at all — no <c>sys.dm_exec_sql_text</c> row for
/// its handle, a NULL input buffer — while a plan cached for it names the
/// masked text and Query Store captures none of the statements themselves
/// (probed 2026-10-16 against SQL Server 2025). Which statements: a
/// <c>CREATE</c> / <c>ALTER LOGIN</c>, <c>CREATE</c> / <c>ALTER APPLICATION
/// ROLE</c>, <c>CREATE</c> / <c>OPEN</c> / <c>ALTER MASTER KEY</c>,
/// <c>CREATE CERTIFICATE</c>, <c>CREATE</c> / <c>OPEN SYMMETRIC KEY</c> and
/// <c>CREATE</c> / <c>ALTER ASYMMETRIC KEY</c> naming a <c>PASSWORD</c>; a
/// <c>CREATE</c> / <c>ALTER</c> [<c>DATABASE SCOPED</c>] <c>CREDENTIAL</c>
/// naming a <c>SECRET</c>, both labelled without the scope; a <c>SELECT</c>
/// calling <c>PWDENCRYPT</c>, <c>PWDCOMPARE</c>, <c>ENCRYPTBYPASSPHRASE</c>
/// or <c>DECRYPTBYPASSPHRASE</c>, labelled <c>SELECT WITHOUT QUERY</c>; and an
/// <c>EXEC</c> of <c>sp_addlogin</c>, <c>sp_password</c>,
/// <c>sp_addlinkedsrvlogin</c> or <c>sp_setapprole</c>, labelled with the
/// procedure's name as written. The words count only as tokens, so a string
/// or a comment holding them changes nothing.
/// </summary>
internal static class SecretStatements
{
    /// <summary>A word one of the statements must hold, which a text holding none can't carry a secret past.</summary>
    private static readonly SearchValues<string> Markers =
        SearchValues.Create(["password", "secret", "pwdencrypt", "pwdcompare", "passphrase", "sp_addlogin", "sp_addlinkedsrvlogin", "sp_setapprole"], StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> may carry a secret statement: one vectorized search, which every other text stops at.</summary>
    public static bool MayCarry(string? text) => text is not null && MayCarry(text.AsSpan());

    /// <summary>
    /// Whether <paramref name="text"/> may carry a secret statement, or be
    /// one: each of the statements holds a marker in its own text, so a batch
    /// asks this of each statement's span rather than of the whole text again
    /// per statement.
    /// </summary>
    public static bool MayCarry(ReadOnlySpan<char> text) => text.IndexOfAny(Markers) >= 0;

    /// <summary>The label real reports <paramref name="statement"/> under, or null for a statement it reports as written.</summary>
    public static string? Label(ReadOnlySpan<char> statement)
    {
        var words = new List<string>(4);
        var at = 0;
        while (words.Count < 4 && NextWord(statement, ref at) is { } word)
            words.Add(word);
        if (words.Count == 0)
            return null;
        var first = words[0].ToUpperInvariant();
        string? Second(int index) => index < words.Count ? words[index].ToUpperInvariant() : null;
        switch (first)
        {
            case "ALTER" or "CREATE" or "OPEN":
                {
                    var (label, marker) = (first, Second(1), Second(2), Second(3)) switch
                    {
                        ("CREATE" or "ALTER", "LOGIN", _, _) => (first + " LOGIN", "PASSWORD"),
                        ("CREATE" or "ALTER", "APPLICATION", "ROLE", _) => (first + " APPLICATION ROLE", "PASSWORD"),
                        ("CREATE" or "OPEN" or "ALTER", "MASTER", "KEY", _) => (first + " MASTER KEY", "PASSWORD"),
                        ("CREATE", "CERTIFICATE", _, _) => ("CREATE CERTIFICATE", "PASSWORD"),
                        ("CREATE" or "OPEN", "SYMMETRIC", "KEY", _) => (first + " SYMMETRIC KEY", "PASSWORD"),
                        ("CREATE" or "ALTER", "ASYMMETRIC", "KEY", _) => (first + " ASYMMETRIC KEY", "PASSWORD"),
                        ("CREATE" or "ALTER", "CREDENTIAL", _, _) or ("CREATE" or "ALTER", "DATABASE", "SCOPED", "CREDENTIAL") => (first + " CREDENTIAL", "SECRET"),
                        _ => (null, null),
                    };
                    return label is not null && HoldsWord(statement, marker!) ? label : null;
                }
            case "SELECT":
                return HoldsWord(statement, "PWDENCRYPT") || HoldsWord(statement, "PWDCOMPARE")
                    || HoldsWord(statement, "ENCRYPTBYPASSPHRASE") || HoldsWord(statement, "DECRYPTBYPASSPHRASE")
                    ? "SELECT WITHOUT QUERY"
                    : null;
            case "EXEC" or "EXECUTE":
                return CalledProcedure(statement) is { } procedure
                    && (procedure.Equals("sp_addlogin", StringComparison.OrdinalIgnoreCase) || procedure.Equals("sp_password", StringComparison.OrdinalIgnoreCase)
                        || procedure.Equals("sp_addlinkedsrvlogin", StringComparison.OrdinalIgnoreCase) || procedure.Equals("sp_setapprole", StringComparison.OrdinalIgnoreCase))
                    ? procedure
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Where the mask of a statement of <paramref name="text"/> whose last token
    /// ends at <paramref name="tight"/> ends: there for a query or
    /// an <c>EXEC</c>, and at the <c>;</c> right after it for any other (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static int MaskEnd(string text, int tight, string label) =>
        label != "SELECT WITHOUT QUERY" && !label.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) && tight + 1 < text.Length && text[tight + 1] == ';'
            ? tight + 1
            : tight;

    /// <summary><paramref name="text"/> with each span <paramref name="secrets"/> holds replaced by its label's placeholder of the same length.</summary>
    public static string Mask(string text, List<(int Start, int End, string Label)> secrets)
    {
        var builder = new StringBuilder(text);
        foreach (var (start, end, label) in secrets)
        {
            var length = end - start + 1;
            var placeholder = ("--*" + label).PadRight(length, '-');
            for (var i = 0; i < length && start + i < builder.Length; i++)
                builder[start + i] = placeholder[i];
        }
        return builder.ToString();
    }

    /// <summary>The next word of <paramref name="text"/> from <paramref name="at"/>, skipping white space, comments and every other token, or null at its end.</summary>
    private static string? NextWord(ReadOnlySpan<char> text, ref int at)
    {
        while (at < text.Length)
        {
            var c = text[at];
            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }
            if (c == '-' && at + 1 < text.Length && text[at + 1] == '-')
            {
                while (at < text.Length && text[at] != '\n')
                    at++;
                continue;
            }
            if (c == '/' && at + 1 < text.Length && text[at + 1] == '*')
            {
                var close = text[(at + 2)..].IndexOf("*/", StringComparison.Ordinal);
                at = close < 0 ? text.Length : at + 4 + close;
                continue;
            }
            if (c is '\'' or '"' or '[')
            {
                var closing = c == '[' ? ']' : c;
                for (at++; at < text.Length; at++)
                {
                    if (text[at] != closing)
                        continue;
                    if (at + 1 < text.Length && text[at + 1] == closing)
                    {
                        at++;
                        continue;
                    }
                    break;
                }
                at++;
                return c == '[' ? "[" : "'";
            }
            if (char.IsLetter(c) || c == '_' || c == '@' || c == '#')
            {
                var from = at;
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] is '_' or '@' or '#' or '$'))
                    at++;
                return text[from..at].ToString();
            }
            at++;
            return c.ToString();
        }
        return null;
    }

    /// <summary>Whether <paramref name="text"/> holds <paramref name="word"/> as a word token, not inside a string, a quoted name or a comment.</summary>
    private static bool HoldsWord(ReadOnlySpan<char> text, string word)
    {
        var at = 0;
        while (NextWord(text, ref at) is { } next)
        {
            if (next.Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The leaf name of the procedure an <c>EXEC</c> statement calls, as written, past any <c>@status =</c>.</summary>
    private static string? CalledProcedure(ReadOnlySpan<char> statement)
    {
        var at = 0;
        _ = NextWord(statement, ref at);
        var word = NextWord(statement, ref at);
        if (word is not null && word.StartsWith('@'))
        {
            if (NextWord(statement, ref at) != "=")
                return null;
            word = NextWord(statement, ref at);
        }
        var leaf = word;
        while (true)
        {
            var save = at;
            if (NextWord(statement, ref at) != ".")
            {
                at = save;
                return leaf is null or "[" or "'" ? null : leaf;
            }
            leaf = NextWord(statement, ref at);
        }
    }
}
