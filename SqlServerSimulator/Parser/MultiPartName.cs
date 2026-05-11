namespace SqlServerSimulator.Parser;

/// <summary>
/// Multi-part column / table / schema reference (a 1- to 4-segment identifier
/// like <c>col</c>, <c>alias.col</c>, <c>schema.table.col</c>, or
/// <c>db.schema.table.col</c>). Carried on every <see cref="Expressions.Reference"/>
/// expression and passed to runtime / parse-time column resolvers as the
/// shape they bind on.
/// </summary>
/// <remarks>
/// <para>
/// Storage is up to four <see cref="string"/> slots inline (no <c>byte[]</c>
/// or <c>List&lt;string&gt;</c> per name), with <see cref="Count"/> tracking
/// how many are populated. SQL Server's grammar caps qualified names at 4
/// segments (<c>linked.db.schema.object</c>); attempting a fifth segment
/// raises Msg 4104 from <see cref="WithAddedPart"/> with the full attempted
/// dotted name (matching the wire effect of real SQL Server, which parses
/// arbitrary-many parts but rejects them at resolution).
/// </para>
/// <para>
/// The struct is immutable — the parser grows a reference one dot-segment at
/// a time via <see cref="WithAddedPart"/>, which returns a fresh value the
/// caller reassigns. Resolvers read named accessors (<see cref="Leaf"/>,
/// <see cref="ImmediateQualifier"/>) and use <see cref="ToString"/> for
/// error-message rendering — the dotted form (<c>"db.schema.table.col"</c>)
/// drops straight into <c>$"Invalid column name '{name}'."</c> without an
/// explicit join.
/// </para>
/// </remarks>
internal readonly struct MultiPartName
{
    private readonly string p1;
    private readonly string? p2;
    private readonly string? p3;
    private readonly string? p4;

    /// <summary>Number of populated segments (1–<c>MaxParts</c>).</summary>
    public readonly int Count;

    public MultiPartName(string singlePart)
    {
        ArgumentNullException.ThrowIfNull(singlePart);
        this.p1 = singlePart;
        this.Count = 1;
    }

    private MultiPartName(string p1, string p2, string? p3, string? p4, int count)
    {
        this.p1 = p1;
        this.p2 = p2;
        this.p3 = p3;
        this.p4 = p4;
        this.Count = count;
    }

    /// <summary>
    /// Returns a new <see cref="MultiPartName"/> with <paramref name="next"/>
    /// appended as the new rightmost segment (i.e. the new
    /// <see cref="Leaf"/>); used by the parser to grow a reference one
    /// dot-segment at a time. Throws Msg 4104 (the same error real
    /// SQL Server emits at resolution time) when the name is already at
    /// the 4-part grammar limit.
    /// </summary>
    public MultiPartName WithAddedPart(string next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return this.Count switch
        {
            1 => new(this.p1, next, null, null, count: 2),
            2 => new(this.p1, this.p2!, next, null, count: 3),
            3 => new(this.p1, this.p2!, this.p3!, next, count: 4),
            _ => throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound($"{this}.{next}"),
        };
    }

    /// <summary>
    /// Indexed access to populated segments, left-to-right. <c>name[0]</c> is
    /// the leftmost qualifier (e.g. the db in <c>db.schema.table</c>);
    /// <c>name[Count - 1]</c> is the <see cref="Leaf"/>.
    /// </summary>
    public string this[int index] => index switch
    {
        0 when this.Count >= 1 => this.p1,
        1 when this.Count >= 2 => this.p2!,
        2 when this.Count >= 3 => this.p3!,
        3 when this.Count >= 4 => this.p4!,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>The rightmost segment — the column / object name itself.</summary>
    public string Leaf => this.Count switch
    {
        1 => this.p1,
        2 => this.p2!,
        3 => this.p3!,
        4 => this.p4!,
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// The segment immediately to the left of <see cref="Leaf"/> — the
    /// table alias / table name in <c>alias.col</c>, the table in
    /// <c>schema.table.col</c>, the table in <c>db.schema.table.col</c> —
    /// or <see langword="null"/> when the reference is unqualified
    /// (<see cref="Count"/> == 1). Use with
    /// <c>Collation.Default.Equals(name.ImmediateQualifier, "INSERTED")</c>
    /// shape: the equality check folds the null-or-unqualified case into a
    /// <c>false</c> result without a separate guard.
    /// </summary>
    public string? ImmediateQualifier => this.Count switch
    {
        1 => null,
        2 => this.p1,
        3 => this.p2,
        4 => this.p3,
        _ => null,
    };

    /// <summary>
    /// Renders the name in dotted form (<c>"db.schema.table.col"</c>).
    /// Used by error-message interpolation as the natural default —
    /// <c>$"Invalid column name '{name}'."</c> emits the full reference
    /// without any explicit join at the call site.
    /// </summary>
    public override string ToString() => this.Count switch
    {
        1 => this.p1,
        2 => $"{this.p1}.{this.p2}",
        3 => $"{this.p1}.{this.p2}.{this.p3}",
        4 => $"{this.p1}.{this.p2}.{this.p3}.{this.p4}",
        _ => throw new InvalidOperationException(),
    };
}
