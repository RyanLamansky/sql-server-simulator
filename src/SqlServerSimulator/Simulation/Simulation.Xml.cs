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
    /// reference — <c>xml(name)</c>, <c>xml(CONTENT name)</c> or
    /// <c>xml(DOCUMENT name)</c> — answering the collection and whether
    /// <c>DOCUMENT</c> was written. Cursor enters on the <c>(</c>, exits on
    /// the matching <c>)</c>.
    /// </summary>
    /// <remarks>
    /// Real resolves the collection while the batch compiles, so one the
    /// batch itself creates first is still Msg 6314, which stops the whole
    /// batch — a column, a variable, a parameter and a <c>CAST</c> target
    /// alike, the name quoted as written (probed 2026-10-06 against SQL
    /// Server 2025).
    /// </remarks>
    internal static (XmlSchemaCollection Collection, bool Document) ParseXmlSchemaCollectionArgument(ParserContext context)
    {
        // Cursor on `(`. Advance to the inner content.
        context.MoveNextRequired();

        var document = false;
        if (context.Token is UnquotedString { Value: var maybeKind }
            && (maybeKind.Equals("CONTENT", StringComparison.OrdinalIgnoreCase) || maybeKind.Equals("DOCUMENT", StringComparison.OrdinalIgnoreCase)))
        {
            document = maybeKind.Equals("DOCUMENT", StringComparison.OrdinalIgnoreCase);
            context.MoveNextRequired();
        }

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var collectionName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        return (ResolveXmlSchemaCollectionReference(context, collectionName), document);
    }

    /// <summary>
    /// The collection an <c>xml(…)</c> type names, an unqualified name
    /// searching as a type name does; Msg 6314 quoting the name as written
    /// when nothing matches.
    /// </summary>
    private static XmlSchemaCollection ResolveXmlSchemaCollectionReference(ParserContext context, MultiPartName collectionName) =>
        context.Batch.TryResolveXmlSchemaCollectionSchema(collectionName, out var schema)
            && schema.XmlSchemaCollections.TryGetValue(collectionName.Leaf, out var collection)
            ? collection
            : throw SimulatedSqlException.XmlSchemaCollectionNotInMetadata(collectionName.ToString());

    /// <summary>
    /// Parses the typed target of a <c>CAST</c> / <c>CONVERT</c> —
    /// <c>xml([CONTENT | DOCUMENT] [schema.]collection)</c> — answering the
    /// collection and whether <c>DOCUMENT</c> was written, or null (with the
    /// cursor untouched) when <paramref name="typeName"/> isn't that form. On
    /// success the cursor sits on the token after the closing <c>)</c>; a
    /// collection that doesn't resolve is Msg 6314, as for a declaration.
    /// </summary>
    internal static (XmlSchemaCollection Collection, bool Document)? TryParseXmlCastTarget(ParserContext context, Name typeName)
    {
        if (typeName is not UnquotedString || !typeName.Span.Equals("xml", StringComparison.OrdinalIgnoreCase))
            return null;
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' } || !PeekIsXmlSchemaArgument(context))
        {
            context.RestoreCheckpoint(checkpoint);
            return null;
        }

        context.MoveNextRequired();
        var document = false;
        if (context.Token is UnquotedString { Value: var kind }
            && (kind.Equals("CONTENT", StringComparison.OrdinalIgnoreCase) || kind.Equals("DOCUMENT", StringComparison.OrdinalIgnoreCase)))
        {
            document = kind.Equals("DOCUMENT", StringComparison.OrdinalIgnoreCase);
            context.MoveNextRequired();
        }
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var collectionName = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        return (ResolveXmlSchemaCollectionReference(context, collectionName), document);
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

        if (!context.Batch.TryResolveCreateSchema(name, out var ownerSchema, missingDefaultState: 2))
            throw SimulatedSqlException.XmlSchemaCollectionSchemaNotFound(name.ImmediateQualifier ?? Database.DefaultSchemaName);
        var schemaName = ownerSchema.Name;

        // Dual DDL gate — and this one runs the two halves in the opposite order
        // from CREATE TABLE / SYNONYM / TYPE: real checks ALTER on the target
        // schema first (Msg 15151 "Cannot alter the schema"), then the
        // database-scope CREATE XML SCHEMA COLLECTION (Msg 262 state 1).
        // Probe-confirmed against SQL Server 2025.
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, ownerSchema))
            throw SimulatedSqlException.CannotAlterSchemaDoesNotExist(schemaName);
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.CreateXmlSchemaCollection))
            throw SimulatedSqlException.DatabasePermissionDenied("CREATE XML SCHEMA COLLECTION", context.CurrentDatabase.Name);

        if (ownerSchema.XmlSchemaCollections.ContainsKey(name.Leaf))
            throw SimulatedSqlException.XmlSchemaCollectionCannotBeCreated(name.Leaf);

        // The id is drawn before the text is read, so a text real refuses
        // still spends one, while its components draw theirs only once it is
        // accepted; a rolled-back CREATE keeps both spent (probed 2026-10-16
        // against SQL Server 2025). A restore's replay takes its backed-up
        // ids instead, its components' stamped after.
        var database = context.CurrentDatabase;
        var restoring = database.RestoredXmlCollectionIds is { Count: > 0 };
        var id = database.AllocateXmlCollectionId();

        // The text is read as XML first, its parse errors real's own.
        _ = XmlWellFormedness.Canonical(xsdText, SqlType.IsNationalStringCategory(literalValue.Type));
        XmlSchemaCompiler.Reject(xsdText, existingText: null);
        var documents = XmlSchemaComponentBuilder.CountDocuments(xsdText);
        var componentCount = XmlSchemaComponentBuilder.Build(xsdText, [new(documents, 0)]).Components.Count;
        ownerSchema.XmlSchemaCollections[name.Leaf] = new XmlSchemaCollection(
            id, name.Leaf, ownerSchema.SchemaId,
            principalId: null,
            xsdText: xsdText,
            createDate: context.Batch.CurrentStatement.UtcNow)
        {
            Segments = [new(documents, restoring ? 0 : database.AllocateXmlComponentIds(componentCount))],
        };
        RecordSlotUndo<XmlSchemaCollection>(context, ownerSchema.XmlSchemaCollections, name.Leaf, null);
        ExtendedEvents.XeObjectEvents.Note(context.Batch, ExtendedEvents.XeEventKind.ObjectCreated, ownerSchema.Database, id, "SX", 0, name.Leaf);
        RecordDdlEvent(context, "CREATE_XML_SCHEMA_COLLECTION", schemaName, name.Leaf, "XML SCHEMA COLLECTION");
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER XML SCHEMA COLLECTION [schema.]name ADD '&lt;xsd&gt;…'</c>
    /// (the text may be a variable). Cursor enters on the <c>XML</c>
    /// contextual keyword; caller has matched <c>ALTER</c>.
    /// </summary>
    /// <remarks>
    /// The added schema documents join the collection's existing ones. Real
    /// admits only components the collection lacks — a global element, type or attribute the
    /// collection already declares in that namespace is Msg 6310 — refuses
    /// text that isn't a schema document with Msg 2378 and the identity
    /// constraints with Msg 9336, takes an empty string as a no-op, and rolls
    /// the change back with the transaction (all probed 2026-09-28 against SQL
    /// Server 2025). A collection that doesn't resolve, or that the session
    /// can't alter (ALTER on its schema), is Msg 6347 either way.
    /// </remarks>
    internal static bool TryParseAlterXmlSchemaCollection(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Schema })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name { Value: var collectionWord } || !collectionWord.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Add })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var textExpression = Expression.Parse(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveXmlSchemaCollectionSchema(name, out var ownerSchema)
            || !ownerSchema.XmlSchemaCollections.TryGetValue(name.Leaf, out var collection)
            || !PermissionEnforcement.HasSchemaAlter(context.Batch, ownerSchema))
        {
            throw SimulatedSqlException.XmlSchemaCollectionCannotBeAltered(name.Leaf);
        }

        var value = textExpression.Run(new RuntimeContext(missing => throw SimulatedSqlException.InvalidColumnName(missing), context.Batch));
        var added = value.IsNull ? string.Empty : value.AsString;
        if (added.Trim().Length == 0)
            return true;
        _ = XmlWellFormedness.Canonical(added, SqlType.IsNationalStringCategory(value.Type));
        var previousText = collection.XsdText;
        var previousSegments = collection.Segments;
        var documents = XmlSchemaComponentBuilder.CountDocuments(added);
        int AddedComponents() =>
            XmlSchemaComponentBuilder.Build(previousText + added, [.. previousSegments, new(documents, 0)]).Components.Count
                - collection.GetComponents().Components.Count;
        // Every compile refusal comes ahead of a redeclared component's, and
        // the added text's own names don't collide with the collection's
        // (probed 2026-10-16 against SQL Server 2025).
        XmlSchemaCompiler.Reject(added, previousText);
        try
        {
            collection.RejectRedeclaredComponents(added);
        }
        catch (SimulatedSqlException e) when (e.Number == 6310)
        {
            // Real numbers the added components up to the one it finds
            // already declared, and those ids stay spent (probed 2026-10-16
            // against SQL Server 2025).
            _ = context.CurrentDatabase.AllocateXmlComponentIds(AddedComponents());
            throw;
        }

        var previousModified = collection.ModifyDate;
        // The added components' ids stay spent when the ALTER rolls back.
        var firstComponentId = context.CurrentDatabase.AllocateXmlComponentIds(AddedComponents());
        collection.XsdText = previousText + added;
        collection.Segments = [.. previousSegments, new(documents, firstComponentId)];
        collection.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        collection.AlteredAt = Interlocked.Increment(ref context.Connection.Simulation.XmlSchemaCollectionAlterations);
        RecordDdlUndo(context, () =>
        {
            collection.XsdText = previousText;
            collection.Segments = previousSegments;
            collection.ModifyDate = previousModified;
        });
        ExtendedEvents.XeObjectEvents.Note(context.Batch, ExtendedEvents.XeEventKind.ObjectAltered, ownerSchema.Database, collection.Id, "SX", 0, collection.Name);
        RecordDdlEvent(context, "ALTER_XML_SCHEMA_COLLECTION", ownerSchema.Name, name.Leaf, "XML SCHEMA COLLECTION");
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

        var options = context.Token is ReservedKeyword { Keyword: Keyword.With } ? ParseXmlIndexOptions(context, isPrimary) : XmlIndexOptions.Default;

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(tableName.ToString(), state: 201);
        if (table.IsTableVariable)
            throw SimulatedSqlException.InvalidObjectName(tableName);

        var ordinal = -1;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, columnName))
            {
                ordinal = i;
                break;
            }
        }
        // Real checks the column, its type, the index's name and the primary
        // index a secondary names, then the SET options, then the table's
        // clustered primary key, each refusal ending the batch and rolling the
        // transaction back, or dooming it under TRY (probed 2026-10-16 against
        // SQL Server 2025).
        var writtenTable = tableName.ToString();
        if (ordinal < 0)
            throw SimulatedSqlException.IndexColumnMissing(columnName, state: 201).AbortingAsUnderXactAbort();
        if (table.Columns[ordinal].Type is not XmlSqlType)
            throw SimulatedSqlException.XmlIndexOnNonXmlColumn(table.Columns[ordinal].Name, writtenTable).AbortingAsUnderXactAbort();

        var replaced = table.XmlIndexes.Find(existing => context.Batch.CurrentDatabase.Collation.Equals(existing.Name, indexName));
        var collation = context.Batch.CurrentDatabase.Collation;
        if (options.DropExisting)
        {
            if (replaced is null)
                throw SimulatedSqlException.XmlIndexNotFoundToDrop(isPrimary, indexName, writtenTable).AbortingAsUnderXactAbort();
        }
        else if (replaced is not null)
        {
            throw SimulatedSqlException.IndexAlreadyExists(indexName, writtenTable, state: 201).AbortingAsUnderXactAbort();
        }
        else if (isPrimary && table.XmlIndexes.Find(existing => existing.IsPrimary && existing.ColumnOrdinal == ordinal) is { } columnPrimary)
        {
            throw SimulatedSqlException.SecondPrimaryXmlIndex(columnPrimary.Name, table.Columns[ordinal].Name, writtenTable).AbortingAsUnderXactAbort();
        }

        // Msg 1934 echoes the statement as written, so the primary and
        // secondary forms report different verbs (probe-confirmed).
        if (IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptions(isPrimary ? "CREATE PRIMARY XML INDEX" : "CREATE XML INDEX", setOptions);

        if (!isPrimary && !table.XmlIndexes.Exists(existing => existing.IsPrimary && collation.Equals(existing.Name, usingPrimaryName!)))
            throw SimulatedSqlException.UsingPrimaryXmlIndexNotFound(usingPrimaryName!, writtenTable).AbortingAsUnderXactAbort();
        if (isPrimary)
        {
            // The node table's rows are keyed by the clustered primary key:
            // fewer than 32 columns of at most 128 declared bytes together.
            var primaryKey = table.KeyConstraints.Find(key => key.Kind == KeyConstraintKind.PrimaryKey);
            if (primaryKey is not { IsClustered: true } || primaryKey.FullOrdinals.Length >= 32)
                throw SimulatedSqlException.PrimaryXmlIndexNeedsClusteredPrimaryKey(writtenTable).AbortingAsUnderXactAbort();
            var keyBytes = 0;
            foreach (var keyOrdinal in primaryKey.FullOrdinals)
                keyBytes += Math.Max(0, (int)BuiltInResources.GetSysColumnMetadata(table.Columns[keyOrdinal]).MaxLength);
            if (keyBytes > 128)
                throw SimulatedSqlException.PrimaryXmlIndexKeyTooWide(writtenTable, keyBytes).AbortingAsUnderXactAbort();
        }

        // Rebuilding a primary XML index drops the secondaries built on it
        // (probed 2026-10-07 against SQL Server 2025).
        var dropped = replaced is null ? [] : table.XmlIndexes.FindAll(existing => ReferenceEquals(existing, replaced)
            || (replaced.IsPrimary && existing.UsingPrimaryIndexName is { } primaryName && collation.Equals(primaryName, replaced.Name)));
        _ = table.XmlIndexes.RemoveAll(dropped.Contains);

        // A primary XML index owns an internal "node table" (sys.objects type
        // IT). Secondary indexes share their primary's node table. DacFx's
        // XML-index reverse-engineering joins through this internal table + its
        // per-index statistics, so a primary allocates an object id for it here
        // (0 for secondaries — they resolve their primary's at enumeration).

        var internalTableObjectId = isPrimary ? context.CurrentDatabase.AllocateObjectId() : 0;
        // XML indexes take index ids from real's dedicated 256000+ range, one
        // past the table's highest — probe-confirmed (a second XML index on
        // the same table is 256001, the first on a second table is 256000
        // again, and a dropped top id is reused). Spatial indexes have their
        // own 384000+ range.
        var nextIndexId = XmlIndexIdBase;
        foreach (var existing in table.XmlIndexes)
            nextIndexId = Math.Max(nextIndexId, existing.IndexId + 1);
        var index = new XmlIndex(
            indexName,
            ordinal,
            isPrimary,
            usingPrimaryName,
            secondaryType,
            nextIndexId,
            internalTableObjectId)
        {
            FillFactor = options.FillFactor,
            IsPadded = options.PadIndex,
            AllowRowLocks = options.AllowRowLocks,
            AllowPageLocks = options.AllowPageLocks,
        };
        table.XmlIndexes.Add(index);
        RecordDdlUndo(context, () =>
        {
            _ = table.XmlIndexes.Remove(index);
            table.XmlIndexes.AddRange(dropped);
        });
        return true;
    }

    /// <summary>What a <c>CREATE [PRIMARY] XML INDEX</c>'s <c>WITH</c> list sets.</summary>
    private readonly struct XmlIndexOptions(byte fillFactor, bool padIndex, bool allowRowLocks, bool allowPageLocks, bool dropExisting)
    {
        public readonly byte FillFactor = fillFactor;
        public readonly bool PadIndex = padIndex;
        public readonly bool AllowRowLocks = allowRowLocks;
        public readonly bool AllowPageLocks = allowPageLocks;
        public readonly bool DropExisting = dropExisting;

        public static XmlIndexOptions Default => new(0, false, true, true, false);
    }

    /// <summary>
    /// Reads a <c>CREATE [PRIMARY] XML INDEX</c>'s <c>WITH ( option = value
    /// [, …] )</c> list, the cursor on <c>WITH</c>; on exit, the token past
    /// its <c>)</c>. Real refuses, as the batch compiles (probed 2026-10-07
    /// against SQL Server 2025): a name no index takes as the generic XML
    /// INDEX's option, followed by Msg 153 for a number, a Msg 155 naming the
    /// statement for <c>ON</c> / <c>OFF</c> and a syntax error for anything
    /// else; <c>STATISTICS_ONLY</c> and <c>WAIT_AT_LOW_PRIORITY</c> as syntax;
    /// and, once the list has read, the first option the statement can't take
    /// — <c>ONLINE</c>, <c>RESUMABLE</c> or <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c>
    /// on, <c>IGNORE_DUP_KEY</c> on, any <c>MAX_DURATION</c>,
    /// <c>STATISTICS_INCREMENTAL</c>, <c>BUCKET_COUNT</c> or
    /// <c>DATA_COMPRESSION</c>.
    /// </summary>
    private static XmlIndexOptions ParseXmlIndexOptions(ParserContext context, bool isPrimary)
    {
        var statement = isPrimary ? "CREATE PRIMARY XML INDEX" : "CREATE XML INDEX";
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var options = XmlIndexOptions.Default;
        var fillFactor = options.FillFactor;
        bool padIndex = options.PadIndex, allowRowLocks = options.AllowRowLocks, allowPageLocks = options.AllowPageLocks, dropExisting = false;
        SimulatedSqlException? refusal = null, onlineRefusal = null;
        // Every name the switch below meets is an index option's, none longer than this.
        Span<char> upper = stackalloc char[32];
        while (true)
        {
            var nameToken = context.GetNextRequired();
            if (nameToken is not (StringToken or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var name = nameToken.Source.ToString();
            if (name.Equals("STATISTICS_ONLY", StringComparison.OrdinalIgnoreCase))
            {
                throw isPrimary
                    ? SimulatedSqlException.Aggregate([SimulatedSqlException.SyntaxErrorNear(context), SimulatedSqlException.InvalidUsageOfIndexOption(name, statement)])
                    : SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (name.Equals("WAIT_AT_LOW_PRIORITY", StringComparison.OrdinalIgnoreCase) || context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var value = context.GetNextRequired();
            if (!IndexOptionNames.Contains(name) && !name.Equals("BUCKET_COUNT", StringComparison.OrdinalIgnoreCase))
            {
                throw SimulatedSqlException.Aggregate([
                    SimulatedSqlException.UnrecognizedIndexOption(name, "XML INDEX"),
                    value switch
                    {
                        Numeric => SimulatedSqlException.InvalidUsageOfIndexOption(name, "XML INDEX"),
                        ReservedKeyword { Keyword: Keyword.On or Keyword.Off } => SimulatedSqlException.UnrecognizedIndexOption(name, statement),
                        _ => SimulatedSqlException.SyntaxErrorNear(context),
                    },
                ]);
            }
            var on = value is ReservedKeyword { Keyword: Keyword.On };
            switch (upper[..name.AsSpan().ToUpperInvariant(upper)])
            {
                case "ALLOW_PAGE_LOCKS":
                    allowPageLocks = on;
                    break;
                case "ALLOW_ROW_LOCKS":
                    allowRowLocks = on;
                    break;
                case "BUCKET_COUNT" or "STATISTICS_INCREMENTAL":
                    refusal ??= SimulatedSqlException.UnrecognizedIndexOption(name, statement);
                    break;
                case "DATA_COMPRESSION":
                    refusal ??= SimulatedSqlException.UnrecognizedIndexOption("data_compression", statement);
                    break;
                case "DROP_EXISTING":
                    dropExisting = on;
                    break;
                case "FILLFACTOR":
                    fillFactor = ReadFillFactor(context);
                    break;
                case "IGNORE_DUP_KEY" when on:
                    refusal ??= SimulatedSqlException.InvalidUsageOfIndexOption("ignore_dup_key", statement, state: 2);
                    break;
                case "MAX_DURATION":
                    refusal ??= SimulatedSqlException.InvalidUsageOfIndexOption("MAX_DURATION", statement, state: 3);
                    break;
                case "MAXDOP":
                    if (ReadIntegerOptionLiteral(context) > 32767)
                        throw SimulatedSqlException.IndexMaxDopOutOfRange(value.Source.ToString());
                    break;
                case "ONLINE" when on:
                    onlineRefusal = SimulatedSqlException.InvalidUsageOfIndexOption("ONLINE", statement, state: 3);
                    refusal ??= onlineRefusal;
                    break;
                case "OPTIMIZE_FOR_SEQUENTIAL_KEY" when on:
                    refusal ??= SimulatedSqlException.InvalidUsageOfIndexOption("OPTIMIZE_FOR_SEQUENTIAL_KEY", statement, state: 6);
                    break;
                case "PAD_INDEX":
                    padIndex = on;
                    break;
                case "RESUMABLE" when on:
                    refusal ??= SimulatedSqlException.InvalidUsageOfIndexOption("RESUMABLE", statement, state: 3);
                    break;
            }
            // The rest of a value: DATA_COMPRESSION's ON PARTITIONS list, a
            // unit (MAX_DURATION takes MINUTES, COMPRESSION_DELAY MINUTE or
            // MINUTES), ONLINE's low-priority clause. Anything else before the
            // next option or the list's end is a syntax error, as the batch
            // compiles.
            var optionName = name;
            if (optionName.Equals("DATA_COMPRESSION", StringComparison.OrdinalIgnoreCase) && !IsCompressionLevel(value))
            {
                throw SimulatedSqlException.Aggregate([NearToken(value), SimulatedSqlException.UnrecognizedIndexOption("data_compression", statement)]);
            }
            context.MoveNextRequired();
            if (optionName.Equals("DATA_COMPRESSION", StringComparison.OrdinalIgnoreCase) && context.Token is ReservedKeyword { Keyword: Keyword.On })
            {
                if (context.GetNextRequired() is not StringToken { Span: var partitions } || !partitions.Equals("PARTITIONS", StringComparison.OrdinalIgnoreCase)
                    || context.GetNextRequired() is not Operator { Character: '(' })
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                _ = ReadPartitionRanges(context);
                context.MoveNextRequired();
            }
            else if (context.Token is StringToken { Span: var unit }
                && (optionName.Equals("MAX_DURATION", StringComparison.OrdinalIgnoreCase) ? unit.Equals("MINUTES", StringComparison.OrdinalIgnoreCase)
                    : optionName.Equals("COMPRESSION_DELAY", StringComparison.OrdinalIgnoreCase) && (unit.Equals("MINUTE", StringComparison.OrdinalIgnoreCase) || unit.Equals("MINUTES", StringComparison.OrdinalIgnoreCase))))
            {
                context.MoveNextRequired();
            }
            else if (optionName.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) && value is ReservedKeyword { Keyword: Keyword.On or Keyword.Off } && context.Token is Operator { Character: '(' })
            {
                ReadLowPriorityClause(context);
                // The clause names no option this statement takes, which real
                // reports in place of ONLINE's own refusal.
                var unrecognized = SimulatedSqlException.UnrecognizedIndexOption("WAIT_AT_LOW_PRIORITY", statement);
                refusal = refusal is null || ReferenceEquals(refusal, onlineRefusal) ? unrecognized : refusal;
                context.MoveNextRequired();
            }
            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return refusal is null ? new(fillFactor, padIndex, allowRowLocks, allowPageLocks, dropExisting) : throw refusal;
    }

    private static SimulatedSqlException NearToken(Token token) =>
        token is ReservedKeyword keyword ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword) : SimulatedSqlException.SyntaxErrorNear(token);

    private static bool IsCompressionLevel(Token token) =>
        token.Source.ToString().ToUpperInvariant() is "COLUMNSTORE" or "COLUMNSTORE_ARCHIVE" or "NONE" or "PAGE" or "ROW";

    /// <summary>
    /// Reads the clause that follows <c>ONLINE = ON | OFF</c> — <c>( name (
    /// MAX_DURATION = n [MINUTES], ABORT_AFTER_WAIT = NONE | SELF | BLOCKERS ) )</c>
    /// — the cursor on its opening parenthesis and left on its closing one.
    /// Real reads it by that grammar whatever the statement and refuses its
    /// name, unless <c>WAIT_AT_LOW_PRIORITY</c>, as a syntax error once the
    /// clause has read (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    private static void ReadLowPriorityClause(ParserContext context)
    {
        var clauseName = context.GetNextRequired();
        if (clauseName is not (StringToken or ReservedKeyword) || context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (!IsWord(context.GetNextRequired(), "MAX_DURATION") || context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } duration } || duration.Type is not Int32SqlType)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (IsWord(context.GetNextRequired(), "MINUTES"))
            context.MoveNextRequired();
        if (context.Token is not Operator { Character: ',' }
            || !IsWord(context.GetNextRequired(), "ABORT_AFTER_WAIT") || context.GetNextRequired() is not Operator { Character: '=' }
            || !(IsWord(context.GetNextRequired(), "NONE") || IsWord(context.Token, "SELF") || IsWord(context.Token, "BLOCKERS"))
            || context.GetNextRequired() is not Operator { Character: ')' }
            || context.GetNextRequired() is not Operator { Character: ')' })
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (!IsWord(clauseName, "WAIT_AT_LOW_PRIORITY"))
            throw NearToken(clauseName);
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

        if (!context.Batch.TryResolveXmlSchemaCollectionSchema(name, out var ownerSchema))
            throw SimulatedSqlException.InvalidObjectName(name);
        var schemaName = ownerSchema.Name;
        // Real gates the drop on ALTER of the owning schema or CONTROL on the
        // collection, and reports Msg 15151 naming the collection's leaf.
        if (!ownerSchema.XmlSchemaCollections.TryGetValue(name.Leaf, out var existing))
            throw SimulatedSqlException.InvalidObjectName(name);
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, ownerSchema)
            && !PermissionEnforcement.HoldsPermission(context.Batch, ownerSchema.Database, Permission.Control, PermissionChecker.ClassXmlSchemaCollection, existing.Id, ownerSchema.SchemaId))
        {
            throw SimulatedSqlException.CannotDropXmlSchemaCollection(name.Leaf);
        }
        _ = ownerSchema.XmlSchemaCollections.TryRemove(name.Leaf, out _);
        DropSecurablePermissions(context, ownerSchema.Database, PermissionChecker.ClassXmlSchemaCollection, existing.Id);
        ExtendedEvents.XeObjectEvents.Note(context.Batch, ExtendedEvents.XeEventKind.ObjectDeleted, ownerSchema.Database, existing.Id, "SX", 0, existing.Name);
        RecordDdlEvent(context, "DROP_XML_SCHEMA_COLLECTION", schemaName, name.Leaf, "XML SCHEMA COLLECTION");
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

    /// <summary>The <c>FILLFACTOR</c> written, 0 for none, as <c>sys.indexes.fill_factor</c> reports it.</summary>
    public byte FillFactor;

    /// <summary><c>PAD_INDEX</c>, <c>sys.indexes.is_padded</c>.</summary>
    public bool IsPadded;

    /// <summary><c>ALLOW_ROW_LOCKS</c>, on unless written off.</summary>
    public bool AllowRowLocks = true;

    /// <summary><c>ALLOW_PAGE_LOCKS</c>, on unless written off.</summary>
    public bool AllowPageLocks = true;
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
