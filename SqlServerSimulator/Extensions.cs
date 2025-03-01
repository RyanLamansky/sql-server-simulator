using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

static class Extensions
{
    public static bool TryMoveNext<T>(this IEnumerator<T> enumerator, [NotNullWhen(true)] out T? current)
    {
        bool moved;
        current = (moved = enumerator.MoveNext()) ? enumerator.Current : default;
        return moved;
    }

    public static Parser.Token RequireNext(this IEnumerator<Parser.Token> enumerator, Simulation simulation)
    {
        Debug.Assert(enumerator.Current is not null);
        var previous = enumerator.Current!;
        return enumerator.MoveNext() ? enumerator.Current : throw simulation.SyntaxErrorNear(previous);
    }

    public static T RequireNext<T>(this IEnumerator<Parser.Token> enumerator, Simulation simulation)
        where T : Parser.Token
    {
        Debug.Assert(enumerator.Current is not null);
        var previous = enumerator.Current!;

        if (enumerator.MoveNext())
        {
            var current = enumerator.Current as T;
            if (current is not null)
                return current;
        }

        throw simulation.SyntaxErrorNear(previous);
    }
}
