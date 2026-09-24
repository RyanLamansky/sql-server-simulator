namespace SqlServerSimulator.Storage;

/// <summary>
/// A <c>SET DATEFORMAT</c> order: how a numeric date string's three parts map
/// to year, month and day. One shared instance per order, so the ambient
/// <see cref="Current"/> compares by reference and republishing an unchanged
/// order costs nothing.
/// </summary>
internal sealed class DateOrder
{
    public static readonly DateOrder Mdy = new("mdy", yearPosition: 2, monthBeforeDay: true);
    public static readonly DateOrder Dmy = new("dmy", yearPosition: 2, monthBeforeDay: false);
    public static readonly DateOrder Ymd = new("ymd", yearPosition: 0, monthBeforeDay: true);
    public static readonly DateOrder Ydm = new("ydm", yearPosition: 0, monthBeforeDay: false);
    public static readonly DateOrder Myd = new("myd", yearPosition: 1, monthBeforeDay: true);
    public static readonly DateOrder Dym = new("dym", yearPosition: 1, monthBeforeDay: false);

    /// <summary>The order's name as <c>sys.dm_exec_sessions.date_format</c> and <c>DBCC USEROPTIONS</c> report it.</summary>
    public readonly string Name;

    /// <summary>Which of the three parts, 0 to 2, the order puts the year in.</summary>
    public readonly int YearPosition;

    /// <summary>Whether the month comes before the day among the other two parts.</summary>
    public readonly bool MonthBeforeDay;

    private DateOrder(string name, int yearPosition, bool monthBeforeDay)
    {
        this.Name = name;
        this.YearPosition = yearPosition;
        this.MonthBeforeDay = monthBeforeDay;
    }

    /// <summary>The order <paramref name="name"/> names, case aside, or null.</summary>
    public static DateOrder? Find(ReadOnlySpan<char> name)
    {
        Span<char> folded = stackalloc char[3];
        if (name.Length != 3 || name.ToUpperInvariant(folded) != 3)
            return null;
        return folded switch
        {
            "DMY" => Dmy,
            "DYM" => Dym,
            "MDY" => Mdy,
            "MYD" => Myd,
            "YDM" => Ydm,
            "YMD" => Ymd,
            _ => null,
        };
    }

    private static readonly AsyncLocal<DateOrder?> current = new();

    /// <summary>
    /// The order of the session whose statement is running, published by the
    /// dispatch loop for each statement because the string → date-time
    /// conversion it steers runs inside <see cref="SqlValue.CoerceTo"/>, which
    /// has no session to ask. An <see cref="AsyncLocal{T}"/> rather than a
    /// thread-static so it flows into the parallel grouped accumulation's
    /// tasks. Unset, it is us_english's <c>mdy</c>.
    /// </summary>
    public static DateOrder Current
    {
        get => current.Value ?? Mdy;
        set => current.Value = value;
    }
}
