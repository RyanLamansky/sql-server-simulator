namespace SqlServerSimulator.Parser.Tokens;

sealed class DoubleAtPrefixedString(string command, int index, int length) : StringToken(command, index, length)
{
    public override ReadOnlySpan<char> Span => Source[2..];

    public AtAtKeyword Parse()
    {
        // Strip underscores so SQL Server's `@@LOCK_TIMEOUT` /
        // `@@MAX_CONNECTIONS` / `@@MAX_PRECISION` map onto the
        // CamelCase enum entries (LockTimeout / MaxConnections /
        // MaxPrecision). Other entries (@@SPID / @@VERSION / @@ROWCOUNT)
        // have no underscore so this is a no-op for them.
        var source = this.Span;
        Span<char> stripped = stackalloc char[source.Length];
        var pos = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != '_')
                stripped[pos++] = source[i];
        }
        return !Enum.TryParse<AtAtKeyword>(stripped[..pos], true, out var result)
            ? throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{this.Span}`.")
            : result;
    }
}

