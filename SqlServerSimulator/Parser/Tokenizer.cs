using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Text;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Specializes in refining a SQL command string into sequence of <see cref="Token"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Index contract.</b> Every parse function (and <see cref="NextToken"/>
/// itself) leaves <c>index</c> at the position of the next un-read character
/// — one past the last character consumed by the returned token. Callers
/// never need to "step back" or "step forward" to re-align: invoking
/// <see cref="NextToken"/> again with the same <c>ref index</c> reads the
/// next token. To begin tokenizing a new command, start with <c>index = 0</c>.
/// </para>
/// <para>
/// This invariant is what lets each parser end with <c>index - start</c> for
/// length math, with no off-by-one. Adding a new token type? Scan forward
/// while characters match, stop at the first that doesn't, return — the
/// natural exit position of that loop is already correct.
/// </para>
/// <para>
/// <b>Active collation.</b> String literals (<c>'foo'</c>, <c>N'foo'</c>)
/// produce <see cref="SqlValue"/>s tagged with the active database
/// collation at <see cref="Coercibility.CoercibleDefault"/>, matching
/// real SQL Server's rule that a string literal inherits the executing
/// database's collation. Callers thread <see cref="ParserContext.CurrentDatabase"/>'s
/// collation into <see cref="NextToken"/>; other literal kinds (varbinary,
/// money) don't carry collation and ignore the parameter.
/// </para>
/// </remarks>
static class Tokenizer
{
    /// <summary>
    /// Provides the next <see cref="Token"/> from the provided SQL command text beginning at <paramref name="index"/>.
    /// </summary>
    /// <param name="command">The command from which a token is produced.</param>
    /// <param name="index">The position of the next un-read character (0 to begin); updated to the next un-read position past the returned token.</param>
    /// <param name="activeCollation">Collation tagged onto string-literal <see cref="SqlValue"/>s; supplied by the caller's active <see cref="Database"/>.</param>
    /// <param name="quotedIdentifiers">The effective <c>QUOTED_IDENTIFIER</c> setting at this parse position: <see langword="true"/> (the default) tokenizes <c>"…"</c> as a delimited identifier, <see langword="false"/> as a varchar string literal. Threaded from <see cref="ParserContext.QuotedIdentifiers"/>.</param>
    /// <returns>The next token, or null if the end of <paramref name="command"/> has been reached.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect or unsupported syntax.</exception>
    public static Token? NextToken(string command, ref int index, Collation activeCollation, bool quotedIdentifiers = true) =>
        index >= command.Length ? null : command[index] switch
        {
            ' ' or '\r' or '\n' or '\t' => ParseWhitespace(command, ref index),
            'N' or 'n' when index + 1 < command.Length && command[index + 1] == '\'' => ParseNPrefixedStringLiteral(command, ref index, activeCollation),
            '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') => ParseUnquotedStringOrReservedKeyword(command, ref index),
            '0' when index + 1 < command.Length && (command[index + 1] == 'x' || command[index + 1] == 'X') => ParseHexLiteral(command, ref index),
            >= '0' and <= '9' => ParseNumeric(command, ref index),
            '\'' => ParseStringLiteral(command, ref index, activeCollation),
            '"' when quotedIdentifiers => ParseQuoteDelimitedIdentifier(command, ref index),
            '"' => ParseDoubleQuotedStringLiteral(command, ref index, activeCollation),
            '@' => ParseAtOrDoubleAtPrefixedString(command, ref index),
            '#' => ParseHashPrefixedName(command, ref index),
            '-' => ParseMinusOrComment(command, ref index),
            '/' => ParseForwardSlashOrComment(command, ref index),
            '[' => ParseBracketDelimitedIdentifier(command, ref index),
            '+' or '*' or '%' or '(' or ')' or ',' or '.' or ';' or ':' or '=' or '&' or '|' or '^' or '>' or '<' or '!' => new Operator(command, index++),
            '$' when IsDollarAction(command, index) => ParseDollarAction(command, ref index),
            '$' or '¢' or '£' or '¥' or '฿' or (>= '₠' and <= '₱') => ParseCurrencyLiteral(command, ref index),
            // Non-ASCII BMP letters (fullwidth, accented, Greek, CJK, ...) start identifiers on real SQL Server — probe-confirmed against SQL Server 2025.
            var c when char.IsLetter(c) => ParseUnquotedStringOrReservedKeyword(command, ref index),
            var c => throw SimulatedSqlException.SyntaxErrorNear(c) // Might throw on valid-but-unsupported syntax.
        };

    private static Whitespace ParseWhitespace(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length && command[index] is ' ' or '\r' or '\n' or '\t')
        {
        }

        return new(command, start, index - start);
    }

    /// <summary>
    /// True when <paramref name="c"/> can continue an unquoted identifier
    /// (including <c>@</c>-name and <c>#</c>-name) body: letters, digits,
    /// underscore, plus non-spacing combining marks — probe-confirmed
    /// (2026-07-13) against SQL Server 2025: a decomposed identifier
    /// spelling (<c>zzcafe</c> + U+0301 COMBINING ACUTE ACCENT) both
    /// tokenizes and resolves to a table created as composed
    /// <c>zzcafé</c>.
    /// </summary>
    private static bool IsIdentifierBodyChar(char c) =>
        char.IsLetterOrDigit(c)
        || c == '_'
        || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;

    private static Token ParseUnquotedStringOrReservedKeyword(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length)
        {
            if (!IsIdentifierBodyChar(command[index]))
                break;
        }

        return UnquotedString.CheckReserved(command, start, index - start);
    }

    /// <summary>
    /// Parses a <c>#</c>-prefixed local-temp or <c>##</c>-prefixed global-temp
    /// identifier. The leading <c>#</c> (and optional second <c>#</c>) are part
    /// of the token's value; the trailing identifier body follows the same
    /// letter/digit/underscore rule as a regular unquoted name. The bare forms
    /// <c>#</c> and <c>##</c> are valid identifiers in SQL Server, so the
    /// trailing body is allowed to be empty.
    /// </summary>
    private static Token ParseHashPrefixedName(string command, ref int index)
    {
        var start = index;
        index++; // leading '#'
        if (index < command.Length && command[index] == '#')
            index++; // optional second '#' for ## globals
        while (index < command.Length)
        {
            if (!IsIdentifierBodyChar(command[index]))
                break;
            index++;
        }
        // CheckReserved short-circuits to UnquotedString because no reserved
        // keyword begins with '#'.
        return UnquotedString.CheckReserved(command, start, index - start);
    }

    /// <summary>
    /// Parses a SQL numeric literal — integer (<c>123</c>), decimal-with-
    /// fractional-part (<c>123.456</c>), or scientific (<c>1.5e2</c>,
    /// <c>1E+10</c>). The starting digit is at <paramref name="index"/>;
    /// scanning advances through fractional and exponent parts only when the
    /// next character actually fits the grammar, so a digit followed by a
    /// non-numeric <c>.</c> (e.g. <c>1.alias</c>) leaves the dot for the
    /// outer expression parser. The token's typed value is computed inside
    /// <see cref="Numeric"/>'s constructor.
    /// </summary>
    private static Numeric ParseNumeric(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length && command[index] is >= '0' and <= '9')
        {
        }

        // Fractional part: '.' followed by at least one digit. A bare '.'
        // belongs to the surrounding expression (e.g. dotted-name reference).
        if (index + 1 < command.Length && command[index] == '.' && command[index + 1] is >= '0' and <= '9')
        {
            index++;
            while (index < command.Length && command[index] is >= '0' and <= '9')
                index++;
        }

        // Exponent: 'e'/'E' followed by optional sign and at least one digit.
        if (index < command.Length && (command[index] == 'e' || command[index] == 'E'))
        {
            var afterExp = index + 1;
            if (afterExp < command.Length && (command[afterExp] == '+' || command[afterExp] == '-'))
                afterExp++;
            if (afterExp < command.Length && command[afterExp] is >= '0' and <= '9')
            {
                index = afterExp;
                while (index < command.Length && command[index] is >= '0' and <= '9')
                    index++;
            }
        }

        return new(command, start, index - start);
    }

    private static Token ParseAtOrDoubleAtPrefixedString(string command, ref int index)
    {
        var start = index;
        if (++index == command.Length)
            throw SimulatedSqlException.MustDeclareScalarVariable(string.Empty);

        bool doubleAt;
        if (command[index] == '@')
        {
            doubleAt = true;
            index++;
        }
        else
        {
            doubleAt = false;
        }

        while (index < command.Length)
        {
            if (!IsIdentifierBodyChar(command[index]))
                break;
            index++;
        }

        return doubleAt ?
            new DoubleAtPrefixedString(command, start, index - start) :
            new AtPrefixedString(command, start, index - start);
    }

    private static Token ParseMinusOrComment(string command, ref int index)
    {
        var start = index;
        return ++index == command.Length || command[index] != '-'
            ? new Operator(command, start)
            : Comment.ParseSingleLine(start, ref index, command);
    }

    private static Token ParseForwardSlashOrComment(string command, ref int index)
    {
        var start = index;
        return ++index == command.Length || command[index] != '*'
            ? new Operator(command, start)
            : Comment.ParseBlock(start, ref index, command);
    }

    /// <summary>
    /// Parses a SQL string literal: <c>'foo'</c>, with <c>''</c> as the
    /// embedded-apostrophe escape. The opening quote is at <paramref name="index"/>;
    /// returns a <see cref="Literal"/> typed as <see cref="SqlType.Varchar"/>
    /// tagged with <paramref name="activeCollation"/> at
    /// <see cref="Coercibility.CoercibleDefault"/>.
    /// </summary>
    private static Literal ParseStringLiteral(string command, ref int index, Collation activeCollation)
    {
        var start = index;
        var body = ParseQuotedBody(command, ref index, '\'');
        var literalType = VarcharSqlType.Get(0, activeCollation, Coercibility.CoercibleDefault);
        return new Literal(SqlValue.FromVarchar(literalType, body), command, start, index - start);
    }

    /// <summary>
    /// Parses a double-quoted string literal — the <c>SET QUOTED_IDENTIFIER
    /// OFF</c> reading of <c>"foo"</c>, with <c>""</c> as the embedded-quote
    /// escape (an apostrophe needs no escape inside it). Typed exactly like a
    /// single-quoted literal: <see cref="SqlType.Varchar"/> tagged with
    /// <paramref name="activeCollation"/> at
    /// <see cref="Coercibility.CoercibleDefault"/>. There is no N-prefixed
    /// double-quoted form — <c>N"foo"</c> tokenizes as the identifier
    /// <c>N</c> followed by this literal (probe-confirmed).
    /// </summary>
    private static Literal ParseDoubleQuotedStringLiteral(string command, ref int index, Collation activeCollation)
    {
        var start = index;
        var body = ParseQuotedBody(command, ref index, '"');
        var literalType = VarcharSqlType.Get(0, activeCollation, Coercibility.CoercibleDefault);
        return new Literal(SqlValue.FromVarchar(literalType, body), command, start, index - start);
    }

    /// <summary>
    /// Parses an N-prefixed Unicode string literal: <c>N'foo'</c>. The leading
    /// N (or n) is at <paramref name="index"/>; the body uses the same
    /// <c>''</c>-escape rules as a plain string literal but the result is
    /// typed as <see cref="SqlType.NVarchar"/> tagged with
    /// <paramref name="activeCollation"/> at
    /// <see cref="Coercibility.CoercibleDefault"/>.
    /// </summary>
    private static Literal ParseNPrefixedStringLiteral(string command, ref int index, Collation activeCollation)
    {
        var start = index;
        index++; // skip the N
        var body = ParseQuotedBody(command, ref index, '\'');
        var literalType = NVarcharSqlType.Get(0, activeCollation, Coercibility.CoercibleDefault);
        return new Literal(SqlValue.FromNVarchar(literalType, body), command, start, index - start);
    }

    /// <summary>
    /// Scans a quote-delimited body whose opening <paramref name="quote"/> is
    /// at <paramref name="index"/>, unescaping the doubled-quote form, and
    /// leaves <paramref name="index"/> one past the closing quote. Shared by
    /// the <c>'…'</c> / <c>N'…'</c> / <c>"…"</c> literal parsers and the
    /// quote-delimited-identifier parser — all four use the same
    /// doubled-delimiter escape and the same Msg 105 on end-of-input
    /// (probe-confirmed for <c>"</c> against SQL Server 2025, echoing the
    /// scanned body in the message).
    /// </summary>
    private static string ParseQuotedBody(string command, ref int index, char quote)
    {
        var builder = new StringBuilder();
        while (++index < command.Length)
        {
            var c = command[index];
            if (c != quote)
            {
                _ = builder.Append(c);
                continue;
            }

            if (index + 1 < command.Length && command[index + 1] == quote)
            {
                _ = builder.Append(quote);
                index++;
                continue;
            }

            index++;
            return builder.ToString();
        }

        throw SimulatedSqlException.UnclosedStringLiteral(builder.ToString());
    }

    /// <summary>
    /// Parses a double-quote-delimited identifier — the default
    /// <c>SET QUOTED_IDENTIFIER ON</c> reading of <c>"foo"</c>, with
    /// <c>""</c> as the embedded-quote escape (<c>[</c> / <c>]</c> /
    /// <c>'</c> are ordinary characters inside). An empty body raises
    /// Msg 1038 — probe-confirmed at every identifier position, not just
    /// aliases. Unclosed raises Msg 105, unlike the historically lenient
    /// bracket form.
    /// </summary>
    private static DelimitedIdentifier ParseQuoteDelimitedIdentifier(string command, ref int index)
    {
        var start = index;
        var value = ParseQuotedBody(command, ref index, '"');
        return value.Length == 0
            ? throw SimulatedSqlException.EmptyColumnAlias()
            : new(value, command, start, index - start);
    }

    /// <summary>
    /// Parses a SQL binary literal: <c>0xDEADBEEF</c>. The leading <c>0</c> is
    /// at <paramref name="index"/>. An odd-length hex body is left-padded with
    /// a leading nibble of <c>0</c> (matching SQL Server's behavior). A bare
    /// <c>0x</c> with no hex digits is a zero-length varbinary, which SQL
    /// Server also accepts. Returns a <see cref="Literal"/> typed as
    /// <see cref="SqlType.Varbinary"/>.
    /// </summary>
    private static Literal ParseHexLiteral(string command, ref int index)
    {
        var start = index;
        index += 2; // skip '0x' / '0X'
        var bodyStart = index;
        while (index < command.Length && IsHexDigit(command[index]))
            index++;

        var bodyLength = index - bodyStart;
        var bytes = bodyLength == 0 ? []
            : bodyLength % 2 == 0 ? Convert.FromHexString(command.AsSpan(bodyStart, bodyLength))
            : Convert.FromHexString(string.Concat("0", command.AsSpan(bodyStart, bodyLength)));

        return new Literal(SqlValue.FromVarbinary(bytes), command, start, index - start);
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>
    /// Returns true when the cursor sits on a <c>$action</c> pseudo-column
    /// — only recognized in MERGE's OUTPUT clause, but tokenized here so
    /// it surfaces as a single <see cref="UnquotedString"/> with value
    /// <c>"$action"</c> rather than tokenizing into a money-literal
    /// <c>$</c> followed by an unrelated identifier. Word-boundary
    /// terminated (rejects <c>$action_</c>).
    /// </summary>
    private static bool IsDollarAction(string command, int index)
    {
        if (index + 6 >= command.Length)
            return false;
        var body = command.AsSpan(index + 1, 6);
        if (!body.Equals("action", StringComparison.OrdinalIgnoreCase))
            return false;
        if (index + 7 < command.Length)
        {
            var next = command[index + 7];
            if (char.IsLetterOrDigit(next) || next == '_')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Emits the <c>$action</c> pseudo-column as a single
    /// <see cref="UnquotedString"/>. The MERGE OUTPUT parser detects it
    /// by string comparison and synthesizes a <c>MergeActionReference</c>
    /// expression.
    /// </summary>
    private static UnquotedString ParseDollarAction(string command, ref int index)
    {
        var start = index;
        index += 7;
        return (UnquotedString)UnquotedString.CheckReserved(command, start, 7);
    }

    /// <summary>
    /// Parses a money literal: a single currency symbol followed by an
    /// optional sign, optional digits, and an optional fractional part.
    /// Lone currency symbols are valid (parse to <c>0.0000</c>) — verified
    /// against SQL Server 2025. Scientific notation is not accepted (the
    /// digit scanner stops at <c>e</c>/<c>E</c>).
    /// </summary>
    private static Literal ParseCurrencyLiteral(string command, ref int index)
    {
        var start = index;
        index++; // consume the currency symbol
        if (index < command.Length && (command[index] == '+' || command[index] == '-'))
            index++;
        // Skip optional whitespace between symbol/sign and digits — SQL
        // Server accepts <c>$ 5</c>.
        while (index < command.Length && command[index] is ' ' or '\t')
            index++;
        while (index < command.Length && command[index] is >= '0' and <= '9')
            index++;
        // SQL Server accepts <c>$5.</c> (lone trailing dot) — consume the
        // dot here even when no fractional digits follow.
        if (index < command.Length && command[index] == '.')
        {
            index++;
            while (index < command.Length && command[index] is >= '0' and <= '9')
                index++;
        }
        var body = command.AsSpan(start, index - start);
        // Reuse the runtime money parser so the simulator has a single
        // source of truth for money string parsing.
        var value = SqlValue.FromMoney(SqlType.Money, ParseLiteralBody(body));
        return new Literal(value, command, start, index - start);
    }

    /// <summary>
    /// Lightweight parse for the literal-form body (currency symbol
    /// already consumed). Produces a <see cref="decimal"/>; range checks
    /// happen later inside <see cref="SqlValue.FromMoney"/>.
    /// </summary>
    private static decimal ParseLiteralBody(ReadOnlySpan<char> body)
    {
        // Strip the currency symbol that's always at index 0.
        body = body[1..].Trim();
        if (body.Length == 0)
            return 0m;
        var negative = false;
        if (body[0] is '+' or '-')
        {
            negative = body[0] == '-';
            body = body[1..].TrimStart();
        }
        return body.Length == 0 ? 0m
            : decimal.TryParse(
                body,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d)
                    ? (negative ? -d : d)
                    : 0m;
    }

    /// <summary>
    /// Parses a bracket-delimited identifier: <c>[foo]</c>, with <c>]]</c>
    /// as the embedded-bracket escape. A properly-closed empty <c>[]</c>
    /// raises Msg 1038 (probe-confirmed at every identifier position, same
    /// as the empty <c>""</c> form); an unclosed <c>[</c> at end-of-input
    /// keeps its historically lenient empty-token behavior.
    /// </summary>
    private static DelimitedIdentifier ParseBracketDelimitedIdentifier(string command, ref int index)
    {
        var start = index;
        var builder = new StringBuilder();
        while (++index < command.Length)
        {
            var c = command[index];
            if (c != ']')
            {
                _ = builder.Append(c);
                continue;
            }

            if (index + 1 < command.Length && command[index + 1] == ']')
            {
                _ = builder.Append(']');
                index++;
                continue;
            }

            break;
        }

        var length = index - start;
        if (index < command.Length)
        {
            index++;
            if (builder.Length == 0)
                throw SimulatedSqlException.EmptyColumnAlias();
        }

        return new(builder.ToString(), command, start, length);
    }
}
