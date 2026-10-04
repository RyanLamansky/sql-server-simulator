using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A <c>CREATE SECURITY POLICY</c> object: a named set of row-level security
/// predicates, each an inline table-valued function applied to a table (or a
/// schema-bound view) as a filter on what reads see, or as a block on what a
/// write may leave behind. It shares the schema's object namespace and
/// projects through <c>sys.objects</c> as type <c>SP</c>,
/// <c>sys.security_policies</c> and <c>sys.security_predicates</c>.
/// </summary>
/// <remarks>
/// Everything an <c>ALTER SECURITY POLICY</c> changes lives on one immutable
/// <see cref="SecurityPolicyState"/> swapped whole, so a rolled-back
/// <c>ALTER</c> restores the prior state by putting the old reference back.
/// What a statement applies is read through
/// <see cref="Parser.RowSecurity.For(Parser.BatchContext, SchemaObject)"/>,
/// never off the policy directly.
/// </remarks>
internal sealed class SecurityPolicy(Schema schema, string name, int objectId, DateTime createDate, SecurityPolicyState state)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    public SecurityPolicyState State = state;

    public override string ObjectTypeCode => "SP";
    public override string ObjectTypeDescription => "SECURITY_POLICY";
}

/// <summary>
/// One version of a <see cref="SecurityPolicy"/>'s alterable state.
/// <see cref="NextPredicateId"/> only grows: real numbers a predicate added
/// after a drop past every id the policy ever used (probed 2026-10-04
/// against SQL Server 2025).
/// </summary>
internal sealed class SecurityPolicyState(bool isEnabled, bool isSchemaBound, bool notForReplication, SecurityPredicate[] predicates, int nextPredicateId)
{
    public readonly bool IsEnabled = isEnabled;
    public readonly bool IsSchemaBound = isSchemaBound;
    public readonly bool NotForReplication = notForReplication;
    public readonly SecurityPredicate[] Predicates = predicates;
    public readonly int NextPredicateId = nextPredicateId;
}

/// <summary>The two kinds of security predicate, numbered as <c>sys.security_predicates.predicate_type</c> numbers them.</summary>
internal enum SecurityPredicateKind
{
    Filter = 0,
    Block = 1,
}

/// <summary>
/// The write a block predicate guards, numbered as
/// <c>sys.security_predicates.operation</c> numbers it; a block predicate
/// declared without one guards all four.
/// </summary>
internal enum BlockOperation
{
    AfterInsert = 1,
    AfterUpdate = 2,
    BeforeUpdate = 3,
    BeforeDelete = 4,
}

/// <summary>
/// One <c>ADD FILTER | BLOCK PREDICATE schema.fn(args) ON target</c> of a
/// security policy. <see cref="Arguments"/> are parsed over the target's
/// columns and resolve them by name when they run, so a column added, dropped
/// or reordered elsewhere in the table leaves them valid; the function is
/// looked up by name each time a statement applies the predicate, which is
/// what lets a non-schema-bound policy follow its function's
/// <c>ALTER</c> and fail once it is dropped.
/// </summary>
internal sealed class SecurityPredicate(
    int id,
    SchemaObject target,
    SecurityPredicateKind kind,
    BlockOperation? operation,
    string functionSchema,
    string functionName,
    Expression[] arguments,
    string definition,
    bool isSchemaBound)
{
    /// <summary><c>sys.security_predicates.security_predicate_id</c>.</summary>
    public readonly int Id = id;

    /// <summary>The table, or schema-bound view, the predicate applies to.</summary>
    public readonly SchemaObject Target = target;

    public readonly SecurityPredicateKind Kind = kind;

    /// <summary>The write a block predicate guards; null for a filter, and for a block guarding every write.</summary>
    public readonly BlockOperation? Operation = operation;

    /// <summary>The predicate function's schema, as the policy names it (the default schema when it names none).</summary>
    public readonly string FunctionSchema = functionSchema;

    /// <summary>The predicate function's name, as the policy names it.</summary>
    public readonly string FunctionName = functionName;

    /// <summary>The call's arguments, one per parameter of the function as it stood when the predicate was added.</summary>
    public readonly Expression[] Arguments = arguments;

    /// <summary>
    /// <c>sys.security_predicates.predicate_definition</c>: the call in the
    /// canonical form a CHECK constraint's definition takes —
    /// <c>([dbo].[fp]([owner]))</c>, the names spelled as written.
    /// </summary>
    public readonly string Definition = definition;

    /// <summary>
    /// The policy's <c>SCHEMABINDING</c>, which no <c>ALTER</c> changes: off,
    /// the function and the columns are bound afresh each time a statement
    /// applies the predicate, and the reading principal needs <c>SELECT</c>
    /// on the function.
    /// </summary>
    public readonly bool IsSchemaBound = isSchemaBound;

    /// <summary>Whether this predicate guards <paramref name="operation"/>.</summary>
    public bool Guards(BlockOperation operation) =>
        this.Kind == SecurityPredicateKind.Block && (this.Operation is null || this.Operation == operation);
}
