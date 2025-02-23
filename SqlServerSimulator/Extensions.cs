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

    public static T RequireNext<T>(this IEnumerator<T> enumerator)
        => enumerator.RequireNext(null);

    public static T RequireNext<T>(this IEnumerator<T> enumerator, string? exceptionMessage = null) => !enumerator.MoveNext()
        ? throw new SimulatedSqlException(exceptionMessage ?? "Simulated command processor expected the command to be longer.")
        : enumerator.Current;
}
