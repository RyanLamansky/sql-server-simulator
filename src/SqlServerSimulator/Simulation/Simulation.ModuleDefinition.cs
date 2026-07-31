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
            return existing;
        }
        return schema.HasNameInSharedNamespace(name.Leaf)
            ? throw (isAlter || createOrAlter
                ? SimulatedSqlException.CannotAlterIncompatibleObjectType(name)
                : SimulatedSqlException.ThereIsAlreadyAnObject(name.Leaf))
            : isAlter ? throw SimulatedSqlException.InvalidObjectName(name) : null;
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
