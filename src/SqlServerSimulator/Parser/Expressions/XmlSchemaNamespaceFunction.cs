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
/// the natural miss matches). A NULL argument raises Msg 8116. The
/// three-argument namespace-filtering form is not modeled. DacFx's bacpac
/// export reads this per user collection while scripting
/// <c>sys.xml_schema_collections</c>.
/// </summary>
internal sealed class XmlSchemaNamespaceFunction : Expression
{
    private readonly Expression schemaArg;
    private readonly Expression nameArg;

    public XmlSchemaNamespaceFunction(ParserContext context)
    {
        this.schemaArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.nameArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            throw new NotSupportedException("The three-argument XML_SCHEMA_NAMESPACE(schema, collection, namespace) form is not modeled; use the two-argument form.");
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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Xml;

    internal override string DebugDisplay() => "XML_SCHEMA_NAMESPACE(...)";
}
