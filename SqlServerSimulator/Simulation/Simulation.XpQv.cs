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
    /// hash isn't validated), yields no result set, and returns status
    /// <strong>2</strong> — AlwaysOn <em>available</em>. This is the
    /// edition-capability answer (the simulator reports
    /// <c>SERVERPROPERTY('EngineEdition')</c> = 3 / Enterprise, which
    /// supports AlwaysOn), which is a DIFFERENT axis from whether AlwaysOn
    /// is <em>enabled/configured</em> (<c>SERVERPROPERTY('IsHadrEnabled')</c>
    /// = 0, no availability groups) — probe-confirmed against SQL Server 2025:
    /// a normal Enterprise instance with no AGs returns xp_qv = 2 and
    /// IsHadrEnabled = 0. SMO's Object-Explorer Databases enumeration is
    /// HADR-aware and keys off this value; returning the earlier
    /// (edition-inconsistent) 0 made SMO take a degraded path and skip the
    /// user-database enumeration entirely. With 2, SMO issues its standard
    /// HADR-aware enumeration, which resolves against the empty
    /// availability-group DMVs and the per-database <c>sys.database_mirroring</c>
    /// / <c>sys.master_files</c> views.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpQv(BatchContext batch, string? returnCodeVariableName)
    {
        _ = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (returnCodeVariableName is not null)
        {
            var slot = batch.GetVariableSlot(returnCodeVariableName);
            slot.Value = SqlValue.FromInt32(2).CoerceTo(slot.DeclaredType);
        }
    }
}
