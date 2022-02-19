using System.Text;

namespace SqlServerSimulator.Parser;

using Tokens;

static class Tokenizer
{
    enum State
    {
        None,
        UnquotedString,
        BracketDelimitedString,
        SingleQuotedString,
        DoubleQuotedString,
        AtPrefixedString,
        DoubleAtPrefixedString,
        Numeric
    }

    public static IEnumerable<Token> Tokenize(string? command)
    {
        if (string.IsNullOrEmpty(command))
            throw new InvalidOperationException("ExecuteReader: CommandText property has not been initialized");

        using var commandEnumerator = command.GetEnumerator();

        var state = State.None;
        var index = -1;
        var buffer = new StringBuilder();

        while (commandEnumerator.TryGetNext(out var c, ref index))
        {
            switch (c)
            {
                default:
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    {
                        switch (state)
                        {
                            case State.None:
                                state = State.UnquotedString;
                                break;
                        }

                        buffer.Append(c);
                        continue;
                    }
                    else if (c >= '0' && c <= '9')
                    {
                        if (state.IsAnyString() || state == State.Numeric)
                        {
                            buffer.Append(c);
                            continue;
                        }

                        state = State.Numeric;
                        buffer.Append(c);

                        continue;
                    }

                    break;

                case '@':
                    switch (state)
                    {
                        case State.None:
                            state = State.AtPrefixedString;
                            continue;
                        case State.AtPrefixedString:
                            state = State.DoubleAtPrefixedString;
                            continue;
                    }

                    buffer.Append(c);
                    continue;

                case '[':
                    state = State.BracketDelimitedString;
                    continue;
                case ']':
                    switch (state)
                    {
                        case State.BracketDelimitedString:
                            yield return new BracketDelimitedString(buffer);
                            state = State.None;
                            continue;
                    }

                    break;

                case '(':
                    if (state.IsQuotedString())
                        buffer.Append(c);
                    else
                        yield return new OpenParentheses();
                    continue;
                case ')':
                    if (state.IsQuotedString())
                        buffer.Append(c);
                    else
                    {
                        switch (state)
                        {
                            case State.UnquotedString:
                                yield return new UnquotedString(buffer);
                                break;
                            case State.AtPrefixedString:
                                yield return new AtPrefixedString(buffer);
                                break;
                        }
                        yield return new CloseParentheses();
                        state = State.None;
                    }
                    continue;

                case ';':
                    switch (state)
                    {
                        case State.UnquotedString:
                            yield return new UnquotedString(buffer);
                            state = State.None;
                            break;
                        case State.Numeric:
                            yield return new Numeric(buffer);
                            state = State.None;
                            break;
                    }
                    yield return new StatementTerminator();
                    continue;

                case ' ':
                case '\r':
                case '\n':
                case '\t':
                    switch (state)
                    {
                        case State.UnquotedString:
                            yield return new UnquotedString(buffer);
                            state = State.None;
                            break;
                        case State.Numeric:
                            yield return new Numeric(buffer);
                            state = State.None;
                            break;
                    }
                    continue;

                case '-':
                    c = commandEnumerator.GetNext(ref index);
                    switch (c)
                    {
                        case '-':
                            while (commandEnumerator.TryGetNext(out c, ref index))
                            {
                                switch (c)
                                {
                                    case '\r':
                                    case '\n':
                                        yield return new Comment();
                                        continue;
                                }
                            }
                            continue;
                    }
                    break;
            }

            throw new NotSupportedException($"Simulated command tokenizer doesn't know what to do with command text past character at index {index}, '{c}'.");
        }

        switch (state)
        {
            case State.AtPrefixedString:
                yield return new AtPrefixedString(buffer);
                break;
            case State.DoubleAtPrefixedString:
                yield return new DoubleAtPrefixedString(buffer);
                break;
            case State.Numeric:
                yield return new Numeric(buffer);
                break;
        }
    }

    static bool TryGetNext(this CharEnumerator enumerator, out char c, ref int index)
    {
        if (enumerator.MoveNext())
        {
            index++;
            c = enumerator.Current;
            return true;
        }

        c = default;
        return false;
    }

    static char GetNext(this CharEnumerator enumerator, ref int index)
    {
        if (!enumerator.TryGetNext(out var c, ref index))
            throw new SimulatedSqlException($"Simulated syntax error at index {index}.");

        return c;
    }

    static bool IsQuotedString(this State state) => state switch
    {
        State.BracketDelimitedString or State.SingleQuotedString or State.DoubleQuotedString => true,
        _ => false,
    };

    static bool IsAnyString(this State state) => state.IsQuotedString() || state switch
    {
        State.AtPrefixedString or State.DoubleAtPrefixedString => true,
        _ => false,
    };
}
