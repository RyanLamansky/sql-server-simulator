using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A legacy <c>CREATE DEFAULT</c> / <c>CREATE RULE</c> object: a named,
/// schema-resident expression that does nothing until <c>sp_bindefault</c> /
/// <c>sp_bindrule</c> attaches it to a table column or an alias type. It shares
/// the object-name namespace, carries its whole batch as its definition (as a
/// module does), and refuses <c>DROP</c> while any column still has it bound.
/// </summary>
internal abstract class BindableObject : SchemaObject
{
    public Schema Schema;

    protected BindableObject(Schema schema, string name, int objectId, DateTime createDate, string definition)
        : base(name, objectId, schema.SchemaId, createDate)
    {
        this.Schema = schema;
        this.DefinitionText = definition;
    }

    /// <summary>
    /// True when a column of any table in this object's database, or an alias
    /// type, has this object bound — what <c>DROP DEFAULT</c> / <c>DROP RULE</c>
    /// refuses over.
    /// </summary>
    public bool IsBound()
    {
        foreach (var schema in this.Schema.Database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var column in table.Columns)
                {
                    if (ReferenceEquals(column.BoundDefault, this) || ReferenceEquals(column.BoundRule, this))
                        return true;
                }
            }
            foreach (var alias in schema.AliasTypes.Values)
            {
                if (ReferenceEquals(alias.BoundDefault, this) || ReferenceEquals(alias.BoundRule, this))
                    return true;
            }
        }
        return false;
    }
}

/// <summary>
/// <c>CREATE DEFAULT name AS expr</c>. Bound to a column, its expression is
/// that column's default exactly as a DEFAULT constraint's would be; real
/// lists it in <c>sys.objects</c> as type <c>D</c> with no parent.
/// </summary>
internal sealed class DefaultObject(Schema schema, string name, int objectId, DateTime createDate, string definition, Expression expression)
    : BindableObject(schema, name, objectId, createDate, definition)
{
    public override string ObjectTypeCode => "D ";
    public override string ObjectTypeDescription => "DEFAULT_CONSTRAINT";

    public readonly Expression Expression = expression;
}

/// <summary>
/// <c>CREATE RULE name AS predicate</c>, the predicate reading its one
/// variable as the value a bound column is given. Checked where an INSERT
/// writes the column or an UPDATE sets it, a false answer is Msg 513; UNKNOWN
/// passes, as a CHECK constraint's does.
/// </summary>
internal sealed class RuleObject(Schema schema, string name, int objectId, DateTime createDate, string definition, BooleanExpression predicate)
    : BindableObject(schema, name, objectId, createDate, definition)
{
    public override string ObjectTypeCode => "R ";
    public override string ObjectTypeDescription => "RULE";

    /// <summary>The predicate, its variable parsed as a reference the enforcing site answers with the column's value.</summary>
    public readonly BooleanExpression Predicate = predicate;
}
