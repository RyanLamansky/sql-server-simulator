using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Active-stored-procedure-call state on a <see cref="BatchContext"/>. The
/// presence of a non-null <see cref="BatchContext.ProcFrame"/> tells the
/// dispatch loop that the batch is executing a procedure body — relaxes the
/// <c>RETURN &lt;expr&gt;</c> rejection (Msg 178), provides the slot the
/// body writes its return code into, and tells the per-statement
/// <c>SELECT</c> dispatcher to surface result sets to the outer caller's
/// iterator (rather than discarding them as scalar-UDF bodies do).
/// </summary>
/// <remarks>
/// Distinct from <see cref="UdfFrame"/> in two ways: the return slot is
/// always <see cref="SqlType.Int32"/> (procs return an int return code, not
/// a typed value), and result sets propagate up. The frame also carries the
/// procedure name for <c>ERROR_PROCEDURE()</c> attribution.
/// </remarks>
internal sealed class ProcFrame(string procedureName, bool isDynamicSql = false)
{
    /// <summary>
    /// True when this frame wraps a dynamic-SQL batch (<c>EXEC('…')</c> /
    /// <c>sp_executesql</c>) rather than a real stored-procedure body. The
    /// two differ in <c>SET QUOTED_IDENTIFIER</c> handling (probe-confirmed):
    /// dynamic SQL honors the SET within its own batch (reverting at exit),
    /// while a procedure body ignores it entirely.
    /// </summary>
    public readonly bool IsDynamicSql = isDynamicSql;

    /// <summary>
    /// Procedure being executed. Procedure-body error attribution
    /// (<c>ERROR_PROCEDURE()</c> / <see cref="SimulatedError.Procedure"/>) is
    /// threaded through <see cref="BatchContext.ErrorProcedureName"/> — the
    /// schema-qualified name set on the child batch at invocation — rather than
    /// this leaf name; see <c>docs/claude/errors.md</c>.
    /// </summary>
    public readonly string ProcedureName = procedureName;

    /// <summary>
    /// The value the body's <c>RETURN &lt;expr&gt;</c> assigned, or
    /// <see langword="null"/> when the body ended without one (a bare
    /// <c>RETURN</c> included). <c>RETURN NULL</c> lands 0 here, after Msg 282
    /// says so. The call site reads this after dispatch completes, falling
    /// back to <see cref="StatusWithoutReturnValue"/>.
    /// </summary>
    public int? ReturnCode;

    /// <summary>
    /// The highest severity among the errors the body's own statements raised,
    /// caught by its own <c>TRY</c> or not; 0 when none did. Errors a nested
    /// procedure or dynamic SQL raised don't count, even when they reach this
    /// body.
    /// </summary>
    public byte MaxErrorSeverity;

    /// <summary>
    /// The status a body without a <c>RETURN</c> value reports: 0, or
    /// <c>10 - severity</c> for the most severe error its own statements raised
    /// (−1 for severity 11 through −6 for 16), probed 2026-09-24 against
    /// SQL Server 2025.
    /// </summary>
    public int StatusWithoutReturnValue => this.MaxErrorSeverity >= 11 ? 10 - this.MaxErrorSeverity : 0;
}
