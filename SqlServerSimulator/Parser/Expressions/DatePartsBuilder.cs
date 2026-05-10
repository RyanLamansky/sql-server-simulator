using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Discriminator for the six <c>*FROMPARTS</c> builder functions. The kind
/// dictates argument count, argument layout (which slot is the scale arg,
/// if any), and result type construction.
/// </summary>
internal enum DatePartsBuilderKind
{
    /// <summary><c>DATEFROMPARTS(year, month, day)</c> → <c>date</c>.</summary>
    DateFromParts,

    /// <summary><c>DATETIMEFROMPARTS(year, month, day, hour, minute, seconds, milliseconds)</c> → <c>datetime</c>.</summary>
    DateTimeFromParts,

    /// <summary><c>DATETIME2FROMPARTS(year, month, day, hour, minute, seconds, fractions, precision)</c> → <c>datetime2(precision)</c>.</summary>
    DateTime2FromParts,

    /// <summary><c>DATETIMEOFFSETFROMPARTS(year, month, day, hour, minute, seconds, fractions, hour_offset, minute_offset, precision)</c> → <c>datetimeoffset(precision)</c>.</summary>
    DateTimeOffsetFromParts,

    /// <summary><c>SMALLDATETIMEFROMPARTS(year, month, day, hour, minute)</c> → <c>smalldatetime</c>.</summary>
    SmallDateTimeFromParts,

    /// <summary><c>TIMEFROMPARTS(hour, minute, seconds, fractions, precision)</c> → <c>time(precision)</c>.</summary>
    TimeFromParts,
}

/// <summary>
/// Backs the six SQL Server <c>*FROMPARTS</c> date/time builder scalars.
/// All six share the same NULL-propagation rule (NULL on any non-precision
/// argument → NULL result), the same out-of-range error path
/// (<see cref="SimulatedSqlException.CannotConstructFromParts"/> /
/// Msg 289 with type-specific State numbers), and the same int-coercion
/// rule for argument values (each non-precision arg coerces through
/// <see cref="SqlValue.CoerceTo"/>(<c>Int32</c>), so decimal / string /
/// bigint inputs work the same as ints — verified against SQL Server 2025).
/// </summary>
/// <remarks>
/// <para>The variable-precision targets — <c>datetime2</c>,
/// <c>datetimeoffset</c>, <c>time</c> — take a final <c>precision</c>
/// argument. SQL Server requires it to be a non-NULL integer constant
/// expression: NULL precision raises <c>Msg 10760</c>; out-of-range
/// precision (not in <c>[0, 7]</c>) raises <c>Msg 1002</c>. The simulator
/// captures the precision at parse time by extracting the <see cref="Value"/>
/// literal's constant; complex constant expressions (e.g. <c>3 + 4</c>) aren't
/// folded — the precision arg must be a numeric literal or
/// <c>NULL</c>. Real SQL Server's constant-folder is more permissive here.</para>
/// <para>EOMONTH lives in a separate file because its shape (1 or 2 args,
/// date input rather than int components) differs structurally.</para>
/// </remarks>
internal sealed class DatePartsBuilder : Expression
{
    private readonly DatePartsBuilderKind kind;
    private readonly Expression[] arguments;
    /// <summary>For datetime2/datetimeoffset/time: the precision captured at parse time. -1 for the other kinds.</summary>
    private readonly int parsedPrecision;
    private readonly SqlType resultType;

    public DatePartsBuilder(ParserContext context, DatePartsBuilderKind kind)
    {
        this.kind = kind;
        var expectedCount = ExpectedArgCount(kind);

        var args = new List<Expression> { Expression.Parse(context) };
        while (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            args.Add(Expression.Parse(context));
        }
        if (args.Count != expectedCount)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.arguments = [.. args];

        // Variable-precision targets: resolve the precision arg at parse time.
        // SQL Server requires "integer constants and integer constant
        // expressions" — model this by evaluating the parsed expression with a
        // column resolver that returns NULL (so column refs degrade to NULL),
        // producing constants for literals and constant arithmetic
        // (e.g. `-1` parses as `Subtract(Value(0), Value(1))` and folds to -1).
        // NULL or non-integer result → Msg 10760; out-of-[0,7] integer → Msg 1002.
        if (TryGetPrecisionSlot(kind, out var slotIndex))
        {
            SqlValue precisionConstant;
            try
            {
                precisionConstant = this.arguments[slotIndex].Run(new RuntimeContext(_ => SqlValue.Null(SqlType.Int32), context.Batch));
            }
            catch
            {
                throw SimulatedSqlException.ScaleArgumentNotValid(TargetTypeName(kind));
            }
            if (precisionConstant.IsNull || precisionConstant.Type.Category != SqlTypeCategory.Integer)
                throw SimulatedSqlException.ScaleArgumentNotValid(TargetTypeName(kind));
            var p = precisionConstant.CoerceTo(SqlType.Int32).AsInt32;
            if (p is < 0 or > 7)
                throw SimulatedSqlException.InvalidScale(p, line: 1);
            this.parsedPrecision = p;
            this.resultType = kind switch
            {
                DatePartsBuilderKind.DateTime2FromParts => SqlType.GetDateTime2(p),
                DatePartsBuilderKind.TimeFromParts => SqlType.GetTime(p),
                DatePartsBuilderKind.DateTimeOffsetFromParts => SqlType.GetDateTimeOffset(p),
                _ => throw new InvalidOperationException("Unreachable: only variable-precision kinds reach this branch."),
            };
        }
        else
        {
            this.parsedPrecision = -1;
            this.resultType = kind switch
            {
                DatePartsBuilderKind.DateFromParts => SqlType.Date,
                DatePartsBuilderKind.DateTimeFromParts => SqlType.DateTime,
                DatePartsBuilderKind.SmallDateTimeFromParts => SqlType.SmallDateTime,
                _ => throw new InvalidOperationException($"Unhandled non-precision kind {kind}."),
            };
        }
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.resultType;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var values = new SqlValue[this.arguments.Length];
        var precisionSlot = TryGetPrecisionSlot(this.kind, out var slot) ? slot : -1;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            // Skip the precision slot — already resolved at parse time and
            // its NULL-handling rules diverge from the value-NULL rules.
            if (i == precisionSlot)
                continue;
            values[i] = this.arguments[i].Run(runtime);
            if (values[i].IsNull)
                return SqlValue.Null(this.resultType);
        }

        // All non-precision args coerce to int via the existing CAST path.
        // Throws Msg 245 / 248 / 8115 if a string can't parse to int — same
        // wording as a direct CAST attempt, matching real SQL Server's
        // implicit conversion behavior.
        var ints = new int[this.arguments.Length];
        for (var i = 0; i < this.arguments.Length; i++)
        {
            if (i == precisionSlot)
                continue;
            ints[i] = values[i].CoerceTo(SqlType.Int32).AsInt32;
        }

        return this.kind switch
        {
            DatePartsBuilderKind.DateFromParts => BuildDate(ints[0], ints[1], ints[2]),
            DatePartsBuilderKind.DateTimeFromParts => BuildDateTime(ints[0], ints[1], ints[2], ints[3], ints[4], ints[5], ints[6]),
            DatePartsBuilderKind.DateTime2FromParts => BuildDateTime2(ints[0], ints[1], ints[2], ints[3], ints[4], ints[5], ints[6], this.parsedPrecision),
            DatePartsBuilderKind.DateTimeOffsetFromParts => BuildDateTimeOffset(ints[0], ints[1], ints[2], ints[3], ints[4], ints[5], ints[6], ints[7], ints[8], this.parsedPrecision),
            DatePartsBuilderKind.SmallDateTimeFromParts => BuildSmallDateTime(ints[0], ints[1], ints[2], ints[3], ints[4]),
            DatePartsBuilderKind.TimeFromParts => BuildTime(ints[0], ints[1], ints[2], ints[3], this.parsedPrecision),
            _ => throw new InvalidOperationException($"Unknown {nameof(DatePartsBuilderKind)} {this.kind}."),
        };
    }

    private static SqlValue BuildDate(int year, int month, int day) =>
        year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DaysInMonthClamped(year, month)
            ? throw SimulatedSqlException.CannotConstructFromParts("date", state: 1)
            : SqlValue.FromDate(new DateOnly(year, month, day));

    private static SqlValue BuildDateTime(int year, int month, int day, int hour, int minute, int second, int millisecond)
    {
        if (year is < 1753 or > 9999 || month is < 1 or > 12 || day < 1 || day > DaysInMonthClamped(year, month)
            || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59 || millisecond is < 0 or > 999)
        {
            throw SimulatedSqlException.CannotConstructFromParts("datetime", state: 3);
        }
        // Construct via the standard FromDateTime path — it applies the
        // legacy 1/300s rounding that yields the probe-observed ms-999
        // → next-day rollover.
        return SqlValue.FromDateTime(new DateTime(year, month, day, hour, minute, second, millisecond));
    }

    private static SqlValue BuildDateTime2(int year, int month, int day, int hour, int minute, int second, int fractions, int precision)
    {
        var maxFractions = Pow10(precision) - 1;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DaysInMonthClamped(year, month)
            || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59
            || fractions < 0 || fractions > maxFractions)
        {
            throw SimulatedSqlException.CannotConstructFromParts("datetime2", state: 5);
        }
        // Convert the precision-N integer fractions into 100-ns ticks. At
        // precision 7 each fraction unit is 1 tick; at lower precisions one
        // fraction unit equals 10^(7-N) ticks.
        var ticks = fractions * Pow10(7 - precision);
        var dt = new DateTime(year, month, day, hour, minute, second).AddTicks(ticks);
        return SqlValue.FromDateTime2(SqlType.GetDateTime2(precision), dt);
    }

    private static SqlValue BuildDateTimeOffset(int year, int month, int day, int hour, int minute, int second, int fractions, int hourOffset, int minuteOffset, int precision)
    {
        var maxFractions = Pow10(precision) - 1;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DaysInMonthClamped(year, month)
            || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59
            || fractions < 0 || fractions > maxFractions)
        {
            throw SimulatedSqlException.CannotConstructFromParts("datetimeoffset", state: 6);
        }
        // Offset rules (probe-confirmed): hour_offset and minute_offset must
        // share a sign (or one is zero); combined absolute offset must be ≤
        // 14:00. Real SQL Server raises Msg 289 with State 6 for any of these.
        if (Math.Abs(hourOffset) > 14 || Math.Abs(minuteOffset) > 59
            || (hourOffset > 0 && minuteOffset < 0) || (hourOffset < 0 && minuteOffset > 0)
            || (Math.Abs(hourOffset) == 14 && minuteOffset != 0))
        {
            throw SimulatedSqlException.CannotConstructFromParts("datetimeoffset", state: 6);
        }
        var totalOffsetMinutes = (hourOffset * 60) + (hourOffset < 0 ? -minuteOffset : minuteOffset);
        var ticks = fractions * Pow10(7 - precision);
        var local = new DateTime(year, month, day, hour, minute, second).AddTicks(ticks);
        var dto = new DateTimeOffset(local, TimeSpan.FromMinutes(totalOffsetMinutes));
        return SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(precision), dto);
    }

    private static SqlValue BuildSmallDateTime(int year, int month, int day, int hour, int minute)
    {
        if (year is < 1900 or > 2079 || month is < 1 or > 12 || day < 1 || day > DaysInMonthClamped(year, month)
            || hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            // Real SQL Server reuses State 3 for smalldatetime; not separately
            // probed, but smalldatetime traces follow datetime's State by
            // convention. Accept State 3 to avoid an over-claim.
            throw SimulatedSqlException.CannotConstructFromParts("smalldatetime", state: 3);
        }
        return SqlValue.FromSmallDateTime(new DateTime(year, month, day, hour, minute, 0));
    }

    private static SqlValue BuildTime(int hour, int minute, int second, int fractions, int precision)
    {
        var maxFractions = Pow10(precision) - 1;
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59
            || fractions < 0 || fractions > maxFractions)
        {
            throw SimulatedSqlException.CannotConstructFromParts("time", state: 2);
        }
        var ticks = fractions * Pow10(7 - precision);
        var span = new TimeSpan(0, hour, minute, second).Add(TimeSpan.FromTicks(ticks));
        return SqlValue.FromTime(SqlType.GetTime(precision), span);
    }

    private static int DaysInMonthClamped(int year, int month) =>
        DateTime.DaysInMonth(Math.Max(1, Math.Min(9999, year)), Math.Max(1, Math.Min(12, month)));

    private static long Pow10(int n)
    {
        long r = 1;
        for (var i = 0; i < n; i++)
            r *= 10;
        return r;
    }

    private static int ExpectedArgCount(DatePartsBuilderKind kind) => kind switch
    {
        DatePartsBuilderKind.DateFromParts => 3,
        DatePartsBuilderKind.DateTimeFromParts => 7,
        DatePartsBuilderKind.DateTime2FromParts => 8,
        DatePartsBuilderKind.DateTimeOffsetFromParts => 10,
        DatePartsBuilderKind.SmallDateTimeFromParts => 5,
        DatePartsBuilderKind.TimeFromParts => 5,
        _ => throw new InvalidOperationException($"Unknown {nameof(DatePartsBuilderKind)} {kind}."),
    };

    private static bool TryGetPrecisionSlot(DatePartsBuilderKind kind, out int slot)
    {
        slot = kind switch
        {
            DatePartsBuilderKind.DateTime2FromParts => 7,
            DatePartsBuilderKind.DateTimeOffsetFromParts => 9,
            DatePartsBuilderKind.TimeFromParts => 4,
            _ => -1,
        };
        return slot >= 0;
    }

    private static string TargetTypeName(DatePartsBuilderKind kind) => kind switch
    {
        DatePartsBuilderKind.DateTime2FromParts => "datetime2",
        DatePartsBuilderKind.TimeFromParts => "time",
        DatePartsBuilderKind.DateTimeOffsetFromParts => "datetimeoffset",
        _ => throw new InvalidOperationException($"{kind} has no scale argument."),
    };

    internal override string DebugDisplay()
    {
        var name = this.kind switch
        {
            DatePartsBuilderKind.DateFromParts => "DATEFROMPARTS",
            DatePartsBuilderKind.DateTimeFromParts => "DATETIMEFROMPARTS",
            DatePartsBuilderKind.DateTime2FromParts => "DATETIME2FROMPARTS",
            DatePartsBuilderKind.DateTimeOffsetFromParts => "DATETIMEOFFSETFROMPARTS",
            DatePartsBuilderKind.SmallDateTimeFromParts => "SMALLDATETIMEFROMPARTS",
            DatePartsBuilderKind.TimeFromParts => "TIMEFROMPARTS",
            _ => this.kind.ToString(),
        };
        return $"{name}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
    }
}
