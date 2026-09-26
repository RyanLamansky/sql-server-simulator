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

        // Optional trailers, in any order: ON FILEGROUP fg (parse-and-discard),
        // IN PATH 'path' (parse-and-discard, legacy), WITH ACCENT_SENSITIVITY,
        // AS DEFAULT, AUTHORIZATION owner.
        while (context.Token is not (null or Operator { Character: ';' }))
        {
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

        if (context.CurrentDatabase.FullTextCatalogs.ContainsKey(name))
            throw SimulatedSqlException.FullTextCatalogAlreadyExists(name);

        if (!context.CurrentDatabase.Principals.TryGetValue(ownerName, out var owner))
            throw SimulatedSqlException.CannotFindPrincipal(ownerName);

        // AS DEFAULT semantics: demote any existing default before assigning.
        if (asDefault)
        {
            foreach (var existing in context.CurrentDatabase.FullTextCatalogs.Values)
                existing.IsDefault = false;
        }

        var id = context.CurrentDatabase.AllocateFullTextCatalogId();
        context.CurrentDatabase.FullTextCatalogs[name] = new FullTextCatalog(
            id, name, asDefault, accentSensitive, owner.PrincipalId,
            context.Batch.CurrentStatement.UtcNow);
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

        var columnSpecs = ParseFullTextColumnList(context);

        // Optional KEY INDEX <name>. Real SQL Server requires this clause on
        // CREATE FULLTEXT INDEX; AW's emit always includes it. The simulator
        // permits omission for forward-compat with hypothetical loaders that
        // emit without it.
        string? keyIndexName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Key })
        {
            context.MoveNextRequired();
            if (context.Token is not ReservedKeyword { Keyword: Keyword.Index })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not Name keyToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            keyIndexName = keyToken.Value;
            context.MoveNextOptional();
        }

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

        if (!context.Batch.TryResolveTable(tableName, out var table)
            || table.IsTableVariable
            || BatchContext.IsLocalTempName(table.Name))
        {
            throw SimulatedSqlException.InvalidObjectName(tableName);
        }

        if (table.FullTextIndex is not null)
            throw SimulatedSqlException.FullTextIndexAlreadyExists(tableName.ToString());

        // Resolve the catalog. When no ON clause is present, use the default
        // catalog (matches real SQL Server semantics — Msg 9967 if no default
        // exists, abbreviated here as a generic "could not find" rejection).
        FullTextCatalog catalog;
        if (catalogName is not null)
        {
            if (!context.CurrentDatabase.FullTextCatalogs.TryGetValue(catalogName, out catalog!))
                throw SimulatedSqlException.InvalidObjectName(new MultiPartName(catalogName));
        }
        else
        {
            catalog = context.CurrentDatabase.FullTextCatalogs.Values.FirstOrDefault(c => c.IsDefault)
                ?? throw SimulatedSqlException.InvalidObjectName(new MultiPartName("<default fulltext catalog>"));
        }

        // Resolve the key-index name to a unique-index id. PK is conventionally
        // index_id=1 in real SQL Server; named unique constraints follow.
        // The simulator's per-table key/index numbering mirrors sys.indexes
        // emit order: KeyConstraints first, then Indexes.
        var uniqueIndexId = 1;
        if (keyIndexName is not null)
        {
            var found = false;
            for (var ki = 0; ki < table.KeyConstraints.Count; ki++)
            {
                if (context.Batch.CurrentDatabase.Collation.Equals(table.KeyConstraints[ki].Name, keyIndexName))
                {
                    uniqueIndexId = ki + 1;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                for (var ii = 0; ii < table.Indexes.Count; ii++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(table.Indexes[ii].Name, keyIndexName))
                    {
                        uniqueIndexId = table.KeyConstraints.Count + ii + 1;
                        found = true;
                        break;
                    }
                }
            }
            if (!found)
                throw SimulatedSqlException.InvalidObjectName(new MultiPartName(keyIndexName));
        }

        var columns = ResolveFullTextColumns(context, table, columnSpecs, [], missingState: 4);
        if (options.StoplistName is { } stoplistName)
            throw SimulatedSqlException.FullTextStoplistNotFound(stoplistName, state: 1);
        if (options.PropertyListName is { } propertyListName)
            throw SimulatedSqlException.SearchPropertyListNotFound(propertyListName, state: 1);

        table.FullTextIndex = new FullTextIndex(catalog.Id, keyIndexName ?? string.Empty, uniqueIndexId, columns)
        {
            ChangeTracking = options.ChangeTracking,
            StoplistOff = options.StoplistOff,
        };
        return true;
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
            var languageId = 0;
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
                switch (context.Token)
                {
                    case Numeric n:
                        languageId = n.Value.AsInt32;
                        break;
                    case Literal:
                        // Language by name — parse-and-discard, leave LCID at
                        // 0 (matches the column's stored shape if AW emits a
                        // literal name in some other model).
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
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
            if (columns.Exists(c => c.ColumnId == ordinal) || existing.Exists(c => c.ColumnId == ordinal))
                throw SimulatedSqlException.FullTextDuplicateColumn(column.Name);
            if (spec.StatisticalSemantics)
                throw SimulatedSqlException.SemanticDatabaseNotRegistered();
            int? typeColumnId = spec.TypeColumnName is null ? null : ResolveColumnOrdinalForFullText(collation, table, spec.TypeColumnName, missingState);
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
                options.ChangeTracking = ParseChangeTrackingOption(context, allowNoPopulation: true);
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
    private static FullTextChangeTracking ParseChangeTrackingOption(ParserContext context, bool allowNoPopulation)
    {
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
        // Real gates the drop on ALTER ANY FULLTEXT CATALOG (or CONTROL on the
        // catalog, a securable class the simulator's GRANT surface doesn't
        // carry). Denial is Msg 7641 (probe-confirmed), and a db_ddladmin member
        // passes through the permission's DDL category.
        if (!context.CurrentDatabase.FullTextCatalogs.ContainsKey(name))
            throw SimulatedSqlException.InvalidObjectName(new MultiPartName(name));
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.AlterAnyFullTextCatalog))
            throw SimulatedSqlException.FullTextCatalogNotFoundOrDenied(name, context.CurrentDatabase.Name);
        _ = context.CurrentDatabase.FullTextCatalogs.TryRemove(name, out _);
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
            throw SimulatedSqlException.FullTextIndexMissing(tableName.Leaf, state: 5);
        table.FullTextIndex = null;
        return true;
    }

}
