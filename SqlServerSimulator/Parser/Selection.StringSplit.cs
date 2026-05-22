using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// <c>STRING_SPLIT(input, separator [, enable_ordinal])</c> rowset-returning
/// function support. Implemented as a <see cref="Selection"/> factory in the
/// same shape as <c>OPENJSON</c> so the rest of the FROM-source machinery
/// (alias / qualifier / lateral re-execution per outer row, JOIN / APPLY
/// composition) reuses the existing derived-table codepath.
/// </summary>
/// <remarks>
/// Probe-confirmed schema (SQL Server 2025):
/// <list type="bullet">
/// <item><description>2-arg form: <c>(value <i>input_string_type</i>)</c>.</description></item>
/// <item><description>3-arg form with <c>enable_ordinal = 1</c>: <c>(value <i>input_string_type</i>, ordinal bigint)</c>.</description></item>
/// <item><description>3-arg form with <c>enable_ordinal IN (0, NULL)</c>: schema collapses to the 2-arg form.</description></item>
/// <item><description>The third argument must be a parse-time constant on real SQL Server (the schema is shape-fixed at compile time); the simulator enforces this by evaluating the third arg against an empty resolver and surfacing <see cref="NotSupportedException"/> on column / parameter references.</description></item>
/// </list>
/// Runtime errors:
/// <list type="bullet">
/// <item><description>NULL / empty / multi-character separator → <strong>Msg 214</strong>.</description></item>
/// <item><description>Non-int third-arg type → <strong>Msg 8116</strong>.</description></item>
/// <item><description>Third-arg value outside {0, 1, NULL} → <strong>Msg 4199</strong>.</description></item>
/// </list>
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// Builds a <see cref="Selection"/> for <c>STRING_SPLIT(...)</c>. Captures
    /// the type-of-input decision at parse time so the projected schema is
    /// stable for downstream column resolution.
    /// </summary>
    private static Selection FromStringSplit(Expression input, Expression separator, bool emitOrdinal, SqlType valueColumnType)
    {
        SqlType[] schema = emitOrdinal ? [valueColumnType, SqlType.BigInt] : [valueColumnType];
        string[] columnNames = emitOrdinal ? ["value", "ordinal"] : ["value"];

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateStringSplitRows(input, separator, schema, emitOrdinal, valueColumnType, batch, outerResolver));
    }

    private static IEnumerable<byte[]> EnumerateStringSplitRows(
        Expression input,
        Expression separator,
        SqlType[] schema,
        bool emitOrdinal,
        SqlType valueColumnType,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);

        // Separator argument: validated before checking the input — NULL /
        // empty / multi-char separator surfaces Msg 214 regardless of whether
        // the input is NULL. Real SQL Server has the same ordering (probe:
        // NULL sep raises 214 even when the string is also NULL).
        var sepValue = separator.Run(runtime);
        if (sepValue.IsNull
            || !SqlType.IsStringCategory(sepValue.Type)
            || sepValue.AsString.Length != 1)
        {
            throw SimulatedSqlException.StringSplitSeparatorMustBeSingleChar();
        }
        var sepChar = sepValue.AsString[0];

        var inputValue = input.Run(runtime);
        if (inputValue.IsNull)
            yield break;
        if (!SqlType.IsStringCategory(inputValue.Type))
        {
            // Non-string input is implicitly coerced by SQL Server to the
            // declared string family of the parse-time-resolved value column.
            inputValue = inputValue.CoerceTo(valueColumnType);
        }
        var inputString = inputValue.AsString;

        // Empty-input behavior: probe shows STRING_SPLIT('', ',') returns one
        // row with an empty value, AND when enable_ordinal=1 the row's
        // ordinal is 1. That falls out naturally from the split-and-emit
        // loop below, since `''.Split(',')` yields a single empty element.
        var ordinal = 1L;
        var start = 0;
        for (var i = 0; i <= inputString.Length; i++)
        {
            if (i == inputString.Length || inputString[i] == sepChar)
            {
                var segment = inputString[start..i];
                SqlValue[] values = emitOrdinal
                    ? [SqlValue.FromString(valueColumnType, segment), SqlValue.FromInt64(ordinal)]
                    : [SqlValue.FromString(valueColumnType, segment)];
                yield return RowEncoder.EncodeRow(schema, values);
                ordinal++;
                start = i + 1;
            }
        }
    }

    /// <summary>
    /// Parses a <c>STRING_SPLIT(string, separator [, enable_ordinal])</c>
    /// source from <paramref name="context"/>. Enters with
    /// <see cref="ParserContext.Token"/> on the <c>STRING_SPLIT</c> name; on
    /// return the cursor sits on the first token past the closing <c>)</c>.
    /// </summary>
    public static Selection ParseStringSplit(ParserContext context, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var input = Expression.Parse(context);

        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var separator = Expression.Parse(context);

        var emitOrdinal = false;
        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            var enableOrdinalExpr = Expression.Parse(context);

            // SQL Server requires the third arg to be a parse-time constant
            // (the schema is fixed at compile time) and raises Msg 8748 for
            // variable / column references. The simulator gates first on a
            // bare-VariableReference check (the empty-resolver Run trick
            // alone misses variables — VariableReference reads its slot
            // directly without going through the column resolver), then
            // evaluates the expression with an empty resolver to catch
            // column references. Cast / parenthesized wrappers around a
            // variable slip past this gate (a divergence from real SQL
            // Server's broader rejection), but the common bare-`@v` shape
            // surfaces correctly.
            if (enableOrdinalExpr is VariableReference)
                throw SimulatedSqlException.StringSplitEnableOrdinalMustBeConstant();
            SqlValue enableOrdinalValue;
            try
            {
                var dummyRuntime = new RuntimeContext(
                    n => throw new InvalidOperationException("Not parse-time constant."),
                    context.Batch);
                enableOrdinalValue = enableOrdinalExpr.Run(dummyRuntime);
            }
            catch (InvalidOperationException)
            {
                throw SimulatedSqlException.StringSplitEnableOrdinalMustBeConstant();
            }

            if (enableOrdinalValue.IsNull)
            {
                emitOrdinal = false;
            }
            else if (enableOrdinalValue.Type.Category != SqlTypeCategory.Integer || enableOrdinalValue.Type == SqlType.Bit)
            {
                throw SimulatedSqlException.InvalidArgumentDataType(enableOrdinalValue.Type.SqlServerName, argumentIndex: 3, "string_split");
            }
            else
            {
                var asLong = enableOrdinalValue.CoerceTo(SqlType.BigInt).AsInt64;
                emitOrdinal = asLong switch
                {
                    0 => false,
                    1 => true,
                    _ => throw SimulatedSqlException.StringSplitInvalidEnableOrdinal(asLong),
                };
            }
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        // Project the value column with the input's static string family
        // (varchar → varchar; nvarchar → nvarchar). Non-string input maps
        // to nvarchar — SQL Server's behavior is to silently CONVERT the
        // first arg to nvarchar; the simulator follows the same routing.
        var inputType = input.GetSqlType(context.Batch, outerTypeResolver ?? (_ => SqlType.NVarchar));
        var valueColumnType = SqlType.IsStringCategory(inputType) ? inputType : SqlType.NVarchar;
        return FromStringSplit(input, separator, emitOrdinal, valueColumnType);
    }
}
