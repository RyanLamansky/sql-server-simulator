using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A user-defined function — either a <see cref="ScalarFunction"/>
/// (<c>RETURNS &lt;type&gt; AS BEGIN ... END</c>, called as a scalar
/// expression) or an <see cref="InlineTableValuedFunction"/>
/// (<c>RETURNS TABLE AS RETURN (SELECT ...)</c>, called from a FROM clause).
/// Both live in their owning <see cref="Schema"/>'s
/// <see cref="Schema.Functions"/> dict and share the
/// schema-qualified-name resolution rule: bare <c>fn(x)</c> raises Msg 195
/// (scalar) or Msg 208 (TVF — looks like a missing table), matching real
/// SQL Server's routing.
/// </summary>
internal abstract class UserDefinedFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    string bodyText,
    DateTime createDate)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    /// <summary>
    /// Declared parameters in source order. Each parameter has a name (with
    /// the leading <c>@</c> stripped), a declared <see cref="SqlType"/>, and
    /// an optional default expression that takes effect when the caller passes
    /// the <c>DEFAULT</c> keyword (probe-confirmed: bare omission raises
    /// Msg 313 — the <c>DEFAULT</c> keyword is required at the call site).
    /// </summary>
    public readonly UdfParameter[] Parameters = parameters;

    /// <summary>
    /// The module's <c>WITH EXECUTE AS { CALLER | SELF | OWNER | 'user' }</c>
    /// clause, or <see langword="null"/> for the default (CALLER). Captured at
    /// CREATE FUNCTION time and pushed/popped as an impersonation frame around
    /// the body at invocation — OWNER / SELF resolve to <c>dbo</c>, CALLER is a
    /// no-op, a named user pushes that database principal.
    /// </summary>
    public string? ExecuteAsClause;

    /// <summary>
    /// True when the function was declared <c>WITH SCHEMABINDING</c>.
    /// Surfaced through <c>sys.sql_modules.is_schema_bound</c> and
    /// <c>OBJECTPROPERTY(id, 'IsSchemaBound')</c>, and read by
    /// <see cref="ModuleDeterminism"/> — schema-binding is the precondition
    /// real SQL Server puts on <c>IsDeterministic</c>. Set, it also enrolls
    /// the body's references in the dependency gate: dropping or altering
    /// anything the body names is refused, and the body may only reference
    /// schema-bound modules (see <see cref="SchemaBinding"/>).
    /// </summary>
    public bool IsSchemaBound;

    /// <summary>
    /// Raw source text of the body. For scalars: the text between the outer
    /// <c>BEGIN</c> and <c>END</c> (exclusive of both). For inline TVFs: the
    /// SELECT-statement text between <c>AS RETURN [(</c> and the trailing
    /// <c>)]</c> (parens optional in source). Re-tokenized and re-parsed per
    /// call.
    /// </summary>
    public readonly string BodyText = bodyText;

    /// <summary>
    /// Newlines between the start of the batch that created the function and
    /// its body, which places a body line in that batch's text.
    /// </summary>
    public int BodyLineOffset;

    /// <summary>
    /// What an attempt to inline the body — or, for an inline table-valued
    /// function, to expand it — meets, settled by
    /// <c>Simulation.ScalarInliningFailures</c> and kept while
    /// <see cref="InliningFailuresSchemaVersion"/> is current; empty when the
    /// body inlines.
    /// </summary>
    public InliningFailure[] InliningFailures = [];

    /// <summary>The <c>Simulation.SchemaVersion</c> <see cref="InliningFailures"/> was settled at; -1 before the first.</summary>
    public long InliningFailuresSchemaVersion = -1;
}

/// <summary>
/// A scalar user-defined function. Body is a multi-statement
/// <c>BEGIN ... END</c> block that ends with <c>RETURN &lt;expr&gt;</c>;
/// per-call execution runs through <c>Simulation.InvokeScalarFunction</c>
/// and lands its value in <see cref="UdfFrame.ReturnedValue"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per-call execution allocates a fresh <see cref="BatchContext"/>; the
/// child batch's <see cref="BatchContext.Variables"/> are seeded from the
/// call's argument values, the <see cref="BatchContext.UdfFrame"/> is set
/// so value-form <c>RETURN &lt;expr&gt;</c> is legal, and the connection's
/// UDF recursion counter is incremented (Msg 217 when it would exceed 32,
/// matching probe-confirmed behavior).
/// </para>
/// </remarks>
internal sealed class ScalarFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    SqlType returnType,
    bool returnsNullOnNullInput,
    string bodyText,
    DateTime createDate)
    : UserDefinedFunction(schema, name, objectId, parameters, bodyText, createDate)
{
    public override string ObjectTypeCode => "FN";
    public override string ObjectTypeDescription => "SQL_SCALAR_FUNCTION";

    public readonly SqlType ReturnType = returnType;

    /// <summary>
    /// The declared width of a string or binary return type (1 when written
    /// without one), which the returned value is cut to; null for the other
    /// types.
    /// </summary>
    public int? ReturnMaxLength;

    /// <summary>The return type was written <c>numeric</c>, which a call reports (probed 2026-09-24).</summary>
    public bool ReturnSpelledNumeric;

    /// <summary>The user alias type the return type was written as; see <see cref="HeapColumn.AliasType"/>.</summary>
    public AliasType? ReturnAliasType;

    /// <summary>
    /// True when the function was declared with
    /// <c>WITH RETURNS NULL ON NULL INPUT</c>. At call time, if any argument
    /// is NULL the body is skipped entirely and the function returns
    /// <see cref="SqlValue.Null"/> of <see cref="ReturnType"/>.
    /// </summary>
    public readonly bool ReturnsNullOnNullInput = returnsNullOnNullInput;

    /// <summary>
    /// The masked columns the function's result reads, which a principal
    /// without <c>UNMASK</c> reads through <c>default()</c> of the return
    /// type; null when it reads none. Settled by
    /// <see cref="Simulation.ScalarFunctionReturnMask"/> and kept while
    /// <see cref="ReturnMaskSchemaVersion"/> is current.
    /// </summary>
    public Parser.DataMask? ReturnMask;

    /// <summary>The <see cref="Simulation.SchemaVersion"/> <see cref="ReturnMask"/> was settled at; -1 before the first.</summary>
    public long ReturnMaskSchemaVersion = -1;

    /// <summary>Whether the body is one the optimizer inlines (<see cref="ModuleInlining"/>), read on first use.</summary>
    public bool? BodyInlines;

    /// <summary>
    /// The <c>WITH INLINE = ON | OFF</c> setting; null when not written. OFF
    /// keeps an inlineable body from inlining, which <c>sys.sql_modules</c>
    /// reports as <c>inline_type</c> 0 beside <c>is_inlineable</c> 1 (probed
    /// 2026-10-01 against SQL Server 2025).
    /// </summary>
    public bool? InlineOption;

}

/// <summary>
/// A function whose body is code in a registered <see cref="SqlAssembly"/>
/// rather than T-SQL — the CLR scalar, table-valued and aggregate kinds. None
/// has a <c>sys.sql_modules</c> row; each has a <c>sys.assembly_modules</c>
/// one naming its <see cref="Entry"/>.
/// </summary>
/// <remarks>
/// The entry point is resolved once at CREATE time so the binding errors
/// (Msg 6505 / 6506 / 6550 / 6551 / 6552 and each kind's own) fire there
/// rather than at first call, matching real SQL Server.
/// </remarks>
internal abstract class ClrFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    ClrEntryPoint entry,
    DateTime createDate)
    : UserDefinedFunction(schema, name, objectId, parameters, "", createDate)
{
    public readonly ClrEntryPoint Entry = entry;

    /// <summary>
    /// Whether the method's <c>SqlFunction</c> attribute marks it
    /// <c>DataAccessKind.Read</c> and <c>SystemDataAccessKind.Read</c>; either
    /// lets it open the context connection, and the second alone reads only
    /// the catalog through it.
    /// </summary>
    public readonly (bool User, bool System) DataAccess = entry.Method is { } method ? Clr.ClrAttributes.DataAccess(method) : default;

    /// <summary>Whether the function may open the context connection.</summary>
    public bool ReadsData => this.DataAccess.User || this.DataAccess.System;

    /// <summary>
    /// The state of the Msg 6522 a throw reports: 1 for a function that reads
    /// data or takes a <c>max</c>-typed parameter, 2 otherwise (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </summary>
    public byte ThrowState => this.ReadsData || Array.Exists(this.Parameters, parameter => parameter.Type is NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { length: SqlType.MaxLengthSentinel })
        ? (byte)1
        : (byte)2;
}

/// <summary>
/// A CLR scalar function — <c>CREATE FUNCTION … RETURNS &lt;type&gt; AS
/// EXTERNAL NAME assembly.[Class].Method</c>. Surfaces in <c>sys.objects</c>
/// as type <c>FS</c>.
/// </summary>
internal sealed class ClrScalarFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    SqlType returnType,
    ClrEntryPoint entry,
    DateTime createDate)
    : ClrFunction(schema, name, objectId, parameters, entry, createDate)
{
    public override string ObjectTypeCode => "FS";
    public override string ObjectTypeDescription => "CLR_SCALAR_FUNCTION";

    public readonly SqlType ReturnType = returnType;
}

/// <summary>
/// A CLR table-valued function — <c>CREATE FUNCTION … RETURNS TABLE (cols)
/// [ORDER (…)] AS EXTERNAL NAME assembly.[Class].Method</c>, where the method
/// returns the rows as an <see cref="System.Collections.IEnumerable"/> (or an
/// <see cref="System.Collections.IEnumerator"/>) and the method its
/// <c>SqlFunction(FillRowMethodName = …)</c> names splits each row object into
/// the columns' <c>out</c> parameters. Called from a FROM clause like the
/// T-SQL kinds; surfaces in <c>sys.objects</c> as type <c>FT</c>.
/// </summary>
internal sealed class ClrTableValuedFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    HeapColumn[] outputColumns,
    ClrEntryPoint entry,
    System.Reflection.MethodInfo fillRow,
    DateTime createDate)
    : ClrFunction(schema, name, objectId, parameters, entry, createDate)
{
    public override string ObjectTypeCode => "FT";
    public override string ObjectTypeDescription => "CLR_TABLE_VALUED_FUNCTION";

    /// <summary>The declared result table's columns, all nullable.</summary>
    public readonly HeapColumn[] OutputColumns = outputColumns;

    /// <summary>The bound <c>FillRow</c> method: the row object, then one <c>out</c> parameter per column.</summary>
    public readonly System.Reflection.MethodInfo FillRow = fillRow;
}

/// <summary>
/// A CLR user-defined aggregate — <c>CREATE AGGREGATE name (@p type, …)
/// RETURNS type EXTERNAL NAME assembly.[Class]</c>. The class carries
/// <c>SqlUserDefinedAggregate</c> and the <c>Init</c> / <c>Accumulate</c> /
/// <c>Merge</c> / <c>Terminate</c> contract; one instance accumulates one
/// group. Called as a schema-qualified aggregate; surfaces in
/// <c>sys.objects</c> as type <c>AF</c>.
/// </summary>
internal sealed class ClrAggregateFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    SqlType returnType,
    ClrEntryPoint entry,
    System.Reflection.MethodInfo init,
    System.Reflection.MethodInfo accumulate,
    System.Reflection.MethodInfo terminate,
    DateTime createDate)
    : ClrFunction(schema, name, objectId, parameters, entry, createDate)
{
    public override string ObjectTypeCode => "AF";
    public override string ObjectTypeDescription => "AGGREGATE_FUNCTION";

    public readonly SqlType ReturnType = returnType;

    public readonly System.Reflection.MethodInfo Init = init;

    public readonly System.Reflection.MethodInfo Accumulate = accumulate;

    public readonly System.Reflection.MethodInfo Terminate = terminate;
}

/// <summary>
/// An inline table-valued function. Body is a single SELECT statement
/// whose projection determines the function's output schema. Called from a
/// FROM clause (<c>FROM schema.fn(args) [alias]</c>); the per-call execution
/// allocates a child <see cref="BatchContext"/> with parameters seeded as
/// variables, re-parses the body, and yields its rows directly to the join
/// driver.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="OutputColumns"/> array is derived once at
/// <c>CREATE FUNCTION</c> time by parsing the body under a temporary batch
/// with parameters declared as their typed variables. Real SQL Server
/// effectively schema-binds inline TVFs at CREATE time so the surface
/// matches; the simulator additionally enforces Msg 4514 (unnamed
/// projection column) and Msg 4506 (duplicate column name) at CREATE.
/// Nullability follows the same rules as <c>SELECT INTO</c>
/// (<see cref="Expression.ResultIsNullable"/>); identity is never
/// propagated (TVF output is a projection, not a heap).
/// </para>
/// </remarks>
internal sealed class InlineTableValuedFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    HeapColumn[] outputColumns,
    string bodyText,
    DateTime createDate)
    : UserDefinedFunction(schema, name, objectId, parameters, bodyText, createDate)
{
    public override string ObjectTypeCode => "IF";
    public override string ObjectTypeDescription => "SQL_INLINE_TABLE_VALUED_FUNCTION";

    /// <summary>
    /// One <see cref="HeapColumn"/> per projection column of the body's
    /// SELECT, derived at <c>CREATE FUNCTION</c> time. Column names come from
    /// the SELECT's aliases (or the underlying column name for direct refs);
    /// nullability follows <see cref="Expression.ResultIsNullable"/>;
    /// max-length / precision / scale follow the projection expression's
    /// <see cref="SqlType"/>. Identity is never set.
    /// </summary>
    public readonly HeapColumn[] OutputColumns = outputColumns;

    /// <summary>
    /// The body's per-column COLMETADATA flags (updatable 0x08, identity 0x10,
    /// computed 0x20), captured at CREATE, which a query reading the function
    /// reports for its columns.
    /// </summary>
    public byte[]? OutputWireFlags;
}

/// <summary>
/// A multi-statement table-valued function. Body is a multi-statement
/// <c>BEGIN ... END</c> block that writes into a declared return-table
/// variable (<c>RETURNS @r TABLE (cols)</c>) — bare <c>RETURN;</c> exits
/// the body and projects the accumulated <c>@r</c> rows to the caller.
/// Called from a FROM clause exactly like an inline TVF, but body
/// execution actually dispatches the body's statements (rather than
/// inlining a single SELECT).
/// </summary>
/// <remarks>
/// <para>
/// Per-call execution allocates a fresh <see cref="BatchContext"/> and
/// pre-seeds the parameters as variables AND the return-table variable
/// in <see cref="BatchContext.TableVariables"/>. Neither
/// <see cref="BatchContext.UdfFrame"/> nor
/// <see cref="BatchContext.ProcFrame"/> is set, so value-form
/// <c>RETURN N</c> naturally raises Msg 178 (probe-confirmed against
/// real SQL Server, which enforces this at CREATE time; the simulator
/// surfaces it at invoke time instead). Bare <c>RETURN;</c> sets
/// <see cref="BatchContext.ReturnSignaled"/> and the dispatch loop
/// bails — same path procedures use.
/// </para>
/// <para>
/// The output column schema is captured once at CREATE-FUNCTION time
/// (so <c>sys.columns</c> and FROM-source binding can resolve names
/// without re-parsing the body), and the
/// <see cref="KeyConstraints"/> / <see cref="CheckConstraints"/> arrays
/// hold the same constraint instances each per-call <see cref="HeapTable"/>
/// hands off to its constraint enforcer — sharing is safe because
/// constraint instances are immutable and the simulator runs
/// single-threaded per <see cref="Simulation"/>.
/// </para>
/// </remarks>
internal sealed class MultiStatementTableValuedFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    string returnVariableName,
    HeapColumn[] outputColumns,
    KeyConstraint[] keyConstraints,
    CheckConstraint[] checkConstraints,
    string bodyText,
    DateTime createDate)
    : UserDefinedFunction(schema, name, objectId, parameters, bodyText, createDate)
{
    public override string ObjectTypeCode => "TF";
    public override string ObjectTypeDescription => "SQL_TABLE_VALUED_FUNCTION";

    /// <summary>
    /// The declared <c>@</c>-stripped return-table variable name (the
    /// <c>r</c> in <c>RETURNS @r TABLE (...)</c>). Pre-seeded into the
    /// per-call child batch's <see cref="BatchContext.TableVariables"/>
    /// so the body's <c>INSERT INTO @r ...</c> / <c>SELECT FROM @r</c>
    /// route through the existing table-variable plumbing.
    /// </summary>
    public readonly string ReturnVariableName = returnVariableName;

    /// <summary>
    /// One <see cref="HeapColumn"/> per declared return-table column,
    /// parsed once at <c>CREATE FUNCTION</c> time. Mirrors
    /// <see cref="InlineTableValuedFunction.OutputColumns"/> in shape;
    /// the values are reused as-is when constructing each per-call
    /// <see cref="HeapTable"/> for <c>@r</c>.
    /// </summary>
    public readonly HeapColumn[] OutputColumns = outputColumns;

    /// <summary>
    /// Key (PRIMARY KEY / UNIQUE) constraints declared on the return
    /// table. Same instances shared across all per-call invocations —
    /// constraint state is immutable, the row-level uniqueness check
    /// reads ordinals + kind only.
    /// </summary>
    public readonly KeyConstraint[] KeyConstraints = keyConstraints;

    /// <summary>
    /// CHECK constraints declared on the return table. Same sharing
    /// rule as <see cref="KeyConstraints"/>.
    /// </summary>
    public readonly CheckConstraint[] CheckConstraints = checkConstraints;

    private HeapTable? catalogShape;

    /// <summary>
    /// The return table as the catalog views list it: real reports its
    /// PRIMARY KEY / UNIQUE / CHECK / DEFAULT constraints and their indexes
    /// under the function's own object id, as a table's (probed 2026-09-26
    /// against SQL Server 2025). Built once and never stored into; a call's
    /// <c>@r</c> is a table of its own.
    /// </summary>
    public HeapTable CatalogShape() => this.catalogShape ??= new HeapTable(
        this.Name, this.OutputColumns, this.ObjectId, this.SchemaId, this.CreateDate,
        this.KeyConstraints, this.CheckConstraints, isTableVariable: true);
}

/// <summary>
/// One declared parameter on a <see cref="UserDefinedFunction"/>. The
/// <see cref="Name"/> is stored with the leading <c>@</c> stripped (matching
/// the <see cref="BatchContext.Variables"/> keying convention).
/// </summary>
internal sealed class UdfParameter(string name, SqlType type, Expression? defaultExpression)
{
    /// <summary>Declared <c>numeric</c> rather than <c>decimal</c>; see <see cref="HeapColumn.SpelledNumeric"/>.</summary>
    public bool SpelledNumeric;

    /// <summary>The user alias type the parameter was declared with; see <see cref="HeapColumn.AliasType"/>.</summary>
    public AliasType? AliasType;

    /// <summary>The line of the parameter's name in the CREATE text, which a schema-bound alias-type refusal reports.</summary>
    public int LineNumber;

    /// <summary>
    /// The user-defined table type of a table-valued parameter (declared
    /// <c>READONLY</c>, Msg 352 otherwise); null for a scalar one. The body
    /// reads it as a read-only table variable, and <see cref="Type"/> is a
    /// placeholder <c>int</c>.
    /// </summary>
    public TableType? TableType;

    /// <summary>
    /// Whether <see cref="TableType"/> was written as a one-part name, which a
    /// schema-bound function refuses with Msg 2789.
    /// </summary>
    public bool TableTypeNamedOnePart;

    /// <summary>
    /// The declared width of a string or binary parameter (1 when written
    /// without one), which an argument is cut to as a variable assignment
    /// would; null for the other types.
    /// </summary>
    public int? DeclaredMaxLength;

    public readonly string Name = name;
    public readonly SqlType Type = type;

    /// <summary>
    /// The <c>= expr</c> default, parsed once at CREATE FUNCTION time and
    /// evaluated in the per-call <see cref="BatchContext"/> when the caller
    /// passes the <c>DEFAULT</c> keyword. <see langword="null"/> when no
    /// default was declared — calls must supply the argument or raise
    /// Msg 313 (probe-confirmed).
    /// </summary>
    public readonly Expression? Default = defaultExpression;
}
