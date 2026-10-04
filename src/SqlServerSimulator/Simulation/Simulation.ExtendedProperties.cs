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

        // Ahead of the target resolution, unlike the read-only gate below:
        // real reports Msg 3930 even when the named table doesn't exist
        // (probe-confirmed).
        RejectWriteInDoomedTransaction(batch.Connection);

        string? name = null;
        var value = SqlValue.Null(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
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

        // Real's own signatures, positional or named (probed 2026-09-25
        // against SQL Server 2025): the drop takes no @value, an argument past
        // the list is Msg 8144, a missing @name Msg 201 ahead of an unknown
        // name's Msg 8145, and the value defaults to NULL.
        string[] parameters = op == ExtendedPropertyOp.Drop
            ? ["name", "level0type", "level0name", "level1type", "level1name", "level2type", "level2name"]
            : ["name", "value", "level0type", "level0name", "level1type", "level1name", "level2type", "level2name"];
        string? unknownParameter = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg.Name is null && i >= parameters.Length)
                throw SimulatedSqlException.TooManyArgumentsToFunction(procLabel);
            switch (arg.Name ?? parameters[i])
            {
                case var n when BuiltInToken.Equals(n, "name"):
                    // Real's drop procedure misspells its own name in this
                    // one message (probed 2026-09-26).
                    name = arg.Value.IsNull
                        ? throw SimulatedSqlException.InvalidExtendedPropertyParameter(op == ExtendedPropertyOp.Drop ? "sp_dropeextendedproperty" : procLabel)
                        : ExpectStringArgOrNull(arg.Value);
                    break;
                case var n when op != ExtendedPropertyOp.Drop && BuiltInToken.Equals(n, "value"):
                    value = arg.Value;
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
                    unknownParameter ??= arg.Name;
                    break;
            }
        }

        if (name is null)
            throw SimulatedSqlException.ProcedureExpectsParameter(procLabel, "name");
        if (unknownParameter is not null)
            throw SimulatedSqlException.NotAParameterForProcedure(unknownParameter, procLabel);

        if (ResolveExtendedPropertyTarget(
            batch, procLabel,
            level0Type, level0Name,
            level1Type, level1Name,
            level2Type, level2Name,
            name) is not var (key, targetLabel))
        {
            yield break;
        }

        // The read-only refusal comes after the target resolves — an unknown
        // target in a read-only database still reports Msg 15135
        // (probe-confirmed).
        batch.CurrentDatabase.RejectWriteWhenReadOnly();

        var props = batch.CurrentDatabase.ExtendedProperties;
        var hadPrevious = props.TryGetValue(key, out var previous);
        switch (op)
        {
            case ExtendedPropertyOp.Add:
                if (!props.TryAdd(key, value))
                    throw SimulatedSqlException.ExtendedPropertyAlreadyExists(name, targetLabel);
                break;
            case ExtendedPropertyOp.Update:
                if (!props.ContainsKey(key))
                    throw SimulatedSqlException.ExtendedPropertyDoesNotExist(name, targetLabel, state: 2);
                props[key] = value;
                break;
            case ExtendedPropertyOp.Drop:
                if (!props.TryRemove(key, out _))
                    throw SimulatedSqlException.ExtendedPropertyDoesNotExist(name, targetLabel, state: 1);
                break;
        }
        RecordDdlUndo(batch, () =>
        {
            if (hadPrevious)
                props[key] = previous;
            else
                _ = props.TryRemove(key, out _);
        });
        if (RaisesDdlEvents(batch.Parser))
            RecordExtendedPropertyEvent(batch, op, name, value, level0Type, level0Name, level1Type, level1Name, level2Type, level2Name);
        yield break;
    }

    /// <summary>
    /// Records the <c>CREATE_</c> / <c>ALTER_</c> / <c>DROP_EXTENDED_PROPERTY</c>
    /// event: the deepest level named is the object — a level-2 one with its
    /// level-1 host as the target, a schema or the database itself with an
    /// empty <c>SchemaName</c> — followed by the property and the procedure's
    /// arguments as written (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    private static void RecordExtendedPropertyEvent(
        BatchContext batch, ExtendedPropertyOp op, string name, SqlValue value,
        string? level0Type, string? level0Name, string? level1Type, string? level1Name, string? level2Type, string? level2Name)
    {
        var (schemaName, objectName, objectType, targetName, targetType) =
            level2Type is not null ? (level0Name ?? "", level2Name ?? "", level2Type, level1Name ?? "", level1Type ?? "")
            : level1Type is not null ? (level0Name ?? "", level1Name ?? "", level1Type, "", "")
            : level0Type is not null ? ("", level0Name ?? "", level0Type, "", "")
            : ("", batch.CurrentDatabase.Name, "DATABASE", "", "");
        var elements = new System.Text.StringBuilder();
        AppendElement(elements, "PropertyName", name);
        string? valueText = null;
        if (op != ExtendedPropertyOp.Drop && !value.IsNull)
        {
            valueText = value.Type.Category == SqlTypeCategory.String
                ? value.AsString
                : value.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;
            AppendElement(elements, "PropertyValue", valueText);
        }
        _ = elements.Append(op == ExtendedPropertyOp.Drop
            ? RenderEventParameters(name, level0Type, level0Name, level1Type, level1Name, level2Type, level2Name)
            : RenderEventParameters(name, valueText, level0Type, level0Name, level1Type, level1Name, level2Type, level2Name));
        var eventType = op switch
        {
            ExtendedPropertyOp.Add => "CREATE_EXTENDED_PROPERTY",
            ExtendedPropertyOp.Update => "ALTER_EXTENDED_PROPERTY",
            _ => "DROP_EXTENDED_PROPERTY",
        };
        RecordDdlEvent(batch.Parser, eventType, schemaName, objectName, objectType.ToUpperInvariant(),
            targetName, targetType.ToUpperInvariant(), trailingElements: elements.ToString());
    }

    /// <summary>
    /// Resolves the (level0type/name, level1type/name, level2type/name)
    /// triple to an <see cref="ExtendedPropertyKey"/>. Levels left null
    /// climb up the target hierarchy — <c>(null, null, null)</c> is
    /// DATABASE-level (class 0). The returned target-label string is the
    /// human-readable token that lands in the Msg 15233 / 15217 wording
    /// (probe-confirmed: <c>'object specified'</c> for DB-level,
    /// <c>'&lt;schemaName&gt;'</c> for schema, <c>'&lt;schema&gt;.&lt;name&gt;'</c>
    /// for a schema-scoped object or type, and
    /// <c>'&lt;schema&gt;.&lt;name&gt;.&lt;leaf&gt;'</c> below that, each
    /// name in its declared spelling).
    /// </summary>
    /// <remarks>
    /// Level0 is <c>SCHEMA</c>, or one of the terminal database-scoped hosts
    /// <c>TRIGGER</c> (a DDL trigger) / <c>FILEGROUP</c>. Level1 is a
    /// schema-scoped object — <c>TABLE</c> / <c>VIEW</c> / <c>PROCEDURE</c>
    /// / <c>FUNCTION</c> / <c>SEQUENCE</c> / <c>SYNONYM</c> / <c>RULE</c> /
    /// <c>DEFAULT</c> (class 1) — a <c>TYPE</c> (class 6, by user type id) or
    /// an <c>XML SCHEMA COLLECTION</c> (class 10). Level2 is a
    /// <c>COLUMN</c> of a table, view, table-valued function or table type
    /// (class 8 for the last), a <c>PARAMETER</c> of a procedure or function
    /// (class 2), a table's <c>CONSTRAINT</c>, or a table's or view's
    /// <c>INDEX</c> (class 7) or <c>TRIGGER</c>. Every refusal carries the
    /// state real's own resolution raises it with (probed 2026-09-26 against
    /// SQL Server 2025): Msg 15135 for a name that doesn't resolve, Msg 15600
    /// for a level type that doesn't fit, and Msg 15096 for a missing
    /// database-scoped host.
    /// </remarks>
    private static (ExtendedPropertyKey Key, string TargetLabel)? ResolveExtendedPropertyTarget(
        BatchContext batch,
        string procLabel,
        string? level0Type, string? level0Name,
        string? level1Type, string? level1Name,
        string? level2Type, string? level2Name,
        string propertyName)
    {
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        if (level0Type is null)
        {
            // Real ignores the later levels when level0 is null — the loader
            // emits the database-level call with all 6 level args absent —
            // but not a level0 name without its type.
            return level0Name is null
                ? (new ExtendedPropertyKey(0, 0, 0, propertyName), "object specified")
                : throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 2);
        }
        // A level's name without its type, or its type without its name, is
        // state 2 at every level.
        if (level0Name is null || (level1Type is null && level1Name is not null) || (level2Type is null && level2Name is not null))
            throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 2);

        // A DDL trigger lands class 1 / major_id = its object_id, a filegroup
        // class 20 = DATASPACE / major_id = its data_space_id; both are
        // terminal, so later levels don't apply.
        if (BuiltInToken.Equals(level0Type, "TRIGGER"))
        {
            return database.DdlTriggers.TryGetValue(level0Name, out var ddlTrigger)
                ? (new ExtendedPropertyKey(1, ddlTrigger.ObjectId, 0, propertyName), level0Name)
                : throw SimulatedSqlException.ExtendedPropertyHostMissing(level0Name, state: 10);
        }
        if (BuiltInToken.Equals(level0Type, "FILEGROUP"))
        {
            return database.Filegroups.TryGetValue(level0Name, out var dataSpaceId)
                ? (new ExtendedPropertyKey(20, dataSpaceId, 0, propertyName), level0Name)
                : throw SimulatedSqlException.ExtendedPropertyHostMissing(level0Name, state: 1);
        }
        if (BuiltInToken.Equals(level0Type, "USER"))
        {
            // Class 4 = DATABASE_PRINCIPAL. The built-in principals and the
            // roles are state 2, and a user hosts nothing beneath it (state
            // 27) — the level1 slot is a pre-2005 owner's, not a schema's.
            if (!database.Principals.TryGetValue(level0Name, out var user))
                throw SimulatedSqlException.ExtendedPropertyTargetMissing(level0Name, state: 1);
            if (user.PrincipalId < 5 || user.IsFixedRole || user.TypeCode == "R")
                throw SimulatedSqlException.ExtendedPropertyTargetMissing(user.Name, state: 2);
            return level1Type is null
                ? (new ExtendedPropertyKey(4, user.PrincipalId, 0, propertyName), user.Name)
                : throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{user.Name}.{level1Name}", state: 27);
        }
        if (!BuiltInToken.Equals(level0Type, "SCHEMA"))
            throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 3);
        if (!database.Schemas.TryGetValue(level0Name, out var schema))
            throw SimulatedSqlException.ExtendedPropertyTargetMissing(level0Name, state: 4);

        if (level1Type is null)
            return (new ExtendedPropertyKey(3, schema.SchemaId, 0, propertyName), schema.Name);
        if (level1Name is null)
            throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 2);

        var level1Label = $"{schema.Name}.{level1Name}";
        if (BuiltInToken.Equals(level1Type, "TYPE"))
        {
            if (schema.TableTypes.TryGetValue(level1Name, out var tableType))
            {
                if (level2Type is null)
                    return (new ExtendedPropertyKey(6, tableType.UserTypeId, 0, propertyName), $"{schema.Name}.{tableType.Name}");
                return ResolveColumnLevel(8, tableType.UserTypeId, tableType.Columns, $"{schema.Name}.{tableType.Name}");
            }
            if (schema.AliasTypes.TryGetValue(level1Name, out var aliasType))
            {
                // A level2 beneath an alias type is accepted and recorded
                // nowhere (probed 2026-09-26).
                return level2Type is null
                    ? (new ExtendedPropertyKey(6, aliasType.UserTypeId, 0, propertyName), $"{schema.Name}.{aliasType.Name}")
                    : null;
            }
            throw SimulatedSqlException.ExtendedPropertyTargetMissing(level1Label, state: 6);
        }
        if (BuiltInToken.Equals(level1Type, "XML SCHEMA COLLECTION"))
        {
            return schema.XmlSchemaCollections.TryGetValue(level1Name, out var collection) && level2Type is null
                ? (new ExtendedPropertyKey(10, collection.Id, 0, propertyName), $"{schema.Name}.{collection.Name}")
                : throw SimulatedSqlException.ExtendedPropertyTargetMissing(level1Label, state: 8);
        }

        Span<char> kind = stackalloc char[level1Type.Length];
        _ = level1Type.AsSpan().ToUpperInvariant(kind);
        SchemaObject? obj = kind switch
        {
            "DEFAULT" => schema.Defaults.GetValueOrDefault(level1Name),
            "FUNCTION" => schema.Functions.GetValueOrDefault(level1Name),
            "PROCEDURE" => schema.Procedures.GetValueOrDefault(level1Name),
            "RULE" => schema.Rules.GetValueOrDefault(level1Name),
            "SECURITY POLICY" => schema.SecurityPolicies.GetValueOrDefault(level1Name),
            "SEQUENCE" => schema.Sequences.GetValueOrDefault(level1Name),
            "SYNONYM" => schema.Synonyms.GetValueOrDefault(level1Name),
            "TABLE" => schema.HeapTables.GetValueOrDefault(level1Name),
            "VIEW" => schema.Views.GetValueOrDefault(level1Name),
            _ => throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 5),
        };
        // A name held by an object of another kind is state 9, one held by
        // nothing state 8.
        if (obj is null)
            throw SimulatedSqlException.ExtendedPropertyTargetMissing(level1Label, state: schema.TryFindInSharedNamespace(level1Name, out _) ? (byte)9 : (byte)8);

        var objLabel = $"{schema.Name}.{obj.Name}";
        if (level2Type is null)
            return (new ExtendedPropertyKey(1, obj.ObjectId, 0, propertyName), objLabel);
        if (level2Name is null)
            throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 2);

        Span<char> level2Kind = stackalloc char[level2Type.Length];
        _ = level2Type.AsSpan().ToUpperInvariant(level2Kind);
        switch (level2Kind)
        {
            case "COLUMN":
                return obj switch
                {
                    HeapTable table => ResolveColumnLevel(1, obj.ObjectId, table.Columns, objLabel, stableIds: true),
                    View view => ResolveColumnLevel(1, obj.ObjectId, view.OutputColumns, objLabel),
                    InlineTableValuedFunction inline => ResolveColumnLevel(1, obj.ObjectId, inline.OutputColumns, objLabel),
                    MultiStatementTableValuedFunction multi => ResolveColumnLevel(1, obj.ObjectId, multi.OutputColumns, objLabel),
                    UserDefinedFunction => throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{objLabel}.{level2Name}", state: 14),
                    _ => throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 12),
                };
            case "CONSTRAINT":
                return obj is HeapTable constrained
                    ? ResolveConstraintLevel(constrained, objLabel)
                    : throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 12);
            case "INDEX":
                var identities = obj switch
                {
                    HeapTable indexedTable => indexedTable.IndexIdentities(),
                    View indexedView => indexedView.IndexIdentities(),
                    _ => throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 12),
                };
                foreach (var identity in identities)
                {
                    if (identity.Name is { } indexName && collation.Equals(indexName, level2Name))
                        return (new ExtendedPropertyKey(7, obj.ObjectId, identity.IndexId, propertyName), $"{objLabel}.{indexName}");
                }
                if (obj is HeapTable withXmlOrSpatial)
                {
                    foreach (var xmlIndex in withXmlOrSpatial.XmlIndexes)
                    {
                        if (collation.Equals(xmlIndex.Name, level2Name))
                            return (new ExtendedPropertyKey(7, obj.ObjectId, xmlIndex.IndexId, propertyName), $"{objLabel}.{xmlIndex.Name}");
                    }
                    foreach (var spatialIndex in withXmlOrSpatial.SpatialIndexes)
                    {
                        if (collation.Equals(spatialIndex.Name, level2Name))
                            return (new ExtendedPropertyKey(7, obj.ObjectId, spatialIndex.IndexId, propertyName), $"{objLabel}.{spatialIndex.Name}");
                    }
                }
                // Real answers a missing index with Msg 15600, not Msg 15135.
                throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 17);
            case "PARAMETER":
                var parameterNames = obj switch
                {
                    Procedure procedure => Array.ConvertAll(procedure.Parameters, static parameter => parameter.Name),
                    UserDefinedFunction function => Array.ConvertAll(function.Parameters, static parameter => parameter.Name),
                    _ => throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 12),
                };
                for (var i = 0; i < parameterNames.Length; i++)
                {
                    if (level2Name.StartsWith('@') && collation.Equals(parameterNames[i], level2Name[1..]))
                        return (new ExtendedPropertyKey(2, obj.ObjectId, i + 1, propertyName), $"{objLabel}.@{parameterNames[i]}");
                }
                throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{objLabel}.{level2Name}", state: 16);
            case "TRIGGER":
                if (obj is not (HeapTable or View))
                    throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 12);
                return schema.Triggers.TryGetValue(level2Name, out var trigger) && ReferenceEquals(trigger.Parent, obj)
                    ? (new ExtendedPropertyKey(1, trigger.ObjectId, 0, propertyName), $"{objLabel}.{trigger.Name}")
                    : throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{objLabel}.{level2Name}", state: 17);
            default:
                throw SimulatedSqlException.InvalidExtendedPropertyLevel(procLabel, state: 11);
        }

        // The minor_id is the column_id: a table's stable one, survives an
        // earlier column's drop; anything else's is its 1-based position.
        (ExtendedPropertyKey, string) ResolveColumnLevel(byte @class, int majorId, HeapColumn[] columns, string ownerLabel, bool stableIds = false)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (collation.Equals(columns[i].Name, level2Name))
                    return (new ExtendedPropertyKey(@class, majorId, stableIds ? columns[i].ColumnId : i + 1, propertyName), $"{ownerLabel}.{columns[i].Name}");
            }
            throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{ownerLabel}.{level2Name}", state: 15);
        }

        // Constraints (PK / UQ / FK / CHECK / DEFAULT) all carry their own
        // object_id and reuse class=1 (OBJECT_OR_COLUMN) like every other
        // schema object — same wire shape real SQL Server uses.
        (ExtendedPropertyKey, string) ResolveConstraintLevel(HeapTable table, string ownerLabel)
        {
            foreach (var k in table.KeyConstraints)
            {
                if (collation.Equals(k.Name, level2Name))
                    return (new ExtendedPropertyKey(1, k.ObjectId, 0, propertyName), $"{ownerLabel}.{k.Name}");
            }
            foreach (var c in table.CheckConstraints)
            {
                if (collation.Equals(c.Name, level2Name))
                    return (new ExtendedPropertyKey(1, c.ObjectId, 0, propertyName), $"{ownerLabel}.{c.Name}");
            }
            foreach (var fk in table.OutgoingForeignKeys)
            {
                if (collation.Equals(fk.Name, level2Name))
                    return (new ExtendedPropertyKey(1, fk.ObjectId, 0, propertyName), $"{ownerLabel}.{fk.Name}");
            }
            foreach (var col in table.Columns)
            {
                if (col.DefaultConstraint is { } dc && collation.Equals(dc.Name, level2Name))
                    return (new ExtendedPropertyKey(1, dc.ObjectId, 0, propertyName), $"{ownerLabel}.{dc.Name}");
            }
            throw SimulatedSqlException.ExtendedPropertyTargetMissing($"{ownerLabel}.{level2Name}", state: 19);
        }
    }

    /// <summary>
    /// Carries a module's column and parameter extended properties across an
    /// <c>ALTER</c> by name, as real does (probed 2026-09-26 against SQL Server
    /// 2025): one whose column or parameter survives follows it to its new
    /// position, and one whose name is gone goes with it. The object's own
    /// properties stay, since the object id does.
    /// </summary>
    internal static void RebindExtendedProperties(BatchContext batch, SchemaObject replaced, SchemaObject replacement)
    {
        var database = batch.CurrentDatabase;
        var props = database.ExtendedProperties;
        var before = new List<KeyValuePair<ExtendedPropertyKey, SqlValue>>();
        foreach (var entry in props)
        {
            if (entry.Key.MajorId == replaced.ObjectId && entry.Key.MinorId > 0 && entry.Key.Class is 1 or 2)
                before.Add(entry);
        }
        if (before.Count == 0)
            return;

        // Every entry leaves before any returns, so one moving onto a slot
        // another is vacating can't be swept up with it.
        foreach (var (key, _) in before)
            _ = props.TryRemove(key, out _);
        foreach (var (key, value) in before)
        {
            var (oldNames, newNames) = key.Class == 1
                ? (ColumnNamesOf(replaced), ColumnNamesOf(replacement))
                : (ParameterNamesOf(replaced), ParameterNamesOf(replacement));
            if (key.MinorId > oldNames.Length)
                continue;
            var moved = Array.FindIndex(newNames, name => database.Collation.Equals(name, oldNames[key.MinorId - 1]));
            if (moved >= 0)
                props[new ExtendedPropertyKey(key.Class, key.MajorId, moved + 1, key.Name)] = value;
        }
        RecordDdlUndo(batch, () =>
        {
            foreach (var entry in props)
            {
                if (entry.Key.MajorId == replaced.ObjectId && entry.Key.MinorId > 0 && entry.Key.Class is 1 or 2)
                    _ = props.TryRemove(entry.Key, out _);
            }
            foreach (var (key, value) in before)
                props[key] = value;
        });

        static string[] ColumnNamesOf(SchemaObject obj) => obj switch
        {
            View view => Array.ConvertAll(view.OutputColumns, static column => column.Name),
            InlineTableValuedFunction inline => Array.ConvertAll(inline.OutputColumns, static column => column.Name),
            MultiStatementTableValuedFunction multi => Array.ConvertAll(multi.OutputColumns, static column => column.Name),
            _ => [],
        };

        static string[] ParameterNamesOf(SchemaObject obj) => obj switch
        {
            Procedure procedure => Array.ConvertAll(procedure.Parameters, static parameter => parameter.Name),
            UserDefinedFunction function => Array.ConvertAll(function.Parameters, static parameter => parameter.Name),
            _ => [],
        };
    }

    /// <summary>
    /// Coerces a sproc argument to <see cref="string"/>, or null for NULL —
    /// the level0..2 type/name args may legitimately be omitted (null climbs
    /// the target hierarchy).
    /// </summary>
    private static string? ExpectStringArgOrNull(SqlValue value) =>
        value.IsNull ? null : value.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;
}
