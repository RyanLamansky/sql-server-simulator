using System.Data;
using System.Data.SqlTypes;
using System.Runtime.CompilerServices;

namespace Microsoft.SqlServer.Server;

/// <summary>
/// One column of a result set a routine sends through <see cref="SqlPipe"/>,
/// with the constructors .NET Framework's <c>System.Data</c> offers for the
/// scalar types. The overloads taking a <c>System.Data.SqlClient.SortOrder</c>
/// are absent: that enum lives in the client assembly .NET no longer ships.
/// The constructors' refusals word their messages as the server's do (probed
/// 2026-09-28 against SQL Server 2025), and the constructors are never
/// inlined, so a stack trace names them.
/// </summary>
public sealed class SqlMetaData
{
    private const SqlCompareOptions DefaultCompareOptions = SqlCompareOptions.IgnoreCase | SqlCompareOptions.IgnoreKanaType | SqlCompareOptions.IgnoreWidth;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlMetaData(string name, SqlDbType dbType)
    {
        this.Name = CheckName(name);
        this.SqlDbType = dbType;
        switch (dbType)
        {
            case SqlDbType.BigInt: this.MaxLength = 8; this.Precision = 19; break;
            case SqlDbType.Bit: this.MaxLength = 1; this.Precision = 1; break;
            case SqlDbType.Date: this.MaxLength = 3; this.Precision = 10; break;
            case SqlDbType.DateTime: this.MaxLength = 8; this.Precision = 23; this.Scale = 3; break;
            case SqlDbType.DateTime2: this.MaxLength = 8; this.Precision = 27; this.Scale = 7; break;
            case SqlDbType.DateTimeOffset: this.MaxLength = 10; this.Precision = 34; this.Scale = 7; break;
            case SqlDbType.Decimal: this.MaxLength = 9; this.Precision = 18; break;
            case SqlDbType.Float: this.MaxLength = 8; this.Precision = 53; break;
            case SqlDbType.Image or SqlDbType.NText or SqlDbType.Text or SqlDbType.Xml: this.MaxLength = Max; break;
            case SqlDbType.Int: this.MaxLength = 4; this.Precision = 10; break;
            case SqlDbType.Money: this.MaxLength = 8; this.Precision = 19; this.Scale = 4; break;
            case SqlDbType.Real: this.MaxLength = 4; this.Precision = 24; break;
            case SqlDbType.SmallDateTime: this.MaxLength = 4; this.Precision = 16; break;
            case SqlDbType.SmallInt: this.MaxLength = 2; this.Precision = 5; break;
            case SqlDbType.SmallMoney: this.MaxLength = 4; this.Precision = 10; this.Scale = 4; break;
            case SqlDbType.Time: this.MaxLength = 5; this.Precision = 16; this.Scale = 7; break;
            case SqlDbType.Timestamp: this.MaxLength = 8; break;
            case SqlDbType.TinyInt: this.MaxLength = 1; this.Precision = 3; break;
            case SqlDbType.UniqueIdentifier: this.MaxLength = 16; break;
            case SqlDbType.Variant: this.MaxLength = 8016; break;
            default: throw InvalidForConstructor(dbType);
        }

        this.CompareOptions = IsCharacter(dbType) ? DefaultCompareOptions : SqlCompareOptions.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlMetaData(string name, SqlDbType dbType, long maxLength)
    {
        this.Name = CheckName(name);
        this.SqlDbType = dbType;
        switch (dbType)
        {
            case SqlDbType.Binary or SqlDbType.Char or SqlDbType.NChar:
                if (maxLength is < 1 or > 8000 || (dbType == SqlDbType.NChar && maxLength > 4000))
                    throw InvalidMaxLength(maxLength);
                break;
            case SqlDbType.VarBinary or SqlDbType.VarChar or SqlDbType.NVarChar:
                if (maxLength != Max && (maxLength is < 1 or > 8000 || (dbType == SqlDbType.NVarChar && maxLength > 4000)))
                    throw InvalidMaxLength(maxLength);
                break;
            case SqlDbType.Image or SqlDbType.NText or SqlDbType.Text:
                maxLength = Max;
                break;
            default:
                throw InvalidForConstructor(dbType);
        }

        this.MaxLength = maxLength;
        this.CompareOptions = IsCharacter(dbType) ? DefaultCompareOptions : SqlCompareOptions.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlMetaData(string name, SqlDbType dbType, long maxLength, long locale, SqlCompareOptions compareOptions)
        : this(name, dbType, maxLength)
    {
        if (!IsCharacter(dbType))
            throw InvalidForConstructor(dbType);
        this.LocaleId = locale;
        this.CompareOptions = compareOptions;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlMetaData(string name, SqlDbType dbType, byte precision, byte scale)
    {
        this.Name = CheckName(name);
        this.SqlDbType = dbType;
        switch (dbType)
        {
            case SqlDbType.Decimal:
                if (precision is < 1 or > 38 || scale > precision)
                    throw new ArgumentException($"Precision '{precision}' and scale '{scale}' are not valid for type {dbType}.");
                this.MaxLength = precision switch { <= 9 => 5, <= 19 => 9, <= 28 => 13, _ => 17 };
                this.Precision = precision;
                this.Scale = scale;
                break;
            case SqlDbType.Time or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset:
                if (scale > 7)
                    throw new ArgumentException($"Scale '{scale}' is not valid for type {dbType}.");
                this.Scale = scale;
                this.MaxLength = dbType switch { SqlDbType.Time => 5, SqlDbType.DateTime2 => 8, _ => 10 };
                break;
            default:
                throw InvalidForConstructor(dbType);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlMetaData(string name, SqlDbType dbType, long maxLength, byte precision, byte scale, long locale, SqlCompareOptions compareOptions, Type? userDefinedType)
    {
        if (userDefinedType is not null || dbType == SqlDbType.Udt)
            throw new NotSupportedException("User-defined type columns are not modeled by the simulator.");

        var shaped = dbType switch
        {
            SqlDbType.Decimal or SqlDbType.Time or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset => new SqlMetaData(name, dbType, precision, scale),
            SqlDbType.Binary or SqlDbType.Char or SqlDbType.NChar or SqlDbType.VarBinary or SqlDbType.VarChar or SqlDbType.NVarChar
                or SqlDbType.Image or SqlDbType.NText or SqlDbType.Text => new SqlMetaData(name, dbType, maxLength),
            _ => new SqlMetaData(name, dbType),
        };
        this.Name = shaped.Name;
        this.SqlDbType = dbType;
        this.MaxLength = shaped.MaxLength;
        this.Precision = shaped.Precision;
        this.Scale = shaped.Scale;
        this.LocaleId = IsCharacter(dbType) ? locale : 0;
        this.CompareOptions = IsCharacter(dbType) ? compareOptions : SqlCompareOptions.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlMetaData(string name, SqlDbType dbType, Type userDefinedType)
    {
        _ = name;
        _ = dbType;
        _ = userDefinedType;
        throw new NotSupportedException("User-defined type columns are not modeled by the simulator.");
    }

    /// <summary>The <see cref="MaxLength"/> of a <c>MAX</c> or large-object column.</summary>
    public static long Max => -1;

    public string Name { get; }

    public SqlDbType SqlDbType { get; }

    public long MaxLength { get; }

    public byte Precision { get; }

    public byte Scale { get; }

    public long LocaleId { get; }

    public SqlCompareOptions CompareOptions { get; }

    public Type? Type => null;

    public string TypeName => this.SqlDbType == SqlDbType.Variant ? "sql_variant" : this.SqlDbType.ToString().ToLowerInvariant();

    public DbType DbType => this.SqlDbType switch
    {
        SqlDbType.BigInt => DbType.Int64,
        SqlDbType.Binary or SqlDbType.Image or SqlDbType.Timestamp or SqlDbType.VarBinary => DbType.Binary,
        SqlDbType.Bit => DbType.Boolean,
        SqlDbType.Char or SqlDbType.Text or SqlDbType.VarChar => DbType.AnsiString,
        SqlDbType.Date => DbType.Date,
        SqlDbType.DateTime or SqlDbType.SmallDateTime => DbType.DateTime,
        SqlDbType.DateTime2 => DbType.DateTime2,
        SqlDbType.DateTimeOffset => DbType.DateTimeOffset,
        SqlDbType.Decimal => DbType.Decimal,
        SqlDbType.Float => DbType.Double,
        SqlDbType.Int => DbType.Int32,
        SqlDbType.Money or SqlDbType.SmallMoney => DbType.Currency,
        SqlDbType.NChar or SqlDbType.NText or SqlDbType.NVarChar => DbType.String,
        SqlDbType.Real => DbType.Single,
        SqlDbType.SmallInt => DbType.Int16,
        SqlDbType.Time => DbType.Time,
        SqlDbType.TinyInt => DbType.Byte,
        SqlDbType.UniqueIdentifier => DbType.Guid,
        SqlDbType.Xml => DbType.Xml,
        _ => DbType.Object,
    };

    private static bool IsCharacter(SqlDbType dbType) =>
        dbType is SqlDbType.Char or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.Text or SqlDbType.NText;

    private static string CheckName(string name) =>
        name is null
            ? throw new ArgumentNullException(nameof(name))
            : name.Length > 128
                ? throw new ArgumentException("Name cannot be longer than 128 characters.", nameof(name))
                : name;

    private static ArgumentException InvalidForConstructor(SqlDbType dbType) =>
        new($"The dbType {dbType} is invalid for this constructor.");

    private static ArgumentException InvalidMaxLength(long maxLength) =>
        new($"Specified length '{maxLength}' is out of range.", nameof(maxLength));
}
