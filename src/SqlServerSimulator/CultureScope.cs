using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// Sets the thread's <see cref="CultureInfo.CurrentCulture"/> and
/// <see cref="CultureInfo.CurrentUICulture"/> for one span of work and puts the
/// previous pair back on dispose, exceptions included:
/// <c>using var culture = CultureScope.Engine();</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine runs under the invariant culture.</b> A simulated server
/// answers the same on every host, but .NET formats and parses with the
/// current culture wherever no provider is passed — a negative integer in an
/// error message prints U+2212 under <c>fi-FI</c>, a <c>th-TH</c> date takes
/// a Buddhist year, <c>tr-TR</c> upper-cases <c>i</c> to <c>İ</c> — and an
/// analyzer can't find every such call. So each public member that runs engine
/// code opens <see cref="Engine"/> for its duration rather than each formatting
/// site naming a provider. A member that returns while engine work is still
/// pending (the data reader's lazily advanced outcome stream) opens it again
/// on each call that resumes the work.
/// </para>
/// <para>
/// The culture flows with the <see cref="ExecutionContext"/>, so a task, a
/// thread-pool item or a thread the engine starts inside the scope inherits it.
/// An <c>async</c> method sets it once instead (<see cref="SetEngine"/>):
/// the builder confines an <c>async</c> method's context changes to that
/// method and its continuations, so the caller never sees it.
/// </para>
/// <para>
/// User code running inside the engine gets its own culture back:
/// <see cref="Host"/> restores the consumer's around an event or callback the
/// engine raises, and <see cref="Clr"/> presents a SQLCLR routine with the
/// culture SQL Server's own CLR host does.
/// </para>
/// <para>
/// A scope whose target is already current changes nothing, so a nested entry
/// costs two reads. A switch is cheap rather than free (see
/// <see cref="engineFrom"/>), which is why the data reader enters around advancing
/// its outcome stream and building a schema table, not in its cell
/// accessors: the cells it
/// serves were produced before their result set was yielded, and the two
/// values it renders as text — <c>vector</c> and the spatial types' WKT —
/// format invariantly at the site.
/// </para>
/// </remarks>
internal readonly struct CultureScope : IDisposable
{
    /// <summary>
    /// The consumer's culture pair the outermost <see cref="Engine"/> scope on
    /// this thread switched away from, which <see cref="Host"/> restores;
    /// <see langword="null"/> outside one, or when the consumer was already
    /// invariant.
    /// </summary>
    [ThreadStatic]
    private static CultureInfo? hostCulture, hostUICulture;

    /// <summary>
    /// What a SQLCLR routine reads as its current culture and UI culture on
    /// SQL Server 2025: <c>en-US</c> whatever the session's <c>SET LANGUAGE</c>,
    /// the login's default language or the database's collation, with
    /// .NET Framework's Windows (NLS) data where .NET's ICU data differs — a
    /// plain space before the AM/PM designator, two decimal digits for the
    /// <c>N</c> and <c>P</c> formats, and a negative currency amount in
    /// parentheses. Every other <see cref="NumberFormatInfo"/> and
    /// <see cref="DateTimeFormatInfo"/> property already agrees (probed
    /// 2026-10-04 against SQL Server 2025 from a <c>SAFE</c> scalar function).
    /// </summary>
    private static readonly CultureInfo SqlClrCulture = CreateSqlClrCulture();

    /// <summary>
    /// The last switch each cached kind made on this thread: the execution
    /// context it started from and the one setting the culture produced. A
    /// switch from the same context again, which is every call after the first
    /// from an unchanging caller, swaps the cached context in with
    /// <see cref="ExecutionContext.Restore"/> instead of writing the cultures,
    /// and its dispose swaps the caller's back. A swap neither allocates nor
    /// changes what the caller sees, since an execution context never changes
    /// once captured, so it's half the cost of the writes (two AsyncLocal
    /// writes allocate a context each). The price is that each thread holds
    /// the last caller's context, and the AsyncLocal values in it, until its
    /// next switch.
    /// </summary>
    [ThreadStatic]
    private static ExecutionContext? engineFrom, engineTo, clrFrom, clrTo;

    private readonly CultureInfo? culture, uiCulture;
    private readonly ExecutionContext? from, to;
    private readonly bool recordedHost;

    private CultureScope(CultureInfo culture, CultureInfo uiCulture, ExecutionContext? from, ExecutionContext? to, bool recordHost)
    {
        this.culture = culture;
        this.uiCulture = uiCulture;
        this.from = from;
        this.to = to;
        if (recordHost && hostCulture is null)
        {
            this.recordedHost = true;
            hostCulture = culture;
            hostUICulture = uiCulture;
        }
    }

    /// <summary>
    /// Switches to <see cref="CultureInfo.InvariantCulture"/> for a public
    /// entry into the engine, remembering the consumer's culture for
    /// <see cref="Host"/>.
    /// </summary>
    public static CultureScope Engine()
    {
        var invariant = CultureInfo.InvariantCulture;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        if (ReferenceEquals(culture, invariant) && ReferenceEquals(uiCulture, invariant))
            return default;
        var to = Switch(invariant, invariant, ref engineFrom, ref engineTo, out var from);
        return new(culture, uiCulture, from, to, recordHost: true);
    }

    /// <summary>
    /// Switches to the culture SQL Server's CLR host presents (see
    /// <see cref="SqlClrCulture"/>) around a call into a registered assembly's
    /// code. A callback that code makes into the engine opens
    /// <see cref="Engine"/> again.
    /// </summary>
    public static CultureScope Clr()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        if (ReferenceEquals(culture, SqlClrCulture) && ReferenceEquals(uiCulture, SqlClrCulture))
            return default;
        var to = Switch(SqlClrCulture, SqlClrCulture, ref clrFrom, ref clrTo, out var from);
        return new(culture, uiCulture, from, to, recordHost: false);
    }

    /// <summary>
    /// Switches back to the consumer's culture around an event or callback the
    /// engine raises — <c>InfoMessage</c>, the <c>OpenBulkFile</c> delegate —
    /// so its handler runs as it would have outside the engine. A no-op where
    /// no <see cref="Engine"/> scope on this thread recorded one, as on the TDS
    /// endpoint, whose engine work no consumer call is waiting on.
    /// </summary>
    public static CultureScope Host()
    {
        if (hostCulture is not { } host)
            return default;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        if (ReferenceEquals(culture, host) && ReferenceEquals(uiCulture, hostUICulture))
            return default;
        CultureInfo.CurrentCulture = host;
        CultureInfo.CurrentUICulture = hostUICulture!;
        return new(culture, uiCulture, from: null, to: null, recordHost: false);
    }

    /// <summary>
    /// Sets the invariant culture with no scope to dispose, for a context that
    /// is the engine's alone: the TDS session loop, an <c>async</c> method that
    /// confines the change to itself (see the type's remarks), and an engine
    /// worker thread.
    /// </summary>
    public static void SetEngine()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    /// <summary>
    /// Makes <paramref name="target"/> current, through the cached swap when
    /// the thread's context is the one <paramref name="cachedFrom"/> names, and
    /// answers the context it left in place.
    /// </summary>
    private static ExecutionContext? Switch(CultureInfo target, CultureInfo uiTarget, ref ExecutionContext? cachedFrom, ref ExecutionContext? cachedTo, out ExecutionContext? from)
    {
        from = ExecutionContext.Capture();
        if (from is not null && ReferenceEquals(from, cachedFrom))
        {
            ExecutionContext.Restore(cachedTo!);
            return cachedTo;
        }

        CultureInfo.CurrentCulture = target;
        CultureInfo.CurrentUICulture = uiTarget;
        var to = ExecutionContext.Capture();
        if (from is not null && to is not null)
        {
            cachedFrom = from;
            cachedTo = to;
        }
        return to;
    }

    /// <summary>
    /// Restores the culture pair the scope switched away from — by swapping
    /// the caller's execution context back when nothing else changed the
    /// context inside the scope, and by writing the cultures otherwise.
    /// </summary>
    public void Dispose()
    {
        if (this.culture is null)
            return;
        if (this.recordedHost)
            hostCulture = hostUICulture = null;
        if (this.from is not null && ReferenceEquals(ExecutionContext.Capture(), this.to))
        {
            ExecutionContext.Restore(this.from);
            return;
        }
        if (!ReferenceEquals(CultureInfo.CurrentCulture, this.culture))
            CultureInfo.CurrentCulture = this.culture;
        if (!ReferenceEquals(CultureInfo.CurrentUICulture, this.uiCulture))
            CultureInfo.CurrentUICulture = this.uiCulture!;
    }

    private static CultureInfo CreateSqlClrCulture()
    {
        var culture = new CultureInfo("en-US", useUserOverride: true);
        culture.NumberFormat.CurrencyNegativePattern = 0;
        culture.NumberFormat.NumberDecimalDigits = 2;
        culture.NumberFormat.PercentDecimalDigits = 2;
        culture.DateTimeFormat.LongTimePattern = "h:mm:ss tt";
        culture.DateTimeFormat.ShortTimePattern = "h:mm tt";
        culture.DateTimeFormat.FullDateTimePattern = "dddd, MMMM d, yyyy h:mm:ss tt";
        return CultureInfo.ReadOnly(culture);
    }
}
