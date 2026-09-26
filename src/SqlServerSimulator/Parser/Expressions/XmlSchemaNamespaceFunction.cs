using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>XML_SCHEMA_NAMESPACE(relational_schema, collection_name)</c>:
/// returns an XML schema collection's XSD content typed as <c>xml</c>.
/// The simulator returns the raw source text captured from
/// <c>CREATE XML SCHEMA COLLECTION … AS '…'</c>; real SQL Server
/// reconstructs a normalized XSD from its internal component metadata — a
/// documented divergence. An unresolved schema/collection pair raises
/// Msg 6314 at execution (probe-confirmed: real raises 6314 even for the
/// built-in <c>sys</c> collection, which the simulator doesn't register, so
/// the natural miss matches). A NULL argument raises Msg 8116, and so does
/// one that isn't a string, while compiling (probed 2026-09-26 against SQL
/// Server 2025). The three-argument namespace-filtering form is not modeled.
/// DacFx's bacpac
/// export reads this per user collection while scripting
/// <c>sys.xml_schema_collections</c>.
/// </summary>
internal sealed class XmlSchemaNamespaceFunction : Expression
{
    private readonly Expression schemaArg;
    private readonly Expression nameArg;
    private readonly Expression? namespaceArg;

    public XmlSchemaNamespaceFunction(ParserContext context)
    {
        this.schemaArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.nameArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.namespaceArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var schemaName = RequireName(this.schemaArg.Run(runtime), 1);
        var collectionName = RequireName(this.nameArg.Run(runtime), 2);
        return runtime.Batch.CurrentDatabase.Schemas.TryGetValue(schemaName, out var schema)
            && schema.XmlSchemaCollections.TryGetValue(collectionName, out var collection)
            ? SqlValue.FromXml(collection.XsdText)
            : throw SimulatedSqlException.XmlSchemaCollectionNotInMetadata(collectionName);
    }

    private static string RequireName(SqlValue value, int argumentIndex)
        => value.IsNull
            ? throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex, "XML_SCHEMA_NAMESPACE")
            : value.CoerceTo(SqlType.NVarchar).AsString;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = StringScalars.RequireStringArgument(this.schemaArg, this.schemaArg.GetSqlType(batch, resolveColumnType), "XML_SCHEMA_NAMESPACE", 1, acceptsLegacyLob: false);
        _ = StringScalars.RequireStringArgument(this.nameArg, this.nameArg.GetSqlType(batch, resolveColumnType), "XML_SCHEMA_NAMESPACE", 2, acceptsLegacyLob: false);
        if (this.namespaceArg is not null)
        {
            _ = StringScalars.RequireStringArgument(this.namespaceArg, this.namespaceArg.GetSqlType(batch, resolveColumnType), "XML_SCHEMA_NAMESPACE", 3, acceptsLegacyLob: false);
            throw new NotSupportedException("The three-argument XML_SCHEMA_NAMESPACE(schema, collection, namespace) form is not modeled; use the two-argument form.");
        }
        return SqlType.Xml;
    }

    internal override string DebugDisplay() => "XML_SCHEMA_NAMESPACE(...)";

    internal override void Describe(NodeShape shape) => shape.Child(this.schemaArg).Child(this.nameArg).Child(this.namespaceArg);
}
