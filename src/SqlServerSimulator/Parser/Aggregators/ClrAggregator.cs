using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// One group's instance of a CLR user-defined aggregate: a fresh instance of
/// the aggregate's class, <c>Init</c> called on it, <c>Accumulate</c> once per
/// row with the row's arguments converted to their declared parameter types —
/// NULLs included — and <c>Terminate</c> for the result (probed 2026-09-28
/// against SQL Server 2025: an empty group still calls <c>Terminate</c>).
/// <c>DISTINCT</c> passes each distinct argument tuple once.
/// </summary>
/// <remarks>
/// The instance lives in memory for the whole group, so <c>Merge</c> is never
/// called and a <c>Format.UserDefined</c> aggregate's <c>Read</c> /
/// <c>Write</c> never run. A throw from any of the three is Msg 6522 state 2
/// naming the aggregate.
/// </remarks>
internal sealed class ClrAggregator : Aggregator
{
    private readonly ClrAggregateFunction function;
    private readonly object instance;
    private readonly HashSet<SqlValueKey>? seen;
    private readonly ParameterInfo[] parameters;

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2077:DynamicallyAccessedMembers",
        Justification = "The aggregate's class comes from an assembly registered from bytes at run time, outside the application's static closure, so trimming cannot remove its constructor.")]
    public ClrAggregator(ClrAggregateFunction function, bool distinct)
    {
        this.function = function;
        this.seen = distinct ? [] : null;
        this.parameters = function.Accumulate.GetParameters();
        // The constructor is the aggregate's own code.
        using (CultureScope.Clr())
        {
            this.instance = Activator.CreateInstance(function.Entry.Type)
                ?? throw new InvalidOperationException($"CLR aggregate type {function.Entry.Type.FullName} produced no instance.");
        }
        _ = this.Call(function.Init, []);
    }

    public override void Add(SqlValue value) => this.Accumulate([value]);

    /// <summary>
    /// Passes one row's arguments, already converted to the declared parameter
    /// types, to <c>Accumulate</c>.
    /// </summary>
    public void Accumulate(SqlValue[] arguments)
    {
        if (this.seen is not null && !this.seen.Add(new SqlValueKey((SqlValue[])arguments.Clone())))
            return;
        var values = new object?[arguments.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = ClrTypeMarshaller.ToClr(arguments[i], this.parameters[i].ParameterType);
        _ = this.Call(this.function.Accumulate, values);
    }

    /// <summary>
    /// Evaluates the aggregate's arguments against the current row, converts
    /// them to their declared types, and accumulates them.
    /// </summary>
    public void AccumulateRow(Expressions.AggregateExpression aggregate, RuntimeContext runtime)
    {
        var expressions = aggregate.ClrArguments!;
        var arguments = new SqlValue[expressions.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameter = this.function.Parameters[i];
            arguments[i] = Expressions.Cast.ApplyCoercion(expressions[i].Run(runtime).CoerceTo(parameter.Type), parameter.Type, parameter.DeclaredMaxLength);
        }

        this.Accumulate(arguments);
    }

    public override SqlValue Result()
    {
        var returnType = this.function.ReturnType;
        var result = ClrTypeMarshaller.FromClr(this.Call(this.function.Terminate, []), returnType);
        return ClrTypeMarshaller.OverflowedWidth(result, returnType) is { } width
            ? throw SimulatedSqlException.ClrRoutineThrew(this.function.Name, ClrExceptionReport.Truncation(result.AsString.Length, width), state: 2)
            : result;
    }

    private object? Call(MethodInfo method, object?[] arguments)
    {
        try
        {
            using (CultureScope.Clr())
            using (this.function.Entry.Assembly.UsesServerContext ? ClrHost.Enter(pipe: null) : default(ClrHost.RoutineScope?))
                return method.Invoke(this.instance, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw SimulatedSqlException.ClrRoutineThrew(this.function.Name, ClrExceptionReport.Describe(ex.InnerException, method), state: 2);
        }
    }
}
