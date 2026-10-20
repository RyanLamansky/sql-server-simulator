using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>XML_SCHEMA_NAMESPACE(relational_schema, collection_name [, namespace])</c>:
/// an XML schema collection's XSD typed as <c>xml</c>, regenerated from its
/// components as real does (<see cref="Schemas.XmlSchemaNamespaceWriter"/>),
/// the third argument narrowing it to one namespace's schema (Msg 6312 when
/// that namespace holds none). An unresolved schema/collection pair raises
/// Msg 6314 at execution (probe-confirmed: real raises 6314 even for the
/// built-in <c>sys</c> collection, which the simulator doesn't register, so
/// the natural miss matches). A NULL argument raises Msg 8116, and so does
/// one that isn't a string, while compiling (probed 2026-09-26 against SQL
/// Server 2025). DacFx's bacpac
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
        if (!runtime.Batch.CurrentDatabase.Schemas.TryGetValue(schemaName, out var schema)
            || !schema.XmlSchemaCollections.TryGetValue(collectionName, out var collection))
        {
            throw SimulatedSqlException.XmlSchemaCollectionNotInMetadata(collectionName);
        }
        var components = collection.GetComponents();
        if (this.namespaceArg is null)
            return SqlValue.FromXml(components.Components.Count == 0 ? collection.XsdText : Schemas.XmlSchemaNamespaceWriter.Write(components, null)!);
        // A NULL namespace names none, real's 6312 quoting it as empty.
        var namespaceValue = this.namespaceArg.Run(runtime);
        var namespaceName = namespaceValue.IsNull ? string.Empty : namespaceValue.CoerceTo(SqlType.NVarchar).AsString;
        return !namespaceValue.IsNull && Schemas.XmlSchemaNamespaceWriter.Write(components, namespaceName) is { } schemaText
            ? SqlValue.FromXml(schemaText)
            : throw SimulatedSqlException.XmlSchemaNamespaceNotInCollection(namespaceName, collectionName);
    }

    private static string RequireName(SqlValue value, int argumentIndex)
        => value.IsNull
            ? throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex, "XML_SCHEMA_NAMESPACE")
            : value.CoerceTo(SqlType.NVarchar).AsString;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        RequireName(this.schemaArg, 1);
        RequireName(this.nameArg, 2);
        if (this.namespaceArg is not null && !IsUntypedNullLiteral(this.namespaceArg))
            RequireName(this.namespaceArg, 3);
        return SqlType.Xml;

        void RequireName(Expression argument, int argumentIndex)
        {
            if (IsUntypedNullLiteral(argument))
                throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex, "XML_SCHEMA_NAMESPACE");
            _ = StringScalars.RequireStringArgument(argument, argument.GetSqlType(batch, resolveColumnType), "XML_SCHEMA_NAMESPACE", argumentIndex, acceptsLegacyLob: false);
        }
    }

    internal override string DebugDisplay() => "XML_SCHEMA_NAMESPACE(...)";

    internal override void Describe(NodeShape shape) => shape.Child(this.schemaArg).Child(this.nameArg).Child(this.namespaceArg);
}
