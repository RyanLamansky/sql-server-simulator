namespace SqlServerSimulator.Schemas;

/// <summary>
/// One plan guide <c>sp_create_plan_guide</c> stored in a database, as
/// <c>sys.plan_guides</c> lists it. A guide is no schema object: its name is
/// database-wide, compared under the database's collation, and it takes no
/// object id. The simulator has no optimizer whose plans a guide could steer,
/// so a guide is kept, listed, enabled, disabled, validated and dropped, and
/// otherwise changes nothing a statement does.
/// </summary>
internal sealed class PlanGuide(int id, string name, PlanGuideScope scope, string queryText, int? scopeObjectId, string? scopeBatch, string? parameters, string? hints, DateTime createDate)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public readonly PlanGuideScope Scope = scope;

    /// <summary>The <c>@stmt</c> the guide was created over, as written.</summary>
    public readonly string QueryText = queryText;

    /// <summary>The module an <c>OBJECT</c> guide names; null for the other scopes.</summary>
    public readonly int? ScopeObjectId = scopeObjectId;

    /// <summary>The batch a <c>SQL</c> guide matches (its <c>@module_or_batch</c>, or <c>@stmt</c> without one), a <c>TEMPLATE</c> guide's <c>@stmt</c>; null for an <c>OBJECT</c> guide.</summary>
    public readonly string? ScopeBatch = scopeBatch;

    public readonly string? Parameters = parameters;
    public readonly string? Hints = hints;
    public readonly DateTime CreateDate = createDate;
    public DateTime ModifyDate = createDate;
    public bool IsDisabled;
}

/// <summary>A plan guide's <c>scope_type</c>, its value the column's.</summary>
internal enum PlanGuideScope : byte
{
    Object = 1,
    Sql = 2,
    Template = 3,
}
