using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sa</c> carries <c>CHECK_POLICY = ON</c>, so a password real's policy
/// refuses is refused ahead of the recording the simulator doesn't model
/// (probed 2026-09-30 against SQL Server 2025 with a too-short password; a
/// password real accepts stays a <see cref="NotSupportedException"/>).
/// </summary>
[TestClass]
public sealed class SaPasswordPolicyTests
{
    [TestMethod]
    public void ATooShortPassword_Is33062_ForBothForms()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("alter login sa with password = 'abc'", 33062);
        _ = simulation.AssertSqlError("exec sp_password NULL, 'abc', 'sa'", 33062);
    }

    [TestMethod]
    public void AnAcceptablePassword_IsStillUnmodeled()
        => _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("alter login sa with password = 'Str0ng!Passw0rd'"));
}
