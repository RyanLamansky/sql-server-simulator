using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Represents a <c>@v = expr</c> projection element in a SELECT-assign:
/// holds the live <see cref="VariableSlot"/> reference (mutated as the
/// projection runs row-by-row) and the RHS source expression. <c>Run</c>
/// has the side effect of writing to the slot via the standard CAST
/// coercion path; the returned <see cref="SqlValue"/> is the post-coerce
/// value but is never surfaced because SELECT-assign produces no result
/// rows.
/// </summary>
/// <remarks>
/// Empty-result-keeps-prior-value (probe-confirmed) falls out naturally:
/// when the FROM clause yields zero rows, this expression's <c>Run</c>
/// is never called, so the slot retains its prior value. Non-empty
/// last-row-wins (also probe-confirmed) follows from per-row evaluation
/// — each row's <c>Run</c> overwrites the slot, so the final value is the
/// last iterated row's RHS.
/// </remarks>
internal sealed class AssignmentExpression(VariableSlot slot, Expression source) : Expression
{
    public readonly VariableSlot Slot = slot;

    public readonly Expression Source = source;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.Source.Run(runtime);
        var coerced = Cast.ApplyCoercion(value, this.Slot.DeclaredType, this.Slot.DeclaredMaxLength);
        this.Slot.Value = coerced;
        return coerced;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // The slot's type is the projection's, but the source still has to bind
        // — that is what surfaces the assigned expression's own compile-time
        // errors, including a varchar whose collation never resolved (Msg 456;
        // an nvarchar one settles against the slot silently).
        UnresolvedCollation.RequireAssignable(this.Source.GetSqlType(batch, resolveColumnType));
        return this.Slot.DeclaredType;
    }

    internal override string DebugDisplay() => $"@{this.Slot.DeclaredType} = {this.Source.DebugDisplay()}";
}
