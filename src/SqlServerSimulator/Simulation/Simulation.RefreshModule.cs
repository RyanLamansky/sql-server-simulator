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
            batch.AppendInfoError(@class: 0, state: 1, number: 2023, message: $"Metadata was not updated for the schema-bound object '{module.Name}'.");
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
            foreach (var outcome in this.ExecuteDynamicBatch(batch, "ALTER" + definition["CREATE".Length..], preDeclaredVariables: null))
                yield return outcome;
        }
        finally
        {
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
        }
    }
}
