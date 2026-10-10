namespace SqlServerSimulator;

/// <summary>
/// <c>sys.databases.state</c> for the states <c>ALTER DATABASE … SET</c> moves
/// a database between, numbered as real numbers them.
/// </summary>
internal enum DatabaseState : byte
{
    Online = 0,

    /// <summary>Readable by every user it admits; a write that would change rows raises Msg 3908.</summary>
    Emergency = 5,

    /// <summary>Unopenable: every reference raises Msg 942 and a connection naming it is refused.</summary>
    Offline = 6,
}
