using System.Globalization;

namespace SqlServerSimulator;

// The vector data type's own diagnostics, probed 2026-09-26 against SQL Server
// 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 195: the second argument of a <c>vector(n, …)</c> type spec names a
    /// base type other than <c>float32</c> — <c>float16</c> included unless
    /// the database's <c>PREVIEW_FEATURES</c> is on.
    /// </summary>
    internal static SimulatedSqlException VectorBaseTypeNotRecognized(string baseType) =>
        new($"'{baseType}' is not a recognized vector base type.", 195, 15, 1);

    /// <summary>
    /// Msg 2717: a <c>vector</c> declared wider than 1998 dimensions — 3996
    /// for a <c>float16</c> one — naming the column at state 4 (5) in a column
    /// declaration and the type at state 3 (2) everywhere else.
    /// </summary>
    internal static SimulatedSqlException VectorSizeExceedsMaximum(int requested, string? columnName, bool float16 = false)
    {
        var maximum = float16 ? "3996" : "1998";
        return columnName is not null
            ? new($"The size ({requested.ToString(CultureInfo.InvariantCulture)}) given to the column '{columnName}' exceeds the maximum allowed ({maximum}).", 2717, 15, float16 ? (byte)5 : (byte)4)
            : new($"The size ({requested.ToString(CultureInfo.InvariantCulture)}) given to the type 'vector' exceeds the maximum allowed ({maximum}).", 2717, 15, float16 ? (byte)2 : (byte)3);
    }

    /// <summary>
    /// Msg 42204: two vectors of different dimension counts met. The state
    /// names the site: 4 when text converted to a vector holds the wrong
    /// count, 3 in <c>VECTOR_DISTANCE</c>, and 1 where one vector type meets
    /// another (a unification, an assignment, a <c>CAST</c> between the two,
    /// <c>ALTER COLUMN</c>).
    /// </summary>
    internal static SimulatedSqlException VectorDimensionsMismatch(int first, int second, byte state) =>
        new($"The vector dimensions {first.ToString(CultureInfo.InvariantCulture)} and {second.ToString(CultureInfo.InvariantCulture)} do not match.", 42204, 16, state);

    /// <summary>
    /// Msg 13670: text converted to a vector is JSON, but not a flat array of
    /// numbers. <paramref name="reason"/> is real's own phrase, capitalization
    /// and misspelling included (<c>Boolean not supported</c> for <c>true</c>
    /// but <c>Boolean Not Supported</c> for <c>false</c>,
    /// <c>Empty object Not Suppoted</c>), and the state tracks it.
    /// </summary>
    internal static SimulatedSqlException VectorJsonInvalid(string reason, byte state) =>
        new($"Input JSON is not a valid Vector : '{reason}'.", 13670, 16, state);

    /// <summary>
    /// Msg 13609 at state 9: text converted to a vector isn't well-formed JSON.
    /// <paramref name="character"/> is the offending UTF-8 byte read as a
    /// single-byte character (<c>.</c> past the end) and
    /// <paramref name="position"/> its zero-based byte offset.
    /// </summary>
    internal static SimulatedSqlException VectorJsonMalformed(char character, int position) =>
        JsonInvalidText(character, position, state: 9);

    /// <summary>
    /// Msg 42241: an element of text converted to a vector lies outside
    /// float32's range (state 1), or inside it but outside half precision's
    /// for a <c>float16</c> vector (state 2); the message names the target's
    /// base type either way.
    /// </summary>
    internal static SimulatedSqlException VectorElementOutOfRange(bool float16 = false, byte state = 1) =>
        new($"Input JSON contains out-of-range values for {(float16 ? "float16" : "float32")}.", 42241, 16, state);

    /// <summary>
    /// Msg 42238: a vector converted to one of the other base type — a
    /// <c>CAST</c>, an assignment, or two arms unified, which settle on
    /// float32.
    /// </summary>
    internal static SimulatedSqlException VectorBaseTypeConversion(string from, string to) =>
        new($"Conversion of vector from data type {from} to {to} is not allowed.", 42238, 16, 1);

    /// <summary>Msg 42243: <c>VECTOR_DISTANCE</c> (or <c>VECTOR_SEARCH</c>) over a float16 and a float32 vector, raised as the statement runs and ending the batch.</summary>
    internal static SimulatedSqlException VectorDistanceBaseTypesDiffer() =>
        new("VECTOR_DISTANCE function does not support different base types for vector arguments.", 42243, 16, 1) { TerminatesBatch = true };

    /// <summary>Msg 42246: <c>VECTOR_NORM</c> or <c>VECTOR_NORMALIZE</c> over a float16 vector.</summary>
    internal static SimulatedSqlException VectorNormFloat16(string functionName) =>
        new($"{functionName} function does not support vector with base type float16.", 42246, 16, 1);

    /// <summary>
    /// Msg 42211: a vector's text form is longer than the character type it
    /// is converted to, which real refuses rather than truncating.
    /// </summary>
    internal static SimulatedSqlException VectorTruncation() =>
        new("Truncation of vector is not allowed during the conversion. Ensure the vector size is appropriate before conversion.", 42211, 16, 1);

    /// <summary>
    /// Msg 42213: a vector in a sorting or grouping slot — ORDER BY, GROUP BY,
    /// a window's ORDER BY or PARTITION BY.
    /// </summary>
    internal static SimulatedSqlException VectorCannotBeComparedOrSorted() =>
        new("The vector data types cannot be compared or sorted, except when using the IS NULL operator.", 42213, 16, 1);

    /// <summary>Msg 42201: <c>VECTOR_DISTANCE</c>'s metric isn't <c>cosine</c>, <c>euclidean</c> or <c>dot</c>.</summary>
    internal static SimulatedSqlException VectorDistanceMetricNotSupported(string metric) =>
        new($"The requested distance metric '{metric}' is not supported by vector_distance. Provide a valid distance metric.", 42201, 16, 2);

    /// <summary>Msg 42210: <c>VECTOR_NORM</c> / <c>VECTOR_NORMALIZE</c>'s norm isn't <c>norm1</c>, <c>norm2</c> or <c>norminf</c>.</summary>
    internal static SimulatedSqlException VectorNormNotSupported(string norm) =>
        new($"The requested norm function '{norm}' is not supported by vector_norm/vector_normalize. Please provide a valid norm function.", 42210, 16, 1);

    /// <summary>Msg 1760: a CHECK constraint whose expression reads a vector column.</summary>
    internal static SimulatedSqlException CheckConstraintOnVectorColumn() =>
        new("Constraints of type CHECK cannot be created on columns of type vector.", 1760, 16, 0);

    /// <summary>
    /// Msg 1978: a vector column (state 4), a json column (state 3) or a CLR
    /// type column not marked <c>IsByteOrdered</c> (state 1) named as
    /// a key of a <c>CREATE INDEX</c> or <c>CREATE STATISTICS</c> — where a key
    /// constraint takes Msg 1919 instead. The table is named as the statement wrote it.
    /// </summary>
    internal static SimulatedSqlException VectorKeyColumnInvalid(string columnName, string tableName, byte state = 4) =>
        new($"Column '{columnName}' in table '{tableName}' is of a type that is invalid for use as a key column in an index or statistics.", 1978, 16, state);
}
