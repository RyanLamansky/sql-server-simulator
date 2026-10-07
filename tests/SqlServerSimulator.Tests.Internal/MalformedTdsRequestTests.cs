using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Requests no driver sends: a payload that ends inside a value is malformed
/// traffic, which ends the session rather than reaching the engine.
/// </summary>
[TestClass]
public sealed class MalformedTdsRequestTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RpcEndingInsideTheProcedureName_EndsTheSession()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var client = await RawTdsClient.ConnectAsync(listener, "simulated", TestContext.CancellationToken);
        // A procedure name announced as ten characters, of which one arrives.
        IsEmpty(await client.RpcAsync([10, 0, (byte)'s', 0], TestContext.CancellationToken));
    }
}
