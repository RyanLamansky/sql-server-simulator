using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// One user-defined stored procedure. Created via <c>CREATE [OR ALTER]
/// PROCEDURE schema.name [(@p type [=default] [OUTPUT], ...)] [WITH options]
/// AS [body]</c>, dropped via <c>DROP PROCEDURE</c>, invoked via
/// <c>EXEC schema.name ...</c> or <see cref="System.Data.CommandType.StoredProcedure"/>.
/// Lives in its owning <see cref="Schema"/>'s <see cref="Schema.Procedures"/>
/// dict; the name namespace is shared with tables / views / functions
/// (Msg 2714 on collision).
/// </summary>
/// <remarks>
/// <para>
/// Body source is captured at CREATE time between the <c>AS</c> keyword
/// (exclusive) and end-of-batch (or the trailing statement boundary), then
/// re-tokenized per call inside a fresh child <see cref="BatchContext"/>.
/// Unlike scalar UDFs, BEGIN/END is optional — real SQL Server accepts
/// <c>CREATE PROCEDURE p AS SELECT 1</c> and the multi-statement form
/// without an outer block (probe-confirmed against SQL Server 2025).
/// </para>
/// <para>
/// Per-call execution allocates a child <see cref="BatchContext"/> via the
/// procedure-body constructor: parameters pre-seed <c>Variables</c>; a
/// <see cref="ProcFrame"/> gates value-form <c>RETURN N</c> and
/// captures the return code; result sets from <c>SELECT</c> statements in
/// the body propagate to the outer caller's iterator (distinct from scalar
/// UDF bodies, which discard).
/// </para>
/// </remarks>
internal sealed class Procedure(
    Schema schema,
    string name,
    int objectId,
    ProcedureParameter[] parameters,
    string bodyText,
    DateTime createDate,
    int bodyLineOffset = 0)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    /// <summary>
    /// Number of newlines in the <c>CREATE</c> text that precede
    /// <see cref="BodyText"/>'s start. Added to a body error's line so the
    /// reported number is relative to the whole <c>CREATE PROCEDURE</c>
    /// statement rather than the stored body span, matching real SQL Server
    /// (probe-confirmed). Threaded onto the per-call child batch's
    /// <see cref="BatchContext.LineOffset"/>.
    /// </summary>
    public readonly int BodyLineOffset = bodyLineOffset;

    public override string ObjectTypeCode => "P ";
    public override string ObjectTypeDescription => "SQL_STORED_PROCEDURE";

    /// <summary>
    /// Declared parameters in source order. Each carries name, type, optional
    /// default expression, and an <c>IsOutput</c> flag for <c>OUTPUT</c> /
    /// <c>OUT</c>-declared params (which writeback to the caller's argument
    /// variable at proc exit).
    /// </summary>
    public readonly ProcedureParameter[] Parameters = parameters;

    /// <summary>
    /// Raw source text of the body — everything between the <c>AS</c> keyword
    /// (exclusive) and the trailing statement boundary that terminated the
    /// CREATE/ALTER PROCEDURE statement. Re-tokenized and re-parsed per call.
    /// Empty bodies are legal (probe-confirmed: <c>CREATE PROC p AS</c> with
    /// nothing after <c>AS</c> succeeds and yields one empty result set when
    /// invoked).
    /// </summary>
    public readonly string BodyText = bodyText;
}

/// <summary>
/// One declared parameter on a <see cref="Procedure"/>. The <see cref="Name"/>
/// is stored with the leading <c>@</c> stripped (matching the
/// <see cref="BatchContext.Variables"/> keying convention).
/// </summary>
internal sealed class ProcedureParameter(string name, SqlType type, int? declaredMaxLength, Expression? defaultExpression, bool isOutput, TableType? tableType = null, bool isCursor = false)
{
    public readonly string Name = name;
    public readonly SqlType Type = type;

    /// <summary>
    /// True when the parameter is a cursor parameter (<c>@c CURSOR VARYING
    /// OUTPUT</c>). Cursor parameters are output-only (real SQL Server requires
    /// <c>VARYING OUTPUT</c>): the body <c>SET</c>s and <c>OPEN</c>s a cursor
    /// on the parameter, and the invocation binds it back into the caller's
    /// cursor variable. The <see cref="Type"/> placeholder is unused for these.
    /// </summary>
    public readonly bool IsCursor = isCursor;

    /// <summary>
    /// Non-null when this parameter is a table-valued parameter — references
    /// the <see cref="TableType"/> that defines its column shape. Always
    /// implies <see cref="IsOutput"/> is false and <c>READONLY</c> was set
    /// at CREATE PROCEDURE time (Msg 352 otherwise — probed). The
    /// <see cref="Type"/> field for TVP parameters is the placeholder
    /// <see cref="SqlType.Int32"/> (catalog views consult
    /// <see cref="TableType"/> instead — system_type_id 243 for TVP rows in
    /// sys.parameters / sys.types).
    /// </summary>
    public readonly TableType? TableType = tableType;

    /// <summary>
    /// The declared <c>(N)</c> on a variable-length string/binary type, kept
    /// alongside <see cref="Type"/> for catalog-view fidelity
    /// (<c>INFORMATION_SCHEMA.PARAMETERS.CHARACTER_MAXIMUM_LENGTH</c>).
    /// Null when not applicable.
    /// </summary>
    public readonly int? DeclaredMaxLength = declaredMaxLength;

    /// <summary>
    /// The <c>= expr</c> default, parsed once at CREATE PROCEDURE time and
    /// evaluated in the per-call <see cref="BatchContext"/> when the caller
    /// omits the argument or passes the <c>DEFAULT</c> keyword.
    /// <see langword="null"/> when no default was declared — calls must
    /// supply the argument or raise Msg 201 (probe-confirmed).
    /// </summary>
    public readonly Expression? Default = defaultExpression;

    /// <summary>
    /// True when the parameter was declared with <c>OUTPUT</c> or <c>OUT</c>.
    /// Surfaces in <c>sys.parameters.is_output</c> and gates EXEC-time
    /// writeback to the caller's argument variable: only OUTPUT-declared
    /// params, AND only when the caller passed <c>OUTPUT</c> on the
    /// corresponding argument, write back at proc exit. Probe-confirmed: a
    /// caller that omits <c>OUTPUT</c> on an OUTPUT-declared param silently
    /// suppresses the writeback (the caller's variable retains its
    /// pre-EXEC value).
    /// </summary>
    public readonly bool IsOutput = isOutput;
}
