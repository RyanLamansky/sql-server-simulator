using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>KEY</c> lock's <c>resource_description</c> is real's hash of the
/// index row's key, each expectation here the description SQL Server 2025
/// gave the same key (probed 2026-10-07): every key a written row locks, in
/// each index it enters.
/// </summary>
[TestClass]
public sealed class KeyLockDescriptionTests
{
    [TestMethod]
    [DataRow("k int primary key", "(0), (1), (-1), (2147483647)", "(0000ffffffff)|(78d82fa561ac)|(8194443284a0)|(c0e5328bcd82)", DisplayName = "int")]
    [DataRow("k bigint primary key", "(0), (1), (-1)", "(1b7fe5b8af93)|(42416d56cea6)|(ffffffffffff)", DisplayName = "bigint, -1 printing as the infinity key")]
    [DataRow("k smallint primary key", "(1), (-2)", "(6b2af6208e53)|(e1784bd73cba)", DisplayName = "smallint")]
    [DataRow("k tinyint primary key", "(0), (1)", "(26e6f8b585ce)|(cd076b836f67)", DisplayName = "tinyint")]
    [DataRow("k bit primary key", "(0), (1)", "(26e6f8b585ce)|(cd076b836f67)", DisplayName = "bit")]
    [DataRow("k decimal(5, 2) primary key", "(1.5), (-123.45)", "(2069fada245f)|(f251b08f7b83)", DisplayName = "decimal")]
    [DataRow("k decimal(38, 0) primary key", "(1), (-1)", "(31e260e83a69)|(96da6e9c6dbb)", DisplayName = "decimal(38)")]
    [DataRow("k money primary key", "(1), (-1.5)", "(0fe194457cf7)|(8e96c7a69d12)", DisplayName = "money")]
    [DataRow("k smallmoney primary key", "(1), (-1.5)", "(5e2acbfb70df)|(8f2db235afd3)", DisplayName = "smallmoney")]
    [DataRow("k float primary key", "(1), (-2.5)", "(c5d5bc906aa3)|(edcc8d127f2c)", DisplayName = "float")]
    [DataRow("k real primary key", "(1), (-2.5)", "(968d4e4ead7d)|(b1d2e0d224bc)", DisplayName = "real")]
    [DataRow("k date primary key", "('2000-01-01'), ('0001-01-01')", "(0e9b579ffa63)|(246b88f8197a)", DisplayName = "date")]
    [DataRow("k time(7) primary key", "('00:00:01'), ('12:34:56.1234567')", "(09abfb1547a2)|(1f07703c2958)", DisplayName = "time(7)")]
    [DataRow("k time(0) primary key", "('00:00:01'), ('12:34:56')", "(916c90872d59)|(acd91ddf8a4a)", DisplayName = "time(0)")]
    [DataRow("k datetime primary key", "('1900-01-01'), ('2000-01-01 00:00:01')", "(42416d56cea6)|(42e86d36cee4)", DisplayName = "datetime")]
    [DataRow("k smalldatetime primary key", "('1900-01-01'), ('2000-01-01 00:01')", "(78d82fa561ac)|(d61f610498b5)", DisplayName = "smalldatetime")]
    [DataRow("k datetime2 primary key", "('2000-01-01'), ('2000-01-01 00:00:01')", "(68b1b2312dbf)|(a80dda65a5ae)", DisplayName = "datetime2")]
    [DataRow("k datetimeoffset primary key", "('2000-01-01 00:00 +00:00'), ('2000-01-01 00:00 +01:00')", "(efd0699884e3)|(f3f5ff32529e)", DisplayName = "datetimeoffset")]
    [DataRow("k uniqueidentifier primary key", "('00000000-0000-0000-0000-000000000001'), ('01020304-0506-0708-090a-0b0c0d0e0f10')", "(34b704b31641)|(89fc15be9edb)", DisplayName = "uniqueidentifier")]
    [DataRow("k varbinary(5) primary key", "(0x), (0x010203)", "(90fba454e4ad)|(e1784bd73cba)", DisplayName = "varbinary, empty as its ordinal")]
    [DataRow("k binary(3) primary key", "(0x0A)", "(2229725e0669)", DisplayName = "binary, padded")]
    [DataRow("k varchar(10) collate Latin1_General_CI_AS primary key", "('a'), (''), ('b ')", "(27f49aa9c0ac)|(e1784bd73cba)|(ee210b7edba4)", DisplayName = "varchar, as stored")]
    [DataRow("k varchar(10) collate Latin1_General_CI_AS primary key", "('A')", "(d80544a2038d)", DisplayName = "varchar, case kept")]
    [DataRow("k nvarchar(10) primary key", "(N'a'), (N'ab'), (N'')", "(29c1e1a992ef)|(474f5d2261d8)|(e1784bd73cba)", DisplayName = "nvarchar")]
    [DataRow("k char(3) primary key", "('a')", "(b3918c2e9ec3)", DisplayName = "char, padded")]
    [DataRow("k nchar(2) primary key", "(N'a')", "(89f1f56aaac5)", DisplayName = "nchar, padded")]
    [DataRow("a int, b int, primary key (a, b)", "(0, 0), (1, 0), (0, 1), (1, 1)", "(1b7fe5b8af93)|(42416d56cea6)|(bb0d06c12baa)|(e2338e2f4a9f)", DisplayName = "Composite key")]
    [DataRow("a varchar(5), b int, primary key (a, b)", "('', 0), ('a', 1), ('ab', 2)", "(9c6eea2d1e5e)|(b568fe4449c6)|(e68f92b335d5)", DisplayName = "Composite key, empty string as its ordinal")]
    [DataRow("a int null, b int null, id int identity primary key, index ix unique (a, b)", "(null, null), (null, 1), (1, null), (0, 0)",
        "(42416d56cea6)|(4c2495d3acca)|(61a06abd401c)|(8194443284a0)|(88dd7fccbec8)|(98ec012aa510)|(a0c936a3c965)|(bc21c5ba73ae)", DisplayName = "Unique index, NULLs as their ordinals")]
    [DataRow("a int, id int primary key, index ix (a)", "(5, 1), (null, 3)", "(555cd0cb8d7a)|(6d2b3da327e3)|(8194443284a0)|(98ec012aa510)", DisplayName = "Non-unique index, the clustered key appended")]
    [DataRow("a int null, b int, index cx clustered (a)", "(null, 1), (7, 3)", "(0a8a6362ce4f)|(5910e987dcea)", DisplayName = "Non-unique clustered index, the uniquifier column 1")]
    [DataRow("a int, b int, c int, index cx clustered (a, b)", "(1, 2, 3)", "(313781f59976)", DisplayName = "Non-unique composite clustered index")]
    [DataRow("a int, b int, index cx clustered (a), index ix (b)", "(5, 1)", "(13976a09a80a)|(827e92618c58)", DisplayName = "Non-unique index over a non-unique clustered one")]
    public void WrittenKey_IsDescribedByRealsHash(string columns, string rows, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t ({columns})");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand($"begin tran; insert t values {rows}").ExecuteNonQuery();

        AreEqual(expected, connection.CreateCommand("""
            select string_agg(rtrim(resource_description), '|') within group (order by resource_description)
            from sys.dm_tran_locks where request_session_id = @@spid and resource_type = 'KEY'
            """).ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// Real's view holds every description blank-padded to the column's 256
    /// characters, an OBJECT row's empty one included.
    /// </summary>
    [TestMethod]
    public void Descriptions_ArePaddedToTheColumnWidth()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (k int primary key)");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("begin tran; insert t values (1)").ExecuteNonQuery();

        AreEqual("KEY 512|OBJECT 512", connection.CreateCommand("""
            select string_agg(resource_type + ' ' + cast(datalength(resource_description) as varchar(5)), '|') within group (order by resource_type)
            from sys.dm_tran_locks where request_session_id = @@spid and resource_type in ('KEY', 'OBJECT')
            """).ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
    }
}
