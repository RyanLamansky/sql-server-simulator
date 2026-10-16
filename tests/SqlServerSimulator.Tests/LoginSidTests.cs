using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CREATE LOGIN … SID</c> and <c>sp_addlogin @sid</c>, probed 2026-10-16
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class LoginSidTests
{
    [TestMethod]
    public void Sid_IsTheLoginsOwn_AndNamesItBack()
        => AreEqual("0x0102030405060708090A0B0C0D0E0F10|0x0102030405060708090A0B0C0D0E0F10|l1|l1", new Simulation().ExecuteScalar("""
            create login l1 with password = 'Xy!12345abcd', sid = 0x0102030405060708090A0B0C0D0E0F10;
            select convert(varchar(40), (select sid from sys.server_principals where name = 'l1'), 1)
                + '|' + convert(varchar(40), suser_sid('l1'), 1)
                + '|' + suser_sname(0x0102030405060708090A0B0C0D0E0F10)
                + '|' + suser_sname(0x0102030405060708090A0B0C0D0E0F1000)
            """));

    [TestMethod]
    [DataRow("sid = 0x01", 15419)]
    [DataRow("sid = 0x010500000000000515000000A1B2C3D4E5F60718293A4B5C51040000", 15419)]
    [DataRow("sid = 0x00000000000000000000000000000000", 15433)]
    [DataRow("sid = 0x03000000000000000000000000000000", 15433)]
    [DataRow("sid = 'abc'", 102)]
    [DataRow("sid = null", 156)]
    [DataRow("sid = 0x01020304050607080900000000000000, sid = 0x01020304050607080900000000000001", 102)]
    public void Sid_IsRefusedAsRealRefusesIt(string option, int number)
        => _ = new Simulation().AssertSqlError($"create login l1 with password = 'Xy!12345abcd', {option}", number);

    [TestMethod]
    public void Sid_IsCheckedAheadOfTheNameAndThePassword()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login l1 with password = 'Xy!12345abcd', sid = 0x0102030405060708090A0B0C0D0E0F10");
        _ = simulation.AssertSqlError("create login l1 with password = 'Xy!12345abcd', sid = 0x01", 15419);
        _ = simulation.AssertSqlError("create login l2 with password = 'a', sid = 0x01", 15419);
        _ = simulation.AssertSqlError("create login l2 with password = 'Xy!12345abcd', sid = 0x0102030405060708090A0B0C0D0E0F10", 15433);
        _ = simulation.AssertSqlError("alter login l1 with sid = 0x0102030405060708090A0B0C0D0E0F11", 102);
    }

    [TestMethod]
    public void SpAddLogin_PassesItsSidOn_CutToSixteenBytes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_addlogin 'l1', 'Xy!12345abcd', @sid = 0x0102030405060708090A0B0C0D0E0F1066");
        AreEqual("0x0102030405060708090A0B0C0D0E0F10", simulation.ExecuteScalar("select convert(varchar(40), suser_sid('l1'), 1)"));
        _ = simulation.AssertSqlError("exec sp_addlogin 'l2', 'Xy!12345abcd', @sid = 0x01", 15419);
    }

    [TestMethod]
    public void ALoginMadeAgainWithTheSid_GetsItsUsersBack()
        => AreEqual("l2|u", new Simulation().ExecuteScalar("""
            create login l1 with password = 'Xy!12345abcd', sid = 0x0A0B0C0D0E0F10111213141516171819;
            create user u for login l1;
            drop login l1;
            create login l2 with password = 'Xy!12345abcd', sid = 0x0A0B0C0D0E0F10111213141516171819;
            execute as login = 'l2';
            declare @r nvarchar(300) = suser_sname() + '|' + user_name();
            revert;
            select @r
            """));
}
