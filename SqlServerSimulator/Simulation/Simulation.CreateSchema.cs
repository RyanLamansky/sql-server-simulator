using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE SCHEMA &lt;name&gt;</c> (the bare form — neither the
    /// <c>AUTHORIZATION owner</c> clause nor the trailing
    /// <c>&lt;schema_element&gt;</c> list with inline CREATE TABLE / VIEW /
    /// GRANT / etc. is modeled; those raise <see cref="NotSupportedException"/>).
    /// Adds a fresh entry to <see cref="Database.Schemas"/>; subsequent two-
    /// part references like <c>SELECT * FROM &lt;name&gt;.t</c> route through
    /// it. The dispatcher reaches this from <see cref="TryParseCreate"/>'s
    /// <c>SCHEMA</c> branch; cursor on entry is the <c>SCHEMA</c> keyword.
    /// </summary>
    /// <remarks>
    /// <para>Probe-confirmed against SQL Server 2025 (2026-05-11):</para>
    /// <list type="bullet">
    /// <item>Duplicate name (case-insensitive) → <strong>Msg 2714</strong>
    /// (<c>"There is already an object named '&lt;n&gt;' in the
    /// database."</c>) — same factory as duplicate CREATE TABLE; SQL Server
    /// shares the namespace.</item>
    /// <item>Built-in / reserved names (<c>dbo</c>, <c>sys</c>,
    /// <c>INFORMATION_SCHEMA</c>) → <strong>Msg 2760</strong>
    /// (<c>"The specified schema name \"&lt;n&gt;\" either does not exist or
    /// you do not have permission to use it."</c>). The wording is quirky on
    /// a CREATE — real SQL Server resolves the principal first, and these
    /// schemas exist tied to system principals the caller can't write to. The
    /// simulator rejects all three uniformly.</item>
    /// <item>Real SQL Server requires <c>CREATE SCHEMA</c> to be the first
    /// statement in a batch and greedy-consumes trailing tokens as
    /// <c>&lt;schema_element&gt;</c>s (CREATE TABLE / VIEW / GRANT inside
    /// the same statement). The simulator deviates here: trailing tokens
    /// that begin a recognized statement (<c>CREATE</c>, <c>INSERT</c>,
    /// <c>SELECT</c>, etc.) parse as their own statement in the same batch,
    /// reaching the same end state as the common embedded-CREATE-TABLE
    /// idiom. Only the <c>AUTHORIZATION</c> clause and other non-boundary
    /// trailers raise <see cref="NotSupportedException"/>.</item>
    /// </list>
    /// </remarks>
    private static bool TryParseCreateSchema(ParserContext context)
    {
        if (context.Batch.BlockDepth > 0 || context.Batch.HasDispatchedStatement)
            throw SimulatedSqlException.MustBeFirstStatementInBatch("CREATE SCHEMA");

        if (context.GetNextRequired() is not Name schemaNameToken)
            return false;
        var schemaName = schemaNameToken.Value;

        context.MoveNextOptional();
        // The full SQL Server grammar accepts AUTHORIZATION <owner> and an
        // open-ended `[ <schema_element> [...] ]` list (CREATE TABLE / VIEW /
        // GRANT / etc. nested inside the same statement). Neither is modeled;
        // raise NotSupportedException with a hint pointing at the bare form.
        // Statement-boundary tokens (;, EOB, next-statement keyword) pass
        // through.
        if (context.Token is not null && !IsStatementBoundary(context.Token))
        {
            throw new NotSupportedException(
                context.Token is ReservedKeyword { Keyword: Keyword.Authorization }
                    ? "CREATE SCHEMA AUTHORIZATION isn't modeled (the simulator has no user / principal model). Use the bare CREATE SCHEMA <name> form."
                    : "CREATE SCHEMA with embedded <schema_element> elements (inline CREATE TABLE / VIEW / GRANT) isn't modeled. Emit CREATE SCHEMA <name> on its own and follow with separate CREATE TABLE statements.");
        }

        if (context.Batch.IsSkipping)
            return true;

        // Built-ins: dbo lives in every database; sys / INFORMATION_SCHEMA are
        // server-owned. Real SQL Server raises Msg 2760 on each — replicated.
        return IsReservedSchemaName(schemaName)
            ? throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schemaName)
            : context.CurrentDatabase.Schemas.TryAdd(schemaName, new Schema(schemaName, context.CurrentDatabase.AllocateSchemaId()))
                ? true
                : throw SimulatedSqlException.ThereIsAlreadyAnObject(schemaName);
    }

    private static bool IsReservedSchemaName(string schemaName) =>
        Collation.Default.Equals(schemaName, "dbo")
        || Collation.Default.Equals(schemaName, "sys")
        || Collation.Default.Equals(schemaName, "INFORMATION_SCHEMA");
}
