using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// One scalar user-defined alias type (UDDT). Created via
/// <c>CREATE TYPE schema.name FROM &lt;builtin&gt;[(N[, S])] [NULL | NOT NULL]</c>,
/// dropped via <c>DROP TYPE [IF EXISTS] schema.name</c>. Lives in its owning
/// <see cref="Schema"/>'s <see cref="Schema.AliasTypes"/> dict; shares the
/// type-name namespace with <see cref="TableType"/> entries (Msg 219 on
/// cross-kind dup-type-name collision).
/// </summary>
/// <remarks>
/// <para>
/// At resolution time the alias expands to its <see cref="UnderlyingType"/>;
/// the <see cref="DeclaredMaxLength"/> applies to the underlying for
/// length-bearing types (varchar / nvarchar / varbinary / char / nchar /
/// binary / datetime2 / time / datetimeoffset / decimal / numeric).
/// <see cref="IsNullable"/> carries the nullability marker declared on the
/// alias itself (bare and explicit <c>NULL</c> are both nullable;
/// <c>NOT NULL</c> is not). When a column / variable references an alias
/// without an explicit nullability marker of its own, this default
/// propagates — matches probe behavior against SQL Server 2025.
/// </para>
/// <para>
/// Restrictions enforced at usage time (probe-confirmed against SQL Server
/// 2025): a length parameter at the usage site raises Msg 2716 verbatim
/// (<c>"Cannot specify a column width on data type X."</c>).
/// </para>
/// </remarks>
internal sealed class AliasType(
    Schema schema,
    string name,
    SqlType underlyingType,
    int? declaredMaxLength,
    bool isNullable,
    int userTypeId,
    DateTime createDate,
    bool spelledNumeric)
{
    public Schema Schema = schema;

    /// <summary>
    /// The type's explicit owner — <c>sys.types.principal_id</c> — set by
    /// <c>ALTER AUTHORIZATION ON TYPE::</c>; null means the owning schema's
    /// owner, which is also what <c>TO SCHEMA OWNER</c> restores.
    /// </summary>
    public int? OwnerPrincipalId;

    public readonly string Name = name;

    /// <summary>
    /// The resolved built-in type the alias wraps (e.g. <c>nvarchar(50)</c>'s
    /// underlying is the simulator's <c>NVarchar</c> singleton). The fully-
    /// resolved <c>SqlType</c> instance — the declared length from the
    /// CREATE TYPE source is captured in <see cref="DeclaredMaxLength"/>
    /// alongside since the singleton itself is dimension-agnostic for most
    /// variable-length types.
    /// </summary>
    public readonly SqlType UnderlyingType = underlyingType;

    /// <summary>
    /// Declared length in characters / bytes (for variable-length string /
    /// binary types) or the precision (for <c>datetime2</c> / <c>time</c> /
    /// <c>datetimeoffset</c> / <c>decimal</c> / <c>numeric</c>'s precision
    /// parameter). Null when the underlying is fixed-shape (e.g. <c>int</c>,
    /// <c>bit</c>).
    /// </summary>
    public readonly int? DeclaredMaxLength = declaredMaxLength;

    /// <summary>
    /// Nullability marker from the CREATE TYPE declaration. Bare <c>CREATE
    /// TYPE T FROM int</c> and explicit <c>NULL</c> both set this to true;
    /// <c>NOT NULL</c> sets false. Used to default a column / variable's
    /// nullability when the consumer has no explicit marker — see the
    /// nullability-inheritance contract above.
    /// </summary>
    public readonly bool IsNullable = isNullable;

    /// <summary>
    /// Per-database <c>user_type_id</c> allocated via
    /// <see cref="Database.AllocateUserTypeId"/>. Surfaces in
    /// <c>sys.types.user_type_id</c> and <c>sys.columns.user_type_id</c>
    /// (for columns whose declared type is this alias).
    /// </summary>
    public readonly int UserTypeId = userTypeId;

    public readonly DateTime CreateDate = createDate;

    /// <summary>Declared over <c>numeric</c> rather than <c>decimal</c>; see <see cref="HeapColumn.SpelledNumeric"/>.</summary>
    public readonly bool SpelledNumeric = spelledNumeric;

    /// <summary>
    /// The <c>CREATE DEFAULT</c> object <c>sp_bindefault</c> bound to this type,
    /// which a column declared with the type afterwards takes as its own.
    /// </summary>
    public DefaultObject? BoundDefault;

    /// <summary>
    /// The <c>CREATE RULE</c> object <c>sp_bindrule</c> bound to this type,
    /// which a column declared with the type afterwards takes as its own.
    /// </summary>
    public RuleObject? BoundRule;

    /// <summary>The base type's <c>system_type_id</c>, <c>numeric</c>'s where the alias spelled it.</summary>
    public byte SystemTypeId => this.SpelledNumeric && this.UnderlyingType is DecimalSqlType ? HeapColumn.NumericTypeId : this.UnderlyingType.SystemTypeId;
}
