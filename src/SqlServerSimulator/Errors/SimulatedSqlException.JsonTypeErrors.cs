using System.Globalization;

namespace SqlServerSimulator;

// The json data type's own diagnostics, probed 2026-09-26 against SQL Server
// 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 13609 at state 9: text converted to <c>json</c> isn't a JSON
    /// object or array. <paramref name="character"/> is the offending UTF-8
    /// byte read as a single-byte character (<c>.</c> past the end) and
    /// <paramref name="position"/> its zero-based byte offset.
    /// </summary>
    internal static SimulatedSqlException JsonTypeMalformed(char character, int position) =>
        JsonInvalidText(character, position, state: 9);

    /// <summary>
    /// Msg 1007 at class 16: a number in text converted to <c>json</c> can't be
    /// held as a <c>decimal(38)</c> — state 3 for a plain number, state 5 for
    /// one written with an exponent. <paramref name="token"/> is the number
    /// as written.
    /// </summary>
    internal static SimulatedSqlException JsonNumberOutOfRange(string token, byte state) =>
        new($"The number '{token}' is out of the range for numeric representation (maximum precision 38).", 1007, 16, state);

    /// <summary>Msg 13645: text converted to <c>json</c> nests containers more than 128 deep.</summary>
    internal static SimulatedSqlException JsonNestingTooDeep() =>
        new("Nested level of JSON document exceeds limit 128.", 13645, 16, 1);

    /// <summary>Msg 13647: an object or array in text converted to <c>json</c> holds more than 65535 members.</summary>
    internal static SimulatedSqlException JsonTooManyItems() =>
        new("Number of items in one object/array exceeds limit 65535 in JSON type.", 13647, 16, 1);

    /// <summary>Msg 13649: text converted to <c>json</c> names more than 32768 distinct property names.</summary>
    internal static SimulatedSqlException JsonTooManyKeys() =>
        new("Number of unique keys exceeds limit 32768 in JSON type.", 13649, 16, 1);

    /// <summary>
    /// Msg 13639: a <c>json</c> value's text is longer than the bounded
    /// character type it is converted to, which real refuses rather than
    /// truncating.
    /// </summary>
    internal static SimulatedSqlException JsonTargetTooSmall() =>
        new("Target string size is too small to represent the JSON instance.", 13639, 16, 1);

    /// <summary>
    /// Msg 13636: a <c>json</c> value met a comparison (state 1) or a sorting
    /// or grouping slot — ORDER BY, GROUP BY, a window's ORDER BY or
    /// PARTITION BY (state 2).
    /// </summary>
    internal static SimulatedSqlException JsonCannotBeComparedOrSorted(byte state) =>
        new("The JSON data type cannot be compared or sorted, except when using the IS NULL operator.", 13636, 16, state);

    /// <summary>
    /// Msg 102 at state 19: a JSON builder's <c>RETURNING</c> clause names a
    /// type other than <c>json</c>. Real names the clause itself, and for an
    /// <c>nvarchar</c> target appends its own hint to the quoted text.
    /// </summary>
    internal static SimulatedSqlException JsonReturningNotJson(bool nvarcharTarget) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Incorrect syntax near '{(nvarcharTarget ? "RETURNING. Supported Syntax is RETURNING JSON" : "RETURNING")}'."), 102, 15, 19);
}
