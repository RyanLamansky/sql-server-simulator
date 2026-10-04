using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>Whether a system procedure's run ended in an error its own statements raised, which decides the return code it answers.</summary>
    internal sealed class SystemProcedureResult
    {
        public bool Failed;
    }

    /// <summary>
    /// One declared parameter of a system procedure whose arguments
    /// <see cref="BindSystemProcedureArguments"/> binds: its name, the type a
    /// supplied value is converted to (an integer type, or a string of at most
    /// <see cref="MaxLength"/> characters that a longer value is cut to, as a
    /// parameter's own assignment cuts it), and the value it takes when the
    /// call leaves it out — a parameter with no <see cref="Default"/> is
    /// required.
    /// </summary>
    internal readonly struct SystemProcedureParameter(string name, SqlType type, int maxLength = 0, SqlValue? defaultValue = null)
    {
        public readonly string Name = name;
        public readonly SqlType Type = type;
        public readonly int MaxLength = maxLength;
        public readonly SqlValue? Default = defaultValue;
    }

    /// <summary>
    /// Binds an <c>EXEC</c> argument list to a system procedure's parameters
    /// the way SQL Server binds a stored procedure's: positional then named, a
    /// surplus positional one Msg 8144, a required parameter left out Msg 201
    /// (ahead of a name that is no parameter, Msg 8145 — probed 2026-09-30
    /// against SQL Server 2025), and each value converted to its parameter's
    /// type with Msg 8114 for one that won't convert. A <c>DEFAULT</c> argument
    /// takes the parameter's default. Each refusal is attributed to line 0 of
    /// <paramref name="calledAs"/>, the name the call used.
    /// </summary>
    private static SqlValue[] BindSystemProcedureArguments(string procedure, string calledAs, List<ProcArgument> arguments, SystemProcedureParameter[] parameters)
    {
        try
        {
            return BindArguments(procedure, arguments, parameters);
        }
        catch (SimulatedSqlException refusal)
        {
            // A binding refusal is the procedure's own at line 0, under the
            // name the call used, and unlike its body's errors leaves the
            // return code alone.
            refusal.PreserveDiagnostics(0, calledAs);
            refusal.SystemProcedureBindingError = true;
            throw;
        }
    }

    /// <summary>
    /// Refuses an argument list a system procedure whose own parser reads the
    /// values can't bind, as real's parameter binder refuses it, before the
    /// procedure runs: any argument to one declaring no parameters is Msg 8146,
    /// a positional argument past <paramref name="parameterNames"/> Msg 8144,
    /// one of the leading <paramref name="required"/> parameters left out (or
    /// given <c>DEFAULT</c>) Msg 201, and a name that is no parameter Msg 8145,
    /// in that order (probed 2026-10-04 against SQL Server 2025: sp_rename,
    /// sp_help, sp_spaceused, sp_helpsort). Each is a binding refusal, which
    /// leaves the caller's return code alone.
    /// </summary>
    private static void RequireSystemProcedureShape(string procedure, List<ProcArgument> arguments, string[] parameterNames, int required = 0)
    {
        try
        {
            if (parameterNames.Length == 0)
            {
                if (arguments.Count > 0)
                    throw SimulatedSqlException.ArgumentsSuppliedToParameterlessRoutine(procedure, state: 2);
                return;
            }
            var positional = 0;
            foreach (var argument in arguments)
            {
                if (argument.Name is null && ++positional > parameterNames.Length)
                    throw SimulatedSqlException.TooManyArgumentsToFunction(procedure);
            }
            for (var i = 0; i < required; i++)
            {
                var parameterName = parameterNames[i];
                var supplied = (i < arguments.Count && arguments[i] is { Name: null, IsDefault: false })
                    || arguments.Exists(argument => argument is { Name: { } name, IsDefault: false } && BuiltInToken.Equals(name, parameterName));
                if (!supplied)
                    throw SimulatedSqlException.ProcedureExpectsParameter(procedure, parameterName);
            }
            foreach (var argument in arguments)
            {
                if (argument.Name is { } name && Array.FindIndex(parameterNames, parameter => BuiltInToken.Equals(name, parameter)) < 0)
                    throw SimulatedSqlException.NotAParameterForProcedure(name, procedure);
            }
        }
        catch (SimulatedSqlException refusal)
        {
            refusal.SystemProcedureBindingError = true;
            throw;
        }
    }

    private static SqlValue[] BindArguments(string procedure, List<ProcArgument> arguments, SystemProcedureParameter[] parameters)
    {
        if (parameters.Length == 0 && arguments.Count > 0)
            throw SimulatedSqlException.ArgumentsSuppliedToParameterlessRoutine(procedure, state: 2);
        var supplied = new ProcArgument?[parameters.Length];
        string? unknownName = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var slot = i;
            if (argument.Name is { } name)
            {
                slot = Array.FindIndex(parameters, parameter => BuiltInToken.Equals(name, parameter.Name));
                if (slot < 0)
                {
                    unknownName ??= name;
                    continue;
                }
            }
            else if (i >= parameters.Length)
            {
                throw SimulatedSqlException.TooManyArgumentsToFunction(procedure);
            }
            supplied[slot] = argument;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (supplied[i] is not { IsDefault: false } && parameters[i].Default is null)
                throw SimulatedSqlException.ProcedureExpectsParameter(procedure, parameters[i].Name);
        }
        if (unknownName is not null)
            throw SimulatedSqlException.NotAParameterForProcedure(unknownName, procedure);

        var values = new SqlValue[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            values[i] = supplied[i] is { IsDefault: false } argument
                ? ConvertToParameter(argument.Value, parameter, argument.IsNumericLiteral)
                : parameter.Default!.Value;
        }
        return values;
    }

    private static SqlValue ConvertToParameter(SqlValue value, SystemProcedureParameter parameter, bool numericLiteral)
    {
        if (parameter.MaxLength > 0)
        {
            if (value.IsNull)
                return SqlValue.Null(SqlType.NVarchar);
            var text = value.CoerceTo(SqlType.NVarchar).AsString;
            return SqlValue.FromNVarchar(text.Length > parameter.MaxLength ? text[..parameter.MaxLength] : text);
        }
        if (value.IsNull)
            return SqlValue.Null(parameter.Type);
        int number;
        try
        {
            number = ScalarArguments.CoerceProcedureParameter(value, parameter.Type);
        }
        catch (SimulatedSqlException refusal) when (refusal.Number == 8114)
        {
            // Binding a procedure's argument reports the conversion at state 5
            // (probed 2026-10-04 against SQL Server 2025: sp_lock, sp_addmessage).
            throw numericLiteral
                ? SimulatedSqlException.ConvertingDataTypeError("numeric", parameter.Type.SqlServerName)
                : SimulatedSqlException.ConvertingDataTypeError(value.Type, parameter.Type.SqlServerName);
        }
        return parameter.Type == SqlType.SmallInt ? SqlValue.FromInt16((short)number) : SqlValue.FromInt32(number);
    }
}
