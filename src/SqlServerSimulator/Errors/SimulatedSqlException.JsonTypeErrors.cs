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

    /// <summary>
    /// Msg 13660: an advanced JSON array accessor — a wildcard, range, list or
    /// <c>last</c> — somewhere real's reader doesn't take it.
    /// <paramref name="what"/> opens the message and the state names the
    /// refusal: 1 a range written high-to-low, 2 <c>last</c> over a text
    /// document or a wildcard in <c>OPENJSON</c>'s default schema over one,
    /// 4 <c>JSON_MODIFY</c> over text, 5 a list over text or a many-valued
    /// <c>JSON_MODIFY</c> path over <c>json</c> (probed 2026-09-27 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException JsonAdvancedAccessorNotSupported(string what, byte state) =>
        new($"{what} not yet supported for advanced JSON array accessors.", 13660, 16, state);

    /// <summary>
    /// Msg 13665: a wildcard path in <c>OPENJSON</c> over a <c>json</c>
    /// document — state 5 as the document path, 3 as a <c>WITH</c> column's.
    /// </summary>
    internal static SimulatedSqlException JsonOpenJsonComplexPath(byte state) =>
        new("OpenJson support with complex path parameters not yet supported for JSON native data type.", 13665, 16, state);

    /// <summary>Msg 13692: <c>JSON_CONTAINS</c>'s fourth argument is neither 0 nor 1.</summary>
    internal static SimulatedSqlException JsonContainsModeInvalid() =>
        new("The comparison_mode argument of JSON_CONTAINS must be 0 or 1.", 13692, 16, 1);

    /// <summary>
    /// Msg 102 at state 29: <c>JSON_VALUE … RETURNING</c> names a type real
    /// doesn't return — <paramref name="typeName"/> as written, or
    /// <c>sys.vector</c> for <c>vector</c>.
    /// </summary>
    internal static SimulatedSqlException JsonValueReturningType(string typeName) =>
        new($"Incorrect syntax near '{typeName}'.", 102, 15, 29);

    /// <summary>
    /// Msg 5302 for the <c>json</c> type's <c>.modify()</c> on a NULL column
    /// or variable — the xml mutator's wording, but unlike it this one ends
    /// the batch and rolls the transaction back as under
    /// <c>SET XACT_ABORT ON</c> (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException JsonMutatorOnNullValue(string name) =>
        new($"Mutator 'modify()' on '{name}' cannot be called on a null value.", 5302, 16, 1) { AbortsAsUnderXactAbort = true };

    // CREATE JSON INDEX's refusals, probed 2026-09-27 against SQL Server 2025.

    /// <summary>Msg 13675: a JSON index on a temp table, named as written.</summary>
    internal static SimulatedSqlException JsonIndexOnTempObject(string writtenName) =>
        new($"Cannot create a JSON index on temp objects. '{writtenName}' is identified as a temp object.", 13675, 16, 1);

    /// <summary>Msg 13680: the indexed column isn't <c>json</c>.</summary>
    internal static SimulatedSqlException JsonIndexColumnNotJson(string columnName, string tableName) =>
        new($"Column '{columnName}' on table '{tableName}' is not of JSON data type, which is required to create a JSON index on it.", 13680, 16, 1);

    /// <summary>Msg 13672: the table has no clustered primary key of fewer than 32 columns.</summary>
    internal static SimulatedSqlException JsonIndexNeedsClusteredPrimaryKey(string tableName) =>
        new($"Table '{tableName}' needs to have a clustered primary key with less than 32 columns in it in order to create a JSON index on it.", 13672, 16, 1);

    /// <summary>Msg 13681: the column already carries a JSON index, named here.</summary>
    internal static SimulatedSqlException JsonIndexAlreadyOnColumn(string existingIndexName, string columnName, string tableName) =>
        new($"A JSON index '{existingIndexName}' already exists on column '{columnName}' on table '{tableName}', and multiple JSON indexes per column are not allowed.", 13681, 16, 1);

    /// <summary>Msg 13685 state 2: <c>DROP_EXISTING = ON</c> found no JSON index of that name on the column.</summary>
    internal static SimulatedSqlException JsonIndexNotFound(string indexName, string columnName, string tableName) =>
        new($"A JSON index '{indexName}' cannot be found on column '{columnName}' on table '{tableName}'.", 13685, 16, 2);

    /// <summary>
    /// Msg 13683: the <c>FOR</c> clause's paths overlap (state 1), one uses
    /// <c>[*]</c> (state 2), or one uses another advanced accessor (state 3).
    /// </summary>
    internal static SimulatedSqlException JsonIndexPathsInvalid(byte state) =>
        new("Invalid JSON paths in JSON index.", 13683, 16, state);

    /// <summary>Msg 13688: an <c>ALTER INDEX</c> form a JSON index takes no part in — <c>SET</c>, <c>RESUME</c>, <c>PAUSE</c>, <c>ABORT</c>.</summary>
    internal static SimulatedSqlException JsonIndexAlterOptionsInvalid() =>
        new("One or more of the specified ALTER INDEX options is invalid for a JSON index.", 13688, 16, 1);

    /// <summary>Msg 7731 state 5: a <c>REBUILD</c> / <c>REORGANIZE</c> of a JSON index naming a partition number.</summary>
    internal static SimulatedSqlException PartitionNumberOnJsonIndex(string indexName) =>
        new($"Cannot specify partition number in Alter index statement to rebuild or reorganize a partition of JSON index '{indexName}'.", 7731, 16, 5);

    /// <summary>
    /// Msg 153: a <c>REBUILD</c> option a JSON index refuses when turned on; the state names the option
    /// (<c>ONLINE</c> 37, <c>STATISTICS_INCREMENTAL</c> 40, <c>IGNORE_DUP_KEY</c> 36, <c>SORT_IN_TEMPDB</c> 42,
    /// <c>STATISTICS_NORECOMPUTE</c> 43, <c>XML_COMPRESSION</c> 45).
    /// </summary>
    internal static SimulatedSqlException InvalidJsonIndexRebuildOption(string optionName, byte state) =>
        new($"Invalid usage of the option {optionName} in the ALTER INDEX REBUILD statement.", 153, 15, state);

    /// <summary>Msg 35375 state 3: <c>REORGANIZE … COMPRESS_ALL_ROW_GROUPS = ON</c> over an index that isn't a clustered columnstore.</summary>
    internal static SimulatedSqlException CompressAllRowGroupsNeedsColumnstore() =>
        new("ALTER INDEX REORGANIZE statement option COMPRESS_ALL_ROW_GROUPS can only be used with clustered columnstore indexes.", 35375, 16, 3);

    /// <summary>Msg 153 state 35: an index option <c>CREATE JSON INDEX</c> doesn't take, echoed as written.</summary>
    internal static SimulatedSqlException InvalidJsonIndexOption(string optionName) =>
        new($"Invalid usage of the option {optionName} in the CREATE JSON INDEX statement.", 153, 15, 35);

    /// <summary>Msg 155 for an option no index statement knows, which real follows with Msg 153 state 35.</summary>
    internal static SimulatedSqlException UnrecognizedJsonIndexOption(string optionName) =>
        Aggregate([UnrecognizedIndexOption(optionName, "CREATE JSON INDEX"), InvalidJsonIndexOption(optionName)]);

    /// <summary>Msg 3766 state 4: <c>DROP INDEX table.index</c> naming a JSON index.</summary>
    internal static SimulatedSqlException JsonIndexDropNeedsOnSyntax(string writtenName) =>
        new($"Cannot drop JSON index '{writtenName}' using old 'Table.Index' syntax, use 'Index ON Table' syntax instead.", 3766, 16, 4);

    /// <summary>Msg 3767 then 3727: dropping the primary key of a table with a JSON index.</summary>
    internal static SimulatedSqlException PrimaryKeyDropBlockedByJsonIndex(string constraintName) =>
        FollowedByConstraintNotDropped(new($"Could not drop the primary key constraint '{constraintName}' because the table has a JSON index.", 3767, 16, 1));
}
