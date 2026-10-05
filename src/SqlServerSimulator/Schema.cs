using System.Collections.Concurrent;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One namespace inside a <see cref="Database"/>. A <see cref="Schema"/>
/// holds the user tables that resolve under its name; <c>SELECT * FROM
/// audit.t</c> routes through <see cref="Database.Schemas"/>[<c>"audit"</c>]
/// to find <c>t</c>. Every <see cref="Database"/> ships with a
/// <see cref="Database.DefaultSchemaName"/>-named instance; <c>CREATE SCHEMA
/// &lt;name&gt;</c> adds more. Views / procs / sequences will land alongside
/// <see cref="HeapTables"/> here when those features arrive — the type's
/// shape is in place so they can graft on without touching the resolution
/// rule again.
/// </summary>
internal sealed class Schema
{
    /// <summary>
    /// Owning <see cref="Database"/>. Back-pointer threaded through resolvers
    /// so a caller holding a <see cref="Schema"/> reference can tell which
    /// database it belongs to without keeping the database alongside —
    /// required for cross-database name resolution (3-part names route to a
    /// different <see cref="Database"/>) and for catalog-view enumerators
    /// that scope their output to the resolved database, not the connection's
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/>.
    /// </summary>
    public readonly Database Database;

    public readonly string Name;

    /// <summary>
    /// Per-database schema identifier — surfaces in <c>SCHEMA_ID()</c>,
    /// <c>sys.schemas.schema_id</c>, <c>sys.tables.schema_id</c>, and
    /// <c>sys.objects.schema_id</c>. The built-in schemas use the real-SQL-
    /// Server-conventional values (<c>dbo=1</c>, <c>INFORMATION_SCHEMA=3</c>,
    /// <c>sys=4</c>); user schemas allocate from <see cref="Database.AllocateSchemaId"/>
    /// starting at 5. Apps occasionally hard-code <c>schema_id = 1</c> for
    /// dbo, so matching is worth the small bit of setup logic.
    /// </summary>
    public readonly int SchemaId;

    /// <summary>
    /// Id of the database principal that owns this schema — what
    /// <c>sys.schemas.principal_id</c> projects. <c>CREATE SCHEMA … AUTHORIZATION
    /// &lt;principal&gt;</c> sets it; a schema created without the clause is
    /// owned by <c>dbo</c>, matching real. A principal that owns a schema can't
    /// be dropped (Msg 15138).
    /// </summary>
    public int PrincipalId = Database.DboPrincipalId;

    public Schema(Database database, string name, int schemaId)
    {
        this.Database = database;
        this.Name = name;
        this.SchemaId = schemaId;
        var collation = database.Collation;
        this.HeapTables = new(collation);
        this.Functions = new(collation);
        this.Views = new(collation);
        this.Procedures = new(collation);
        this.TableTypes = new(collation);
        this.AliasTypes = new(collation);
        this.XmlSchemaCollections = new(collation);
        this.Sequences = new(collation);
        this.Triggers = new(collation);
        this.Synonyms = new(collation);
        this.Defaults = new(collation);
        this.Rules = new(collation);
        this.SecurityPolicies = new(collation);
    }

    public readonly ConcurrentDictionary<string, HeapTable> HeapTables;

    /// <summary>
    /// User-defined functions hosted by this schema — either scalar
    /// (<see cref="ScalarFunction"/>) or inline TVF
    /// (<see cref="InlineTableValuedFunction"/>) under the
    /// <see cref="UserDefinedFunction"/> base.
    /// </summary>
    public readonly ConcurrentDictionary<string, UserDefinedFunction> Functions;

    /// <summary>
    /// Views hosted by this schema. <c>CREATE VIEW schema.name AS SELECT
    /// ...</c> adds entries here; FROM-clause references resolve through
    /// this dict before falling through to the heap-table lookup. Views
    /// share the same name namespace as tables (Msg 2714 on collision),
    /// so a view's leaf name is unique across both dicts within the schema.
    /// </summary>
    public readonly ConcurrentDictionary<string, View> Views;

    /// <summary>
    /// Stored procedures hosted by this schema. <c>CREATE PROCEDURE
    /// schema.name AS ...</c> adds entries; <c>EXEC schema.name ...</c>
    /// resolves through this dict. Procedures share the object-name
    /// namespace with tables / views / functions (Msg 2714 on collision).
    /// <c>ALTER PROCEDURE</c> replaces the entry while preserving the
    /// existing <see cref="SchemaObject.ObjectId"/>; <c>DROP PROCEDURE</c>
    /// removes it.
    /// </summary>
    public readonly ConcurrentDictionary<string, Procedure> Procedures;

    /// <summary>
    /// User-defined table types hosted by this schema. Created via
    /// <c>CREATE TYPE schema.name AS TABLE (...)</c>, consumed by
    /// <c>DECLARE @t schema.name</c> and as <c>READONLY</c> procedure
    /// parameters (TVPs). Probed against SQL Server 2025: type names occupy
    /// a separate namespace from tables / views / functions / procs — a
    /// table type can share a leaf with a table (Msg 219 on dup type name
    /// only). Alias types (<see cref="AliasTypes"/>) share this same
    /// type-name namespace.
    /// </summary>
    public readonly ConcurrentDictionary<string, TableType> TableTypes;

    /// <summary>
    /// Scalar user-defined alias types (UDDTs) hosted by this schema.
    /// Created via <c>CREATE TYPE schema.name FROM &lt;builtin&gt;[(N[, S])]
    /// [NULL | NOT NULL]</c>, dropped via <c>DROP TYPE schema.name</c>,
    /// consumed wherever a builtin type name is legal (CREATE TABLE column
    /// type, DECLARE @v, procedure / function parameter, etc.). Shares the
    /// type-name namespace with <see cref="TableTypes"/> — a CREATE TYPE
    /// colliding with an existing entry in either dict raises Msg 219.
    /// </summary>
    public readonly ConcurrentDictionary<string, AliasType> AliasTypes;

    /// <summary>
    /// XML schema collections hosted by this schema. Created via
    /// <c>CREATE XML SCHEMA COLLECTION schema.name AS '&lt;xsd:schema&gt;…'</c>,
    /// referenced by per-column <c>xml(collection_name)</c> type
    /// declarations. Shares the type-name namespace with
    /// <see cref="TableTypes"/> / <see cref="AliasTypes"/> (Msg 219 on
    /// duplicate). A value written to a bound column, variable or CAST
    /// target validates against it.
    /// </summary>
    public readonly ConcurrentDictionary<string, XmlSchemaCollection> XmlSchemaCollections;

    /// <summary>
    /// Sequence objects hosted by this schema. Created via <c>CREATE
    /// SEQUENCE schema.name ...</c>, consumed via <c>NEXT VALUE FOR
    /// schema.name</c>. Shares the object-name namespace with tables /
    /// views / functions / procs (Msg 2714 on collision).
    /// </summary>
    public readonly ConcurrentDictionary<string, Sequence> Sequences;

    /// <summary>
    /// DML triggers hosted by this schema. Created via <c>CREATE [OR
    /// ALTER] TRIGGER schema.name ON schema.parent { AFTER | INSTEAD OF }
    /// ... AS body</c>; fired automatically by INSERT / UPDATE / DELETE /
    /// MERGE against the trigger's parent (table or view). The trigger
    /// NAME shares the object-name namespace with tables / views /
    /// functions / procs / sequences (Msg 2714 on collision). Lookup at
    /// DML time scans this dict for triggers whose <see cref="Trigger.Parent"/>
    /// matches the DML target — a per-table cache lives on
    /// <see cref="HeapTable"/> itself but the dict here is the source
    /// of truth (ENABLE / DISABLE / DROP all operate on it).
    /// </summary>
    public readonly ConcurrentDictionary<string, Trigger> Triggers;

    /// <summary>
    /// Synonyms hosted by this schema. Created via <c>CREATE SYNONYM
    /// [schema.]name FOR base</c>, resolved at binding time by redirecting a
    /// reference to the synonym onto its base object (see <see cref="Synonym"/>).
    /// Shares the object-name namespace with tables / views / … in both
    /// directions (Msg 2714) and projects through <c>sys.synonyms</c> /
    /// <c>sys.objects</c>.
    /// </summary>
    public readonly ConcurrentDictionary<string, Synonym> Synonyms;

    /// <summary>
    /// <c>CREATE DEFAULT</c> objects hosted by this schema, bound to columns and
    /// alias types with <c>sp_bindefault</c> (see <see cref="DefaultObject"/>).
    /// </summary>
    public readonly ConcurrentDictionary<string, DefaultObject> Defaults;

    /// <summary>
    /// <c>CREATE RULE</c> objects hosted by this schema, bound to columns and
    /// alias types with <c>sp_bindrule</c> (see <see cref="RuleObject"/>).
    /// </summary>
    public readonly ConcurrentDictionary<string, RuleObject> Rules;

    /// <summary>
    /// Row-level security policies hosted by this schema (see
    /// <see cref="SecurityPolicy"/>), sharing the object-name namespace.
    /// </summary>
    public readonly ConcurrentDictionary<string, SecurityPolicy> SecurityPolicies;

    /// <summary>
    /// Yields every <see cref="SchemaObject"/> in this schema's
    /// object-name namespace (heap tables, views, UDFs, procedures,
    /// sequences, triggers, synonyms, defaults, rules, security policies) — the set whose leaf names must be
    /// unique (Msg 2714 on CREATE collision). <see cref="TableTypes"/> are
    /// deliberately omitted: probe-confirmed that table-type names occupy
    /// a separate namespace from this set. Used by sys.objects projection
    /// and by the CREATE-time auto-name-collision check.
    /// </summary>
    public IEnumerable<SchemaObject> SchemaObjects()
    {
        foreach (var (_, t) in this.HeapTables) yield return t;
        foreach (var (_, v) in this.Views) yield return v;
        foreach (var (_, fn) in this.Functions) yield return fn;
        foreach (var (_, p) in this.Procedures) yield return p;
        foreach (var (_, s) in this.Sequences) yield return s;
        foreach (var (_, tr) in this.Triggers) yield return tr;
        foreach (var (_, sn) in this.Synonyms) yield return sn;
        foreach (var (_, d) in this.Defaults) yield return d;
        foreach (var (_, r) in this.Rules) yield return r;
        foreach (var (_, sp) in this.SecurityPolicies) yield return sp;
    }

    /// <summary>
    /// Whether <paramref name="obj"/> itself is one of this schema's objects —
    /// a dictionary lookup under its own name, so it answers what a walk of
    /// <see cref="SchemaObjects"/> for its id would without the walk; a table
    /// variable, a temp table or a dropped object is held by none.
    /// </summary>
    public bool Holds(SchemaObject obj) => obj switch
    {
        HeapTable table => this.HeapTables.TryGetValue(table.Name, out var held) && ReferenceEquals(held, table),
        View view => this.Views.TryGetValue(view.Name, out var held) && ReferenceEquals(held, view),
        UserDefinedFunction function => this.Functions.TryGetValue(function.Name, out var held) && ReferenceEquals(held, function),
        Procedure procedure => this.Procedures.TryGetValue(procedure.Name, out var held) && ReferenceEquals(held, procedure),
        Sequence sequence => this.Sequences.TryGetValue(sequence.Name, out var held) && ReferenceEquals(held, sequence),
        Trigger trigger => this.Triggers.TryGetValue(trigger.Name, out var held) && ReferenceEquals(held, trigger),
        Synonym synonym => this.Synonyms.TryGetValue(synonym.Name, out var held) && ReferenceEquals(held, synonym),
        DefaultObject @default => this.Defaults.TryGetValue(@default.Name, out var held) && ReferenceEquals(held, @default),
        RuleObject rule => this.Rules.TryGetValue(rule.Name, out var held) && ReferenceEquals(held, rule),
        SecurityPolicy policy => this.SecurityPolicies.TryGetValue(policy.Name, out var held) && ReferenceEquals(held, policy),
        _ => false,
    };

    /// <summary>
    /// True when <paramref name="leaf"/> matches any existing name in
    /// this schema's object-name namespace (<see cref="SchemaObjects"/>).
    /// Used by every CREATE path to raise Msg 2714 before allocating an
    /// ObjectId or writing into the relevant dict.
    /// </summary>
    /// <remarks>
    /// Constraint names share the namespace too (probed 2026-09-24 against
    /// SQL Server 2025), so a view can't take a constraint's name any more than
    /// a constraint can take a view's.
    /// </remarks>
    public bool HasNameInSharedNamespace(string leaf) => this.TryFindInSharedNamespace(leaf, out _) || this.HasConstraintNamed(leaf);

    /// <summary>
    /// True when an object of the shared namespace is named <paramref name="leaf"/>
    /// — the dictionary-lookup form of <see cref="TryFindInSharedNamespace"/>,
    /// asked of a principal's default schema before an unqualified name falls
    /// back to <c>dbo</c>: an object of any kind there shadows <c>dbo</c>'s, so
    /// <c>EXEC x</c> naming a table in the default schema is refused rather than
    /// running <c>dbo.x</c> (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    public bool HoldsObjectNamed(string leaf) =>
        this.HeapTables.ContainsKey(leaf) || this.Views.ContainsKey(leaf) || this.Functions.ContainsKey(leaf)
        || this.Procedures.ContainsKey(leaf) || this.Sequences.ContainsKey(leaf) || this.Synonyms.ContainsKey(leaf)
        || this.Triggers.ContainsKey(leaf) || this.Defaults.ContainsKey(leaf) || this.Rules.ContainsKey(leaf)
        || this.SecurityPolicies.ContainsKey(leaf);

    /// <summary>
    /// True when a table type or alias type is named <paramref name="leaf"/> —
    /// the type namespace's <see cref="HoldsObjectNamed"/>.
    /// </summary>
    public bool HoldsTypeNamed(string leaf) => this.TableTypes.ContainsKey(leaf) || this.AliasTypes.ContainsKey(leaf);

    /// <summary>
    /// True when a key, check, foreign-key, default or edge constraint on one
    /// of this schema's tables is named <paramref name="leaf"/>.
    /// </summary>
    public bool HasConstraintNamed(string leaf)
    {
        foreach (var (_, table) in this.HeapTables)
        {
            if (TableHasConstraintNamed(table, leaf, this.Database.Collation))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a key, check, foreign-key, default or edge constraint on
    /// <paramref name="table"/> is named <paramref name="leaf"/>.
    /// </summary>
    public static bool TableHasConstraintNamed(HeapTable table, string leaf, Collation collation)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (collation.Equals(key.Name, leaf))
                return true;
        }
        foreach (var check in table.CheckConstraints)
        {
            if (collation.Equals(check.Name, leaf))
                return true;
        }
        foreach (var foreignKey in table.OutgoingForeignKeys)
        {
            if (collation.Equals(foreignKey.Name, leaf))
                return true;
        }
        foreach (var column in table.Columns)
        {
            if (column.DefaultConstraint is { } def && collation.Equals(def.Name, leaf))
                return true;
        }
        foreach (var edgeConstraint in table.EdgeConstraints)
        {
            if (collation.Equals(edgeConstraint.Name, leaf))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Collation-aware lookup of <paramref name="leaf"/> across this schema's
    /// object-name namespace (<see cref="SchemaObjects"/>), handing back the
    /// matched object. The name-uniqueness rule that
    /// <see cref="HasNameInSharedNamespace"/> enforces makes the match unique,
    /// so enumeration order doesn't matter.
    /// </summary>
    public bool TryFindInSharedNamespace(
        string leaf, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SchemaObject? found)
    {
        var collation = this.Database.Collation;
        foreach (var obj in this.SchemaObjects())
        {
            if (collation.Equals(obj.Name, leaf))
            {
                found = obj;
                return true;
            }
        }

        found = null;
        return false;
    }
}
