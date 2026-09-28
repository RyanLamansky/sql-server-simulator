using System.Collections;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable IDE0130 // The namespace is .NET Framework's own, which a SQLCLR assembly's references name.

namespace System.Data.SqlClient;

/// <summary>
/// A parameter of a context-connection command. An untyped parameter takes
/// its type from its value when the command runs — a string as
/// <c>nvarchar</c> of its own length, a <see cref="decimal"/> as
/// <c>numeric</c> of its own precision and scale — and a sized one cuts a
/// longer string or binary value to its size, as the provider does before
/// sending it (probed 2026-09-28 against SQL Server 2025).
/// </summary>
public sealed class SqlParameter : DbParameter, ICloneable
{
    private string parameterName = "";
    private SqlDbType? sqlDbType;
    private object? value;
    private object? outputSqlValue;
    private bool hasOutput;
    private string sourceColumn = "";

    public SqlParameter()
    {
    }

    public SqlParameter(string? parameterName, SqlDbType dbType)
    {
        this.ParameterName = parameterName;
        this.SqlDbType = dbType;
    }

    public SqlParameter(string? parameterName, object? value)
    {
        this.ParameterName = parameterName;
        this.value = value;
    }

    public SqlParameter(string? parameterName, SqlDbType dbType, int size)
        : this(parameterName, dbType) => this.Size = size;

    public SqlParameter(string? parameterName, SqlDbType dbType, int size, string? sourceColumn)
        : this(parameterName, dbType, size) => this.SourceColumn = sourceColumn;

    public override DbType DbType
    {
        get => ToDbType(this.SqlDbType);
        set => this.SqlDbType = FromDbType(value);
    }

    public SqlDbType SqlDbType
    {
        get => this.sqlDbType ?? Infer(this.value);
        set => this.sqlDbType = value;
    }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => this.parameterName;
        set => this.parameterName = value ?? "";
    }

    public override byte Precision { get; set; }

    public override byte Scale { get; set; }

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn
    {
        get => this.sourceColumn;
        set => this.sourceColumn = value ?? "";
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    public override object? Value
    {
        get => this.hasOutput ? ToClrValue(this.outputSqlValue) : this.value;
        set
        {
            this.value = value;
            this.hasOutput = false;
        }
    }

    public object? SqlValue
    {
        get => this.hasOutput ? this.outputSqlValue : this.value;
        set => this.Value = value;
    }

    public string TypeName { get; set; } = "";

    public string UdtTypeName { get; set; } = "";

    public int LocaleId { get; set; }

    public SqlCompareOptions CompareInfo { get; set; }

    public int Offset { get; set; }

    /// <summary>What the command sends: the parameter's name with its <c>@</c>, its type, and its value.</summary>
    internal (string Name, SqlDbType Type, bool Typed, int Size, byte Precision, byte Scale, ParameterDirection Direction, object? Value) Describe() =>
        (this.parameterName.StartsWith('@') ? this.parameterName : "@" + this.parameterName,
            this.SqlDbType, this.sqlDbType is not null, this.Size, this.Precision, this.Scale, this.Direction,
            this.hasOutput ? this.outputSqlValue : this.value);

    /// <summary>Takes the value an output, input-output or return-value parameter came back with, in its SqlTypes form.</summary>
    internal void SetOutput(object? sqlValue)
    {
        this.outputSqlValue = sqlValue;
        this.hasOutput = true;
    }

    public override void ResetDbType() => this.sqlDbType = null;

    public void ResetSqlDbType() => this.sqlDbType = null;

    public object Clone() => this.MemberwiseClone();

    public override string ToString() => this.parameterName;

    /// <summary>The plain-CLR form of a value in its SqlTypes form, <see cref="DBNull"/> for NULL.</summary>
    internal static object ToClrValue(object? value) => value switch
    {
        null => DBNull.Value,
        INullable { IsNull: true } => DBNull.Value,
        SqlXml xml => xml.Value,
        SqlBinary binary => binary.Value,
        SqlBoolean boolean => boolean.Value,
        SqlByte number => number.Value,
        SqlDateTime moment => moment.Value,
        SqlDecimal number => number.Value,
        SqlDouble number => number.Value,
        SqlGuid guid => guid.Value,
        SqlInt16 number => number.Value,
        SqlInt32 number => number.Value,
        SqlInt64 number => number.Value,
        SqlMoney money => money.Value,
        SqlSingle number => number.Value,
        SqlString text => text.Value,
        _ => value,
    };

    private static SqlDbType Infer(object? value) => value switch
    {
        int or SqlInt32 => SqlDbType.Int,
        long or SqlInt64 => SqlDbType.BigInt,
        short or SqlInt16 => SqlDbType.SmallInt,
        byte or SqlByte => SqlDbType.TinyInt,
        bool or SqlBoolean => SqlDbType.Bit,
        double or SqlDouble => SqlDbType.Float,
        float or SqlSingle => SqlDbType.Real,
        decimal or SqlDecimal => SqlDbType.Decimal,
        SqlMoney => SqlDbType.Money,
        DateTime or SqlDateTime => SqlDbType.DateTime,
        DateTimeOffset => SqlDbType.DateTimeOffset,
        TimeSpan => SqlDbType.Time,
        Guid or SqlGuid => SqlDbType.UniqueIdentifier,
        byte[] or SqlBinary or SqlBytes => SqlDbType.VarBinary,
        SqlXml => SqlDbType.Xml,
        _ => SqlDbType.NVarChar,
    };

    private static DbType ToDbType(SqlDbType type) => type switch
    {
        SqlDbType.BigInt => DbType.Int64,
        SqlDbType.Binary or SqlDbType.Image or SqlDbType.Timestamp or SqlDbType.VarBinary => DbType.Binary,
        SqlDbType.Bit => DbType.Boolean,
        SqlDbType.Char => DbType.AnsiStringFixedLength,
        SqlDbType.Date => DbType.Date,
        SqlDbType.DateTime or SqlDbType.SmallDateTime => DbType.DateTime,
        SqlDbType.DateTime2 => DbType.DateTime2,
        SqlDbType.DateTimeOffset => DbType.DateTimeOffset,
        SqlDbType.Decimal => DbType.Decimal,
        SqlDbType.Float => DbType.Double,
        SqlDbType.Int => DbType.Int32,
        SqlDbType.Money or SqlDbType.SmallMoney => DbType.Currency,
        SqlDbType.NChar => DbType.StringFixedLength,
        SqlDbType.NText or SqlDbType.NVarChar => DbType.String,
        SqlDbType.Real => DbType.Single,
        SqlDbType.SmallInt => DbType.Int16,
        SqlDbType.Text or SqlDbType.VarChar => DbType.AnsiString,
        SqlDbType.Time => DbType.Time,
        SqlDbType.TinyInt => DbType.Byte,
        SqlDbType.UniqueIdentifier => DbType.Guid,
        SqlDbType.Xml => DbType.Xml,
        _ => DbType.Object,
    };

    private static SqlDbType FromDbType(DbType type) => type switch
    {
        DbType.AnsiString => SqlDbType.VarChar,
        DbType.AnsiStringFixedLength => SqlDbType.Char,
        DbType.Binary => SqlDbType.VarBinary,
        DbType.Boolean => SqlDbType.Bit,
        DbType.Byte => SqlDbType.TinyInt,
        DbType.Currency => SqlDbType.Money,
        DbType.Date => SqlDbType.Date,
        DbType.DateTime => SqlDbType.DateTime,
        DbType.DateTime2 => SqlDbType.DateTime2,
        DbType.DateTimeOffset => SqlDbType.DateTimeOffset,
        DbType.Decimal => SqlDbType.Decimal,
        DbType.Double => SqlDbType.Float,
        DbType.Guid => SqlDbType.UniqueIdentifier,
        DbType.Int16 => SqlDbType.SmallInt,
        DbType.Int32 => SqlDbType.Int,
        DbType.Int64 => SqlDbType.BigInt,
        DbType.Object => SqlDbType.Variant,
        DbType.Single => SqlDbType.Real,
        DbType.String => SqlDbType.NVarChar,
        DbType.StringFixedLength => SqlDbType.NChar,
        DbType.Time => SqlDbType.Time,
        DbType.Xml => SqlDbType.Xml,
        _ => throw new ArgumentException($"No mapping exists from DbType {type} to a known SqlDbType."),
    };
}

/// <summary>The parameters of a <see cref="SqlCommand"/>.</summary>
public sealed class SqlParameterCollection : DbParameterCollection
{
    private readonly List<SqlParameter> items = [];

    internal SqlParameterCollection()
    {
    }

    public override int Count => this.items.Count;

    public override object SyncRoot => this.items;

    public new SqlParameter this[int index]
    {
        get => this.items[index];
        set => this.items[index] = value;
    }

    public new SqlParameter this[string parameterName]
    {
        get => this.items[this.RequireIndex(parameterName)];
        set => this.items[this.RequireIndex(parameterName)] = value;
    }

    public SqlParameter Add(SqlParameter value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.items.Add(value);
        return value;
    }

    public override int Add(object value)
    {
        _ = this.Add(Require(value));
        return this.items.Count - 1;
    }

    public SqlParameter Add(string parameterName, SqlDbType sqlDbType) => this.Add(new SqlParameter(parameterName, sqlDbType));

    public SqlParameter Add(string parameterName, SqlDbType sqlDbType, int size) => this.Add(new SqlParameter(parameterName, sqlDbType, size));

    public SqlParameter Add(string parameterName, SqlDbType sqlDbType, int size, string sourceColumn) => this.Add(new SqlParameter(parameterName, sqlDbType, size, sourceColumn));

    public SqlParameter Add(string parameterName, object value) => this.Add(new SqlParameter(parameterName, value));

    public SqlParameter AddWithValue(string parameterName, object value) => this.Add(new SqlParameter(parameterName, value));

    public void AddRange(SqlParameter[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
            _ = this.Add(value);
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
            _ = this.Add(Require(value));
    }

    public override void Clear() => this.items.Clear();

    public override bool Contains(object value) => value is SqlParameter parameter && this.items.Contains(parameter);

    public override bool Contains(string value) => this.IndexOf(value) >= 0;

    public bool Contains(SqlParameter value) => this.items.Contains(value);

    public override void CopyTo(Array array, int index) => ((ICollection)this.items).CopyTo(array, index);

    public void CopyTo(SqlParameter[] array, int index) => this.items.CopyTo(array, index);

    public override IEnumerator GetEnumerator() => this.items.GetEnumerator();

    public override int IndexOf(object value) => value is SqlParameter parameter ? this.items.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName)
    {
        for (var i = 0; i < this.items.Count; i++)
        {
            if (string.Equals(this.items[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public int IndexOf(SqlParameter value) => this.items.IndexOf(value);

    public override void Insert(int index, object value) => this.items.Insert(index, Require(value));

    public void Insert(int index, SqlParameter value) => this.items.Insert(index, value);

    public override void Remove(object value) => _ = this.items.Remove(Require(value));

    public void Remove(SqlParameter value) => _ = this.items.Remove(value);

    public override void RemoveAt(int index) => this.items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => this.items.RemoveAt(this.RequireIndex(parameterName));

    protected override DbParameter GetParameter(int index) => this.items[index];

    protected override DbParameter GetParameter(string parameterName) => this[parameterName];

    protected override void SetParameter(int index, DbParameter value) => this.items[index] = Require(value);

    protected override void SetParameter(string parameterName, DbParameter value) => this[parameterName] = Require(value);

    internal SqlParameter[] ToArray() => [.. this.items];

    private int RequireIndex(string parameterName)
    {
        var index = this.IndexOf(parameterName);
        return index >= 0
            ? index
            : throw new IndexOutOfRangeException($"An SqlParameter with ParameterName '{parameterName}' is not contained by this SqlParameterCollection.");
    }

    private static SqlParameter Require(object? value) => value as SqlParameter
        ?? throw new InvalidCastException($"The SqlParameterCollection only accepts non-null SqlParameter type objects, not {value?.GetType().Name ?? "null"} objects.");
}
