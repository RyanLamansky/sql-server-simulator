using System.Collections.Frozen;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Organizes relevant information for parsing of SQL commands.
/// </summary>
internal sealed class ParserContext : IDisposable
{
    public readonly SimulatedDbCommand Command;
#if DEBUG
    private readonly TokenArrayEnumerator tokens;
#else
    private readonly IEnumerator<Token> tokens;
#endif
    public Token? Token;
    private readonly FrozenDictionary<string, (string Name, (DataType type, object? Value) TypeValue)> variables;

    public ParserContext(SimulatedDbCommand command)
    {
        this.Command = command;

        this.variables = command
            .Parameters
            .Cast<DbParameter>()
            .Select(parameter =>
            {
                var name = parameter.ParameterName;
                var type = DataType.GetByDbType(parameter.DbType);
                return (Name: name.StartsWith('@') ? name[1..] : name, TypeValue: (DataType: type, Value: parameter.Value is null ? null : type.ConvertFrom(parameter.Value)));
            })
            .ToFrozenDictionary(tuple => tuple.Name, StringComparer.InvariantCultureIgnoreCase);

#if DEBUG
        this.tokens = new TokenArrayEnumerator(command.CommandText);
#else
        this.tokens = Tokenizer.Tokenize(command.CommandText).GetEnumerator();
#endif
    }

    public Simulation Simulation => Command.simulation;

    /// <summary>
    /// Gets the value of the variable with the provided <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <returns>The variable's value.</returns>
    /// <exception cref="SimulatedSqlException">Must declare the scalar variable \"@{value of <paramref name="name"/>}\".</exception>
    public object? GetVariableValue(string name) =>
        variables.TryGetValue(name, out var value)
        ? value.TypeValue.Value
        : throw new SimulatedSqlException($"Must declare the scalar variable \"@{name}\".");


    /// <summary>
    /// Moves to the next item in an enumeration (if possible) and transfers <see cref="IEnumerator{T}.Current"/> to <paramref name="current"/>.
    /// </summary>
    /// <param name="current">Receives the <see cref="IEnumerator{T}.Current"/> value, or the types default if advancement isn't possible.</param>
    /// <returns>True if the enumerator was advanced, otherwise false.</returns>
    public bool TryMoveNext([NotNullWhen(true)] out Token? current)
    {
        var enumerator = this.tokens;
        bool moved;
        current = (moved = enumerator.MoveNext()) ? enumerator.Current : default;
        return moved;
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead.
    /// </summary>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public Token RequireNext()
    {
        var enumerator = this.tokens;
        Debug.Assert(enumerator.Current is not null);
        var previous = enumerator.Current;
        return enumerator.MoveNext() ? enumerator.Current : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead or the token is the wrong type.
    /// </summary>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public T RequireNext<T>()
        where T : Token
    {
        var enumerator = this.tokens;
        Debug.Assert(enumerator.Current is not null);
        var previous = enumerator.Current;
        return enumerator.MoveNext() && enumerator.Current is T current ? current : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    private bool isDisposed;

    private void Dispose(bool disposing)
    {
        if (isDisposed)
            return;

        if (disposing)
        {
            this.tokens.Dispose();
            this.Token = null;
        }

        isDisposed = true;
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(true);
        GC.SuppressFinalize(this);
    }

#if DEBUG
    public override string? ToString() => this.tokens.ToString();

    /// <summary>
    /// Simplifies parser debugging by providing a useful string representation of the token enumeration.
    /// Specifically, when calling <see cref="ToString"/> tokens are separated by '·' and the <see cref="Current"/> token is wrapped by '»' and '«'.
    /// </summary>
    /// <param name="command">The SQL command to process.</param>
    /// <remarks>This should only be included in debug builds because it reduces parsing efficiency.</remarks>
    private sealed class TokenArrayEnumerator(string? command) : IEnumerator<Token>
    {
        /// <summary>
        /// Retains the full results of <see cref="Tokenizer.Tokenize(string?)"/>.
        /// This is less efficient than streaming the results, but enables this class's debugging-friendly <see cref="ToString"/>.
        /// </summary>
        private readonly Token[] source = [.. Tokenizer.Tokenize(command)];

        public int Index { get; private set; } = -1;

        public Token Current => source[Index];

        object System.Collections.IEnumerator.Current => Current;

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
            var newIndex = checked(Index + 1);
            if (newIndex >= source.Length)
                return false;

            Index = newIndex;
            return true;
        }

        public void Reset() => Index = -1;

        /// <summary>
        /// Returns a string representation of the tokenized command.
        /// Tokens are separated by '·' and the <see cref="Current"/> token is wrapped by '»' and '«'.
        /// </summary>
        /// <returns>The string representation.</returns>
        public override string ToString()
        {
            var result = new System.Text.StringBuilder();
            var source = this.source;

            for (var i = 0; i < source.Length; i++)
            {
                var token = source[i];
                if (i != Index)
                {
                    _ = result.Append(token).Append('·');
                    continue;
                }

                _ = result.Append('»').Append(token).Append('«').Append('·');
            }

            return result.ToString(0, result.Length - 1);

        }
    }
#endif
}
