using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE SCHEMA [&lt;name&gt;] [AUTHORIZATION &lt;owner&gt;]
    /// [&lt;schema_element&gt; …]</c>. Adds a fresh entry to
    /// <see cref="Database.Schemas"/>; subsequent two-part references like
    /// <c>SELECT * FROM &lt;name&gt;.t</c> route through it. The dispatcher
    /// reaches this from <see cref="TryParseCreate"/>'s <c>SCHEMA</c> branch;
    /// cursor on entry is the <c>SCHEMA</c> keyword.
    /// </summary>
    /// <remarks>
    /// <para>Probe-confirmed against SQL Server 2025:</para>
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
    /// <item><c>AUTHORIZATION</c> names the schema's owner, which
    /// <c>sys.schemas.principal_id</c> projects and which the principal DDL
    /// then refuses to drop out from under (<strong>Msg 15138</strong>). Any
    /// database principal will do, roles included; one the database doesn't
    /// carry is <strong>Msg 15151</strong>'s user variant. Written without a
    /// schema name the clause supplies one — <c>CREATE SCHEMA AUTHORIZATION
    /// dbo</c> creates a schema called <c>dbo</c>, which is then Msg 2760.</item>
    /// <item>Every failure inside the statement carries a trailing
    /// <strong>Msg 2759</strong>, and the statement is atomic: an element that
    /// raises leaves neither the schema nor its earlier elements behind.</item>
    /// </list>
    /// <para>
    /// The <c>&lt;schema_element&gt;</c> list is part of the statement rather
    /// than a run of trailing statements, and its point is the name scope:
    /// <b>an unqualified name inside an element resolves to the schema being
    /// created</b>, so <c>CREATE SCHEMA s CREATE TABLE t (…) GRANT SELECT ON t
    /// TO u</c> creates <c>s.t</c> and grants on <c>s.t</c> (probe-confirmed,
    /// including a sibling element's FOREIGN KEY and a view body resolving
    /// there). An explicit qualifier still wins. Real's element grammar admits
    /// <c>CREATE TABLE</c>, <c>CREATE VIEW</c>, <c>GRANT</c>, <c>REVOKE</c> and
    /// <c>DENY</c> and nothing else — probe-confirmed that <c>CREATE
    /// PROCEDURE</c> / <c>FUNCTION</c> is Msg 156, <c>CREATE TYPE</c> Msg 102
    /// and <c>CREATE INDEX</c> Msg 1018.
    /// </para>
    /// </remarks>
    private bool TryParseCreateSchema(ParserContext context)
    {
        if (context.Batch.BlockDepth > 0 || context.Batch.HasDispatchedStatement)
            throw SimulatedSqlException.MustBeFirstStatementInBatch("CREATE SCHEMA");

        context.MoveNextRequired();
        string? schemaName = null;
        if (context.Token is Name schemaNameToken)
        {
            schemaName = schemaNameToken.Value;
            context.MoveNextOptional();
        }

        string? ownerName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Authorization })
        {
            if (context.GetNextRequired() is not Name ownerToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ownerName = ownerToken.Value;
            context.MoveNextOptional();
        }

        // Neither a name nor an AUTHORIZATION clause means this wasn't a
        // CREATE SCHEMA at all; hand the token back to the dispatcher.
        if (schemaName is null && ownerName is null)
            return false;
        // `CREATE SCHEMA AUTHORIZATION <owner>` names the schema after its
        // owner, which is how the ownerless form of the grammar stays
        // unambiguous.
        schemaName ??= ownerName!;

        try
        {
            return this.CreateSchemaBody(context, schemaName, ownerName);
        }
        catch (SimulatedSqlException exception)
        {
            throw SimulatedSqlException.Aggregate([exception, SimulatedSqlException.CreateSchemaFailed()]);
        }
    }

    /// <summary>
    /// The part of <c>CREATE SCHEMA</c> whose every failure earns the trailing
    /// Msg 2759: the permission gate, the name checks, the owner binding and
    /// the element list. Rolls the schema back out of
    /// <see cref="Database.Schemas"/> when an element raises, so the statement
    /// is all-or-nothing as on real.
    /// </summary>
    private bool CreateSchemaBody(ParserContext context, string schemaName, string? ownerName)
    {
        if (context.Batch.IsSkipping)
            return this.ParseSchemaElements(context, elementScope: null);

        // CREATE SCHEMA isn't a modeled named permission — Msg 15247 for a
        // non-privileged principal (probe M3).
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, context.CurrentDatabase))
            throw SimulatedSqlException.UserDoesNotHavePermission();

        // Built-ins: dbo lives in every database; sys / INFORMATION_SCHEMA are
        // server-owned. Real SQL Server raises Msg 2760 on each — replicated.
        if (IsReservedSchemaName(context.CurrentDatabase.Collation, schemaName))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schemaName);

        var ownerPrincipalId = Database.DboPrincipalId;
        if (ownerName is not null)
        {
            if (!context.CurrentDatabase.Principals.TryGetValue(ownerName, out var owner))
                throw SimulatedSqlException.CannotFindUser(ownerName);
            ownerPrincipalId = owner.PrincipalId;
        }

        var schema = new Schema(context.CurrentDatabase, schemaName, context.CurrentDatabase.AllocateSchemaId())
        {
            PrincipalId = ownerPrincipalId,
        };
        if (!context.CurrentDatabase.Schemas.TryAdd(schemaName, schema))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(schemaName);

        try
        {
            _ = this.ParseSchemaElements(context, schemaName);
        }
        catch
        {
            _ = context.CurrentDatabase.Schemas.TryRemove(schemaName, out _);
            throw;
        }

        // Real reports the new schema as both SchemaName and ObjectName.
        RecordDdlEvent(context, "CREATE_SCHEMA", schemaName, schemaName, "SCHEMA");
        return true;
    }

    /// <summary>
    /// Walks the trailing <c>&lt;schema_element&gt;</c> list, binding each
    /// element with <paramref name="elementScope"/> installed as the default
    /// schema so unqualified names land in the schema being created. Returns
    /// true unconditionally — the caller's own return value.
    /// </summary>
    private bool ParseSchemaElements(ParserContext context, string? elementScope)
    {
        var previousScope = context.Batch.CreateSchemaElementScope;
        context.Batch.CreateSchemaElementScope = elementScope ?? previousScope ?? Database.DefaultSchemaName;
        try
        {
            while (context.Token is ReservedKeyword { Keyword: var verb }
                && verb is Keyword.Create or Keyword.Grant or Keyword.Revoke or Keyword.Deny)
            {
                var parsed = verb switch
                {
                    Keyword.Create => this.ParseSchemaCreateElement(context),
                    Keyword.Grant => TryParseGrantRevokeDeny(context, PermissionStatementKind.Grant),
                    Keyword.Revoke => TryParseGrantRevokeDeny(context, PermissionStatementKind.Revoke),
                    _ => TryParseGrantRevokeDeny(context, PermissionStatementKind.Deny),
                };
                if (!parsed)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                while (context.Token is Operator { Character: ';' })
                    context.MoveNextOptional();
            }
        }
        finally
        {
            context.Batch.CreateSchemaElementScope = previousScope;
        }

        return true;
    }

    /// <summary>
    /// Dispatches one <c>CREATE</c>-shaped schema element. Only <c>TABLE</c>
    /// and <c>VIEW</c> are on real's element grammar; the near misses each
    /// carry their own probed refusal.
    /// </summary>
    private bool ParseSchemaCreateElement(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var kind = context.GetNextRequired();
        context.RestoreCheckpoint(checkpoint);
        return kind switch
        {
            ReservedKeyword { Keyword: Keyword.Table or Keyword.View } => this.TryParseCreate(context),
            ReservedKeyword { Keyword: Keyword.Index } => throw SimulatedSqlException.IndexHintNeedsWithKeyword(),
            ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
            _ => throw SimulatedSqlException.SyntaxErrorNear(kind),
        };
    }

    /// <summary>
    /// True when <paramref name="schemaName"/> matches one of the reserved
    /// built-in schema names (<c>dbo</c>, <c>sys</c>,
    /// <c>INFORMATION_SCHEMA</c>) under <paramref name="collation"/>. Real
    /// SQL Server's check follows the database collation
    /// (probe-confirmed 2026-05-21 against a CS database: <c>CREATE SCHEMA
    /// DBO</c> succeeds because <c>DBO</c> doesn't case-equal the reserved
    /// <c>dbo</c>).
    /// </summary>
    private static bool IsReservedSchemaName(Collation collation, string schemaName) =>
        collation.Equals(schemaName, "dbo")
        || collation.Equals(schemaName, "sys")
        || collation.Equals(schemaName, "INFORMATION_SCHEMA");
}
