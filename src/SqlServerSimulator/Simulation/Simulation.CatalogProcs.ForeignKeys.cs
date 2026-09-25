using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    // sp_fkeys: the 14-column ODBC SQLForeignKeys result set — eight sysname
    // name columns, KEY_SEQ / UPDATE_RULE / DELETE_RULE smallint, FK_NAME /
    // PK_NAME sysname, DEFERRABILITY smallint (probed 2026-09-25).
    private static readonly SqlType[] SpFkeysSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName,
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName,
        SqlType.SmallInt, SqlType.SmallInt, SqlType.SmallInt,
        SqlType.SystemName, SqlType.SystemName, SqlType.SmallInt,
    ];

    private static readonly string[] SpFkeysColumnNames =
    [
        "PKTABLE_QUALIFIER", "PKTABLE_OWNER", "PKTABLE_NAME", "PKCOLUMN_NAME",
        "FKTABLE_QUALIFIER", "FKTABLE_OWNER", "FKTABLE_NAME", "FKCOLUMN_NAME",
        "KEY_SEQ", "UPDATE_RULE", "DELETE_RULE", "FK_NAME", "PK_NAME", "DEFERRABILITY",
    ];

    /// <summary>
    /// Handles <c>EXEC sp_fkeys [@pktable_name] [, @pktable_owner]
    /// [, @pktable_qualifier] [, @fktable_name] [, @fktable_owner]
    /// [, @fktable_qualifier]</c> — the proc ODBC's <c>SQLForeignKeys</c> and
    /// JDBC's <c>getImportedKeys</c> / <c>getExportedKeys</c> call: one row per
    /// column of each foreign key between the named tables.
    /// </summary>
    /// <remarks>
    /// Real's own behavior, probed 2026-09-25 against SQL Server 2025. Names
    /// are exact (no wildcards); a missing owner, or an empty one, means the
    /// default schema; a name naming no table matches nothing. Neither table
    /// name is Msg 15252, a qualifier other than the current database Msg 15250.
    /// Given a primary-key table the rows sort by foreign-key table, then
    /// <c>KEY_SEQ</c>, then column, and the rules map <c>sys.foreign_keys</c>'
    /// action codes to ODBC's by swapping CASCADE (0) and NO ACTION (1) and
    /// passing SET NULL (2) and SET DEFAULT (3) through. Given only a
    /// foreign-key table they sort by primary-key table instead, and a rule
    /// reads 0 for CASCADE and 1 for anything else — so the same foreign key
    /// reports <c>ON UPDATE SET NULL</c> as 2 one way and 1 the other.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpFkeys(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (pkName, pkOwner, pkQualifier, fkName, fkOwner, fkQualifier) = ParseSpFkeysArgs(arguments);
        if (pkName is null && fkName is null)
            throw SimulatedSqlException.ForeignKeyTableNameRequired();

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        if ((fkQualifier is not null && !collation.Equals(fkQualifier, database.Name))
            || (pkQualifier is not null && !collation.Equals(pkQualifier, database.Name)))
        {
            throw SimulatedSqlException.HelpObjectNotInCurrentDatabase();
        }

        var pkTable = pkName is null ? null : ResolveSpFkeysTable(batch, pkOwner, pkName);
        var fkTable = fkName is null ? null : ResolveSpFkeysTable(batch, fkOwner, fkName);
        var byPrimaryKey = pkName is not null;

        var rows = new List<SqlValue[]>();
        // A named table that doesn't exist matches nothing.
        if ((pkName is null || pkTable is not null) && (fkName is null || fkTable is not null))
        {
            var qualifier = SqlValue.FromSystemName(database.Name);
            var candidates = byPrimaryKey ? pkTable!.IncomingForeignKeys : fkTable!.OutgoingForeignKeys;
            foreach (var fk in candidates)
            {
                if (byPrimaryKey && fkTable is not null && !ReferenceEquals(fk.ChildTable, fkTable))
                    continue;

                var referenced = fk.ReferencedTable;
                var child = fk.ChildTable;
                var keyIndexId = BuiltInResources.ResolveForeignKeyIndexId(fk);
                var keyName = referenced.IndexIdentities().Find(identity => identity.IndexId == keyIndexId).Name;
                var pkOwnerValue = SqlValue.FromSystemName(SchemaNameOf(database, referenced));
                var pkNameValue = SqlValue.FromSystemName(referenced.Name);
                var fkOwnerValue = SqlValue.FromSystemName(SchemaNameOf(database, child));
                var fkNameValue = SqlValue.FromSystemName(child.Name);
                var updateRule = SqlValue.FromInt16(SpFkeysRule(fk.UpdateAction, byPrimaryKey));
                var deleteRule = SqlValue.FromInt16(SpFkeysRule(fk.DeleteAction, byPrimaryKey));
                var constraintName = SqlValue.FromSystemName(fk.Name);
                var keyNameValue = keyName is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(keyName);
                for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
                {
                    rows.Add([
                        qualifier, pkOwnerValue, pkNameValue,
                        SqlValue.FromSystemName(referenced.Columns[fk.ReferencedColumnOrdinals[i]].Name),
                        qualifier, fkOwnerValue, fkNameValue,
                        SqlValue.FromSystemName(child.Columns[fk.ChildColumnOrdinals[i]].Name),
                        SqlValue.FromInt16((short)(i + 1)),
                        updateRule, deleteRule, constraintName, keyNameValue,
                        SqlValue.FromInt16(7),
                    ]);
                }
            }
        }

        // Pk mode orders by FKTABLE_OWNER, FKTABLE_NAME, KEY_SEQ,
        // FKCOLUMN_NAME; fk mode by the PKTABLE / PKCOLUMN counterparts. The
        // qualifier column is the current database on every row.
        var ownerSlot = byPrimaryKey ? 5 : 1;
        rows.Sort((a, b) =>
        {
            var byOwner = collation.Compare(a[ownerSlot].AsString, b[ownerSlot].AsString);
            if (byOwner != 0)
                return byOwner;
            var byTable = collation.Compare(a[ownerSlot + 1].AsString, b[ownerSlot + 1].AsString);
            if (byTable != 0)
                return byTable;
            var bySequence = a[8].AsInt16.CompareTo(b[8].AsInt16);
            return bySequence != 0 ? bySequence : collation.Compare(a[ownerSlot + 2].AsString, b[ownerSlot + 2].AsString);
        });

        yield return new SimulatedSqlResultSet(SpFkeysSchema, SpFkeysColumnNames, rows);
    }

    private static short SpFkeysRule(ReferentialAction action, bool byPrimaryKey) => action switch
    {
        ReferentialAction.Cascade => 0,
        ReferentialAction.NoAction => 1,
        _ => byPrimaryKey ? (short)action : (short)1,
    };

    /// <summary>
    /// The table sp_fkeys' <c>OBJECT_ID(QUOTENAME(owner) + '.' + QUOTENAME(name))</c>
    /// lands on — the default schema when the owner is missing or empty — or
    /// null when that names no table.
    /// </summary>
    private static HeapTable? ResolveSpFkeysTable(BatchContext batch, string? owner, string name)
    {
        var parsed = string.IsNullOrEmpty(owner) ? new MultiPartName(name) : new MultiPartName(owner).WithAddedPart(name);
        return batch.TryResolveTable(parsed, out var table) && !table.IsTableVariable ? table : null;
    }

    private static (string? PkName, string? PkOwner, string? PkQualifier, string? FkName, string? FkOwner, string? FkQualifier) ParseSpFkeysArgs(
        List<ProcArgument> arguments)
    {
        var values = new string?[6];
        var positional = 0;
        foreach (var arg in arguments)
        {
            var slot = arg.Name switch
            {
                null => positional++,
                var n when BuiltInToken.Equals(n, "pktable_name") => 0,
                var n when BuiltInToken.Equals(n, "pktable_owner") => 1,
                var n when BuiltInToken.Equals(n, "pktable_qualifier") => 2,
                var n when BuiltInToken.Equals(n, "fktable_name") => 3,
                var n when BuiltInToken.Equals(n, "fktable_owner") => 4,
                var n when BuiltInToken.Equals(n, "fktable_qualifier") => 5,
                _ => -1,
            };
            if (slot is < 0 or > 5)
                throw SimulatedSqlException.InvalidProcedureParameters("sp_fkeys");
            values[slot] = CatalogStringArg(arg);
        }

        return (values[0], values[1], values[2], values[3], values[4], values[5]);
    }
}
