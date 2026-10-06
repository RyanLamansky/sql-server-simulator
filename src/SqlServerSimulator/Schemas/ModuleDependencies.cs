using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The expression-dependency graph behind <c>sys.sql_expression_dependencies</c>,
/// <c>sys.dm_sql_referencing_entities</c>, <c>sys.dm_sql_referenced_entities</c>
/// and <c>sp_depends</c>: which entity references which object, and which of
/// that object's columns it reads or writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Computed on read, never stored.</b> The graph is derived from each
/// entity's own saved definition text every time a surface asks for it, the
/// way <see cref="SchemaBinding"/> derives its gate. That is not only cheaper
/// than a registry — it <em>is</em> real's semantic. Probed against SQL Server
/// 2025 (2026-08-02): a row survives a <c>DROP</c> of the object it names with
/// <c>referenced_id</c> gone NULL and the name intact, gets its id back when an
/// object of that name is recreated, and keeps naming the <em>old</em> name
/// after an <c>sp_rename</c> of the referenced table (with a NULL id) — all of
/// which say the store holds names and resolves ids at read. An <c>ALTER</c>
/// refreshing the rows and a <c>DROP</c> of the referencing module taking them
/// away then fall out for free, since the definition text is the only input.
/// </para>
/// <para>
/// <b>Reference extraction is a token walk</b>, for the same reason
/// <see cref="SchemaBinding"/>'s is: scalar-function, procedure and trigger
/// bodies are stored as source and re-parsed per call, so there is no
/// expression tree to visit, and one walk reaches every referencing kind — the
/// four module kinds, DML and DDL triggers, computed columns, and CHECK /
/// DEFAULT constraint expressions — through one mechanism. The walk splits a
/// definition into statement frames, classifies each dotted name chain by the
/// keyword that introduces it, and resolves the result against the live schema.
/// </para>
/// <para>
/// <b>Object-row flags come from the reference position, not from the
/// columns.</b> Probe-confirmed: a procedure whose only mention of a table is
/// <c>UPDATE t SET a = 5 WHERE b = 'q'</c> reports the table itself as
/// <c>is_updated</c> and <em>not</em> <c>is_selected</c>, even though column
/// <c>b</c> is read. The same procedure adding a <c>SELECT … FROM t</c> reports
/// both.
/// </para>
/// <para>
/// <b>Column granularity is name-based</b>, inheriting
/// <see cref="SchemaBinding"/>'s model: a frame counts as touching column
/// <c>C</c> of referenced object <c>T</c> when it names <c>T</c> and mentions
/// the identifier <c>C</c>, and <c>T</c> has a column by that name. A
/// <em>qualified</em> mention narrows to the source its qualifier names, so a
/// join, an <c>APPLY</c> and a <c>MERGE</c> between two objects sharing a
/// column name each report what real reports; an <em>unqualified</em> mention
/// in a multi-source frame still reaches every source that has a column by
/// that name, where real's binder picks one.
/// </para>
/// </remarks>
internal static class ModuleDependencies
{
    /// <summary><c>referenced_class</c> 1 — a table, view, module, synonym or sequence.</summary>
    internal const byte ObjectOrColumnClass = 1;

    /// <summary><c>referenced_class</c> 6 — a table type named by a parameter declaration.</summary>
    internal const byte TypeClass = 6;

    /// <summary><c>referencing_class</c> 7 — a filtered index, through the columns its filter reads.</summary>
    internal const byte IndexClass = 7;

    /// <summary><c>referencing_class</c> 12 — a database-scoped DDL trigger.</summary>
    internal const byte DatabaseDdlTriggerClass = 12;

    /// <summary>The separator that keeps a reference's name parts from colliding in the dedupe key.</summary>
    private const char KeySeparator = '\u0001';

    /// <summary>How one column of a referenced object is touched by the referencing definition.</summary>
    internal sealed class ColumnUse(string name)
    {
        public readonly string Name = name;

        /// <summary>The column is read — <c>is_selected</c>.</summary>
        public bool Selected;

        /// <summary>The column is written by an UPDATE's SET list or an INSERT's column list — <c>is_updated</c>.</summary>
        public bool Updated;

        /// <summary>The column is covered by a <c>*</c> rather than named — <c>is_select_all</c>.</summary>
        public bool SelectAll;
    }

    /// <summary>
    /// One (referencing entity, referenced entity) pair, keyed by the referenced
    /// name <em>as written</em> — which is what real stores, and why two
    /// spellings of one object are two rows.
    /// </summary>
    internal sealed class Reference(string? serverName, string? databaseName, string? schemaName, string entityName, byte referencedClass)
    {
        /// <summary>Leading segment of a four-part name; null otherwise.</summary>
        public readonly string? ServerName = serverName;

        /// <summary>Database segment of a three-part name; null when the reference is local.</summary>
        public readonly string? DatabaseName = databaseName;

        /// <summary>Schema as written — null for a name written without one, which real also reports as NULL.</summary>
        public readonly string? SchemaName = schemaName;

        public readonly string EntityName = entityName;

        /// <summary><see cref="ObjectOrColumnClass"/> or <see cref="TypeClass"/>.</summary>
        public readonly byte ReferencedClass = referencedClass;

        /// <summary>True when the referencing definition is schema bound — <c>is_schema_bound_reference</c>.</summary>
        public bool IsSchemaBound;

        /// <summary>True for a one-part <c>EXEC</c> name, whose schema only the caller settles.</summary>
        public bool IsCallerDependent;

        /// <summary>True for a <c>q.m(…)</c> call whose qualifier names no schema.</summary>
        public bool IsAmbiguous;

        /// <summary>
        /// True for an <c>EXEC</c> target, which real never counts as a gap in
        /// the report: a missing procedure leaves its row complete and raises
        /// no Msg 2020 (probed 2026-10-06 against SQL Server 2025).
        /// </summary>
        public bool IsProcedureCall;

        /// <summary>
        /// True for a synonym whose base names nothing: real reports the
        /// synonym's id but marks the row incomplete, with no use flags.
        /// </summary>
        public bool SynonymBaseMissing;

        /// <summary>
        /// The object a three-part name binds to in another database of the
        /// simulation, which <c>sys.dm_sql_referenced_entities</c> resolves —
        /// id, columns and all — where the catalog view and the legacy pair
        /// report no id (probed 2026-10-06 against SQL Server 2025).
        /// </summary>
        public SchemaObject? CrossDatabaseResolved;

        /// <summary>
        /// A statement read the object without reaching any of its columns
        /// (<c>SELECT 1 FROM t</c>), which the legacy pair reports as a
        /// <c>referenced_minor_id = 0</c> row beside any column rows.
        /// </summary>
        public bool WholeSelected;

        /// <summary>
        /// A statement wrote the object without naming a column it writes — a
        /// <c>DELETE</c>, a <c>MERGE</c>'s insert — which the legacy pair
        /// reports as a <c>referenced_minor_id = 0</c> row beside any column rows.
        /// </summary>
        public bool WholeUpdated;

        /// <summary>
        /// True when the definition names this entity as an object — a FROM
        /// source, a DML target, an <c>EXEC</c>, a function call — rather than
        /// reaching it only through a column. Drives whether the
        /// <c>referenced_minor_id = 0</c> row exists.
        /// </summary>
        public bool HasObjectReference;

        public bool IsSelected;

        public bool IsUpdated;

        public bool IsSelectAll;

        /// <summary>True for an <c>INSERT t VALUES …</c> that carries no column list.</summary>
        public bool IsInsertAll;

        /// <summary>Per-column detail, in the referenced object's own column order.</summary>
        public readonly List<ColumnUse> Columns = [];

        /// <summary>The object this reference resolves to, or null when nothing of that name exists.</summary>
        public SchemaObject? Resolved;

        /// <summary>The table type this reference resolves to, for a <see cref="TypeClass"/> row.</summary>
        public TableType? ResolvedType;

        /// <summary>
        /// True when the reference names something real can't find — a table,
        /// view, function or sequence that doesn't exist, another database's
        /// missing object, a synonym over nothing — which marks its rows
        /// <c>is_incomplete</c> and makes the DMV raise Msg 2020. An
        /// <c>EXEC</c> target, a caller-dependent name and an ambiguous method
        /// call never are (probed 2026-10-06 against SQL Server 2025).
        /// </summary>
        public bool IsIncomplete =>
            this.ReferencedClass == ObjectOrColumnClass
            && !this.IsProcedureCall && !this.IsAmbiguous && !this.IsCallerDependent
            && (this.ServerName is not null
                || (this.DatabaseName is not null ? this.CrossDatabaseResolved is null : this.Resolved is null || this.SynonymBaseMissing));

        /// <summary>The object whose columns the reference reaches: this database's, or another's for a three-part name.</summary>
        public SchemaObject? Bound => this.SynonymBaseMissing ? null : this.Resolved ?? this.CrossDatabaseResolved;

        /// <summary><c>referenced_id</c> — NULL for a cross-database, caller-dependent, ambiguous or missing reference.</summary>
        public int? ReferencedId =>
            this.DatabaseName is not null || this.ServerName is not null || this.IsCallerDependent || this.IsAmbiguous
                ? null
                : this.ResolvedType is { } type ? type.UserTypeId
                : this.Resolved?.ObjectId;
    }

    /// <summary>One entity that carries dependencies, together with what it references.</summary>
    internal sealed class Entity(
        int referencingId, int referencingMinorId, byte referencingClass,
        string schemaName, string entityName, string objectTypeCode, List<Reference> references)
    {
        public readonly int ReferencingId = referencingId;

        /// <summary>Nonzero only for a computed column, where it is the column's <c>column_id</c>.</summary>
        public readonly int ReferencingMinorId = referencingMinorId;

        /// <summary><see cref="ObjectOrColumnClass"/> or <see cref="DatabaseDdlTriggerClass"/>.</summary>
        public readonly byte ReferencingClass = referencingClass;

        public readonly string SchemaName = schemaName;

        public readonly string EntityName = entityName;

        /// <summary>The <c>sys.objects</c> type code, which <c>sp_depends</c> renders as a label.</summary>
        public readonly string ObjectTypeCode = objectTypeCode;

        public readonly List<Reference> References = references;
    }

    /// <summary>
    /// Every dependency-bearing entity in <paramref name="database"/>, ordered
    /// by referencing object id then minor id. Views, procedures, functions and
    /// DML triggers contribute their bodies; DDL triggers theirs under
    /// <see cref="DatabaseDdlTriggerClass"/>; a table contributes one entity per
    /// computed column and one per CHECK / DEFAULT constraint, all schema bound
    /// the way real records them. On <paramref name="includeIndexes"/> a
    /// filtered index contributes its filter under <see cref="IndexClass"/>,
    /// which only <c>sys.sql_expression_dependencies</c> reports — the legacy
    /// views, the referencing-entities DMVs and <c>sp_depends</c> don't list
    /// it (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    internal static List<Entity> Enumerate(Database database, bool includeIndexes = false, Func<string, Database?>? otherDatabases = null)
    {
        List<Entity> entities = [];
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, view) in schema.Views)
                AddModule(database, entities, view, schema.Name, view.BodyText, view.IsSchemaBound, otherDatabases);
            foreach (var (_, procedure) in schema.Procedures)
            {
                var references = AnalyzeBody(database, procedure.BodyText, isSchemaBound: false, otherDatabases);
                AddTableTypeParameters(database, procedure, references);
                Add(entities, procedure, ObjectOrColumnClass, schema.Name, references);
            }
            foreach (var (_, function) in schema.Functions)
            {
                var references = AnalyzeBody(database, function.BodyText, function.IsSchemaBound, otherDatabases);
                foreach (var parameter in function.Parameters)
                    AddTableTypeParameter(database, parameter.TableType, references);
                Add(entities, function, ObjectOrColumnClass, schema.Name, references);
            }
            foreach (var (_, trigger) in schema.Triggers)
                AddModule(database, entities, trigger, schema.Name, trigger.BodyText, isSchemaBound: false, otherDatabases);
            foreach (var (_, table) in schema.HeapTables)
            {
                AddTableExpressions(database, entities, schema, table);
                if (includeIndexes)
                    AddFilteredIndexes(database, entities, schema, table);
            }
            foreach (var (_, policy) in schema.SecurityPolicies)
                AddSecurityPolicy(database, entities, schema, policy);
        }

        foreach (var (_, ddlTrigger) in database.DdlTriggers)
        {
            var references = AnalyzeBody(database, ddlTrigger.BodyText, isSchemaBound: false, otherDatabases);
            if (references.Count > 0)
            {
                entities.Add(new Entity(
                    ddlTrigger.ObjectId, 0, DatabaseDdlTriggerClass,
                    Database.DefaultSchemaName, ddlTrigger.Name, ddlTrigger.ObjectTypeCode, references));
            }
        }

        entities.Sort(static (a, b) => a.ReferencingId != b.ReferencingId
            ? a.ReferencingId.CompareTo(b.ReferencingId)
            : a.ReferencingMinorId.CompareTo(b.ReferencingMinorId));
        return entities;
    }

    /// <summary>
    /// The entities the object with id <paramref name="objectId"/> carries, in
    /// minor-id order: a module's or a constraint's one, or a table's one per
    /// computed column — empty when it carries no dependency-bearing
    /// definition.
    /// </summary>
    internal static List<Entity> ForObject(Database database, int objectId, Func<string, Database?>? otherDatabases = null) =>
        Enumerate(database, otherDatabases: otherDatabases).FindAll(entity => entity.ReferencingId == objectId && entity.ReferencingClass == ObjectOrColumnClass);

    /// <summary>
    /// The object id of the CHECK or DEFAULT constraint named
    /// <paramref name="name"/> on one of <paramref name="schema"/>'s tables, or
    /// null — the two constraint kinds whose expression carries dependencies.
    /// </summary>
    internal static int? ExpressionConstraintId(Database database, Schema schema, string name)
    {
        foreach (var (_, table) in schema.HeapTables)
        {
            foreach (var check in table.CheckConstraints)
            {
                if (database.Collation.Equals(check.Name, name))
                    return check.ObjectId;
            }
            foreach (var column in table.Columns)
            {
                if (column.DefaultConstraint is { } constraint && database.Collation.Equals(constraint.Name, name))
                    return constraint.ObjectId;
            }
        }
        return null;
    }

    /// <summary>
    /// Orders two referencing entities by schema, then name, under the catalog
    /// collation — the order real's referencing-entities DMV and
    /// <c>sp_depends</c>' "referenced by" set list them in (probed 2026-09-30
    /// against SQL Server 2025).
    /// </summary>
    internal static int CompareByName(Entity a, Entity b)
    {
        var bySchema = Collation.Catalog.Compare(a.SchemaName, b.SchemaName);
        return bySchema != 0 ? bySchema : Collation.Catalog.Compare(a.EntityName, b.EntityName);
    }

    /// <summary>
    /// The objects of <paramref name="database"/> that <paramref name="bodyText"/>
    /// names and that exist, each once — what a compiled module body depends on.
    /// </summary>
    internal static List<SchemaObject> ObjectsNamedBy(Database database, string bodyText)
    {
        List<SchemaObject> objects = [];
        foreach (var reference in AnalyzeBody(database, bodyText, isSchemaBound: false))
        {
            if (reference.DatabaseName is null && reference.ServerName is null && reference.Resolved is { } resolved && !objects.Contains(resolved))
                objects.Add(resolved);
        }
        return objects;
    }

    /// <summary>
    /// A schema-bound security policy's predicates, one entity each with the
    /// predicate's id as its minor id: the target, the columns the arguments
    /// read, and the function. A policy that isn't schema bound records
    /// nothing (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static void AddSecurityPolicy(Database database, List<Entity> entities, Schema schema, SecurityPolicy policy)
    {
        if (!policy.State.IsSchemaBound)
            return;
        foreach (var predicate in policy.State.Predicates)
        {
            var target = new Reference(null, null, SchemaNameOf(database, predicate.Target), predicate.Target.Name, ObjectOrColumnClass)
            {
                IsSchemaBound = true,
                HasObjectReference = true,
                IsSelected = true,
                Resolved = predicate.Target,
            };
            if (ColumnsOf(predicate.Target) is { } columns)
            {
                foreach (var column in columns)
                {
                    var reads = false;
                    foreach (var argument in predicate.Arguments)
                        argument.VisitColumnReferences(name => reads |= database.Collation.Equals(name.Leaf, column.Name));
                    if (reads)
                        target.Columns.Add(new ColumnUse(column.Name) { Selected = true });
                }
            }
            _ = database.Schemas.TryGetValue(predicate.FunctionSchema, out var functionSchema);
            var function = new Reference(null, null, predicate.FunctionSchema, predicate.FunctionName, ObjectOrColumnClass)
            {
                IsSchemaBound = true,
                HasObjectReference = true,
                IsSelected = true,
                Resolved = functionSchema?.Functions.GetValueOrDefault(predicate.FunctionName),
            };
            entities.Add(new Entity(policy.ObjectId, predicate.Id, ObjectOrColumnClass, schema.Name, policy.Name, policy.ObjectTypeCode, [target, function]));
        }

        static string? SchemaNameOf(Database database, SchemaObject target)
        {
            foreach (var (name, candidate) in database.Schemas)
            {
                if (candidate.SchemaId == target.SchemaId)
                    return name;
            }
            return null;
        }
    }

    private static void AddModule(
        Database database, List<Entity> entities, SchemaObject module,
        string schemaName, string bodyText, bool isSchemaBound, Func<string, Database?>? otherDatabases) =>
        Add(entities, module, ObjectOrColumnClass, schemaName, AnalyzeBody(database, bodyText, isSchemaBound, otherDatabases));

    private static void Add(
        List<Entity> entities, SchemaObject module, byte referencingClass, string schemaName, List<Reference> references)
    {
        if (references.Count > 0)
        {
            entities.Add(new Entity(
                module.ObjectId, 0, referencingClass, schemaName, module.Name, module.ObjectTypeCode, references));
        }
    }

    /// <summary>
    /// A procedure's table-valued parameters, which real records as
    /// <see cref="TypeClass"/> references off the parameter declaration rather
    /// than the body (probe-confirmed: <c>referenced_id</c> is the type's
    /// <c>user_type_id</c> and <c>is_schema_bound_reference</c> is 0); a
    /// function's are recorded the same way (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    private static void AddTableTypeParameters(Database database, Procedure procedure, List<Reference> references)
    {
        foreach (var parameter in procedure.Parameters)
            AddTableTypeParameter(database, parameter.TableType, references);
    }

    private static void AddTableTypeParameter(Database database, TableType? tableType, List<Reference> references)
    {
        if (tableType is null)
            return;
        references.Add(new Reference(null, null, SchemaNameOf(database, tableType.SchemaId), tableType.Name, TypeClass)
        {
            ResolvedType = tableType,
            HasObjectReference = true,
        });
    }

    /// <summary>
    /// A table's own expression-bearing pieces: one entity per computed column
    /// (referencing the table itself, with the column's <c>column_id</c> as the
    /// referencing minor id) and one per CHECK / DEFAULT constraint (referencing
    /// under the constraint's own object id). All three are schema bound, and
    /// all three reach their own table's columns without an object-level
    /// reference to it — which is why real reports them as column rows with no
    /// <c>referenced_minor_id = 0</c> companion.
    /// </summary>
    private static void AddTableExpressions(Database database, List<Entity> entities, Schema schema, HeapTable table)
    {
        foreach (var column in table.Columns)
        {
            if (column.ComputedDefinition is not { } definition)
                continue;
            var references = AnalyzeExpression(database, schema, table, definition);
            if (references.Count > 0)
            {
                entities.Add(new Entity(
                    table.ObjectId, column.ColumnId, ObjectOrColumnClass,
                    schema.Name, table.Name, table.ObjectTypeCode, references));
            }
        }

        foreach (var check in table.CheckConstraints)
        {
            if (check.Definition is not { } definition)
                continue;
            var references = AnalyzeExpression(database, schema, table, definition);
            if (references.Count > 0)
                entities.Add(new Entity(check.ObjectId, 0, ObjectOrColumnClass, schema.Name, check.Name, "C ", references));
        }

        foreach (var column in table.Columns)
        {
            if (column.DefaultConstraint is not { } constraint || constraint.Definition is not { } definition)
                continue;
            var references = AnalyzeExpression(database, schema, table, definition);
            if (references.Count > 0)
                entities.Add(new Entity(constraint.ObjectId, 0, ObjectOrColumnClass, schema.Name, constraint.Name, "D ", references));
        }
    }

    /// <summary>
    /// A filtered index's entity: the index id as the referencing minor id
    /// under the table's own id, its filter's columns as schema-bound column
    /// references on the table.
    /// </summary>
    private static void AddFilteredIndexes(Database database, List<Entity> entities, Schema schema, HeapTable table)
    {
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Index is not { FilterDefinition: { } filter })
                continue;
            var references = AnalyzeExpression(database, schema, table, filter);
            if (references.Count > 0)
                entities.Add(new Entity(table.ObjectId, identity.IndexId, IndexClass, schema.Name, table.Name, table.ObjectTypeCode, references));
        }
    }

    /// <summary>
    /// Analyzes a computed-column / CHECK / DEFAULT expression: bare identifiers
    /// naming a column of <paramref name="owner"/> become column references on
    /// that table, and a qualified call resolving to a function becomes an
    /// object reference. Everything recorded here is schema bound.
    /// </summary>
    private static List<Reference> AnalyzeExpression(Database database, Schema schema, HeapTable owner, string definition)
    {
        Dictionary<string, Reference> byKey = new(StringComparer.OrdinalIgnoreCase);
        List<Reference> ordered = [];
        var analysis = new Analysis(database, isSchemaBound: true, otherDatabases: null);
        var tokens = Tokenize(definition);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is not Name)
                continue;
            // A default's sequence draw is an object reference to the sequence
            // (probed 2026-10-04 against SQL Server 2025).
            if (i + 3 < tokens.Count
                && tokens[i] is Name { Value: var next } && next.Equals("NEXT", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 1] is Name { Value: var value } && value.Equals("VALUE", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 2] is ReservedKeyword { Keyword: Keyword.For } && tokens[i + 3] is Name)
            {
                i += 3;
                if (BuildObjectReference(analysis, ReadName(tokens, ref i)) is { } sequenceReference)
                {
                    var recorded = Remember(byKey, ordered, sequenceReference);
                    recorded.HasObjectReference = true;
                    recorded.IsSchemaBound = true;
                }
                continue;
            }
            var name = ReadName(tokens, ref i);
            if (name.SegmentCount >= 2)
            {
                if (name.IsCall && BuildCallReference(analysis, name) is { } call)
                {
                    var recorded = Remember(byKey, ordered, call);
                    recorded.HasObjectReference = true;
                    recorded.IsSchemaBound = true;
                }
                continue;
            }
            if (name.IsCall || FindColumn(owner, name.Leaf) is not { } column)
                continue;
            var reference = Remember(byKey, ordered, new Reference(null, null, schema.Name, owner.Name, ObjectOrColumnClass)
            {
                Resolved = owner,
                IsSchemaBound = true,
            });
            // The column row exists, but its use flags stay 0: real reports none
            // for a computed column, CHECK or DEFAULT (probed 2026-09-29 against
            // SQL Server 2025 on sys.sql_dependencies and sp_depends).
            _ = ColumnFor(reference, column.Name);
        }
        return ordered;
    }

    /// <summary>
    /// Analyzes a module body into its reference set. The body is split into
    /// statement frames (a <c>;</c> or a statement-opening keyword at paren
    /// depth 0 starts a new one) so each frame's column mentions attach to the
    /// objects that frame names, and so an UPDATE's SET list stays
    /// distinguishable from its WHERE.
    /// </summary>
    private static List<Reference> AnalyzeBody(Database database, string bodyText, bool isSchemaBound, Func<string, Database?>? otherDatabases = null)
    {
        var analysis = new Analysis(database, isSchemaBound, otherDatabases);
        if (string.IsNullOrEmpty(bodyText))
            return analysis.Ordered;

        var tokens = Tokenize(bodyText);
        var cteNames = DeclaredCteNames(tokens);
        var frame = new Frame(analysis);
        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    frame.Depth++;
                    continue;
                case Operator { Character: ')' }:
                    frame.Depth--;
                    while (frame.FromListDepths.TryPeek(out var fromDepth) && fromDepth > frame.Depth)
                        _ = frame.FromListDepths.Pop();
                    if (frame.Depth <= 0)
                    {
                        frame.InInsertColumnList = false;
                        frame.InsertColumnListQualifier = null;
                        frame.InMergeInsertColumnList = false;
                    }
                    continue;
                case Operator { Character: ',' } when frame.FromListDepths.TryPeek(out var listDepth) && listDepth == frame.Depth:
                    // A comma in a FROM list introduces the next source.
                    frame.PendingSource = SourceRole.Selected;
                    continue;
                case Operator { Character: ';' }:
                    frame = CloseFrame(frame);
                    continue;
                case Operator { Character: '*' } when frame.Depth == 0 && StarIsSelectAll(tokens, i):
                    frame.NoteStar(StarQualifier(tokens, i));
                    continue;
                case Operator { Character: '=' } when frame.InSetList && frame.LastMention is { } assigned:
                    assigned.Updated = true;
                    continue;
                case ReservedKeyword { Keyword: Keyword.For } when IsNextValueFor(tokens, i):
                    frame.PendingSource = SourceRole.Sequence;
                    continue;
                case ReservedKeyword keyword:
                    frame = ApplyKeyword(keyword.Keyword, frame);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Apply or ContextualKeyword.Using }:
                    frame.PendingSource = SourceRole.Selected;
                    continue;
                case not Name:
                    continue;
            }

            var name = ReadName(tokens, ref i);
            var role = frame.TakePendingSource();
            switch (role)
            {
                case SourceRole.Procedure:
                    RecordProcedure(analysis, name);
                    continue;
                case SourceRole.None when name.IsCall:
                    RecordCall(analysis, name, frame);
                    continue;
                case SourceRole.None:
                    frame.NoteColumn(name.SegmentCount >= 2 ? name.Qualifier : null, name.Leaf);
                    continue;
                default:
                    RecordSource(analysis, name, role, frame, cteNames, tokens, i);
                    // The alias names the source, not a column.
                    if (i + 2 < tokens.Count && tokens[i + 1] is ReservedKeyword { Keyword: Keyword.As } && tokens[i + 2] is Name)
                        i += 2;
                    else if (i + 1 < tokens.Count && IsBareAlias(tokens[i + 1]))
                        i++;
                    continue;
            }
        }

        _ = CloseFrame(frame);
        return analysis.Ordered;
    }

    /// <summary>
    /// The state one body's walk shares across its statement frames: the
    /// database names resolve in, the references recorded so far (deduplicated
    /// by name as written, kept in first-mention order), and how a name in
    /// another database resolves.
    /// </summary>
    private sealed class Analysis(Database database, bool isSchemaBound, Func<string, Database?>? otherDatabases)
    {
        public readonly Database Database = database;

        public readonly bool IsSchemaBound = isSchemaBound;

        public readonly Func<string, Database?>? OtherDatabases = otherDatabases;

        public readonly Dictionary<string, Reference> ByKey = new(StringComparer.OrdinalIgnoreCase);

        public readonly List<Reference> Ordered = [];

        public Reference Remember(Reference candidate)
        {
            var reference = ModuleDependencies.Remember(this.ByKey, this.Ordered, candidate);
            reference.HasObjectReference = true;
            if (this.IsSchemaBound)
                reference.IsSchemaBound = true;
            return reference;
        }
    }

    /// <summary>One object a statement frame names: the reference, the alias it was introduced under, its role, and the paren depth it sits at.</summary>
    private struct FrameSource(Reference reference, string alias, SourceRole role, int depth)
    {
        public readonly Reference Reference = reference;

        public readonly string Alias = alias;

        public SourceRole Role = role;

        public readonly int Depth = depth;
    }

    /// <summary>What the keyword before a name says the name is.</summary>
    private enum SourceRole
    {
        None,

        /// <summary>A FROM / JOIN / APPLY / USING source.</summary>
        Selected,

        /// <summary>An UPDATE / DELETE / MERGE target.</summary>
        Updated,

        /// <summary>An INSERT target, which also decides <c>is_insert_all</c>.</summary>
        InsertTarget,

        /// <summary>An <c>EXEC</c> name.</summary>
        Procedure,

        /// <summary>A <c>NEXT VALUE FOR</c> sequence, which real records with no read or write flag.</summary>
        Sequence,
    }

    /// <summary>
    /// One statement's accumulation: the objects it names, the identifiers it
    /// mentions, and the cursor state (paren depth, SET-list position, pending
    /// source role) the walk needs while it runs.
    /// </summary>
    private sealed class Frame(Analysis analysis)
    {
        public readonly Analysis Analysis = analysis;

        public int Depth;

        /// <summary>
        /// True inside a <c>MERGE</c>'s <c>WHEN NOT MATCHED THEN INSERT (…)</c>
        /// column list, whose names real records nothing for: the insert is a
        /// whole-object write (probed 2026-10-06 against SQL Server 2025).
        /// </summary>
        public bool InMergeInsertColumnList;

        /// <summary>
        /// A one-part UPDATE / DELETE target no object carries — an alias the
        /// statement's FROM introduces (<c>UPDATE x … FROM dbo.t x</c>), else a
        /// missing object — settled once the frame's sources are known.
        /// </summary>
        public string? PendingTargetName;

        /// <summary>
        /// The <c>CASE</c> expressions open in the frame, whose <c>END</c>
        /// closes the expression rather than the statement.
        /// </summary>
        public int CaseDepth;

        /// <summary>Set by an introducer keyword; consumed by the next name.</summary>
        public SourceRole PendingSource;

        /// <summary>True once an <c>UPDATE</c> opened this frame, so its <c>SET</c> is an assignment list.</summary>
        public bool IsUpdateFrame;

        /// <summary>True between an UPDATE's <c>SET</c> and the clause that ends its assignment list.</summary>
        public bool InSetList;

        /// <summary>True while the walk is inside an INSERT target's column list.</summary>
        public bool InInsertColumnList;

        /// <summary>
        /// The INSERT target's alias, stamped on every mention the column list
        /// yields so the written columns land on that target rather than on
        /// every source the frame reads.
        /// </summary>
        public string? InsertColumnListQualifier;

        /// <summary>True once a <c>MERGE</c> opened this frame, so its WHEN clauses' verbs stay inside it.</summary>
        public bool IsMergeFrame;

        /// <summary>
        /// The paren depths whose <c>FROM</c> list the walk is inside, innermost
        /// on top, so a comma at that depth introduces another source.
        /// </summary>
        public readonly Stack<int> FromListDepths = new();

        /// <summary>Notes a <c>FROM</c> opening its source list at the current depth.</summary>
        public void OpenFromList()
        {
            if (!this.FromListDepths.TryPeek(out var top) || top != this.Depth)
                this.FromListDepths.Push(this.Depth);
        }

        /// <summary>Notes a clause that ends the <c>FROM</c> list at the current depth.</summary>
        public void CloseFromList()
        {
            if (this.FromListDepths.TryPeek(out var top) && top == this.Depth)
                _ = this.FromListDepths.Pop();
        }

        /// <summary>The most recent mention, so an <c>=</c> can mark it written.</summary>
        public Mention? LastMention;

        /// <summary>Objects this frame names, with the alias, role and depth each was introduced under.</summary>
        public readonly List<FrameSource> Sources = [];

        /// <summary>Identifiers this frame mentions that could be columns.</summary>
        public readonly List<Mention> Mentions = [];

        /// <summary>Qualifiers a <c>*</c> was written under; an empty string means an unqualified one.</summary>
        public readonly HashSet<string> StarQualifiers = new(StringComparer.OrdinalIgnoreCase);

        public SourceRole TakePendingSource()
        {
            var role = this.PendingSource;
            this.PendingSource = SourceRole.None;
            return role;
        }

        public void NoteColumn(string? qualifier, string name)
        {
            if (this.InMergeInsertColumnList)
                return;
            var mention = new Mention(
                this.InInsertColumnList ? qualifier ?? this.InsertColumnListQualifier : qualifier,
                name,
                this.Depth)
            {
                Updated = this.InInsertColumnList,
            };
            this.Mentions.Add(mention);
            this.LastMention = mention;
        }

        public void NoteStar(string? qualifier) => _ = this.StarQualifiers.Add(qualifier ?? "");
    }

    /// <summary>
    /// One identifier a statement frame mentions that could name a column of a
    /// source it reads. <see cref="Qualifier"/> is what the reference was
    /// written under — an alias or a table name — and is what keeps
    /// <c>a.id</c> off a joined <c>b</c> that also has an <c>id</c>.
    /// </summary>
    private sealed class Mention(string? qualifier, string name, int depth)
    {
        public readonly string? Qualifier = qualifier;

        public readonly string Name = name;

        /// <summary>The paren depth the mention sits at, which bounds the sources it can see.</summary>
        public readonly int Depth = depth;

        /// <summary>True for an UPDATE SET-list target or an INSERT column-list entry.</summary>
        public bool Updated;
    }

    /// <summary>
    /// Applies a reserved keyword's effect on the frame — starting a new one,
    /// arming a source role, or moving in and out of an UPDATE's SET list.
    /// </summary>
    private static Frame ApplyKeyword(Keyword keyword, Frame frame)
    {
        if (frame.Depth > 0)
        {
            // A nested query's own clauses keep feeding the enclosing frame: its
            // sources and columns belong to the same statement, which is what
            // makes a correlated predicate attach to the outer object too. Only
            // the source-introducing and source-ending keywords matter here —
            // a nested SELECT must disarm a role an enclosing APPLY armed, or
            // the nested projection's first name would be read as a source.
            switch (keyword)
            {
                case Keyword.From:
                    frame.PendingSource = SourceRole.Selected;
                    frame.OpenFromList();
                    break;
                case Keyword.Join:
                    frame.PendingSource = SourceRole.Selected;
                    break;
                case Keyword.On or Keyword.Values:
                    frame.PendingSource = SourceRole.None;
                    break;
                case Keyword.Select or Keyword.Where or Keyword.Group or Keyword.Order or Keyword.Having or Keyword.For:
                    frame.PendingSource = SourceRole.None;
                    frame.CloseFromList();
                    break;
            }
            return frame;
        }

        switch (keyword)
        {
            case Keyword.Update when frame.IsMergeFrame:
                // MERGE's WHEN MATCHED THEN UPDATE SET … writes the merge
                // target, so the verb opens an assignment list inside the merge
                // frame rather than a statement of its own.
                frame.IsUpdateFrame = true;
                return frame;
            case Keyword.Insert when frame.IsMergeFrame:
                // WHEN NOT MATCHED THEN INSERT (cols) — the column list opens
                // immediately and its closing paren ends it; real records
                // nothing for its names.
                frame.InMergeInsertColumnList = true;
                return frame;
            case Keyword.Delete when frame.IsMergeFrame:
                return frame;
            case Keyword.Update:
                var updateFrame = CloseFrame(frame);
                updateFrame.PendingSource = SourceRole.Updated;
                updateFrame.IsUpdateFrame = true;
                return updateFrame;
            case Keyword.Insert:
                var insertFrame = CloseFrame(frame);
                insertFrame.PendingSource = SourceRole.InsertTarget;
                return insertFrame;
            case Keyword.Merge:
                var mergeFrame = CloseFrame(frame);
                mergeFrame.PendingSource = SourceRole.Updated;
                mergeFrame.IsMergeFrame = true;
                return mergeFrame;
            case Keyword.Delete:
                var deleteFrame = CloseFrame(frame);
                deleteFrame.PendingSource = SourceRole.Updated;
                return deleteFrame;
            case Keyword.Exec or Keyword.Execute:
                var execFrame = CloseFrame(frame);
                execFrame.PendingSource = SourceRole.Procedure;
                return execFrame;
            case Keyword.Set:
                // An UPDATE's SET opens its assignment list rather than a new
                // statement, so the target and the columns it writes stay
                // together; every other SET is a statement of its own.
                if (!frame.IsUpdateFrame)
                    return CloseFrame(frame);
                frame.InSetList = true;
                return frame;
            case Keyword.From or Keyword.Join:
                // FROM keeps a still-unfilled DML target role (DELETE FROM t);
                // otherwise the name it introduces is read, not written.
                if (frame.PendingSource != SourceRole.Updated)
                    frame.PendingSource = SourceRole.Selected;
                if (keyword == Keyword.From)
                    frame.OpenFromList();
                frame.InSetList = false;
                return frame;
            case Keyword.For:
                frame.CloseFromList();
                return frame;
            case Keyword.Into:
                // INSERT INTO t / MERGE INTO t — the armed target role carries
                // through INTO. SELECT … INTO creates a table rather than
                // referencing one, so an unarmed INTO introduces nothing.
                return frame;
            case Keyword.Where or Keyword.Having or Keyword.Group or Keyword.Order or Keyword.On or Keyword.Values:
                frame.InSetList = false;
                frame.PendingSource = SourceRole.None;
                if (keyword is not (Keyword.On or Keyword.Values))
                    frame.CloseFromList();
                return frame;
            case Keyword.Case:
                frame.CaseDepth++;
                return frame;
            case Keyword.End when frame.CaseDepth > 0:
                frame.CaseDepth--;
                return frame;
            case Keyword.Select or Keyword.Declare or Keyword.If or Keyword.While or Keyword.Return
                or Keyword.Print or Keyword.Begin or Keyword.End or Keyword.RaisError or Keyword.WaitFor
                or Keyword.Open or Keyword.Fetch or Keyword.Close or Keyword.Deallocate or Keyword.Use
                or Keyword.Create or Keyword.Alter or Keyword.Drop or Keyword.Grant or Keyword.Revoke
                or Keyword.Deny or Keyword.Truncate or Keyword.Commit or Keyword.Rollback or Keyword.Save
                or Keyword.Break or Keyword.Continue or Keyword.With:
                return CloseFrame(frame);
            default:
                return frame;
        }
    }

    /// <summary>
    /// Folds a finished frame's column mentions onto the objects it named and
    /// hands back a fresh frame. A source's column rows are the identifiers the
    /// frame mentioned that the source actually has — or every column it has,
    /// when a <c>*</c> covering it was written, which is how real reports a
    /// <c>SELECT *</c> (all columns <c>is_select_all</c>, none
    /// <c>is_selected</c>, even one the WHERE names separately). A reference
    /// real can't find carries no use flags at all (probed 2026-10-06 against
    /// SQL Server 2025).
    /// </summary>
    private static Frame CloseFrame(Frame frame)
    {
        var sources = frame.Sources;
        if (frame.PendingTargetName is { } target)
        {
            var aliased = sources.FindIndex(source => string.Equals(source.Alias, target, StringComparison.OrdinalIgnoreCase));
            if (aliased >= 0)
            {
                var source = sources[aliased];
                source.Role = SourceRole.Updated;
                sources[aliased] = source;
            }
            else
            {
                _ = frame.Analysis.Remember(new Reference(null, null, null, target, ObjectOrColumnClass));
            }
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            _ = aliases.Add(source.Alias);

        var starred = new bool[sources.Count];
        var read = new bool[sources.Count];
        var written = new bool[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            var (reference, role) = (sources[i].Reference, sources[i].Role);
            if (reference.Bound is null)
                continue;
            switch (role)
            {
                case SourceRole.Selected:
                    reference.IsSelected = true;
                    break;
                case SourceRole.Updated or SourceRole.InsertTarget:
                    reference.IsUpdated = true;
                    break;
            }
            if (ColumnsOf(reference.Bound) is not { } columns
                || !(frame.StarQualifiers.Contains("") || frame.StarQualifiers.Contains(sources[i].Alias)))
            {
                continue;
            }
            starred[i] = true;
            read[i] = true;
            reference.IsSelectAll = true;
            reference.IsSelected = false;
            foreach (var column in columns)
            {
                var use = ColumnFor(reference, column.Name);
                use.SelectAll = true;
                use.Selected = false;
            }
        }

        var reached = new List<int>();
        foreach (var mention in frame.Mentions)
        {
            reached.Clear();
            for (var i = 0; i < sources.Count; i++)
            {
                if (!starred[i]
                    && sources[i].Depth <= mention.Depth
                    && FindColumnOf(sources[i].Reference.Bound, mention.Name) is not null
                    && MentionReaches(mention, sources[i].Alias, aliases))
                {
                    reached.Add(i);
                }
            }
            if (mention.Qualifier is null && reached.Count > 1)
            {
                // An unqualified SET-list target binds the statement's target;
                // any other unqualified name binds the innermost scope that has
                // the column, as the binder resolves it.
                if (mention.Updated && reached.Exists(i => sources[i].Role is SourceRole.Updated or SourceRole.InsertTarget))
                {
                    _ = reached.RemoveAll(i => sources[i].Role is not (SourceRole.Updated or SourceRole.InsertTarget));
                }
                else
                {
                    var innermost = reached.Max(i => sources[i].Depth);
                    _ = reached.RemoveAll(i => sources[i].Depth != innermost);
                }
            }
            foreach (var i in reached)
            {
                var use = ColumnFor(sources[i].Reference, FindColumnOf(sources[i].Reference.Bound, mention.Name)!);
                if (mention.Updated)
                {
                    use.Updated = true;
                    written[i] = true;
                }
                else
                {
                    use.Selected = true;
                    read[i] = true;
                }
            }
        }

        // A statement that reads or writes an object without reaching one of
        // its columns uses the whole object, which the legacy pair records
        // as an object row even beside the columns other statements reach.
        for (var i = 0; i < sources.Count; i++)
        {
            var reference = sources[i].Reference;
            if (ColumnsOf(reference.Bound) is null)
                continue;
            switch (sources[i].Role)
            {
                case SourceRole.Selected when !read[i]:
                    reference.WholeSelected = true;
                    break;
                case SourceRole.Updated or SourceRole.InsertTarget when !written[i]:
                    reference.WholeUpdated = true;
                    break;
            }
        }
        return new Frame(frame.Analysis);
    }

    /// <summary>The column of <paramref name="bound"/> named <paramref name="name"/>, spelled as the object spells it, or null.</summary>
    private static string? FindColumnOf(SchemaObject? bound, string name)
    {
        if (ColumnsOf(bound) is not { } columns)
            return null;
        foreach (var column in columns)
        {
            if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
                return column.Name;
        }
        return null;
    }

    /// <summary>
    /// Whether a mention can name a column of the source introduced under
    /// <paramref name="alias"/>. An unqualified mention reaches every source in
    /// the frame; a qualified one reaches its own source, and reaches all of
    /// them when its qualifier names no source the frame knows (a derived-table
    /// or CTE alias the walk doesn't track as a source).
    /// </summary>
    private static bool MentionReaches(Mention mention, string alias, HashSet<string> aliases) =>
        mention.Qualifier is not { } qualifier
        || string.Equals(qualifier, alias, StringComparison.OrdinalIgnoreCase)
        || !aliases.Contains(qualifier);

    /// <summary>
    /// Records a FROM / JOIN / DML-target name. A CTE name, a temp table, a
    /// table variable and a trigger pseudo-table all name something that is not
    /// a schema object, so none produces a row — matching real, which records
    /// none of them either. A one-part name no object carries is a missing
    /// object, which real lists with a NULL schema — unless it is an UPDATE or
    /// DELETE target, which may be an alias the statement's FROM introduces.
    /// </summary>
    private static void RecordSource(
        Analysis analysis, BodyName name, SourceRole role, Frame frame,
        HashSet<string> cteNames, List<Token> tokens, int leafIndex)
    {
        if (IsNonSchemaName(name) || (name.SegmentCount == 1 && cteNames.Contains(name.Leaf)))
            return;
        var candidate = BuildObjectReference(analysis, name);
        if (candidate is null)
        {
            // A one-part call is a built-in rowset (STRING_SPLIT, OPENJSON …).
            if (name.SegmentCount != 1 || name.IsCall)
                return;
            if (role == SourceRole.Updated)
            {
                frame.PendingTargetName ??= name.Leaf;
                return;
            }
            candidate = new Reference(null, null, null, name.Leaf, ObjectOrColumnClass);
        }

        var reference = analysis.Remember(candidate);
        if (role == SourceRole.InsertTarget)
        {
            // A target with no column list is real's is_insert_all: the
            // statement writes every column without naming one, so no column
            // rows accompany it.
            if (leafIndex + 1 < tokens.Count && tokens[leafIndex + 1] is Operator { Character: '(' })
            {
                frame.InInsertColumnList = true;
                frame.InsertColumnListQualifier = AliasOf(tokens, leafIndex) ?? name.Leaf;
            }
            else if (reference.Bound is not null)
            {
                reference.IsInsertAll = true;
            }
        }

        frame.Sources.Add(new FrameSource(reference, AliasOf(tokens, leafIndex) ?? name.Leaf, role, frame.Depth));
    }

    /// <summary>
    /// Records an <c>EXEC</c> name. A one-part name is real's
    /// <c>is_caller_dependent</c>: only the caller's default schema settles it,
    /// so the row carries a NULL schema and a NULL id even when a procedure of
    /// that name exists (probe-confirmed).
    /// </summary>
    private static void RecordProcedure(Analysis analysis, BodyName name)
    {
        if (IsNonSchemaName(name))
            return;
        var candidate = name.SegmentCount == 1
            ? new Reference(null, null, null, name.Leaf, ObjectOrColumnClass) { IsCallerDependent = true }
            : BuildObjectReference(analysis, name);
        if (candidate is null)
            return;
        analysis.Remember(candidate).IsProcedureCall = true;
    }

    /// <summary>
    /// Records a qualified call: a function when the qualifier names a schema,
    /// and otherwise real's <c>is_ambiguous</c> row — the <c>q.m(…)</c> that
    /// could still turn out to be an XML or UDT method on a column named
    /// <c>q</c>, which the frame also notes as a mention of that column, so a
    /// genuine <c>doc.value('…')</c> over an <c>xml</c> column reports the
    /// column read too. Probe-confirmed in both directions: an unresolvable
    /// <c>mystery.value('…')</c> and a genuine one both report
    /// <c>is_ambiguous = 1</c> with the qualifier as the schema name; a
    /// three-part <c>t.doc.value('…')</c> whose first part names no database
    /// keeps it as the database name (probed 2026-10-06 against SQL Server
    /// 2025).
    /// </summary>
    private static void RecordCall(Analysis analysis, BodyName name, Frame frame)
    {
        if (name.SegmentCount < 2 || IsNonSchemaName(name))
            return;
        if (BuildCallReference(analysis, name) is not { } candidate)
            return;
        _ = analysis.Remember(candidate);
        if (candidate.IsAmbiguous)
            frame.NoteColumn(name.SegmentCount == 3 ? name[0] : null, name.Qualifier!);
    }

    /// <summary>
    /// The reference a qualified call names — a function under an existing
    /// schema, an ambiguous method call under anything else, or null for a
    /// chain too long to be either.
    /// </summary>
    private static Reference? BuildCallReference(Analysis analysis, BodyName name)
    {
        var database = analysis.Database;
        if (name.SegmentCount >= 4)
            return null;
        if (!TryResolveTargetSchema(database, name, out var databaseName, out var schema))
        {
            return name.SegmentCount == 2
                ? new Reference(null, null, name.Qualifier, name.Leaf, ObjectOrColumnClass) { IsAmbiguous = true }
                : null;
        }
        if (databaseName is not null)
        {
            if (IsSystemSchema(name.Qualifier!))
                return null;
            // A first part that names no database is a column's qualifier.
            if (analysis.OtherDatabases is { } others && others(databaseName) is not { } other)
                return new Reference(null, databaseName, name.Qualifier, name.Leaf, ObjectOrColumnClass) { IsAmbiguous = true };
            return new Reference(null, databaseName, name.Qualifier, name.Leaf, ObjectOrColumnClass)
            {
                CrossDatabaseResolved = analysis.OtherDatabases?.Invoke(databaseName) is { } target && target.Schemas.TryGetValue(name.Qualifier!, out var targetSchema)
                    ? targetSchema.Functions.GetValueOrDefault(name.Leaf)
                    : null,
            };
        }
        var resolved = schema is null ? null
            : schema.Functions.TryGetValue(name.Leaf, out var function) ? (SchemaObject)function
            : schema.Synonyms.TryGetValue(name.Leaf, out var synonym) ? synonym
            : null;
        return schema is not null && resolved is null && IsSystemSchema(schema.Name)
            ? null
            : new Reference(null, null, name.Qualifier, name.Leaf, ObjectOrColumnClass)
            {
                Resolved = resolved,
                SynonymBaseMissing = resolved is Synonym named && !SynonymBaseExists(analysis, named),
            };
    }

    /// <summary>
    /// The reference an object name written in a source or DML-target position
    /// denotes. Cross-server names keep their leading segments and resolve to
    /// no id; a cross-database one resolves in that database when the
    /// simulation holds it, for the DMV that reports it. A one-part name that
    /// resolves to nothing yields null, for the caller to settle, while a
    /// two-part one under a real schema keeps its row with a NULL id, which is
    /// real's deferred reference.
    /// </summary>
    private static Reference? BuildObjectReference(Analysis analysis, BodyName name)
    {
        var database = analysis.Database;
        if (name.SegmentCount >= 4)
            return new Reference(name[0], name[1], name.Qualifier, name.Leaf, ObjectOrColumnClass);
        if (!TryResolveTargetSchema(database, name, out var databaseName, out var schema))
            return null;
        if (databaseName is not null)
        {
            // A catalog view is not a user object; real records no dependency on
            // one, in this database or another.
            return IsSystemSchema(name.Qualifier!) ? null
                : new Reference(null, databaseName, name.Qualifier, name.Leaf, ObjectOrColumnClass)
                {
                    CrossDatabaseResolved = analysis.OtherDatabases?.Invoke(databaseName) is { } target && target.Schemas.TryGetValue(name.Qualifier!, out var targetSchema)
                        ? ResolveInSchema(targetSchema, name.Leaf)
                        : null,
                };
        }

        var resolved = schema is null ? null : ResolveInSchema(schema, name.Leaf);
        return resolved is null && (name.SegmentCount == 1 || IsSystemSchema(schema!.Name))
            ? null
            : new Reference(null, null, name.SegmentCount >= 2 ? name.Qualifier : null, name.Leaf, ObjectOrColumnClass)
            {
                Resolved = resolved,
                SynonymBaseMissing = resolved is Synonym synonym && !SynonymBaseExists(analysis, synonym),
            };
    }

    /// <summary>
    /// Whether a synonym's base names an object — in this database, or in
    /// another the simulation holds; a base in a database the walk can't see
    /// counts as present.
    /// </summary>
    private static bool SynonymBaseExists(Analysis analysis, Synonym synonym)
    {
        var baseName = synonym.BaseObject;
        var database = analysis.Database;
        if (baseName.Count >= 4)
            return true;
        if (baseName.Count == 3 && !database.Collation.Equals(baseName[0], database.Name))
        {
            if (analysis.OtherDatabases?.Invoke(baseName[0]) is not { } other)
                return analysis.OtherDatabases is null;
            database = other;
        }
        var schemaName = baseName.Count >= 2 ? baseName.ImmediateQualifier! : synonym.Schema.Name;
        return database.Schemas.TryGetValue(schemaName, out var schema)
            && (ResolveInSchema(schema, baseName.Leaf) is not null || schema.TableTypes.ContainsKey(baseName.Leaf));
    }

    /// <summary>
    /// The object a reference binds to for a surface that stores <em>ids</em>
    /// rather than names — the legacy <c>sysdepends</c> /
    /// <c>sys.sql_dependencies</c> pair, whose rows real settled when the
    /// referencing module was created. It differs from
    /// <see cref="Reference.ReferencedId"/> in one shape: a one-part
    /// <c>EXEC</c> name, which the modern surfaces report NULL and
    /// caller-dependent while the legacy pair names the procedure the default
    /// schema holds (probe-confirmed).
    /// </summary>
    internal static SchemaObject? ResolveForStoredId(Database database, Reference reference) =>
        reference.Resolved
        ?? (reference.IsCallerDependent && database.Schemas.TryGetValue(Database.DefaultSchemaName, out var schema)
            ? ResolveInSchema(schema, reference.EntityName)
            : null);

    private static SchemaObject? ResolveInSchema(Schema schema, string leaf) =>
        schema.HeapTables.TryGetValue(leaf, out var table) ? table
        : schema.Views.TryGetValue(leaf, out var view) ? view
        : schema.Functions.TryGetValue(leaf, out var function) ? function
        : schema.Procedures.TryGetValue(leaf, out var procedure) ? procedure
        : schema.Synonyms.TryGetValue(leaf, out var synonym) ? synonym
        : schema.Sequences.TryGetValue(leaf, out var sequence) ? sequence
        : null;

    /// <summary>
    /// Resolves the schema a name targets. Returns false when the qualifier
    /// names neither a schema of this database nor a database of this
    /// simulation — the signal that the chain isn't an object name at all.
    /// <paramref name="databaseName"/> comes back non-null for a three-part
    /// name reaching another database, whose ids are never resolvable here.
    /// </summary>
    private static bool TryResolveTargetSchema(Database database, BodyName name, out string? databaseName, out Schema? schema)
    {
        databaseName = null;
        schema = null;
        switch (name.SegmentCount)
        {
            case 1:
                return database.Schemas.TryGetValue(Database.DefaultSchemaName, out schema);
            case 2:
                return database.Schemas.TryGetValue(name.Qualifier!, out schema);
            case 3:
                if (database.Collation.Equals(name[0], database.Name))
                    return database.Schemas.TryGetValue(name.Qualifier!, out schema);
                databaseName = name[0];
                return true;
            default:
                return false;
        }
    }

    private static bool IsSystemSchema(string schemaName) =>
        string.Equals(schemaName, "sys", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schemaName, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a name that can't denote a schema object: a temp table, a table
    /// variable, or a trigger's <c>INSERTED</c> / <c>DELETED</c> pseudo-table.
    /// </summary>
    private static bool IsNonSchemaName(BodyName name) =>
        name.Leaf.StartsWith('#')
        || name.Leaf.StartsWith('@')
        || (name.SegmentCount == 1
            && (string.Equals(name.Leaf, "inserted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Leaf, "deleted", StringComparison.OrdinalIgnoreCase)));

    private static Reference Remember(Dictionary<string, Reference> byKey, List<Reference> ordered, Reference candidate)
    {
        var key = string.Concat(
            candidate.ServerName, KeySeparator, candidate.DatabaseName, KeySeparator,
            candidate.SchemaName, KeySeparator, candidate.EntityName);
        if (candidate.ReferencedClass == TypeClass)
            key += KeySeparator + "type";
        if (byKey.TryGetValue(key, out var existing))
        {
            existing.IsCallerDependent |= candidate.IsCallerDependent;
            existing.IsAmbiguous |= candidate.IsAmbiguous;
            return existing;
        }
        byKey[key] = candidate;
        ordered.Add(candidate);
        return candidate;
    }

    private static ColumnUse ColumnFor(Reference reference, string columnName)
    {
        foreach (var existing in reference.Columns)
        {
            if (string.Equals(existing.Name, columnName, StringComparison.OrdinalIgnoreCase))
                return existing;
        }
        var use = new ColumnUse(columnName);
        reference.Columns.Add(use);
        return use;
    }

    /// <summary>The column set a referenced object exposes, or null when it has none (a module, a synonym, a sequence).</summary>
    internal static HeapColumn[]? ColumnsOf(SchemaObject? resolved) => resolved switch
    {
        HeapTable table => table.Columns,
        View view => view.OutputColumns,
        _ => null,
    };

    /// <summary>The <c>column_id</c> a referenced column carries, or 0 when the object has no column by that name.</summary>
    internal static int ColumnIdOf(SchemaObject? resolved, string columnName)
    {
        switch (resolved)
        {
            case HeapTable table:
                return FindColumn(table, columnName)?.ColumnId ?? 0;
            case View view:
                for (var i = 0; i < view.OutputColumns.Length; i++)
                {
                    if (string.Equals(view.OutputColumns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
                        return i + 1;
                }
                return 0;
            default:
                return 0;
        }
    }

    private static HeapColumn? FindColumn(HeapTable table, string columnName)
    {
        foreach (var column in table.Columns)
        {
            if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                return column;
        }
        return null;
    }

    private static string SchemaNameOf(Database database, int schemaId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == schemaId)
                return schema.Name;
        }
        return Database.DefaultSchemaName;
    }

    /// <summary>True when <c>FOR</c> at <paramref name="index"/> closes a <c>NEXT VALUE FOR</c>.</summary>
    private static bool IsNextValueFor(List<Token> tokens, int index) =>
        index >= 2
        && tokens[index - 2] is UnquotedString { ContextualKeyword: ContextualKeyword.Next }
        && tokens[index - 1] is UnquotedString { ContextualKeyword: ContextualKeyword.Value };

    /// <summary>
    /// True when the <c>*</c> at <paramref name="index"/> is a select-list
    /// wildcard rather than multiplication — which turns on what precedes it (a
    /// <c>SELECT</c>, a comma, or the dot of a qualified star), never on an
    /// operand.
    /// </summary>
    private static bool StarIsSelectAll(List<Token> tokens, int index) => index > 0 && tokens[index - 1] switch
    {
        ReservedKeyword { Keyword: Keyword.Select or Keyword.Distinct or Keyword.Top } => true,
        Operator { Character: ',' or '.' } => true,
        _ => false,
    };

    /// <summary>The qualifier of a <c>q.*</c>, or null for a bare <c>*</c>.</summary>
    private static string? StarQualifier(List<Token> tokens, int index) =>
        index >= 2 && tokens[index - 1] is Operator { Character: '.' } && tokens[index - 2] is Name qualifier
            ? qualifier.Value
            : null;

    /// <summary>
    /// The alias a source was introduced under — the name after <c>AS</c>, or a
    /// bare name following the object name. Read only to attribute a
    /// <c>q.*</c> to the right source.
    /// </summary>
    private static string? AliasOf(List<Token> tokens, int leafIndex) =>
        leafIndex + 1 >= tokens.Count ? null
        : tokens[leafIndex + 1] is ReservedKeyword { Keyword: Keyword.As }
            ? leafIndex + 2 < tokens.Count && tokens[leafIndex + 2] is Name aliased ? aliased.Value : null
        : IsBareAlias(tokens[leafIndex + 1]) ? ((Name)tokens[leafIndex + 1]).Value
        : null;

    /// <summary>
    /// True for a name following a source that is its alias, rather than the
    /// word of a clause that follows it (<c>USING</c>, <c>CROSS APPLY</c>'s
    /// <c>APPLY</c>, a table hint's <c>TABLESAMPLE</c> …).
    /// </summary>
    private static bool IsBareAlias(Token token) => token switch
    {
        UnquotedString word => word.ContextualKeyword == ContextualKeyword.NotAKeyword,
        Name => true,
        _ => false,
    };

    /// <summary>One name chain lifted out of a definition's token stream.</summary>
    private readonly struct BodyName(string[] segments, bool isCall)
    {
        private readonly string[] segments = segments;

        /// <summary>True when an open paren follows the leaf.</summary>
        public readonly bool IsCall = isCall;

        public string this[int index] => this.segments[index];

        public int SegmentCount => this.segments.Length;

        public string Leaf => this.segments[^1];

        public string? Qualifier => this.segments.Length >= 2 ? this.segments[^2] : null;
    }

    /// <summary>
    /// Reads the dotted chain starting at <paramref name="index"/>, leaving the
    /// index on the chain's leaf so the caller's loop advances past it.
    /// </summary>
    private static BodyName ReadName(List<Token> tokens, ref int index)
    {
        List<string> segments = [((Name)tokens[index]).Value];
        while (index + 2 < tokens.Count
            && tokens[index + 1] is Operator { Character: '.' }
            && tokens[index + 2] is Name next)
        {
            segments.Add(next.Value);
            index += 2;
        }
        return new BodyName(
            [.. segments],
            index + 1 < tokens.Count && tokens[index + 1] is Operator { Character: '(' });
    }

    private static List<Token> Tokenize(string definition)
    {
        List<Token> tokens = [];
        var index = 0;
        while (Tokenizer.NextToken(definition, ref index, Collation.Baseline) is { } token)
        {
            if (token is not (Whitespace or Comment))
                tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>
    /// The names every <c>WITH cte [(col, …)] AS (…) [, …]</c> prefix in the
    /// body declares, wherever its statement sits. A one-part source reference
    /// matching one of these is the CTE, not a missing table of that name.
    /// </summary>
    private static HashSet<string> DeclaredCteNames(List<Token> tokens)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; start < tokens.Count; start++)
        {
            if (tokens[start] is not ReservedKeyword { Keyword: Keyword.With })
                continue;
            var i = start + 1;
            while (i < tokens.Count && tokens[i] is Name cteName)
            {
                var at = i + 1;
                if (at < tokens.Count && tokens[at] is Operator { Character: '(' })
                    at = PastParenGroup(tokens, at);
                if (at + 1 >= tokens.Count || tokens[at] is not ReservedKeyword { Keyword: Keyword.As } || tokens[at + 1] is not Operator { Character: '(' })
                    break;
                _ = names.Add(cteName.Value);
                i = PastParenGroup(tokens, at + 1);
                if (i >= tokens.Count || tokens[i] is not Operator { Character: ',' })
                    break;
                i++;
            }
        }
        return names;
    }

    private static int PastParenGroup(List<Token> tokens, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    if (depth == 0)
                        return i + 1;
                    break;
            }
        }
        return tokens.Count;
    }
}
