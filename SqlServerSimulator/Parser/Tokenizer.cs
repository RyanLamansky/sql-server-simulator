using System;
using System.Collections.Generic;

namespace SqlServerSimulator.Parser
{
    using Tokens;

    static class Tokenizer
    {
        public static IEnumerable<Token> Tokenize(CharEnumerator commandEnumerator)
        {
            var index = -1;
            while (commandEnumerator.TryGetNext(out var c, ref index))
            {
                switch (c)
                {
                    case ' ':
                    case '\r':
                    case '\n':
                    case '\t':
                        yield return new Whitespace();
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
    }
}
