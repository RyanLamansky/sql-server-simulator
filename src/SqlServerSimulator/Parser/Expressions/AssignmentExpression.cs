using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Represents a <c>@v = expr</c> projection element in a SELECT-assign:
/// holds the variable's name and declared type and the RHS source
/// expression. <c>Run</c> has the side effect of writing to the executing
/// batch's slot for the variable (mutated as the projection runs row-by-row)
/// via the standard CAST coercion path; the returned <see cref="SqlValue"/>
/// is the post-coerce value but is never surfaced because SELECT-assign
/// produces no result rows. The slot is looked up as each row assigns it,
/// as <see cref="VariableReference"/> reads it, so a cached plan assigns the
/// variables of the batch replaying it.
/// </summary>
/// <remarks>
/// Empty-result-keeps-prior-value (probe-confirmed) falls out naturally:
/// when the FROM clause yields zero rows, this expression's <c>Run</c>
/// is never called, so the slot retains its prior value. Non-empty
/// last-row-wins (also probe-confirmed) follows from per-row evaluation
/// — each row's <c>Run</c> overwrites the slot, so the final value is the
/// last iterated row's RHS.
/// </remarks>
internal sealed class AssignmentExpression(string variableName, VariableSlot slot, Expression source) : Expression
{
    /// <summary>The variable's name as written, which the executing batch's <see cref="BatchContext.GetVariableSlot"/> finds it by.</summary>
    public readonly string VariableName = variableName;

    /// <summary>The variable's declared type, as parsing found it.</summary>
    public readonly SqlType DeclaredType = slot.DeclaredType;

    private readonly int? declaredMaxLength = slot.DeclaredMaxLength;

    public readonly Expression Source = source;

    /// <summary>
    /// The mask the assigned value reads through, set with the projection's
    /// <see cref="Selection.ColumnMasks"/>; see <see cref="DataMasking.ForAssignment(BatchContext, DataMask, SqlValue, SqlValue, SqlType)"/>.
    /// </summary>
    internal DataMask? Mask;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.Source.Run(runtime);
        Cast.RejectRoundingUnderRoundAbort(value, this.DeclaredType, runtime.Batch);
        var coerced = SqlValue.NameVariantBase(value, Cast.ApplyCoercion(value, this.DeclaredType, this.declaredMaxLength), this.Source.ResultReportsNumeric);
        if (this.Mask is { } mask)
            coerced = DataMasking.ForAssignment(runtime.Batch, mask, value, coerced, this.DeclaredType);
        runtime.Batch.GetVariableSlot(this.VariableName).Assign(coerced);
        return coerced;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // The slot's type is the projection's, but the source still has to bind
        // — that is what surfaces the assigned expression's own compile-time
        // errors, including a varchar whose collation never resolved (Msg 456;
        // an nvarchar one settles against the slot silently).
        var sourceType = this.Source.GetSqlType(batch, resolveColumnType);
        UnresolvedCollation.RequireAssignable(sourceType);
        AssignmentRules.RequireAssignable(this.Source, sourceType, this.DeclaredType);
        return this.DeclaredType;
    }

    internal override string DebugDisplay() => $"@{this.DeclaredType} = {this.Source.DebugDisplay()}";

    internal override void Describe(NodeShape shape) => shape.Local(this.VariableName).Child(this.Source);
}
