using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Creates <see cref="ParserScope{T}"/> guards, inferring the slot's type.
/// </summary>
internal static class ParserScope
{
    /// <summary>
    /// Sets <paramref name="slot"/> to <paramref name="value"/> until the
    /// returned guard is disposed, which puts back the value the slot held on
    /// entry.
    /// </summary>
    public static ParserScope<T> Enter<T>(ref T slot, T value) => new(ref slot, value);

    /// <summary>
    /// Leaves <paramref name="slot"/> as it is and puts its entry value back
    /// when the returned guard is disposed — the frame for a body that assigns
    /// the slot itself.
    /// </summary>
    public static ParserScope<T> Save<T>(ref T slot) => new(ref slot, slot);
}

/// <summary>
/// Holds one <see cref="ParserContext"/> field at a value for a lexical scope
/// — <c>using var x = ParserScope.Enter(ref context.Field, value);</c> — and
/// restores the field's entry value on every exit, a throw included. A
/// <c>ref struct</c> so the guard lives on the stack: the parser is a measured
/// hot path, and a guard must not allocate.
/// </summary>
internal readonly ref struct ParserScope<T>
{
    private readonly ref T slot;
    private readonly T saved;

    public ParserScope(ref T slot, T value)
    {
        this.slot = ref slot;
        this.saved = slot;
        slot = value;
    }

    /// <summary>Restores the slot's entry value.</summary>
    public void Dispose() => this.slot = this.saved;
}

/// <summary>
/// The pair <see cref="ParserContext.ScalarOnlyOperand"/> and
/// <see cref="ParserContext.ScalarOnlyColumnReference"/>, armed together for
/// an operand that admits only scalar expressions and restored together on
/// exit. Created by <see cref="ParserContext.EnterScalarOnlyOperand"/>.
/// </summary>
internal readonly ref struct ScalarOnlyOperandScope
{
    private readonly ParserContext context;
    private readonly bool operand;
    private readonly Reference? firstReference;

    public ScalarOnlyOperandScope(ParserContext context)
    {
        this.context = context;
        this.operand = context.ScalarOnlyOperand;
        this.firstReference = context.ScalarOnlyColumnReference;
        context.ScalarOnlyOperand = true;
        context.ScalarOnlyColumnReference = null;
    }

    /// <summary>Restores both fields' entry values.</summary>
    public void Dispose()
    {
        this.context.ScalarOnlyOperand = this.operand;
        this.context.ScalarOnlyColumnReference = this.firstReference;
    }
}

/// <summary>
/// The frame one query specification parses in: its own aggregate, window and
/// graph-path collectors (the enclosing query's aggregate collector becoming
/// <see cref="ParserContext.EnclosingAggregateCollector"/>), no enclosing FROM
/// source's column sink, <c>NEXT VALUE FOR</c> refusals deferred to the spec's
/// end, and the fields the spec installs as it goes — the outer type and mask
/// resolvers, its scope sources and its <c>NEXT VALUE FOR</c> floor — put
/// back for the enclosing query on exit. Created by
/// <see cref="ParserContext.EnterQueryBlock"/>.
/// </summary>
internal readonly ref struct QueryBlockScope
{
    private readonly ParserContext context;
    private readonly List<AggregateExpression>? aggregateCollector;
    private readonly List<AggregateExpression>? enclosingAggregateCollector;
    private readonly List<WindowExpression>? windowCollector;
    private readonly List<GraphPathAggregate>? graphPathAggregates;
    private readonly List<Reference>? fromSourceColumnSink;
    private readonly bool deferNextValueRefusals;
    private readonly List<DeferredNextValueRef>? deferredNextValueRefs;
    private readonly Func<MultiPartName, SqlType>? outerTypeResolver;
    private readonly Func<MultiPartName, DataMask?>? outerMaskResolver;
    private readonly FromSource[]? scopeSources;
    private readonly NextValueForScope nextValueForRejection;

    public QueryBlockScope(ParserContext context, List<AggregateExpression> aggregates, List<WindowExpression> windows)
    {
        this.context = context;
        this.aggregateCollector = context.AggregateCollector;
        this.enclosingAggregateCollector = context.EnclosingAggregateCollector;
        this.windowCollector = context.WindowCollector;
        this.graphPathAggregates = context.GraphPathAggregates;
        this.fromSourceColumnSink = context.FromSourceColumnSink;
        this.deferNextValueRefusals = context.DeferNextValueRefusals;
        this.deferredNextValueRefs = context.DeferredNextValueRefs;
        this.outerTypeResolver = context.OuterTypeResolver;
        this.outerMaskResolver = context.OuterMaskResolver;
        this.scopeSources = context.ScopeSources;
        this.nextValueForRejection = context.NextValueForRejection;
        context.EnclosingAggregateCollector = context.AggregateCollector;
        context.AggregateCollector = aggregates;
        context.WindowCollector = windows;
        context.GraphPathAggregates = [];
        context.FromSourceColumnSink = null;
        context.DeferNextValueRefusals = true;
        context.DeferredNextValueRefs = null;
    }

    /// <summary>Restores every field's entry value.</summary>
    public void Dispose()
    {
        var context = this.context;
        context.AggregateCollector = this.aggregateCollector;
        context.EnclosingAggregateCollector = this.enclosingAggregateCollector;
        context.WindowCollector = this.windowCollector;
        context.GraphPathAggregates = this.graphPathAggregates;
        context.FromSourceColumnSink = this.fromSourceColumnSink;
        context.DeferNextValueRefusals = this.deferNextValueRefusals;
        context.DeferredNextValueRefs = this.deferredNextValueRefs;
        context.OuterTypeResolver = this.outerTypeResolver;
        context.OuterMaskResolver = this.outerMaskResolver;
        context.ScopeSources = this.scopeSources;
        context.NextValueForRejection = this.nextValueForRejection;
    }
}
