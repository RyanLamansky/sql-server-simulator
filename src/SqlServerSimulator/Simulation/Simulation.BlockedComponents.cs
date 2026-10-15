using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// <c>xp_cmdshell</c> and the OLE Automation procedures (<c>sp_OACreate</c>,
    /// <c>sp_OAMethod</c>, …): with their configuration option off, as a
    /// stock server ships it, each call is real's Msg 15281 naming the
    /// component, attributed to the procedure as called, whatever its
    /// arguments (probed 2026-10-15 against SQL Server 2025). With the
    /// option turned on through <c>sp_configure</c> the call would reach the
    /// host's shell or COM, which the simulator doesn't model.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeBlockedComponentProcedure(BatchContext batch, string systemProcName, string calledAs)
    {
        _ = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var shell = systemProcName == "xp_cmdshell";
        var component = shell ? "xp_cmdshell" : "Ole Automation Procedures";
        if (this.ConfigurationInUse(shell ? XpCmdShellConfigurationId : OleAutomationConfigurationId) == 0)
            throw AtProcedureLine(SimulatedSqlException.ComponentBlocked(systemProcName, component), calledAs, 1);
        throw new NotSupportedException($"{systemProcName} with '{component}' enabled isn't modeled by the simulator, which runs no operating-system commands or COM objects.");
    }
}
