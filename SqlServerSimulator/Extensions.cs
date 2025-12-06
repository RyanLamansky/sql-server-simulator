using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

static class Extensions
{
    /// <summary>
    /// Moves to the next item in an enumeration (if possible) and transfers <see cref="IEnumerator{T}.Current"/> to <paramref name="current"/>.
    /// </summary>
    /// <typeparam name="T">The type of enumeration.</typeparam>
    /// <param name="enumerator">The enumerator to advance.</param>
    /// <param name="current">Receives the <see cref="IEnumerator{T}.Current"/> value, or the types default if advancement isn't possible.</param>
    /// <returns>True if the enumerator was advanced, otherwise false.</returns>
    public static bool TryMoveNext<T>(this IEnumerator<T> enumerator, [NotNullWhen(true)] out T? current)
    {
        bool moved;
        current = (moved = enumerator.MoveNext()) ? enumerator.Current : default;
        return moved;
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead.
    /// </summary>
    /// <param name="enumerator">The enumerator to advance.</param>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public static Parser.Token RequireNext(this IEnumerator<Parser.Token> enumerator)
    {
        Debug.Assert(enumerator.Current is not null);
        var previous = enumerator.Current;
        return enumerator.MoveNext() ? enumerator.Current : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead or the token is the wrong type.
    /// </summary>
    /// <param name="enumerator">The enumerator to advance.</param>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public static T RequireNext<T>(this IEnumerator<Parser.Token> enumerator)
        where T : Parser.Token
    {
        Debug.Assert(enumerator.Current is not null);
        var previous = enumerator.Current;
        return enumerator.MoveNext() && enumerator.Current is T current ? current : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }
}
