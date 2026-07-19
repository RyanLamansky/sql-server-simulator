namespace SqlServerSimulator.Network;

/// <summary>
/// MS-TDS protocol constants used by the network listener. Values come from
/// the MS-TDS specification (packet types §2.2.3.1.1, tokens §2.2.7, prelogin
/// §2.2.6.5, DONE status flags §2.2.7.6, ENVCHANGE types §2.2.7.9).
/// </summary>
internal static class Tds
{
    /// <summary>Bytes in every TDS packet header.</summary>
    public const int HeaderSize = 8;

    /// <summary>Packet size assumed before LOGIN7 negotiates one.</summary>
    public const int DefaultPacketSize = 4096;

    /// <summary>The TDS 7.4 version constant exchanged during login.</summary>
    public const uint Version74 = 0x74000004;

    // Packet header types.
    public const byte PacketSqlBatch = 1;
    public const byte PacketRpc = 3;
    public const byte PacketTabularResult = 4;
    public const byte PacketAttention = 6;
    public const byte PacketBulkLoad = 7;
    public const byte PacketTransactionManager = 14;
    public const byte PacketLogin7 = 16;
    public const byte PacketPrelogin = 18;

    // Packet header status bits.
    public const byte StatusEndOfMessage = 0x01;
    public const byte StatusResetConnection = 0x08;
    public const byte StatusResetConnectionSkipTran = 0x10;

    // Token identifiers (server to client).
    public const byte TokenReturnStatus = 0x79;
    public const byte TokenColMetadata = 0x81;
    public const byte TokenError = 0xAA;
    public const byte TokenInfo = 0xAB;
    public const byte TokenReturnValue = 0xAC;
    public const byte TokenLoginAck = 0xAD;
    public const byte TokenRow = 0xD1;
    public const byte TokenEnvChange = 0xE3;
    public const byte TokenDone = 0xFD;
    public const byte TokenDoneProc = 0xFE;
    public const byte TokenDoneInProc = 0xFF;

    // DONE token status flags.
    public const ushort DoneFinal = 0x0000;
    public const ushort DoneMore = 0x0001;
    public const ushort DoneError = 0x0002;
    public const ushort DoneCount = 0x0010;
    public const ushort DoneAttention = 0x0020;

    // ENVCHANGE types.
    public const byte EnvDatabase = 1;
    public const byte EnvLanguage = 2;
    public const byte EnvPacketSize = 4;
    public const byte EnvSqlCollation = 7;
    public const byte EnvBeginTransaction = 8;
    public const byte EnvCommitTransaction = 9;
    public const byte EnvRollbackTransaction = 10;
    public const byte EnvResetConnectionAck = 18;

    // Transaction Manager request types.
    public const ushort TmBeginTransaction = 5;
    public const ushort TmCommitTransaction = 7;
    public const ushort TmRollbackTransaction = 8;
    public const ushort TmSaveTransaction = 9;

    // Well-known RPC ProcIDs.
    public const ushort ProcIdCursor = 1;
    public const ushort ProcIdCursorOpen = 2;
    public const ushort ProcIdCursorPrepare = 3;
    public const ushort ProcIdCursorExecute = 4;
    public const ushort ProcIdCursorPrepExec = 5;
    public const ushort ProcIdCursorUnprepare = 6;
    public const ushort ProcIdCursorFetch = 7;
    public const ushort ProcIdCursorOption = 8;
    public const ushort ProcIdCursorClose = 9;
    public const ushort ProcIdExecuteSql = 10;
    public const ushort ProcIdPrepare = 11;
    public const ushort ProcIdExecute = 12;
    public const ushort ProcIdPrepExec = 13;
    public const ushort ProcIdUnprepare = 15;

    // SMP (Session Multiplex Protocol, [MC-SMP]) framing — the 16-byte header
    // wrapping every post-login TDS packet on a MARS-negotiated connection.
    public const byte SmpSmid = 0x53;
    public const int SmpHeaderSize = 16;
    public const byte SmpFlagSyn = 0x01;
    public const byte SmpFlagAck = 0x02;
    public const byte SmpFlagFin = 0x04;
    public const byte SmpFlagData = 0x08;

    /// <summary>Receive-window size (in SMP packets) advertised for each session.</summary>
    public const uint SmpWindow = 4;

    // PRELOGIN option tokens.
    public const byte PreloginVersion = 0;
    public const byte PreloginEncryption = 1;
    public const byte PreloginInstance = 2;
    public const byte PreloginThreadId = 3;
    public const byte PreloginMars = 4;
    public const byte PreloginTraceId = 5;
    public const byte PreloginFedAuthRequired = 6;
    public const byte PreloginTerminator = 0xFF;

    // PRELOGIN ENCRYPTION option values.
    public const byte EncryptOff = 0;
    public const byte EncryptOn = 1;
    public const byte EncryptNotSupported = 2;
    public const byte EncryptRequired = 3;
}
