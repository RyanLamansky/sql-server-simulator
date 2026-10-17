using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// A query body's select-list column that the query reading the body
/// evaluates where it reads the column, as real's optimizer merges the body's
/// projection into its reader: <c>SELECT CASE WHEN k = 3 THEN 0 ELSE x END
/// FROM (SELECT k, 10 / (k - 3) AS x FROM src) d</c> and <c>SELECT x FROM v
/// WHERE k &lt;&gt; 3</c> answer there, the division never running for the
/// row the guard or the filter turns away (probed 2026-10-17 against SQL
/// Server 2025). The body leaves the column uncomputed and computes the
/// columns the expression reads instead; the reader runs the expression over
/// them (<see cref="Evaluate"/>).
/// </summary>
/// <remarks>
/// Each name the expression reads is bound to where the reader finds its
/// value (<see cref="DeferredBinding"/>): a column of the same body row, a
/// nested deferred column of that row, and — once the reader binds the
/// column (<see cref="BindAtReader"/>) — a column of a source to the
/// left of an <c>APPLY</c> or a name of the reader's enclosing scope. Every
/// instance is immutable and shared by the plans that hold it.
/// </remarks>
internal sealed class DeferredColumn
{
    private readonly Expression expression;

    private readonly MultiPartName[] names;

    private readonly DeferredBinding[] bindings;

    /// <summary>
    /// The columns of the body row the evaluation reads, nested deferred
    /// columns' included, which the body must compute.
    /// </summary>
    public readonly int[] Dependencies;

    /// <summary>Whether the evaluation reads nothing but its own row.</summary>
    public readonly bool ReadsOnlyItsRow;

    private DeferredColumn(Expression expression, MultiPartName[] names, DeferredBinding[] bindings)
    {
        this.expression = expression;
        this.names = names;
        this.bindings = bindings;
        var dependencies = new List<int>();
        var readsOnlyItsRow = true;
        foreach (var binding in bindings)
        {
            switch (binding.Kind)
            {
                case DeferredBindingKind.Output:
                    if (!dependencies.Contains(binding.Column))
                        dependencies.Add(binding.Column);
                    break;
                case DeferredBindingKind.Nested:
                    foreach (var dependency in binding.Nested!.Dependencies)
                    {
                        if (!dependencies.Contains(dependency))
                            dependencies.Add(dependency);
                    }
                    readsOnlyItsRow &= binding.Nested.ReadsOnlyItsRow;
                    break;
                default:
                    readsOnlyItsRow = false;
                    break;
            }
        }
        this.Dependencies = [.. dependencies];
        this.ReadsOnlyItsRow = readsOnlyItsRow;
    }

    /// <summary>
    /// The deferred form of <paramref name="expression"/>, each name it reads
    /// bound by <paramref name="bind"/>; null when the expression can't be
    /// evaluated outside its body — it isn't pure (<see cref="Expression.ParallelSafe"/>
    /// declines a subquery, a function call, a per-call value), holds an
    /// aggregate or a window, reads no column at all (real evaluates such an
    /// expression once as its plan starts), or names something
    /// <paramref name="bind"/> can't place.
    /// </summary>
    public static DeferredColumn? Of(Expression expression, Func<MultiPartName, DeferredBinding?> bind)
    {
        if (!expression.ParallelSafe)
            return null;
        var holdsAggregate = false;
        expression.Walk(ref holdsAggregate, static (node, _, ref holdsAggregate) =>
        {
            holdsAggregate |= node is Expressions.AggregateExpression or Expressions.WindowExpression;
            return !holdsAggregate;
        });
        if (holdsAggregate)
            return null;
        var names = new List<MultiPartName>();
        expression.VisitColumnReferences(ref names, static (name, ref names) =>
        {
            foreach (var known in names)
            {
                if (SameName(known, name))
                    return;
            }
            names.Add(name);
        });
        if (names.Count == 0)
            return null;
        var bindings = new DeferredBinding[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i].Leaf.StartsWith('$') || RowLocator.IsLocatorName(names[i]) || bind(names[i]) is not { } binding)
                return null;
            bindings[i] = binding;
        }
        return new DeferredColumn(expression, [.. names], bindings);
    }

    /// <summary>
    /// Has the body behind <paramref name="computes"/> — the columns it
    /// computes — compute what each <paramref name="deferred"/> column reads
    /// in place of the column, which its reader evaluates.
    /// </summary>
    public static void ComputeDependenciesInstead(bool[] computes, DeferredColumn?[]? deferred)
    {
        if (deferred is null)
            return;
        foreach (var column in deferred)
        {
            foreach (var dependency in column?.Dependencies ?? [])
                computes[dependency] = true;
        }
        for (var c = 0; c < deferred.Length && c < computes.Length; c++)
        {
            if (deferred[c] is not null)
                computes[c] = false;
        }
    }

    /// <summary>
    /// This column with every binding re-placed by <paramref name="map"/>,
    /// nested columns re-placed the same way; null when any binding can't be.
    /// </summary>
    public DeferredColumn? Rebind(Func<DeferredBinding, MultiPartName, DeferredBinding?> map)
    {
        var bindings = new DeferredBinding[this.bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = this.bindings[i];
            if (binding.Nested is { } nested)
            {
                if (nested.Rebind(map) is not { } rebound)
                    return null;
                bindings[i] = DeferredBinding.NestedColumn(rebound);
            }
            else if (map(binding, this.names[i]) is { } mapped)
            {
                bindings[i] = mapped;
            }
            else
            {
                return null;
            }
        }
        return new DeferredColumn(this.expression, this.names, bindings);
    }

    /// <summary>
    /// This column as the query reading it at <paramref name="slot"/> of
    /// <paramref name="sources"/> evaluates it: a name the body read from
    /// outside itself reads a source to the slot's left when the slot is an
    /// <c>APPLY</c> (<paramref name="lateral"/>), and the reader's enclosing
    /// scope otherwise. Null when such a name is ambiguous among those sources.
    /// </summary>
    public DeferredColumn? BindAtReader(FromSource[] sources, int slot, bool lateral) =>
        this.Rebind((binding, name) =>
        {
            if (binding.Kind != DeferredBindingKind.Free)
                return binding;
            if (!lateral)
                return DeferredBinding.OuterName;
            return Selection.TryBindSourceColumn(sources[..slot], name) switch
            {
                null => DeferredBinding.OuterName,
                (-1, _) => null,
                var (source, column) => DeferredBinding.SiblingColumn(source, column),
            };
        });

    /// <summary>
    /// The column's value for the reader's current <paramref name="tuple"/>,
    /// whose <paramref name="slot"/> holds the body row (never NULL-extended:
    /// the caller reads such a slot as NULL without evaluating).
    /// </summary>
    public SqlValue Evaluate(FromSource[] sources, byte[]?[] tuple, int slot, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, SourceColumnMemo? memo)
    {
        // Evaluated once per row a reader reads the column on, so the resolver
        // comes from a per-thread pool of frames rather than a closure per call;
        // a nested evaluation takes a frame of its own.
        var frame = idleFrames ?? new Frame();
        idleFrames = frame.NextIdle;
        (frame.Column, frame.Sources, frame.Tuple, frame.Slot, frame.Batch, frame.OuterResolver, frame.Memo) = (this, sources, tuple, slot, batch, outerResolver, memo);
        var value = this.expression.Run(new RuntimeContext(frame.Resolve, batch));
        (frame.Column, frame.Sources, frame.Tuple, frame.Batch, frame.OuterResolver, frame.Memo) = (null, null, null, null, null, null);
        frame.NextIdle = idleFrames;
        idleFrames = frame;
        return value;
    }

    /// <summary>
    /// The column's value over a row already decoded into
    /// <paramref name="values"/> — a <c>MERGE</c> source's — for a column
    /// that reads nothing but its own row (<see cref="ReadsOnlyItsRow"/>).
    /// </summary>
    public SqlValue EvaluateOver(SqlValue[] values, BatchContext batch) =>
        this.expression.Run(new RuntimeContext(name => this.ReadValue(name, values, batch), batch));

    private SqlValue ReadValue(MultiPartName name, SqlValue[] values, BatchContext batch)
    {
        for (var i = 0; i < this.names.Length; i++)
        {
            if (!SameName(this.names[i], name))
                continue;
            var binding = this.bindings[i];
            return binding.Kind switch
            {
                DeferredBindingKind.Output => values[binding.Column],
                DeferredBindingKind.Nested => binding.Nested!.EvaluateOver(values, batch),
                _ => throw new InvalidOperationException($"'{name}' in a deferred column reads outside the row it was handed."),
            };
        }
        throw new InvalidOperationException($"'{name}' in a deferred column has no binding.");
    }

    [ThreadStatic]
    private static Frame? idleFrames;

    /// <summary>One evaluation's state, read by its cached resolver.</summary>
    private sealed class Frame
    {
        public readonly Func<MultiPartName, SqlValue> Resolve;

        public DeferredColumn? Column;

        public FromSource[]? Sources;

        public byte[]?[]? Tuple;

        public int Slot;

        public BatchContext? Batch;

        public Func<MultiPartName, SqlValue>? OuterResolver;

        public SourceColumnMemo? Memo;

        public Frame? NextIdle;

        public Frame() => this.Resolve = name => this.Column!.Read(name, this.Sources!, this.Tuple!, this.Slot, this.Batch!, this.OuterResolver, this.Memo);
    }

    private SqlValue Read(MultiPartName name, FromSource[] sources, byte[]?[] tuple, int slot, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, SourceColumnMemo? memo)
    {
        for (var i = 0; i < this.names.Length; i++)
        {
            if (!SameName(this.names[i], name))
                continue;
            var binding = this.bindings[i];
            return binding.Kind switch
            {
                DeferredBindingKind.Output => Selection.DecodeOrCompute(sources[slot], binding.Column, tuple[slot]!, batch),
                DeferredBindingKind.Nested => binding.Nested!.Evaluate(sources, tuple, slot, batch, outerResolver, memo),
                // A sibling sits left of the APPLY slot in the reader's own
                // source array; a narrower array never holds one.
                DeferredBindingKind.Sibling when binding.Source < slot => Selection.ReadSourceColumn(sources, tuple, binding.Source, binding.Column, batch, outerResolver, memo),
                DeferredBindingKind.Outer when outerResolver is not null => outerResolver(name),
                _ => throw new InvalidOperationException($"'{name}' in a deferred column reads outside the row it was handed."),
            };
        }
        throw new InvalidOperationException($"'{name}' in a deferred column has no binding.");
    }

    /// <summary>
    /// Whether two names are the one name: the same parsed reference hands its
    /// own strings every row, so reference identity answers almost always.
    /// </summary>
    private static bool SameName(MultiPartName left, MultiPartName right)
    {
        if (left.Count != right.Count)
            return false;
        if (ReferenceEquals(left.Leaf, right.Leaf) && ReferenceEquals(left.ImmediateQualifier, right.ImmediateQualifier) && left.Count <= 2)
            return true;
        return string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>Where a <see cref="DeferredColumn"/> finds the value of one name its expression reads.</summary>
internal readonly struct DeferredBinding
{
    public readonly DeferredBindingKind Kind;

    /// <summary>The reader's source a <see cref="DeferredBindingKind.Sibling"/> binding reads.</summary>
    public readonly int Source;

    /// <summary>The column an <see cref="DeferredBindingKind.Output"/> or <see cref="DeferredBindingKind.Sibling"/> binding reads.</summary>
    public readonly int Column;

    /// <summary>The column a <see cref="DeferredBindingKind.Nested"/> binding evaluates.</summary>
    public readonly DeferredColumn? Nested;

    private DeferredBinding(DeferredBindingKind kind, int source, int column, DeferredColumn? nested)
    {
        this.Kind = kind;
        this.Source = source;
        this.Column = column;
        this.Nested = nested;
    }

    public static DeferredBinding OutputColumn(int column) => new(DeferredBindingKind.Output, -1, column, null);

    public static DeferredBinding NestedColumn(DeferredColumn nested) => new(DeferredBindingKind.Nested, -1, -1, nested);

    public static DeferredBinding SiblingColumn(int source, int column) => new(DeferredBindingKind.Sibling, source, column, null);

    public static readonly DeferredBinding FreeName = new(DeferredBindingKind.Free, -1, -1, null);

    public static readonly DeferredBinding OuterName = new(DeferredBindingKind.Outer, -1, -1, null);
}

/// <summary>The kinds of <see cref="DeferredBinding"/>.</summary>
internal enum DeferredBindingKind
{
    /// <summary>A column of the body row itself.</summary>
    Output,

    /// <summary>Another deferred column of the body row.</summary>
    Nested,

    /// <summary>A name the body read from outside itself, not yet placed in a reader.</summary>
    Free,

    /// <summary>A column of the reader's source to the left of the <c>APPLY</c> the body is.</summary>
    Sibling,

    /// <summary>A name the reader's enclosing scope answers.</summary>
    Outer,
}
