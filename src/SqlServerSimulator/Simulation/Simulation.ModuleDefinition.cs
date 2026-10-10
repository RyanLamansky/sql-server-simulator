using System.Text.RegularExpressions;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;

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
    /// ALTER. Sch-M is taken on the object being replaced, to the
    /// transaction's end, so a concurrent reader holding Sch-S blocks the swap
    /// and a reader arriving later waits for the change to settle.
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
                throw SimulatedSqlException.ThereIsAlreadyAnObject(name.Leaf, state: 3);
            context.Batch.LockDefinition(schema, existing);
            return existing is View or UserDefinedFunction
                && SchemaBinding.FindReferencingModule(context.CurrentDatabase, existing) is { } referencing
                ? throw SimulatedSqlException.CannotAlterReferencedBySchemaBoundObject(
                    name.ToString(), existing.Name, referencing.Name)
                : existing is UserDefinedFunction function && SchemaBinding.FindReferencingConstraint(context.CurrentDatabase, function) is { } constraint
                ? throw SimulatedSqlException.CannotAlterReferencedBySchemaBoundObject(name.ToString(), existing.Name, constraint)
                : existing;
        }
        return schema.HasNameInSharedNamespace(name.Leaf)
            ? throw (isAlter || createOrAlter
                ? SimulatedSqlException.CannotAlterIncompatibleObjectType(name)
                : SimulatedSqlException.ThereIsAlreadyAnObject(name.Leaf, state: 3))
            : isAlter ? throw SimulatedSqlException.InvalidObjectName(name, state: 6) : null;
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
    /// left to the Msg 208 the resolver raises. A schema-bound module's
    /// REFERENCES refusal, which <paramref name="schemaBoundBind"/> meets,
    /// outranks a missing CREATE permission (probed 2026-10-06 against SQL
    /// Server 2025: Msg 229 and Msg 1088 rather than Msg 262).
    /// </summary>
    private static void CheckModuleDdlPermission(
        ParserContext context,
        string createPermission,
        MultiPartName name,
        Schema schema,
        bool isAlter,
        bool createOrAlter,
        SchemaObject? existing,
        Action? schemaBoundBind = null)
    {
        if (existing is null)
        {
            if (isAlter)
                return;
            try
            {
                PermissionEnforcement.CheckCreateModule(context.Batch, createPermission, name.Leaf, schema);
            }
            catch (SimulatedSqlException denied) when (denied.Number == 262 && schemaBoundBind is not null)
            {
                if (ReferencesDenial(schemaBoundBind) is { } references)
                    throw references;
                throw;
            }
            return;
        }
        if ((isAlter || createOrAlter)
            && !PermissionEnforcement.HasObjectAlter(context.Batch, schema.Database, existing.ObjectId, existing.SchemaId))
        {
            throw SimulatedSqlException.AlterObjectPermissionDenied(ModuleKindNoun(existing), name.Leaf);
        }
    }

    /// <summary>
    /// The REFERENCES check a schema-bound module's creation makes of every
    /// object its body binds to, or null for a session that bypasses it — Msg
    /// 229 then Msg 1088 state 18, attributed to the module (probed
    /// 2026-10-04 against SQL Server 2025, the creator's own grant answering
    /// whoever owns the object).
    /// </summary>
    private static Action<SchemaObject>? SchemaBoundReferenceCheck(BatchContext batch, string moduleLeaf) =>
        batch.Connection.Security.EffectiveIsDbo ? null : bound =>
        {
            var database = batch.DatabaseFor(bound);
            if (!PermissionEnforcement.HoldsPermission(batch, database, Permission.References, PermissionChecker.ClassObject, bound.ObjectId, bound.SchemaId))
            {
                throw SimulatedSqlException.SchemaBoundReferencesDenied(bound.Name, database.Name, PermissionEnforcement.SchemaNameFor(database, bound.SchemaId), moduleLeaf);
            }
        };

    /// <summary>
    /// The REFERENCES refusal a schema-bound module's body bind raises, or
    /// null when it raises none — any other bind error waits behind the
    /// permission refusal the caller holds.
    /// </summary>
    private static SimulatedSqlException? ReferencesDenial(Action schemaBoundBind)
    {
        try
        {
            schemaBoundBind();
        }
        catch (SimulatedSqlException references) when (references.Number == 229)
        {
            return references;
        }
        catch (SimulatedSqlException)
        {
        }
        return null;
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
        // Real reports the database prefix at line 12 whatever the statement's
        // own, and neither refusal names the module (probed 2026-10-04 against
        // SQL Server 2025), so the callers raise them ahead of their own
        // attribution.
        if (name.Count == 3)
            throw SimulatedSqlException.ModuleNameMayNotBeDatabaseQualified(moduleKind).PinLine(12);
    }

    /// <summary>
    /// Enforces real's rule that a <c>VIEW</c> or <c>FUNCTION</c> body runs to
    /// the end of its batch: the module statement must be the batch's
    /// <em>only</em> statement, not merely its first. A token left over after
    /// the body is a plain syntax error at that token —
    /// <strong>Msg 156</strong> naming the keyword when it is one,
    /// <strong>Msg 102</strong> otherwise (probe-confirmed against SQL Server
    /// 2025, 2026-08-04, for scalar / inline-TVF / multi-statement-TVF
    /// functions and views alike). The keyword is echoed as the source spelled
    /// it, which <see cref="Token.ToString"/> already does.
    /// <para>The error carries the module's <strong>unqualified</strong> name
    /// as <c>Procedure</c> — real attributes it to the module being defined,
    /// since it is that module's body parse that ran off the end — and the
    /// offending token's own line, which the severity-15 branch of the dispatch
    /// frame's diagnostics stamping supplies.</para>
    /// <para>Trailing <c>;</c> separators (any number) and comments are not
    /// statements and are accepted. Raised before the module is created: real
    /// parses the batch first, so the object does not exist afterward
    /// (probe-confirmed).</para>
    /// <para><strong>Procedures and triggers take no such check</strong> —
    /// their bodies are multi-statement and read to end-of-batch, so a trailing
    /// statement is swallowed into the body on real exactly as it is here
    /// (probe-confirmed via <c>OBJECT_DEFINITION</c> and by executing the
    /// procedure).</para>
    /// </summary>
    private static void RejectStatementAfterModuleBody(ParserContext context, string moduleName)
    {
        // A view body inside a CREATE SCHEMA element list is not its batch's
        // only statement and isn't meant to be: real ends the body at the next
        // <schema_element> keyword and carries on.
        if (context.Batch.CreateSchemaElementScope is not null)
            return;
        while (context.Token is Operator { Character: ';' })
            context.MoveNextOptional();
        if (context.Token is not { } trailing)
            return;

        var error = trailing is ReservedKeyword keyword
            ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
            : SimulatedSqlException.SyntaxErrorNear(trailing);
        error.Errors[0].Procedure = moduleName;
        throw error;
    }

    /// <summary>
    /// Resolves the schema a <c>CREATE</c> / <c>ALTER</c> / <c>CREATE OR
    /// ALTER</c> module statement targets. A schema that doesn't exist is
    /// <strong>Msg 2760</strong> on either create form but <strong>Msg
    /// 208</strong> on a bare <c>ALTER</c> — probe-confirmed, and the split
    /// follows from what the statement asserts: a create claims a namespace,
    /// while an alter claims an object that a missing schema can't hold.
    /// </summary>
    /// <remarks>
    /// Also the read-only gate for every module <c>CREATE</c> / <c>ALTER</c>:
    /// a module is defined in exactly one place, and the resolved schema names
    /// the database the definition would be written to — which is the database
    /// real's Msg 3906 names, not the session's.
    /// </remarks>
    private static Schema ResolveModuleSchema(ParserContext context, MultiPartName name, bool isAlter)
    {
        if (!(isAlter ? context.Batch.TryResolveSchema(name, out var schema) : context.Batch.TryResolveCreateSchema(name, out schema, statementOnly: true)))
        {
            // A CREATE SCHEMA element names the schema its statement creates,
            // which the compile pass hasn't created; the run settles the name
            // (probed 2026-09-30 against SQL Server 2025).
            if (context.Batch.SchemaCompiledUncreated is { } uncreated && name.ImmediateQualifier is { } qualifier
                && context.CurrentDatabase.Collation.Equals(qualifier, uncreated))
            {
                return new Schema(context.CurrentDatabase, qualifier, 0);
            }
            throw isAlter
                ? SimulatedSqlException.InvalidObjectName(name, state: 6)
                : SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(name.ImmediateQualifier ?? Database.DefaultSchemaName);
        }

        // The system schemas take no module, refused as a schema that doesn't
        // exist (probed 2026-10-04 against SQL Server 2025 for a view, a
        // procedure and a function).
        if (!isAlter && name.ImmediateQualifier is { } systemQualifier
            && (Collation.Baseline.Equals(systemQualifier, "sys") || Collation.Baseline.Equals(systemQualifier, "INFORMATION_SCHEMA")))
        {
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(systemQualifier);
        }
        schema.Database.RejectWriteWhenReadOnly();
        context.Batch.DefiningModuleSchema = schema;
        return schema;
    }

    // ^(CREATE <ws>) OR (<ws>) ALTER — collapses a CREATE OR ALTER verb phrase
    // to a bare CREATE in the stored definition. SQL Server removes the OR /
    // ALTER keyword tokens but keeps the whitespace that surrounded them, so
    // `CREATE OR ALTER PROCEDURE` is stored as `CREATE   PROCEDURE`
    // (probe-confirmed). The two captured whitespace runs reproduce that.
    [GeneratedRegex(@"^(CREATE\s+)OR(\s+)ALTER", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateOrAlterVerb();

    /// <summary>
    /// Builds the module-definition text stored for <c>OBJECT_DEFINITION</c> /
    /// <c>sys.sql_modules</c>: the whole batch, from its first character —
    /// leading whitespace and comments included — to its last, trailing
    /// semicolons and comments included, with the leading verb at
    /// <paramref name="verbStart"/> (<see cref="StatementContext.StartIndex"/>)
    /// normalized to <c>CREATE</c> in place: SQL Server stores
    /// <c>ALTER PROCEDURE …</c> as <c>CREATE PROCEDURE …</c> and collapses
    /// <c>CREATE OR ALTER</c> to <c>CREATE</c> (probed 2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    private static string BuildModuleDefinition(string commandText, int verbStart, bool isAlter, bool createOrAlter)
    {
        var raw = commandText[verbStart..];
        var verb = createOrAlter ? CreateOrAlterVerb().Replace(raw, "$1$2")
            : isAlter ? "CREATE" + raw["ALTER".Length..]
            : raw;
        return commandText[..verbStart] + verb;
    }

    /// <summary>
    /// Where the <c>CREATE</c> verb of a stored module definition starts. The
    /// definition keeps whatever preceded the statement in its batch, which —
    /// a module leading its batch — is only whitespace and comments (nested
    /// block comments included).
    /// </summary>
    private static int ModuleVerbStart(string definition)
    {
        var i = 0;
        while (i < definition.Length)
        {
            if (char.IsWhiteSpace(definition[i]))
            {
                i++;
            }
            else if (definition.AsSpan(i).StartsWith("--"))
            {
                var end = definition.IndexOf('\n', i);
                i = end < 0 ? definition.Length : end + 1;
            }
            else if (definition.AsSpan(i).StartsWith("/*"))
            {
                var depth = 0;
                do
                {
                    if (definition.AsSpan(i).StartsWith("/*"))
                    {
                        depth++;
                        i += 2;
                    }
                    else if (definition.AsSpan(i).StartsWith("*/"))
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                while (depth > 0 && i < definition.Length);
            }
            else
            {
                break;
            }
        }
        return i;
    }
}
