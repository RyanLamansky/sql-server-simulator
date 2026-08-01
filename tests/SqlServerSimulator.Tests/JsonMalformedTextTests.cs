using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Msg 13609 — the document argument of a JSON function isn't JSON text.
/// Every expectation here was probed verbatim against SQL Server 2025 CU7.
/// </summary>
/// <remarks>
/// The shape of the rule is a reader that runs left to right and stops as
/// soon as the path is satisfied, so the same document raises for one path
/// and answers for another. The path's <c>lax</c> / <c>strict</c> prefix
/// doesn't enter into it.
/// </remarks>
[TestClass]
public sealed class JsonMalformedTextTests
{
    private const string Prefix = "JSON text is not properly formatted. Unexpected character ";

    /// <summary>
    /// Only an object or an array is JSON text: a root-level scalar is
    /// malformed input, reported at its first character.
    /// </summary>
    [TestMethod]
    [DataRow("'1'", '1', 0)]
    [DataRow("'\"abc\"'", '"', 0)]
    [DataRow("'true'", 't', 0)]
    [DataRow("'null'", 'n', 0)]
    [DataRow("'-1.5e3'", '-', 0)]
    public void JsonValue_RootScalar_RaisesMsg13609(string document, char character, int position)
        => new Simulation().AssertSqlError(
            $"select json_value({document}, '$.a')",
            13609,
            $"{Prefix}'{character}' is found at position {position}.");

    /// <summary>Running off the end of the text names <c>.</c> at the length.</summary>
    [TestMethod]
    [DataRow("''", 0)]
    [DataRow("'   '", 3)]
    [DataRow("'{\"a\":1'", 6)]
    public void JsonValue_EndOfText_NamesPeriodAtLength(string document, int position)
        => new Simulation().AssertSqlError(
            $"select json_value({document}, '$.b')",
            13609,
            $"{Prefix}'.' is found at position {position}.");

    /// <summary>An unexpected character mid-document is named where it sits.</summary>
    [TestMethod]
    [DataRow("'{x}'", "'$.zzz'", 'x', 1)]
    [DataRow("'{\"a\":1,\"b\":}'", "'$.zzz'", '}', 11)]
    [DataRow("'{\"b\":},\"a\":1}'", "'$.zzz'", '}', 5)]
    [DataRow("'{\"a\":1 \"b\":2}'", "'$.zzz'", '"', 7)]
    [DataRow("'[1,2,]'", "'$[9]'", ']', 5)]
    public void JsonValue_UnexpectedCharacter_NamesItsPosition(string document, string path, char character, int position)
        => new Simulation().AssertSqlError(
            $"select json_value({document}, {path})",
            13609,
            $"{Prefix}'{character}' is found at position {position}.");

    /// <summary>
    /// A malformed scalar is reported at the token's first character, not at
    /// the character that spoiled it — SQL Server reads the whole token before
    /// judging it. An unterminated string is its opening quote; a raw control
    /// character inside one likewise.
    /// </summary>
    [TestMethod]
    [DataRow("'{\"a\":1x}'", '1')]
    [DataRow("'{\"a\":01}'", '0')]
    [DataRow("'{\"a\":+1}'", '+')]
    [DataRow("'{\"a\":1e'", '1')]
    [DataRow("'{\"a\":tru'", 't')]
    [DataRow("'{\"a\":\"str'", '"')]
    [DataRow("'{\"a\":\"' + char(9) + '\"}'", '"')]
    public void JsonValue_MalformedScalar_NamesTokenStart(string document, char character)
        => new Simulation().AssertSqlError(
            $"select json_value({document}, '$.zzz')",
            13609,
            $"{Prefix}'{character}' is found at position 5.");

    /// <summary>An unterminated property name is reported at its opening quote.</summary>
    [TestMethod]
    public void JsonValue_UnterminatedKey_NamesOpeningQuote()
        => new Simulation().AssertSqlError(
            "select json_value('{\"a', '$.a')",
            13609,
            $"{Prefix}'\"' is found at position 1.");

    /// <summary>Text past a complete root value is a problem only for a path that keeps reading.</summary>
    [TestMethod]
    [DataRow("'{} x'", "'$.zzz'", 'x', 3)]
    [DataRow("'{\"a\":1}extra'", "'$.zzz'", 'e', 7)]
    [DataRow("'[1,2]extra'", "'$[9]'", 'e', 5)]
    public void JsonValue_TrailingText_RaisesOnlyWhenThePathMisses(string document, string path, char character, int position)
        => new Simulation().AssertSqlError(
            $"select json_value({document}, {path})",
            13609,
            $"{Prefix}'{character}' is found at position {position}.");

    /// <summary>
    /// The reader stops at the value the path names, so anything wrong after
    /// it — trailing text, a truncation, a missing comma — is never reached.
    /// </summary>
    [TestMethod]
    [DataRow("json_value('{\"a\":1}extra', '$.a')", "1")]
    [DataRow("json_value('{\"a\":1} x', '$.a')", "1")]
    [DataRow("json_value('{\"a\":1', '$.a')", "1")]
    [DataRow("json_value('{\"a\":1,', '$.a')", "1")]
    [DataRow("json_value('{\"a\":1,}', '$.a')", "1")]
    [DataRow("json_value('{\"a\":1,\"b\":}', '$.a')", "1")]
    [DataRow("json_value('{\"a\":1 \"b\":2}', '$.a')", "1")]
    [DataRow("json_value('{\"a\":12', '$.a')", "12")]
    [DataRow("json_value('{\"a\":1,\"b\":{\"c\":2', '$.a')", "1")]
    [DataRow("json_value('{\"a\":{\"x\":1', '$.a.x')", "1")]
    [DataRow("json_value('[1,2', '$[0]')", "1")]
    [DataRow("json_value('[1,2]]', '$[0]')", "1")]
    [DataRow("json_value('[{\"a\":1}]extra', '$[0].a')", "1")]
    [DataRow("json_query('{\"a\":{\"x\":1} bad', '$.a')", "{\"x\":1}")]
    [DataRow("json_query('{} x')", "{}")]
    [DataRow("json_query('{\"a\":1}extra')", "{\"a\":1}")]
    public void PathSatisfiedBeforeTheProblem_Answers(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    /// <summary>
    /// A value the input never closed doesn't count as read, so the path it
    /// sits on hasn't resolved and the truncation surfaces.
    /// </summary>
    [TestMethod]
    [DataRow("json_query('{\"a\":{\"x\":1', '$.a')", 11)]
    [DataRow("json_query('{\"a\":{\"x\":1} bad')", 13)]
    public void PathLandsOnAnUnclosedValue_RaisesMsg13609(string expression, int position)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}", 13609);
        Contains($"is found at position {position}.", ex.Message);
    }

    /// <summary>
    /// Asking an object for an element (or an array for a property) is settled
    /// by the container's opening bracket and first member, so the reader stops
    /// there and never meets what's wrong with the rest of the text.
    /// </summary>
    [TestMethod]
    [DataRow("json_value('{\"a\":1', '$[0]')")]
    [DataRow("json_value('{\"a\":1}extra', '$[0]')")]
    [DataRow("json_value('{\"a\":{\"x\":1', '$.a[0]')")]
    [DataRow("json_value('[1,2', '$.a')")]
    [DataRow("json_value('[1,2]extra', '$.a')")]
    [DataRow("json_value('{\"a\":1,\"b\":}', '$.a.x')")]
    [DataRow("json_value('{\"a\":[1],\"b\":}', '$.a[5]')")]
    [DataRow("json_query('{\"a\":{\"z\":1},\"b\":}', '$.a.q')")]
    public void PathSettledWithoutReadingOn_ReturnsNull(string expression)
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($"select {expression}"));

    /// <summary>
    /// A container with no member to start on settles nothing early, so the
    /// reader keeps going and meets the document's problem after all.
    /// </summary>
    [TestMethod]
    [DataRow("json_value('{x}', '$[0]')", 'x', 1)]
    [DataRow("json_value('{} x', '$[0]')", 'x', 3)]
    [DataRow("json_value('[,1]', '$.a')", ',', 1)]
    public void EmptyContainerOfTheWrongKind_RaisesMsg13609(string expression, char character, int position)
        => new Simulation().AssertSqlError(
            $"select {expression}",
            13609,
            $"{Prefix}'{character}' is found at position {position}.");

    /// <summary>
    /// Stepping past a value the truncation itself terminated costs the reader
    /// one more token, which is where the document ran out.
    /// </summary>
    [TestMethod]
    [DataRow("json_value('{\"a\":1', '$.a.x')", '.', 6)]
    [DataRow("json_value('{\"a\":1 ', '$.a.x')", '.', 7)]
    [DataRow("json_value('{\"a\":\"s\"', '$.a.x')", '.', 8)]
    [DataRow("json_value('{\"a\":1,', '$.a.x')", '.', 7)]
    [DataRow("json_value('{\"a\":1,}', '$.a.x')", '}', 7)]
    [DataRow("json_value('{\"a\":1}extra', '$.a.x')", 'e', 7)]
    [DataRow("json_query('{\"a\":{\"z\":1}}extra', '$.a.q')", 'e', 13)]
    public void StepPastTheLastValueRead_RaisesMsg13609(string expression, char character, int position)
        => new Simulation().AssertSqlError(
            $"select {expression}",
            13609,
            $"{Prefix}'{character}' is found at position {position}.");

    /// <summary>The <c>strict</c> prefix changes nothing: Msg 13609 beats Msg 13608.</summary>
    [TestMethod]
    public void StrictPath_MalformedDocument_StillRaisesMsg13609()
        => new Simulation().AssertSqlError(
            "select json_value('{x}', 'strict $.a')",
            13609,
            $"{Prefix}'x' is found at position 1.");

    /// <summary>A well-formed document with a missing <c>strict</c> path is still Msg 13608.</summary>
    [TestMethod]
    public void StrictPath_WellFormedDocument_RaisesMsg13608()
        => new Simulation().AssertSqlError(
            "select json_value('{\"a\":1}', 'strict $.b')",
            13608,
            "Property cannot be found on the specified JSON path.");

    /// <summary>
    /// A path settled early keeps its own error even under <c>strict</c>: the
    /// reader never got as far as the document's problem.
    /// </summary>
    [TestMethod]
    public void StrictPath_SettledEarly_RaisesMsg13608()
        => new Simulation().AssertSqlError(
            "select json_value('{\"a\":1}extra', 'strict $[0]')",
            13608,
            "Property cannot be found on the specified JSON path.");

    /// <summary>
    /// A <c>strict</c> path onto an object or array is JSON_VALUE's own
    /// Msg 13623 (State 2) — it has no scalar to hand back.
    /// </summary>
    [TestMethod]
    public void JsonValue_StrictPath_NonScalarMatch_RaisesMsg13623()
    {
        var ex = new Simulation().AssertSqlError("select json_value('{\"a\":[1]}', 'strict $.a')", 13623);
        AreEqual(2, ex.State);
        AreEqual("Scalar value cannot be found in the specified JSON path.", ex.Message);
    }

    /// <summary>JSON_QUERY's complement, Msg 13624, reports State 2 as well.</summary>
    [TestMethod]
    public void JsonQuery_StrictPath_ScalarMatch_RaisesMsg13624State2()
    {
        var ex = new Simulation().AssertSqlError("select json_query('{\"a\":1}', 'strict $.a')", 13624);
        AreEqual(2, ex.State);
        AreEqual("Object or array cannot be found in the specified JSON path.", ex.Message);
    }

    /// <summary>A NULL document is NULL, never an error.</summary>
    [TestMethod]
    [DataRow("json_value(cast(null as nvarchar(max)), '$.a')")]
    [DataRow("json_query(cast(null as nvarchar(max)), '$.a')")]
    [DataRow("json_query(cast(null as nvarchar(max)))")]
    [DataRow("json_modify(cast(null as nvarchar(max)), '$.a', 2)")]
    [DataRow("json_path_exists(cast(null as nvarchar(max)), '$.a')")]
    public void NullDocument_ReturnsNullWithoutRaising(string expression)
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($"select {expression}"));

    /// <summary>JSON_QUERY reports State 1, the same as JSON_VALUE.</summary>
    [TestMethod]
    public void JsonQuery_RootScalar_RaisesMsg13609State1()
    {
        var ex = new Simulation().AssertSqlError("select json_query('\"abc\"')", 13609);
        AreEqual(1, ex.State);
        AreEqual($"{Prefix}'\"' is found at position 0.", ex.Message);
    }

    /// <summary>
    /// JSON_MODIFY reproduces the whole document, so it has no path that lets
    /// it stop early — trailing text counts against it — and it reports its own
    /// State 7.
    /// </summary>
    [TestMethod]
    [DataRow("'1'", '1', 0)]
    [DataRow("'{} x'", 'x', 3)]
    [DataRow("'{\"a\":1}extra'", 'e', 7)]
    [DataRow("'{\"a\":1'", '.', 6)]
    public void JsonModify_MalformedDocument_RaisesMsg13609State7(string document, char character, int position)
    {
        var ex = new Simulation().AssertSqlError($"select json_modify({document}, '$.a', 2)", 13609);
        AreEqual(7, ex.State);
        AreEqual($"{Prefix}'{character}' is found at position {position}.", ex.Message);
    }

    /// <summary>Whitespace around the document is not trailing text.</summary>
    [TestMethod]
    public void JsonModify_TrailingWhitespace_Modifies()
        => AreEqual("{\"a\":2}", new Simulation().ExecuteScalar("select json_modify('{\"a\":1} ', '$.a', 2)"));

    /// <summary>
    /// A path that can't apply to what JSON_MODIFY finds is a no-op the reader
    /// settles early, so the input comes straight back however malformed the
    /// rest of it is.
    /// </summary>
    [TestMethod]
    [DataRow("'[1,2'")]
    [DataRow("'[1,2]extra'")]
    [DataRow("'[1 2]'")]
    [DataRow("'[{\"a\":1}]extra'")]
    public void JsonModify_PathCannotApply_ReturnsTheInputVerbatim(string document)
        => AreEqual(document.Trim('\''), new Simulation().ExecuteScalar($"select json_modify({document}, '$.a', 2)"));

    /// <summary>
    /// JSON_PATH_EXISTS is the one member of the family that never raises: it
    /// answers 0 for a document the others reject, and 0 for a
    /// <c>strict</c>-mode miss that would be Msg 13608 elsewhere.
    /// </summary>
    [TestMethod]
    [DataRow("json_path_exists('1', '$.a')")]
    [DataRow("json_path_exists('{x}', '$.a')")]
    [DataRow("json_path_exists('{x}', 'strict $.a')")]
    [DataRow("json_path_exists('{\"a\":1', '$.b')")]
    [DataRow("json_path_exists('{\"a\":1}extra', '$.a')")]
    [DataRow("json_path_exists('{\"a\":1}x', '$.a')")]
    [DataRow("json_path_exists('{\"a\":1}', 'strict $.b')")]
    public void JsonPathExists_NeverRaises(string expression)
        => IsFalse((bool)new Simulation().ExecuteScalar($"select {expression}")!);

    /// <summary>Trailing whitespace leaves JSON_PATH_EXISTS's answer alone.</summary>
    [TestMethod]
    public void JsonPathExists_TrailingWhitespace_Returns1()
        => IsTrue((bool)new Simulation().ExecuteScalar("select json_path_exists('{\"a\":1} ', '$.a')")!);

    /// <summary>ISJSON applies the same two rules, reporting them as 0 rather than raising.</summary>
    [TestMethod]
    [DataRow("isjson('1')", 0)]
    [DataRow("isjson('\"abc\"')", 0)]
    [DataRow("isjson('true')", 0)]
    [DataRow("isjson('{\"a\":1}extra')", 0)]
    [DataRow("isjson('{\"a\":1')", 0)]
    [DataRow("isjson('{\"a\":1} ')", 1)]
    [DataRow("isjson('  [1,2]  ')", 1)]
    public void IsJson_RootShapeAndTrailingText(string expression, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    /// <summary>
    /// OPENJSON reports State 4 when the reader was inside the value it was
    /// after — always so for the one-argument form, whose value is the whole
    /// document — and State 3 when it was still looking for it.
    /// </summary>
    [TestMethod]
    [DataRow("openjson('{x}')", 4, 'x', 1)]
    [DataRow("openjson('1')", 4, '1', 0)]
    [DataRow("openjson('{x}', '$.a')", 3, 'x', 1)]
    [DataRow("openjson('{}extra', '$.a')", 3, 'e', 2)]
    [DataRow("openjson('{\"a\":{\"x\":1', '$.a')", 4, '.', 11)]
    [DataRow("openjson('{\"a\":[1,2', '$.a')", 4, '.', 9)]
    [DataRow("openjson('{\"a\"1}', '$.a')", 4, '1', 4)]
    public void OpenJson_MalformedDocument_RaisesMsg13609(string source, int state, char character, int position)
    {
        var ex = new Simulation().AssertSqlError($"select count(*) from {source}", 13609);
        AreEqual((byte)state, ex.State);
        AreEqual($"{Prefix}'{character}' is found at position {position}.", ex.Message);
    }

    /// <summary>OPENJSON stops at the root value's closing bracket, so trailing text is invisible to it.</summary>
    [TestMethod]
    [DataRow("select [key] from openjson('{\"a\":1}extra')", "a")]
    [DataRow("select [value] from openjson('{\"a\":[1,2]}extra', '$.a')", "1")]
    [DataRow("select a from openjson('{\"a\":1}extra') with (a int)", 1)]
    public void OpenJson_TrailingTextAfterACompleteRoot_Unfolds(string commandText, object expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(commandText));

    /// <summary>
    /// A truncated document unfolds the members the reader got through and
    /// then raises where it stopped. Real streams those rows out ahead of the
    /// error token; the simulator surfaces a failed statement as the error
    /// alone (see <c>docs/claude/data-reader.md</c>), so what's observable
    /// here is the position the truncation is reported at.
    /// </summary>
    [TestMethod]
    public void OpenJson_TruncatedDocument_RaisesWhereItStopped()
        => new Simulation().AssertSqlError(
            "select [key] from openjson('{\"a\":1,\"b\":}')",
            13609,
            $"{Prefix}'}}' is found at position 11.");

    /// <summary>
    /// Position counts UTF-16 characters, not bytes: a non-ASCII character
    /// ahead of the problem still advances it by one.
    /// </summary>
    [TestMethod]
    public void Position_CountsCharacters_NotBytes()
        => new Simulation().AssertSqlError(
            "select json_value(N'{\"é\":1} x', '$.b')",
            13609,
            $"{Prefix}'x' is found at position 8.");
}
