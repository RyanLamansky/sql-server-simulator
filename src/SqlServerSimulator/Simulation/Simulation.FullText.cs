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

        // Database-scope CREATE FULLTEXT CATALOG gate — real reports its own
        // Msg 7666 rather than the Msg 262 the other CREATE permissions use.
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.CreateFullTextCatalog))
            throw SimulatedSqlException.FullTextUserDoesNotHavePermission();

        if (context.CurrentDatabase.FullTextCatalogs.ContainsKey(name))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name);

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

        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var columnSpecs = new List<(string ColumnName, string? TypeColumnName, int LanguageId)>();
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

            // Optional STATISTICAL_SEMANTICS (parse-and-discard).
            if (context.Token is UnquotedString { Value: var statWord }
                && statWord.Equals("STATISTICAL_SEMANTICS", StringComparison.OrdinalIgnoreCase))
            {
                context.MoveNextRequired();
            }

            columnSpecs.Add((columnName, typeColumnName, languageId));

            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                break;
            }
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }

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

        // Optional WITH (option [, ...]) — parse-and-discard balanced parens.
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

        if (table.FullTextIndex is not null)
            throw SimulatedSqlException.ThereIsAlreadyAnObject($"FULLTEXT INDEX ON {tableName}");

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

        // Resolve each column ordinal against the table's storage schema.
        var columns = new List<FullTextIndexColumn>(columnSpecs.Count);
        foreach (var (colName, typeColName, languageId) in columnSpecs)
        {
            var ordinal = ResolveColumnOrdinalForFullText(context.Batch.CurrentDatabase.Collation, table, colName);
            int? typeColumnId = null;
            if (typeColName is not null)
                typeColumnId = ResolveColumnOrdinalForFullText(context.Batch.CurrentDatabase.Collation, table, typeColName);
            columns.Add(new FullTextIndexColumn(ordinal, languageId, typeColumnId));
        }

        table.FullTextIndex = new FullTextIndex(catalog.Id, keyIndexName ?? string.Empty, uniqueIndexId, columns);
        return true;
    }

    private static int ResolveColumnOrdinalForFullText(Collation collation, HeapTable table, string columnName)
    {
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (collation.Equals(table.Columns[i].Name, columnName))
                return i + 1;
        }
        throw SimulatedSqlException.InvalidColumnName(columnName);
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

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableName);
        if (table.FullTextIndex is null)
            throw SimulatedSqlException.InvalidObjectName(tableName);
        table.FullTextIndex = null;
        return true;
    }

}
