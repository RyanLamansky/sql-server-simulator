namespace SqlServerSimulator.Parser.Tokens;

sealed class DoubleAtPrefixedString(string command, int index, int length) : StringToken(command, index, length)
{
    public override ReadOnlySpan<char> Span => Source[2..];

    /// <summary>
    /// Whether this names one of the built-in <c>@@</c> functions; any other
    /// <c>@@</c> name is a variable.
    /// </summary>
    public bool IsBuiltIn => TryParse(this.Span, out _);

    public AtAtKeyword Parse() => !TryParse(this.Span, out var result)
        ? throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{this.Span}`.")
        : result;

    /// <summary>
    /// The built-in <paramref name="source"/> spells, exactly as SQL Server
    /// does: the underscores of <c>@@LOCK_TIMEOUT</c> and its siblings, which
    /// the CamelCase entries leave out, are required where real writes them
    /// and refused elsewhere (probed 2026-10-16 against SQL Server 2025:
    /// <c>@@LOCKTIMEOUT</c> and <c>@@CPU__BUSY</c> are undeclared variables).
    /// </summary>
    private static bool TryParse(ReadOnlySpan<char> source, out AtAtKeyword keyword)
    {
        Span<char> stripped = stackalloc char[source.Length];
        var pos = 0;
        foreach (var c in source)
        {
            if (c != '_')
                stripped[pos++] = c;
        }
        if (!EnumNameLookup<AtAtKeyword>.TryParse(stripped[..pos], out keyword))
            return false;
        return UnderscoredSpelling(keyword) is { } spelled
            ? source.Equals(spelled, StringComparison.OrdinalIgnoreCase)
            : !source.Contains('_');
    }

    /// <summary>The spelling of a built-in whose name real writes with underscores, or null.</summary>
    private static string? UnderscoredSpelling(AtAtKeyword keyword) => keyword switch
    {
        AtAtKeyword.CpuBusy => "CPU_BUSY",
        AtAtKeyword.CursorRows => "CURSOR_ROWS",
        AtAtKeyword.FetchStatus => "FETCH_STATUS",
        AtAtKeyword.IoBusy => "IO_BUSY",
        AtAtKeyword.LockTimeout => "LOCK_TIMEOUT",
        AtAtKeyword.MaxConnections => "MAX_CONNECTIONS",
        AtAtKeyword.MaxPrecision => "MAX_PRECISION",
        AtAtKeyword.PackReceived => "PACK_RECEIVED",
        AtAtKeyword.PacketErrors => "PACKET_ERRORS",
        AtAtKeyword.PackSent => "PACK_SENT",
        AtAtKeyword.TotalErrors => "TOTAL_ERRORS",
        AtAtKeyword.TotalRead => "TOTAL_READ",
        AtAtKeyword.TotalWrite => "TOTAL_WRITE",
        _ => null,
    };
}

