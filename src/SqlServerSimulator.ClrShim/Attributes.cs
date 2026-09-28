namespace Microsoft.SqlServer.Server;

// The routine-marking attributes and their enums, shaped as .NET Framework's
// System.Data declares them. The simulator reads them off a loaded assembly as
// CustomAttributeData — never instantiating them — so only the member names
// and types matter to binding, but a routine that reads its own attributes
// through reflection gets working instances.

public enum Format
{
    Unknown = 0,
    Native = 1,
    UserDefined = 2,
}

public enum DataAccessKind
{
    None = 0,
    Read = 1,
}

public enum SystemDataAccessKind
{
    None = 0,
    Read = 1,
}

public enum TriggerAction
{
    Invalid = 0,
    Insert = 1,
    Update = 2,
    Delete = 3,
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SqlFunctionAttribute : Attribute
{
    public bool IsDeterministic { get; set; }

    public DataAccessKind DataAccess { get; set; }

    public SystemDataAccessKind SystemDataAccess { get; set; }

    public bool IsPrecise { get; set; }

    public string? Name { get; set; }

    public string? TableDefinition { get; set; }

    public string? FillRowMethodName { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SqlMethodAttribute : SqlFunctionAttribute
{
    public bool OnNullCall { get; set; } = true;

    public bool IsMutator { get; set; }

    public bool InvokeIfReceiverIsNull { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SqlProcedureAttribute : Attribute
{
    public string? Name { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SqlTriggerAttribute : Attribute
{
    public string? Name { get; set; }

    public string? Target { get; set; }

    public string? Event { get; set; }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.ReturnValue | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public class SqlFacetAttribute : Attribute
{
    public bool IsFixedLength { get; set; }

    public int MaxSize { get; set; }

    public int Precision { get; set; }

    public int Scale { get; set; }

    public bool IsNullable { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class SqlUserDefinedAggregateAttribute(Format format) : Attribute
{
    public const int MaxByteSizeValue = 8000;

    public Format Format { get; } = format;

    public int MaxByteSize { get; set; }

    public bool IsInvariantToDuplicates { get; set; }

    public bool IsInvariantToNulls { get; set; }

    public bool IsInvariantToOrder { get; set; }

    public bool IsNullIfEmpty { get; set; }

    public string? Name { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class SqlUserDefinedTypeAttribute(Format format) : Attribute
{
    public Format Format { get; } = format;

    public int MaxByteSize { get; set; }

    public bool IsFixedLength { get; set; }

    public bool IsByteOrdered { get; set; }

    public string? ValidationMethodName { get; set; }

    public string? Name { get; set; }
}

public interface IBinarySerialize
{
    void Read(BinaryReader r);

    void Write(BinaryWriter w);
}

public sealed class InvalidUdtException : SystemException
{
    public InvalidUdtException()
    {
    }

    public InvalidUdtException(string message)
        : base(message)
    {
    }

    public InvalidUdtException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
