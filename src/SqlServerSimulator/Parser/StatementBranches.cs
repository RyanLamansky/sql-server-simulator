using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The query specifications of a statement's own query in written order —
/// whether each assigns variables and whether it retrieves columns, a select
/// list mixing the two doing both, and the set operator written before it —
/// from which real's refusal of an assignment in a set operation is read
/// (<see cref="ParserContext.StatementBranches"/>).
/// </summary>
internal sealed class StatementBranches
{
    public readonly List<(bool Assigns, bool Retrieves, ReservedKeyword? Operator)> Branches = [];

    /// <summary>Whether a branch read so far assigns variables.</summary>
    public bool Assigns;

    /// <summary>
    /// The first branch's own Msg 141 for a select list mixing assignment and
    /// retrieval, which real reports ahead of the set operation's refusal and
    /// a later branch's list doesn't earn, at its line as the batch counts it.
    /// </summary>
    public SimulatedSqlException? MixedFirstBranch;

    private ReservedKeyword? pendingOperator;

    private bool pendingMixed;

    /// <summary>Notes the set operator the next branch read follows.</summary>
    public void FollowWith(ReservedKeyword setOperator) => this.pendingOperator = setOperator;

    /// <summary>Notes that the branch reading now mixes assignment and retrieval.</summary>
    public void NoteMixed() => this.pendingMixed = true;

    public void Add(bool assignsOnly)
    {
        var assigns = assignsOnly || this.pendingMixed;
        this.Branches.Add((assigns, !assignsOnly, this.pendingOperator));
        this.pendingOperator = null;
        this.pendingMixed = false;
        this.Assigns |= assigns;
    }
}
