using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Active-scalar-UDF-call state on a <see cref="BatchContext"/>. The
/// presence of a non-null <see cref="BatchContext.UdfFrame"/> tells the
/// dispatch loop that the batch is executing a function body — relaxes the
/// <c>RETURN &lt;expr&gt;</c> rejection (Msg 178) and provides the slot the
/// body writes its return value into.
/// </summary>
internal sealed class UdfFrame(SqlType returnType)
{
    /// <summary>Declared return type, used to coerce <see cref="ReturnedValue"/> on assignment.</summary>
    public readonly SqlType ReturnType = returnType;

    /// <summary>
    /// The value the body's last <c>RETURN &lt;expr&gt;</c> assigned, or
    /// <see cref="SqlValue.Null"/> of <see cref="ReturnType"/> if dispatch
    /// completed without one. The call site reads this after the body's
    /// dispatch returns.
    /// </summary>
    public SqlValue ReturnedValue = SqlValue.Null(returnType);
}
