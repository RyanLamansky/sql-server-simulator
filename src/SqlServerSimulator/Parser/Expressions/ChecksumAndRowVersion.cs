using System.Numerics;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>MIN_ACTIVE_ROWVERSION()</c>: returns the smallest active
/// rowversion value as <c>binary(8)</c>. The simulator doesn't track
/// per-transaction reservation of rowversion values, so this returns
/// the current next-to-be-allocated rowversion — a safe-over-approximation
/// of "the lowest value that any uncommitted transaction might still
/// commit at".
/// </summary>
internal sealed class MinActiveRowVersion : Expression
{
    private static readonly SqlType Binary8 = SqlType.GetBinary(8);

    public MinActiveRowVersion(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("min_active_rowversion", 0);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // The next value to be allocated, read without allocating it: two
        // calls answer alike (probed 2026-09-24 against SQL Server 2025).
        var current = runtime.Batch.CurrentDatabase.LastRowVersion + 1;
        var bytes = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            bytes[i] = (byte)(current & 0xff);
            current >>= 8;
        }
        return SqlValue.FromBinary(Binary8, bytes);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => Binary8;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "MIN_ACTIVE_ROWVERSION()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// SQL <c>CHECKSUM(arg1, arg2, ...)</c> and <c>BINARY_CHECKSUM</c>: an
/// <c>int</c> hash over the arguments, reproducing real's algorithm (probed
/// 2026-09-25 against SQL Server 2025). Each argument reduces to a 32-bit
/// value hash, and the result folds them in order as
/// <c>h = rotl(h, 4) ^ v</c> from zero; a string or binary value folds its own
/// units the same way. See <see cref="ValueHash"/> for the per-type
/// reductions, and <c>docs/claude/scalars.md</c> for the two families whose
/// bits don't match real.
/// </summary>
internal sealed class Checksum : Expression
{
    private readonly bool isBinary;
    private readonly Expression[] args;

    public Checksum(ParserContext context, bool isBinary)
    {
        this.isBinary = isBinary;
        if (context.Token is Tokens.Operator { Character: '*' })
        {
            // `CHECKSUM(*)` hashes every column of the FROM clause, in order,
            // and needs one (probed 2026-09-25 against SQL Server 2025).
            var sources = context.ScopeSources ?? throw SimulatedSqlException.MustSpecifyTableToSelectFrom();
            context.MoveNextRequired();
            if (context.Token is not Tokens.Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            List<Expression> columns = [];
            foreach (var source in sources)
            {
                foreach (var column in source.ColumnNames)
                    columns.Add(source.Qualifier is { } qualifier ? new Reference(qualifier, column) : new Reference(column));
            }
            this.args = [.. columns];
            // The expanded columns never parse as arguments, so the fold
            // bookkeeping has to hear from them here: a row hash is no constant.
            context.FoldableArguments = false;
            return;
        }
        var list = new List<Expression> { Parse(context) };
        while (context.Token is Tokens.Operator { Character: ',' })
            list.Add(Parse(context.MoveNextRequiredReturnSelf()));
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RejectBareNulls(list, isBinary);
        this.args = [.. list];
    }

    /// <summary>
    /// Real refuses a bare <c>NULL</c> argument while compiling:
    /// <c>CHECKSUM</c> names each one (Msg 8116 state 4, all reported
    /// together), while <c>BINARY_CHECKSUM</c> skips them and refuses only a
    /// call left with nothing to hash (Msg 8184). Probed against SQL Server 2025.
    /// </summary>
    private static void RejectBareNulls(List<Expression> arguments, bool isBinary)
    {
        if (isBinary)
        {
            if (arguments.TrueForAll(IsUntypedNullLiteral))
                throw SimulatedSqlException.NoComparableBinaryChecksumColumns();
            return;
        }

        List<SimulatedSqlException>? errors = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (IsUntypedNullLiteral(arguments[i]))
                (errors ??= []).Add(SimulatedSqlException.InvalidArgumentDataType("NULL", i + 1, "checksum", 4));
        }
        if (errors is not null)
            throw SimulatedSqlException.Aggregate(errors);
    }

    /// <summary>
    /// The types neither function can hash — the legacy LOBs, <c>xml</c> and
    /// the spatial pair. <c>CHECKSUM</c> refuses each while compiling (Msg
    /// 8116 state 4, a spatial type spelled <c>sys.geography</c>);
    /// <c>BINARY_CHECKSUM</c> passes over them and refuses only a call left
    /// with nothing to hash (Msg 8184). Probed 2026-09-25 against SQL Server
    /// 2025.
    /// </summary>
    private static bool IsUnhashable(SqlType type) => type.IsLob;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        List<SimulatedSqlException>? errors = null;
        var hashable = false;
        for (var i = 0; i < this.args.Length; i++)
        {
            var type = this.args[i].GetSqlType(batch, resolveColumnType);
            if (!IsUnhashable(type))
            {
                hashable |= !IsUntypedNullLiteral(this.args[i]);
                continue;
            }
            if (!this.isBinary)
                (errors ??= []).Add(SimulatedSqlException.InvalidArgumentDataType(type is SpatialSqlType ? $"sys.{type.SqlServerName}" : type.SqlServerName, i + 1, "checksum", 4));
        }
        if (errors is not null)
            throw SimulatedSqlException.Aggregate(errors);
        return hashable || !this.isBinary ? SqlType.Int32 : throw SimulatedSqlException.NoComparableBinaryChecksumColumns();
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var hash = 0u;
        foreach (var argument in this.args)
        {
            var value = argument.Run(runtime);
            if (!IsUnhashable(value.Type))
                hash = BitOperations.RotateLeft(hash, 4) ^ ValueHash(value, this.isBinary);
        }
        return SqlValue.FromInt32((int)hash);
    }

    /// <summary>
    /// One argument's 32-bit hash, as real reduces each type (every rule
    /// probed 2026-09-25 against SQL Server 2025). A NULL of any type is
    /// <c>0x7FFFFFFF</c>. An integer is its own value (<c>smallint</c>
    /// unsigned), and every 8-byte value folds its two halves with XOR: a
    /// <c>bigint</c>, a <c>float</c>'s bits, <c>money</c>'s scaled units, a
    /// <c>time</c>'s 100-ns ticks. The dates count days from 1900-01-01:
    /// <c>date</c> alone, <c>datetime</c> XOR its 1/300-second units,
    /// <c>smalldatetime</c> in the high half over its minutes, <c>datetime2</c>
    /// XOR its time's hash, and <c>datetimeoffset</c> the same at its UTC
    /// instant. A <c>uniqueidentifier</c> folds its sixteen stored bytes, a
    /// binary its bytes up to the trailing zeros, and a string its characters
    /// up to the trailing spaces — a <c>varchar</c>'s as signed code-page
    /// bytes, an <c>nvarchar</c>'s as UTF-16 units. <c>CHECKSUM</c> folds a
    /// string's collation sort weights instead, which are reproduced for
    /// <c>varchar</c> under <c>SQL_Latin1_General_CP1_CI_AS</c>. A
    /// <c>sql_variant</c> hashes its base value (an exact number as a
    /// <c>decimal</c>, an approximate one as a <c>float</c>) XOR its family:
    /// 1 <c>uniqueidentifier</c>, 2 binary, 3 string, 4 exact, 5 approximate,
    /// 7 date and time.
    /// </summary>
    private static uint ValueHash(SqlValue value, bool isBinary)
    {
        if (value.IsNull)
            return 0x7FFFFFFF;
        var type = value.Type;
        return type switch
        {
            BitSqlType => value.AsBoolean ? 1u : 0u,
            TinyIntSqlType => value.AsByte,
            SmallIntSqlType => (ushort)value.AsInt16,
            Int32SqlType => (uint)value.AsInt32,
            BigIntSqlType => Fold(value.AsInt64),
            RealSqlType => BitConverter.SingleToUInt32Bits(value.AsSingle),
            FloatSqlType => Fold(BitConverter.DoubleToInt64Bits(value.AsDouble)),
            SmallMoneySqlType => (uint)(int)value.AsMoneyScaledUnits,
            MoneySqlType => Fold(value.AsMoneyScaledUnits),
            DecimalSqlType => DecimalHash(value.AsDecimal38),
            DateSqlType => (uint)DaysFrom1900(value.AsDate),
            DateTimeSqlType => DateTimeHash(value.AsDateTime),
            SmallDateTimeSqlType => ((uint)DaysFrom1900(DateOnly.FromDateTime(value.AsSmallDateTime)) << 16) | (uint)(value.AsSmallDateTime.TimeOfDay.Ticks / TimeSpan.TicksPerMinute),
            TimeSqlType => Fold(value.AsTime.Ticks),
            DateTime2SqlType => (uint)DaysFrom1900(DateOnly.FromDateTime(value.AsDateTime2)) ^ Fold(value.AsDateTime2.TimeOfDay.Ticks),
            DateTimeOffsetSqlType => (uint)DaysFrom1900(DateOnly.FromDateTime(value.AsDateTimeOffset.UtcDateTime)) ^ Fold(value.AsDateTimeOffset.UtcDateTime.TimeOfDay.Ticks),
            UniqueIdentifierSqlType => FoldBytes(value.AsGuid.ToByteArray()),
            BinarySqlType or VarbinarySqlType or RowVersionSqlType => FoldBytes(WithoutTrailingZeros(value.AsBytes)),
            HierarchyIdSqlType => FoldBytes(WithoutTrailingZeros(value.AsHierarchyIdBytes)),
            SqlVariantSqlType => VariantHash(value.AsVariantInner, isBinary),
            _ => StringHash(value, isBinary),
        };
    }

    private static uint VariantHash(SqlValue inner, bool isBinary) => inner.Type.Category switch
    {
        SqlTypeCategory.Integer => DecimalHash(Decimal38.FromParts((UInt128)(ulong)Math.Abs(MathScalars.AsLong(inner)), false, 0)) ^ 4,
        SqlTypeCategory.Decimal => DecimalHash(inner.AsDecimal38) ^ 4,
        SqlTypeCategory.Money => DecimalHash(inner.AsMoneyDecimal38) ^ 4,
        SqlTypeCategory.Approximate => Fold(BitConverter.DoubleToInt64Bits(inner.Type == SqlType.Real ? inner.AsSingle : inner.AsDouble)) ^ 5,
        SqlTypeCategory.DateTime => ValueHash(inner, isBinary) ^ 7,
        SqlTypeCategory.UniqueIdentifier => ValueHash(inner, isBinary) ^ 1,
        SqlTypeCategory.String => ValueHash(inner, isBinary) ^ 3,
        _ => ValueHash(inner, isBinary) ^ 2,
    };

    /// <summary>
    /// A stand-in for real's <c>decimal</c> hash, whose bits aren't
    /// reproduced: like real's, it ignores the sign, the declared precision
    /// and scale and every trailing zero (<c>1.5</c>, <c>1.50</c> and
    /// <c>150</c> hash alike), so equal values still hash equal.
    /// </summary>
    private static uint DecimalHash(Decimal38 value)
    {
        var digits = value.Abs().ToString().Replace(".", "", StringComparison.Ordinal).Trim('0');
        var hash = 0u;
        foreach (var digit in digits)
            hash = BitOperations.RotateLeft(hash, 4) ^ digit;
        return hash;
    }

    private static uint StringHash(SqlValue value, bool isBinary)
    {
        var text = value.AsString.TrimEnd(' ');
        var collation = value.Type.Collation ?? Collation.Baseline;
        var national = SqlType.IsNationalStringCategory(value.Type);
        var hash = 0u;
        if (!national && (isBinary || collation is Collation.SqlLatin1Cp1CiAsCollation))
        {
            foreach (var b in collation.StorageEncoding.GetBytes(text))
                hash = BitOperations.RotateLeft(hash, 4) ^ (isBinary ? (uint)(sbyte)b : Cp1252CiAsChecksumWeight[b]);
            return hash;
        }
        // An nvarchar's sort weights aren't reproduced; folding the case-folded
        // characters keeps strings the collation calls equal hashing alike.
        if (!isBinary && !collation.CaseSensitive)
            text = text.ToUpperInvariant();
        foreach (var unit in text)
            hash = BitOperations.RotateLeft(hash, 4) ^ unit;
        return hash;
    }

    /// <summary>
    /// The per-byte sort weight <c>CHECKSUM</c> folds for a <c>varchar</c>
    /// under <c>SQL_Latin1_General_CP1_CI_AS</c>: the primary weight in the low
    /// byte (a letter's case folded) and the accent in the high one, so
    /// <c>'a'</c> is 142, <c>'A'</c> 142 and <c>'é'</c> <c>0x392</c> — read off
    /// <c>CHECKSUM(CHAR(n))</c> for every byte on SQL Server 2025, 2026-09-25,
    /// save the space, which that call trims away and <c>CHECKSUM(' x')</c>
    /// shows to weigh 32.
    /// </summary>
    private static ReadOnlySpan<ushort> Cp1252CiAsChecksumWeight =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
        132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 48, 49, 50, 51, 52, 53,
        54, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156,
        157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 55, 56, 57, 58, 59,
        60, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156,
        157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 61, 62, 63, 64, 65,
        66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
        82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
        98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
        114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129,
        654, 910, 1166, 1422, 1678, 1934, 6514, 400, 658, 914, 1170, 1426, 406, 662, 918, 1174,
        168, 411, 412, 668, 924, 1180, 1436, 130, 1692, 418, 674, 930, 1186, 422, 169, 7072,
        654, 910, 1166, 1422, 1678, 1934, 6514, 400, 658, 914, 1170, 1426, 406, 662, 918, 1174,
        168, 411, 412, 668, 924, 1180, 1436, 131, 1692, 418, 674, 930, 1186, 422, 169, 678,
    ];

    private static uint Fold(long value) => (uint)(value >> 32) ^ (uint)value;

    private static int DaysFrom1900(DateOnly date) => date.DayNumber - Epoch1900.DayNumber;

    private static readonly DateOnly Epoch1900 = new(1900, 1, 1);

    private static uint DateTimeHash(DateTime value) =>
        (uint)DaysFrom1900(DateOnly.FromDateTime(value)) ^ (uint)DateTimeSqlType.UnitsFromTicks(value.TimeOfDay.Ticks);

    private static uint FoldBytes(ReadOnlySpan<byte> bytes)
    {
        var hash = 0u;
        foreach (var b in bytes)
            hash = BitOperations.RotateLeft(hash, 4) ^ b;
        return hash;
    }

    private static ReadOnlySpan<byte> WithoutTrailingZeros(byte[] bytes)
    {
        var length = bytes.Length;
        while (length > 0 && bytes[length - 1] == 0)
            length--;
        return bytes.AsSpan(0, length);
    }

    internal override string DebugDisplay() => $"{(this.isBinary ? "BINARY_CHECKSUM" : "CHECKSUM")}(...{this.args.Length} args)";

    internal override void Describe(NodeShape shape) => shape.Local(this.isBinary).Children(this.args);
}
