namespace SqlServerSimulator.Parser;

/// <summary>
/// What <see cref="Expression"/> and <see cref="BooleanExpression"/> share: every
/// node describes its own shape through <see cref="Describe"/>, which is what
/// lets a walk over a tree reach every node and two trees be compared node by
/// node. <see cref="Describe"/> is abstract so a new node kind can't leave a
/// walk short.
/// </summary>
internal abstract class ExpressionNode
{
    private protected ExpressionNode()
    {
    }

    /// <summary>
    /// Reports this node's own state and its child nodes, each in source order,
    /// into <paramref name="shape"/>. A child is any expression or predicate the
    /// node evaluates in its own scope, an absent optional operand included (as
    /// <see langword="null"/>, so the remaining children keep their positions).
    /// Local state is whatever else distinguishes two nodes of one kind: an
    /// operator, a function, a literal value, a target type, a resolved object.
    /// A subquery binds in its own scope, so a node carrying one reports the
    /// inner query as local state, compared by identity, and no walk enters it.
    /// State that only caches or tracks execution is not shape and stays out.
    /// </summary>
    internal abstract void Describe(NodeShape shape);

    /// <summary>
    /// Visits this node and every node below it, parents before children and
    /// children in source order. <paramref name="visit"/> receives each node
    /// with its described shape and answers whether to descend into that node's
    /// children. Runs on an explicit stack rather than recursion, since a long
    /// operator chain nests thousands of nodes deep.
    /// </summary>
    internal void Walk(Func<ExpressionNode, NodeShape, bool> visit)
    {
        var shape = new NodeShape();
        var pending = new Stack<ExpressionNode>();
        pending.Push(this);
        while (pending.TryPop(out var node))
        {
            shape.Clear();
            node.Describe(shape);
            if (!visit(node, shape))
                continue;
            for (var i = shape.ChildNodes.Count - 1; i >= 0; i--)
            {
                if (shape.ChildNodes[i] is { } child)
                    pending.Push(child);
            }
        }
    }
}

/// <summary>
/// One node's shape as <see cref="ExpressionNode.Describe"/> reports it. The
/// reporting methods return the shape so a node describes itself in one chain:
/// <c>shape.Local(this.op).Child(this.left).Child(this.right)</c>.
/// </summary>
internal sealed class NodeShape
{
    /// <summary>The node's own distinguishing state, in the order reported.</summary>
    public readonly List<object?> Locals = [];

    /// <summary>The node's children in source order; <see langword="null"/> marks an absent optional operand.</summary>
    public readonly List<ExpressionNode?> ChildNodes = [];

    /// <summary>The column a column reference names; set only by that node kind.</summary>
    public MultiPartName? Column;

    public NodeShape Local(object? value)
    {
        this.Locals.Add(value);
        return this;
    }

    /// <summary>
    /// Reports text whose case is significant (an XQuery), which compares
    /// ordinally where plain string state compares case-insensitively.
    /// </summary>
    public NodeShape LocalExact(string? text)
    {
        this.Locals.Add(text is null ? null : new ExactText(text));
        return this;
    }

    public NodeShape Child(ExpressionNode? child)
    {
        this.ChildNodes.Add(child);
        return this;
    }

    /// <summary>
    /// Reports a variable-length run of children. The run's length is recorded
    /// as local state, so two runs splitting the same children differently
    /// (a <c>CASE</c> with one more <c>WHEN</c> and one fewer argument
    /// elsewhere) still compare unequal.
    /// </summary>
    public NodeShape Children(ExpressionNode?[] children)
    {
        this.Locals.Add(children.Length);
        foreach (var child in children)
            this.ChildNodes.Add(child);
        return this;
    }

    public NodeShape ColumnReference(MultiPartName name)
    {
        this.Column = name;
        return this;
    }

    private readonly struct ExactText(string text) : IEquatable<ExactText>
    {
        private readonly string text = text;

        public bool Equals(ExactText other) => string.Equals(this.text, other.text, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ExactText other && this.Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(this.text);
    }

    internal void Clear()
    {
        this.Locals.Clear();
        this.ChildNodes.Clear();
        this.Column = null;
    }
}

/// <summary>
/// A tree's shape flattened to one comparable sequence: per node, its kind, its
/// local state, which of its children are present, and the column it names,
/// parents before children. Two trees with equal keys are the same expression
/// written the same way — the structural match real's binder makes between a
/// GROUP BY expression and a select-list sub-expression. Parentheses are not
/// part of the shape, since real's tree has none.
/// </summary>
/// <remarks>
/// String state compares ordinal and case-insensitively (it holds identifiers
/// and keywords) unless reported through <see cref="NodeShape.LocalExact"/>. A string literal compares exactly, whatever its collation:
/// real refuses <c>SELECT b + 'X' … GROUP BY b + 'x'</c> under a
/// case-insensitive one (probed 2026-09-23 against SQL Server 2025). Every
/// other value compares by its own equality. A column is
/// reported through the caller's normalizer, so a binder can key a reference
/// by the column it resolves to rather than by how it was spelled.
/// </remarks>
internal sealed class ShapeKey : IEquatable<ShapeKey>
{
    private readonly object?[] tokens;
    private readonly int hash;

    private ShapeKey(object?[] tokens)
    {
        this.tokens = tokens;
        var hash = new HashCode();
        foreach (var token in tokens)
            hash.Add(token is string text ? StringComparer.OrdinalIgnoreCase.GetHashCode(text) : token?.GetHashCode() ?? 0);
        this.hash = hash.ToHashCode();
    }

    public static ShapeKey Of(ExpressionNode root, Func<MultiPartName, object> column)
    {
        var tokens = new List<object?>();
        root.Walk((node, shape) =>
        {
            if (node is Expressions.Parenthesized)
                return true;
            tokens.Add(node.GetType());
            tokens.AddRange(shape.Locals);
            tokens.Add(shape.ChildNodes.Count);
            foreach (var child in shape.ChildNodes)
                tokens.Add(child is not null);
            if (shape.Column is { } name)
                tokens.Add(column(name));
            return true;
        });
        return new([.. tokens]);
    }

    public bool Equals(ShapeKey? other)
    {
        if (other is null || other.hash != this.hash || other.tokens.Length != this.tokens.Length)
            return false;
        for (var i = 0; i < this.tokens.Length; i++)
        {
            var equal = (this.tokens[i], other.tokens[i]) switch
            {
                (string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
                (Storage.SqlValue left, Storage.SqlValue right) => left.Equals(right)
                    && (left.IsNull || left.Type.Category != Storage.SqlTypeCategory.String || string.Equals(left.AsString, right.AsString, StringComparison.Ordinal)),
                var (left, right) => Equals(left, right),
            };
            if (!equal)
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => this.Equals(obj as ShapeKey);

    public override int GetHashCode() => this.hash;
}
