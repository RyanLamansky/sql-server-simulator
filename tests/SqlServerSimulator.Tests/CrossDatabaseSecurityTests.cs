using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Cross-database identity: a login's rights are per database, so a reference
/// through a three-part name resolves the login's user in the <em>target</em>
/// and checks that principal's grants there (Msg 229 naming the target
/// database), while a login with no user there gets Msg 916 whatever the
/// object grants say. An identity that exists only inside one database — an
/// <c>EXECUTE AS USER</c> frame or an application role — never crosses at all,
/// and an ownership chain breaks at the database boundary. <c>USE</c> asks the
/// same question and rebinds the session's user on success.
/// All probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CrossDatabasePermissionTests
{
    /// <summary>
    /// Two databases and one login. <c>home</c> holds user <c>homeuser</c>
    /// (SELECT on <c>home.dbo.local</c>); <c>away</c> holds a differently-named
    /// user <c>awayuser</c> for the same login, with no grants — the split real
    /// makes between a login's two database identities.
    /// </summary>
    private static Simulation TwoDatabaseFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create login app with password = 'S3cret!Pass';
            create database home;
            create database away
            """);
        _ = sim.ExecuteNonQuery("""
            use home;
            create table dbo.local (id int not null);
            insert dbo.local values (1);
            create user homeuser for login app;
            grant select on dbo.local to homeuser
            """);
        _ = sim.ExecuteNonQuery("""
            use away;
            create table dbo.remote (id int not null);
            insert dbo.remote values (7)
            """);
        return sim;
    }

    private static void CreateAwayUser(Simulation sim, string grants = "") =>
        _ = sim.ExecuteNonQuery($"use away; create user awayuser for login app; {grants}");

    private static SimulatedDbConnection ConnectAsApp(Simulation sim)
    {
        var connection = sim.CreateDbConnection();
        connection.ConnectionString = "User ID=app;Password=S3cret!Pass;Initial Catalog=home";
        connection.Open();
        return connection;
    }

    // ---- the login's user in the target database answers ----

    [TestMethod]
    public void CrossDatabaseRead_TargetUserHoldsGrant_Succeeds()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim, "grant select on dbo.remote to awayuser");
        using var connection = ConnectAsApp(sim);
        AreEqual(7, connection.CreateCommand("select id from away.dbo.remote").ExecuteScalar());
    }

    [TestMethod]
    public void CrossDatabaseRead_SessionUsersGrantDoesNotTravel_Raises229()
    {
        // The grant on home.dbo.local belongs to homeuser; awayuser holds
        // nothing, so the away read is denied and the message names `away`.
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim);
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("select id from away.dbo.remote").ExecuteScalar());
        AreEqual(229, ex.Number);
        AreEqual("The SELECT permission was denied on the object 'remote', database 'away', schema 'dbo'.", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseRead_NoUserInTarget_Raises916Verbatim()
    {
        var sim = TwoDatabaseFixture();
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("select id from away.dbo.remote").ExecuteScalar());
        AreEqual(916, ex.Number);
        AreEqual(14, ex.Class);
        AreEqual(2, ex.State);
        AreEqual("The server principal \"app\" is not able to access the database \"away\" under the current security context.", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseWrite_TargetUserHoldsGrant_Succeeds()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim, "grant insert on dbo.remote to awayuser");
        using var connection = ConnectAsApp(sim);
        AreEqual(1, connection.CreateCommand("insert away.dbo.remote values (8)").ExecuteNonQuery());
    }

    [TestMethod]
    public void CrossDatabaseWrite_WithoutTargetGrant_Raises229NamingTargetDatabase()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim);
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert away.dbo.remote values (8)").ExecuteNonQuery());
        AreEqual(229, ex.Number);
        AreEqual("The INSERT permission was denied on the object 'remote', database 'away', schema 'dbo'.", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseWrite_NoUserInTarget_Raises916()
    {
        var sim = TwoDatabaseFixture();
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert away.dbo.remote values (8)").ExecuteNonQuery());
        AreEqual(916, ex.Number);
    }

    [TestMethod]
    public void CrossDatabaseRead_SysadminLogin_Unrestricted()
    {
        var sim = TwoDatabaseFixture();
        _ = sim.ExecuteNonQuery("alter server role sysadmin add member app");
        using var connection = ConnectAsApp(sim);
        AreEqual(7, connection.CreateCommand("select id from away.dbo.remote").ExecuteScalar());
    }

    [TestMethod]
    public void CrossDatabaseRead_DboSession_Unrestricted()
    {
        // The unauthenticated in-process front door is dbo everywhere and
        // pays no lookup at all.
        var sim = TwoDatabaseFixture();
        AreEqual(7, sim.ExecuteScalar("use home; select id from away.dbo.remote"));
    }

    // ---- database-scoped identities can't cross ----

    [TestMethod]
    public void ExecuteAsUser_ThenCrossDatabaseRead_Raises916NamingImpersonatedIdentity()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim, "grant select on dbo.remote to awayuser");
        var ex = sim.AssertSqlError("use home; execute as user = 'homeuser'; select id from away.dbo.remote", 916);
        // The frame reports the impersonated user's login identity, which for a
        // FOR LOGIN user is the login name.
        AreEqual("The server principal \"app\" is not able to access the database \"away\" under the current security context.", ex.Message);
    }

    [TestMethod]
    public void ExecuteAsUser_WithoutLogin_CrossDatabase_Raises916NamingSid()
    {
        var sim = TwoDatabaseFixture();
        _ = sim.ExecuteNonQuery("use home; create user loner without login");
        var ex = sim.AssertSqlError("use home; execute as user = 'loner'; select id from away.dbo.remote", 916);
        Contains("\"S-1-9-3-", ex.Message);
        Contains("\"away\"", ex.Message);
    }

    [TestMethod]
    public void ApplicationRole_CrossDatabaseRead_Raises916NamingLogin()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim, "grant select on dbo.remote to awayuser");
        _ = sim.ExecuteNonQuery("use home; create application role appr with password = 'S3cret!Pass'");
        using var connection = ConnectAsApp(sim);
        _ = connection.CreateCommand("exec sp_setapprole 'appr', 'S3cret!Pass'").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("select id from away.dbo.remote").ExecuteScalar());
        AreEqual(916, ex.Number);
        Contains("\"app\"", ex.Message);
    }

    // ---- ownership chaining breaks at the database boundary ----

    [TestMethod]
    public void ViewOverAnotherDatabase_RequiresGrantOnTheBase()
    {
        // DB_CHAINING is off, so the dbo-owned view in `home` does not lend its
        // owner's rights to the `away` base table — the caller needs its own.
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim);
        sim.ExecuteBatches(
            "use home",
            "create view dbo.v_remote as select id from away.dbo.remote",
            "grant select on dbo.v_remote to homeuser");
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("select id from dbo.v_remote").ExecuteScalar());
        AreEqual(229, ex.Number);
        AreEqual("The SELECT permission was denied on the object 'remote', database 'away', schema 'dbo'.", ex.Message);
    }

    [TestMethod]
    public void ViewOverAnotherDatabase_WithBaseGrant_Succeeds()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim, "grant select on dbo.remote to awayuser");
        sim.ExecuteBatches(
            "use home",
            "create view dbo.v_remote as select id from away.dbo.remote",
            "grant select on dbo.v_remote to homeuser");
        using var connection = ConnectAsApp(sim);
        AreEqual(7, connection.CreateCommand("select id from dbo.v_remote").ExecuteScalar());
    }

    [TestMethod]
    public void InlineTvfOverAnotherDatabase_RequiresGrantOnTheBase()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim);
        sim.ExecuteBatches(
            "use home",
            "create function dbo.f_remote() returns table as return (select id from away.dbo.remote)",
            "grant select on dbo.f_remote to homeuser");
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("select id from dbo.f_remote()").ExecuteScalar());
        AreEqual(229, ex.Number);
        AreEqual("The SELECT permission was denied on the object 'remote', database 'away', schema 'dbo'.", ex.Message);
    }

    [TestMethod]
    public void ProcedureWritingToAnotherDatabase_RequiresGrantOnTheTarget()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim);
        sim.ExecuteBatches(
            "use home",
            "create procedure dbo.p_push as insert away.dbo.remote values (8)",
            "grant execute on dbo.p_push to homeuser");
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec dbo.p_push").ExecuteNonQuery());
        AreEqual(229, ex.Number);
        AreEqual("The INSERT permission was denied on the object 'remote', database 'away', schema 'dbo'.", ex.Message);
    }

    [TestMethod]
    public void ProcedureWritingToAnotherDatabase_WithTargetGrant_Succeeds()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim, "grant insert on dbo.remote to awayuser");
        sim.ExecuteBatches(
            "use home",
            "create procedure dbo.p_push as insert away.dbo.remote values (8)",
            "grant execute on dbo.p_push to homeuser");
        using var connection = ConnectAsApp(sim);
        AreEqual(1, connection.CreateCommand("exec dbo.p_push").ExecuteNonQuery());
    }

    [TestMethod]
    public void SameDatabaseView_StillChains()
    {
        // The break is specific to crossing databases: an ordinary same-database
        // chain still hides the base table from the check.
        var sim = TwoDatabaseFixture();
        sim.ExecuteBatches(
            "use home; create table dbo.other (id int not null); insert dbo.other values (42)",
            "create view dbo.v_local as select id from dbo.other",
            "grant select on dbo.v_local to homeuser");
        using var connection = ConnectAsApp(sim);
        AreEqual(42, connection.CreateCommand("select id from dbo.v_local").ExecuteScalar());
    }

    // ---- USE / ChangeDatabase ----

    [TestMethod]
    public void Use_RestrictedLogin_WithUserInTarget_SwitchesAndRebindsPrincipal()
    {
        var sim = TwoDatabaseFixture();
        CreateAwayUser(sim);
        using var connection = ConnectAsApp(sim);
        AreEqual("homeuser", connection.CreateCommand("select current_user").ExecuteScalar());
        _ = connection.CreateCommand("use away").ExecuteNonQuery();
        AreEqual("away|awayuser|app", connection.CreateCommand("select db_name() + '|' + current_user + '|' + system_user").ExecuteScalar());
    }

    [TestMethod]
    public void Use_RestrictedLogin_WithoutUserInTarget_Raises916()
    {
        var sim = TwoDatabaseFixture();
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("use away").ExecuteNonQuery());
        AreEqual(916, ex.Number);
        AreEqual("home", connection.CreateCommand("select db_name()").ExecuteScalar());
    }

    [TestMethod]
    public void ChangeDatabase_RestrictedLogin_WithGuestAccess_Switches()
    {
        // guest is accessible in master, so the login resolves there and the
        // switch stands — real allows it (the connect path already does).
        var sim = TwoDatabaseFixture();
        using var connection = ConnectAsApp(sim);
        connection.ChangeDatabase("master");
        AreEqual("master|guest", connection.CreateCommand("select db_name() + '|' + current_user").ExecuteScalar());
    }

    [TestMethod]
    public void ChangeDatabase_RestrictedLogin_WithoutUserInTarget_Raises916()
    {
        var sim = TwoDatabaseFixture();
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.ChangeDatabase("away"));
        AreEqual(916, ex.Number);
        AreEqual("home", connection.CreateCommand("select db_name()").ExecuteScalar());
    }
}

/// <summary>
/// Cross-database MVCC scoping: the commit-id sequence is instance-wide, so a
/// SNAPSHOT transaction fixes one stamp at its first data-access statement and
/// reads <em>every</em> database as of that instant; RCSI re-stamps per
/// statement. Both the versioning flags and the Msg 3952 gate follow the
/// table's own database, not the session's.
/// All probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CrossDatabaseSnapshotTests
{
    /// <summary>Two databases, each with one row, snapshot options per <paramref name="options"/>.</summary>
    private static Simulation TwoDatabaseFixture(string options)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database home; create database away");
        _ = sim.ExecuteNonQuery($"""
            alter database home set {options};
            alter database away set {options};
            use home;
            create table dbo.local (id int not null primary key, v int);
            insert dbo.local values (1, 100)
            """);
        _ = sim.ExecuteNonQuery("""
            use away;
            create table dbo.remote (id int not null primary key, v int);
            insert dbo.remote values (1, 700)
            """);
        return sim;
    }

    [TestMethod]
    public void SnapshotTransaction_FixesOneStampAtFirstRead_AcrossDatabases()
    {
        // Probe: the snapshot instant is the transaction's first data-access
        // statement wherever it lands, and a database first read later is still
        // read as of that instant — so the concurrent `away` commit is invisible.
        var sim = TwoDatabaseFixture("allow_snapshot_isolation on");
        using var siConn = sim.CreateOpenConnection();
        AreEqual(100, siConn.CreateCommand("use home; set transaction isolation level snapshot; begin tran; select v from dbo.local where id = 1").ExecuteScalar());

        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("update away.dbo.remote set v = 999 where id = 1").ExecuteNonQuery();

        AreEqual(700, siConn.CreateCommand("select v from away.dbo.remote where id = 1").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void SnapshotTransaction_CommitBeforeFirstRead_IsVisible()
    {
        // BEGIN TRAN alone doesn't fix the stamp — the first read does.
        var sim = TwoDatabaseFixture("allow_snapshot_isolation on");
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("use home; set transaction isolation level snapshot; begin tran").ExecuteNonQuery();

        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("update away.dbo.remote set v = 999 where id = 1").ExecuteNonQuery();

        AreEqual(999, siConn.CreateCommand("select v from away.dbo.remote where id = 1").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void Rcsi_ReStampsPerStatement_AcrossDatabases()
    {
        var sim = TwoDatabaseFixture("read_committed_snapshot on");
        using var reader = sim.CreateOpenConnection();
        AreEqual(100, reader.CreateCommand("use home; begin tran; select v from dbo.local where id = 1").ExecuteScalar());

        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("update away.dbo.remote set v = 999 where id = 1").ExecuteNonQuery();

        // A fresh statement takes a fresh stamp, so the committed write shows.
        AreEqual(999, reader.CreateCommand("select v from away.dbo.remote where id = 1").ExecuteScalar());
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void Msg3952_NamesTheTargetDatabase_NotTheSession()
    {
        // ALLOW_SNAPSHOT_ISOLATION is the *table's* database's flag.
        var sim = TwoDatabaseFixture("allow_snapshot_isolation on");
        _ = sim.ExecuteNonQuery("alter database away set allow_snapshot_isolation off");
        sim.AssertSqlError("""
            use home;
            set transaction isolation level snapshot;
            begin tran;
            select v from dbo.local where id = 1;
            select v from away.dbo.remote where id = 1
            """,
            3952,
            "Snapshot isolation transaction failed accessing database 'away' because snapshot isolation is not allowed in this database. Use ALTER DATABASE to allow snapshot isolation.");
    }

    [TestMethod]
    public void Rcsi_FollowsTheTargetDatabaseFlag()
    {
        // RCSI on in `away` only: a session in `home` (RCSI off) still reads
        // `away` versioned, so an uncommitted concurrent write doesn't block it.
        var sim = TwoDatabaseFixture("read_committed_snapshot off");
        _ = sim.ExecuteNonQuery("alter database away set read_committed_snapshot on");
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; update away.dbo.remote set v = 999 where id = 1").ExecuteNonQuery();

        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("use home; set lock_timeout 0").ExecuteNonQuery();
        AreEqual(700, reader.CreateCommand("select v from away.dbo.remote where id = 1").ExecuteScalar());

        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }
}
