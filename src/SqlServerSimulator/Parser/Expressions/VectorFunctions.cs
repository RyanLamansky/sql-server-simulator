using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The shared argument handling of SQL Server 2025's vector scalars
/// (<c>VECTOR_DISTANCE</c>, <c>VECTOR_NORM</c>, <c>VECTOR_NORMALIZE</c>,
/// <c>VECTORPROPERTY</c>): a vector argument takes a vector alone, and a
/// metric / norm / property name a character string alone, each refusal Msg
/// 8116 naming the argument (probed 2026-09-26 against SQL Server 2025).
/// </summary>
internal static class VectorArguments
{
    /// <summary>
    /// Reads a call's comma-separated argument list with the cursor on its
    /// first argument, leaving the cursor on the closing parenthesis. The
    /// count was already settled by <see cref="BuiltInArity"/>.
    /// </summary>
    public static Expression[] Parse(ParserContext context, int count)
    {
        var arguments = new Expression[count];
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                if (context.Token is not Tokens.Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            arguments[i] = Expression.Parse(context);
        }
        return context.Token is Tokens.Operator { Character: ')' } ? arguments : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>The type of a vector argument, or null for an untyped <c>NULL</c>; Msg 8116 for anything else.</summary>
    public static VectorSqlType? RequireVector(Expression argument, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType, string functionName, int argumentIndex)
    {
        var type = argument.GetSqlType(batch, resolveColumnType);
        return type is VectorSqlType vector ? vector
            : Expression.IsUntypedNullLiteral(argument) ? null
            : throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, argument), argumentIndex, functionName);
    }

    /// <summary>
    /// Msg 8116 when a built-in outside this family receives a vector, at the
    /// state real gives that built-in (probed 2026-09-26 against SQL Server
    /// 2025).
    /// </summary>
    public static void RejectVector(SqlType type, string functionName, int argumentIndex, byte state)
    {
        if (type is VectorSqlType)
            throw SimulatedSqlException.InvalidArgumentDataType(type.SqlServerName, argumentIndex, functionName, state);
    }

    /// <summary>Msg 8116 unless a name argument is a character string or an untyped <c>NULL</c>.</summary>
    public static void RequireName(Expression argument, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType, string functionName, int argumentIndex)
    {
        var type = argument.GetSqlType(batch, resolveColumnType);
        if (!SqlType.IsCollatedString(type) && !Expression.IsUntypedNullLiteral(argument))
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, argument), argumentIndex, functionName);
    }

    /// <summary>
    /// Real's distance kernel, reverse-engineered from cancellation probes and
    /// fitted to probed results (probed 2026-09-26 against SQL Server 2025):
    /// eight float32 lanes with fused multiply-adds, <paramref name="accumulators"/>
    /// sets of them taking successive blocks of eight in turn and then combined
    /// pairwise, the remaining whole blocks of eight added to the combined
    /// lanes, and then one of two finishes. The block finish adds the last
    /// partial block into the low lanes and halves the lanes into each other
    /// (lane i with lane i + 4, then + 2, then + 1); the scalar finish, which
    /// only the cosine's cross product takes, sums the lanes in order and
    /// multiply-adds the leftover elements onto that. The term is
    /// <c>x·y</c>, or <c>(x − y)²</c> with the difference rounded first.
    /// </summary>
    public static float FusedSum(float[] x, float[] y, int accumulators, bool difference, bool scalarFinish = false)
    {
        const int Lanes = 8;
        System.Diagnostics.Debug.Assert(accumulators is 1 or 2 or 4, "The accumulator sets combine pairwise.");
        Span<float> sets = stackalloc float[Lanes * 4];
        var lanes = sets[..(Lanes * accumulators)];
        lanes.Clear();
        var n = x.Length;
        var i = 0;
        for (; i + (Lanes * accumulators) <= n; i += Lanes * accumulators)
        {
            for (var j = 0; j < Lanes * accumulators; j++)
                lanes[j] = Term(x[i + j], y[i + j], lanes[j], difference);
        }
        for (var width = accumulators; width > 1; width /= 2)
        {
            for (var j = 0; j < Lanes * (width / 2); j++)
            {
                var (set, lane) = (j / Lanes, j % Lanes);
                lanes[j] = lanes[(2 * set * Lanes) + lane] + lanes[(((2 * set) + 1) * Lanes) + lane];
            }
        }
        var acc = lanes[..Lanes];
        for (; i + Lanes <= n; i += Lanes)
        {
            for (var j = 0; j < Lanes; j++)
                acc[j] = Term(x[i + j], y[i + j], acc[j], difference);
        }
        if (scalarFinish)
        {
            var sum = 0f;
            foreach (var lane in acc)
                sum += lane;
            for (; i < n; i++)
                sum = Term(x[i], y[i], sum, difference);
            return sum;
        }
        for (var j = 0; i + j < n; j++)
            acc[j] = Term(x[i + j], y[i + j], acc[j], difference);
        for (var width = Lanes / 2; width >= 1; width /= 2)
        {
            for (var j = 0; j < width; j++)
                acc[j] += acc[j + width];
        }
        return acc[0];
    }

    private static float Term(float x, float y, float accumulated, bool difference)
    {
        if (!difference)
            return MathF.FusedMultiplyAdd(x, y, accumulated);
        var d = x - y;
        return MathF.FusedMultiplyAdd(d, d, accumulated);
    }

    /// <summary>
    /// The position of <paramref name="name"/> among <paramref name="choices"/>,
    /// matched case-insensitively with trailing spaces set aside as a string
    /// comparison sets them aside (a <c>char(10)</c> <c>'dot'</c> names the
    /// metric, a leading space doesn't), or -1.
    /// </summary>
    public static int Choice(string name, string[] choices)
    {
        var trimmed = name.TrimEnd(' ');
        return Array.FindIndex(choices, choice => string.Equals(choice, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// <c>VECTOR_DISTANCE(metric, a, b)</c>: the <c>cosine</c>, <c>euclidean</c>
/// or <c>dot</c> distance between two vectors of one dimension count, as
/// <c>float</c>. Real computes in float32 and widens the result, so the value
/// carries single precision's digits: cosine is one less the dot product over
/// the product of the two norms (1 when either vector is all zeros), dot is
/// the negated dot product, and a sum that overflows float32 is Msg 8115
/// (probed 2026-09-26 against SQL Server 2025).
/// </summary>
internal sealed class VectorDistance : Expression
{
    private static readonly string[] Metrics = ["cosine", "dot", "euclidean"];

    private readonly Expression metric;
    private readonly Expression first;
    private readonly Expression second;

    public VectorDistance(ParserContext context)
    {
        var arguments = VectorArguments.Parse(context, 3);
        (this.metric, this.first, this.second) = (arguments[0], arguments[1], arguments[2]);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        VectorArguments.RequireName(this.metric, batch, resolveColumnType, "vector_distance", 1);
        var a = VectorArguments.RequireVector(this.first, batch, resolveColumnType, "vector_distance", 2);
        var b = VectorArguments.RequireVector(this.second, batch, resolveColumnType, "vector_distance", 3);
        return a is not null && b is not null && a != b
            ? throw SimulatedSqlException.VectorDimensionsMismatch(a.dimensions, b.dimensions, 3)
            : SqlType.Float;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var metric = this.metric.Run(runtime);
        var first = this.first.Run(runtime);
        var second = this.second.Run(runtime);
        if (metric.IsNull || first.IsNull || second.IsNull)
            return SqlValue.Null(SqlType.Float);
        var name = metric.AsString;
        var a = VectorSqlType.Elements(first.AsVectorBytes);
        var b = VectorSqlType.Elements(second.AsVectorBytes);
        if (a.Length != b.Length)
            throw SimulatedSqlException.VectorDimensionsMismatch(a.Length, b.Length, 3);
        var distance = VectorArguments.Choice(name, Metrics) switch
        {
            0 => Cosine(a, b),
            1 => -Dot(a, b),
            2 => Euclidean(a, b),
            _ => throw SimulatedSqlException.VectorDistanceMetricNotSupported(name),
        };
        return float.IsInfinity(distance) ? throw SimulatedSqlException.ArithmeticOverflow("float") : SqlValue.FromDouble(distance);
    }

    private static float Dot(float[] a, float[] b) => VectorArguments.FusedSum(a, b, accumulators: 4, difference: false);

    private static float Euclidean(float[] a, float[] b) => MathF.Sqrt(VectorArguments.FusedSum(a, b, accumulators: 4, difference: true));

    /// <summary>
    /// One less the similarity, whose cross product real sums with two
    /// accumulator sets and the scalar finish, and whose norms as the dot
    /// kernel does. The
    /// similarity is clamped to [-1, 1] with an undefined one (both norms
    /// overflowing) read as 1, which is how real answers 0 for two vectors
    /// whose norms overflow; a zero vector is at distance 1 from anything.
    /// </summary>
    private static float Cosine(float[] a, float[] b)
    {
        var aa = VectorArguments.FusedSum(a, a, accumulators: 4, difference: false);
        var bb = VectorArguments.FusedSum(b, b, accumulators: 4, difference: false);
        if (aa == 0 || bb == 0)
            return 1;
        var similarity = VectorArguments.FusedSum(a, b, accumulators: 2, difference: false, scalarFinish: true) / (MathF.Sqrt(aa) * MathF.Sqrt(bb));
        similarity = float.IsNaN(similarity) ? 1 : Math.Clamp(similarity, -1f, 1f);
        return 1 - similarity;
    }

    internal override string DebugDisplay() => $"VECTOR_DISTANCE({this.metric.DebugDisplay()}, {this.first.DebugDisplay()}, {this.second.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.metric).Child(this.first).Child(this.second);
}

/// <summary>
/// <c>VECTOR_NORM(v, norm)</c> and <c>VECTOR_NORMALIZE(v, norm)</c> over the
/// <c>norm1</c>, <c>norm2</c> and <c>norminf</c> norms. <c>VECTOR_NORM</c>
/// answers in double precision over the float32 elements, where
/// <c>VECTOR_NORMALIZE</c> divides by a norm it sums in float32 — so a vector
/// whose norm overflows single precision normalizes to zeros, as does one
/// whose norm is zero (probed 2026-09-26 against SQL Server 2025).
/// </summary>
internal sealed class VectorNorm : Expression
{
    private static readonly string[] Norms = ["norm1", "norm2", "norminf"];

    private readonly Expression vector;
    private readonly Expression norm;
    private readonly bool normalize;

    public VectorNorm(ParserContext context, bool normalize)
    {
        var arguments = VectorArguments.Parse(context, 2);
        (this.vector, this.norm) = (arguments[0], arguments[1]);
        this.normalize = normalize;
    }

    private string FunctionName => this.normalize ? "vector_normalize" : "vector_norm";

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = VectorArguments.RequireVector(this.vector, batch, resolveColumnType, this.FunctionName, 1);
        VectorArguments.RequireName(this.norm, batch, resolveColumnType, this.FunctionName, 2);
        // A bare NULL normalizes to a NULL vector; the simulator has to give
        // it some dimension count, and gives it one.
        return !this.normalize ? SqlType.Float : type ?? VectorSqlType.Get(1);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.vector.Run(runtime);
        var norm = this.norm.Run(runtime);
        if (value.IsNull || norm.IsNull)
            return SqlValue.Null(!this.normalize ? SqlType.Float : value.Type is VectorSqlType ? value.Type : VectorSqlType.Get(1));
        var name = norm.AsString;
        var kind = VectorArguments.Choice(name, Norms) switch
        {
            0 => 1,
            1 => 2,
            2 => 0,
            _ => throw SimulatedSqlException.VectorNormNotSupported(name),
        };
        var elements = VectorSqlType.Elements(value.AsVectorBytes);
        return this.normalize ? Normalize((VectorSqlType)value.Type, elements, kind) : SqlValue.FromDouble(DoubleNorm(elements, kind));
    }

    /// <summary>
    /// Real's norm kernel: eight float32 lanes, each summing every eighth
    /// magnitude (or its float32 square) without fused multiplies, the lanes
    /// then summed in double precision in order and the elements past the
    /// last full block of eight added exactly in double.
    /// </summary>
    private static double DoubleNorm(float[] elements, int kind)
    {
        if (kind == 0)
        {
            var largest = 0f;
            foreach (var element in elements)
                largest = MathF.Max(largest, MathF.Abs(element));
            return largest;
        }
        Span<float> lanes = stackalloc float[8];
        lanes.Clear();
        var blocked = elements.Length - (elements.Length % 8);
        for (var i = 0; i < blocked; i++)
            lanes[i % 8] += kind == 1 ? MathF.Abs(elements[i]) : elements[i] * elements[i];
        var sum = 0.0;
        foreach (var lane in lanes)
            sum += lane;
        for (var i = blocked; i < elements.Length; i++)
            sum += kind == 1 ? Math.Abs((double)elements[i]) : (double)elements[i] * elements[i];
        return kind == 2 ? Math.Sqrt(sum) : sum;
    }

    /// <summary>
    /// Each element over the norm narrowed to float32, so a norm past
    /// float32's range divides everything to zero; a zero norm leaves zeros.
    /// </summary>
    private static SqlValue Normalize(VectorSqlType type, float[] elements, int kind)
    {
        var norm = (float)DoubleNorm(elements, kind);
        var normalized = new float[elements.Length];
        if (norm != 0)
        {
            for (var i = 0; i < elements.Length; i++)
                normalized[i] = elements[i] / norm;
        }
        return SqlValue.FromVector(type, VectorSqlType.ToBytes(normalized, type.dimensions));
    }

    internal override string DebugDisplay() => $"{this.FunctionName.ToUpperInvariant()}({this.vector.DebugDisplay()}, {this.norm.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.normalize).Child(this.vector).Child(this.norm);
}

/// <summary>
/// <c>VECTORPROPERTY(v, property)</c>: <c>Dimensions</c> as a
/// <c>smallint</c> and <c>BaseType</c> as the <c>nvarchar</c>
/// <c>float32</c>, both inside a <c>sql_variant</c>; any other property
/// name, matched case-insensitively, is NULL (probed 2026-09-26 against SQL
/// Server 2025).
/// </summary>
internal sealed class VectorProperty : Expression
{
    private static readonly string[] Properties = ["BaseType", "Dimensions"];

    private readonly Expression vector;
    private readonly Expression property;

    public VectorProperty(ParserContext context)
    {
        var arguments = VectorArguments.Parse(context, 2);
        (this.vector, this.property) = (arguments[0], arguments[1]);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // A bare NULL is refused as the varbinary real reads it as, at state
        // 36 (probed 2026-09-26 against SQL Server 2025).
        if (IsUntypedNullLiteral(this.vector))
            throw SimulatedSqlException.InvalidArgumentDataType("varbinary", 1, "vectorproperty", 36);
        _ = VectorArguments.RequireVector(this.vector, batch, resolveColumnType, "vectorproperty", 1);
        VectorArguments.RequireName(this.property, batch, resolveColumnType, "vectorproperty", 2);
        return SqlType.SqlVariant;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.vector.Run(runtime);
        var property = this.property.Run(runtime);
        return value.IsNull || property.IsNull ? SqlValue.Null(SqlType.SqlVariant)
            : VectorArguments.Choice(property.AsString, Properties) switch
            {
                0 => SqlValue.FromVariant(SqlValue.FromNVarchar(NVarcharSqlType.Get(128, runtime.Batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault), "float32")),
                1 => SqlValue.FromVariant(SqlValue.FromInt16((short)((VectorSqlType)value.Type).dimensions)),
                _ => SqlValue.Null(SqlType.SqlVariant),
            };
    }

    internal override string DebugDisplay() => $"VECTORPROPERTY({this.vector.DebugDisplay()}, {this.property.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.vector).Child(this.property);
}
