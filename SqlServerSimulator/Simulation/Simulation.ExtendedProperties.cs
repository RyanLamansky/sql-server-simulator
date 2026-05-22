using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Mode passed to <see cref="InvokeSpExtendedProperty"/> to select the
    /// add / update / drop dispatch. The three sprocs share argument parsing
    /// and target resolution; only the final dict mutation differs.
    /// </summary>
    private enum ExtendedPropertyOp
    {
        Add,
        Update,
        Drop,
    }

    /// <summary>
    /// Body for <c>sp_addextendedproperty</c> / <c>sp_updateextendedproperty</c>
    /// / <c>sp_dropextendedproperty</c>. Parses the named-arg list, resolves
    /// the target to a <see cref="ExtendedPropertyKey"/>, then performs the
    /// requested operation against
    /// <see cref="Database.ExtendedProperties"/>. Behavior probed against
    /// SQL Server 2025 (2026-05-14) — see the doc strings on
    /// <see cref="ResolveExtendedPropertyTarget"/> + the error factories
    /// for verbatim wording.
    /// </summary>
    /// <remarks>
    /// Cursor on entry: first token after the procedure name (or trailing
    /// terminator if no args). Cursor on exit: the trailing terminator.
    /// Arguments may appear in any order but must be named (the simulator
    /// doesn't model positional binding for the system sprocs); a positional
    /// arg falls through to Msg 15600 ("invalid parameter") via the
    /// argument-name lookup miss.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpExtendedProperty(BatchContext batch, ExtendedPropertyOp op)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string? name = null;
        var value = SqlValue.Null(NVarcharSqlType.MaxForm);
        var hasValueArg = false;
        string? level0Type = null;
        string? level0Name = null;
        string? level1Type = null;
        string? level1Name = null;
        string? level2Type = null;
        string? level2Name = null;
        var procLabel = op switch
        {
            ExtendedPropertyOp.Add => "sp_addextendedproperty",
            ExtendedPropertyOp.Update => "sp_updateextendedproperty",
            ExtendedPropertyOp.Drop => "sp_dropextendedproperty",
            _ => throw new InvalidOperationException(),
        };

        foreach (var arg in arguments)
        {
            if (arg.Name is null)
                throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);
            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "name"):
                    name = ExpectStringArg(arg.Value);
                    break;
                case var n when BuiltInToken.Equals(n, "value"):
                    value = arg.Value;
                    hasValueArg = true;
                    break;
                case var n when BuiltInToken.Equals(n, "level0type"):
                    level0Type = ExpectStringArgOrNull(arg.Value);
                    break;
                case var n when BuiltInToken.Equals(n, "level0name"):
                    level0Name = ExpectStringArgOrNull(arg.Value);
                    break;
                case var n when BuiltInToken.Equals(n, "level1type"):
                    level1Type = ExpectStringArgOrNull(arg.Value);
                    break;
                case var n when BuiltInToken.Equals(n, "level1name"):
                    level1Name = ExpectStringArgOrNull(arg.Value);
                    break;
                case var n when BuiltInToken.Equals(n, "level2type"):
                    level2Type = ExpectStringArgOrNull(arg.Value);
                    break;
                case var n when BuiltInToken.Equals(n, "level2name"):
                    level2Name = ExpectStringArgOrNull(arg.Value);
                    break;
                default:
                    throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);
            }
        }

        if (name is null)
            throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);
        if (op == ExtendedPropertyOp.Add && !hasValueArg)
            throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);

        var (key, targetLabel) = ResolveExtendedPropertyTarget(
            batch, procLabel,
            level0Type, level0Name,
            level1Type, level1Name,
            level2Type, level2Name,
            name);

        var props = batch.CurrentDatabase.ExtendedProperties;
        switch (op)
        {
            case ExtendedPropertyOp.Add:
                if (!props.TryAdd(key, value))
                    throw SimulatedSqlException.ExtendedPropertyAlreadyExists(name, targetLabel);
                break;
            case ExtendedPropertyOp.Update:
                if (!props.ContainsKey(key))
                    throw SimulatedSqlException.ExtendedPropertyDoesNotExist(name, targetLabel);
                props[key] = value;
                break;
            case ExtendedPropertyOp.Drop:
                if (!props.TryRemove(key, out _))
                    throw SimulatedSqlException.ExtendedPropertyDoesNotExist(name, targetLabel);
                break;
        }
        yield break;
    }

    /// <summary>
    /// Resolves the (level0type/name, level1type/name, level2type/name)
    /// triple to an <see cref="ExtendedPropertyKey"/>. Levels left null
    /// climb up the target hierarchy — <c>(null, null, null)</c> is
    /// DATABASE-level (class 0). The returned target-label string is the
    /// human-readable token that lands in the Msg 15233 / 15217 wording
    /// (probe-confirmed: <c>'object specified'</c> for DB-level,
    /// <c>'&lt;schemaName&gt;'</c> for schema, <c>'&lt;schema&gt;.&lt;table&gt;'</c>
    /// for table / view / proc / func, <c>'&lt;schema&gt;.&lt;table&gt;.&lt;col&gt;'</c>
    /// for column).
    /// </summary>
    /// <remarks>
    /// Closed accept-list for level types (probe-confirmed against AW's
    /// <c>SqlExtendedProperty</c> Host relationships): level0 = <c>SCHEMA</c>;
    /// level1 = <c>TABLE</c> / <c>VIEW</c> / <c>PROCEDURE</c> / <c>FUNCTION</c>
    /// / <c>TYPE</c>; level2 = <c>COLUMN</c>. Anything outside this set →
    /// Msg 15600 "invalid parameter". Missing target object → Msg 15135
    /// "Extended properties are not permitted on '…'".
    /// </remarks>
    private static (ExtendedPropertyKey Key, string TargetLabel) ResolveExtendedPropertyTarget(
        BatchContext batch,
        string procLabel,
        string? level0Type, string? level0Name,
        string? level1Type, string? level1Name,
        string? level2Type, string? level2Name,
        string propertyName)
    {
        if (level0Type is null)
        {
            // All later levels must be null too (real SQL Server ignores
            // them when level0 is null — the loader emits the database-level
            // call with all 6 level args absent).
            return (new ExtendedPropertyKey(0, 0, 0, propertyName), "object specified");
        }
        if (!BuiltInToken.Equals(level0Type, "SCHEMA"))
            throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);
        if (level0Name is null)
            throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);

        if (!batch.CurrentDatabase.Schemas.TryGetValue(level0Name, out var schema))
            throw SimulatedSqlException.ExtendedPropertyTargetMissing(level0Name);

        if (level1Type is null)
            return (new ExtendedPropertyKey(3, schema.SchemaId, 0, propertyName), schema.Name);

        if (level1Name is null)
            throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);

        // Resolve the level1 object inside the schema. Closed accept-list of
        // level1 kinds — anything else raises Msg 15600 (real SQL Server's
        // grammar accepts more but AW only uses these five).
        SchemaObject? obj = null;
        if (BuiltInToken.Equals(level1Type, "TABLE"))
        {
            if (schema.HeapTables.TryGetValue(level1Name, out var t)) obj = t;
        }
        else if (BuiltInToken.Equals(level1Type, "VIEW"))
        {
            if (schema.Views.TryGetValue(level1Name, out var v)) obj = v;
        }
        else if (BuiltInToken.Equals(level1Type, "PROCEDURE"))
        {
            if (schema.Procedures.TryGetValue(level1Name, out var p)) obj = p;
        }
        else if (BuiltInToken.Equals(level1Type, "FUNCTION"))
        {
            if (schema.Functions.TryGetValue(level1Name, out var f)) obj = f;
        }
        else if (BuiltInToken.Equals(level1Type, "TYPE"))
        {
            // TYPE-level extended properties target alias or table types.
            // The object_id is the table-type's, alias types don't carry one.
            if (schema.TableTypes.TryGetValue(level1Name, out var tt))
                obj = tt;
        }
        else
        {
            throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);
        }

        if (obj is null)
            throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{schema.Name}.{level1Name}");

        if (level2Type is null)
            return (new ExtendedPropertyKey(1, obj.ObjectId, 0, propertyName), $"{schema.Name}.{obj.Name}");

        if (level2Name is null)
            throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);

        if (obj is not HeapTable table)
            throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{schema.Name}.{obj.Name}.{level2Name}");

        if (BuiltInToken.Equals(level2Type, "COLUMN"))
        {
            // 1-based column ordinal (real SQL Server's minor_id convention).
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, level2Name))
                    return (new ExtendedPropertyKey(1, obj.ObjectId, i + 1, propertyName), $"{schema.Name}.{obj.Name}.{table.Columns[i].Name}");
            }
            throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{schema.Name}.{obj.Name}.{level2Name}");
        }

        if (BuiltInToken.Equals(level2Type, "CONSTRAINT"))
        {
            // Constraints (PK / UQ / FK / CHECK / DEFAULT) all carry their own
            // object_id and reuse class=1 (OBJECT_OR_COLUMN) like every other
            // schema object — same wire shape real SQL Server uses.
            foreach (var k in table.KeyConstraints)
            {
                if (batch.CurrentDatabase.Collation.Equals(k.Name, level2Name))
                    return (new ExtendedPropertyKey(1, k.ObjectId, 0, propertyName), $"{schema.Name}.{obj.Name}.{k.Name}");
            }
            foreach (var c in table.CheckConstraints)
            {
                if (batch.CurrentDatabase.Collation.Equals(c.Name, level2Name))
                    return (new ExtendedPropertyKey(1, c.ObjectId, 0, propertyName), $"{schema.Name}.{obj.Name}.{c.Name}");
            }
            foreach (var fk in table.OutgoingForeignKeys)
            {
                if (batch.CurrentDatabase.Collation.Equals(fk.Name, level2Name))
                    return (new ExtendedPropertyKey(1, fk.ObjectId, 0, propertyName), $"{schema.Name}.{obj.Name}.{fk.Name}");
            }
            foreach (var col in table.Columns)
            {
                if (col.DefaultConstraint is { } dc && batch.CurrentDatabase.Collation.Equals(dc.Name, level2Name))
                    return (new ExtendedPropertyKey(1, dc.ObjectId, 0, propertyName), $"{schema.Name}.{obj.Name}.{dc.Name}");
            }
            throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{schema.Name}.{obj.Name}.{level2Name}");
        }

        if (BuiltInToken.Equals(level2Type, "INDEX"))
        {
            // INDEX-level uses class=7. major_id=table.object_id, minor_id=
            // index_id (matches real SQL Server's sys.extended_properties).
            // The index_id is derived the same way as sys.indexes: PK gets 1
            // (or HEAP-row 0 if no PK); other key constraints + indexes get
            // sequential ids in ObjectId order starting at 2 (or 1 if no PK).
            var indexId = ComputeIndexId(batch.CurrentDatabase.Collation, table, level2Name);
            return indexId < 0
                ? throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{schema.Name}.{obj.Name}.{level2Name}")
                : (new ExtendedPropertyKey(7, obj.ObjectId, indexId, propertyName), $"{schema.Name}.{obj.Name}.{level2Name}");
        }

        throw SimulatedSqlException.InvalidExtendedPropertyParameter(procLabel);
    }

    /// <summary>
    /// Resolves an index name on a table to its <c>index_id</c> — the same
    /// numbering <c>sys.indexes</c> exposes (PK = 1 / HEAP = 0; other keys +
    /// <c>CREATE INDEX</c>-declared indexes get sequential ids in <c>ObjectId</c>
    /// order starting at the next slot). Returns -1 when no index matches.
    /// </summary>
    private static int ComputeIndexId(Collation collation, HeapTable table, string indexName)
    {
        KeyConstraint? primaryKey = null;
        foreach (var k in table.KeyConstraints)
        {
            if (k.Kind == KeyConstraintKind.PrimaryKey)
            {
                primaryKey = k;
                break;
            }
        }
        if (primaryKey is not null && collation.Equals(primaryKey.Name, indexName))
            return 1;

        var others = new List<(int ObjectId, string Name)>();
        foreach (var k in table.KeyConstraints)
        {
            if (!ReferenceEquals(k, primaryKey))
                others.Add((k.ObjectId, k.Name));
        }
        foreach (var ix in table.Indexes)
            others.Add((ix.ObjectId, ix.Name));
        others.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

        var nextIndexId = primaryKey is null ? 1 : 2;
        foreach (var (_, name) in others)
        {
            if (collation.Equals(name, indexName))
                return nextIndexId;
            nextIndexId++;
        }
        return -1;
    }

    /// <summary>
    /// Coerces a sproc argument to <see cref="string"/> for the
    /// type/name/property-name parameter shapes. NULL raises the parameter-
    /// validation error (the loader never passes null for these), matching
    /// real SQL Server's Msg 15600 fall-through.
    /// </summary>
    private static string ExpectStringArg(SqlValue value) =>
        value.IsNull
            ? throw SimulatedSqlException.InvalidExtendedPropertyParameter("sp_addextendedproperty")
            : value.CoerceTo(NVarcharSqlType.MaxForm).AsString;

    /// <summary>
    /// Same as <see cref="ExpectStringArg"/> but returns null for a NULL
    /// SqlValue input — used for the level0..2 type/name args, which may
    /// legitimately be omitted (null climbs the target hierarchy).
    /// </summary>
    private static string? ExpectStringArgOrNull(SqlValue value) =>
        value.IsNull ? null : value.CoerceTo(NVarcharSqlType.MaxForm).AsString;
}
