using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes a view body and yields its row bytes. Synthesizes a
    /// <see cref="SimulatedDbCommand"/> over <see cref="View.BodyText"/>,
    /// builds a child <see cref="BatchContext"/> sharing the caller's
    /// connection (so the body sees the same schemas / tables / temp tables
    /// / current transaction), and dispatches the body's SELECT through the
    /// regular <see cref="Selection.Parse"/> / <see cref="Selection.Execute"/>
    /// pair. Recursion (a view referencing another view, or a view
    /// referencing a TVF / scalar UDF that re-enters via FROM) counts
    /// against the shared <see cref="SimulatedDbConnection.NestingLevel"/>
    /// — exceeding 32 raises Msg 217.
    /// </summary>
    internal IEnumerable<byte[]> InvokeView(BatchContext outerBatch, View view) =>
        outerBatch.Connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel
            ? throw SimulatedSqlException.MaximumNestingLevelExceeded()
            : InvokeViewCore(outerBatch, view);

    private IEnumerable<byte[]> InvokeViewCore(BatchContext outerBatch, View view)
    {
        var connection = outerBatch.Connection;
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // view.BodyText is the view's pre-validated stored body, not external input
        bodyCommand.CommandText = view.BodyText;
#pragma warning restore CA2100

        // Views have no parameters. The empty variables dict + dummy UdfFrame
        // keep the BatchContext signature uniform with scalar UDF / inline
        // TVF invocation — the body's RETURN <value> form is parse-time-
        // rejected here because UdfFrame is set, but that's harmless since
        // a view body is a plain SELECT with no RETURN.
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var dummyFrame = new UdfFrame(SqlType.Int32);
        // Body errors attribute to the outer statement that referenced the view
        // (probe-confirmed: real reports the outer SELECT's line, no procedure).
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame) { SuppressDiagnosticsResolution = true };
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            var bodySelection = Selection.Parse(parser, depth: 0);
            var resultSet = bodySelection.Execute(innerBatch, outerResolver: null);
            foreach (var rowBytes in resultSet.RowBytes)
                yield return rowBytes;
        }
        finally
        {
            connection.NestingLevel--;
        }
    }
}
