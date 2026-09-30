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

    /// <summary>How many empty leading parts were written ahead of the first (<c>..t</c> is 2).</summary>
    private readonly byte omittedLeading;

    /// <summary>Whether the schema part was written empty (<c>db..t</c>) and filled in with the default.</summary>
    public readonly bool SchemaOmitted;

    /// <summary>
    /// Whether the leaf was written delimited (<c>[$node_id]</c>, <c>"$node_id"</c>),
    /// which a graph pseudo-column needs it not to be: real reads only the bare
    /// <c>$node_id</c> token as one (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    public readonly bool LeafDelimited;

    /// <summary>
    /// Whether the name was read from the statement's text rather than built
    /// by the simulator (MATCH's desugared equalities name a graph table's
    /// hidden internal columns, which a written name may not).
    /// </summary>
    public readonly bool FromText;

    /// <summary>A one-part name read from the statement's text, remembering whether it was written delimited.</summary>
    public MultiPartName(string singlePart, bool delimited)
        : this(singlePart)
    {
        this.LeafDelimited = delimited;
        this.FromText = true;
    }

    public MultiPartName(string singlePart)
    {
        ArgumentNullException.ThrowIfNull(singlePart);
        this.p1 = singlePart;
        this.Count = 1;
    }

    private MultiPartName(string p1, string p2, string? p3, string? p4, int count, byte omittedLeading = 0, bool schemaOmitted = false, bool leafDelimited = false, bool fromText = false)
    {
        this.LeafDelimited = leafDelimited;
        this.FromText = fromText;
        this.p1 = p1;
        this.p2 = p2;
        this.p3 = p3;
        this.p4 = p4;
        this.Count = count;
        this.omittedLeading = omittedLeading;
        this.SchemaOmitted = schemaOmitted;
    }

    /// <summary>
    /// This name, remembering the parts its writer left empty — the leading
    /// ones dropped and a middle schema filled with the default — so
    /// <see cref="Written"/> can echo it as real's messages do. Resolution
    /// reads the filled-in parts and never sees the difference.
    /// </summary>
    public MultiPartName WithOmissions(int leading, bool schema) =>
        leading == 0 && !schema
            ? this
            : new(this.p1, this.p2!, this.p3, this.p4, this.Count, (byte)leading, schema, this.LeafDelimited, this.FromText);

    /// <summary>
    /// The name as written: empty leading parts as leading dots, an omitted
    /// schema empty (<c>db..t</c>, <c>..t</c>), which is how real names an
    /// object it can't find (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    public string Written =>
        new string('.', this.omittedLeading)
            + (this.SchemaOmitted && this.Count == 3 ? $"{this.p1}..{this.p3}"
                : this.SchemaOmitted && this.Count == 4 ? $"{this.p1}.{this.p2}..{this.p4}"
                : this.ToString());

    /// <summary>
    /// This name without its empty leading parts, which is how <c>INSERT</c>
    /// and <c>EXEC</c> name what they can't find (probed 2026-09-26 against
    /// SQL Server 2025).
    /// </summary>
    public MultiPartName WithoutOmittedLeading() =>
        new(this.p1, this.p2!, this.p3, this.p4, this.Count, 0, this.SchemaOmitted);

    /// <summary>
    /// Returns a new <see cref="MultiPartName"/> with <paramref name="next"/>
    /// appended as the new rightmost segment (i.e. the new
    /// <see cref="Leaf"/>); used by the parser to grow a reference one
    /// dot-segment at a time. Throws Msg 4104 (the same error real
    /// SQL Server emits at resolution time) when the name is already at
    /// the 4-part grammar limit.
    /// </summary>
    /// <remarks><paramref name="delimited"/> records whether the new leaf was written delimited (<see cref="LeafDelimited"/>).</remarks>
    public MultiPartName WithAddedPart(string next, bool delimited = false)
    {
        ArgumentNullException.ThrowIfNull(next);
        return this.Count switch
        {
            1 => new(this.p1, next, null, null, count: 2, leafDelimited: delimited, fromText: this.FromText),
            2 => new(this.p1, this.p2!, next, null, count: 3, leafDelimited: delimited, fromText: this.FromText),
            3 => new(this.p1, this.p2!, this.p3!, next, count: 4, leafDelimited: delimited, fromText: this.FromText),
            _ => throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound($"{this}.{next}"),
        };
    }

    /// <summary>
    /// This name with its <see cref="Leaf"/> replaced by <paramref name="leaf"/>,
    /// every qualifier and flag kept.
    /// </summary>
    public MultiPartName WithLeaf(string leaf) => this.Count switch
    {
        1 => this.FromText ? new(leaf, this.LeafDelimited) : new(leaf),
        2 => new(this.p1, leaf, null, null, 2, this.omittedLeading, this.SchemaOmitted, this.LeafDelimited, this.FromText),
        3 => new(this.p1, this.p2!, leaf, null, 3, this.omittedLeading, this.SchemaOmitted, this.LeafDelimited, this.FromText),
        _ => new(this.p1, this.p2!, this.p3!, leaf, 4, this.omittedLeading, this.SchemaOmitted, this.LeafDelimited, this.FromText),
    };

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
    /// <c>BuiltInToken.Equals(name.ImmediateQualifier, "INSERTED")</c>
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
