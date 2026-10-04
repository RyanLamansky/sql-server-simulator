using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// The resolver contract the parser depends on: temp-table, table-variable and
// schema-qualified name resolution for every kind of schema object, synonyms,
// catalog views, linked-server and remote tables, and ParseObjectName.
internal sealed partial class BatchContext
{
    /// <summary>
    /// Recognizes a local temp-table name (<c>#foo</c>, including bare
    /// <c>#</c>). Global temps (<c>##foo</c>) are recognized by
    /// <see cref="IsGlobalTempName(string)"/> instead. The rule: leading
    /// <c>#</c>, second char is not <c>#</c>.
    /// </summary>
    public static bool IsLocalTempName(string name) =>
        name.Length >= 1 && name[0] == '#' && (name.Length == 1 || name[1] != '#');

    /// <summary>
    /// Recognizes a global temp-table name (<c>##foo</c>, including bare
    /// <c>##</c> — probe-confirmed against SQL Server 2025 that two-char
    /// <c>##</c> is a valid table name). Rule: at least two characters,
    /// both <c>#</c>.
    /// </summary>
    public static bool IsGlobalTempName(string name) =>
        name.Length >= 2 && name[0] == '#' && name[1] == '#';

    /// <summary>
    /// Recognizes a table-variable name (<c>@foo</c>). Used by DML / FROM
    /// resolution to route 1-part references with a leading <c>@</c> to
    /// <see cref="TableVariables"/> instead of the regular schema/temp lookup.
    /// </summary>
    public static bool IsTableVariableName(string name) =>
        name.Length >= 2 && name[0] == '@';

    /// <summary>
    /// When non-null, every base <see cref="HeapTable"/> and every
    /// <see cref="View"/> resolved during parsing is recorded here. Set only
    /// while collecting an indexed view's base-table dependencies at CREATE
    /// INDEX-on-view time (<c>Simulation.IndexedViews.cs</c>); null on the hot
    /// path, so normal parsing pays a single null check.
    /// </summary>
    public (HashSet<HeapTable> Tables, HashSet<View> Views)? DependencySink;

    /// <summary>
    /// Resolves <paramref name="name"/> against the right table dictionary —
    /// the connection's <see cref="SimulatedDbConnection.TempTables"/> for
    /// <c>#foo</c> names, otherwise the named schema (or the unqualified search,
    /// <see cref="TryResolveUnqualified"/>) plus the simulation's flat
    /// system-table dict. Centralizes the routing
    /// rule so callsites (SELECT/INSERT/UPDATE/DELETE/MERGE name lookups,
    /// <c>IDENT_CURRENT</c>, <c>SET IDENTITY_INSERT</c>) stay uniform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution by <see cref="MultiPartName.Count"/>:
    /// </para>
    /// <list type="bullet">
    /// <item>1-part <c>t</c> — temp dict (if <c>#</c>-prefixed); else default
    /// schema then system tables.</item>
    /// <item>2-part <c>schema.t</c> — named schema; falls through to false
    /// when the schema doesn't exist or doesn't hold a table by that name.
    /// System tables are <em>not</em> reachable through a schema qualifier
    /// (real SQL Server's <c>sys.&lt;table&gt;</c> isn't modeled).</item>
    /// <item>3-part <c>db.schema.t</c> — same as 2-part after validating the
    /// db segment matches <see cref="CurrentDatabase"/>'s name; mismatched db
    /// returns false.</item>
    /// <item>4-part <c>server.db.schema.t</c> — false (linked servers not
    /// modeled; the callsite raises Msg 208 via the standard path).</item>
    /// </list>
    /// <para>
    /// For <c>#</c>-prefixed leaves a qualifier is cosmetic and ignored —
    /// matches probe-confirmed behavior for <c>tempdb..#foo</c> /
    /// <c>tempdb.dbo.#foo</c> in DROP TABLE; the connection's temp-table dict
    /// is the routing key regardless of preceding segments.
    /// </para>
    /// </remarks>
    public bool TryResolveTable(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapTable? table)
    {
        if (!this.TryResolveTableCore(name, out table))
            return false;
        // A natively compiled module reaches memory-optimized tables only
        // (Msg 10775 as it binds at CREATE, naming the table as written —
        // probed 2026-10-02 against SQL Server 2025).
        if (this.NativelyCompiledBody && this.CreateTimeBinding && !table.IsMemoryOptimized && !table.IsTableVariable)
            throw SimulatedSqlException.NativeModuleDiskTable(name.ToString());
        return true;
    }

    private bool TryResolveTableCore(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapTable? table)
    {
        // Trigger pseudo-tables INSERTED / DELETED resolve first when a
        // trigger body is in flight. 1-part names only (probe-confirmed:
        // qualified `dbo.inserted` raises Msg 208 in real SQL Server).
        // Pseudo-tables are batch-local materializations — no Sch-S needed
        // (no DDL can target them).
        if (this.TriggerFrame is { } triggerFrame && name.Count == 1)
        {
            if (BuiltInToken.Equals(name.Leaf, "inserted") && triggerFrame.Inserted is { } ins)
            {
                table = ins;
                return true;
            }
            if (BuiltInToken.Equals(name.Leaf, "deleted") && triggerFrame.Deleted is { } del)
            {
                table = del;
                return true;
            }
        }

        // A view's or function's body can't read a temp table (probed
        // 2026-10-01 against SQL Server 2025).
        if (IsLocalTempName(name.Leaf) || IsGlobalTempName(name.Leaf))
        {
            if (this.Parser.BindingViewDefinition)
                throw SimulatedSqlException.ViewOnTemporaryTable();
            if (this.UdfFrame is not null)
                throw SimulatedSqlException.TemporaryTableInFunction();
        }

        if (IsLocalTempName(name.Leaf))
        {
            // Temp tables are session-local; no other connection can DROP
            // them, so Sch-S acquisition is unnecessary (and would be a
            // self-conflict-free no-op anyway). Session-scoped: the parsed
            // Selection binds the specific HeapTable instance from THIS
            // connection's TempTables dict, so a cross-session plan-cache
            // replay would project the wrong table.
            this.HasSessionScopedReference = true;
            this.ResolvedTempTable = true;
            this.CurrentStatement.ReadsTemporaryObject = true;
            return this.Connection.TempTables.TryGetValue(name.Leaf, out table);
        }

        if (IsGlobalTempName(name.Leaf))
        {
            // Global temp tables live on the simulation; the qualifier is
            // cosmetic and ignored (probe-confirmed `tempdb..##q` works the
            // same as `##q`). Sch-S acquisition is skipped — the connection-
            // dispose cleanup is the only DDL that races with reads, and it
            // walks the dict under TryRemove which is atomic against
            // resolution lookups. The binding still captures a specific
            // HeapTable instance whose drop/recreate cycle isn't
            // SchemaVersion-tracked, so the plan-cache treats it as
            // session-scoped and declines.
            this.HasSessionScopedReference = true;
            this.ResolvedTempTable = true;
            this.CurrentStatement.ReadsTemporaryObject = true;
            return this.Connection.Simulation.GlobalTempTables.TryGetValue(name.Leaf, out table);
        }

        // Table-variable routing: @-prefixed leaves are per-batch, 1-part-only
        // (probe-confirmed: dbo.@t raises Msg 102 at parse). Dict key is the
        // @-stripped name (matches Variables dict convention). Table variables
        // are per-batch — no concurrency, no Sch-S. Strictly batch-scoped, so
        // any cached plan referencing one is invalid the moment it leaves the
        // owning batch.
        if (IsTableVariableName(name.Leaf))
        {
            if (name.Count > 1)
            {
                table = null;
                return false;
            }
            this.HasSessionScopedReference = true;
            this.CurrentStatement.ReadsTemporaryObject = true;
            this.CurrentStatement.ReadsTableVariable = true;
            return this.TableVariables.TryGetValue(name.Leaf[1..], out table);
        }

        if (!this.TryResolveSchema(name, out var schema))
        {
            // 1-part fallback to system tables when the default schema lookup
            // misses; matches the legacy bare-`systypes` access path. System
            // tables are immutable and SHARED across Simulations (the dict
            // lives in BuiltInResources as a static Value), so per-instance
            // lock acquisition would race across simulations — skip the
            // schema-stability acquire; nothing can DDL them anyway.
            if (name.Count == 1)
                return Simulation.SystemHeapTables.TryGetValue(name.Leaf, out table);
            table = null;
            return false;
        }

        if (schema.HeapTables.TryGetValue(name.Leaf, out table))
        {
            this.CurrentStatement.ReadsPermanentObject = true;
            this.AcquireStatementLock(table.SchemaLock, LockMode.SchemaStability);
            _ = this.DependencySink?.Tables.Add(table);
            return true;
        }

        // Synonym redirect: `FROM syn` where `syn FOR t` resolves through to
        // the base table (recursing so a schema-qualified base routes too).
        // A synonym whose base is a view returns false here and is picked up
        // by the caller's TryResolveView, which applies the same redirect.
        if (this.TryRedirectThroughSynonym(schema, name, out var tableBase))
            return this.TryResolveTable(tableBase, out table);

        // Bare 1-part also falls through to system tables when the default
        // schema doesn't hold the table — same shared-instance reasoning,
        // no Sch-S acquire.
        if (name.Count == 1)
            return Simulation.SystemHeapTables.TryGetValue(name.Leaf, out table);

        table = null;
        return false;
    }

    /// <summary>
    /// The database a written object name targets — the one a three-part
    /// name's leading segment resolves to, else <see cref="CurrentDatabase"/>.
    /// The pre-resolution counterpart to <see cref="DatabaseFor(SchemaObject)"/>:
    /// the DDL permission gates need the target database before the schema
    /// lookup that would produce an object to read it off. An unrecognized
    /// database segment falls back to the session's, leaving the miss for the
    /// caller's own resolution step to report.
    /// </summary>
    public Database DatabaseForName(MultiPartName name) =>
        name.Count == 3 && this.Connection.Simulation.Databases.TryGetValue(name[0], out var database)
            ? database
            : this.CurrentDatabase;

    /// <summary>
    /// Schema an unqualified name resolves to while a <c>CREATE SCHEMA
    /// &lt;name&gt; &lt;element&gt; …</c> element list is being bound — the
    /// schema being created rather than the principal's default. Real scopes the elements that
    /// way (probe-confirmed: an element's <c>CREATE TABLE t</c> lands in the new
    /// schema and a sibling <c>GRANT SELECT ON t</c> grants on it), and routing
    /// it through the one place unqualified names bind is what makes creation
    /// sites and reference sites agree. Null everywhere else.
    /// </summary>
    internal string? CreateSchemaElementScope;

    /// <summary>
    /// The schema a <c>CREATE SCHEMA</c> statement creates, while the compile
    /// pass walks its element list without creating it, so an element naming
    /// it as a qualifier defers to the run. Null everywhere else.
    /// </summary>
    internal string? SchemaCompiledUncreated;

    /// <summary>
    /// The schema a module body's unqualified references search before
    /// <c>dbo</c>: the module's own, whoever calls it — set on the batch a
    /// procedure, function, trigger or view body binds and runs in. Real binds
    /// a body's queries and DML (tables, views, functions, sequences, synonyms,
    /// types) this way, while its DDL, its <c>EXEC</c> of a procedure, the
    /// catalog functions it calls and the dynamic SQL it runs resolve through
    /// the caller's default schema, and an unqualified <c>CREATE</c> lands in
    /// the caller's (probed 2026-10-04 against SQL Server 2025). Null outside
    /// a module body.
    /// </summary>
    public Schema? ModuleSchema;

    /// <summary>
    /// The schema of the module a <c>CREATE</c> / <c>ALTER</c> statement is
    /// defining, which the body binds through as <see cref="ModuleSchema"/> when
    /// the statement checks it. Set as the statement resolves the module's name.
    /// </summary>
    public Schema? DefiningModuleSchema;

    /// <summary>
    /// Set by the dispatch loop for a statement that resolves its names as the
    /// caller does even inside a module body (see <see cref="ModuleSchema"/>):
    /// DDL, <c>EXEC</c> and the permission statements.
    /// </summary>
    public bool SuspendsModuleSchema;

    /// <summary>
    /// <see cref="DefaultSchemaName"/> as the batch began, which is what a
    /// <c>DECLARE</c>'s type binds through: real binds a batch's declarations
    /// when it compiles, so an <c>EXECUTE AS</c> earlier in the same batch
    /// doesn't reach them, where a query it recompiles per statement follows
    /// the switch (probed 2026-10-04 against SQL Server 2025). Taken as the
    /// batch dispatches its first statement; null before.
    /// </summary>
    public string? CompiledDefaultSchemaName;

    /// <summary>
    /// <see cref="DefaultSchemaName"/> and the identity and database it was
    /// derived for, null until first asked or seeded.
    /// </summary>
    private (string Name, SessionSecurityContext Security, int Generation, Database Database)? defaultSchema;

    /// <summary>
    /// The effective principal's default schema in <see cref="CurrentDatabase"/>
    /// (<see cref="SessionSecurityContext.EffectiveDefaultSchemaName"/>) — what an
    /// unqualified name searches before <c>dbo</c> outside a module body, and
    /// where an unqualified <c>CREATE</c> lands. Derived once per identity
    /// (<see cref="SessionSecurityContext.Generation"/>) and database, or seeded
    /// from the plan-cache key the batch took (<see cref="SeedDefaultSchemaName"/>).
    /// </summary>
    public string DefaultSchemaName
    {
        get
        {
            var security = this.Connection.Security;
            var database = this.CurrentDatabase;
            if (this.defaultSchema is { } cached
                && cached.Generation == security.Generation
                && ReferenceEquals(cached.Database, database)
                && ReferenceEquals(cached.Security, security))
            {
                return cached.Name;
            }
#if DEBUG
            // A key component (PlanCacheKey.DefaultSchemaName) that
            // MayCacheDmlPlan re-checks per statement, so a parse reading it
            // decides nothing a principal with another default schema could
            // replay.
            using var excused = PlanCacheCaptureAudit.SuspendPrincipalWatch();
#endif
            var name = security.EffectiveDefaultSchemaName(database);
            this.defaultSchema = (name, security, security.Generation, database);
            return name;
        }
    }

    /// <summary>Takes <paramref name="name"/> as <see cref="DefaultSchemaName"/> for the session's identity and database as they stand.</summary>
    public void SeedDefaultSchemaName(string name)
    {
        var security = this.Connection.Security;
        this.defaultSchema = (name, security, security.Generation, this.CurrentDatabase);
    }

    /// <summary>
    /// The schema of <see cref="CurrentDatabase"/> named <see cref="DefaultSchemaName"/>,
    /// or null when the principal's <c>DEFAULT_SCHEMA</c> names none —
    /// <c>SCHEMA_NAME()</c> / <c>SCHEMA_ID()</c> with no argument.
    /// </summary>
    public Schema? DefaultSchema =>
        this.CurrentDatabase.Schemas.TryGetValue(this.DefaultSchemaName, out var schema) ? schema : null;

    /// <summary>
    /// Resolves <paramref name="name"/> to the <see cref="Schema"/> an object
    /// reference lives in. Returns false when the schema doesn't exist, when a
    /// 3-part name's db segment doesn't match any database in
    /// <see cref="Simulation.Databases"/>, or when the name is 4-part
    /// (linked-server names aren't modeled — the simulator returns false rather
    /// than silently ignoring the server segment). 3-part names route to the
    /// named database, enabling cross-database SELECT / JOIN / catalog-view
    /// inspection and DML; the returned <see cref="Schema"/> carries its owning
    /// <see cref="Database"/> via <see cref="Schema.Database"/> so callers
    /// (catalog-view enumerators, constraint-violation error messages, etc.)
    /// can scope correctly. A 1-part name — and a <c>db..name</c> one —
    /// resolves through <see cref="TryResolveUnqualified"/>, to the first of
    /// the searched schemas holding an object of that name, else <c>dbo</c>.
    /// A <c>CREATE</c> asks <see cref="TryResolveCreateSchema"/> instead.
    /// </summary>
    public bool TryResolveSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema) =>
        this.TryResolveSchemaCore(name, static (candidate, leaf) => candidate.HoldsObjectNamed(leaf), callerScope: false, types: false, out schema);

    /// <summary>
    /// <see cref="TryResolveSchema"/> as the caller
    /// resolves, ignoring <see cref="ModuleSchema"/>: a procedure an
    /// <c>EXEC</c> names, and the name a catalog function (<c>OBJECT_ID</c>,
    /// <c>COL_LENGTH</c>, …) reads at run time.
    /// </summary>
    public bool TryResolveCallerSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema) =>
        this.TryResolveSchemaCore(name, static (candidate, leaf) => candidate.HoldsObjectNamed(leaf), callerScope: true, types: false, out schema);

    /// <summary>
    /// <see cref="TryResolveSchema"/> for a type
    /// name, searching the type namespace (table and alias types) and binding
    /// a batch's unqualified type through <see cref="CompiledDefaultSchemaName"/>.
    /// </summary>
    public bool TryResolveTypeSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema) =>
        this.TryResolveSchemaCore(name, static (candidate, leaf) => candidate.HoldsTypeNamed(leaf), callerScope: false, types: true, out schema);

    /// <summary>
    /// <see cref="TryResolveTypeSchema"/> as the caller resolves a type name
    /// when the statement runs — <c>DROP TYPE</c>, <c>TYPE_ID</c>,
    /// <c>TYPEPROPERTY</c> — through the principal's default schema as it
    /// stands rather than as the batch compiled.
    /// </summary>
    public bool TryResolveCallerTypeSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema) =>
        this.TryResolveSchemaCore(name, static (candidate, leaf) => candidate.HoldsTypeNamed(leaf), callerScope: true, types: false, out schema);

    /// <summary>
    /// <see cref="TryResolveTypeSchema"/> for an XML schema collection, whose
    /// names are a namespace of their own.
    /// </summary>
    public bool TryResolveXmlSchemaCollectionSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema) =>
        this.TryResolveSchemaCore(name, static (candidate, leaf) => candidate.XmlSchemaCollections.ContainsKey(leaf), callerScope: false, types: true, out schema);

    private bool TryResolveSchemaCore(MultiPartName name, Func<Schema, string, bool> holds, bool callerScope, bool types, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema)
    {
        if (name.Count >= 4)
        {
            schema = null;
            return false;
        }
        var database = this.CurrentDatabase;
        if (name.Count == 3)
        {
            // 3-part name: route to the named database (which may be the
            // current one or any other instance-hosted database). Missing
            // database returns false — the caller surfaces Msg 208 the same
            // way a missing table does, matching real SQL Server.
            if (!this.Connection.Simulation.Databases.TryGetValue(name[0], out database))
            {
                schema = null;
                return false;
            }
        }
        return name.Count == 1 || name.SchemaOmitted
            ? this.TryResolveUnqualified(database, name.Leaf, holds, callerScope, types, out schema)
            : database.Schemas.TryGetValue(name.ImmediateQualifier!, out schema);
    }

    /// <summary>
    /// The schema of <paramref name="database"/> an unqualified
    /// <paramref name="leaf"/> resolves into: the first searched schema
    /// <paramref name="holds"/> it in, else <c>dbo</c>. Real searches one schema
    /// before <c>dbo</c> — the module's (<see cref="ModuleSchema"/>) for a
    /// module body's references, otherwise the principal's default
    /// (<see cref="DefaultSchemaName"/>) — and an object of any kind there
    /// shadows <c>dbo</c>'s; a principal's default searches only its own
    /// database. A <c>CREATE SCHEMA</c> element list searches the new schema
    /// alone (<see cref="CreateSchemaElementScope"/>).
    /// </summary>
    private bool TryResolveUnqualified(Database database, string leaf, Func<Schema, string, bool> holds, bool callerScope, bool types, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema)
    {
        if (this.CreateSchemaElementScope is { } elementScope)
            return database.Schemas.TryGetValue(elementScope, out schema);
        if (!callerScope && !this.SuspendsModuleSchema && this.ModuleSchema is { } module && ReferenceEquals(module.Database, database))
        {
            if (module.SchemaId != Database.DboSchemaId && holds(module, leaf))
            {
                schema = module;
                return true;
            }
        }
        else if (ReferenceEquals(database, this.CurrentDatabase))
        {
            // dbo and every principal declaring no default schema share the
            // interned constant, so they search dbo alone without a lookup.
            var first = types ? this.CompiledDefaultSchemaName ?? this.DefaultSchemaName : this.DefaultSchemaName;
            if (!ReferenceEquals(first, Database.DefaultSchemaName) && database.Schemas.TryGetValue(first, out var candidate) && holds(candidate, leaf))
            {
                schema = candidate;
                return true;
            }
        }
        return database.Schemas.TryGetValue(Database.DefaultSchemaName, out schema);
    }

    /// <summary>
    /// Resolves the schema a <c>CREATE</c> (or <c>SELECT … INTO</c>) of
    /// <paramref name="name"/> places its object in: a written schema as
    /// <see cref="TryResolveSchema"/> finds it, an
    /// unqualified name the principal's default schema — the caller's even
    /// inside a module body — or a <c>CREATE SCHEMA</c> element list's new
    /// schema. A default schema that doesn't exist is Msg 2797 at
    /// <paramref name="missingDefaultState"/>, ending the batch unless
    /// <paramref name="statementOnly"/> (probed 2026-10-04 against SQL Server
    /// 2025).
    /// </summary>
    public bool TryResolveCreateSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema, byte missingDefaultState = 1, bool statementOnly = false)
    {
        if (name.Count != 1 && !name.SchemaOmitted)
            return this.TryResolveSchema(name, out schema);
        var database = name.Count == 3 && this.Connection.Simulation.Databases.TryGetValue(name[0], out var named) ? named : this.CurrentDatabase;
        if (this.CreateSchemaElementScope is { } elementScope)
            return database.Schemas.TryGetValue(elementScope, out schema);
        var target = ReferenceEquals(database, this.CurrentDatabase) ? this.DefaultSchemaName : Database.DefaultSchemaName;
        return database.Schemas.TryGetValue(target, out schema) ? true : throw SimulatedSqlException.DefaultSchemaDoesNotExist(missingDefaultState, terminatesBatch: !statementOnly);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered scalar
    /// <see cref="UserDefinedFunction"/>. Schema-qualified (2- or 3-part)
    /// references route through <see cref="TryResolveSchema"/>; 1-part names
    /// fall through to <see langword="false"/> (real SQL Server treats
    /// unqualified UDF calls as built-in function lookups, raising Msg 195
    /// when nothing matches — the call site enforces that 2-part minimum by
    /// only invoking this resolver when <see cref="MultiPartName.Count"/>
    /// is &gt;= 2).
    /// </summary>
    public bool TryResolveFunction(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserDefinedFunction? function)
    {
        function = null;
        return name.Count >= 2 && this.TryResolveFunctionCore(name, out function);
    }

    /// <summary>
    /// <see cref="TryResolveFunction"/> for a name looked up rather than
    /// called — <c>OBJECT_ID('f')</c> — which a one-part name reaches through
    /// the unqualified search.
    /// </summary>
    public bool TryResolveFunctionName(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserDefinedFunction? function) =>
        this.TryResolveFunctionCore(name, out function);

    private bool TryResolveFunctionCore(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserDefinedFunction? function)
    {
        function = null;
        if (!this.TryResolveSchema(name, out var schema))
            return false;
        if (!schema.Functions.TryGetValue(name.Leaf, out function))
        {
            // Synonym redirect: `SELECT dbo.syn(1)` where `syn FOR f` calls the
            // base scalar function, and `FROM dbo.syn(1)` the base TVF
            // (probe-confirmed both work on real). A base written unqualified
            // resolves as an unqualified name, since a 1-part name never
            // reaches function resolution at a call site (Msg 195).
            return this.TryRedirectThroughSynonym(schema, name, out var functionBase)
                && this.TryResolveFunctionCore(functionBase, out function);
        }
        this.AcquireStatementLock(function.SchemaLock, LockMode.SchemaStability);
        this.CurrentStatement.MarkOpensTransaction();
        this.CurrentStatement.CallsUserFunction = true;
        return true;
    }

    /// <summary>
    /// <see cref="TryResolveFunction"/> for a FROM or APPLY source, which
    /// answers only a table-valued function and which a one-part name reaches
    /// through the default schema, where a scalar call's never does (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    public bool TryResolveTableValuedFunction(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserDefinedFunction? function) =>
        this.TryResolveFunctionCore(name, out function)
        && function is InlineTableValuedFunction or MultiStatementTableValuedFunction or ClrTableValuedFunction;

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered <see cref="View"/>.
    /// Unlike scalar UDFs, views accept 1-part names too (probe-confirmed:
    /// <c>FROM v1</c> works the same as <c>FROM dbo.v1</c>) through the
    /// unqualified search (<see cref="TryResolveUnqualified"/>). Schema-qualified misses return false; the caller
    /// is responsible for routing those to Msg 208.
    /// </summary>
    public bool TryResolveView(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out View? view)
    {
        view = null;
        if (!this.TryResolveSchema(name, out var schema))
            return false;
        if (!schema.Views.TryGetValue(name.Leaf, out view))
        {
            // Synonym redirect for a synonym whose base is a view (see
            // TryResolveTable for the table-base case).
            return this.TryRedirectThroughSynonym(schema, name, out var viewBase)
                && this.TryResolveView(viewBase, out view);
        }
        this.CurrentStatement.ReadsPermanentObject = true;
        this.AcquireStatementLock(view.SchemaLock, LockMode.SchemaStability);
        _ = this.DependencySink?.Views.Add(view);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered <see cref="Procedure"/>.
    /// Like views (and unlike scalar UDFs), procedures accept 1-part names —
    /// probe-confirmed: <c>EXEC p1</c> finds <c>dbo.p1</c>. The lookup falls
    /// back to the caller's default schema and then <c>dbo</c> for the
    /// unqualified case — inside a module body too, where real doesn't search
    /// the module's schema for a procedure (probed 2026-10-04 against SQL
    /// Server 2025); schema-qualified misses return false (caller routes to
    /// Msg 2812).
    /// </summary>
    public bool TryResolveProcedure(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Procedure? procedure)
    {
        procedure = null;
        if (!this.TryResolveCallerSchema(name, out var schema)
            || !schema.Procedures.TryGetValue(name.Leaf, out procedure))
        {
            return false;
        }
        this.AcquireStatementLock(procedure.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered DML <see cref="Trigger"/>.
    /// Triggers share the schema's object-name namespace; an unqualified name
    /// searches as the caller does (<see cref="TryResolveCallerSchema"/>).
    /// Used by <c>OBJECT_ID</c> so the canonical
    /// <c>OBJECT_DEFINITION(OBJECT_ID('trg'))</c> idiom resolves. DDL triggers
    /// (database-scoped, not schema-resident) aren't covered here.
    /// </summary>
    public bool TryResolveTrigger(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Trigger? trigger)
    {
        trigger = null;
        if (!this.TryResolveCallerSchema(name, out var schema)
            || !schema.Triggers.TryGetValue(name.Leaf, out trigger))
        {
            return false;
        }
        this.AcquireStatementLock(trigger.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered user-defined
    /// <see cref="TableType"/>. Like views / procedures (and unlike scalar
    /// UDFs), table types accept 1-part names: probe-confirmed against SQL
    /// Server 2025 that <c>DECLARE @t MyType</c> finds <c>dbo.MyType</c>.
    /// An unqualified name searches the type namespace
    /// (<see cref="TryResolveTypeSchema"/>).
    /// </summary>
    public bool TryResolveTableType(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TableType? tableType)
    {
        tableType = null;
        if (!this.TryResolveTypeSchema(name, out var schema)
            || !schema.TableTypes.TryGetValue(name.Leaf, out tableType))
        {
            return false;
        }
        this.AcquireStatementLock(tableType.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered scalar
    /// <see cref="AliasType"/> (UDDT) in the per-database schema dictionary.
    /// Like table types, alias types accept 1-part names, searching the type
    /// namespace (<see cref="TryResolveTypeSchema"/>). Used by every type-
    /// reference parser site (CREATE TABLE column, DECLARE @v, procedure /
    /// function / sequence param, ALTER TABLE ALTER COLUMN, OPENJSON, EXEC
    /// dynamic-SQL parameter) to determine whether a parsed type reference
    /// expands to a built-in or to an alias's underlying type.
    /// </summary>
    public bool TryResolveAliasType(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AliasType? aliasType)
    {
        aliasType = null;
        return this.TryResolveTypeSchema(name, out var schema)
            && schema.AliasTypes.TryGetValue(name.Leaf, out aliasType);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered <see cref="Sequence"/>.
    /// Accepts 1-part names (probe-confirmed: <c>NEXT VALUE FOR seq1</c> finds
    /// <c>dbo.seq1</c>) through the unqualified search
    /// (<see cref="TryResolveUnqualified"/>); 2-part / 3-part qualified routes
    /// through the named schema. Returns false
    /// on miss (caller routes to Msg 208 for unknown name or Msg 11726 if the
    /// name resolves to a non-sequence object).
    /// </summary>
    public bool TryResolveSequence(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Sequence? sequence)
    {
        sequence = null;
        if (!this.TryResolveSchema(name, out var schema)
            || !schema.Sequences.TryGetValue(name.Leaf, out sequence))
        {
            return false;
        }
        this.AcquireStatementLock(sequence.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a <see cref="Synonym"/> — the
    /// synonym object itself, without following it to its base. Accepts 1-part
    /// names through the unqualified search (<see cref="TryResolveUnqualified"/>).
    /// Used by <c>OBJECT_ID</c> (which reports a synonym's own id and never
    /// follows it — probe-confirmed <c>OBJECT_ID('syn', 'U')</c> is NULL) and
    /// by the DROP / error paths that need to know a name is a synonym.
    /// </summary>
    public bool TryResolveSynonym(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Synonym? synonym)
    {
        synonym = null;
        if (!this.TryResolveSchema(name, out var schema) || !schema.Synonyms.TryGetValue(name.Leaf, out synonym))
            return false;
        this.AcquireStatementLock(synonym.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves the object a <paramref name="synonym"/> points at, by direct
    /// dictionary lookup rather than through the kind-specific resolvers (so
    /// no second redirect is applied). Returns false when the base name has no
    /// object behind it — the deferred-resolution case real reports as
    /// Msg 5313 at first use.
    /// </summary>
    public bool TryResolveSynonymBase(Synonym synonym, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SchemaObject? baseObject)
    {
        baseObject = null;
        if (!this.TryResolveSchema(synonym.BaseObject, out var schema))
            return false;
        var collation = schema.Database.Collation;
        foreach (var candidate in schema.SchemaObjects())
        {
            if (collation.Equals(candidate.Name, synonym.BaseObject.Leaf))
            {
                baseObject = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Rewrites <paramref name="name"/> to the base object it names when it is
    /// a synonym, leaving any other name untouched. The EXEC path expands
    /// before resolving so a synonym over a missing procedure reports Msg 2812
    /// naming the base, matching real.
    /// </summary>
    public MultiPartName ExpandSynonym(MultiPartName name) =>
        this.TryResolveCallerSchema(name, out var schema) && this.TryRedirectThroughSynonym(schema, name, out var baseName)
            ? baseName
            : name;

    /// <summary>
    /// The error a reference to <paramref name="name"/> raises when nothing
    /// resolved: Msg 208 for an unknown name, or Msg 5313 when the name is a
    /// synonym (whose base binds lazily, so the failure belongs to the base,
    /// not the synonym). Real distinguishes the two 5313 states — 1 when the
    /// base names nothing, 224 when it names an object the reference can't use
    /// (a procedure or sequence in a FROM clause).
    /// </summary>
    /// <remarks>
    /// Binding a module definition, real reports a missing schema-qualified
    /// object at line 12 whatever the definition's layout, and an unqualified
    /// one at the line of its name (probed 2026-09-26 against SQL Server 2025).
    /// </remarks>
    public SimulatedSqlException UnresolvableObjectName(MultiPartName name)
    {
        if (this.TryResolveSynonym(name, out var synonym))
            return SimulatedSqlException.SynonymRefersToInvalidObject(name.ToString(), this.TryResolveSynonymBase(synonym, out _) ? (byte)224 : (byte)1);
        var error = SimulatedSqlException.InvalidObjectName(name);
        if (!this.CreateTimeBinding && !this.Parser.BindingViewDefinition && !this.BindsModuleDefinition)
            return error;
        if (name.Count >= 2)
            return error.PinLine(12);
        return this.Parser.Token is { } leaf ? error.PinLine(leaf.LineNumber + this.LineOffset) : error;
    }

    /// <summary>
    /// The shared synonym-redirect step behind <see cref="TryResolveTable"/> /
    /// <see cref="TryResolveView"/> / <see cref="TryResolveFunction"/>: hands
    /// back the base name when <paramref name="name"/>'s leaf is a synonym in
    /// <paramref name="schema"/>. A base that is itself a synonym raises
    /// Msg 470 — real accepts the <c>CREATE SYNONYM</c> that builds the chain
    /// and rejects it at first use, so the check belongs here rather than at
    /// creation.
    /// </summary>
    private bool TryRedirectThroughSynonym(Schema schema, MultiPartName name, out MultiPartName baseName)
    {
        if (!schema.Synonyms.TryGetValue(name.Leaf, out var synonym))
        {
            baseName = default;
            return false;
        }
        this.AcquireStatementLock(synonym.SchemaLock, LockMode.SchemaStability);
        baseName = synonym.BaseObject;
        return this.TryResolveSchema(baseName, out var baseSchema) && baseSchema.Synonyms.ContainsKey(baseName.Leaf)
            ? throw SimulatedSqlException.SynonymChainingNotAllowed(name.ToString(), baseName.ToString())
            : true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a <see cref="CatalogView"/> in
    /// either the <c>sys</c> or <c>INFORMATION_SCHEMA</c> schema. Returns
    /// true for 2-part names <c>{sys|INFORMATION_SCHEMA}.&lt;view&gt;</c>
    /// (case-insensitive) whose leaf matches a registered view, or for
    /// 3-part names whose db segment names a database in
    /// <see cref="Simulation.Databases"/>. <paramref name="targetDatabase"/>
    /// receives the database the view is scoped to — the connection's
    /// <see cref="CurrentDatabase"/> for 2-part references, the resolved
    /// instance-hosted database for 3-part references. The catalog-view row
    /// generator reads from this rather than <c>batch.CurrentDatabase</c>
    /// so <c>WideWorldImporters.sys.tables</c> projects WWI's tables even
    /// when the connection is pointed at a different DB.
    /// Used by the FROM parser to route catalog-view references to virtual
    /// projections before falling through to the regular
    /// <see cref="TryResolveTable"/> path. The registry is keyed by the
    /// fully-qualified name (e.g. <c>"sys.tables"</c>,
    /// <c>"INFORMATION_SCHEMA.COLUMNS"</c>) so one resolver can serve both
    /// schemas without per-namespace dispatch.
    /// </summary>
    public bool TryResolveCatalogView(
        MultiPartName name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogView? view,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Database? targetDatabase)
    {
        view = null;
        targetDatabase = null;
        if (name.Count is not (1 or 2 or 3))
            return false;
        targetDatabase = this.CurrentDatabase;
        if (name.Count == 3 && !this.Connection.Simulation.Databases.TryGetValue(name[0], out targetDatabase))
            return false;
        // A 1-part name resolves only the legacy compatibility views registered
        // under a bare (dot-less) key — sysobjects / sysusers, which live in the
        // sys schema but resolve unqualified (probe-confirmed: bare `sysobjects`
        // works, bare `objects` / `tables` raise Msg 208). Every modern catalog
        // view is keyed `sys.<name>` / `INFORMATION_SCHEMA.<name>`, so a bare
        // user-table name never collides.
        var key = name.Count == 1 ? name.Leaf : $"{name.ImmediateQualifier}.{name.Leaf}";
        if (!Simulation.CatalogViews.TryGetValue(key, out view)
            && !TryResolveCompatibilityViewThroughDbo(name, targetDatabase, out view))
        {
            return false;
        }
        // master.dbo.spt_values (and its 1-/2-part forms) resolve only when the
        // reference lands in master — the compatibility table exists nowhere else.
        if (view.MasterScoped && !Collation.Baseline.Equals(targetDatabase.Name, Simulation.MasterDatabaseName))
        {
            view = null;
            targetDatabase = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// A compatibility view (<c>sysobjects</c>, <c>sysprocesses</c>, …) also
    /// resolves under <c>dbo</c> — <c>dbo.sysobjects</c>,
    /// <c>master.dbo.sysprocesses</c>, <c>master..sysobjects</c> — where a
    /// modern catalog view doesn't (<c>dbo.tables</c> is Msg 208), unless a
    /// <c>dbo</c> object of that name is there to take it (probed 2026-09-30
    /// against SQL Server 2025).
    /// </summary>
    private static bool TryResolveCompatibilityViewThroughDbo(MultiPartName name, Database targetDatabase, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogView? view)
    {
        view = null;
        return name.Count > 1
            && Collation.Baseline.Equals(name.ImmediateQualifier, Database.DefaultSchemaName)
            && Simulation.CatalogViews.TryGetValue(name.Leaf, out view)
            && !(targetDatabase.Schemas.TryGetValue(Database.DefaultSchemaName, out var dbo)
                && (dbo.HeapTables.ContainsKey(name.Leaf) || dbo.Views.ContainsKey(name.Leaf) || dbo.Synonyms.ContainsKey(name.Leaf)));
    }

    /// <summary>
    /// Routes a four-part name (<c>server.db.schema.t</c>) through the
    /// connection's <see cref="Simulation.ActiveLinkedServers"/> dict to
    /// the matching <see cref="LinkedServer"/> + remote <see cref="HeapTable"/>.
    /// Returns false for non-4-part names (so callers can fall through to
    /// regular table resolution); returns false for 4-part names whose
    /// leading segment isn't an active linked server (caller surfaces
    /// Msg 208 via the standard <c>InvalidObjectName</c> path). On a
    /// successful match, <paramref name="remoteDatabaseName"/> /
    /// <paramref name="remoteSchemaName"/> are the resolved literal
    /// segments (with empty-middle <c>srv..t</c> substitution applied via
    /// <see cref="Database.DefaultSchemaName"/>) so the caller can plumb
    /// them into the remote-query SQL string.
    /// </summary>
    /// <remarks>
    /// Looks up the remote table or view via direct access to
    /// <see cref="LinkedServer.Target"/>'s <see cref="Database.Schemas"/>
    /// dict — parse-time metadata stays in-process even though execution
    /// round-trips through the remote's public ADO.NET surface. Matches
    /// real SQL Server's "metadata at compile, data at execute" linked-
    /// server contract; the in-process shortcut avoids a no-row query on
    /// every parse without changing observable behavior.
    /// </remarks>
    public bool TryResolveLinkedServerTable(
        MultiPartName name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LinkedServer? linkedServer,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapColumn[]? remoteColumns,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteDatabaseName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteSchemaName)
    {
        linkedServer = null;
        remoteName = null;
        remoteColumns = null;
        remoteDatabaseName = null;
        remoteSchemaName = null;
        return name.Count == 4
            && this.Connection.Simulation.ActiveLinkedServers.TryGetValue(name[0], out linkedServer)
            && TryResolveRemoteTable(linkedServer, name, out remoteName, out remoteColumns, out remoteDatabaseName, out remoteSchemaName);
    }

    /// <summary>
    /// The table or view the last three segments of the four-part
    /// <paramref name="name"/> name on <paramref name="linkedServer"/> — a
    /// linked server, or the one an ad hoc <c>OPENROWSET</c> connects to —
    /// as <see cref="TryResolveLinkedServerTable"/> describes.
    /// </summary>
    public static bool TryResolveRemoteTable(
        LinkedServer linkedServer,
        MultiPartName name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapColumn[]? remoteColumns,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteDatabaseName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteSchemaName)
    {
        remoteName = null;
        remoteColumns = null;
        remoteDatabaseName = null;
        remoteSchemaName = null;

        // Empty middle segments fall back to defaults: missing db → the
        // database a fresh session of the server starts in; missing schema →
        // the remote's per-database DefaultSchemaName.
        var dbSegment = string.IsNullOrEmpty(name[1]) ? linkedServer.SessionDatabaseName : name[1];
        if (!linkedServer.Target.Databases.TryGetValue(dbSegment, out var remoteDatabase))
            return false;
        var schemaSegment = string.IsNullOrEmpty(name[2]) ? Database.DefaultSchemaName : name[2];
        if (!remoteDatabase.Schemas.TryGetValue(schemaSegment, out var remoteSchema))
            return false;
        // A view reads as its projection does.
        if (remoteSchema.HeapTables.TryGetValue(name.Leaf, out var remoteTable))
        {
            remoteName = remoteTable.Name;
            remoteColumns = Array.FindAll(remoteTable.Columns, column => !column.IsHidden);
        }
        else if (remoteSchema.Views.TryGetValue(name.Leaf, out var remoteView))
        {
            remoteName = remoteView.Name;
            remoteColumns = remoteView.OutputColumns;
        }
        else
        {
            return false;
        }
        remoteColumns = RemoteWrite.ProviderColumns(linkedServer, name, remoteColumns);
        remoteDatabaseName = dbSegment;
        remoteSchemaName = schemaSegment;
        return true;
    }

    /// <summary>
    /// Parses an object name (1–4 dotted segments) at the current token,
    /// leaving the cursor on the <em>last</em> consumed name segment (matching
    /// the standard parser-context contract that every parser leaves Token on
    /// its last consumed token). An empty middle segment (<c>db..table</c>,
    /// <c>tempdb..#foo</c>) substitutes <c>dbo</c> as a placeholder and marks the
    /// name <see cref="MultiPartName.SchemaOmitted"/>, which the resolvers read
    /// as an unqualified name in that database — real searches the principal's
    /// default schema there first (probed 2026-10-04 against SQL Server 2025).
    /// Used everywhere a table-shaped name appears (CREATE / DROP / TRUNCATE
    /// / SELECT-FROM / INSERT / UPDATE / DELETE / MERGE / SET IDENTITY_INSERT)
    /// so the multi-part-name grammar lives in one place. The 5th segment
    /// raises Msg 4104 via <see cref="MultiPartName.WithAddedPart"/>.
    /// </summary>
    public static MultiPartName ParseObjectName(ParserContext context, bool acceptTableVariable = false)
    {
        // Table-variable references (@t in DML target / FROM-source position
        // when <paramref name="acceptTableVariable"/> is true): accept as a
        // 1-part name with the @ kept in the leaf so downstream routing
        // (TryResolveTable's IsTableVariableName check) can identify it. A
        // trailing `.` raises a syntax error matching the probe-confirmed
        // Msg 102 for `dbo.@t` (real SQL Server rejects any dotted form
        // involving an @-prefixed segment at parse time). Contexts where @t
        // isn't legal (CREATE TABLE / ALTER TABLE / DROP TABLE / TRUNCATE
        // TABLE / SELECT INTO) leave <paramref name="acceptTableVariable"/>
        // false so the @ token falls through to a syntax error — matches
        // probe-confirmed Msg 102 for those statement shapes.
        if (acceptTableVariable && context.Token is AtPrefixedString atVar)
        {
            var leaf = "@" + atVar.Value;
            var atCheckpoint = context.SaveCheckpoint();
            if (context.MoveNext() && context.Token is Operator { Character: '.' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.RestoreCheckpoint(atCheckpoint);
            return new MultiPartName(leaf);
        }
        // Leading empty segment(s): `..name` (db + schema omitted) or
        // `.name` (schema omitted) resolve to the current database / default
        // schema, matching real SQL Server (`SELECT * FROM ..t` reads dbo.t
        // in the current db; `EXEC ..sp_foo` runs the system/dbo proc; the
        // form SqlClient's SqlBulkCopy metadata batch sends is
        // `exec ..sp_tablecollations_100`). Drop the omitted leading
        // positions — an empty db means the current database anyway, and the
        // trailing segments carry the schema/object identity — so the name
        // parses as if the empties weren't written.
        var omittedLeading = 0;
        while (context.Token is Operator { Character: '.' })
        {
            omittedLeading++;
            if (!context.MoveNext())
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        var schemaOmitted = false;

        if (context.Token is not Name first)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = new MultiPartName(first.Value);
        while (true)
        {
            // Peek for a `.` continuation without permanently advancing — if
            // the next token isn't a dot, restore so the cursor sits on the
            // last consumed name segment.
            var checkpoint = context.SaveCheckpoint();
            if (!context.MoveNext() || context.Token is not Operator { Character: '.' })
            {
                context.RestoreCheckpoint(checkpoint);
                return name.WithOmissions(omittedLeading, schemaOmitted);
            }

            // Advanced past the dot. Read the next segment — a Name extends
            // the dotted name; a second `.` is an empty segment that we skip
            // and read one more time.
            if (!context.MoveNext())
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is Name next)
            {
                name = name.WithAddedPart(next.Value);
                continue;
            }
            if (context.Token is Operator { Character: '.' } && context.MoveNext() && context.Token is Name afterEmpty)
            {
                // Empty middle segment (`db..table`). Substitute dbo and mark
                // the schema omitted, which the resolvers read as an
                // unqualified name in that database. The pattern is required
                // for cross-database short-form queries to land in the
                // correct database (the leading segment routes to that DB
                // rather than being interpreted as a schema in the current
                // DB).
                name = name.WithAddedPart(Database.DefaultSchemaName).WithAddedPart(afterEmpty.Value);
                schemaOmitted = true;
                continue;
            }
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
