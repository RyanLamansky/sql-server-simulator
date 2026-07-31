namespace SqlServerSimulator.Parser;

/// <summary>
/// Constructs SQL Server forbids in a recursive CTE's recursive member,
/// tracked while a WITH branch parses. Each maps to the error real raises
/// when the branch turns out to be recursive — see
/// <c>Simulation.With.cs</c>, which does the raising.
/// </summary>
internal struct RecursiveMemberConstructs
{
    /// <summary>SELECT DISTINCT anywhere in the member — Msg 460.</summary>
    public bool Distinct;

    /// <summary>TOP, OFFSET or FETCH anywhere in the member — Msg 461.</summary>
    public bool TopOrOffset;

    /// <summary>An outer join anywhere in the member — Msg 462.</summary>
    public bool OuterJoin;

    /// <summary>GROUP BY, HAVING or an aggregate anywhere in the member — Msg 467.</summary>
    public bool GroupingOrAggregate;
}
