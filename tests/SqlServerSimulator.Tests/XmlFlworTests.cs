using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The XQuery-subset evaluator's binding constructs: FLWOR
/// (<c>for</c> / <c>let</c> / <c>where</c> / <c>order by</c> / <c>return</c>),
/// the quantified <c>some</c> / <c>every</c> expressions, the conditional
/// <c>if … then … else …</c>, <c>$</c>-variable references, direct element
/// constructors, and the diagnostics SQL Server settles statically over all of
/// them. Every expected value and message here was probed against SQL Server
/// 2025.
/// </summary>
[TestClass]
public sealed class XmlFlworTests
{
    private const string TwoValues = "<r><a>1</a><a>2</a></r>";
    private const string KeyedValues = "<r><a x=\"2\">p</a><a x=\"10\">q</a></r>";

    private static string? Query(string document, string xquery) =>
        (string?)new Simulation().ExecuteScalar(
            $"declare @x xml = '{document}'; select convert(nvarchar(400), @x.query('{xquery}'))");

    private static object? Value(string document, string xquery, string type) =>
        new Simulation().ExecuteScalar($"declare @x xml = '{document}'; select @x.value('{xquery}', '{type}')");

    private static bool Exists(string document, string xquery) =>
        (bool)new Simulation().ExecuteScalar($"declare @x xml = '{document}'; select @x.exist('{xquery}')")!;

    private static void QueryError(string xquery, int errorNumber, string expectedMessage) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoValues}'; select @x.query('{xquery}')", errorNumber, expectedMessage);

    // ---- FLWOR ------------------------------------------------------------

    [TestMethod]
    public void For_BindsEachItemInTurn() =>
        AreEqual("<a>1</a><a>2</a>", Query(TwoValues, "for $i in /r/a return $i"));

    [TestMethod]
    public void Let_BindsTheWholeSequenceAtOnce() =>
        AreEqual("<a>1</a><a>2</a>", Query(TwoValues, "let $i := /r/a return $i"));

    [TestMethod]
    public void Where_FiltersTuples() =>
        AreEqual("<a>2</a>", Query(TwoValues, "for $i in /r/a where $i > 1 return $i"));

    [TestMethod]
    public void LetAndFor_ComposeInEitherOrder()
    {
        AreEqual("1x 2x", Query(TwoValues, "for $i in /r/a let $j := concat($i,\"x\") return $j"));
        AreEqual("1x 2x", Query(TwoValues, "let $j := \"x\" for $i in /r/a return concat($i,$j)"));
    }

    [TestMethod]
    public void TwoBindings_NestLikeLoops() =>
        AreEqual("11 12 21 22", Query(TwoValues, "for $i in /r/a, $j in /r/a return concat($i,$j)"));

    [TestMethod]
    public void TwoForClauses_NestLikeLoops() =>
        AreEqual("11 12 21 22", Query(TwoValues, "for $i in /r/a for $j in /r/a return concat($i,$j)"));

    [TestMethod]
    public void NestedFlwor_NestsLikeLoops() =>
        AreEqual("11 12 21 22", Query(TwoValues, "for $i in /r/a return for $j in /r/a return concat($i,$j)"));

    [TestMethod]
    public void ABindingCanReadAnEarlierOne() =>
        AreEqual(
            "<b>1</b><b>2</b>",
            Query("<r><a><b>1</b></a><a><b>2</b></a></r>", "for $i in /r/a, $j in $i/b return $j"));

    [TestMethod]
    public void AnInnerBindingShadowsAnOuterOne() =>
        AreEqual("z z", Query(TwoValues, "for $i in /r/a return let $i := \"z\" return $i"));

    [TestMethod]
    public void For_OverAnAtomicSequence_Iterates() =>
        AreEqual("1 2", Query(TwoValues, "for $i in (1,2) return $i"));

    /// <summary>
    /// A FLWOR result keeps iteration order and every duplicate — it is not
    /// folded into document order the way a path step's output is.
    /// </summary>
    [TestMethod]
    public void FlworResult_KeepsDuplicatesAndIterationOrder() =>
        AreEqual("<b>9</b><b>9</b>", Query("<r><a>1</a><a>2</a><b>9</b></r>", "for $i in /r/a return /r/b"));

    [TestMethod]
    public void APositionalPredicateNarrowsAFlwor() =>
        AreEqual("<a>1</a>", Query(TwoValues, "(for $i in /r/a return $i)[1]"));

    [TestMethod]
    public void AVariableTakesPathStepsLikeAnyOtherExpression() =>
        AreEqual(
            "<b>1</b><b>2</b>",
            Query("<r><a><b>1</b></a><a><b>2</b></a></r>", "for $i in /r/a return $i/b"));

    // ---- order by ---------------------------------------------------------

    /// <summary>
    /// An untyped key compares by code point, so <c>"10"</c> sorts ahead of
    /// <c>"2"</c>; only a key real types as a number compares numerically.
    /// </summary>
    [TestMethod]
    public void OrderBy_UntypedKey_ComparesByCodePoint() =>
        AreEqual("<a x=\"10\">q</a><a x=\"2\">p</a>", Query(KeyedValues, "for $i in /r/a order by $i/@x return $i"));

    [TestMethod]
    public void OrderBy_NumericKey_ComparesNumerically() =>
        AreEqual("<a x=\"2\">p</a><a x=\"10\">q</a>", Query(KeyedValues, "for $i in /r/a order by number($i/@x) return $i"));

    [TestMethod]
    public void OrderBy_Descending_ReversesTheComparison() =>
        AreEqual("<a x=\"2\">p</a><a x=\"10\">q</a>", Query(KeyedValues, "for $i in /r/a order by $i/@x descending return $i"));

    [TestMethod]
    public void OrderBy_Ascending_IsTheDefault() =>
        AreEqual("<a x=\"10\">q</a><a x=\"2\">p</a>", Query(KeyedValues, "for $i in /r/a order by $i/@x ascending return $i"));

    /// <summary>Real's default is <c>empty least</c>, and <c>descending</c> reverses it.</summary>
    [TestMethod]
    public void OrderBy_AnEmptyKeySortsFirstAndLastRespectively()
    {
        AreEqual(
            "<a>2</a><a x=\"2\">1</a>",
            Query("<r><a x=\"2\">1</a><a>2</a></r>", "for $i in /r/a order by $i/@x return $i"));
        AreEqual(
            "<a x=\"2\">p</a><a x=\"10\">s</a><a>q</a>",
            Query("<r><a x=\"2\">p</a><a>q</a><a x=\"10\">s</a></r>", "for $i in /r/a order by $i/@x descending return $i"));
    }

    [TestMethod]
    public void OrderBy_IsStableAcrossTies() =>
        AreEqual(
            "<a k=\"0\">w</a><a k=\"1\">z</a><a k=\"1\">y</a>",
            Query("<r><a k=\"1\">z</a><a k=\"1\">y</a><a k=\"0\">w</a></r>", "for $i in /r/a order by $i/@k return $i"));

    [TestMethod]
    public void StableOrderBy_IsAccepted() =>
        AreEqual("<a x=\"10\">q</a><a x=\"2\">p</a>", Query(KeyedValues, "for $i in /r/a stable order by $i/@x return $i"));

    [TestMethod]
    public void OrderBy_TakesSeveralItems() =>
        AreEqual(
            "<a x=\"1\" y=\"2\">2</a><a x=\"2\" y=\"1\">1</a>",
            Query("<r><a x=\"2\" y=\"1\">1</a><a x=\"1\" y=\"2\">2</a></r>", "for $i in /r/a order by $i/@x, $i/@y return $i"));

    [TestMethod]
    public void WhereAndOrderBy_ComposeWithTheReturnClause() =>
        AreEqual("<v>2</v>", Query(TwoValues, "for $i in /r/a where $i > 1 order by $i return <v>{string($i)}</v>"));

    // ---- quantified expressions -------------------------------------------

    [TestMethod]
    public void Some_IsExistential() =>
        AreEqual("yes", Query(TwoValues, "if (some $i in /r/a satisfies $i = 2) then \"yes\" else \"no\""));

    [TestMethod]
    public void Every_IsUniversal()
    {
        AreEqual("yes", Query(TwoValues, "if (every $i in /r/a satisfies $i > 0) then \"yes\" else \"no\""));
        AreEqual("no", Query(TwoValues, "if (every $i in /r/a satisfies $i > 1) then \"yes\" else \"no\""));
    }

    /// <summary>Over an empty binding sequence <c>some</c> is false and <c>every</c> true.</summary>
    [TestMethod]
    public void QuantifiersOverAnEmptySequence()
    {
        AreEqual("n", Query("<r/>", "if (some $i in /r/nope satisfies $i = 1) then \"y\" else \"n\""));
        AreEqual("y", Query("<r/>", "if (every $i in /r/nope satisfies $i = 1) then \"y\" else \"n\""));
    }

    [TestMethod]
    public void Quantified_TakesSeveralBindings() =>
        AreEqual(
            "y",
            Query("<r><a>1</a><b>2</b></r>", "if (some $i in /r/a, $j in /r/b satisfies $i < $j) then \"y\" else \"n\""));

    [TestMethod]
    public void Quantified_IsLegalInsideAPredicate() =>
        AreEqual(
            "<a x=\"2\">2</a>",
            Query("<r><a x=\"1\">1</a><a x=\"2\">2</a></r>", "/r/a[some $j in @x satisfies $j = 2]"));

    [TestMethod]
    public void Satisfies_ReadsTheEffectiveBooleanValue() =>
        AreEqual("true", Query(TwoValues, "some $i in /r/a satisfies $i"));

    // ---- conditional ------------------------------------------------------

    [TestMethod]
    public void If_PicksABranch()
    {
        AreEqual("yes", Query(TwoValues, "if (/r/a[1] = 1) then \"yes\" else \"no\""));
        AreEqual("no", Query(TwoValues, "if (/r/a[1] = 9) then \"yes\" else \"no\""));
    }

    [TestMethod]
    public void If_OverAnEmptySequence_TakesTheElseBranch() =>
        AreEqual("n", Query(TwoValues, "if (/r/nope) then \"y\" else \"n\""));

    [TestMethod]
    public void If_IsLegalInAReturnClauseAndInAPredicate()
    {
        AreEqual("one other", Query(TwoValues, "for $i in /r/a return if ($i = 1) then \"one\" else \"other\""));
        AreEqual("<a>1</a>", Query(TwoValues, "/r/a[if (. = 1) then true() else false()]"));
    }

    // ---- sequences and constructors ---------------------------------------

    [TestMethod]
    public void ACommaSequenceIsLegalAtTheTopLevel() =>
        AreEqual("<a>1</a><b>2</b>", Query("<r><a>1</a><b>2</b></r>", "/r/a, /r/b"));

    [TestMethod]
    public void ADirectElementConstructorSplicesItsEnclosedExpressions()
    {
        AreEqual("<out><a>1</a><a>2</a></out>", Query(TwoValues, "<out>{for $i in /r/a return $i}</out>"));
        AreEqual("<out a=\"1\">t</out>", Query("<r><a>1</a></r>", "<out a=\"{/r/a}\">t</out>"));
        AreEqual("<v>1</v><v>2</v>", Query(TwoValues, "for $i in /r/a return <v>{string($i)}</v>"));
    }

    [TestMethod]
    public void ADirectElementConstructorNestsAndEscapesBraces()
    {
        AreEqual("<o>{lit}</o>", Query(TwoValues, "<o>{{lit}}</o>"));
        AreEqual("<o><p x=\"1\">2</p></o>", Query(TwoValues, "<o><p x=\"{/r/a[1]}\">{string((/r/a)[2])}</p></o>"));
        AreEqual("<o><a>1</a><a>2</a><a>1</a><a>2</a></o>", Query(TwoValues, "<o>{/r/a}{/r/a}</o>"));
    }

    [TestMethod]
    public void AConstructorIsLegalInExist() =>
        IsTrue(Exists("<r><a>1</a></r>", "<v>x</v>"));

    [TestMethod]
    public void AFlworIsLegalAsABindingSourceAndCanReturnNothing()
    {
        AreEqual("<a>1</a><a>2</a>", Query(TwoValues, "for $i in (for $j in /r/a return $j) return $i"));
        AreEqual(string.Empty, Query(TwoValues, "for $i in /r/a return ()"));
    }

    [TestMethod]
    public void QuantifiersNest() =>
        AreEqual("true", Query(TwoValues, "some $i in /r/a satisfies some $j in /r/a satisfies $i < $j"));

    // ---- reaching the other three methods ---------------------------------

    [TestMethod]
    public void AFlworReachesValueWhenItsShapeIsSingular()
    {
        AreEqual("1", Value("<r><a>1</a></r>", "for $i in (/r/a)[1] return $i", "nvarchar(10)"));
        AreEqual("1", Value("<r><a>1</a></r>", "let $i := (/r/a)[1] return $i", "nvarchar(10)"));
        AreEqual("1", Value("<r><a>1</a></r>", "if (1=1) then (/r/a)[1] else (/r/a)[1]", "nvarchar(10)"));
        IsTrue((bool)Value("<r><a>1</a></r>", "some $i in /r/a satisfies $i = 1", "bit")!);
    }

    [TestMethod]
    public void AFlworReachesExist() =>
        IsTrue(Exists(TwoValues, "for $i in /r/a where $i > 1 return $i"));

    [TestMethod]
    public void AFlworReachesNodes() =>
        AreEqual("2", new Simulation().ExecuteScalar($"""
            create table dbo.doc (body xml);
            insert dbo.doc values (N'{TwoValues}');
            select n.ref.value('(.)[1]', 'nvarchar(10)')
            from dbo.doc cross apply doc.body.nodes('for $i in /r/a where $i > 1 return $i') as n(ref)
            """));

    /// <summary>A mutator's target path compiles through the same evaluator.</summary>
    [TestMethod]
    public void AFlworReachesAModifyTarget() =>
        AreEqual(
            "<r><a>1<b/></a></r>",
            new Simulation().ExecuteScalar(
                "declare @x xml = '<r><a>1</a></r>'; set @x.modify('insert <b/> into (for $i in /r/a return $i)[1]'); select @x"));

    // ---- static cardinality -----------------------------------------------

    /// <summary>
    /// A <c>for</c> multiplies the binding sequence's cardinality into the
    /// return clause's; a <c>let</c> binds once, so it carries only the return
    /// clause's; and a <c>where</c> narrows neither.
    /// </summary>
    [TestMethod]
    public void ForMultipliesCardinalityWhereLetDoesNot()
    {
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; select @x.value('for $i in /r/a return $i', 'nvarchar(10)')",
            2389,
            "XQuery [value()]: 'value()' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'");
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; select @x.value('for $i in /r/a return \"x\"', 'nvarchar(10)')",
            2389,
            "XQuery [value()]: 'value()' requires a singleton (or empty sequence), found operand of type 'xs:string *'");
        AreEqual("x", Value("<r><a>1</a></r>", "let $i := /r/a return \"x\"", "nvarchar(10)"));
        AreEqual("x", Value("<r><a>1</a></r>", "for $i in . where $i return \"x\"", "nvarchar(10)"));
    }

    /// <summary>A <c>let</c> variable carries its binding sequence's own static type.</summary>
    [TestMethod]
    public void ALetVariableQuotesItsBindingsNodeType()
    {
        QueryError(
            "let $i := /r/a return string($i)",
            2389,
            "XQuery [query()]: 'string()' requires a singleton (or empty sequence), found operand of type 'element(a,xdt:untyped) *'");
        AreEqual("true false", Query(TwoValues, "for $i in /r/a return $i eq 1"));
    }

    [TestMethod]
    public void APluralOrderByKey_Raises2389() =>
        QueryError(
            "for $i in /r/a order by /r/a return $i",
            2389,
            "XQuery [query()]: 'order by' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'");

    // ---- syntax diagnostics -----------------------------------------------

    [TestMethod]
    public void If_WithNoElse_Raises9303() =>
        QueryError("if (1=1) then \"y\"", 9303, "XQuery [query()]: Syntax error near '<eof>', expected 'else'.");

    [TestMethod]
    public void If_WithNoThen_Raises9303() =>
        QueryError("if (1=1) \"y\" else \"n\"", 9303, "XQuery [query()]: Syntax error near 'y', expected 'then'.");

    [TestMethod]
    public void Quantified_WithNoSatisfies_Raises9303() =>
        QueryError("some $i in /r/a $i", 9303, "XQuery [query()]: Syntax error near '$i', expected 'satisfies'.");

    /// <summary>
    /// Real splits the missing-binding-separator diagnostic: a quantified
    /// expression reports Msg 9303 and a FLWOR Msg 2205.
    /// </summary>
    [TestMethod]
    public void MissingBindingSeparators()
    {
        QueryError("some $i /r/a satisfies $i = 1", 9303, "XQuery [query()]: Syntax error near '/', expected 'in'.");
        QueryError("for $i /r/a return $i", 2205, "XQuery [query()]: \"in\" was expected.");
        QueryError("let $i = /r/a return $i", 2205, "XQuery [query()]: \":=\" was expected.");
    }

    [TestMethod]
    public void AStrayTokenAfterTheBindings_Raises9332() =>
        QueryError(
            "for $i in /r/a $i",
            9332,
            "XQuery [query()]: Syntax error near '$i', expected 'where', '(stable) order by' or 'return'.");

    [TestMethod]
    public void ClausesOutOfOrder_Raise9303()
    {
        QueryError("for $i in /r/a order by $i where $i > 1 return $i", 9303, "XQuery [query()]: Syntax error near 'where', expected 'return'.");
        QueryError("for $i in /r/a where $i > 0 where $i > 1 return $i", 9303, "XQuery [query()]: Syntax error near 'where', expected 'return'.");
    }

    [TestMethod]
    public void ACommaInsideAPredicate_Raises9303() =>
        QueryError("/r/a[., .]", 9303, "XQuery [query()]: Syntax error near ',', expected ']'.");

    /// <summary>The binding and order-by modifiers real parses but refuses.</summary>
    [TestMethod]
    public void UnsupportedModifiers_Raise9335()
    {
        QueryError("for $i at $p in /r/a return $i", 9335, "XQuery [query()]: The XQuery syntax 'at' is not supported.");
        QueryError("for $i as xs:string in /r/a return $i", 9335, "XQuery [query()]: The XQuery syntax 'as' is not supported.");
        QueryError(
            "for $i in /r/a order by $i empty greatest return $i",
            9335,
            "XQuery [query()]: The XQuery syntax 'empty greatest' is not supported.");
        QueryError(
            "for $i in /r/a order by $i empty least return $i",
            9335,
            "XQuery [query()]: The XQuery syntax 'empty least' is not supported.");
        QueryError(
            "for $i in /r/a order by $i collation \"urn:c\" return $i",
            9335,
            "XQuery [query()]: The XQuery syntax 'collation' is not supported.");
    }

    // ---- static type diagnostics ------------------------------------------

    [TestMethod]
    public void AnUnboundVariable_Raises2227()
    {
        QueryError("$nope", 2227, "XQuery [query()]: The variable '$nope' was not found in the scope in which it was referenced.");
        QueryError("for $i in /r/a return $j", 2227, "XQuery [query()]: The variable '$j' was not found in the scope in which it was referenced.");
    }

    /// <summary>
    /// A condition admits only boolean or node expressions — unlike a
    /// predicate, where a numeric one is a position.
    /// </summary>
    [TestMethod]
    public void ANonBooleanCondition_Raises2204()
    {
        const string StringOperand =
            "XQuery [query()]: Only 'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*' "
            + "expressions allowed in conditions and with logical operators, found 'xs:string'";
        QueryError("if (\"\") then \"y\" else \"n\"", 2204, StringOperand);
        QueryError("some $i in /r/a satisfies \"x\"", 2204, StringOperand);
        QueryError("for $i in /r/a where \"x\" return $i", 2204, StringOperand);
        QueryError("not(\"x\")", 2204, StringOperand);
        QueryError("\"a\" and 1=1", 2204, StringOperand);
        QueryError(
            "if (1) then \"y\" else \"n\"",
            2204,
            "XQuery [query()]: Only 'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*' "
            + "expressions allowed in conditions and with logical operators, found 'xs:integer'");
        QueryError(
            "if (data(/r/a)) then \"y\" else \"n\"",
            2204,
            "XQuery [query()]: Only 'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*' "
            + "expressions allowed in conditions and with logical operators, found 'xdt:untypedAtomic *'");
    }

    /// <summary>Both read the sequence a predicate is filtering, so real refuses them elsewhere.</summary>
    [TestMethod]
    public void PositionOrLastOutsideAPredicate_Raises2371()
    {
        QueryError("position()", 2371, "XQuery [query()]: 'position()' can only be used within a predicate or XPath selector");
        QueryError("last()", 2371, "XQuery [query()]: 'last()' can only be used within a predicate or XPath selector");
        QueryError("for $i in /r/a return position()", 2371, "XQuery [query()]: 'position()' can only be used within a predicate or XPath selector");
    }

    /// <summary>The message names the atomic type first whichever side wrote it.</summary>
    [TestMethod]
    public void MixingNodesWithAtomicValues_Raises2210()
    {
        const string Mixed = "XQuery [query()]: Heterogeneous sequences are not allowed: found 'xs:string' and 'element(a,xdt:untyped) *'";
        QueryError("(\"s\", /r/a)", 2210, Mixed);
        QueryError("(/r/a, \"s\")", 2210, Mixed);
        QueryError("if (1=1) then /r/a else \"s\"", 2210, Mixed);
        QueryError("if (1=1) then \"s\" else /r/a", 2210, Mixed);
        AreEqual("1 a", Query(TwoValues, "(1, \"a\")"));
    }

    /// <summary>
    /// A constructor is legal in <c>query()</c> and <c>exist()</c>, which hand
    /// the node on, and refused by the two that would have to look inside it.
    /// </summary>
    [TestMethod]
    public void AConstructorInValueOrNodes_Raises2373()
    {
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; select @x.value('<v>{/r/a}</v>', 'nvarchar(20)')",
            2373,
            "XQuery [value()]: data() is not supported with constructed XML");
        new Simulation().AssertSqlError(
            """
            create table dbo.doc (body xml);
            select n.ref.value('(.)[1]', 'nvarchar(10)') from dbo.doc cross apply doc.body.nodes('<v>{/r/a}</v>') as n(ref)
            """,
            2373,
            "XQuery [nodes()]: 'nodes()' is not supported with constructed XML");
    }

    // ---- computed constructors --------------------------------------------

    [TestMethod]
    [DataRow("element n {\"v\"}", "<n>v</n>")]
    [DataRow("element n {}", "<n/>")]
    [DataRow("element n {/r/a}", "<n><a>1</a><a>2</a></n>")]
    [DataRow("<o>{element n {5}}</o>", "<o><n>5</n></o>")]
    public void ComputedElementConstructor(string xquery, string expected) =>
        AreEqual(expected, Query(TwoValues, xquery));

    [TestMethod]
    public void ComputedElementConstructor_TakesThePrologDefaultNamespace() =>
        AreEqual("<n xmlns=\"urn:d\">5</n>", Query(TwoValues, "declare default element namespace \"urn:d\"; element n {5}"));

    /// <summary>
    /// A direct constructor resolves its name through the prolog the same way,
    /// so <c>&lt;b/&gt;</c> under a default element namespace builds in it.
    /// </summary>
    [TestMethod]
    public void DirectConstructor_TakesThePrologDefaultNamespace() =>
        AreEqual("<b xmlns=\"urn:d\"/>", Query(TwoValues, "declare default element namespace \"urn:d\"; <b/>"));

    /// <summary>A declared prefix the markup never writes isn't declared on the result, as real omits it.</summary>
    [TestMethod]
    [DataRow("declare namespace p=\"urn:x\"; <b/>", "<b/>")]
    [DataRow("declare namespace p=\"urn:x\"; <p:b/>", "<p:b xmlns:p=\"urn:x\"/>")]
    public void DirectConstructor_DeclaresOnlyThePrefixesItWrites(string xquery, string expected) =>
        AreEqual(expected, Query(TwoValues, xquery));

    [TestMethod]
    public void ComputedElementConstructor_UndeclaredPrefix_Raises2229() =>
        QueryError("element n:m {1}", 2229, "XQuery [query()]: The name \"n\" does not denote a namespace.");

    /// <summary>
    /// Real takes only the constant-QName form; a <c>{…}</c> name expression is
    /// Msg 9315 even when it holds a string literal.
    /// </summary>
    [TestMethod]
    [DataRow("element {\"n\"} {\"v\"}")]
    [DataRow("attribute {\"z\"} {1}")]
    [DataRow("element {local-name((/r/a)[1])} {5}")]
    public void ComputedNameExpression_Raises9315(string xquery) =>
        QueryError(
            xquery,
            9315,
            "XQuery [query()]: Only constant expressions are supported for the name expression of computed element and attribute constructors.");

    [TestMethod]
    public void ComputedCommentConstructor_Raises9326() =>
        QueryError("<o>{comment {\"c\"}}</o>", 9326, "XQuery [query()]: Computed comment constructors are not supported.");

    [TestMethod]
    public void ComputedProcessingInstructionConstructor_Raises9325() =>
        QueryError("<o>{processing-instruction p {\"d\"}}</o>", 9325, "XQuery [query()]: Computed processing instruction constructors are not supported.");

    [TestMethod]
    public void EmptyExpression_Raises6306() =>
        QueryError(string.Empty, 6306, "Invalid XQuery expression passed to XML data type method.");

    /// <summary>
    /// The expression compiles while the statement parses, so the binding
    /// constructs' static diagnostics fire without a row ever being read.
    /// </summary>
    [TestMethod]
    public void StaticDiagnosticsFireOverAnEmptyRowset()
    {
        _ = new Simulation().AssertSqlError(
            "create table dbo.doc (body xml); select body.query('$nope') from dbo.doc", 2227);
        _ = new Simulation().AssertSqlError(
            "create table dbo.doc (body xml); select body.query('for $i in /r/a where \"s\" return $i') from dbo.doc", 2204);
    }
}
