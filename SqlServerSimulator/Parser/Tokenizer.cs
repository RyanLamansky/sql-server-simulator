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
    public static Token? NextToken(string command, ref int index) =>
        ++index >= command.Length ? null : command[index] switch
        {
            ' ' or '\r' or '\n' or '\t' => ParseWhitespace(command, ref index),
            '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') => ParseUnquotedStringOrReservedKeyword(command, ref index),
            >= '0' and <= '9' => ParseNumeric(command, ref index),
            '@' => ParseAtOrDoubleAtPrefixedString(command, ref index),
            '-' => ParseMinusOrComment(command, ref index),
            '[' => ParseBracketDelimitedString(command, ref index),
            '+' => new Plus(),
            '*' => new Asterisk(),
            '(' => new OpenParentheses(),
            ')' => new CloseParentheses(),
            ',' => new Comma(),
            '.' => new Period(),
            ';' => new StatementTerminator(),
            _ => throw new NotSupportedException($"Simulated tokenizer doesn't know what to do with character '{command[index]}' at index {index}.")
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

        return new(new StringBuilder(command, start, index - start, index-- - start));
    }

    private static Token ParseAtOrDoubleAtPrefixedString(string command, ref int index)
    {
        var start = index;
        if (++index == command.Length)
            throw new SimulatedSqlException("Must declare the scalar variable \"@\".");

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

        return doubleAt
            ? new DoubleAtPrefixedString(new StringBuilder(command, start + 2, index-- - (start + 2), index - (start + 2) - 1))
            : new AtPrefixedString(new StringBuilder(command, start + 1, index-- - (start + 1), index - (start + 1) - 1));
    }

    private static Token ParseMinusOrComment(string command, ref int index)
    {
        if (++index == command.Length)
            return new Minus();
        if (command[index] != '-')
        {
            index--;
            return new Minus();
        }

        while (++index < command.Length)
        {
            switch (command[index])
            {
                case '\r':
                case '\n':
                    return new Comment();
            }
        }

        return new Comment();
    }

    private static BracketDelimitedString ParseBracketDelimitedString(string command, ref int index)
    {
        var start = index;
        while (++index < command.Length)
        {
            if (command[index] != ']')
                continue;

            index++;
            break;
        }

        return new(new StringBuilder(command, start + 1, index - start - 2, index-- - start - 2));
    }
}
