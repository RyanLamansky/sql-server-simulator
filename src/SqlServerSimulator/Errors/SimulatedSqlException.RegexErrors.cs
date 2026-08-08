using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator;

// Factories for the REGEXP_* family's own 193xx diagnostics. All
// probe-confirmed against SQL Server 2025 (17.0.4065.4), including the state
// values — real shifts a pattern error's state between the scalar / predicate
// members and the two rowset members, and gives each (function, numeric
// argument) pair its own state under Msg 19301.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server's Msg 19300 — the pattern didn't compile, with RE2's
    /// own parser diagnostic quoted inside real's wrapper text.
    /// </summary>
    internal static SimulatedSqlException RegexInvalidPattern(string pattern, string detail, RegexCallSite callSite) =>
        new($"An invalid Pattern '{pattern}' was provided. Error '{detail}' occurred during evaluation of the Pattern.",
            19300, 16, callSite == RegexCallSite.Rowset ? (byte)2 : (byte)1);

    /// <summary>
    /// Mimics SQL Server's Msg 19301 — a numeric argument fell below its
    /// minimum. Real's wording is loose about the bound in two places and the
    /// simulator mirrors it rather than correcting it: <c>REGEXP_INSTR</c>'s
    /// <c>RETURN_OPTION</c> reports "greater than or equal to 0" while
    /// rejecting 2, and its <c>GROUP</c> reports "greater than or equal to 1"
    /// while accepting 0.
    /// </summary>
    internal static SimulatedSqlException RegexArgumentBelowMinimum(string argumentName, int minimum, int value, string functionName, byte state) =>
        new($"'{argumentName}' value should be greater than or equal to {minimum} but '{value}' is provided in '{functionName}' function.",
            19301, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 19303 — the flags argument carried a character
    /// outside <c>{c, i, s, m}</c>. Real quotes the whole flags string, not the
    /// offending character, and the match is case-sensitive (<c>'I'</c> is
    /// rejected).
    /// </summary>
    internal static SimulatedSqlException RegexInvalidFlags(string flags) =>
        new($"Invalid flag provided. '{flags}' are not valid flags. Only {{c,i,s,m}} flags are valid.", 19303, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 19307 — a <c>)</c> with no group to close.
    /// </summary>
    internal static SimulatedSqlException RegexUnexpectedCloseParen(string pattern, RegexCallSite callSite) =>
        new($"Encountered an unexpected ')' in the Pattern {pattern}.",
            19307, 16, callSite == RegexCallSite.Rowset ? (byte)2 : (byte)1);

    /// <summary>
    /// Mimics SQL Server's Msg 19308 state 1 / 3 — a group left unclosed at the
    /// end of the pattern.
    /// </summary>
    internal static SimulatedSqlException RegexMissingCloseParen(string pattern, RegexCallSite callSite) =>
        new($"Missing ')' in the Pattern {pattern}.",
            19308, 16, callSite == RegexCallSite.Rowset ? (byte)3 : (byte)1);

    /// <summary>
    /// Mimics SQL Server's Msg 19308 state 2 / 4 — a character class left
    /// unclosed at the end of the pattern.
    /// </summary>
    internal static SimulatedSqlException RegexMissingCloseBracket(string pattern, RegexCallSite callSite) =>
        new($"Missing ']' in the Pattern {pattern}.",
            19308, 16, callSite == RegexCallSite.Rowset ? (byte)4 : (byte)2);

    /// <summary>
    /// Mimics SQL Server's Msg 19309 — the pattern ended on a lone backslash.
    /// </summary>
    internal static SimulatedSqlException RegexTrailingBackslash(string pattern, RegexCallSite callSite) =>
        new($"Invalid trailing backslash (\\) provided at the end of the Pattern {pattern}.",
            19309, 16, callSite == RegexCallSite.Rowset ? (byte)2 : (byte)1);
}
