using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// <c>sp_refreshview @viewname</c> — rebinds a view to its sources' current
    /// shape, so a <c>SELECT *</c> view frozen at CREATE picks up an added
    /// column (and drops one that went away). Anything but a view is Msg 15165.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpRefreshView(BatchContext batch) =>
        this.InvokeRefreshModule(batch, "sp_refreshview", "viewname", viewsOnly: true);

    /// <summary>
    /// <c>sp_recompile @objname</c>: answers the class-0 Msg 15070 for an
    /// object in the schema namespace, Msg 15165 otherwise, each naming the
    /// argument as passed (probed 2026-09-25 against SQL Server 2025). A
    /// procedure or trigger named drops its compiled plan, and a table named
    /// takes a new <c>modify_date</c>, which every plan reading it sees as a
    /// change; a function named changes nothing a caller's plan depends on
    /// (probed 2026-10-01; see <see cref="ModulePlan"/>).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpRecompile(BatchContext batch, string procedureName)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, _) = ParseHelpArgs(arguments, "sp_recompile", firstName: "objname");
        if (objectName is null)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_recompile", "objname");
        if (!Parser.Expressions.ObjectId.TryParseObjectName(objectName, out var name)
            || !batch.TryResolveSchema(name, out var schema)
            || !schema.TryFindInSharedNamespace(name.Leaf, out var marked))
        {
            // The procedure returns the error's number (probed 2026-10-06
            // against SQL Server 2025).
            var missing = SimulatedSqlException.CouldNotFindObjectOrNoPermission(objectName);
            missing.SystemProcedureReturnCode = missing.Number;
            throw missing;
        }
        switch (marked)
        {
            case Procedure procedure:
                procedure.CompiledPlan = null;
                break;
            case Trigger trigger:
                trigger.CompiledPlan = null;
                break;
            case Storage.HeapTable table:
                table.ModifyDate = batch.CurrentStatement.UtcNow;
                batch.Connection.Simulation.BumpSchemaVersion();
                break;
        }
        batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.MarkedForRecompilationMessage(batch, procedureName, objectName));
    }

    /// <summary>
    /// <c>sp_refreshsqlmodule @name</c> — the same rebinding for any module
    /// with a stored definition: a view, procedure, function or DML trigger.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpRefreshSqlModule(BatchContext batch) =>
        this.InvokeRefreshModule(batch, "sp_refreshsqlmodule", "name", viewsOnly: false);

    /// <summary>
    /// Re-runs the module's stored <c>CREATE</c> text as an <c>ALTER</c>, under
    /// the <c>QUOTED_IDENTIFIER</c> / <c>ANSI_NULLS</c> it was created with —
    /// the ALTER path already keeps the object id, permissions and a view's
    /// INSTEAD OF triggers. A body that no longer binds reports its binder
    /// error, and a schema-bound module is left alone with the class-0
    /// Msg 2023 (all probed 2026-09-24 against SQL Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeRefreshModule(BatchContext batch, string procedureName, string parameterName, bool viewsOnly)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, _) = ParseHelpArgs(arguments, procedureName, firstName: parameterName);
        if (objectName is null)
            throw SimulatedSqlException.ProcedureExpectsParameter(procedureName, parameterName);

        SchemaObject? module = null;
        if (Parser.Expressions.ObjectId.TryParseObjectName(objectName, out var name)
            && batch.TryResolveSchema(name, out var schema)
            && schema.TryFindInSharedNamespace(name.Leaf, out var found))
        {
            module = found switch
            {
                View => found,
                Procedure or UserDefinedFunction or Trigger when !viewsOnly => found,
                _ => null,
            };
        }
        if (module is null)
            throw SimulatedSqlException.CouldNotFindObjectOrNoPermission(objectName);

        if (module is View { IsSchemaBound: true } or UserDefinedFunction { IsSchemaBound: true })
        {
            // Named as the argument spelled it (probed 2026-09-25), from
            // line 47 of the internal procedure both front doors call (probed
            // 2026-10-04 against SQL Server 2025).
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(
                batch, "sys.sp_refreshsqlmodule_internal", 47, 2023, $"Metadata was not updated for the schema-bound object '{objectName}'."));
            yield break;
        }

        // An encrypted module keeps no text to rebind from.
        if (module.DefinitionText is not { } definition)
            yield break;

        var connection = batch.Connection;
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.QuotedIdentifiers = module.UsesQuotedIdentifier;
        connection.AnsiNulls = module.UsesAnsiNulls;
        try
        {
            var verbStart = ModuleVerbStart(definition);
            var alter = definition[..verbStart] + "ALTER" + definition[(verbStart + "CREATE".Length)..];
            foreach (var outcome in this.ExecuteDynamicBatch(batch, alter, preDeclaredVariables: null))
                yield return outcome;
        }
        finally
        {
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
        }
    }
}
