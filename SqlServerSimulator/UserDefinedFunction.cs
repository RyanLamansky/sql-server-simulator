using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

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
    /// Raw source text of the body. For scalars: the text between the outer
    /// <c>BEGIN</c> and <c>END</c> (exclusive of both). For inline TVFs: the
    /// SELECT-statement text between <c>AS RETURN [(</c> and the trailing
    /// <c>)]</c> (parens optional in source). Re-tokenized and re-parsed per
    /// call.
    /// </summary>
    public readonly string BodyText = bodyText;
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
    /// True when the function was declared with
    /// <c>WITH RETURNS NULL ON NULL INPUT</c>. At call time, if any argument
    /// is NULL the body is skipped entirely and the function returns
    /// <see cref="SqlValue.Null"/> of <see cref="ReturnType"/>.
    /// </summary>
    public readonly bool ReturnsNullOnNullInput = returnsNullOnNullInput;
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
}

/// <summary>
/// One declared parameter on a <see cref="UserDefinedFunction"/>. The
/// <see cref="Name"/> is stored with the leading <c>@</c> stripped (matching
/// the <see cref="BatchContext.Variables"/> keying convention).
/// </summary>
internal sealed class UdfParameter(string name, SqlType type, Expression? defaultExpression)
{
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
