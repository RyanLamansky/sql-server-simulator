using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The full-text system procedures: the deprecated sp_help_fulltext_* reports,
// sp_fulltext_database and sp_fulltext_load_thesaurus_file. Each error a
// procedure raises from its own body carries the line real's raises it from
// (probed 2026-10-06 against SQL Server 2025).
partial class Simulation
{
    private static readonly SystemProcedureParameter[] HelpFullTextCatalogsParameters =
    [
        new("fulltext_catalog_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] HelpFullTextTablesParameters =
    [
        new("fulltext_catalog_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_name", SqlType.NVarchar, 517, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] HelpFullTextColumnsParameters =
    [
        new("table_name", SqlType.NVarchar, 517, SqlValue.Null(SqlType.NVarchar)),
        new("column_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] HelpFullTextSystemComponentsParameters =
    [
        new("component_type", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("param", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] FullTextDatabaseParameters =
    [
        new("action", SqlType.Varchar, 20),
    ];

    private static readonly SystemProcedureParameter[] LoadThesaurusFileParameters =
    [
        new("lcid", SqlType.Int32),
        new("loadOnlyIfNotLoaded", SqlType.Bit, 0, SqlValue.FromBoolean(false)),
    ];

    /// <summary>
    /// The LCIDs whose thesaurus file a stock instance ships: every full-text language but Finnish, Hungarian,
    /// Estonian, Norwegian, Bangla and Serbian (Sr-Latin), whose load is error 30050 like any LCID with no language.
    /// </summary>
    private static readonly int[] LcidsWithoutThesaurus = [1035, 1038, 1061, 2068, 2117, 9242];

    private static readonly SqlType[] HelpFullTextCatalogsSchema = [SqlType.Int32, SqlType.NVarchar, SqlType.NVarchar, SqlType.Int32, SqlType.Int32];
    private static readonly string[] HelpFullTextCatalogsColumnNames = ["ftcatid", "NAME", "PATH", "STATUS", "NUMBER_FULLTEXT_TABLES"];

    private static readonly SqlType[] HelpFullTextTablesSchema = [SqlType.NVarchar, SqlType.NVarchar, SqlType.NVarchar, SqlType.Int32, SqlType.Bit, SqlType.NVarchar];
    private static readonly string[] HelpFullTextTablesColumnNames = ["TABLE_OWNER", "TABLE_NAME", "FULLTEXT_KEY_INDEX_NAME", "FULLTEXT_KEY_COLID", "FULLTEXT_INDEX_ACTIVE", "FULLTEXT_CATALOG_NAME"];

    private static readonly SqlType[] HelpFullTextColumnsSchema = [SqlType.NVarchar, SqlType.Int32, SqlType.NVarchar, SqlType.NVarchar, SqlType.Int32, SqlType.NVarchar, SqlType.Int32, SqlType.Int32];
    private static readonly string[] HelpFullTextColumnsColumnNames = ["TABLE_OWNER", "TABLE_ID", "TABLE_NAME", "FULLTEXT_COLUMN_NAME", "FULLTEXT_COLID", "FULLTEXT_BLOBTP_COLNAME", "FULLTEXT_BLOBTP_COLID", "FULLTEXT_LANGUAGE"];

    private static readonly SqlType[] HelpFullTextComponentsSchema = [SqlType.NVarchar, SqlType.NVarchar, SqlType.UniqueIdentifier, SqlType.NVarchar, SqlType.NVarchar, SqlType.NVarchar];
    private static readonly string[] HelpFullTextComponentsColumnNames = ["componenttype", "componentname", "clsid", "fullpath", "version", "manufacturer"];

    private static readonly SqlType[] HelpFullTextComponentUsersSchema = [SqlType.Int32, SqlType.Int32];
    private static readonly string[] HelpFullTextComponentUsersColumnNames = ["dbid", "ftcatid"];

    /// <summary>
    /// <c>sp_help_fulltext_catalogs [@fulltext_catalog_name]</c>: each catalog of the current database, or the one
    /// named, with its population status and how many tables it indexes.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpFullTextCatalogs(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_help_fulltext_catalogs", calledAs, arguments, HelpFullTextCatalogsParameters);
        var database = RequireFullTextEnabled(batch, calledAs, line: 7);
        var only = values[0].IsNull ? null : FindFullTextCatalog(batch, calledAs, values[0].AsString, line: 22);

        var rows = new List<SqlValue[]>();
        foreach (var catalog in database.FullTextCatalogs.EnumerateValues().OrderBy(static catalog => catalog.Id))
        {
            if (only is not null && catalog != only)
                continue;
            var tables = 0;
            foreach (var (_, table) in FullTextIndexedTables(database))
            {
                if (table.FullTextIndex!.CatalogId == catalog.Id)
                    tables++;
            }
            rows.Add([
                SqlValue.FromInt32(catalog.Id),
                SqlValue.FromSystemName(catalog.Name),
                SqlValue.Null(SqlType.NVarchar),
                SqlValue.FromInt32(0),
                SqlValue.FromInt32(tables),
            ]);
        }
        yield return new SimulatedSqlResultSet(HelpFullTextCatalogsSchema, HelpFullTextCatalogsColumnNames, rows);
    }

    /// <summary>
    /// <c>sp_help_fulltext_tables [@fulltext_catalog_name] [, @table_name]</c>: each full-text indexed table of the
    /// current database, narrowed to one catalog's and to one table.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpFullTextTables(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_help_fulltext_tables", calledAs, arguments, HelpFullTextTablesParameters);
        var database = RequireFullTextEnabled(batch, calledAs, line: 8);
        var catalog = values[0].IsNull ? null : FindFullTextCatalog(batch, calledAs, values[0].AsString, line: 25);
        var (only, elsewhere) = values[1].IsNull ? (null, false) : FindFullTextHelpTable(batch, calledAs, values[1].AsString, missingLine: 38, viewLine: 45);

        var rows = new List<SqlValue[]>();
        if (!elsewhere)
        {
            foreach (var (schemaName, table) in FullTextIndexedTables(database))
            {
                var index = table.FullTextIndex!;
                if ((only is not null && table != only) || (catalog is not null && index.CatalogId != catalog.Id))
                    continue;
                rows.Add([
                    SqlValue.FromSystemName(schemaName),
                    SqlValue.FromSystemName(table.Name),
                    SqlValue.FromSystemName(index.KeyIndexName),
                    SqlValue.FromInt32(Parser.Expressions.ObjectProperty.FullTextKeyColumnId(table, index)),
                    SqlValue.FromBoolean(true),
                    CatalogNameValue(database, index.CatalogId),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(HelpFullTextTablesSchema, HelpFullTextTablesColumnNames, rows);
    }

    /// <summary>
    /// <c>sp_help_fulltext_columns [@table_name] [, @column_name]</c>: each full-text indexed column of the current
    /// database, with its language and any <c>TYPE COLUMN</c>, narrowed to one table and to one column.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpFullTextColumns(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_help_fulltext_columns", calledAs, arguments, HelpFullTextColumnsParameters);
        var database = RequireFullTextEnabled(batch, calledAs, line: 8);
        var (only, elsewhere) = values[0].IsNull ? (null, false) : FindFullTextHelpTable(batch, calledAs, values[0].AsString, missingLine: 22, viewLine: 29);
        var columnName = values[1].IsNull ? null : values[1].AsString;
        if (only is not null && columnName is not null && !Array.Exists(only.Columns, column => database.Collation.Equals(column.Name, columnName)))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NoTableWithColumn(values[0].AsString, columnName), 40);

        var rows = new List<SqlValue[]>();
        if (!elsewhere)
        {
            foreach (var (schemaName, table) in FullTextIndexedTables(database))
            {
                if (only is not null && table != only)
                    continue;
                foreach (var indexed in table.FullTextIndex!.Columns)
                {
                    var column = Array.Find(table.Columns, candidate => candidate.ColumnId == indexed.ColumnId)!;
                    if (columnName is not null && !database.Collation.Equals(column.Name, columnName))
                        continue;
                    var typeColumn = indexed.TypeColumnId is int typeColumnId ? Array.Find(table.Columns, candidate => candidate.ColumnId == typeColumnId) : null;
                    rows.Add([
                        SqlValue.FromSystemName(schemaName),
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromSystemName(table.Name),
                        SqlValue.FromSystemName(column.Name),
                        SqlValue.FromInt32(column.ColumnId),
                        typeColumn is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromSystemName(typeColumn.Name),
                        typeColumn is null ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(typeColumn.ColumnId),
                        SqlValue.FromInt32(indexed.LanguageId),
                    ]);
                }
            }
        }
        yield return new SimulatedSqlResultSet(HelpFullTextColumnsSchema, HelpFullTextColumnsColumnNames, rows);
    }

    /// <summary>
    /// <c>sp_help_fulltext_system_components { 'all' | 'wordbreaker' | 'filter' | 'protocol handler' } [, @param]</c>:
    /// the registered word breakers (one per full-text language) and document filters. A <c>@param</c> narrows the
    /// list to one LCID or document type and adds the catalogs using that component, which real lists none of.
    /// Any other type, none, or <c>'all'</c> with a <c>@param</c> is Msg 15600.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpFullTextSystemComponents(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_help_fulltext_system_components";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, HelpFullTextSystemComponentsParameters);
        var type = values[0].IsNull ? null : values[0].AsString.TrimEnd(' ');
        var all = type is not null && BuiltInToken.Equals(type, "ALL");
        var wordBreakers = all || (type is not null && BuiltInToken.Equals(type, "WORDBREAKER"));
        var filters = all || (type is not null && BuiltInToken.Equals(type, "FILTER"));
        if (!wordBreakers && !filters && (type is null || !BuiltInToken.Equals(type, "PROTOCOL HANDLER")))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidSystemProcedureOption(procedure), 9, 15600);
        var param = values[1].IsNull ? null : values[1].AsString.TrimEnd(' ');
        if (all && param is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidSystemProcedureOption(procedure), 26, 15600);

        var manufacturer = SqlValue.FromNVarchar("Microsoft Corporation");
        var rows = new List<SqlValue[]>();
        if (filters)
        {
            foreach (var (documentType, classId, path, version) in BuiltInResources.FullTextDocumentTypes)
            {
                if (param is null || BuiltInToken.Equals(documentType, param))
                    rows.Add([SqlValue.FromNVarchar("filter"), SqlValue.FromNVarchar(documentType), SqlValue.FromGuid(classId), SqlValue.FromNVarchar(path), SqlValue.FromNVarchar(version), manufacturer]);
            }
        }
        if (wordBreakers)
        {
            // Listed by the LCID as text, so 1025 comes before 2052 and 0 first.
            var languages = BuiltInResources.FullTextLanguages.ToArray();
            Array.Sort(languages, static (left, right) => string.CompareOrdinal(LcidText(left.Lcid), LcidText(right.Lcid)));
            foreach (var (lcid, _, wordBreaker, path) in languages)
            {
                var lcidText = LcidText(lcid);
                if (param is null || lcidText.Equals(param, StringComparison.Ordinal))
                    rows.Add([SqlValue.FromNVarchar("wordbreaker"), SqlValue.FromNVarchar(lcidText), SqlValue.FromGuid(wordBreaker), SqlValue.FromNVarchar(path), SqlValue.FromNVarchar("16.0.5306.1000"), manufacturer]);
            }
        }
        // Real gathers the list into a table first, whose INSERT reports its count ahead of the rows.
        yield return new SimulatedNonQuery(rows.Count)
        {
            DoneKind = StatementDoneKind.Insert,
            InModule = true,
            CountSuppressed = batch.Connection.NoCount,
        };
        yield return new SimulatedSqlResultSet(HelpFullTextComponentsSchema, HelpFullTextComponentsColumnNames, rows);
        if (param is not null)
            yield return new SimulatedSqlResultSet(HelpFullTextComponentUsersSchema, HelpFullTextComponentUsersColumnNames, []);
    }

    /// <summary>
    /// <c>sp_fulltext_database { 'enable' | 'disable' }</c>: turns the current database's full-text flag on or off.
    /// Refused in master, tempdb and model (Msg 9966), for another action (Msg 15600) and inside a transaction
    /// (Msg 15002), in that order.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpFullTextDatabase(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_fulltext_database";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, FullTextDatabaseParameters);
        var database = batch.CurrentDatabase;
        if (BuiltInToken.EqualsAny(database.Name, "master", "model", "tempdb"))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.FullTextInSystemDatabase(), 33);
        var action = values[0].IsNull ? null : values[0].AsString.TrimEnd(' ');
        var enable = action is not null && BuiltInToken.Equals(action, "ENABLE");
        if (!enable && (action is null || !BuiltInToken.Equals(action, "DISABLE")))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidSystemProcedureOption(procedure), 40, 15600);
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction(procedure), 48);
        database.FullTextDisabled = !enable;
    }

    /// <summary>
    /// <c>sp_fulltext_load_thesaurus_file @lcid [, @loadOnlyIfNotLoaded]</c>: (re)loads a language's thesaurus. Every
    /// thesaurus file a stock instance ships holds no entries, so a load changes nothing a search sees; an LCID with
    /// no file, NULL included, is error 30050, which real rethrows as Msg 50000 from
    /// <c>sys.sp_fulltext_rethrow_error</c> with return code 30050. Refused inside a transaction (Msg 15002).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpFullTextLoadThesaurusFile(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_fulltext_load_thesaurus_file";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, LoadThesaurusFileParameters);
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction(procedure), 23);
        var lcid = values[0].IsNull ? (int?)null : values[0].AsInt32;
        if (lcid is not { } known || Array.IndexOf(LcidsWithoutThesaurus, known) >= 0 || !Array.Exists(BuiltInResources.FullTextLanguages, language => language.Lcid == known))
        {
            var error = SimulatedSqlException.ThesaurusNotLoaded(lcid is { } written ? LcidText(written) : "(null)");
            error.SystemProcedureReturnCode = 30050;
            throw AtProcedureLine(error, "sys.sp_fulltext_rethrow_error", 36);
        }
    }

    private static string LcidText(int lcid) => lcid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The current database, or Msg 15601 from <paramref name="line"/> when its full-text flag is off — master, model and tempdb's always is.</summary>
    private static Database RequireFullTextEnabled(BatchContext batch, string calledAs, int line) =>
        BuiltInResources.ReportsFullTextEnabled(batch.CurrentDatabase)
            ? batch.CurrentDatabase
            : throw AtSystemProcedureLine(calledAs, SimulatedSqlException.FullTextNotEnabledForDatabase(), line);

    /// <summary>
    /// The catalog a help procedure names, or Msg 7641 from <paramref name="line"/> — whose database slot real
    /// fills with the first character of the database's name.
    /// </summary>
    private static FullTextCatalog FindFullTextCatalog(BatchContext batch, string calledAs, string name, int line)
    {
        var database = batch.CurrentDatabase;
        foreach (var catalog in database.FullTextCatalogs.EnumerateValues())
        {
            if (database.Collation.Equals(catalog.Name, name))
                return catalog;
        }
        throw AtSystemProcedureLine(calledAs, SimulatedSqlException.FullTextCatalogNotFoundOrDenied(name, database.Name[..Math.Min(1, database.Name.Length)], state: 1), line);
    }

    /// <summary>
    /// The table a help procedure names: a table of the current database, or <c>Elsewhere</c> for an object of
    /// another database, which lists nothing. A name that resolves to nothing, or to a system object, is Msg 15009
    /// from <paramref name="missingLine"/>; a view is Msg 15218 from <paramref name="viewLine"/>.
    /// </summary>
    private static (HeapTable? Table, bool Elsewhere) FindFullTextHelpTable(BatchContext batch, string calledAs, string written, int missingLine, int viewLine)
    {
        var database = batch.CurrentDatabase;
        var missing = AtSystemProcedureLine(calledAs, SimulatedSqlException.HelpObjectDoesNotExist(written, database.Name), missingLine);
        if (SplitDottedName(written) is not { Length: <= 3 } parts)
            throw missing;
        if (parts.Length == 3 && parts[0].Length > 0 && !database.Collation.Equals(parts[0], database.Name))
        {
            var schemaName = parts[1].Length == 0 ? Database.DefaultSchemaName : parts[1];
            return batch.Connection.Simulation.Databases.TryGetValue(parts[0], out var other)
                && other.Schemas.TryGetValue(schemaName, out var otherSchema)
                && (otherSchema.HeapTables.ContainsKey(parts[2]) || otherSchema.Views.ContainsKey(parts[2]))
                ? (null, true)
                : throw missing;
        }
        if (parts.Length == 3)
            parts = [parts[1].Length == 0 ? Database.DefaultSchemaName : parts[1], parts[2]];
        if (FindUserTable(batch, parts) is { Table: var table })
            return (table, false);
        var (schema, leaf) = parts.Length == 1 ? (Database.DefaultSchemaName, parts[0]) : (parts[0], parts[1]);
        return database.Schemas.TryGetValue(schema, out var viewSchema) && viewSchema.Views.ContainsKey(leaf)
            ? throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ObjectIsNotATable(written), viewLine)
            : throw missing;
    }

    /// <summary>The current database's full-text indexed tables with their schemas' names, in object id order.</summary>
    private static List<(string SchemaName, HeapTable Table)> FullTextIndexedTables(Database database)
    {
        var tables = new List<(string SchemaName, HeapTable Table)>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.FullTextIndex is not null)
                    tables.Add((schema.Name, table));
            }
        }
        tables.Sort(static (left, right) => left.Table.ObjectId.CompareTo(right.Table.ObjectId));
        return tables;
    }

    private static SqlValue CatalogNameValue(Database database, int catalogId)
    {
        foreach (var catalog in database.FullTextCatalogs.EnumerateValues())
        {
            if (catalog.Id == catalogId)
                return SqlValue.FromSystemName(catalog.Name);
        }
        return SqlValue.Null(SqlType.NVarchar);
    }
}
