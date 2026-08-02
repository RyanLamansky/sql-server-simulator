using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The first <c>index_id</c> real SQL Server hands an XML index. The range
    /// is per table, so every table's first XML index is 256000; spatial
    /// indexes have their own 384000+ range, and ordinary indexes keep the
    /// small ids starting at 1.
    /// </summary>
    private const int XmlIndexIdBase = 256000;

    /// <summary>
    /// Returns true when the token after the current <c>(</c> looks like an
    /// XML schema-collection argument (a 1- or 2-part name optionally
    /// preceded by the <c>CONTENT</c> or <c>DOCUMENT</c> contextual keyword)
    /// rather than a length / precision spec. Probes without advancing the
    /// cursor.
    /// </summary>
    internal static bool PeekIsXmlSchemaArgument(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            // A Name token after `(` is either a schema-collection ref or the
            // CONTENT/DOCUMENT discriminator. Numeric / MAX is a length-spec
            // path. UnquotedString is a Name subclass, so a single Name arm
            // covers both forms.
            return context.GetNextOptional() is Name;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Parses the inner argument of <c>xml(...)</c> as a schema-collection
    /// reference. Forms: <c>xml(name)</c>, <c>xml(CONTENT name)</c>,
    /// <c>xml(DOCUMENT name)</c>; the CONTENT/DOCUMENT discriminator is
    /// parsed-and-discarded (AW emits neither — every xml column is the
    /// default CONTENT form). Cursor enters on the <c>(</c>, exits on the
    /// matching <c>)</c>. The resolved <see cref="XmlSchemaCollection"/> is
    /// returned for the caller to attach to the column.
    /// </summary>
    internal static XmlSchemaCollection ParseXmlSchemaCollectionArgument(ParserContext context)
    {
        // Cursor on `(`. Advance to the inner content.
        context.MoveNextRequired();

        // Optional CONTENT / DOCUMENT discriminator.
        if (context.Token is UnquotedString { Value: var maybeKind }
            && (maybeKind.Equals("CONTENT", StringComparison.OrdinalIgnoreCase) || maybeKind.Equals("DOCUMENT", StringComparison.OrdinalIgnoreCase)))
        {
            context.MoveNextRequired();
        }

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var collectionName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Resolve via the schema's XmlSchemaCollections dict, falling back
        // to dbo for an unqualified name (matches the alias-type / table-type
        // resolution shape).
        var schemaName = collectionName.ImmediateQualifier ?? Database.DefaultSchemaName;
        return context.CurrentDatabase.Schemas.TryGetValue(schemaName, out var schema)
            && schema.XmlSchemaCollections.TryGetValue(collectionName.Leaf, out var collection)
            ? collection
            : throw SimulatedSqlException.InvalidObjectName(collectionName);
    }

    /// <summary>
    /// Parses <c>CREATE XML SCHEMA COLLECTION [schema.]name AS '&lt;xsd&gt;…'</c>.
    /// Cursor enters on the <c>XML</c> contextual keyword; caller has matched
    /// <c>CREATE</c>. The XSD text is stored verbatim; no XSD parsing or
    /// validation is performed.
    /// </summary>
    internal static bool TryParseCreateXml(ParserContext context)
    {
        // Cursor on XML. Advance to determine the kind (SCHEMA COLLECTION or
        // INDEX). PRIMARY XML INDEX has its own dispatch through the CREATE
        // path; here we handle the schema-collection and bare-secondary-index
        // forms only. SCHEMA is a reserved keyword in SQL Server's grammar
        // (Keyword.Schema), so the match is on ReservedKeyword rather than
        // the contextual-keyword path.
        context.MoveNextRequired();
        return context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Schema }
                => ParseCreateXmlSchemaCollection(context),
            ReservedKeyword { Keyword: Keyword.Index }
                => ParseCreateXmlIndex(context, isPrimary: false),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    /// <summary>
    /// Parses <c>CREATE PRIMARY XML INDEX name ON table(col) [WITH (…)]</c>.
    /// Cursor enters on the <c>PRIMARY</c> reserved keyword.
    /// </summary>
    internal static bool TryParseCreatePrimaryXml(ParserContext context)
    {
        // Cursor on PRIMARY. Advance to XML then INDEX.
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Xml })
            return false;
        context.MoveNextRequired();
        return context.Token is ReservedKeyword { Keyword: Keyword.Index }
            ? ParseCreateXmlIndex(context, isPrimary: true)
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses <c>DROP XML SCHEMA COLLECTION [schema.]name</c>. Cursor enters
    /// on the <c>XML</c> contextual keyword; caller has matched <c>DROP</c>.
    /// </summary>
    internal static bool TryParseDropXml(ParserContext context)
    {
        context.MoveNextRequired();
        return context.Token is ReservedKeyword { Keyword: Keyword.Schema }
            ? ParseDropXmlSchemaCollection(context)
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    private static bool ParseCreateXmlSchemaCollection(ParserContext context)
    {
        // Cursor on SCHEMA reserved keyword; expect COLLECTION next. COLLECTION
        // is a bare identifier (not in the reserved list).
        context.MoveNextRequired();
        if (context.Token is not Name { Value: var c } || !c.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        // Schema expression — typically a literal string (single-quoted or
        // N'…' prefixed). Real SQL Server also accepts an expression that
        // produces a string; the simulator handles only the literal form
        // since AW emits literals exclusively.
        if (context.Token is not Literal { Value: { IsNull: false } literalValue })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var xsdText = literalValue.AsString;
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        var schemaName = name.ImmediateQualifier ?? Database.DefaultSchemaName;
        if (!context.CurrentDatabase.Schemas.TryGetValue(schemaName, out var ownerSchema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schemaName);

        // Dual DDL gate — and this one runs the two halves in the opposite order
        // from CREATE TABLE / SYNONYM / TYPE: real checks ALTER on the target
        // schema first (Msg 15151 "Cannot alter the schema"), then the
        // database-scope CREATE XML SCHEMA COLLECTION (Msg 262 state 1).
        // Probe-confirmed against SQL Server 2025.
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, ownerSchema))
            throw SimulatedSqlException.CannotAlterSchemaDoesNotExist(schemaName);
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.CreateXmlSchemaCollection))
            throw SimulatedSqlException.DatabasePermissionDenied("CREATE XML SCHEMA COLLECTION", context.CurrentDatabase.Name);

        // Type-namespace collision with existing alias type / table type /
        // xml collection raises Msg 219 — same surface as the existing
        // alias-vs-table-type rule.
        if (ownerSchema.XmlSchemaCollections.ContainsKey(name.Leaf)
            || ownerSchema.TableTypes.ContainsKey(name.Leaf)
            || ownerSchema.AliasTypes.ContainsKey(name.Leaf))
        {
            throw SimulatedSqlException.TypeAlreadyExists($"{schemaName}.{name.Leaf}");
        }

        var id = context.CurrentDatabase.AllocateXmlCollectionId();
        ownerSchema.XmlSchemaCollections[name.Leaf] = new XmlSchemaCollection(
            id, name.Leaf, ownerSchema.SchemaId,
            principalId: null,
            xsdText: xsdText,
            createDate: context.Batch.CurrentStatement.UtcNow);
        return true;
    }

    private static bool ParseCreateXmlIndex(ParserContext context, bool isPrimary)
    {
        // Cursor on INDEX. Advance to the index name.
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var indexName = nameToken.Value;
        context.MoveNextRequired();

        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var tableName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();

        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name colToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnName = colToken.Value;
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        string? usingPrimaryName = null;
        XmlSecondaryIndexType? secondaryType = null;
        if (!isPrimary)
        {
            // USING XML INDEX primary_name FOR {PATH | VALUE | PROPERTY}
            if (context.Token is not UnquotedString { Value: var usingKw } || !usingKw.Equals("USING", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Xml })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not ReservedKeyword { Keyword: Keyword.Index })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not Name usingPrimaryToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            usingPrimaryName = usingPrimaryToken.Value;
            context.MoveNextRequired();
            if (context.Token is not ReservedKeyword { Keyword: Keyword.For })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not Name secondaryTypeToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            Span<char> upper = stackalloc char[secondaryTypeToken.Value.Length];
            var len = secondaryTypeToken.Value.AsSpan().ToUpperInvariant(upper);
            secondaryType = len switch
            {
                4 when upper[..4].SequenceEqual("PATH") => XmlSecondaryIndexType.Path,
                5 when upper[..5].SequenceEqual("VALUE") => XmlSecondaryIndexType.Value,
                8 when upper[..8].SequenceEqual("PROPERTY") => XmlSecondaryIndexType.Property,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextOptional();
        }

        // Optional WITH (...) trailer — parse-and-discard.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            SkipBalancedParens(context);
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table)
            || table.IsTableVariable
            || BatchContext.IsLocalTempName(table.Name))
        {
            throw SimulatedSqlException.InvalidObjectName(tableName);
        }

        var ordinal = -1;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, columnName))
            {
                ordinal = i;
                break;
            }
        }
        if (ordinal < 0)
            throw SimulatedSqlException.InvalidColumnName(columnName);

        // Duplicate index name on the same table raises Msg 1779 / 1913 in
        // real SQL Server (probe-confirmed for xml indexes uses 1913 — the
        // simulator surfaces the generic Msg 2714 since neither catalog
        // error factory exists yet).
        foreach (var existing in table.XmlIndexes)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(existing.Name, indexName))
                throw SimulatedSqlException.ThereIsAlreadyAnObject(indexName);
        }

        // A primary XML index owns an internal "node table" (sys.objects type
        // IT). Secondary indexes share their primary's node table. DacFx's
        // XML-index reverse-engineering joins through this internal table + its
        // per-index statistics, so a primary allocates an object id for it here
        // (0 for secondaries — they resolve their primary's at enumeration).
        // Msg 1934 echoes the statement as written, so the primary and
        // secondary forms report different verbs (probe-confirmed).
        if (IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptions(isPrimary ? "CREATE PRIMARY XML INDEX" : "CREATE XML INDEX", setOptions);

        var internalTableObjectId = isPrimary ? context.CurrentDatabase.AllocateObjectId() : 0;
        // XML indexes take index ids from real's dedicated 256000+ range, one
        // sequence per table in creation order — probe-confirmed (a second XML
        // index on the same table is 256001, the first on a second table is
        // 256000 again). Spatial indexes have their own 384000+ range.
        var index = new XmlIndex(
            indexName,
            ordinal,
            isPrimary,
            usingPrimaryName,
            secondaryType,
            XmlIndexIdBase + table.XmlIndexes.Count,
            internalTableObjectId);
        table.XmlIndexes.Add(index);
        return true;
    }

    private static bool ParseDropXmlSchemaCollection(ParserContext context)
    {
        // Cursor on SCHEMA. Advance to COLLECTION (bare identifier).
        context.MoveNextRequired();
        if (context.Token is not Name { Value: var c } || !c.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        var schemaName = name.ImmediateQualifier ?? Database.DefaultSchemaName;
        if (!context.CurrentDatabase.Schemas.TryGetValue(schemaName, out var ownerSchema))
            throw SimulatedSqlException.InvalidObjectName(name);
        // Real gates the drop on ALTER of the owning schema (or CONTROL on the
        // collection, a securable class the simulator's GRANT surface doesn't
        // carry) and reports Msg 15151 naming the collection's leaf.
        if (!ownerSchema.XmlSchemaCollections.ContainsKey(name.Leaf))
            throw SimulatedSqlException.InvalidObjectName(name);
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, ownerSchema))
            throw SimulatedSqlException.CannotDropXmlSchemaCollection(name.Leaf);
        _ = ownerSchema.XmlSchemaCollections.TryRemove(name.Leaf, out _);
        return true;
    }
}

/// <summary>
/// A registered XML index on a heap table. Created via
/// <c>CREATE [PRIMARY] XML INDEX name ON table(col) [USING XML INDEX primary FOR {PATH|VALUE|PROPERTY}]</c>;
/// stored on <see cref="HeapTable.XmlIndexes"/>. The simulator does not
/// index xml values for query acceleration — entries exist for
/// <c>sys.xml_indexes</c> round-trip only.
/// </summary>
internal sealed class XmlIndex(
    string name,
    int columnOrdinal,
    bool isPrimary,
    string? usingPrimaryIndexName,
    XmlSecondaryIndexType? secondaryType,
    int indexId,
    int internalTableObjectId)
{
    public readonly string Name = name;

    /// <summary>Object id of the internal "node table" (sys.objects type IT)
    /// a primary XML index owns; 0 for secondary indexes (which share their
    /// primary's node table). DacFx's XML-index export joins through this
    /// internal table and its per-index statistics.</summary>
    public readonly int InternalTableObjectId = internalTableObjectId;

    /// <summary>0-based column ordinal that the index targets. Translated
    /// to 1-based <c>column_id</c> on the catalog-view surface.</summary>
    public readonly int ColumnOrdinal = columnOrdinal;

    public readonly bool IsPrimary = isPrimary;

    /// <summary>Name of the primary XML index this secondary index uses
    /// for its rowset. Null for primary indexes.</summary>
    public readonly string? UsingPrimaryIndexName = usingPrimaryIndexName;

    /// <summary>For secondary indexes: PATH / VALUE / PROPERTY. Null for
    /// primary indexes.</summary>
    public readonly XmlSecondaryIndexType? SecondaryType = secondaryType;

    /// <summary>The index's <c>sys.indexes</c> / <c>sys.xml_indexes</c>
    /// <c>index_id</c>, taken from real's dedicated XML range: 256000 for a
    /// table's first XML index, incrementing per index on that table. A
    /// secondary index's primary reports the same value through
    /// <c>using_xml_index_id</c>, and the primary's internal node table is
    /// named after it.</summary>
    public readonly int IndexId = indexId;
}

/// <summary>
/// Secondary XML index kind (real SQL Server's three secondary forms).
/// Maps to <c>sys.xml_indexes.secondary_type</c> char(1): P / V / R.
/// </summary>
internal enum XmlSecondaryIndexType : byte
{
    Path,
    Value,
    Property,
}
