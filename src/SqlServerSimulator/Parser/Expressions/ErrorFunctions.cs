using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Base class for <c>ERROR_*()</c> scalar functions
/// (<c>ERROR_NUMBER</c> / <c>ERROR_MESSAGE</c> / <c>ERROR_SEVERITY</c> /
/// <c>ERROR_STATE</c> / <c>ERROR_LINE</c> / <c>ERROR_PROCEDURE</c>). All
/// take zero arguments, return their declared SQL type (int / nvarchar(4000)
/// / int / int / int / nvarchar(128)), and read
/// <see cref="BatchContext.InFlightError"/> at evaluation time — null
/// outside a CATCH block produces a typed NULL (probe-confirmed against
/// SQL Server 2025, 2026-05-12).
/// </summary>
/// <remarks>
/// <para>
/// CATCH-scope detection uses the InFlightError field rather than the
/// CatchDepth counter: inside a CATCH where the original error has already
/// been cleared (e.g. by a nested THROW that overwrites it), the original
/// in-flight error is what these functions surface. For nested TRY/CATCH
/// re-throw flows the outer CATCH sees the re-thrown error because the
/// throw propagated through the dispatch wrapper and updated InFlightError.
/// </para>
/// </remarks>
internal sealed class ErrorNumberFunction : Expression
{
    public ErrorNumberFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "error_number");

    public override SqlValue Run(RuntimeContext runtime) =>
        ErrorFunctionCtor.Handled(runtime) is CaughtError err
            ? SqlValue.FromInt32(err.Number)
            : SqlValue.Null(SqlType.Int32);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "ERROR_NUMBER()";

    internal override void Describe(NodeShape shape) { }
}

internal sealed class ErrorMessageFunction : Expression
{
    public ErrorMessageFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "error_message");

    public override SqlValue Run(RuntimeContext runtime) =>
        ErrorFunctionCtor.Handled(runtime) is CaughtError err
            ? SqlValue.FromNVarchar(ErrorFunctionCtor.MessageType, err.Message)
            : SqlValue.Null(ErrorFunctionCtor.MessageType);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ErrorFunctionCtor.MessageType;

    internal override string DebugDisplay() => "ERROR_MESSAGE()";

    internal override void Describe(NodeShape shape) { }
}

internal sealed class ErrorSeverityFunction : Expression
{
    public ErrorSeverityFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "error_severity");

    public override SqlValue Run(RuntimeContext runtime) =>
        ErrorFunctionCtor.Handled(runtime) is CaughtError err
            ? SqlValue.FromInt32(err.Severity)
            : SqlValue.Null(SqlType.Int32);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "ERROR_SEVERITY()";

    internal override void Describe(NodeShape shape) { }
}

internal sealed class ErrorStateFunction : Expression
{
    public ErrorStateFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "error_state");

    public override SqlValue Run(RuntimeContext runtime) =>
        ErrorFunctionCtor.Handled(runtime) is CaughtError err
            ? SqlValue.FromInt32(err.State)
            : SqlValue.Null(SqlType.Int32);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "ERROR_STATE()";

    internal override void Describe(NodeShape shape) { }
}

internal sealed class ErrorLineFunction : Expression
{
    public ErrorLineFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "error_line");

    public override SqlValue Run(RuntimeContext runtime) =>
        ErrorFunctionCtor.Handled(runtime) is CaughtError err
            ? SqlValue.FromInt32(err.Line)
            : SqlValue.Null(SqlType.Int32);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "ERROR_LINE()";

    internal override void Describe(NodeShape shape) { }
}

internal sealed class ErrorProcedureFunction : Expression
{
    public ErrorProcedureFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "error_procedure");

    public override SqlValue Run(RuntimeContext runtime) =>
        ErrorFunctionCtor.Handled(runtime) is CaughtError err && err.Procedure is not null
            ? SqlValue.FromNVarchar(ErrorFunctionCtor.ProcedureType, err.Procedure)
            : SqlValue.Null(ErrorFunctionCtor.ProcedureType);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ErrorFunctionCtor.ProcedureType;

    internal override string DebugDisplay() => "ERROR_PROCEDURE()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Shared zero-argument validation for the <c>ERROR_*()</c> family. A
/// non-<c>)</c> token immediately after the function-call's <c>(</c> raises
/// the standard Msg 174 ("...requires 0 argument(s)") path so the family
/// surfaces a uniform diagnostic. Kept as a static helper rather than an
/// abstract base so each concrete function can stay a primary-constructor
/// shape (SSS001 keeps non-public types using fields over auto-properties;
/// the simpler primary-ctor inheritance form trips IDE0290).
/// </summary>
internal static class ErrorFunctionCtor
{
    /// <summary><c>ERROR_MESSAGE()</c>'s type, and <c>FORMATMESSAGE</c>'s: <c>nvarchar(4000)</c>.</summary>
    public static readonly NVarcharSqlType MessageType = NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <summary><c>ERROR_PROCEDURE()</c>'s type: <c>nvarchar(128)</c> (probed 2026-10-02 against SQL Server 2025).</summary>
    public static readonly NVarcharSqlType ProcedureType = NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <summary>
    /// The error a running <c>CATCH</c> handles: the batch's own, else the one
    /// a caller's <c>CATCH</c> handles around the body this batch runs.
    /// </summary>
    public static CaughtError? Handled(RuntimeContext runtime) =>
        runtime.Batch.InFlightError ?? runtime.Batch.Connection.EnclosingCatchError;

    public static void EnsureNoArgs(ParserContext context, string name)
    {
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments(name, 0);
    }
}
