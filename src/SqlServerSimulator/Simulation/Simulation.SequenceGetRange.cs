using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly string[] SequenceGetRangeParameters =
    [
        "sequence_name", "range_size", "range_first_value", "range_last_value",
        "range_cycle_count", "sequence_increment", "sequence_min_value", "sequence_max_value",
    ];

    /// <summary>
    /// <c>sp_sequence_get_range @sequence_name, @range_size, @range_first_value
    /// OUTPUT [, @range_last_value OUTPUT [, @range_cycle_count OUTPUT [,
    /// @sequence_increment OUTPUT [, @sequence_min_value OUTPUT [,
    /// @sequence_max_value OUTPUT]]]]]</c>: reserves a range of the sequence's
    /// values (<see cref="Schemas.Sequence.DrawRange"/>) and reports it through
    /// the output parameters, the values as <c>sql_variant</c>s of the
    /// sequence's type. Binding refusals are the procedure's own at line 0 — a
    /// missing <c>@range_first_value</c> Msg 201, an output variable of another
    /// type than its parameter's Msg 257 — and the body's come from its
    /// internal procedure at line 1: a name that isn't a sequence Msg 208 at
    /// state 134, a range size that isn't positive Msg 11733 (probed 2026-10-04
    /// against SQL Server 2025). Like a draw, the range is never handed back
    /// when the transaction rolls back.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSequenceGetRange(BatchContext batch, string calledAs, string? returnCodeVariableName)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var supplied = new ProcArgument?[SequenceGetRangeParameters.Length];
        try
        {
            string? unknownName = null;
            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                var slot = i;
                if (argument.Name is { } name)
                {
                    slot = Array.FindIndex(SequenceGetRangeParameters, parameter => BuiltInToken.Equals(name, parameter));
                    if (slot < 0)
                    {
                        unknownName ??= name;
                        continue;
                    }
                }
                else if (i >= SequenceGetRangeParameters.Length)
                {
                    throw SimulatedSqlException.TooManyArgumentsToFunction("sp_sequence_get_range");
                }
                supplied[slot] = argument;
            }
            for (var i = 0; i < 3; i++)
            {
                if (supplied[i] is not { IsDefault: false })
                    throw SimulatedSqlException.ProcedureExpectsParameter("sp_sequence_get_range", SequenceGetRangeParameters[i]);
            }
            if (unknownName is not null)
                throw SimulatedSqlException.NotAParameterForProcedure(unknownName, "sp_sequence_get_range");
            for (var i = 2; i < supplied.Length; i++)
            {
                if (supplied[i]?.OutputSlot is { } output && !(i == 4 ? output.DeclaredType is Int32SqlType : output.DeclaredType is SqlVariantSqlType))
                    throw SimulatedSqlException.ImplicitConversionNotAllowed(i == 4 ? "int" : "sql_variant", output.DeclaredType.SqlServerName);
            }
        }
        catch (SimulatedSqlException refusal)
        {
            refusal.PreserveDiagnostics(0, calledAs);
            refusal.SystemProcedureBindingError = true;
            throw;
        }

        var nameValue = supplied[0]!.Value.Value;
        var text = nameValue.IsNull ? "" : nameValue.CoerceTo(SqlType.NVarcharMax).AsString;
        if (!Parser.Expressions.ObjectId.TryParseObjectName(text, out var sequenceName) || !batch.TryResolveSequence(sequenceName, out var sequence))
            throw SimulatedSqlException.SequenceRangeObjectNotFound(text);
        var sizeValue = supplied[1]!.Value.Value;
        var size = sizeValue.IsNull ? 0 : sizeValue.CoerceTo(SqlType.BigInt).AsInt64;
        if (size <= 0)
            throw SimulatedSqlException.SequenceRangeSizeNotPositive();
        if (!batch.Connection.Security.EffectiveIsDbo)
            PermissionEnforcement.CheckSequenceUpdate(batch, sequence);
        sequence.Schema.Database.RejectWriteWhenReadOnly();
        var (first, last, cycles) = sequence.DrawRange(size);

        void Write(int index, SqlValue value)
        {
            if (supplied[index]?.OutputSlot is { } output)
                output.Value = value;
        }
        Write(2, sequence.AsDeclaredVariant(first));
        Write(3, sequence.AsDeclaredVariant(last));
        Write(4, SqlValue.FromInt32(cycles));
        Write(5, sequence.AsDeclaredVariant(sequence.Increment));
        Write(6, sequence.AsDeclaredVariant(sequence.MinValue));
        Write(7, sequence.AsDeclaredVariant(sequence.MaxValue));
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }
}
