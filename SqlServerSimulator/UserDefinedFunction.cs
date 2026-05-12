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
{
    public readonly Schema Schema = schema;
    public readonly string Name = name;
    public readonly int ObjectId = objectId;

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

    public readonly DateTime CreateDate = createDate;
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
