namespace SqlServerSimulator.Storage;

/// <summary>
/// The <c>two digit year cutoff</c> server option: the last year a one- or
/// two-digit year written in a date string reads as, each such year landing
/// in the hundred years ending there — 2049 by default, so <c>49</c> is 2049
/// and <c>50</c> is 1950 (probed 2026-10-17 against SQL Server 2025). One
/// instance per installed value, so the ambient <see cref="Current"/>
/// republishes an unchanged one for free.
/// </summary>
internal sealed class TwoDigitYearCutoff(int lastYear)
{
    /// <summary>The stock cutoff, 2049.</summary>
    public static readonly TwoDigitYearCutoff Default = new(2049);

    /// <summary>The last year a short year reads as, 1753 to 9999.</summary>
    public readonly int LastYear = lastYear;

    /// <summary>The last instance <see cref="For"/> made, which it hands out again while the value stands.</summary>
    private static TwoDigitYearCutoff latest = Default;

    /// <summary>The cutoff ending at <paramref name="lastYear"/>, the same instance while the value stands.</summary>
    public static TwoDigitYearCutoff For(int lastYear)
    {
        var cutoff = Volatile.Read(ref latest);
        if (cutoff.LastYear != lastYear)
            Volatile.Write(ref latest, cutoff = lastYear == Default.LastYear ? Default : new TwoDigitYearCutoff(lastYear));
        return cutoff;
    }

    /// <summary>The year a written year of one or two digits, <paramref name="value"/>, reads as.</summary>
    public int Expand(int value) => this.LastYear - ((((this.LastYear - value) % 100) + 100) % 100);

    private static readonly AsyncLocal<TwoDigitYearCutoff?> current = new();

    /// <summary>
    /// The cutoff of the request whose statement is running, published by the
    /// dispatch loop beside <see cref="DateOrder.Current"/> because the string
    /// → date-time conversion it steers has no session to ask. Unset, it is
    /// <see cref="Default"/>.
    /// </summary>
    public static TwoDigitYearCutoff Current
    {
        get => current.Value ?? Default;
        set => current.Value = value;
    }
}
