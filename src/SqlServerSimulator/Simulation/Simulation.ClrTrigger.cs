using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Runs a CLR trigger's method in place of a T-SQL body, with
    /// <c>SqlContext.TriggerContext</c> describing the fire and
    /// <c>SqlContext.Pipe</c> sending into the firing statement's buffered
    /// outcomes. Its caller has already pushed the trigger frame and set up
    /// the body's scope, as it does for a T-SQL body.
    /// </summary>
    /// <remarks>
    /// Probed 2026-09-28 against SQL Server 2025: <c>TriggerAction</c> is the
    /// DML verb or the DDL event type's number; <c>ColumnCount</c> is the
    /// parent's column count (0 for a DDL trigger); <c>IsUpdatedColumn</c> is
    /// true for every column of an <c>INSERT</c> or a <c>DELETE</c> and for the
    /// <c>SET</c> clause's columns of an <c>UPDATE</c>; <c>EventData</c> is the
    /// DDL event's document and <see langword="null"/> for a DML trigger. A
    /// <c>Send(string)</c> is a class-0 state-2 message at line 1 naming the
    /// trigger, and a throw is Msg 6522 state 1 at line 1, which ends the
    /// firing statement as a T-SQL body's error does. <c>INSERTED</c> and
    /// <c>DELETED</c> are reached only through the context connection, whose
    /// commands carry the trigger's frame.
    /// </remarks>
    private static void RunClrTrigger(BatchContext outerBatch, TriggerFrame frame, ClrEntryPoint entry, string triggerName)
    {
        if (!outerBatch.Connection.Simulation.EnableClr)
            throw SimulatedSqlException.ClrExecutionDisabled();

        (int Action, bool[] UpdatedColumns, string? EventData) fired;
        if (frame.Trigger is { } trigger)
        {
            var columns = frame.Inserted!.Columns;
            var updated = new bool[columns.Length];
            for (var i = 0; i < updated.Length; i++)
            {
                updated[i] = frame.FiringAction != TriggerActions.Update
                    || frame.IsColumnUpdated(trigger.Parent is View ? i + 1 : columns[i].ColumnId);
            }

            fired = (frame.FiringAction switch
            {
                TriggerActions.Insert => 1,
                TriggerActions.Update => 2,
                _ => 3,
            }, updated, null);
        }
        else
        {
            fired = (frame.DdlEventType, [], frame.DdlEventData);
        }

        var outcomes = new List<SimulatedStatementOutcome>();
        var pipe = new ClrProcedurePipe(outerBatch, triggerName, outcomes, lineNumber: 1);
        var contextConnection = entry.Assembly.UsesServerContext
            ? new ClrContextConnection(outerBatch, triggerName, entry.Assembly, pipe, frame, isFunction: false)
            : null;
        string? report = null;
        SimulatedSqlException? failure;
        try
        {
            using (entry.Assembly.UsesServerContext ? ClrHost.Enter(pipe.Sink, fired, contextConnection) : default(ClrHost.RoutineScope?))
                _ = entry.Method!.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            report = ClrExceptionReport.Describe(ex.InnerException, entry.Method!);
        }
        finally
        {
            failure = contextConnection?.Leave(report);
        }

        pipe.Settle(completed: report is null);
        if (report is not null)
            failure ??= SimulatedSqlException.ClrRoutineThrew(triggerName, report, state: 1);
        if (failure is not null)
        {
            failure.PreserveDiagnostics(1, triggerName);
            // The body runs under XACT_ABORT ON, as a T-SQL body does, so the
            // throw ends the firing batch and its transaction the way an
            // error from a body statement would.
            ApplyXactAbortPromotion(outerBatch.Connection, failure);
        }

        if (outcomes.Count > 0)
            (outerBatch.PendingTriggerOutcomes ??= []).AddRange(outcomes);
        if (failure is not null)
            throw failure;
    }
}
