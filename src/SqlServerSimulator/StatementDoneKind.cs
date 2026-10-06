namespace SqlServerSimulator;

/// <summary>
/// The statement kinds real SQL Server names in a DONE token's <c>CurCmd</c>
/// field — its own statement-type codes, which MS-TDS leaves undocumented —
/// captured per statement off the wire (probed 2026-09-28 against SQL Server
/// 2025 with a raw TDS client). A statement not listed sends no DONE of its
/// own: <c>DECLARE</c> without an initializer, the security, schema, type,
/// synonym and sequence DDL, <c>SET QUOTED_IDENTIFIER</c> and
/// <c>SET PARSEONLY</c>, <c>ENABLE</c> / <c>DISABLE TRIGGER</c>,
/// <c>EXECUTE AS</c> / <c>REVERT</c>, a label, and <c>THROW</c> uncaught.
/// </summary>
internal static class StatementDoneKind
{
    /// <summary>No kind recorded: the statement sends no DONE of its own.</summary>
    public const ushort NoDone = 0;

    public const ushort OpenCursor = 0x20;

    /// <summary>
    /// The error ending a security or schema statement that sends no DONE
    /// when it succeeds — <c>CREATE</c> / <c>ALTER</c> / <c>DROP</c> of a user,
    /// role, application role, login or server role, <c>GRANT</c> /
    /// <c>DENY</c> / <c>REVOKE</c>, <c>ALTER AUTHORIZATION</c>,
    /// <c>ALTER</c> / <c>DROP SCHEMA</c> and <c>CREATE SYNONYM</c>, where
    /// <c>CREATE SCHEMA</c>, a type's or a sequence's DDL closes it with the
    /// batch's (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    public const ushort SecurityDdlFailed = 0xAA;
    public const ushort CloseCursor = 0x2B;
    public const ushort DeallocateCursor = 0x2C;
    public const ushort DropFunction = 0xB3;
    public const ushort SetIdentityInsert = 0xB7;

    /// <summary>A boolean <c>SET</c> option turned on.</summary>
    public const ushort SetOptionOn = 0xB9;

    /// <summary>A boolean <c>SET</c> option turned off.</summary>
    public const ushort SetOptionOff = 0xBA;

    public const ushort SetStatisticsOn = 0xBB;
    public const ushort SetStatisticsOff = 0xBC;
    public const ushort SetRowCount = 0xBD;
    public const ushort SetTextSize = 0xBE;

    /// <summary>An <c>IF</c> or <c>WHILE</c> condition, once per evaluation, ahead of the body it chose.</summary>
    public const ushort Condition = 0xC0;

    /// <summary>
    /// A <c>SELECT</c> — returning rows or assigning variables — and the
    /// statements real classes with it: <c>SET @v = …</c>, a <c>DECLARE</c>
    /// with an initializer, <c>DECLARE … CURSOR</c>, <c>FETCH</c>, and a
    /// procedure's <c>RETURN</c> of a value.
    /// </summary>
    public const ushort Select = 0xC1;

    public const ushort SelectInto = 0xC2;
    public const ushort Insert = 0xC3;
    public const ushort Delete = 0xC4;
    public const ushort Update = 0xC5;
    public const ushort CreateTable = 0xC6;
    public const ushort DropTable = 0xC7;

    /// <summary><c>CREATE INDEX</c> and <c>CREATE STATISTICS</c>.</summary>
    public const ushort CreateIndex = 0xC8;

    /// <summary><c>DROP INDEX</c> and <c>DROP STATISTICS</c>.</summary>
    public const ushort DropIndex = 0xC9;

    /// <summary><c>GOTO</c>, <c>BREAK</c> and <c>CONTINUE</c>.</summary>
    public const ushort Goto = 0xCA;

    public const ushort CreateDatabase = 0xCB;
    public const ushort DropDatabase = 0xCC;

    /// <summary><c>CREATE VIEW</c> and <c>ALTER VIEW</c>.</summary>
    public const ushort CreateView = 0xCF;

    public const ushort DropView = 0xD0;

    /// <summary><c>ROLLBACK</c>, to a savepoint or of the whole transaction.</summary>
    public const ushort Rollback = 0xD2;

    public const ushort BeginTransaction = 0xD4;
    public const ushort Commit = 0xD5;
    public const ushort Save = 0xD6;
    public const ushort AlterDatabase = 0xD7;
    public const ushort AlterTable = 0xD8;

    /// <summary>A bare <c>RETURN</c>.</summary>
    public const ushort Return = 0xDB;

    public const ushort Reconfigure = 0xDC;

    /// <summary><c>CREATE TRIGGER</c> and <c>ALTER TRIGGER</c>.</summary>
    public const ushort CreateTrigger = 0xDD;

    /// <summary><c>CREATE</c> / <c>ALTER</c> of a procedure or a function.</summary>
    public const ushort CreateProcedure = 0xDE;

    public const ushort DropProcedure = 0xDF;

    /// <summary>
    /// A procedure call or dynamic SQL, closing its scope: the batch-level
    /// call's DONEPROC and a nested call's DONEINPROC.
    /// </summary>
    public const ushort Execute = 0xE0;

    public const ushort DropTrigger = 0xE1;
    public const ushort Use = 0xE2;
    public const ushort Dbcc = 0xE6;
    public const ushort Checkpoint = 0xE7;
    public const ushort CreateDefault = 0xE9;
    public const ushort Truncate = 0xEA;
    public const ushort CreateRule = 0xEC;
    public const ushort DropRule = 0xED;
    public const ushort DropDefault = 0xEE;
    public const ushort UpdateStatistics = 0xF1;
    public const ushort WaitFor = 0xF3;

    /// <summary><c>RAISERROR</c>, and a <c>THROW</c> a <c>TRY</c> catches.</summary>
    public const ushort RaisError = 0xF6;

    public const ushort Print = 0xF7;
    public const ushort UpdateText = 0xF8;

    /// <summary>
    /// A value-taking <c>SET</c> option — <c>DATEFIRST</c>, <c>LOCK_TIMEOUT</c>,
    /// <c>LANGUAGE</c>, <c>TRANSACTION ISOLATION LEVEL</c> and their kin.
    /// </summary>
    public const ushort SetValue = 0xF9;

    public const ushort ReadText = 0xFB;
    public const ushort WriteText = 0xFC;

    /// <summary>
    /// The DONE that closes a response whose last statement sent none of its
    /// own — a batch ending in a <c>DECLARE</c>, an empty batch, or one an
    /// error ended outside any statement's DONE.
    /// </summary>
    public const ushort Batch = 0xFD;

    public const ushort Merge = 0x117;
    public const ushort CreateXmlSchemaCollection = 0x136;
    public const ushort DropSynonym = 0x149;
    public const ushort AlterIndex = 0x151;
    public const ushort BeginTry = 0x15D;
    public const ushort BeginCatch = 0x15E;
    public const ushort EndCatch = 0x15F;
    public const ushort DropSequence = 0x204;

    /// <summary>
    /// An error that sends no DONE of its own, because the scope it ended
    /// closes with one that carries its error bit — a procedure's DONEPROC.
    /// Never on the wire.
    /// </summary>
    public const ushort ClosedByScope = 0xFFFF;
}
