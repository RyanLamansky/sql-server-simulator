using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Handles <c>EXEC xp_qv</c> (also <c>dbo.xp_qv</c> /
    /// <c>master.dbo.xp_qv</c> from any current database), SSMS's AlwaysOn-
    /// availability probe (<c>EXECUTE @rc = master.dbo.xp_qv N'&lt;hash&gt;',
    /// @@SERVICENAME</c>). Consumes and ignores its arguments (the feature
    /// hash isn't validated), yields no result set, and returns status 0.
    /// A real HADR-enabled instance returns 2; the simulator deliberately
    /// reports not-available, matching <c>SERVERPROPERTY('IsHadrEnabled')</c>
    /// = 0, so <c>SELECT ISNULL(@rc, -1)</c> yields 0 and Object Explorer's
    /// Databases node populates instead of the SMO probe aborting.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpQv(BatchContext batch, string? returnCodeVariableName)
    {
        _ = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (returnCodeVariableName is not null)
        {
            var slot = batch.GetVariableSlot(returnCodeVariableName);
            slot.Value = SqlValue.FromInt32(0).CoerceTo(slot.DeclaredType);
        }
    }
}
