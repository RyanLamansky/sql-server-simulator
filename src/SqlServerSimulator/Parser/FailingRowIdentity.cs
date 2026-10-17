using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The explicit identity value of the row an <c>INSERT</c> under
/// <c>IDENTITY_INSERT</c> fails on. Real computes that cell ahead of the
/// row's others and moves <c>IDENT_CURRENT</c> to it whichever cell failed
/// (probed 2026-10-17 against SQL Server 2025: <c>(v, id) VALUES (1/0, 20)</c>
/// leaves it at 20), so a select list raising in a row reads the cell on its
/// own once the row has failed, when the list is the one the source projects.
/// </summary>
internal sealed class FailingRowIdentity(Expression? cell)
{
    /// <summary>The source select list's identity cell; null when the source
    /// keeps no select list of its own.</summary>
    public readonly Expression? Cell = cell;

    /// <summary>The failing row's identity value, once read; null while no
    /// row failed or when its identity cell fails too.</summary>
    public SqlValue? Value;

    /// <summary>
    /// Reads the failing row's identity cell when <paramref name="expressions"/>
    /// is the select list carrying it, against the failing row's
    /// <paramref name="runtime"/>.
    /// </summary>
    public void Read(List<Expression> expressions, RuntimeContext runtime)
    {
        if (this.Cell is not { } cell || this.Value is not null)
            return;
        foreach (var expression in expressions)
        {
            if (ReferenceEquals(expression, cell))
            {
                this.Read(cell, runtime);
                return;
            }
        }
    }

    /// <summary>Computes <paramref name="cell"/> as the failing row's identity
    /// value, leaving none when it fails too.</summary>
    public void Read(Expression cell, RuntimeContext runtime)
    {
        try
        {
            this.Value = cell.Run(runtime);
        }
        catch (SimulatedSqlException)
        {
            // The identity cell itself is what fails: the row moves nothing.
        }
    }
}
