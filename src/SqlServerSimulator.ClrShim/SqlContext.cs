using System.Security.Principal;

namespace Microsoft.SqlServer.Server;

/// <summary>
/// The ambient context of the routine the simulator is running on this
/// thread. <see cref="Pipe"/> answers only while a procedure runs; a function
/// or aggregate reading it gets the <see cref="InvalidOperationException"/>
/// the server raises there (probed 2026-09-28 against SQL Server 2025).
/// </summary>
public static class SqlContext
{
    public static bool IsAvailable => SimulatorBridge.Current is not null;

    public static SqlPipe? Pipe => SimulatorBridge.Current is { } frame
        ? frame.Pipe ?? throw new InvalidOperationException("Data access is not allowed in this context.  Either the context is a function or method not marked with DataAccessKind.Read or SystemDataAccessKind.Read, is a callback to obtain data from FillRow method of a Table Valued Function, or is a UDT validation method.")
        : null;

    public static SqlTriggerContext? TriggerContext => null;

    public static WindowsIdentity? WindowsIdentity => null;
}

/// <summary>
/// The context a CLR trigger reads. CLR triggers are not modeled, so
/// <see cref="SqlContext.TriggerContext"/> is always <see langword="null"/>;
/// the type exists so a routine that names it still loads.
/// </summary>
public sealed class SqlTriggerContext
{
    private SqlTriggerContext()
    {
    }

    public TriggerAction TriggerAction => TriggerAction.Invalid;

    public int ColumnCount => 0;

    public bool IsUpdatedColumn(int columnOrdinal) => throw new ArgumentOutOfRangeException(nameof(columnOrdinal));
}
