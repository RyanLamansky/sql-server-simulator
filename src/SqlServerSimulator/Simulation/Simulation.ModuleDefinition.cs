using System.Text.RegularExpressions;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

public sealed partial class Simulation
{
    /// <summary>
    /// Applies the shared CREATE / ALTER / CREATE OR ALTER existence rules a
    /// programmable module's parser owes before it registers its definition,
    /// and returns the instance whose identity (<see cref="SchemaObject.ObjectId"/>,
    /// <see cref="SchemaObject.CreateDate"/>, granted permissions, attached
    /// triggers) the new definition inherits — <see langword="null"/> when the
    /// statement is a plain create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>existingOfDeclaredKind</c> is the object already stored under
    /// <c>name</c> <em>when it is the same kind the statement declares</em>. A
    /// stored object of any other kind must come through as
    /// <see langword="null"/> so the Msg 2010 branch fires — that includes a
    /// function whose stored kind (scalar / inline TVF / multi-statement TVF)
    /// differs from the one the ALTER body writes, which real rejects the same
    /// way it rejects ALTER VIEW over a table.
    /// </para>
    /// <para>
    /// Probe-confirmed against SQL Server 2025 (2026-07-31): kind mismatch →
    /// <strong>Msg 2010</strong> on both ALTER legs and <strong>Msg 2714</strong>
    /// on a plain CREATE; a name nothing holds → <strong>Msg 208</strong> for
    /// ALTER. Sch-M is taken on the object being replaced so a concurrent
    /// reader holding Sch-S blocks the swap.
    /// </para>
    /// <para>
    /// Replacing a view or function a <c>WITH SCHEMABINDING</c> module
    /// references is <strong>Msg 3729</strong> (state 3, the altered module
    /// carried as Procedure attribution) — the same record that blocks the
    /// referent's DROP, reached through the one choke point every module
    /// parser's ALTER leg passes.
    /// </para>
    /// </remarks>
    private static SchemaObject? ResolveModuleAlterTarget(
        ParserContext context,
        Schema schema,
        MultiPartName name,
        bool isAlter,
        bool createOrAlter,
        SchemaObject? existingOfDeclaredKind)
    {
        if (existingOfDeclaredKind is { } existing)
        {
            if (!isAlter && !createOrAlter)
                throw SimulatedSqlException.ThereIsAlreadyAnObject(name.Leaf);
            context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
            return existing is View or UserDefinedFunction
                && SchemaBinding.FindReferencingModule(context.CurrentDatabase, existing) is { } referencing
                ? throw SimulatedSqlException.CannotAlterReferencedBySchemaBoundObject(
                    name.ToString(), existing.Name, referencing.Name)
                : existing;
        }
        return schema.HasNameInSharedNamespace(name.Leaf)
            ? throw (isAlter || createOrAlter
                ? SimulatedSqlException.CannotAlterIncompatibleObjectType(name)
                : SimulatedSqlException.ThereIsAlreadyAnObject(name.Leaf))
            : isAlter ? throw SimulatedSqlException.InvalidObjectName(name) : null;
    }

    /// <summary>
    /// The DDL permission gate every programmable-module parser owes, on both
    /// legs of its verb. A statement that <em>creates</em> — a plain
    /// <c>CREATE</c>, or a <c>CREATE OR ALTER</c> over a free name — needs the
    /// database-scope CREATE-of-that-kind permission plus schema ALTER
    /// (<see cref="PermissionEnforcement.CheckCreateModule"/>). A statement that
    /// <em>replaces</em> — <c>ALTER</c>, or <c>CREATE OR ALTER</c> over an
    /// existing module — needs ALTER on the module instead, and reports
    /// <strong>Msg 3701</strong> sev 14 state 20 naming its kind and leaf when it
    /// is missing; the create permission alone does not admit it (probe-confirmed
    /// against SQL Server 2025). A bare <c>ALTER</c> of a name nothing holds is
    /// left to the Msg 208 the resolver raises.
    /// </summary>
    private static void CheckModuleDdlPermission(
        ParserContext context,
        string createPermission,
        MultiPartName name,
        Schema schema,
        bool isAlter,
        bool createOrAlter,
        SchemaObject? existing)
    {
        if (existing is null)
        {
            if (!isAlter)
                PermissionEnforcement.CheckCreateModule(context.Batch, createPermission, name.Leaf, schema);
            return;
        }
        if ((isAlter || createOrAlter)
            && !PermissionEnforcement.HasObjectAlter(context.Batch, schema.Database, existing.ObjectId, existing.SchemaId))
        {
            throw SimulatedSqlException.AlterObjectPermissionDenied(ModuleKindNoun(existing), name.Leaf);
        }
    }

    /// <summary>The noun real spells inside <c>Cannot alter the &lt;kind&gt; '…'</c> for a module being replaced.</summary>
    private static string ModuleKindNoun(SchemaObject module) => module switch
    {
        View => "view",
        Procedure => "procedure",
        Trigger => "trigger",
        _ => "function",
    };

    /// <summary>
    /// Enforces the name shape a programmable module's <c>CREATE</c> /
    /// <c>ALTER</c> / <c>CREATE OR ALTER</c> accepts: at most
    /// <c>schema.object</c>. A database prefix is <strong>Msg 166</strong> even
    /// when it names the current database, and a server prefix is
    /// <strong>Msg 117</strong> (both probe-confirmed against SQL Server 2025,
    /// for every verb and every module kind). <paramref name="moduleKind"/> is
    /// the keyword real echoes inside <c>'CREATE/ALTER X'</c>.
    /// </summary>
    private static void RejectQualifiedModuleName(MultiPartName name, string moduleKind)
    {
        if (name.Count >= 4)
            throw SimulatedSqlException.TooManyNamePrefixes(name, 2);
        if (name.Count == 3)
            throw SimulatedSqlException.ModuleNameMayNotBeDatabaseQualified(moduleKind);
    }

    /// <summary>
    /// Resolves the schema a <c>CREATE</c> / <c>ALTER</c> / <c>CREATE OR
    /// ALTER</c> module statement targets. A schema that doesn't exist is
    /// <strong>Msg 2760</strong> on either create form but <strong>Msg
    /// 208</strong> on a bare <c>ALTER</c> — probe-confirmed, and the split
    /// follows from what the statement asserts: a create claims a namespace,
    /// while an alter claims an object that a missing schema can't hold.
    /// </summary>
    private static Schema ResolveModuleSchema(ParserContext context, MultiPartName name, bool isAlter) =>
        context.Batch.TryResolveSchema(name, out var schema)
            ? schema
            : throw (isAlter
                ? SimulatedSqlException.InvalidObjectName(name)
                : SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(name.ImmediateQualifier ?? Database.DefaultSchemaName));

    // ^(CREATE <ws>) OR (<ws>) ALTER — collapses a CREATE OR ALTER verb phrase
    // to a bare CREATE in the stored definition. SQL Server removes the OR /
    // ALTER keyword tokens but keeps the whitespace that surrounded them, so
    // `CREATE OR ALTER PROCEDURE` is stored as `CREATE   PROCEDURE`
    // (probe-confirmed). The two captured whitespace runs reproduce that.
    private static readonly Regex CreateOrAlterVerb =
        new(@"^(CREATE\s+)OR(\s+)ALTER", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the verbatim module-definition text stored for
    /// <c>OBJECT_DEFINITION</c> / <c>sys.sql_modules</c>, slicing the original
    /// command text from the statement's leading verb keyword (<paramref name="verbStart"/>,
    /// taken from <see cref="StatementContext.StartIndex"/>) through the end of
    /// the body. The leading verb is normalized to <c>CREATE</c> to match SQL
    /// Server, which stores <c>ALTER PROCEDURE …</c> as <c>CREATE PROCEDURE …</c>
    /// and collapses <c>CREATE OR ALTER</c> to <c>CREATE</c> (probe-confirmed
    /// against SQL Server 2025). A plain <c>CREATE</c> is captured verbatim.
    /// </summary>
    private static string BuildModuleDefinition(string commandText, int verbStart, int bodyEnd, bool isAlter, bool createOrAlter)
    {
        var raw = commandText[verbStart..bodyEnd];
        return createOrAlter ? CreateOrAlterVerb.Replace(raw, "$1$2")
            : isAlter ? "CREATE" + raw["ALTER".Length..]
            : raw;
    }
}
