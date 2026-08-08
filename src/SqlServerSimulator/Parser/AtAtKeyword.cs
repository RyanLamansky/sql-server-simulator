namespace SqlServerSimulator.Parser;

enum AtAtKeyword
{
    /// <summary>Default value — this token is not an <c>@@</c>-keyword.</summary>
    _ = 0,

    Connections,
    CpuBusy,
    CursorRows,
    DateFirst,
    Dbts,
    Error,
    FetchStatus,
    Identity,
    Idle,
    IoBusy,
    LangId,
    Language,
    LockTimeout,
    MaxConnections,
    MaxPrecision,
    MicrosoftVersion,
    NestLevel,
    Options,
    PackReceived,
    PacketErrors,
    PackSent,
    ProcId,
    RemServer,
    RowCount,
    ServerName,
    ServiceName,
    SpId,
    TextSize,
    TimeTicks,
    TotalErrors,
    TotalRead,
    TotalWrite,
    TranCount,
    Version
}
