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

// Its numbers are the DDL event types' own, sys.trigger_event_types.type.
public enum TriggerAction
{
    Invalid = 0,
    Insert = 1,
    Update = 2,
    Delete = 3,
    CreateTable = 21,
    AlterTable = 22,
    DropTable = 23,
    CreateIndex = 24,
    AlterIndex = 25,
    DropIndex = 26,
    CreateSecurityExpression = 31,
    DropSecurityExpression = 33,
    CreateSynonym = 34,
    DropSynonym = 36,
    CreateView = 41,
    AlterView = 42,
    DropView = 43,
    CreateProcedure = 51,
    AlterProcedure = 52,
    DropProcedure = 53,
    CreateFunction = 61,
    AlterFunction = 62,
    DropFunction = 63,
    CreateTrigger = 71,
    AlterTrigger = 72,
    DropTrigger = 73,
    CreateEventNotification = 74,
    DropEventNotification = 76,
    CreateType = 91,
    DropType = 93,
    CreateAssembly = 101,
    AlterAssembly = 102,
    DropAssembly = 103,
    CreateUser = 131,
    AlterUser = 132,
    DropUser = 133,
    CreateRole = 134,
    AlterRole = 135,
    DropRole = 136,
    CreateAppRole = 137,
    AlterAppRole = 138,
    DropAppRole = 139,
    CreateSchema = 141,
    AlterSchema = 142,
    DropSchema = 143,
    CreateLogin = 144,
    AlterLogin = 145,
    DropLogin = 146,
    CreateMsgType = 151,
    DropMsgType = 153,
    CreateContract = 154,
    DropContract = 156,
    CreateQueue = 157,
    AlterQueue = 158,
    DropQueue = 159,
    CreateService = 161,
    AlterService = 162,
    DropService = 163,
    CreateRoute = 164,
    AlterRoute = 165,
    DropRoute = 166,
    GrantStatement = 167,
    DenyStatement = 168,
    RevokeStatement = 169,
    GrantObject = 170,
    DenyObject = 171,
    RevokeObject = 172,
    CreateBinding = 174,
    AlterBinding = 175,
    DropBinding = 176,
    CreatePartitionFunction = 191,
    AlterPartitionFunction = 192,
    DropPartitionFunction = 193,
    CreatePartitionScheme = 194,
    AlterPartitionScheme = 195,
    DropPartitionScheme = 196,
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
