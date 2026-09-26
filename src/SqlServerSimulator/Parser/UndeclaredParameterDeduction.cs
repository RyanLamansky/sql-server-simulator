using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The type deduction <c>sp_describe_undeclared_parameters</c> runs while it
/// compiles its batch: each undeclared parameter is declared with a
/// placeholder type, and every place the binder pairs a type with one — an
/// assignment, a comparison, an operator, a unification, a <c>LIKE</c>, a
/// conversion, a row limit — records the type that place implies instead of
/// judging the pair. The first place a parameter meets decides it, as a
/// parameter is used only once (real refuses a second use with Msg 11508).
/// </summary>
/// <remarks>
/// What each place implies is real's own rule, probed 2026-09-26 against SQL
/// Server 2025: an assignment and an equality (<c>=</c>, <c>&lt;&gt;</c>,
/// <c>IN</c>) take the other side's type exactly; an ordering comparison, an
/// arithmetic operator and a unification (<c>CASE</c>, <c>COALESCE</c>,
/// <c>UNION</c>) take it widened — a decimal to 38 digits at scale 19, a
/// fractional-second type to scale 7, a sized string or binary to its
/// 8000-byte form — with the arithmetic operators' own exceptions (see
/// <see cref="ArithmeticType"/>); a row limit takes <c>bigint</c>.
/// </remarks>
internal sealed class UndeclaredParameterDeduction(HashSet<string> names)
{
    /// <summary>The deduction the batch compiling on this thread feeds, if any.</summary>
    [ThreadStatic]
    internal static UndeclaredParameterDeduction? Current;

    /// <summary>Each deduced parameter's type, and whether it spells as <c>numeric</c>.</summary>
    public readonly Dictionary<string, (SqlType Type, bool Numeric)> Deduced = new(BatchContext.VariableNameComparer);

    /// <summary>The refusal the deduction met — Msg 11503 or 11507 — which the procedure raises.</summary>
    public SimulatedSqlException? Failure;

    /// <summary>
    /// Answers for <see cref="SqlType.OperandPairError"/> when either operand
    /// is an undeclared parameter: records what the other side implies and
    /// reports the pair handled, so the placeholder type is never judged.
    /// </summary>
    internal static bool Intercept(TypePairOperation operation, TypePairOperand left, TypePairOperand right, string operatorName)
    {
        if (Current is not { } deduction)
            return false;
        var leftName = deduction.ParameterName(left.Source);
        var rightName = deduction.ParameterName(right.Source);
        if (leftName is null && rightName is null)
            return false;
        if (leftName is not null && rightName is not null)
        {
            deduction.Failure ??= SimulatedSqlException.TwoUntypedParameters(leftName, rightName);
            return true;
        }

        // An assignment's right operand is its target: a parameter there takes
        // the assigned value's type — ISNULL's replacement typing its check
        // expression, or a value written to it.
        var (name, other) = leftName is not null ? (leftName, right) : (rightName!, left);
        var numeric = other.Type is DecimalSqlType && (other.ReportsNumeric || other.Source?.ResultReportsNumeric == true);
        var implied = operation switch
        {
            TypePairOperation.Assign => (other.Type, numeric),
            TypePairOperation.Compare => CompareType(other.Type, numeric, operatorName),
            TypePairOperation.Unify => other.Type is XmlSqlType or SqlVariantSqlType ? (other.Type, false) : (Widen(other.Type), other.Type is DecimalSqlType),
            TypePairOperation.Bitwise => (other.Type, false),
            _ => ArithmeticType(operation, other.Type),
        };
        deduction.Record(name, implied);
        return true;
    }

    /// <summary>
    /// Records <paramref name="target"/> for <paramref name="source"/> when it
    /// is an undeclared parameter a site assigns without a pair check of its
    /// own (an <c>INSERT … SELECT</c> column, a <c>CAST</c>'s target, a row
    /// limit's <c>bigint</c>); true when it was one.
    /// </summary>
    internal static bool NoteExact(Expression? source, SqlType target)
    {
        if (Current is not { } deduction || deduction.ParameterName(source) is not { } name)
            return false;
        deduction.Record(name, (target, false));
        return true;
    }

    /// <summary>
    /// <see cref="Intercept"/> for a comparison whose sides aren't paired
    /// through the type grid with their expressions — an <c>IN (SELECT …)</c>
    /// against the subquery's column. True when either side was a parameter.
    /// </summary>
    internal static bool NoteComparison(Expression left, SqlType leftType, Expression? right, SqlType rightType, string operatorName)
    {
        if (right is NamedExpression named)
            right = named.Inner;
        return Intercept(TypePairOperation.Compare, new TypePairOperand(leftType, left), new TypePairOperand(rightType, right), operatorName);
    }

    /// <summary>
    /// Records what a <c>LIKE</c> implies for a parameter on either side of
    /// it: <c>varchar(8000)</c> against an ANSI string, <c>nvarchar(4000)</c>
    /// against anything else, and no valid type against <c>xml</c> or
    /// <c>sql_variant</c>. True when either side was one.
    /// </summary>
    internal static bool NoteLike(Expression subject, SqlType subjectType, Expression pattern, SqlType patternType)
    {
        if (Current is not { } deduction)
            return false;
        var subjectName = deduction.ParameterName(subject);
        var patternName = deduction.ParameterName(pattern);
        if (subjectName is null && patternName is null)
            return false;
        if (subjectName is not null && patternName is not null)
        {
            deduction.Failure ??= SimulatedSqlException.TwoUntypedParameters(subjectName, patternName);
            return true;
        }

        var (name, other) = subjectName is not null ? (subjectName, patternType) : (patternName!, subjectType);
        deduction.Record(name, other switch
        {
            XmlSqlType or SqlVariantSqlType => (null, false),
            VarcharSqlType or CharSqlType => (VarcharSqlType.Get(8000, other.Collation!, Coercibility.CoercibleDefault), false),
            _ => (NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.CoercibleDefault), false),
        });
        return true;
    }

    /// <summary>
    /// For a set of value arms (<c>CASE</c>, <c>COALESCE</c>, <c>IIF</c>)
    /// holding undeclared parameters: records the other arms' unified type,
    /// widened, for each parameter arm, and hands back the arms left to
    /// unify — or null when no arm is a parameter.
    /// </summary>
    internal static (SqlType Type, int IntegerLiteralDigits, Expression Source)[]? TypeParameterBranches(
        ReadOnlySpan<(SqlType Type, int IntegerLiteralDigits, Expression Source)> branches)
    {
        if (Current is not { } deduction)
            return null;
        var parameters = new List<string>();
        var rest = new List<(SqlType, int, Expression)>();
        foreach (var branch in branches)
        {
            if (deduction.ParameterName(branch.Source) is { } name)
                parameters.Add(name);
            else
                rest.Add(branch);
        }
        if (parameters.Count == 0)
            return null;
        if (rest.Count == 0)
        {
            if (parameters.Count > 1)
                deduction.Failure ??= SimulatedSqlException.TwoUntypedParameters(parameters[0], parameters[1]);
            return [];
        }

        var unified = SqlType.PromoteBranches([.. rest]);
        foreach (var name in parameters)
            deduction.Record(name, unified is XmlSqlType or SqlVariantSqlType ? (unified, false) : (Widen(unified), unified is DecimalSqlType));
        return [.. rest];
    }

    private string? ParameterName(Expression? source)
    {
        while (source is Parenthesized parenthesized)
            source = parenthesized.Wrapped;
        return source is VariableReference variable && names.Contains(variable.VariableName) ? variable.VariableName : null;
    }

    private void Record(string name, (SqlType? Type, bool Numeric) implied)
    {
        if (this.Deduced.ContainsKey(name))
            return;
        if (implied.Type is not { } type)
        {
            this.Failure ??= SimulatedSqlException.NoValidParameterType(name);
            return;
        }
        this.Deduced[name] = (type, implied.Numeric);
    }

    private static (SqlType? Type, bool Numeric) CompareType(SqlType other, bool numeric, string operatorName) =>
        other is XmlSqlType
            ? (null, false)
            : operatorName is "equal to" or "not equal to"
                ? (other, numeric)
                : (Widen(other), numeric);

    /// <summary>
    /// What an arithmetic operator implies from its other operand: the widened
    /// type for a number; the widened string for <c>+</c> over a string and a
    /// float for the other operators; a binary widened for <c>+</c> and a
    /// 38-digit numeric otherwise; a datetime for <c>+</c> over a bit and a
    /// float otherwise; <c>datetime</c> / <c>smalldatetime</c> itself for
    /// <c>+</c> / <c>-</c>; and no valid type over the other temporal types,
    /// a uniqueidentifier, xml or a sql_variant.
    /// </summary>
    private static (SqlType? Type, bool Numeric) ArithmeticType(TypePairOperation operation, SqlType other)
    {
        var additive = operation is TypePairOperation.Add or TypePairOperation.Subtract;
        return other switch
        {
            // Concatenation widens even a MAX string to the sized form.
            VarcharSqlType or CharSqlType => operation == TypePairOperation.Add
                ? (VarcharSqlType.Get(8000, other.Collation!, Coercibility.CoercibleDefault), false)
                : (SqlType.Float, false),
            NVarcharSqlType or NCharSqlType => operation == TypePairOperation.Add
                ? (NVarcharSqlType.Get(4000, other.Collation!, Coercibility.CoercibleDefault), false)
                : (SqlType.Float, false),
            VarbinarySqlType or BinarySqlType =>
                operation == TypePairOperation.Add ? (Widen(other), false) : (SqlType.GetDecimal(38, 19), true),
            BitSqlType => operation == TypePairOperation.Add ? (SqlType.DateTime, false) : (SqlType.Float, false),
            DateTimeSqlType or SmallDateTimeSqlType => additive ? (other, false) : (null, false),
            DateSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType or UniqueIdentifierSqlType or XmlSqlType or SqlVariantSqlType => (null, false),
            DecimalSqlType => (Widen(other), true),
            _ => (other, false),
        };
    }

    /// <summary>
    /// A type widened the way an ordering comparison, an operator or a
    /// unification widens what it implies for a parameter.
    /// </summary>
    private static SqlType Widen(SqlType type) => type switch
    {
        VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { IsLob: true } => type,
        DecimalSqlType => SqlType.GetDecimal(38, 19),
        DateTime2SqlType => SqlType.GetDateTime2(7),
        TimeSqlType => SqlType.GetTime(7),
        DateTimeOffsetSqlType => SqlType.GetDateTimeOffset(7),
        VarcharSqlType or CharSqlType => VarcharSqlType.Get(8000, type.Collation!, Coercibility.CoercibleDefault),
        NVarcharSqlType or NCharSqlType => NVarcharSqlType.Get(4000, type.Collation!, Coercibility.CoercibleDefault),
        VarbinarySqlType or BinarySqlType => VarbinarySqlType.Get(8000),
        _ => type,
    };
}
