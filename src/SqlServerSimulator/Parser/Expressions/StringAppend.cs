using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An assignment to a <c>varchar(max)</c> or <c>nvarchar(max)</c> variable of
/// the variable followed by more text — <c>@v + a + b</c>, the <c>@v + a</c>
/// that <c>@v += a</c> means, <c>CONCAT(@v, a, b)</c>, or the list idiom
/// <c>COALESCE(@v + ',', '') + a</c> and its <c>ISNULL</c> twin — run as an
/// append to the variable's text in place (<see cref="VariableSlot.Append"/>),
/// so a loop or a <c>SELECT</c> over rows building a string runs in linear
/// time where each step copied the string whole. Real copies it at each step
/// too, only more cheaply.
/// </summary>
/// <remarks>
/// Every appended part is <see cref="Expression.ParallelSafe"/> — it reads only
/// its row and writes nothing — so a step meeting anything an append doesn't
/// reproduce declines before it changes anything, and the ordinary assignment
/// evaluates the parts again: a result short of MAX or in another code page
/// than the variable's, and in a <c>+</c> chain a NULL, a value other than a
/// string, or a <c>COALESCE</c> that would convert what it returns. A part in
/// another code page than its step converts into it, as the step's <c>+</c>
/// converts it.
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

    /// <summary>The <c>CONCAT</c> whose arguments past the variable <see cref="parts"/> are, or null for a <c>+</c> chain.</summary>
    private readonly StringConcat? concat;

    private StringAppend(Expression[] parts, Coalesce? coalesce, int wrapped, StringConcat? concat)
    {
        this.parts = parts;
        this.coalesce = coalesce;
        this.wrapped = wrapped;
        this.concat = concat;
    }

    /// <summary>
    /// The append that assigning <paramref name="source"/> makes of
    /// <paramref name="slot"/>'s variable, named <paramref name="variableName"/>,
    /// or null when it makes none.
    /// </summary>
    public static StringAppend? Of(string variableName, VariableSlot slot, Expression source)
    {
        if (!IsMaxString(slot.DeclaredType))
            return null;
        if (source is StringConcat concat)
        {
            return concat.ArgumentsAfterVariable(variableName) is { } rest && Array.TrueForAll(rest, static part => part.ParallelSafe)
                ? new StringAppend(rest, coalesce: null, wrapped: 0, concat)
                : null;
        }
        if (source is not Add add)
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
            ? new StringAppend([.. parts], coalesce, wrapped, concat: null)
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
        if (this.concat is { } concat)
            return this.TryAppendConcat(concat, slot, runtime);
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
            if (!KeepsText(declared, type))
                return false;
            suffixes.Add(Add.TextInCodePageOf(value, type));
        }
        slot.Append(suffixes);
        return true;
    }

    /// <summary>
    /// <see cref="TryAppend"/> for a <c>CONCAT</c>, typed as it types itself
    /// with a NULL of the variable's type standing for the variable, whose
    /// type alone counts there: a NULL argument adds nothing, and any other
    /// is stringified as <c>CONCAT</c> stringifies it.
    /// </summary>
    private bool TryAppendConcat(StringConcat concat, VariableSlot slot, RuntimeContext runtime)
    {
        var declared = slot.DeclaredType;
        var values = new SqlValue[this.parts.Length + 1];
        values[0] = SqlValue.Null(declared);
        for (var i = 0; i < this.parts.Length; i++)
            values[i + 1] = this.parts[i].Run(runtime);
        var type = concat.ResultTypeOf(values, runtime.Batch);
        if (!KeepsText(declared, type))
            return false;
        List<string> suffixes = new(this.parts.Length);
        for (var i = 1; i < values.Length; i++)
        {
            if (!values[i].IsNull)
                suffixes.Add(StringConcat.StringifyForConcat(values[i], type));
        }
        slot.Append(suffixes);
        return true;
    }

    /// <summary>
    /// Whether a step resulting in <paramref name="type"/> keeps as it is the
    /// text a variable of <paramref name="declared"/> holds: a MAX string of
    /// the same family and, for <c>varchar</c>, the same code page — a
    /// <c>+</c> into another code page converts the text it joins, and the
    /// assignment converts a result of another family or code page.
    /// </summary>
    private static bool KeepsText(SqlType declared, SqlType type) =>
        IsMaxString(type)
        && (type is VarcharSqlType
            ? declared is VarcharSqlType && type.Collation!.StorageEncoding == declared.Collation!.StorageEncoding
            : declared is NVarcharSqlType);
}
