using System.Data.SqlTypes;
using System.Runtime.CompilerServices;
using System.Security.Principal;

namespace Microsoft.SqlServer.Server;

/// <summary>
/// The ambient context of the routine the simulator is running on this
/// thread. <see cref="Pipe"/> and <see cref="TriggerContext"/> answer only
/// while a procedure or trigger runs; a function marked
/// <c>DataAccessKind.Read</c> or <c>SystemDataAccessKind.Read</c> reads both
/// as <see langword="null"/>, any other function or aggregate reading either
/// gets the <see cref="InvalidOperationException"/> the server raises there,
/// and a procedure reads a <see langword="null"/> trigger context (probed
/// 2026-09-28 against SQL Server 2025).
/// </summary>
public static class SqlContext
{
    internal const string NoDataAccess = "Data access is not allowed in this context.  Either the context is a function or method not marked with DataAccessKind.Read or SystemDataAccessKind.Read, is a callback to obtain data from FillRow method of a Table Valued Function, or is a UDT validation method.";

    public static bool IsAvailable => SimulatorBridge.Current is not null;

    public static SqlPipe? Pipe => SimulatorBridge.Current is { } frame
        ? frame.Pipe ?? (frame.Data is null ? throw new InvalidOperationException(NoDataAccess) : null)
        : null;

    public static SqlTriggerContext? TriggerContext => SimulatorBridge.Current is { } frame
        ? frame.Pipe is null && frame.Data is null ? throw new InvalidOperationException(NoDataAccess) : frame.Trigger
        : null;

    public static WindowsIdentity? WindowsIdentity => null;
}

/// <summary>
/// What a CLR trigger reads about the statement that fired it: the action, the
/// parent's column count and which columns count as updated — every column for
/// an <c>INSERT</c> or a <c>DELETE</c>, the <c>SET</c> clause's for an
/// <c>UPDATE</c> — and, for a DDL trigger, the event's <c>EVENTDATA()</c>
/// document, which a DML trigger reads as <see langword="null"/> (probed
/// 2026-09-28 against SQL Server 2025).
/// </summary>
public sealed class SqlTriggerContext
{
    private readonly bool[] updatedColumns;
    private readonly string? eventData;

    internal SqlTriggerContext(TriggerAction action, bool[] updatedColumns, string? eventData)
    {
        this.TriggerAction = action;
        this.updatedColumns = updatedColumns;
        this.eventData = eventData;
    }

    public TriggerAction TriggerAction { get; }

    public int ColumnCount => this.updatedColumns.Length;

    public SqlXml? EventData => this.eventData is null
        ? null
        : new SqlXml(new MemoryStream(System.Text.Encoding.Unicode.GetBytes(this.eventData), writable: false));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool IsUpdatedColumn(int columnOrdinal) => this.updatedColumns[columnOrdinal];
}
