using System;
using System.Collections.Generic;

namespace SqlServerSimulator.Parser
{
    static class Tokenizer
    {
        public static IEnumerable<object> Tokenize(SimulatedDbConnection connection, Func<char?> getNextCharacter)
        {
            static bool TryGetNextCharacter(Func<char?> getNextCharacter, out char result)
            {
                var next = getNextCharacter();
                result = next.GetValueOrDefault();
                return next.HasValue;
            }

            while (TryGetNextCharacter(getNextCharacter, out var c))
            {
                yield return c;
            }
        }
    }
}
