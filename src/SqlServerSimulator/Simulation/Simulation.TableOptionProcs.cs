using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_tableoption / sp_indexoption — the pre-ALTER spellings of a few table and
// index options. Both refuse to run inside a transaction and both return the
// number of the error that ended them.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] TableOptionParameters =
    [
        new("TableNamePattern", SqlType.NVarchar, 776),
        new("OptionName", SqlType.NVarchar, 35),
        new("OptionValue", SqlType.NVarchar, 12),
    ];

    private static readonly SystemProcedureParameter[] IndexOptionParameters =
    [
        new("IndexNamePattern", SqlType.NVarchar, 1035),
        new("OptionName", SqlType.NVarchar, 35),
        new("OptionValue", SqlType.NVarchar, 12),
    ];

    /// <summary>
    /// Reads a switch value the way both option procedures take one — ON, OFF,
    /// TRUE, FALSE, YES, NO, 1 or 0, in any case with trailing blanks — and
    /// null for anything else.
    /// </summary>
    private static bool? ReadOptionSwitch(SqlValue value)
    {
        if (value.IsNull)
            return null;
        var text = value.AsString.TrimEnd(' ');
        foreach (var on in (ReadOnlySpan<string>)["1", "ON", "TRUE", "YES"])
        {
            if (text.Equals(on, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var off in (ReadOnlySpan<string>)["0", "OFF", "FALSE", "NO"])
        {
            if (text.Equals(off, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return null;
    }

    /// <summary>
    /// The user table a <c>sp_tableoption</c> / <c>sp_indexoption</c> name
    /// stands for — <c>table</c>, <c>schema.table</c> or
    /// <c>database.schema.table</c> of the current database, its schema
    /// defaulting to <c>dbo</c> — with the schema's name; null when no base
    /// table answers (a view, a temporary table, a wildcard and another
    /// database's table included).
    /// </summary>
    private static (HeapTable Table, string SchemaName)? FindUserTable(BatchContext batch, string[] parts)
    {
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        string schemaName, tableName;
        switch (parts.Length)
        {
            case 1:
                (schemaName, tableName) = (Database.DefaultSchemaName, parts[0]);
                break;
            case 2:
                (schemaName, tableName) = (parts[0], parts[1]);
                break;
            case 3:
                if (!collation.Equals(parts[0], database.Name))
                    return null;
                (schemaName, tableName) = (parts[1], parts[2]);
                break;
            default:
                return null;
        }
        return database.Schemas.TryGetValue(schemaName, out var schema) && schema.HeapTables.TryGetValue(tableName, out var table) && CanSeeTableMetadata(batch, table)
            ? (table, schema.Name)
            : null;
    }

    /// <summary>
    /// Splits a written name into its dot-separated parts, reading a
    /// <c>[bracketed]</c> or <c>"quoted"</c> part whole so a dot inside it stays;
    /// null when the text is empty, unterminated or has more than four parts.
    /// </summary>
    private static string[]? SplitDottedName(string text)
    {
        var parts = new List<string>();
        var position = 0;
        while (true)
        {
            while (position < text.Length && text[position] == ' ')
                position++;
            if (position >= text.Length)
                return null;
            string part;
            if (text[position] is '[' or '"')
            {
                var close = text[position] == '[' ? ']' : '"';
                var builder = new StringBuilder();
                position++;
                while (true)
                {
                    if (position >= text.Length)
                        return null;
                    if (text[position] == close)
                    {
                        if (position + 1 < text.Length && text[position + 1] == close)
                        {
                            _ = builder.Append(close);
                            position += 2;
                            continue;
                        }
                        position++;
                        break;
                    }
                    _ = builder.Append(text[position++]);
                }
                part = builder.ToString();
                while (position < text.Length && text[position] == ' ')
                    position++;
            }
            else
            {
                var end = text.IndexOf('.', position);
                if (end < 0)
                    end = text.Length;
                part = text[position..end].TrimEnd();
                position = end;
            }
            if (part.Length == 0)
                return null;
            parts.Add(part);
            if (position >= text.Length)
                return parts.Count > 4 ? null : [.. parts];
            if (text[position] != '.')
                return null;
            position++;
        }
    }

    private static string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>
    /// <c>sp_tableoption @TableNamePattern, @OptionName, @OptionValue</c> sets
    /// one of a table's storage options: <c>table lock on bulk load</c>,
    /// <c>text in row</c> (ON is 256, OFF 0, or a size from 24 through 7000),
    /// <c>large value types out of row</c>, and <c>pintable</c> and
    /// <c>vardecimal storage format</c>, which are accepted and do nothing. The
    /// checks run in real's order, each from its own line of the procedure: the
    /// transaction, the option and its value, the table, then a <c>text in
    /// row</c> table without a text column, turning it off included (probed
    /// 2026-10-07 against SQL Server 2025). The catalog reports the options; the storage of a
    /// <c>text</c> or LOB value doesn't follow them.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTableOption(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_tableoption";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, TableOptionParameters);
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction(procedure), 34);

        var option = values[1].IsNull ? "" : values[1].AsString.TrimEnd(' ').ToUpperInvariant();
        var invalid = SimulatedSqlException.InvalidSystemProcedureOption(procedure);
        if (option is not ("TABLE LOCK ON BULK LOAD" or "TEXT IN ROW" or "LARGE VALUE TYPES OUT OF ROW" or "PINTABLE" or "VARDECIMAL STORAGE FORMAT"))
            throw AtSystemProcedureLine(calledAs, invalid, 50, 15600);

        var toggle = ReadOptionSwitch(values[2]);
        var textInRow = 0;
        if (option == "TEXT IN ROW")
        {
            var text = values[2].IsNull ? "" : values[2].AsString.TrimEnd(' ');
            if (toggle is not null)
                textInRow = toggle.Value ? 256 : 0;
            else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                textInRow = size is >= 24 and <= 7000 ? size : throw AtSystemProcedureLine(calledAs, SimulatedSqlException.TextInRowValueInvalid(), 63, 15112);
            else
                throw AtSystemProcedureLine(calledAs, invalid, 50, 15600);
        }
        else if (toggle is null)
        {
            throw AtSystemProcedureLine(calledAs, invalid, 50, 15600);
        }

        var written = values[0].IsNull ? "(null)" : values[0].AsString;
        var parts = values[0].IsNull ? null : SplitDottedName(written);
        var (table, _) = (parts is null ? null : FindUserTable(batch, parts))
            ?? throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NoUserTableMatching(written), 92, 15388);
        if (option == "TEXT IN ROW" && !Array.Exists(table.Columns, static column => column.Type is TextSqlType or NTextSqlType or ImageSqlType))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.CannotSwitchToInRowText(table.Name), 102, 2599);

        switch (option)
        {
            case "LARGE VALUE TYPES OUT OF ROW":
                table.LargeValueTypesOutOfRow = toggle!.Value;
                break;
            case "TABLE LOCK ON BULK LOAD":
                table.LockOnBulkLoad = toggle!.Value;
                break;
            case "TEXT IN ROW":
                table.TextInRowLimit = textInRow;
                break;
        }
        yield break;
    }

    /// <summary>
    /// <c>sp_indexoption @IndexNamePattern, @OptionName, @OptionValue</c> moves
    /// one locking option of an index — <c>AllowRowLocks</c>,
    /// <c>AllowPageLocks</c> or their <c>DisAllow</c> negations — or of every
    /// index of a table, its heap included. Real runs an <c>ALTER INDEX … SET</c>
    /// for it, so whatever that statement refuses (a columnstore index's locking
    /// options, a disabled index) comes back from it at line 1 in no procedure.
    /// The name is <c>table</c>, <c>schema.table</c>, <c>table.index</c>,
    /// <c>schema.table.index</c> or <c>database.schema.table.index</c>; the
    /// two-part form is the schema-qualified table first.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpIndexOption(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_indexoption";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, IndexOptionParameters);
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction(procedure), 22);

        var option = values[1].IsNull ? "" : values[1].AsString.TrimEnd(' ').ToUpperInvariant();
        var toggle = ReadOptionSwitch(values[2]);
        if (option is not ("ALLOWROWLOCKS" or "ALLOWPAGELOCKS" or "DISALLOWROWLOCKS" or "DISALLOWPAGELOCKS") || toggle is null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidSystemProcedureOption(procedure), 43, 15600);
        var allow = option.StartsWith("DIS", StringComparison.Ordinal) ? !toggle.Value : toggle.Value;
        var rowLocks = option.EndsWith("ROWLOCKS", StringComparison.Ordinal);

        var written = values[0].IsNull ? "(null)" : values[0].AsString;
        var (table, schemaName, indexName) = FindIndexOptionTarget(batch, values[0].IsNull ? null : SplitDottedName(written))
            ?? throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NoUserTableMatching(written), 128, 15388);

        var target = indexName is null ? "all" : QuoteIdentifier(indexName);
        var statement = $"alter index {target} on {QuoteIdentifier(schemaName)}.{QuoteIdentifier(table.Name)} set ({(rowLocks ? "allow_row_locks" : "allow_page_locks")} = {(allow ? "on" : "off")})";
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }

    private static (HeapTable Table, string SchemaName, string? IndexName)? FindIndexOptionTarget(BatchContext batch, string[]? parts)
    {
        if (parts is null)
            return null;
        var database = batch.CurrentDatabase;
        var collation = database.Collation;

        // The reading that names a table alone, then the one that ends in an index.
        if (parts.Length <= 3 && FindUserTable(batch, parts) is { } whole)
            return (whole.Table, whole.SchemaName, null);
        if (parts.Length is < 2 or > 4)
            return null;
        var (tableParts, index) = (parts[..^1], parts[^1]);
        if (FindUserTable(batch, tableParts) is not { } found)
            return null;
        var (table, schemaName) = found;
        var known = table.KeyConstraints.Exists(key => collation.Equals(key.Name, index)) || table.Indexes.Exists(candidate => collation.Equals(candidate.Name, index));
        return known ? (table, schemaName, index) : null;
    }
}
