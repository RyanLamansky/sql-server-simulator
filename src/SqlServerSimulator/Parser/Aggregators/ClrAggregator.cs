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
/// A <c>Format.UserDefined</c> aggregate's state goes through its
/// <c>Write</c> into a fresh instance's <c>Read</c> exactly once per group,
/// between the last <c>Accumulate</c> and <c>Terminate</c>, which runs on the
/// fresh instance — whatever the group's size, an empty one included, grouped,
/// windowed and at any <c>MAXDOP</c> (probed 2026-10-07 against SQL Server
/// 2025), so a field <c>Write</c> leaves out reaches <c>Terminate</c> at its
/// default. <c>Merge</c> was never seen called. A throw from any of the
/// methods is Msg 6522 state 2 naming the aggregate.
/// </remarks>
internal sealed class ClrAggregator : Aggregator
{
    private readonly ClrAggregateFunction function;
    private object instance;
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
        if (this.function.Serialization is var (write, read))
            this.instance = this.RoundTrip(write, read);
        var returnType = this.function.ReturnType;
        var result = ClrTypeMarshaller.FromClr(this.Call(this.function.Terminate, []), returnType);
        return ClrTypeMarshaller.OverflowedWidth(result, returnType) is { } width
            ? throw SimulatedSqlException.ClrRoutineThrew(this.function.Name, ClrExceptionReport.Truncation(result.AsString.Length, width), state: 2)
            : result;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2077:DynamicallyAccessedMembers",
        Justification = "The aggregate's class comes from an assembly registered from bytes at run time, outside the application's static closure, so trimming cannot remove its constructor.")]
    private object RoundTrip(MethodInfo write, MethodInfo read)
    {
        using var state = new MemoryStream();
        using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, leaveOpen: true))
            _ = this.Call(write, [writer]);
        state.Position = 0;
        object fresh;
        using (CultureScope.Clr())
        {
            fresh = Activator.CreateInstance(this.function.Entry.Type)
                ?? throw new InvalidOperationException($"CLR aggregate type {this.function.Entry.Type.FullName} produced no instance.");
        }
        using var reader = new BinaryReader(state, System.Text.Encoding.UTF8);
        // A struct is boxed once, so Read fills the very box Terminate is called on.
        _ = this.Call(read, [reader], fresh);
        return fresh;
    }

    private object? Call(MethodInfo method, object?[] arguments, object? target = null)
    {
        try
        {
            using (CultureScope.Clr())
            using (this.function.Entry.Assembly.UsesServerContext ? ClrHost.Enter(pipe: null) : default(ClrHost.RoutineScope?))
                return method.Invoke(target ?? this.instance, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw SimulatedSqlException.ClrRoutineThrew(this.function.Name, ClrExceptionReport.Describe(ex.InnerException, method), state: 2);
        }
    }
}
