using SqlServerSimulator.Parser.Tokens;
using System.Text;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Specializes in refining a SQL command string into sequence of <see cref="Token"/> instances.
/// </summary>
static class Tokenizer
{
    /// <summary>
    /// Provides the next <see cref="Token"/> from the provided SQL command text beginning at <paramref name="index"/>.
    /// </summary>
    /// <param name="command">The command from which a token is produced.</param>
    /// <param name="index">The position within <paramref name="command"/> of the previous token (or -1), updated to where the next token ends.</param>
    /// <returns>The next token, or null if the end of <paramref name="command"/> has been reached.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect or unsupported syntax.</exception>
    public static Token? NextToken(string command, ref int index) =>
        ++index >= command.Length ? null : command[index] switch
        {
            ' ' or '\r' or '\n' or '\t' => ParseWhitespace(command, ref index),
            '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') => ParseUnquotedStringOrReservedKeyword(command, ref index),
            >= '0' and <= '9' => ParseNumeric(command, ref index),
            '@' => ParseAtOrDoubleAtPrefixedString(command, ref index),
            '-' => ParseMinusOrComment(command, ref index),
            '/' => ParseForwardSlashOrComment(command, ref index),
            '[' => ParseBracketDelimitedString(command, ref index),
            '+' or '*' or '(' or ')' or ',' or '.' or ';' or '=' => new Operator(command, index),
            var c => throw SimulatedSqlException.SyntaxErrorNear(c) // Might throw on valid-but-unsupported syntax.
        };

    private static Whitespace ParseWhitespace(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length)
        {
            switch (command[index])
            {
                case ' ':
                case '\r':
                case '\n':
                case '\t':
                    continue;
            }

            break;
        }

        return new(command, start, index-- - start);
    }

    private static Token ParseUnquotedStringOrReservedKeyword(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length)
        {
            var c = command[index];

            if (char.IsLetterOrDigit(c) || c == '_')
                continue;

            break;
        }

        return UnquotedString.CheckReserved(command, start, index-- - start);
    }

    private static Numeric ParseNumeric(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length)
        {
            if (command[index] is >= '0' and <= '9')
                continue;

            break;
        }

        return new(command, start, index-- - start);
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

        while (++index < command.Length)
        {
            var c = command[index];

            if (char.IsLetterOrDigit(c) || c == '_')
                continue;

            break;
        }

        return doubleAt ?
            new DoubleAtPrefixedString(command, start, index-- - start) :
            new AtPrefixedString(command, start, index-- - start);
    }

    private static Token ParseMinusOrComment(string command, ref int index)
    {
        var start = index;
        if (++index == command.Length)
            return new Operator(command, --index);
        if (command[index] != '-')
        {
            index--;
            return new Operator(command, index);
        }

        return Comment.ParseSingleLine(start, ref index, command);
    }

    private static Token ParseForwardSlashOrComment(string command, ref int index)
    {
        var start = index;
        if (++index == command.Length)
            return new Operator(command, --index);
        if (command[index] != '*')
        {
            index--;
            return new Operator(command, index);
        }

        return Comment.ParseBlock(start, ref index, command);
    }

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

        return new(builder.ToString(), command, start, index - start);
    }
}
