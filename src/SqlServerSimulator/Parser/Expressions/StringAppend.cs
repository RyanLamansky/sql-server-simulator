using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An assignment to a <c>varchar(max)</c> or <c>nvarchar(max)</c> variable of
/// the variable followed by more text — <c>@v + a + b</c>, the <c>@v + a</c>
/// that <c>@v += a</c> means, or the list idiom <c>COALESCE(@v + ',', '') + a</c>
/// and its <c>ISNULL</c> twin — run as an append to the variable's text in
/// place (<see cref="VariableSlot.Append"/>), so a loop or a <c>SELECT</c> over
/// rows building a string runs in linear time where each step copied the
/// string whole. Real copies it at each step too, only more cheaply.
/// </summary>
/// <remarks>
/// Every appended part is <see cref="Expression.ParallelSafe"/> — it reads only
/// its row and writes nothing — so a step meeting anything an append doesn't
/// reproduce declines before it changes anything, and the ordinary assignment
/// evaluates the parts again: a NULL, a value other than a string, a result
/// short of MAX or in another code page than the variable's, or a
/// <c>COALESCE</c> that would convert what it returns.
/// </remarks>
internal sealed class StringAppend
{
    /// <summary>The text appended, in the order the assignment evaluates it.</summary>
    private readonly Expression[] parts;

    /// <summary>
    /// The <c>COALESCE</c> the variable and the first <see cref="wrapped"/>
    /// parts are the first argument of, which an append passes through only
    /// while it returns that argument unconverted; null for an <c>ISNULL</c>,
    /// which returns it as it is, and for no list idiom at all.
    /// </summary>
    private readonly Coalesce? coalesce;

    private readonly int wrapped;

    private StringAppend(Expression[] parts, Coalesce? coalesce, int wrapped)
    {
        this.parts = parts;
        this.coalesce = coalesce;
        this.wrapped = wrapped;
    }

    /// <summary>
    /// The append that assigning <paramref name="source"/> makes of
    /// <paramref name="slot"/>'s variable, named <paramref name="variableName"/>,
    /// or null when it makes none.
    /// </summary>
    public static StringAppend? Of(string variableName, VariableSlot slot, Expression source)
    {
        if (!IsMaxString(slot.DeclaredType) || source is not Add add)
            return null;
        List<Expression> parts = [];
        var head = add.UnchainAdds(parts);
        var coalesce = head as Coalesce;
        var wrappedChain = head switch
        {
            Coalesce { Arguments: [Add first, ..] } => first,
            IsNullExpression { Check: Add check } => check,
            _ => null,
        };
        var wrapped = 0;
        if (wrappedChain is not null)
        {
            List<Expression> inner = [];
            head = wrappedChain.UnchainAdds(inner);
            wrapped = inner.Count;
            parts.InsertRange(0, inner);
        }
        return head is VariableReference reference
            && BatchContext.VariableNameComparer.Equals(reference.VariableName, variableName)
            && parts.TrueForAll(static part => part.ParallelSafe)
            ? new StringAppend([.. parts], coalesce, wrapped)
            : null;
    }

    private static bool IsMaxString(SqlType type) =>
        type is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel };

    /// <summary>
    /// Appends to <paramref name="slot"/> what the assignment adds to its value,
    /// answering whether it did; false leaves the slot as it was, for the
    /// ordinary assignment to run.
    /// </summary>
    public bool TryAppend(VariableSlot slot, RuntimeContext runtime)
    {
        var declared = slot.DeclaredType;
        if (!slot.HoldsNonNull(declared) || runtime.Batch.Connection.Simulation.DeclaresDataMasks)
            return false;
        var type = declared;
        List<string> suffixes = new(this.parts.Length);
        for (var i = 0; i < this.parts.Length; i++)
        {
            if (i == this.wrapped && this.coalesce?.ArmTarget(type) is not null)
                return false;
            var value = this.parts[i].Run(runtime);
            if (value.IsNull || value.Type is not (VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType or SystemNameSqlType))
                return false;
            type = Add.StringPairType(type, value.Type);
            if (!IsMaxString(type))
                return false;
            suffixes.Add(value.AsString);
        }

        // The assignment converts the result to the variable's type, which
        // keeps the text only within one family and, for varchar, one code page.
        var keepsText = type is VarcharSqlType
            ? declared is VarcharSqlType && type.Collation!.StorageEncoding == declared.Collation!.StorageEncoding
            : declared is NVarcharSqlType;
        if (!keepsText)
            return false;
        slot.Append(suffixes);
        return true;
    }
}
