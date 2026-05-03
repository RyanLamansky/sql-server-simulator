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
/// </remarks>
static class Tokenizer
{
    /// <summary>
    /// Provides the next <see cref="Token"/> from the provided SQL command text beginning at <paramref name="index"/>.
    /// </summary>
    /// <param name="command">The command from which a token is produced.</param>
    /// <param name="index">The position of the next un-read character (0 to begin); updated to the next un-read position past the returned token.</param>
    /// <returns>The next token, or null if the end of <paramref name="command"/> has been reached.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect or unsupported syntax.</exception>
    public static Token? NextToken(string command, ref int index) =>
        index >= command.Length ? null : command[index] switch
        {
            ' ' or '\r' or '\n' or '\t' => ParseWhitespace(command, ref index),
            'N' or 'n' when index + 1 < command.Length && command[index + 1] == '\'' => ParseNPrefixedStringLiteral(command, ref index),
            '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') => ParseUnquotedStringOrReservedKeyword(command, ref index),
            '0' when index + 1 < command.Length && (command[index + 1] == 'x' || command[index + 1] == 'X') => ParseHexLiteral(command, ref index),
            >= '0' and <= '9' => ParseNumeric(command, ref index),
            '\'' => ParseStringLiteral(command, ref index),
            '@' => ParseAtOrDoubleAtPrefixedString(command, ref index),
            '-' => ParseMinusOrComment(command, ref index),
            '/' => ParseForwardSlashOrComment(command, ref index),
            '[' => ParseBracketDelimitedString(command, ref index),
            '+' or '*' or '%' or '(' or ')' or ',' or '.' or ';' or '=' or '&' or '|' or '^' or '>' or '<' or '!' => new Operator(command, index++),
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

    private static Token ParseUnquotedStringOrReservedKeyword(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length)
        {
            var c = command[index];
            if (!char.IsLetterOrDigit(c) && c != '_')
                break;
        }

        return UnquotedString.CheckReserved(command, start, index - start);
    }

    private static Numeric ParseNumeric(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length && command[index] is >= '0' and <= '9')
        {
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
            var c = command[index];
            if (!char.IsLetterOrDigit(c) && c != '_')
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
    /// returns a <see cref="Literal"/> typed as <see cref="SqlType.Varchar"/>.
    /// </summary>
    private static Literal ParseStringLiteral(string command, ref int index)
    {
        var start = index;
        var builder = new StringBuilder();
        while (++index < command.Length)
        {
            var c = command[index];
            if (c != '\'')
            {
                _ = builder.Append(c);
                continue;
            }

            if (index + 1 < command.Length && command[index + 1] == '\'')
            {
                _ = builder.Append('\'');
                index++;
                continue;
            }

            return new Literal(SqlValue.FromVarchar(builder.ToString()), command, start, ++index - start);
        }

        throw SimulatedSqlException.UnclosedStringLiteral();
    }

    /// <summary>
    /// Parses an N-prefixed Unicode string literal: <c>N'foo'</c>. The leading
    /// N (or n) is at <paramref name="index"/>; the body uses the same
    /// <c>''</c>-escape rules as a plain string literal but the result is
    /// typed as <see cref="SqlType.NVarchar"/>.
    /// </summary>
    private static Literal ParseNPrefixedStringLiteral(string command, ref int index)
    {
        var start = index;
        index++; // skip the N
        var builder = new StringBuilder();
        while (++index < command.Length)
        {
            var c = command[index];
            if (c != '\'')
            {
                _ = builder.Append(c);
                continue;
            }

            if (index + 1 < command.Length && command[index + 1] == '\'')
            {
                _ = builder.Append('\'');
                index++;
                continue;
            }

            return new Literal(SqlValue.FromNVarchar(builder.ToString()), command, start, ++index - start);
        }

        throw SimulatedSqlException.UnclosedStringLiteral();
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

    private static BracketDelimitedString ParseBracketDelimitedString(string command, ref int index)
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
            index++;
        return new(builder.ToString(), command, start, length);
    }
}
