namespace SqlServerSimulator;

/// <summary>
/// Verifies that a variety of set commands are processed without error.
/// </summary>
[TestClass]
public class SetTests
{
    [TestMethod]
    public void SetNoCountOn()
        => new Simulation().ExecuteNonQuery("SET NOCOUNT ON");

    [TestMethod]
    public void SetImplicitTransactionsOff()
        => new Simulation().ExecuteNonQuery("SET IMPLICIT_TRANSACTIONS OFF");
}
