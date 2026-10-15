using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_changeobjectowner and sp_procoption: the legacy procedures that move an
// object to the schema named for its new owner and mark a master procedure to
// run at startup. Each check and its source line are real's procedure's
// (probed 2026-10-15 against SQL Server 2025).
partial class Simulation
{
    private static readonly SystemProcedureParameter[] ChangeObjectOwnerParameters =
    [
        new("objname", SqlType.NVarchar, 776),
        new("newowner", SqlType.NVarchar, 128),
    ];

    private static readonly SystemProcedureParameter[] ProcOptionParameters =
    [
        new("ProcName", SqlType.NVarchar, 776),
        new("OptionName", SqlType.NVarchar, 35),
        new("OptionValue", SqlType.NVarchar, 12),
    ];

    /// <summary><c>configuration_id</c> of <c>scan for startup procs</c>, which <c>sp_procoption</c> stages.</summary>
    private const int ScanForStartupProcsConfigurationId = 1547;

    /// <summary>
    /// <c>sp_changeobjectowner @objname, @newowner</c>: the pre-2005 spelling of
    /// <c>ALTER SCHEMA … TRANSFER</c>, moving an object into the schema named
    /// for its new owner. A caller outside <c>db_owner</c> and not in both
    /// <c>db_securityadmin</c> and <c>db_ddladmin</c> is Msg 15247; a name that
    /// doesn't parse Msg 15253; an object that is missing, three- or
    /// four-part, a trigger or constraint, a system object or referenced by a
    /// schema-bound module Msg 15001 from line 64, and one with an explicit
    /// owner or in a schema not named for its owner Msg 15001 from line 78; a
    /// new owner without a schema of its own name that it owns Msg 15411. The
    /// transfer runs as the statement real builds, so what it refuses comes
    /// back from it at line 1, and a success sends real's caution, Msg 15477.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpChangeObjectOwner(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_changeobjectowner", calledAs, arguments, ChangeObjectOwnerParameters);
        var database = batch.CurrentDatabase;
        if (!PermissionEnforcement.IsOwnerOrSecurityAndDdlAdmin(batch, database))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 22);

        var written = values[0].IsNull ? "(null)" : values[0].AsString;
        if (values[0].IsNull || !Parser.Expressions.ObjectId.TryParseObjectName(written, out var name) || name.Leaf.Length == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.HelpNameIsNotAnIdentifier(written), 32);
        if (name.Count > 2 || !batch.TryResolveCallerSchema(name, out var schema)
            || schema.SchemaId is Database.InformationSchemaId or Database.SysSchemaId
            || TransferableObject(schema, name.Leaf) is not { } target
            || SchemaBinding.FindReferencingModule(database, target) is not null)
        {
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ObjectNotValidForOperation(written), 64);
        }
        if (target.OwnerPrincipalId is not null || !SchemaNamedForItsOwner(database, schema))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ObjectNotValidForOperation(written), 78);

        var newOwner = values[1].IsNull ? null : values[1].AsString;
        if (newOwner is null || !database.Schemas.TryGetValue(newOwner, out var destination) || !SchemaNamedForItsOwner(database, destination))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PrincipalOrSchemaDoesNotExist(newOwner ?? "(null)"), 114);

        var source = name.Count == 2 ? QuoteIdentifier(name[0]) + "." : "";
        var failed = false;
        foreach (var outcome in this.RunStatements(batch, [$"alter schema {QuoteIdentifier(newOwner)} transfer {source}{QuoteIdentifier(name.Leaf)}"]))
        {
            failed |= outcome is SimulatedErrorOutcome;
            yield return outcome;
        }
        if (!failed)
            QueueProcedureMessage(batch, calledAs, 15477, RenameCautionMessage, 135);
    }

    /// <summary>
    /// The object <c>sp_changeobjectowner</c> may move: a table, view,
    /// function, procedure, sequence, synonym, default, rule or security
    /// policy, never a trigger or constraint, which belong to their parent.
    /// </summary>
    private static SchemaObject? TransferableObject(Schema schema, string leaf) =>
        schema.HeapTables.TryGetValue(leaf, out var table) ? table
        : schema.Views.TryGetValue(leaf, out var view) ? view
        : schema.Functions.TryGetValue(leaf, out var function) ? function
        : schema.Procedures.TryGetValue(leaf, out var procedure) ? procedure
        : schema.Sequences.TryGetValue(leaf, out var sequence) ? sequence
        : schema.Synonyms.TryGetValue(leaf, out var synonym) ? synonym
        : schema.Defaults.TryGetValue(leaf, out var defaultObject) ? defaultObject
        : schema.Rules.TryGetValue(leaf, out var rule) ? rule
        : schema.SecurityPolicies.TryGetValue(leaf, out var policy) ? policy
        : null;

    /// <summary>Whether <paramref name="schema"/> is owned by the principal it is named for, as a schema standing in for a pre-2005 owner is.</summary>
    private static bool SchemaNamedForItsOwner(Database database, Schema schema) =>
        Ownership.PrincipalName(database, Ownership.SchemaOwnerId(database, schema.SchemaId)) is { } ownerName
        && database.Collation.Equals(ownerName, schema.Name);

    /// <summary>
    /// <c>sp_procoption @ProcName, @OptionName, @OptionValue</c>: sets a
    /// procedure's <c>startup</c> flag, which <c>OBJECTPROPERTY(…,
    /// 'ExecIsStartup')</c> and <c>sys.procedures.is_auto_executed</c> read and
    /// an <c>ALTER PROCEDURE</c> keeps, and stages <c>scan for startup procs</c>
    /// as 1 while any startup procedure stands, installing the other staged
    /// options as its <c>RECONFIGURE</c> does. Every refusal returns its own
    /// number: a transaction is Msg 15002, an option other than
    /// <c>startup</c> / <c>failover</c> or a value other than
    /// <c>1</c>/<c>on</c>/<c>yes</c>/<c>true</c> or their negatives Msg 15600,
    /// a procedure that doesn't resolve or a login outside <c>sysadmin</c> Msg
    /// 15165, one outside <c>master</c>'s <c>dbo</c> schema Msg 15398, one
    /// declaring parameters Msg 15399, and <c>failover</c> Msg 40510. A
    /// simulation never restarts, so a startup procedure never runs.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpProcOption(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_procoption", calledAs, arguments, ProcOptionParameters);
        if (batch.Connection.CurrentTransaction is not null)
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction("sp_procoption"), 18);

        bool? flag = values[2].IsNull ? null
            : IsAnyOf(values[2].AsString, "1", "on", "yes", "true") ? true
            : IsAnyOf(values[2].AsString, "0", "off", "no", "false") ? false
            : null;
        var startup = !values[1].IsNull && DeviceNamesEqual(values[1].AsString, "startup");
        if (flag is not { } on || (!startup && (values[1].IsNull || !DeviceNamesEqual(values[1].AsString, "failover"))))
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.InvalidSystemProcedureOption("sp_procoption"), 30);

        var written = values[0].IsNull ? "(null)" : values[0].AsString;
        Procedure? procedure = null;
        if (!values[0].IsNull && Parser.Expressions.ObjectId.TryParseObjectName(written, out var name) && name.Count <= 2
            && batch.TryResolveCallerSchema(name, out var schema))
        {
            _ = schema.Procedures.TryGetValue(name.Leaf, out procedure);
        }
        var database = batch.CurrentDatabase;
        if (procedure is null || (PermissionEnforcement.ObjectVisibility(batch, database) is { } visible && !visible(procedure)))
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.CouldNotFindObjectOrNoPermission(written), 40);
        if (!SessionInServerRole(batch, SysadminRoleId))
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.CouldNotFindObjectOrNoPermission(written), 65);
        if (database.Id != 1 || procedure.SchemaId != Database.DboSchemaId)
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.StartupOptionOutsideMasterDbo(), 73);
        if (procedure.Parameters.Length != 0)
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.StartupOptionOnProcedureWithParameters(), 90);
        if (!startup)
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.ProcedureOptionNotSupported("sp_procoption"), 114);

        procedure.IsStartup = on;
        var anyStartup = false;
        foreach (var (_, held) in database.Schemas)
        {
            foreach (var (_, candidate) in held.Procedures)
                anyStartup |= candidate.IsStartup;
        }
        var configuration = this.ServerConfiguration;
        foreach (var entry in configuration)
        {
            if (entry.Key != ScanForStartupProcsConfigurationId)
                configuration[entry.Key] = (entry.Value.Configured, entry.Value.Configured);
        }
        configuration[ScanForStartupProcsConfigurationId] = (anyStartup ? 1 : 0, this.ConfigurationInUse(ScanForStartupProcsConfigurationId));
    }

    /// <summary>Whether <paramref name="value"/> is one of <paramref name="accepted"/>, case and trailing spaces aside.</summary>
    private static bool IsAnyOf(string value, params ReadOnlySpan<string> accepted)
    {
        foreach (var candidate in accepted)
        {
            if (DeviceNamesEqual(value, candidate))
                return true;
        }
        return false;
    }

    /// <summary>An option procedure's refusal, which returns its own number as the return code.</summary>
    private static SimulatedSqlException OptionProcedureRefusal(string calledAs, SimulatedSqlException error, int line) =>
        AtSystemProcedureLine(calledAs, error, line, error.Number);
}
