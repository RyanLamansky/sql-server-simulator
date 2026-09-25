using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Database-role membership through <c>ALTER ROLE … ADD | DROP MEMBER</c> and
/// its legacy spelling <c>sp_addrolemember</c> / <c>sp_droprolemember</c>, and
/// what <c>IS_ROLEMEMBER</c> reads back. Probed against SQL Server 2025 on
/// 2026-09-25.
/// </summary>
[TestClass]
public sealed class RoleMembershipTests
{
    private const string Setup = "create user u1 without login; create role r1; create role r2;";

    [TestMethod]
    [DataRow("exec sp_addrolemember 'r1', 'u1'")]
    [DataRow("exec sp_addrolemember @rolename = 'r1', @membername = 'u1'")]
    [DataRow("alter role r1 add member u1")]
    public void AddMember_IsVisible(string add)
        => AreEqual(1, new Simulation().ExecuteScalar($"{Setup} {add}; select is_rolemember('r1', 'u1')"));

    [TestMethod]
    [DataRow("exec sp_droprolemember 'r1', 'u1'")]
    [DataRow("alter role r1 drop member u1")]
    public void DropMember_IsVisible(string drop)
        => AreEqual(0, new Simulation().ExecuteScalar($"{Setup} alter role r1 add member u1; {drop}; select is_rolemember('r1', 'u1')"));

    [TestMethod]
    public void SystemProcedure_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar($"{Setup} declare @rc int = 5; exec @rc = sp_addrolemember 'r1', 'u1'; select @rc"));

    [TestMethod]
    [DataRow("exec sp_addrolemember 'nosuchrole', 'u1'", 15151, "Cannot alter the role 'nosuchrole', because it does not exist or you do not have permission.")]
    [DataRow("exec sp_addrolemember 'u1', 'r1'", 15151, "Cannot alter the role 'u1', because it does not exist or you do not have permission.")]
    [DataRow("exec sp_addrolemember 'r1', 'nosuchuser'", 15410, "User or role 'nosuchuser' does not exist in this database.")]
    [DataRow("exec sp_droprolemember 'r1', 'nosuchuser'", 15151, "Cannot drop the principal 'nosuchuser', because it does not exist or you do not have permission.")]
    [DataRow("exec sp_addrolemember 'r1'", 201, "Procedure or function 'sp_addrolemember' expects parameter '@membername', which was not supplied.")]
    [DataRow("exec sp_addrolemember 'r1', 'u1', 'x'", 8144, "Procedure or function sp_addrolemember has too many arguments specified.")]
    [DataRow("exec sp_addrolemember 'r1', null", 15004, "Name cannot be NULL.")]
    [DataRow("exec sp_addrolemember 'public', 'u1'", 15081, "Membership of the public role cannot be changed.")]
    [DataRow("exec sp_addrolemember 'r1', 'r1'", 15413, "Cannot make a role a member of itself.")]
    [DataRow("alter role nosuchrole add member u1", 15151, "Cannot alter the role 'nosuchrole', because it does not exist or you do not have permission.")]
    [DataRow("alter role u1 add member r1", 15151, "Cannot alter the role 'u1', because it does not exist or you do not have permission.")]
    [DataRow("alter role r1 add member nosuchuser", 15151, "Cannot add the principal 'nosuchuser', because it does not exist or you do not have permission.")]
    [DataRow("alter role r1 drop member nosuchuser", 15151, "Cannot drop the principal 'nosuchuser', because it does not exist or you do not have permission.")]
    [DataRow("alter role [public] add member u1", 15081, "Membership of the public role cannot be changed.")]
    public void Refusals_MatchReal(string statement, int number, string message)
        => new Simulation().AssertSqlError($"{Setup} {statement}", number, message);

    /// <summary>
    /// Both spellings raise <c>ADD_ROLE_MEMBER</c> / <c>DROP_ROLE_MEMBER</c>
    /// naming the member as the object and the role in <c>RoleName</c>.
    /// </summary>
    [TestMethod]
    public void MembershipChanges_RaiseRoleMemberEvents()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            Setup + "create table evlog (e nvarchar(200));",
            """
            create trigger evt on database for ddl_database_level_events as
            insert evlog select eventdata().value('(/EVENT_INSTANCE/EventType)[1]', 'nvarchar(50)') + '|'
                + eventdata().value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(50)') + '|'
                + eventdata().value('(/EVENT_INSTANCE/ObjectType)[1]', 'nvarchar(50)') + '|'
                + eventdata().value('(/EVENT_INSTANCE/RoleName)[1]', 'nvarchar(50)')
            """,
            "alter role r1 add member u1; exec sp_droprolemember 'r1', 'u1'; alter role r2 add member r1");
        AreEqual(
            "ADD_ROLE_MEMBER|u1|SQL USER|r1,DROP_ROLE_MEMBER|u1|SQL USER|r1,ADD_ROLE_MEMBER|r1|ROLE|r2",
            simulation.ExecuteScalar("select string_agg(e, ',') from evlog"));
    }

    /// <summary>
    /// With a principal named, <c>IS_ROLEMEMBER</c> resolves it first — a
    /// missing one is NULL even for <c>public</c> — counts a principal a member
    /// of itself, and follows nested roles.
    /// </summary>
    [TestMethod]
    public void IsRoleMember_WithANamedPrincipal()
        => AreEqual("1,1,NULL,1,1,0,1,NULL,1,0", new Simulation().ExecuteScalar($"""
            {Setup} alter role r2 add member u1; alter role r1 add member r2;
            select concat_ws(',', is_rolemember('r1', 'u1'), is_rolemember('r2', 'u1'), isnull(cast(is_rolemember('r1', 'nosuch') as varchar), 'NULL'),
                is_rolemember('db_owner', 'dbo'), is_rolemember('r1', 'r1'), is_rolemember('r1', 'dbo'), is_rolemember('u1', 'u1'),
                isnull(cast(is_rolemember('public', 'nosuch') as varchar), 'NULL'), is_rolemember('r1', 'r2'), is_rolemember('db_owner', 'u1'))
            """));
}
