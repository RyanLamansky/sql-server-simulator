using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// <c>GENERATE_SERIES(start, stop [, step])</c> rowset-returning function
/// support. Built-in TVF introduced in SQL Server 2022 / Azure SQL DB;
/// modeled as a sibling of <see cref="ParseStringSplit"/> so the rest of the
/// FROM-source machinery (alias / qualifier / lateral re-execution per outer
/// row, JOIN / APPLY composition) reuses the existing derived-table codepath.
/// </summary>
/// <remarks>
/// Probe-confirmed schema and behavior (SQL Server 2025, 2026-05-23):
/// <list type="bullet">
/// <item><description>Output column is always named <c>value</c>; its declared
/// type tracks the input arg type (<c>tinyint</c> → <c>tinyint</c>,
/// <c>int</c> → <c>int</c>, <c>bigint</c> → <c>bigint</c>,
/// <c>decimal(p,s)</c> → <c>decimal</c> with unified scale).</description></item>
/// <item><description>Allowed arg types: <c>tinyint</c>, <c>smallint</c>,
/// <c>int</c>, <c>bigint</c>, <c>decimal</c> / <c>numeric</c>. Anything else
/// (<c>float</c>, <c>real</c>, <c>money</c>, <c>varchar</c>, <c>date</c>, …)
/// raises <strong>Msg 8116</strong>.</description></item>
/// <item><description>All three args must share the same type. Integer
/// subtypes are distinct (<c>int</c> + <c>bigint</c> mismatches);
/// <c>decimal</c> / <c>numeric</c> collapse to one family and tolerate
/// differing precision / scale (unified via <see cref="SqlType.Promote"/>).
/// Mismatched types raise <strong>Msg 5373</strong>.</description></item>
/// <item><description>NULL on any arg → empty rowset (no error).</description></item>
/// <item><description>Step omitted: defaults to <c>-1</c> when
/// <c>start &gt; stop</c>, else <c>1</c>, so <c>GENERATE_SERIES(5, 1)</c>
/// yields the descending sequence.</description></item>
/// <item><description>Step direction wrong relative to start / stop → empty
/// rowset (no error). Step = 0 → <strong>Msg 4199</strong>.</description></item>
/// <item><description>Fewer than 2 args → <strong>Msg 313</strong>; more than
/// 3 → <strong>Msg 8144</strong>.</description></item>
/// </list>
/// </remarks>
internal sealed partial class Selection
{
    private static Selection FromGenerateSeries(Expression startExpr, Expression stopExpr, Expression? stepExpr, SqlType outputType)
    {
        SqlType[] schema = [outputType];
        string[] columnNames = ["value"];

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateGenerateSeriesRows(startExpr, stopExpr, stepExpr, outputType, schema, batch, outerResolver));
    }

    private static IEnumerable<byte[]> EnumerateGenerateSeriesRows(
        Expression startExpr,
        Expression stopExpr,
        Expression? stepExpr,
        SqlType outputType,
        SqlType[] schema,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);

        var startVal = startExpr.Run(runtime);
        var stopVal = stopExpr.Run(runtime);
        var stepValProvided = stepExpr is not null;
        var stepVal = stepExpr?.Run(runtime) ?? SqlValue.Null(SqlType.BigInt);

        // Probe-confirmed: any NULL arg → empty rowset, no error. The schema is
        // already locked in at parse time; we just yield nothing.
        if (startVal.IsNull || stopVal.IsNull || (stepValProvided && stepVal.IsNull))
            yield break;

        if (outputType.Category == SqlTypeCategory.Integer)
        {
            var start = startVal.CoerceTo(SqlType.BigInt).AsInt64;
            var stop = stopVal.CoerceTo(SqlType.BigInt).AsInt64;
            long step;
            if (!stepValProvided)
            {
                step = start > stop ? -1L : 1L;
            }
            else
            {
                step = stepVal.CoerceTo(SqlType.BigInt).AsInt64;
                if (step == 0)
                    throw SimulatedSqlException.GenerateSeriesStepZero();
            }

            var cur = start;
            if (step > 0)
            {
                while (cur <= stop)
                {
                    yield return RowEncoder.EncodeRow(schema, [SqlValue.FromInt64(cur).CoerceTo(outputType)]);
                    if (cur > long.MaxValue - step)
                        yield break;
                    cur += step;
                }
            }
            else
            {
                while (cur >= stop)
                {
                    yield return RowEncoder.EncodeRow(schema, [SqlValue.FromInt64(cur).CoerceTo(outputType)]);
                    if (cur < long.MinValue - step)
                        yield break;
                    cur += step;
                }
            }
        }
        else
        {
            var declared = (DecimalSqlType)outputType;
            var start = startVal.AsDecimal38;
            var stop = stopVal.AsDecimal38;
            Decimal38 step;
            if (!stepValProvided)
            {
                step = start > stop ? Decimal38.One.Negate() : Decimal38.One;
            }
            else
            {
                step = stepVal.AsDecimal38;
                if (step.IsZero)
                    throw SimulatedSqlException.GenerateSeriesStepZero();
            }

            var cur = start;
            var ascending = step.Sign > 0;
            while (ascending ? cur <= stop : cur >= stop)
            {
                yield return RowEncoder.EncodeRow(schema, [SqlValue.FromDecimal(outputType, cur)]);
                if (!Decimal38.TryAdd(cur, step, declared.precision, declared.scale, out cur))
                    throw SimulatedSqlException.ArithmeticOverflow("numeric");
            }
        }
    }

    /// <summary>
    /// Parses a <c>GENERATE_SERIES(start, stop [, step])</c> source from
    /// <paramref name="context"/>. Enters with <see cref="ParserContext.Token"/>
    /// on the <c>GENERATE_SERIES</c> name; on return the cursor sits on the
    /// first token past the closing <c>)</c>.
    /// </summary>
    public static Selection ParseGenerateSeries(ParserContext context, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        if (context.Token is Operator { Character: ')' })
        {
            // Zero-arg form. Real SQL Server raises Msg 313 even though
            // GENERATE_SERIES is a TVF, not a stored proc — wording probe-
            // confirmed.
            throw SimulatedSqlException.InsufficientArgumentsToFunction("GENERATE_SERIES");
        }
        var startExpr = Expression.Parse(context);

        if (context.Token is Operator { Character: ')' })
        {
            // One-arg form → Msg 313 (same wording as zero-arg).
            throw SimulatedSqlException.InsufficientArgumentsToFunction("GENERATE_SERIES");
        }

        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var stopExpr = Expression.Parse(context);

        Expression? stepExpr = null;
        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            stepExpr = Expression.Parse(context);

            if (context.Token is Operator { Character: ',' })
            {
                // 4th-and-beyond args → Msg 8144.
                throw SimulatedSqlException.TooManyArgumentsToFunction("GENERATE_SERIES");
            }
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        // Resolve declared types. The outer-type resolver is for the FROM-source's
        // enclosing scope (lateral correlation). Pass a default resolver so bare
        // refs in the unlikely correlated case have something to chew on.
        var typeResolver = outerTypeResolver ?? (_ => SqlType.Int32);
        var startType = startExpr.GetSqlType(context.Batch, typeResolver);
        var stopType = stopExpr.GetSqlType(context.Batch, typeResolver);
        var stepType = stepExpr?.GetSqlType(context.Batch, typeResolver);

        // Argument-type validation: each arg must be one of the supported types.
        // Msg 8116 fires first if any arg has an unsupported type, before the
        // same-type check.
        ValidateArgType(startType, 1);
        ValidateArgType(stopType, 2);
        if (stepType is not null)
            ValidateArgType(stepType, 3);

        // Same-type check: integer subtypes must match exactly; decimal /
        // numeric (both <see cref="SqlTypeCategory.Decimal"/>) collapse to one
        // family. Probe: <c>int + bigint</c> raises Msg 5373; <c>decimal(10,1)
        // + decimal(10,2)</c> is fine.
        if (!SameFamily(startType, stopType) || (stepType is not null && !SameFamily(startType, stepType)))
            throw SimulatedSqlException.GenerateSeriesArgsMustShareType();

        // Output type: integer args preserve the exact subtype; decimal args
        // promote so the unified scale matches real SQL Server (DECIMAL(10,1) +
        // DECIMAL(10,2) projects DECIMAL(11,2) via SqlType.Promote).
        SqlType outputType;
        if (startType.Category == SqlTypeCategory.Integer)
        {
            outputType = startType;
        }
        else
        {
            outputType = SqlType.Promote(startType, stopType);
            if (stepType is not null)
                outputType = SqlType.Promote(outputType, stepType);
        }

        return FromGenerateSeries(startExpr, stopExpr, stepExpr, outputType);
    }

    private static void ValidateArgType(SqlType type, int argumentIndex)
    {
        if (type.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal)
            return;
        throw SimulatedSqlException.InvalidArgumentDataType(type.SqlServerName, argumentIndex, "generate_series");
    }

    private static bool SameFamily(SqlType a, SqlType b) =>
        a.Category == b.Category && (a.Category == SqlTypeCategory.Decimal || a == b);
}
