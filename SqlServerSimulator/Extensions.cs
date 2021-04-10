using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator
{
    static class Extensions
    {
        public static bool TryMoveNext<T>(this IEnumerator<T> enumerator, [NotNullWhen(true)] out T? current)
        {
            bool moved;
            current = (moved = enumerator.MoveNext()) ? enumerator.Current : default;
            return moved;
        }

        public static T RequireNext<T>(this IEnumerator<T> enumerator, string? exceptionMessage = null)
        {
            if (!enumerator.MoveNext())
                throw new SimulatedSqlException(exceptionMessage);

            return enumerator.Current;
        }
    }
}
