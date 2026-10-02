using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_help — the object-summary proc, split out of Simulation.HelpProcs.cs
// because it is a pure consumer of the rest of the family. Emits a sequence of
// result sets whose membership depends on what @objname resolved to; the
// sibling partial holds the name resolution, sp_helptext, and the sp_helpindex
// / sp_helpconstraint sets this proc delegates to. Every shape below is
// probe-confirmed against SQL Server 2025 (2026-07-31).
partial class Simulation
{
    // Real builds sp_help's flag columns from varchar(35) locals ('yes' / 'no'
    // / 'none'), its Prec / Scale display cells from char(5), and its
    // object-type text from substring(spt_values.name, 5, 31).
    private static readonly VarcharSqlType HelpFlagType =
        VarcharSqlType.Get(35, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType HelpPrecScaleType =
        VarcharSqlType.Get(5, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType HelpObjectTypeType =
        NVarcharSqlType.Get(31, Collation.Baseline, Coercibility.Implicit);

    // IDENT_SEED / IDENT_INCR are numeric(38, 0).
    private static readonly DecimalSqlType HelpIdentityValueType = DecimalSqlType.Get(38, 0);

    private static readonly SqlType[] SpHelpObjectSchema =
        [SqlType.SystemName, SqlType.SystemName, HelpObjectTypeType, SqlType.DateTime];

    private static readonly string[] SpHelpObjectColumnNames =
        ["Name", "Owner", "Type", "Created_datetime"];

    // Name and Created_datetime are NOT NULL, and Seed / Increment are numeric,
    // as real describes them on the wire (probed 2026-09-28 and 2026-10-02
    // against SQL Server 2025); so are the Identity name, the single-column
    // RowGuidCol / filegroup / referencing-view sets, and a parameter's Length
    // and Param_order.
    private static readonly bool[] SpHelpObjectNullability = [false, true, true, false];

    private static readonly bool[] SpHelpIdentityNullability = [false, true, true, true];

    private static readonly bool[] SingleNotNullColumn = [false];

    private static readonly bool[] SpHelpParameterNullability = [true, true, false, true, true, false, true];

    private static readonly bool[] SpHelpIdentityReportsNumeric = [false, true, true, false];

    private static readonly SqlType[] SpHelpObjectListSchema =
        [SqlType.SystemName, SqlType.SystemName, HelpObjectTypeType];

    private static readonly string[] SpHelpObjectListColumnNames = ["Name", "Owner", "Object_type"];

    private static readonly SqlType[] SpHelpColumnSchema =
    [
        SqlType.SystemName,  // Column_name
        SqlType.SystemName,  // Type
        HelpFlagType,        // Computed
        SqlType.Int32,       // Length
        HelpPrecScaleType,   // Prec
        HelpPrecScaleType,   // Scale
        HelpFlagType,        // Nullable
        HelpFlagType,        // TrimTrailingBlanks
        HelpFlagType,        // FixedLenNullInSource
        SqlType.SystemName,  // Collation
    ];

    private static readonly string[] SpHelpColumnColumnNames =
    [
        "Column_name", "Type", "Computed", "Length", "Prec", "Scale", "Nullable",
        "TrimTrailingBlanks", "FixedLenNullInSource", "Collation",
    ];

    private static readonly SqlType[] SpHelpIdentitySchema =
        [SqlType.SystemName, HelpIdentityValueType, HelpIdentityValueType, SqlType.Int32];

    private static readonly string[] SpHelpIdentityColumnNames =
        ["Identity", "Seed", "Increment", "Not For Replication"];

    // One shared single-sysname schema for the three one-column help sets;
    // sharing the array also keeps them on one RowLayout geometry cache entry,
    // which is keyed by array identity.
    private static readonly SqlType[] SingleSystemNameColumn = [SqlType.SystemName];

    private static readonly string[] SpHelpRowGuidColColumnNames = ["RowGuidCol"];

    private static readonly SqlType[] SpHelpParameterSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.SmallInt, SqlType.Int32,
        SqlType.Int32, SqlType.Int32, SqlType.SystemName,
    ];

    private static readonly string[] SpHelpParameterColumnNames =
        ["Parameter_name", "Type", "Length", "Prec", "Scale", "Param_order", "Collation"];

    private static readonly string[] SpHelpFilegroupColumnNames = ["Data_located_on_filegroup"];

    private static readonly string[] SpHelpReferencingViewColumnNames = ["Table is referenced by views"];

    private static readonly SqlType[] SpHelpTypeSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.SmallInt, SqlType.Int32,
        SqlType.Int32, HelpFlagType, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName,
    ];

    private static readonly string[] SpHelpTypeColumnNames =
        ["Type_name", "Storage_type", "Length", "Prec", "Scale", "Nullable", "Default_name", "Rule_name", "Collation"];

    private static readonly string[] SpHelpUserTypeColumnNames =
        ["User_type", "Storage_type", "Length", "Prec", "Scale", "Nullable", "Default_name", "Rule_name", "Collation"];

    /// <summary>
    /// Handles <c>EXEC sp_help [@objname]</c>.
    /// <para>
    /// With no argument: two result sets — every object in the current
    /// database (<c>Name</c> / <c>Owner</c> / <c>Object_type</c>, ordered by
    /// owner, then object type descending, then name) followed by every
    /// user-defined type.
    /// </para>
    /// <para>
    /// With a name that resolves to a user-defined type: the single
    /// nine-column type result set.
    /// </para>
    /// <para>
    /// With a name that resolves to an object: <c>Name</c> / <c>Owner</c> /
    /// <c>Type</c> / <c>Created_datetime</c>, then — as the object kind
    /// warrants — the column detail set, the identity and rowguidcol sets, the
    /// parameter set, and for a table the filegroup set plus
    /// <c>sp_helpindex</c>'s and <c>sp_helpconstraint</c>'s output and the list
    /// of schema-bound views referencing it. A view gets the severity-10 Msg
    /// 15469 / 15470 pair and its index set.
    /// </para>
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelp(BatchContext batch, string procedureName)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, _) = ParseHelpArgs(arguments, "sp_help");
        var database = batch.CurrentDatabase;
        if (objectName is null)
        {
            yield return HelpObjectListResultSet(database);
            yield return HelpBlankLine(batch, procedureName, 35);
            yield return new SimulatedSqlResultSet(
                SpHelpTypeSchema, SpHelpUserTypeColumnNames, HelpUserTypes(database));
            yield break;
        }

        // Objects win over types on a name collision (real looks in
        // sys.all_objects first and only falls back to type_id).
        if (!TryResolveHelpTargetForHelp(batch, objectName, out var target))
        {
            var parsedName = ParseHelpObjectName(database, objectName);
            if (batch.TryResolveCatalogView(parsedName, out var catalogView, out _))
            {
                foreach (var outcome in HelpCatalogView(batch, catalogView, parsedName, objectName, procedureName))
                    yield return outcome;
                yield break;
            }

            var typeRows = HelpNamedUserType(batch, objectName) is { Count: > 0 } found
                ? found
                : throw SimulatedSqlException.HelpObjectDoesNotExist(objectName, database.Name);
            yield return new SimulatedSqlResultSet(SpHelpTypeSchema, SpHelpTypeColumnNames, typeRows);
            yield break;
        }

        List<SqlValue[]> objectInfo =
        [
            [
                SqlValue.FromSystemName(target.Name),
                SqlValue.FromSystemName(HelpOwnerName(target.Schema, target.Object ?? target.Table)),
                SqlValue.FromString(HelpObjectTypeType, HelpObjectTypeText(target.TypeCode)),
                SqlValue.FromDateTime(target.CreateDate),
            ],
        ];
        yield return new SimulatedSqlResultSet(SpHelpObjectSchema, SpHelpObjectColumnNames, objectInfo) { ColumnNullability = SpHelpObjectNullability };
        // Each section follows a blank line real prints at a fixed line of
        // its own source; see HelpMessage.
        yield return HelpBlankLine(batch, procedureName, 122);

        if (target.Columns is { Length: > 0 } columns)
        {
            yield return HelpBlankLine(batch, procedureName, 135);
            yield return HelpColumnResultSet(database, columns);

            // The identity / rowguidcol pair is emitted for tables, views and
            // multi-statement table-valued functions — but not for an inline
            // table-valued function (real gates on type in ('S ','U ','V ','TF')).
            if (target.Object is HeapTable or View or MultiStatementTableValuedFunction)
            {
                yield return HelpBlankLine(batch, procedureName, 164);
                yield return HelpIdentityResultSet(columns);
                yield return HelpBlankLine(batch, procedureName, 173);
                yield return HelpRowGuidColResultSet(columns);
            }
        }

        if (HelpParameterRows(batch, target) is { Count: > 0 } parameters)
        {
            yield return HelpBlankLine(batch, procedureName, 184);
            yield return new SimulatedSqlResultSet(SpHelpParameterSchema, SpHelpParameterColumnNames, parameters) { ColumnNullability = SpHelpParameterNullability };
        }

        if (target.Object is HeapTable helpTable)
        {
            // A memory-optimized table sits on no filegroup (probed 2026-10-02
            // against SQL Server 2025).
            List<SqlValue[]> filegroup = [[SqlValue.FromSystemName(helpTable.IsMemoryOptimized ? "not applicable" : helpTable.Partitioning?.Scheme.Name ?? FilegroupName(DatabaseOf(batch, helpTable), helpTable.FilegroupId))]];
            yield return HelpBlankLine(batch, procedureName, 202);
            yield return new SimulatedSqlResultSet(SingleSystemNameColumn, SpHelpFilegroupColumnNames, filegroup) { ColumnNullability = SingleNotNullColumn };
            yield return HelpBlankLine(batch, procedureName, 204);
            foreach (var outcome in HelpIndexResultSets(batch, target, objectName, "sys.sp_helpindex"))
                yield return outcome;
            yield return HelpBlankLine(batch, procedureName, 206);
            foreach (var outcome in HelpConstraintResultSets(batch, target, objectName, "sys.sp_helpconstraint"))
                yield return outcome;
            foreach (var outcome in HelpReferencingViewResultSets(batch, target, objectName, procedureName))
                yield return outcome;
        }
        else if (target.Object is View)
        {
            foreach (var outcome in HelpViewTail(batch, target, objectName, procedureName))
                yield return outcome;
        }
    }

    /// <summary>
    /// What follows a view's rowguidcol set: the "no constraints" / "no
    /// foreign keys" pair real prints for any view itself, then the view's
    /// indexes through <c>sys.sp_helpindex</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> HelpViewTail(BatchContext batch, HelpTarget? target, string objectName, string procedureName)
    {
        yield return HelpBlankLine(batch, procedureName, 223);
        yield return HelpNoConstraints(batch, procedureName, 224, objectName);
        yield return HelpBlankLine(batch, procedureName, 225);
        yield return HelpNoReferencingForeignKeys(batch, procedureName, 226, objectName);
        if (target is null)
        {
            yield return Printed(SimulatedSqlException.NoIndexesMessage(batch, "sys.sp_helpindex", objectName));
            yield break;
        }
        foreach (var outcome in HelpIndexResultSets(batch, target, objectName, "sys.sp_helpindex"))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_help</c> over a catalog view, which real describes as a view
    /// owned by its schema — its columns, the identity / rowguidcol pair, and
    /// the no-constraints, no-foreign-keys and no-indexes messages (probed
    /// 2026-09-25 against SQL Server 2025 with <c>sys.objects</c>).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> HelpCatalogView(
        BatchContext batch, CatalogView view, MultiPartName name, string objectName, string procedureName)
    {
        var owner = name.ImmediateQualifier switch
        {
            { } qualifier when BuiltInToken.Equals(qualifier, "INFORMATION_SCHEMA") => "INFORMATION_SCHEMA",
            { } qualifier when BuiltInToken.Equals(qualifier, Database.DefaultSchemaName) => Database.DefaultSchemaName,
            _ => "sys",
        };
        List<SqlValue[]> objectInfo =
        [
            [
                SqlValue.FromSystemName(view.Name),
                SqlValue.FromSystemName(owner),
                SqlValue.FromString(HelpObjectTypeType, "view"),
                SqlValue.FromDateTime(BuiltInResources.SystemObjectDate),
            ],
        ];
        yield return new SimulatedSqlResultSet(SpHelpObjectSchema, SpHelpObjectColumnNames, objectInfo) { ColumnNullability = SpHelpObjectNullability };
        yield return HelpBlankLine(batch, procedureName, 122);
        yield return HelpBlankLine(batch, procedureName, 135);
        yield return HelpColumnResultSet(batch.CurrentDatabase, view.Columns);
        yield return HelpBlankLine(batch, procedureName, 164);
        yield return HelpIdentityResultSet(view.Columns);
        yield return HelpBlankLine(batch, procedureName, 173);
        yield return HelpRowGuidColResultSet(view.Columns);
        foreach (var outcome in HelpViewTail(batch, null, objectName, procedureName))
            yield return outcome;
    }

    // sp_help's own resolution, which must not raise when the name is a type
    // rather than an object — the caller falls back to the type lookup.
    private static bool TryResolveHelpTargetForHelp(BatchContext batch, string objectName, out HelpTarget target)
    {
        target = null!;
        var parsed = ParseHelpObjectName(batch.CurrentDatabase, objectName);
        return parsed.Count is >= 1 and <= 3 && TryResolveHelpTarget(batch, parsed, out target);
    }

    // The name of the object's effective owner — its explicit one, else its
    // schema's (probed 2026-09-27: a table in a u1-owned schema reports u1);
    // a constraint answers for its table.
    private static string HelpOwnerName(Schema schema, SchemaObject? obj)
    {
        var ownerId = obj is null ? schema.PrincipalId : Ownership.EffectiveOwnerId(schema.Database, obj);
        return Ownership.PrincipalName(schema.Database, ownerId) ?? schema.Name;
    }

    private static SimulatedSqlResultSet HelpObjectListResultSet(Database database)
    {
        var rows = new List<SqlValue[]>();
        foreach (var (_, schema) in database.Schemas)
        {
            void Add(string name, string typeCode, SchemaObject owned) => rows.Add([
                SqlValue.FromSystemName(name),
                SqlValue.FromSystemName(HelpOwnerName(schema, owned)),
                SqlValue.FromString(HelpObjectTypeType, HelpObjectTypeText(typeCode)),
            ]);

            foreach (var obj in schema.SchemaObjects())
                Add(obj.Name, obj.ObjectTypeCode, obj);
            foreach (var constraint in HelpConstraintObjects(schema))
                Add(constraint.Name, constraint.TypeCode, constraint.Table);
        }

        // Real's order: owner ascending, object type DESCENDING, name ascending
        // — read straight off the projected cells (name, owner, type).
        rows.Sort(static (a, b) =>
        {
            var cmp = string.Compare(a[1].AsString, b[1].AsString, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0)
                return cmp;
            cmp = string.Compare(b[2].AsString, a[2].AsString, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a[0].AsString, b[0].AsString, StringComparison.OrdinalIgnoreCase);
        });
        return new SimulatedSqlResultSet(SpHelpObjectListSchema, SpHelpObjectListColumnNames, rows);
    }

    private static SimulatedSqlResultSet HelpColumnResultSet(Database database, HeapColumn[] columns)
    {
        var blank = SqlValue.FromString(HelpPrecScaleType, "     ");
        var notApplicable = SqlValue.FromString(HelpFlagType, "(n/a)");
        var rows = new List<SqlValue[]>(columns.Length);
        foreach (var column in columns)
        {
            var type = column.Type;
            var (maxLength, precision, scale) = HelpTypeGeometry(type, column.MaxLength);
            var showsPrecScale = HelpShowsPrecisionAndScale(type);
            // ANSI_PADDING is always on, so TrimTrailingBlanks reports 'no' for
            // the padded types and '(n/a)' where the property is undefined.
            // A vector answers as the varbinary it shares a system type id with.
            var padded = type is CharSqlType or VarcharSqlType or BinarySqlType
                or VarbinarySqlType or SqlVariantSqlType or VectorSqlType;
            // The CLR types answer it, the built-in three included (probed
            // 2026-10-02 against SQL Server 2025).
            var fixedLenNullInSource = type is CharSqlType or VarcharSqlType
                or BinarySqlType or VarbinarySqlType or VectorSqlType or ClrUdtSqlType or HierarchyIdSqlType or SpatialSqlType;
            rows.Add([
                SqlValue.FromSystemName(column.Name),
                SqlValue.FromSystemName(column.AliasType?.Name ?? (column.SpelledNumeric ? column.TypeName : HelpTypeName(type))),
                HelpFlag(column.Computed is not null),
                SqlValue.FromInt32(maxLength),
                showsPrecScale ? HelpPrecScaleCell(precision) : blank,
                showsPrecScale ? HelpPrecScaleCell(scale) : blank,
                HelpFlag(column.Nullable),
                padded ? HelpFlag(false) : notApplicable,
                fixedLenNullInSource ? HelpFlag(column.Nullable) : notApplicable,
                HelpColumnCollation(database, column),
            ]);
        }

        return new SimulatedSqlResultSet(SpHelpColumnSchema, SpHelpColumnColumnNames, rows);
    }

    // Real reports the identity column's seed / increment / not-for-replication
    // flag, or a single placeholder row naming the absence. A view names the
    // identity column it passes through (probed 2026-09-26).
    private static SimulatedSqlResultSet HelpIdentityResultSet(HeapColumn[] columns)
    {
        var column = Array.Find(columns, c => (c.Identity ?? c.IdentitySource) is not null);
        var identity = column?.Identity ?? column?.IdentitySource;
        List<SqlValue[]> rows =
        [
            [
                SqlValue.FromSystemName(column?.Name ?? "No identity column defined."),
                identity is null ? SqlValue.Null(HelpIdentityValueType)
                    : SqlValue.FromDecimal(HelpIdentityValueType, IdentityState.ToDecimal38(identity.Seed)),
                identity is null ? SqlValue.Null(HelpIdentityValueType)
                    : SqlValue.FromDecimal(HelpIdentityValueType, IdentityState.ToDecimal38(identity.Increment)),
                identity is null ? SqlValue.Null(SqlType.Int32)
                    : SqlValue.FromInt32(identity.NotForReplication ? 1 : 0),
            ],
        ];
        return new SimulatedSqlResultSet(SpHelpIdentitySchema, SpHelpIdentityColumnNames, rows) { ColumnReportsNumeric = SpHelpIdentityReportsNumeric, ColumnNullability = SpHelpIdentityNullability };
    }

    private static SimulatedSqlResultSet HelpRowGuidColResultSet(HeapColumn[] columns)
    {
        var name = Array.Find(columns, c => c.IsRowGuidCol)?.Name ?? "No rowguidcol column defined.";
        List<SqlValue[]> rows = [[SqlValue.FromSystemName(name)]];
        return new SimulatedSqlResultSet(SingleSystemNameColumn, SpHelpRowGuidColColumnNames, rows) { ColumnNullability = SingleNotNullColumn };
    }

    // Procedure / function parameters in declaration order.
    private static List<SqlValue[]> HelpParameterRows(BatchContext batch, HelpTarget target)
    {
        var rows = new List<SqlValue[]>();
        var serverCollation = batch.Connection.Simulation.ServerCollationName;

        void Add(string name, SqlType type, int? declaredMaxLength, int order, AliasType? alias)
        {
            var (maxLength, precision, scale) = HelpTypeGeometry(type, declaredMaxLength);
            // A MAX or xml parameter or return value reads Prec 0 where a
            // column reads its LOB width (probed 2026-10-02 against SQL Server
            // 2025).
            if (maxLength == -1 && type is VarcharSqlType or NVarcharSqlType or VarbinarySqlType or XmlSqlType)
                precision = 0;
            rows.Add([
                SqlValue.FromSystemName(name),
                SqlValue.FromSystemName(alias?.Name ?? HelpTypeName(type)),
                SqlValue.FromInt16((short)maxLength),
                SqlValue.FromInt32(precision),
                scale is { } s ? SqlValue.FromInt32(s) : SqlValue.Null(SqlType.Int32),
                SqlValue.FromInt32(order),
                type.SystemTypeId is 35 or 99 or 167 or 175 or 231 or 239
                    ? SqlValue.FromSystemName(serverCollation)
                    : SqlValue.Null(SqlType.SystemName),
            ]);
        }

        switch (target.Object)
        {
            case Procedure procedure:
                for (var i = 0; i < procedure.Parameters.Length; i++)
                {
                    var parameter = procedure.Parameters[i];
                    Add("@" + parameter.Name, parameter.Type, parameter.DeclaredMaxLength, i + 1, parameter.AliasType);
                }

                break;
            case UserDefinedFunction function:
                // A scalar function leads with its return value: an
                // empty-named row at Param_order 0 (probe-confirmed).
                if (function is ScalarFunction scalar)
                    Add("", scalar.ReturnType, null, 0, scalar.ReturnAliasType);
                else if (function is ClrScalarFunction clrScalar)
                    Add("", clrScalar.ReturnType, null, 0, null);
                else if (function is ClrAggregateFunction aggregate)
                    Add("", aggregate.ReturnType, null, 0, null);
                for (var i = 0; i < function.Parameters.Length; i++)
                    Add("@" + function.Parameters[i].Name, function.Parameters[i].Type, null, i + 1, function.Parameters[i].AliasType);
                break;
        }

        return rows;
    }

    private static IEnumerable<SimulatedStatementOutcome> HelpReferencingViewResultSets(
        BatchContext batch, HelpTarget target, string objectName, string procedureName)
    {
        var rows = new List<SqlValue[]>();
        foreach (var view in SchemaBinding.ReferencingViews(batch.CurrentDatabase, target.Table!))
            rows.Add([SqlValue.FromSystemName(view.Name)]);

        if (rows.Count == 0)
        {
            yield return Printed(SimulatedSqlException.NoReferencingViewsMessage(batch, procedureName, objectName));
            yield break;
        }

        rows.Sort(ByFirstCell);
        yield return new SimulatedSqlResultSet(SingleSystemNameColumn, SpHelpReferencingViewColumnNames, rows) { ColumnNullability = SingleNotNullColumn };
    }

    // Every user-defined type in the database, alias types and table types
    // alike, ordered by name — real's `where user_type_id > 256` set.
    private static List<SqlValue[]> HelpUserTypes(Database database)
    {
        var rows = new List<SqlValue[]>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, alias) in schema.AliasTypes)
                rows.Add(HelpAliasTypeRow(database, alias));
            foreach (var (_, tableType) in schema.TableTypes)
                rows.Add(HelpTableTypeRow(tableType));
        }

        rows.Sort(ByFirstCell);
        return rows;
    }

    // Real falls back to type_id() when @objname isn't an object. Both lookups
    // route through the BatchContext resolvers so a table type takes the same
    // schema-stability lock every other read of it does.
    private static List<SqlValue[]> HelpNamedUserType(BatchContext batch, string objectName)
    {
        var parsed = ParseHelpObjectName(batch.CurrentDatabase, objectName);
        var rows = new List<SqlValue[]>();
        if (parsed.Count is < 1 or > 3)
            return rows;
        if (batch.TryResolveAliasType(parsed, out var alias))
            rows.Add(HelpAliasTypeRow(batch.CurrentDatabase, alias));
        else if (batch.TryResolveTableType(parsed, out var tableType))
            rows.Add(HelpTableTypeRow(tableType));
        else if (HelpSystemTypeRow(batch.CurrentDatabase, parsed) is { } system)
            rows.Add(system);
        return rows;
    }

    /// <summary>
    /// A system type as <c>sp_help</c> lists it: <c>Type_name</c>, its storage type (null for a CLR type),
    /// then the length, precision and scale cells, whether it is nullable and whether it carries a collation
    /// (probed 2026-09-30 against SQL Server 2025). A one-part name or one under <c>sys</c> reaches it.
    /// </summary>
    private static readonly (string Name, string? Storage, short Length, int Precision, int? Scale, bool Nullable, bool Collated)[] SystemHelpTypes =
    [
        ("bigint", "bigint", 8, 19, 0, true, false),
        ("binary", "binary", 8000, 8000, null, true, false),
        ("bit", "bit", 1, 1, null, true, false),
        ("char", "char", 8000, 8000, null, true, true),
        ("date", "date", 3, 10, 0, true, false),
        ("datetime", "datetime", 8, 23, 3, true, false),
        ("datetime2", "datetime2", 8, 27, 7, true, false),
        ("datetimeoffset", "datetimeoffset", 10, 34, 7, true, false),
        ("decimal", "decimal", 17, 38, 38, true, false),
        ("float", "float", 8, 53, null, true, false),
        ("geography", null, -1, -1, null, true, false),
        ("geometry", null, -1, -1, null, true, false),
        ("hierarchyid", null, 892, 892, null, true, false),
        ("image", "image", 16, int.MaxValue, null, true, false),
        ("int", "int", 4, 10, 0, true, false),
        ("json", "json", -1, -1, null, true, false),
        ("money", "money", 8, 19, 4, true, false),
        ("nchar", "nchar", 8000, 4000, null, true, true),
        ("ntext", "ntext", 16, 1073741823, null, true, true),
        ("numeric", "numeric", 17, 38, 38, true, false),
        ("nvarchar", "nvarchar", 8000, 4000, null, true, true),
        ("real", "real", 4, 24, null, true, false),
        ("smalldatetime", "smalldatetime", 4, 16, 0, true, false),
        ("smallint", "smallint", 2, 5, 0, true, false),
        ("smallmoney", "smallmoney", 4, 10, 4, true, false),
        ("sql_variant", "sql_variant", 8016, 0, null, true, false),
        ("sysname", "nvarchar", 256, 128, null, false, true),
        ("text", "text", 16, int.MaxValue, null, true, true),
        ("time", "time", 5, 16, 7, true, false),
        ("timestamp", "timestamp", 8, 8, null, false, false),
        ("tinyint", "tinyint", 1, 3, 0, true, false),
        ("uniqueidentifier", "uniqueidentifier", 16, 16, null, true, false),
        ("varbinary", "varbinary", 8000, 8000, null, true, false),
        ("varchar", "varchar", 8000, 8000, null, true, true),
        ("xml", "xml", -1, -1, null, true, false),
    ];

    private static SqlValue[]? HelpSystemTypeRow(Database database, MultiPartName name)
    {
        if (name.Count > 2 || (name.Count == 2 && !BuiltInToken.Equals(name[name.Count - 2], "sys")))
            return null;
        foreach (var (typeName, storage, length, precision, scale, nullable, collated) in SystemHelpTypes)
        {
            if (!typeName.Equals(name.Leaf, StringComparison.OrdinalIgnoreCase))
                continue;
            return
            [
                SqlValue.FromSystemName(typeName),
                storage is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(storage),
                SqlValue.FromInt16(length),
                SqlValue.FromInt32(precision),
                scale is { } s ? SqlValue.FromInt32(s) : SqlValue.Null(SqlType.Int32),
                HelpFlag(nullable),
                SqlValue.FromSystemName("none"),
                SqlValue.FromSystemName("none"),
                collated ? SqlValue.FromSystemName(database.CollationName) : SqlValue.Null(SqlType.SystemName),
            ];
        }
        return null;
    }

    private static SqlValue[] HelpAliasTypeRow(Database database, AliasType alias)
    {
        var (maxLength, precision, scale) = HelpTypeGeometry(alias.UnderlyingType, alias.DeclaredMaxLength);
        return
        [
            SqlValue.FromSystemName(alias.Name),
            // A CLR type has no storage type to name (probed 2026-09-28).
            alias.UnderlyingType is ClrUdtSqlType
                ? SqlValue.Null(SqlType.SystemName)
                : SqlValue.FromSystemName(alias.SpelledNumeric ? "numeric" : HelpTypeName(alias.UnderlyingType)),
            SqlValue.FromInt16((short)maxLength),
            SqlValue.FromInt32(precision),
            scale is { } s ? SqlValue.FromInt32(s) : SqlValue.Null(SqlType.Int32),
            HelpFlag(alias.IsNullable),
            SqlValue.FromSystemName("none"),
            SqlValue.FromSystemName("none"),
            alias.UnderlyingType.Category == SqlTypeCategory.String
                ? SqlValue.FromSystemName(database.CollationName)
                : SqlValue.Null(SqlType.SystemName),
        ];
    }

    private static SqlValue[] HelpTableTypeRow(TableType tableType) =>
    [
        SqlValue.FromSystemName(tableType.Name),
        SqlValue.FromSystemName("table type"),
        SqlValue.FromInt16(-1),
        SqlValue.FromInt32(0),
        SqlValue.Null(SqlType.Int32),
        HelpFlag(false),
        SqlValue.FromSystemName("none"),
        SqlValue.FromSystemName("none"),
        SqlValue.Null(SqlType.SystemName),
    ];

    private static SqlValue HelpFlag(bool value) =>
        SqlValue.FromString(HelpFlagType, value ? "yes" : "no");

    private static SqlValue HelpPrecScaleCell(int? value) => value is { } v
        ? SqlValue.FromString(HelpPrecScaleType, v.ToString(CultureInfo.InvariantCulture).PadRight(5))
        : SqlValue.Null(HelpPrecScaleType);

    // xml and the spatial pair carry no collation here (probed 2026-10-02
    // against SQL Server 2025).
    private static SqlValue HelpColumnCollation(Database database, HeapColumn column) =>
        column.Type.Category != SqlTypeCategory.String || column.Type is XmlSqlType or SpatialSqlType ? SqlValue.Null(SqlType.SystemName)
        : SqlValue.FromSystemName(column.Collation
            ?? (column.Type is SystemNameSqlType ? null : column.Type.Collation?.Name)
            ?? database.CollationName);

    // The types whose Prec / Scale cells real renders — matched by name against
    // its @precscaletypes list. Everything else gets five blanks.
    private static bool HelpShowsPrecisionAndScale(SqlType type) =>
        type == SqlType.TinyInt || type == SqlType.SmallInt || type == SqlType.Int32
        || type == SqlType.BigInt || type == SqlType.Real || type == SqlType.Float
        || type == SqlType.Money || type == SqlType.SmallMoney || type == SqlType.Date
        || type is DecimalSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType;

    /// <summary>
    /// The <c>(max_length, ODBC precision, ODBC scale)</c> triple sp_help
    /// reports. <c>max_length</c> is the <c>sys.columns</c> byte width;
    /// precision is the ODBC display width (character count for strings, digit
    /// count for numerics, rendered length for date/time — so a scaled
    /// date/time type adds one for its decimal point); scale is NULL wherever
    /// real's <c>OdbcScale</c> is (approximate / string / binary types).
    /// </summary>
    private static (int MaxLength, int Precision, int? Scale) HelpTypeGeometry(SqlType type, int? declaredMaxLength) =>
        type switch
        {
            _ when type == SqlType.Bit => (1, 1, null),
            _ when type == SqlType.TinyInt => (1, 3, 0),
            _ when type == SqlType.SmallInt => (2, 5, 0),
            _ when type == SqlType.Int32 => (4, 10, 0),
            _ when type == SqlType.BigInt => (8, 19, 0),
            _ when type == SqlType.Money => (8, 19, 4),
            _ when type == SqlType.SmallMoney => (4, 10, 4),
            _ when type == SqlType.Float => (8, 53, null),
            _ when type == SqlType.Real => (4, 24, null),
            _ when type == SqlType.Date => (3, 10, 0),
            _ when type == SqlType.SmallDateTime => (4, 16, 0),
            _ when type == SqlType.DateTime => (8, 23, 3),
            // uniqueidentifier carries no precision in sys.columns /
            // sys.parameters, and sp_help never renders one for it.
            _ when type == SqlType.UniqueIdentifier => (16, 0, null),
            _ when type == SqlType.RowVersion => (8, 8, null),
            _ when type == SqlType.Text => (16, 2147483647, null),
            _ when type == SqlType.NText => (16, 1073741823, null),
            _ when type == SqlType.Image => (16, 2147483647, null),
            _ when type == SqlType.SystemName => (256, 128, null),
            DecimalSqlType d => (d.FixedLength, d.precision, d.scale),
            TimeSqlType t => (t.FixedLength, t.precision == 0 ? 8 : 9 + t.precision, t.precision),
            DateTime2SqlType t => (t.FixedLength, t.precision == 0 ? 19 : 20 + t.precision, t.precision),
            DateTimeOffsetSqlType t => (t.FixedLength, t.precision == 0 ? 26 : 27 + t.precision, t.precision),
            CharSqlType c => (c.length, c.length, null),
            NCharSqlType n => (n.length * 2, n.length, null),
            BinarySqlType b => (b.length, b.length, null),
            VarcharSqlType v => HelpVariableGeometry(v.length, declaredMaxLength, 1, 2147483647),
            NVarcharSqlType n => HelpVariableGeometry(n.length, declaredMaxLength, 2, 1073741823),
            VarbinarySqlType v => HelpVariableGeometry(v.length, declaredMaxLength, 1, 2147483647),
            SqlVariantSqlType => (8016, 0, null),
            XmlSqlType => (-1, -1, null),
            // A json parameter's Prec is 0 (probed 2026-09-26).
            JsonSqlType => (-1, 0, null),
            HierarchyIdSqlType => (892, 892, null),
            SpatialSqlType => (-1, -1, null),
            // A CLR type's Prec is its MaxByteSize too (probed 2026-09-28).
            ClrUdtSqlType udt => (udt.Udt.MaxByteSize, udt.Udt.MaxByteSize, null),
            VectorSqlType vector => (vector.ByteLength, vector.ByteLength, null),
            _ => (0, 0, null),
        };

    // The MAX form reports max_length -1 and the LOB display width; a declared
    // (N) reports N characters and N * bytesPerChar bytes. Length 0 is the
    // "carried on the column / parameter" sentinel the row encoder uses.
    private static (int MaxLength, int Precision, int? Scale) HelpVariableGeometry(
        int typeLength, int? declaredMaxLength, int bytesPerChar, int maxPrecision)
    {
        if (typeLength < 0)
            return (-1, maxPrecision, null);
        var length = typeLength == 0 ? declaredMaxLength ?? 1 : typeLength;
        return (length * bytesPerChar, length, null);
    }

    // sys.objects.type -> the spt_values 'O9T' display text sp_help prints.
    private static string HelpObjectTypeText(string typeCode) => typeCode switch
    {
        "AF" => "aggregate function",
        "C " => "check cns",
        "D " => "default (maybe cns)",
        "EC" => "edge cns",
        "F " => "foreign key cns",
        "FN" => "scalar function",
        "FS" => "assembly scalar function",
        "FT" => "assembly table function",
        "IF" => "inline function",
        "P " => "stored procedure",
        "PC" => " assembly stored procedure",
        "PK" => "primary key cns",
        "R " => "rule",
        "S " => "system table",
        "SN" => "synonym",
        "SO" => "sequence object",
        "TF" => "table function",
        "TR" => "trigger",
        "U " => "user table",
        "UQ" => "unique key cns",
        "V " => "view",
        _ => typeCode.TrimEnd(),
    };

    /// <summary>
    /// <c>type_name(user_type_id)</c> for a storage type, read out of the same
    /// <c>sys.types</c> row data the catalog view projects so the two can't
    /// drift; an alias-typed column or parameter reports its alias instead.
    /// </summary>
    private static string HelpTypeName(SqlType type) =>
        BuiltInResources.SystemTypeName(type.UserTypeId) ?? type.SqlServerName;
}
