namespace SqlServerSimulator.Parser;

/// <summary>
/// What a DML statement nested as an <c>INSERT … SELECT</c>'s source left at
/// its split point: its plan, which its source runs as the outer statement
/// reads it; null where its parse ended before reaching one, as a skip-mode
/// parse may.
/// </summary>
internal sealed class NestedWriteCapture
{
    public DmlStatementPlan? Plan;
}
