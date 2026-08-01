using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Call site of a CLR scalar function — <c>SELECT schema.fn(arg1, arg2)</c>
/// where <c>fn</c> was created with <c>AS EXTERNAL NAME</c>. The T-SQL
/// counterpart is <see cref="UserFunctionCall"/>; this one marshals into the
/// bound <see cref="MethodInfo"/> instead of dispatching a child batch.
/// </summary>
/// <remarks>
/// <para>
/// Arguments coerce to their declared T-SQL types first (so the caller's
/// implicit conversions match a T-SQL UDF's), then marshal through
/// <see cref="ClrTypeMarshaller"/> into the
/// <see cref="System.Data.SqlTypes"/> struct the method declares. NULL is
/// passed as the struct's own <c>Null</c> sentinel, not CLR
/// <see langword="null"/> — a SQLCLR routine is expected to test
/// <c>IsNull</c> itself, and real SQL Server only short-circuits NULL input
/// when the routine opted into <c>RETURNS NULL ON NULL INPUT</c>.
/// </para>
/// <para>
/// Anything the routine throws surfaces as Msg 6522, matching real SQL
/// Server's wrapper around a faulted user routine.
/// </para>
/// </remarks>
internal sealed class ClrFunctionCall(ClrScalarFunction function, Expression?[] arguments) : Expression
{
    private readonly ClrScalarFunction function = function;
    private readonly Expression?[] arguments = arguments;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.function.ReturnType;

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Same gate the T-SQL scalar-UDF call carries: a skipped statement —
        // a dead branch, or a module body binding at CREATE time — must not
        // reach managed code through the FROM-less-SELECT parse-time fold.
        if (runtime.Batch.IsSkipping)
            return SqlValue.Null(this.function.ReturnType);

        if (!runtime.Batch.Connection.Simulation.EnableClr)
            throw SimulatedSqlException.ClrExecutionDisabled();

        var parameters = this.function.Method.GetParameters();
        var values = new object[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var argExpr = this.arguments[i];
            var declared = this.function.Parameters[i].Type;
            var value = argExpr is null
                ? SqlValue.Null(declared)
                : argExpr.Run(runtime).CoerceTo(declared);
            values[i] = ClrTypeMarshaller.ToClr(value, parameters[i].ParameterType);
        }

        object? result;
        try
        {
            result = this.function.Method.Invoke(null, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw SimulatedSqlException.ClrRoutineThrew(this.function.Name, ex.InnerException);
        }

        return ClrTypeMarshaller.FromClr(result, this.function.ReturnType);
    }

    internal override string DebugDisplay() =>
        $"{this.function.Schema.Name}.{this.function.Name}({string.Join(", ", this.arguments.Select(a => a?.DebugDisplay() ?? "DEFAULT"))})";

    /// <summary>
    /// Parses the argument list of a CLR function call. Cursor on entry: the
    /// token <em>after</em> the opening <c>(</c>; on exit, the closing
    /// <c>)</c>.
    /// </summary>
    public static ClrFunctionCall ParseCall(ClrScalarFunction function, ParserContext context)
    {
        context.SecurableSink?.Add(new ReferencedSecurable(function.ObjectId, function.SchemaId, function.Name, function.Schema.Name, "EXECUTE"));
        return new(function, UserFunctionCall.ParseFunctionArguments(function, context));
    }
}
