using System.Data.Common;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// One entry in <see cref="BatchContext.Variables"/>: a declared variable
/// or a SqlClient-parameter-seeded variable. Holds the declared SqlType
/// (so <c>SET @v = expr</c> can coerce the RHS through the existing CAST
/// machinery before storing) plus the current value (mutable as the batch
/// runs). When the slot was seeded from a <see cref="DbParameter"/>, the
/// parameter reference is retained so end-of-batch processing can write the
/// final value back to <see cref="DbParameter.Value"/> for
/// <c>InputOutput</c> / <c>Output</c> direction parameters.
/// </summary>
internal sealed class VariableSlot(SqlType declaredType, int? declaredMaxLength, SqlValue value, DbParameter? parameter)
{
    public readonly SqlType DeclaredType = declaredType;

    /// <summary>
    /// Declared max length for variable-length string / binary types (e.g.
    /// <c>varchar(3)</c> stores 3); null for fixed-length and unbounded
    /// types. Routes through <c>Cast.ApplyCoercion</c> on every assignment
    /// so <c>SET @v(varchar(3)) = 'hello'</c> truncates to <c>'hel'</c>.
    /// </summary>
    public readonly int? DeclaredMaxLength = declaredMaxLength;

    public SqlValue Value = value;

    public readonly DbParameter? Parameter = parameter;
}
