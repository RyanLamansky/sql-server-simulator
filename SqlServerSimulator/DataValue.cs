namespace SqlServerSimulator;

/// <summary>
/// Wraps a raw value with type information.
/// <see cref="object"/> can't be directly used because of differences in type handling between .NET and SQL Server.
/// </summary>
internal readonly struct DataValue : IComparable<DataValue>
{
    /// <summary>
    /// The wrapped value.
    /// </summary>
    public readonly object? Value;

    /// <summary>
    /// The SQL data type of <see cref="Value"/>.
    /// </summary>
    /// <remarks>Unlike .NET, "null" is typed in SQL Server and strings have several additional properties.</remarks>
    public DataType Type => field ?? DataType.BuiltInDbInt32;

    public DataValue(object? value, DataType type)
    {
        System.Diagnostics.Debug.Assert(value is not DataValue);

        this.Value = value;
        this.Type = type;
    }

    public DataValue(int value) : this(value, DataType.BuiltInDbInt32)
    {
    }

    public int CompareTo(DataValue other) => Type.Compare(this, other);

#if DEBUG
    public override string ToString() => $"({Type}) {Value ?? "NULL"}";
#endif
}
