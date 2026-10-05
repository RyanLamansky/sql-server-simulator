using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE FULLTEXT CATALOG name [AS DEFAULT] [AUTHORIZATION owner]
    /// [WITH ACCENT_SENSITIVITY = {ON|OFF}]</c>. Cursor enters on <c>FULLTEXT</c>;
    /// caller has matched <c>CREATE</c>. Routes to either a CATALOG parser or
    /// an INDEX parser based on the keyword following FULLTEXT.
    /// </summary>
    /// <remarks>
    /// The simulator has no full-text search engine — see
    /// <see cref="FullTextCatalog"/> for the no-enforcement rationale. The
    /// parsed catalog lands in <see cref="Database.FullTextCatalogs"/> for
    /// catalog-view round-trip via <c>sys.fulltext_catalogs</c>.
    /// </remarks>
    internal static bool TryParseCreateFullText(ParserContext context)
    {
        // Cursor on FULLTEXT contextual keyword. Advance to the kind.
        context.MoveNextRequired();
        return context.Token switch
        {
            UnquotedString { Value: var w } when w.Equals("CATALOG", StringComparison.OrdinalIgnoreCase)
                => ParseCreateFullTextCatalog(context),
            ReservedKeyword { Keyword: Keyword.Index }
                => ParseCreateFullTextIndex(context),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    /// <summary>
    /// Parses <c>DROP FULLTEXT {CATALOG name | INDEX ON table}</c>. Cursor
    /// enters on <c>FULLTEXT</c>; caller has matched <c>DROP</c>.
    /// </summary>
    internal static bool TryParseDropFullText(ParserContext context)
    {
        context.MoveNextRequired();
        return context.Token switch
        {
            UnquotedString { Value: var w } when w.Equals("CATALOG", StringComparison.OrdinalIgnoreCase)
                => ParseDropFullTextCatalog(context),
            ReservedKeyword { Keyword: Keyword.Index }
                => ParseDropFullTextIndex(context),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    private static bool ParseCreateFullTextCatalog(ParserContext context)
    {
        // Cursor on CATALOG word; advance to the catalog name.
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();

        var asDefault = false;
        var accentSensitive = true;
        var ownerName = "dbo";

        // Optional trailers, in real's fixed order: ON FILEGROUP fg and
        // IN PATH 'path' (both parse-and-discard), WITH ACCENT_SENSITIVITY,
        // AS DEFAULT, AUTHORIZATION owner. A clause out of that order ends the
        // statement, as on real, where `AS DEFAULT WITH …` reads the WITH as
        // the next statement's start (probed 2026-10-05 against SQL Server
        // 2025).
        var phase = 0;
        while (context.Token is not (null or Operator { Character: ';' }))
        {
            var clausePhase = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.On } => 1,
                ReservedKeyword { Keyword: Keyword.In } => 2,
                UnquotedString { Value: var inWord } when inWord.Equals("IN", StringComparison.OrdinalIgnoreCase) => 2,
                ReservedKeyword { Keyword: Keyword.With } => 3,
                ReservedKeyword { Keyword: Keyword.As } => 4,
                ReservedKeyword { Keyword: Keyword.Authorization } => 5,
                _ => 0,
            };
            if (clausePhase <= phase)
                goto done;
            phase = clausePhase;
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.As }:
                    context.MoveNextRequired();
                    if (context.Token is not ReservedKeyword { Keyword: Keyword.Default })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    asDefault = true;
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Authorization }:
                    context.MoveNextRequired();
                    if (context.Token is not Name ownerToken)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    ownerName = ownerToken.Value;
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.With }:
                    // WITH ACCENT_SENSITIVITY = ON | OFF
                    context.MoveNextRequired();
                    if (context.Token is not UnquotedString { Value: var optName }
                        || !optName.Equals("ACCENT_SENSITIVITY", StringComparison.OrdinalIgnoreCase))
                    {
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    accentSensitive = context.Token switch
                    {
                        ReservedKeyword { Keyword: Keyword.On } => true,
                        ReservedKeyword { Keyword: Keyword.Off } => false,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.On }:
                    // ON FILEGROUP fg / IN PATH '…' — legacy filesystem
                    // placement clauses. Parse-and-discard through the next
                    // statement boundary trailer or break out.
                    context.MoveNextRequired();
                    if (context.Token is UnquotedString { Value: var clause }
                        && clause.Equals("FILEGROUP", StringComparison.OrdinalIgnoreCase))
                    {
                        context.MoveNextRequired();
                        if (context.Token is not Name)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextOptional();
                        continue;
                    }
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                case ReservedKeyword { Keyword: Keyword.In }:
                case UnquotedString { Value: var maybeIn } when maybeIn.Equals("IN", StringComparison.OrdinalIgnoreCase):
                    // IN PATH '…' — legacy. Skip the path literal too.
                    context.MoveNextRequired();
                    if (context.Token is UnquotedString { Value: var path }
                        && path.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                    {
                        context.MoveNextRequired();
                        if (context.Token is not Literal)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextOptional();
                        continue;
                    }
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                default:
                    // Unknown trailer — break out and let the dispatch loop
                    // re-evaluate.
                    goto done;
            }
        }
    done:

        if (context.Batch.IsSkipping)
            return true;

        RejectFullTextDdlInTransaction(context, "CREATE FULLTEXT CATALOG");
        context.CurrentDatabase.RejectFullTextWriteWhenReadOnly(state: 100);

        // Database-scope CREATE FULLTEXT CATALOG gate — real reports its own
        // Msg 7666 rather than the Msg 262 the other CREATE permissions use.
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.CreateFullTextCatalog))
            throw SimulatedSqlException.FullTextUserDoesNotHavePermission();

        // A catalog name keeps to 120 characters (probed 2026-10-05 against
        // SQL Server 2025).
        if (name.Length > 120)
            throw SimulatedSqlException.NameTooLong(name, 120, state: 3);

        if (context.CurrentDatabase.FullTextCatalogs.ContainsKey(name))
            throw SimulatedSqlException.FullTextCatalogAlreadyExists(name);

        if (!context.CurrentDatabase.Principals.TryGetValue(ownerName, out var owner))
            throw SimulatedSqlException.FullTextOwnerNotFound(ownerName);

        // AS DEFAULT semantics: demote any existing default before assigning.
        if (asDefault)
        {
            foreach (var (_, existing) in context.CurrentDatabase.FullTextCatalogs)
                existing.IsDefault = false;
        }

        var id = context.CurrentDatabase.AllocateFullTextCatalogId();
        context.CurrentDatabase.FullTextCatalogs[name] = new FullTextCatalog(
            id, name, asDefault, accentSensitive, owner.PrincipalId,
            context.Batch.CurrentStatement.UtcNow);
        RecordDdlEvent(context, "CREATE_FULLTEXT_CATALOG", schemaName: null, name, "FULLTEXT CATALOG");
        return true;
    }

    private static bool ParseCreateFullTextIndex(ParserContext context)
    {
        // Cursor on INDEX keyword. Grammar:
        //   CREATE FULLTEXT INDEX ON table (col [TYPE COLUMN typecol] [LANGUAGE n] [, ...])
        //       [KEY INDEX key_index_name] [ON catalog_name [, FILEGROUP fg]]
        //       [WITH (option [, ...])]
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var tableName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();

        // The column list may be left out, creating an index with no columns
        // yet; KEY INDEX may not (both probed 2026-10-05 against SQL Server
        // 2025).
        var columnSpecs = context.Token is Operator { Character: '(' } ? ParseFullTextColumnList(context) : [];

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Key })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Index })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name keyToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var keyIndexName = keyToken.Value;
        context.MoveNextOptional();

        // Optional ON catalog_name [, FILEGROUP fg]
        string? catalogName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            context.MoveNextRequired();
            if (context.Token is Operator { Character: '(' })
            {
                // ON (catalog_name [, FILEGROUP fg]) — paren form.
                context.MoveNextRequired();
                if (context.Token is not Name catToken)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                catalogName = catToken.Value;
                context.MoveNextRequired();
                while (context.Token is Operator { Character: ',' })
                {
                    // Parse-and-discard FILEGROUP fg trailer.
                    context.MoveNextRequired();
                    if (context.Token is UnquotedString { Value: var fgWord }
                        && fgWord.Equals("FILEGROUP", StringComparison.OrdinalIgnoreCase))
                    {
                        context.MoveNextRequired();
                        if (context.Token is not Name)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextRequired();
                    }
                    else
                    {
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                }
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
            }
            else if (context.Token is Name onCatToken)
            {
                catalogName = onCatToken.Value;
                context.MoveNextOptional();
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        var options = context.Token is ReservedKeyword { Keyword: Keyword.With }
            ? ParseFullTextIndexOptions(context)
            : default;

        if (context.Batch.IsSkipping)
            return true;

        RejectFullTextDdlInTransaction(context, "CREATE FULLTEXT INDEX");
        context.CurrentDatabase.RejectFullTextWriteWhenReadOnly(state: 103);

        // Real splits the refusal by state: 49 for a name that resolves to
        // nothing, 48 for a temporary table (probed 2026-09-26). A view that
        // isn't indexed is Msg 9960 (probed 2026-10-05).
        if (!context.Batch.TryResolveTable(tableName, out var table))
        {
            throw context.Batch.TryResolveView(tableName, out _)
                ? SimulatedSqlException.FullTextViewNotIndexed(tableName.ToString())
                : SimulatedSqlException.InvalidObjectName(tableName, state: 49);
        }
        if (table.IsTableVariable || BatchContext.IsLocalTempName(table.Name))
            throw SimulatedSqlException.InvalidObjectName(tableName, state: 48);

        if (table.FullTextIndex is not null)
            throw SimulatedSqlException.FullTextIndexAlreadyExists(tableName.ToString());

        // Resolve the catalog: the named one (Msg 7641 state 1 when missing),
        // else the database's default (Msg 9967).
        FullTextCatalog catalog;
        if (catalogName is not null)
        {
            if (!context.CurrentDatabase.FullTextCatalogs.TryGetValue(catalogName, out catalog!))
                throw SimulatedSqlException.FullTextCatalogNotFoundOrDenied(catalogName, context.CurrentDatabase.Name, state: 1);
        }
        else
        {
            catalog = context.CurrentDatabase.FullTextCatalogs.EnumerateValues().FirstOrDefault(c => c.IsDefault)
                ?? throw SimulatedSqlException.FullTextDefaultCatalogMissing(context.CurrentDatabase.Name);
        }
        // Naming a catalog in an index takes REFERENCES on it (probed
        // 2026-10-05 against SQL Server 2025: Msg 7666 state 7 without).
        if (!PermissionEnforcement.HoldsPermission(context.Batch, context.CurrentDatabase, Permission.References, PermissionChecker.ClassFulltextCatalog, catalog.Id, 0))
            throw SimulatedSqlException.FullTextUserDoesNotHavePermission(state: 7);

        var uniqueIndexId = ResolveFullTextKeyIndex(context, table, keyIndexName);

        var columns = ResolveFullTextColumns(context, table, columnSpecs, [], missingState: 4);
        if (options.StoplistName is { } stoplistName)
            throw SimulatedSqlException.FullTextStoplistNotFound(stoplistName, state: 1);
        if (options.PropertyListName is { } propertyListName)
            throw SimulatedSqlException.SearchPropertyListNotFound(propertyListName, state: 1);

        if (options.NoPopulation && options.ChangeTracking != FullTextChangeTracking.Off)
            throw SimulatedSqlException.FullTextNoPopulationWithChangeTracking(state: 1);

        table.FullTextIndex = new FullTextIndex(catalog.Id, keyIndexName, uniqueIndexId, columns)
        {
            ChangeTracking = options.ChangeTracking,
            StoplistOff = options.StoplistOff,
            // An index created without columns starts disabled.
            IsEnabled = columns.Count > 0,
        };
        // Change tracking can't follow WRITETEXT / UPDATETEXT into a legacy
        // LOB column, which real warns about as the index is created.
        if (options.ChangeTracking != FullTextChangeTracking.Off
            && columns.Exists(column => table.Columns[column.ColumnId - 1].Type is TextSqlType or NTextSqlType or ImageSqlType))
        {
            context.Connection.PendingMessages.Enqueue(SimulatedSqlException.FullTextLegacyLobTrackingMessage(context.Batch, tableName.ToString()));
        }
        RecordDdlEvent(context, "CREATE_FULLTEXT_INDEX", EventSchemaName(tableName), tableName.Leaf, "TABLE");
        return true;
    }

    /// <summary>
    /// Resolves a <c>KEY INDEX</c> name to its <c>sys.indexes</c> id, refusing
    /// with real's Msg 7653 an index that can't key a full-text index: state 1
    /// for a name the table has no index by, 2 for one that isn't unique, is
    /// filtered, disabled or spans several columns, 3 for a nullable key column
    /// (probed 2026-10-05 against SQL Server 2025). Key constraints number
    /// first, then the table's indexes, as <c>sys.indexes</c> emits them.
    /// </summary>
    private static int ResolveFullTextKeyIndex(ParserContext context, HeapTable table, string keyIndexName)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        for (var ki = 0; ki < table.KeyConstraints.Count; ki++)
        {
            var constraint = table.KeyConstraints[ki];
            if (!collation.Equals(constraint.Name, keyIndexName))
                continue;
            if (constraint.FullOrdinals.Length != 1 || constraint.IsDisabled)
                throw SimulatedSqlException.FullTextKeyIndexInvalid(keyIndexName, state: 2);
            if (table.Columns[constraint.FullOrdinals[0]].Nullable)
                throw SimulatedSqlException.FullTextKeyIndexInvalid(keyIndexName, state: 3);
            return ki + 1;
        }
        for (var ii = 0; ii < table.Indexes.Count; ii++)
        {
            var index = table.Indexes[ii];
            if (!collation.Equals(index.Name, keyIndexName))
                continue;
            if (!index.IsUnique || index.KeyColumns.Length != 1 || index.Filter is not null || index.IsDisabled)
                throw SimulatedSqlException.FullTextKeyIndexInvalid(keyIndexName, state: 2);
            if (table.Columns[index.KeyFullOrdinals[0]].Nullable)
                throw SimulatedSqlException.FullTextKeyIndexInvalid(keyIndexName, state: 3);
            return table.KeyConstraints.Count + ii + 1;
        }
        throw SimulatedSqlException.FullTextKeyIndexInvalid(keyIndexName, state: 1);
    }

    /// <summary>One entry of a full-text column list, as written.</summary>
    private readonly struct FullTextColumnSpec(string columnName, string? typeColumnName, int languageId, bool statisticalSemantics)
    {
        public readonly string ColumnName = columnName;
        public readonly string? TypeColumnName = typeColumnName;
        public readonly int LanguageId = languageId;
        public readonly bool StatisticalSemantics = statisticalSemantics;
    }

    /// <summary>
    /// Parses <c>(col [TYPE COLUMN typecol] [LANGUAGE n] [STATISTICAL_SEMANTICS] [, …])</c>,
    /// shared by <c>CREATE FULLTEXT INDEX</c> and <c>ALTER FULLTEXT INDEX … ADD</c>.
    /// Cursor enters on <c>(</c> and leaves after <c>)</c>.
    /// </summary>
    private static List<FullTextColumnSpec> ParseFullTextColumnList(ParserContext context)
    {
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var columnSpecs = new List<FullTextColumnSpec>();
        while (true)
        {
            if (context.Token is not Name columnToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var columnName = columnToken.Value;
            string? typeColumnName = null;
            // Real's `default full-text language` server option is 1033.
            var languageId = 1033;
            context.MoveNextRequired();

            // Optional TYPE COLUMN typeCol
            if (context.Token is UnquotedString { Value: var typeWord }
                && typeWord.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
            {
                context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Column })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                if (context.Token is not Name typeColToken)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                typeColumnName = typeColToken.Value;
                context.MoveNextRequired();
            }

            // Optional LANGUAGE <lcid or name>
            if (context.Token is UnquotedString { Value: var langWord }
                && langWord.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase))
            {
                context.MoveNextRequired();
                // A language named rather than numbered resolves as the
                // predicates' `LANGUAGE` argument does.
                languageId = context.Token switch
                {
                    Numeric n => n.Value.CoerceTo(SqlType.Int32).AsInt32,
                    Literal { Value: { Type: VarbinarySqlType } binary } => binary.CoerceTo(SqlType.Int32).AsInt32,
                    Literal { Value: var languageName } => Parser.FullText.FullTextLanguage.ResolveName(languageName.AsString),
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                if (!Parser.FullText.FullTextLanguage.IsKnown(languageId))
                    throw SimulatedSqlException.FullTextInvalidLocale();
                context.MoveNextRequired();
            }

            var statisticalSemantics = false;
            if (context.Token is UnquotedString { Value: var statWord }
                && statWord.Equals("STATISTICAL_SEMANTICS", StringComparison.OrdinalIgnoreCase))
            {
                statisticalSemantics = true;
                context.MoveNextRequired();
            }

            columnSpecs.Add(new(columnName, typeColumnName, languageId, statisticalSemantics));

            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return columnSpecs;
            }
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Resolves a column list against <paramref name="table"/> with real's
    /// refusals, column by column (probed 2026-09-26 against SQL Server 2025):
    /// a missing column is Msg 1911 at <paramref name="missingState"/>, one
    /// that isn't character, <c>xml</c>, <c>image</c> or <c>varbinary(max)</c>
    /// Msg 7670, an <c>image</c> / <c>varbinary(max)</c> one without a
    /// <c>TYPE COLUMN</c> Msg 7655, a column named twice or already in
    /// <paramref name="existing"/> Msg 7672, and <c>STATISTICAL_SEMANTICS</c>
    /// Msg 41209.
    /// </summary>
    private static List<FullTextIndexColumn> ResolveFullTextColumns(ParserContext context, HeapTable table, List<FullTextColumnSpec> specs, List<FullTextIndexColumn> existing, byte missingState)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        var columns = new List<FullTextIndexColumn>(specs.Count);
        foreach (var spec in specs)
        {
            var ordinal = ResolveColumnOrdinalForFullText(collation, table, spec.ColumnName, missingState);
            var column = table.Columns[ordinal - 1];
            var type = column.Type;
            var isDocument = type is ImageSqlType || (type is VarbinarySqlType && column.MaxLength == SqlType.MaxLengthSentinel);
            if (!isDocument && !(SqlType.IsCollatedString(type) || type is XmlSqlType))
                throw SimulatedSqlException.FullTextColumnTypeInvalid(column.Name);
            if (isDocument && spec.TypeColumnName is null)
                throw SimulatedSqlException.FullTextTypeColumnRequired();
            if (!isDocument && spec.TypeColumnName is not null)
                throw SimulatedSqlException.FullTextTypeColumnNotAllowed();
            if (columns.Exists(c => c.ColumnId == ordinal) || existing.Exists(c => c.ColumnId == ordinal))
                throw SimulatedSqlException.FullTextDuplicateColumn(column.Name);
            if (spec.StatisticalSemantics)
                throw SimulatedSqlException.SemanticDatabaseNotRegistered();
            // A type column names a document's extension: a character column
            // of at most 260 characters (Msg 7671), its own name missing at the
            // next state up (probed 2026-10-05 against SQL Server 2025).
            int? typeColumnId = spec.TypeColumnName is null ? null : ResolveColumnOrdinalForFullText(collation, table, spec.TypeColumnName, (byte)(missingState + 1));
            if (typeColumnId is int typeOrdinal
                && table.Columns[typeOrdinal - 1] is var typeColumn
                && (!SqlType.IsCollatedString(typeColumn.Type) || typeColumn.MaxLength is not (> 0 and <= 260)))
            {
                throw SimulatedSqlException.FullTextTypeColumnInvalid(typeColumn.Name);
            }
            columns.Add(new FullTextIndexColumn(ordinal, spec.LanguageId, typeColumnId));
        }
        return columns;
    }

    private static int ResolveColumnOrdinalForFullText(Collation collation, HeapTable table, string columnName, byte missingState)
    {
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (collation.Equals(table.Columns[i].Name, columnName))
                return i + 1;
        }
        throw SimulatedSqlException.IndexColumnMissing(columnName, missingState);
    }

    /// <summary>What a <c>CREATE FULLTEXT INDEX … WITH</c> clause asked for.</summary>
    private struct FullTextIndexOptions
    {
        public FullTextChangeTracking ChangeTracking;
        public bool NoPopulation;
        public bool StoplistOff;
        public string? StoplistName;
        public string? PropertyListName;
    }

    /// <summary>
    /// Parses <c>WITH [(] option [, …] [)]</c>, whose options are
    /// <c>CHANGE_TRACKING [=] {MANUAL | AUTO | OFF [, NO POPULATION]}</c>,
    /// <c>STOPLIST [=] {OFF | SYSTEM | name}</c> and <c>SEARCH PROPERTY LIST
    /// [=] name</c>. Cursor enters on <c>WITH</c>.
    /// </summary>
    private static FullTextIndexOptions ParseFullTextIndexOptions(ParserContext context)
    {
        var options = default(FullTextIndexOptions);
        var parenthesized = context.GetNextRequired() is Operator { Character: '(' };
        if (parenthesized)
            context.MoveNextRequired();
        while (true)
        {
            if (IsFullTextWord(context.Token, "CHANGE_TRACKING"))
            {
                (options.ChangeTracking, options.NoPopulation) = ParseChangeTrackingOptionWithPopulation(context);
            }
            else if (IsFullTextWord(context.Token, "STOPLIST"))
            {
                (options.StoplistOff, options.StoplistName) = ParseStoplistValue(context);
            }
            else if (IsFullTextWord(context.Token, "SEARCH"))
            {
                options.PropertyListName = ParseSearchPropertyListValue(context);
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            if (parenthesized && context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return options;
            }
            if (context.Token is not Operator { Character: ',' })
            {
                return parenthesized ? throw SimulatedSqlException.SyntaxErrorNear(context) : options;
            }
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Parses <c>CHANGE_TRACKING [=] {MANUAL | AUTO | OFF}</c>, and on
    /// <paramref name="allowNoPopulation"/> the <c>, NO POPULATION</c> an
    /// <c>OFF</c> may carry. Cursor enters on <c>CHANGE_TRACKING</c> and
    /// leaves on the token after the clause.
    /// </summary>
    private static FullTextChangeTracking ParseChangeTrackingOption(ParserContext context, bool allowNoPopulation) =>
        ParseChangeTrackingOption(context, allowNoPopulation, out _);

    /// <summary>
    /// <see cref="ParseChangeTrackingOption(ParserContext, bool)"/> for
    /// <c>CREATE FULLTEXT INDEX</c>, also answering whether <c>NO
    /// POPULATION</c> followed.
    /// </summary>
    private static (FullTextChangeTracking Mode, bool NoPopulation) ParseChangeTrackingOptionWithPopulation(ParserContext context)
    {
        var mode = ParseChangeTrackingOption(context, allowNoPopulation: true, out var noPopulation);
        return (mode, noPopulation);
    }

    private static FullTextChangeTracking ParseChangeTrackingOption(ParserContext context, bool allowNoPopulation, out bool noPopulation)
    {
        noPopulation = false;
        if (!IsFullTextWord(context.Token, "CHANGE_TRACKING"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is Operator { Character: '=' })
            context.MoveNextRequired();
        FullTextChangeTracking mode;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Off })
            mode = FullTextChangeTracking.Off;
        else if (IsFullTextWord(context.Token, "MANUAL"))
            mode = FullTextChangeTracking.Manual;
        else if (IsFullTextWord(context.Token, "AUTO"))
            mode = FullTextChangeTracking.Auto;
        else
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (!allowNoPopulation || context.Token is not Operator { Character: ',' })
            return mode;
        var beforeComma = context.SaveCheckpoint();
        if (context.MoveNext() && IsFullTextWord(context.Token, "NO"))
        {
            if (context.GetNextRequired() is not Name populationToken || !IsFullTextWord(populationToken, "POPULATION"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            noPopulation = true;
            return mode;
        }
        context.RestoreCheckpoint(beforeComma);
        return mode;
    }

    /// <summary>
    /// Parses <c>STOPLIST [=] {OFF | SYSTEM | name}</c>, cursor entering on
    /// <c>STOPLIST</c>: the pair is <c>(off, named)</c>, where only the
    /// system stoplist exists here, so a name is one real would reject.
    /// </summary>
    private static (bool Off, string? Name) ParseStoplistValue(ParserContext context)
    {
        if (context.GetNextRequired() is Operator { Character: '=' })
            context.MoveNextRequired();
        var off = context.Token is ReservedKeyword { Keyword: Keyword.Off };
        if (!off && context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = off || IsFullTextWord(context.Token, "SYSTEM") ? null : ((Name)context.Token!).Value;
        context.MoveNextOptional();
        return (off, name);
    }

    /// <summary>
    /// Parses <c>SEARCH PROPERTY LIST [=] {OFF | name}</c>, cursor entering on
    /// <c>SEARCH</c>; returns the name, which no search property list here can
    /// answer, or null for <c>OFF</c>.
    /// </summary>
    private static string? ParseSearchPropertyListValue(ParserContext context)
    {
        if (!IsFullTextWord(context.GetNextRequired(), "PROPERTY") || !IsFullTextWord(context.GetNextRequired(), "LIST"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is Operator { Character: '=' })
            context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Off })
        {
            context.MoveNextOptional();
            return null;
        }
        if (context.Token is not Name listName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return listName.Value;
    }

    private static bool IsFullTextWord(Token? token, string word) =>
        token is Name name && name.Value.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Real refuses full-text DDL inside a user transaction (Msg 574).</summary>
    private static void RejectFullTextDdlInTransaction(ParserContext context, string statement)
    {
        if (context.Connection.CurrentTransaction is not null)
            throw SimulatedSqlException.StatementInsideUserTransaction(statement);
    }

    private static bool ParseDropFullTextCatalog(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;
        RejectFullTextDdlInTransaction(context, "DROP FULLTEXT CATALOG");
        context.CurrentDatabase.RejectFullTextWriteWhenReadOnly(state: 102);
        // Real gates the drop on ALTER ANY FULLTEXT CATALOG or CONTROL on the
        // catalog. Denial is Msg 7641 (probe-confirmed), and a db_ddladmin member
        // passes through the permission's DDL category.
        if (!context.CurrentDatabase.FullTextCatalogs.TryGetValue(name, out var existing))
            throw SimulatedSqlException.FullTextCatalogNotFoundOrDenied(name, context.CurrentDatabase.Name, state: 4);
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.AlterAnyFullTextCatalog)
            && !PermissionEnforcement.HoldsPermission(context.Batch, context.CurrentDatabase, Permission.Control, PermissionChecker.ClassFulltextCatalog, existing.Id, 0))
        {
            throw SimulatedSqlException.FullTextCatalogNotFoundOrDenied(name, context.CurrentDatabase.Name);
        }
        // A catalog still holding an index can't go (Msg 7668); a default one
        // goes with a warning (both probed 2026-10-05 against SQL Server 2025).
        foreach (var (_, schema) in context.CurrentDatabase.Schemas)
        {
            foreach (var (_, indexed) in schema.HeapTables)
            {
                if (indexed.FullTextIndex?.CatalogId == existing.Id)
                    throw SimulatedSqlException.FullTextCatalogNotEmpty(name);
            }
        }
        if (existing.IsDefault)
            context.Connection.PendingMessages.Enqueue(SimulatedSqlException.FullTextDefaultCatalogDroppedMessage(context.Batch, name));
        _ = context.CurrentDatabase.FullTextCatalogs.TryRemove(name, out _);
        DropSecurablePermissions(context, context.CurrentDatabase, PermissionChecker.ClassFulltextCatalog, existing.Id);
        RecordDdlEvent(context, "DROP_FULLTEXT_CATALOG", schemaName: null, name, "FULLTEXT CATALOG");
        return true;
    }

    private static bool ParseDropFullTextIndex(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var tableName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        RejectFullTextDdlInTransaction(context, "DROP FULLTEXT INDEX");
        context.CurrentDatabase.RejectFullTextWriteWhenReadOnly(state: 105);

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableName);
        if (table.FullTextIndex is null)
            throw SimulatedSqlException.FullTextIndexMissing(tableName.ToString(), state: 5);
        // Dropping the index takes ALTER on the table; real answers a denial
        // as though the index were missing, at state 3 (probed 2026-10-05
        // against SQL Server 2025).
        if (PermissionEnforcement.SchemaObjectDenial(context.Batch, "ALTER", table) is not null)
            throw SimulatedSqlException.FullTextIndexMissing(tableName.ToString(), state: 3);
        table.FullTextIndex = null;
        RecordDdlEvent(context, "DROP_FULLTEXT_INDEX", EventSchemaName(tableName), tableName.Leaf, "TABLE");
        return true;
    }

}
