using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The XQuery-subset evaluator's predicate and operator surface: value
/// predicates, the general and value comparison operators, boolean
/// composition, positional interplay, the built-in function library, and the
/// diagnostics SQL Server settles statically over all of it. Every expected
/// value here was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlPredicateTests
{
    private const string TwoAttributes = "<r><a x=\"1\">v1</a><a x=\"2\">v2</a></r>";
    private const string PaddedNumbers = "<r><a x=\"01\"/><a x=\"2\"/></r>";
    private const string ChildValues = "<r><p><b>1</b><b>2</b></p><p><b>1</b></p><p><b>3</b></p></r>";

    private static string? Query(string document, string xquery) =>
        (string?)new Simulation().ExecuteScalar(
            $"declare @x xml = '{document}'; select convert(nvarchar(400), @x.query('{xquery}'))");

    private static object? Value(string document, string xquery, string type) =>
        new Simulation().ExecuteScalar($"declare @x xml = '{document}'; select @x.value('{xquery}', '{type}')");

    private static bool Exists(string document, string xquery) =>
        (bool)new Simulation().ExecuteScalar($"declare @x xml = '{document}'; select @x.exist('{xquery}')")!;

    private static SimulatedSqlException QueryError(string document, string xquery, int errorNumber) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{document}'; select @x.query('{xquery}')", errorNumber);

    // ---- comparison semantics on untyped atomics -------------------------

    [TestMethod]
    public void StringLiteral_ComparesAsString()
    {
        AreEqual("<a x=\"01\"/>", Query(PaddedNumbers, "/r/a[@x=\"01\"]"));
        AreEqual(string.Empty, Query(PaddedNumbers, "/r/a[@x=\"1\"]"));
    }

    [TestMethod]
    public void NumericLiteral_CastsTheUntypedOperandToANumber() =>
        AreEqual("<a x=\"01\"/>", Query(PaddedNumbers, "/r/a[@x=1]"));

    [TestMethod]
    public void UncastableValue_AgainstNumericLiteral_MatchesNothingAndDoesNotRaise()
    {
        AreEqual(string.Empty, Query("<r><a x=\"abc\"/></r>", "/r/a[@x=2]"));
        AreEqual(string.Empty, Query("<r><a x=\"abc\"/></r>", "/r/a[@x!=1]"));
    }

    [TestMethod]
    public void Relational_AgainstStringLiteral_ComparesByCodePoint() =>
        AreEqual("<a x=\"abc\"/><a x=\"5\"/>", Query("<r><a x=\"abc\"/><a x=\"zzz\"/><a x=\"5\"/></r>", "/r/a[@x<\"b\"]"));

    [TestMethod]
    public void Relational_AgainstNumericLiteral_ComparesNumerically() =>
        AreEqual("<a x=\"5\"/>", Query("<r><a x=\"abc\"/><a x=\"zzz\"/><a x=\"5\"/></r>", "/r/a[@x<10]"));

    [TestMethod]
    public void StringComparison_IsCaseSensitive_WhateverTheDatabaseCollation() =>
        AreEqual("<a x=\"ABC\"/>", Query("<r><a x=\"abc\"/><a x=\"ABC\"/></r>", "/r/a[@x<\"B\"]"));

    [TestMethod]
    public void GeneralComparison_IsExistentialOverTheWholeSequence() =>
        AreEqual("<p><b>1</b><b>2</b></p><p><b>1</b></p>", Query(ChildValues, "/r/p[b=1]"));

    [TestMethod]
    public void NotEquals_IsNotTheComplementOfEquals()
    {
        AreEqual("<p><b>1</b><b>2</b></p><p><b>3</b></p>", Query(ChildValues, "/r/p[b!=1]"));
        AreEqual("<p><b>3</b></p>", Query(ChildValues, "/r/p[not(b=1)]"));
    }

    [TestMethod]
    public void ComparingTwoPathsAtomizesBothSides() =>
        AreEqual("<a x=\"1\" y=\"2\"/>", Query("<r><a x=\"1\" y=\"2\"/></r>", "/r/a[@x!=@y]"));

    // ---- value comparisons -----------------------------------------------

    [TestMethod]
    public void ValueComparison_OverSingletons_Compares()
    {
        AreEqual("<a x=\"1\">v1</a>", Query(TwoAttributes, "/r/a[@x eq \"1\"]"));
        AreEqual("<a x=\"2\">v2</a>", Query(TwoAttributes, "/r/a[@x gt 1]"));
    }

    [TestMethod]
    public void ValueComparison_PluralOperand_Raises2389() =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{ChildValues}'; select @x.query('/r/p[b eq 1]')",
            2389,
            "XQuery [query()]: 'eq' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'");

    [TestMethod]
    public void ValueComparison_PositionalPredicateSuppliesTheSingleton() =>
        AreEqual("<p><b>1</b><b>2</b></p><p><b>1</b></p>", Query(ChildValues, "/r/p[b[1] eq 1]"));

    [TestMethod]
    public void ValueComparison_EmptyOperand_SelectsNothing() =>
        AreEqual(string.Empty, Query(TwoAttributes, "/r/a[@nope eq 1]"));

    [TestMethod]
    public void MismatchedTypedOperands_Raises2234() =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[\"a\"=1]')",
            2234,
            "XQuery [query()]: The operator \"=\" cannot be applied to \"xs:string\" and \"xs:integer\" operands.");

    // ---- what a predicate means ------------------------------------------

    [TestMethod]
    public void NumericPredicate_IsPositional()
    {
        AreEqual("<a x=\"2\">v2</a>", Query(TwoAttributes, "/r/a[2]"));
        AreEqual("<a x=\"1\">v1</a>", Query(TwoAttributes, "/r/a[1.0]"));
        AreEqual(string.Empty, Query(TwoAttributes, "/r/a[0]"));
    }

    [TestMethod]
    public void ComputedNumericPredicate_IsAlsoPositional() =>
        AreEqual("<a x=\"2\">v2</a>", Query(TwoAttributes, "/r/a[count(/r/a)]"));

    [TestMethod]
    public void StringPredicate_Raises2203() =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[\"a\"]')",
            2203,
            "XQuery [query()]: Only 'http://www.w3.org/2001/XMLSchema#decimal?', "
            + "'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*' expressions allowed as predicates, found 'xs:string'");

    [TestMethod]
    public void AtomizedPredicate_Raises2203_QuotingTheUntypedType() =>
        AreEqual(
            "XQuery [query()]: Only 'http://www.w3.org/2001/XMLSchema#decimal?', "
            + "'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*' expressions allowed as predicates, found 'xdt:untypedAtomic ?'",
            QueryError(TwoAttributes, "/r/a[data(@x)]", 2203).Message);

    [TestMethod]
    public void NodePredicate_IsAnExistenceTest()
    {
        const string Sparse = "<r><a x=\"1\"/><a/><a e=\"\"/></r>";
        AreEqual("<a x=\"1\"/>", Query(Sparse, "/r/a[@x]"));
        AreEqual("<a/><a e=\"\"/>", Query(Sparse, "/r/a[not(@x)]"));

        // An empty attribute is still a node, so it selects.
        AreEqual("<a e=\"\"/>", Query(Sparse, "/r/a[@e]"));
    }

    [TestMethod]
    public void BooleanConstructorsAndPositionFunctions()
    {
        AreEqual("<a x=\"1\">v1</a><a x=\"2\">v2</a>", Query(TwoAttributes, "/r/a[true()]"));
        AreEqual(string.Empty, Query(TwoAttributes, "/r/a[false()]"));
        AreEqual("<a x=\"2\">v2</a>", Query(TwoAttributes, "/r/a[position()=2]"));
        AreEqual("<a x=\"2\">v2</a>", Query(TwoAttributes, "/r/a[last()]"));
        AreEqual("<a x=\"2\">v2</a>", Query(TwoAttributes, "/r/a[position()=last()]"));
    }

    // ---- boolean composition ---------------------------------------------

    [TestMethod]
    public void AndBindsTighterThanOr()
    {
        const string Pairs = "<r><a x=\"1\" y=\"p\"/><a x=\"1\" y=\"q\"/><a x=\"2\" y=\"p\"/></r>";
        AreEqual("<a x=\"1\" y=\"p\"/>", Query(Pairs, "/r/a[@x=\"1\" and @y=\"p\"]"));
        AreEqual("<a x=\"1\" y=\"q\"/><a x=\"2\" y=\"p\"/>", Query(Pairs, "/r/a[@x=\"2\" or @y=\"q\"]"));
        AreEqual("<a x=\"1\" y=\"p\"/><a x=\"1\" y=\"q\"/>", Query(Pairs, "/r/a[@x=\"1\" and @y=\"p\" or @y=\"q\"]"));
        AreEqual("<a x=\"1\" y=\"p\"/><a x=\"1\" y=\"q\"/>", Query(Pairs, "/r/a[@x=\"1\" and (@y=\"p\" or @y=\"q\")]"));
        AreEqual("<a x=\"2\" y=\"p\"/>", Query(Pairs, "/r/a[not(@x=\"1\") and @y=\"p\"]"));
    }

    // ---- predicate composition -------------------------------------------

    [TestMethod]
    public void ChainedPredicatesFilterInWrittenOrder()
    {
        const string Pairs = "<r><a x=\"1\" y=\"p\"/><a x=\"1\" y=\"q\"/><a x=\"2\" y=\"p\"/></r>";
        AreEqual("<a x=\"1\" y=\"q\"/>", Query(Pairs, "/r/a[@x=\"1\"][2]"));
        AreEqual("<a x=\"1\" y=\"q\"/>", Query(Pairs, "/r/a[2][@x=\"1\"]"));
    }

    [TestMethod]
    public void PredicatesReachNestedPathsAndOtherAxes()
    {
        const string Nested = "<r><a x=\"1\"><b><c>v</c></b><d>1</d></a><a x=\"2\"><b><c>w</c></b><d>2</d></a></r>";
        AreEqual("<a x=\"1\"><b><c>v</c></b><d>1</d></a>", Query(Nested, "/r/a[b/c=\"v\"]"));
        AreEqual("<d>1</d>", Query(Nested, "/r/a[@x=\"1\"]/d"));
        AreEqual("<a x=\"2\"><b><c>w</c></b><d>2</d></a>", Query(Nested, "/r/*[@x=\"2\"]"));
        AreEqual("<b><c>w</c></b>", Query(Nested, "/r/a/b[../@x=\"2\"]"));
        AreEqual("<d>1</d>", Query(Nested, "/r/a/d[text()=\"1\"]"));
        AreEqual("<d>2</d>", Query(Nested, "/r/a/d[.=\"2\"]"));
    }

    // ---- arithmetic --------------------------------------------------------

    [TestMethod]
    public void ArithmeticInsideAPredicate()
    {
        const string Numbers = "<r><a n=\"2\"/></r>";
        AreEqual("<a n=\"2\"/>", Query(Numbers, "/r/a[@n+1=3]"));
        AreEqual("<a n=\"2\"/>", Query(Numbers, "/r/a[@n - 1=1]"));
        AreEqual("<a n=\"2\"/>", Query(Numbers, "/r/a[@n*2=4]"));
        AreEqual("<a n=\"2\"/>", Query(Numbers, "/r/a[@n div 2=1]"));
        AreEqual("<a n=\"2\"/>", Query(Numbers, "/r/a[@n mod 2=0]"));
        AreEqual("<a n=\"2\"/>", Query(Numbers, "/r/a[-@n=-2]"));
    }

    /// <summary>
    /// XQuery's name grammar swallows a <c>-</c> that follows a name
    /// character, so <c>@n-1</c> reads the attribute named <c>n-1</c> — real
    /// answers no match here rather than 1 (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void HyphenWithoutSpace_ContinuesTheName() =>
        AreEqual(string.Empty, Query("<r><a n=\"2\"/></r>", "/r/a[@n-1=1]"));

    // ---- the function library ---------------------------------------------

    [TestMethod]
    public void StringFunctionsInsidePredicates()
    {
        const string Words = "<r><a x=\"abcdef\"/><a x=\"xyz\"/></r>";
        AreEqual("<a x=\"abcdef\"/>", Query(Words, "/r/a[contains(@x,\"cd\")]"));
        AreEqual("<a x=\"xyz\"/>", Query(Words, "/r/a[string-length(@x)=3]"));
        AreEqual("<a x=\"abcdef\"/>", Query(Words, "/r/a[substring(@x,1,3)=\"abc\"]"));
        AreEqual("<a x=\"xyz\"/>", Query(Words, "/r/a[upper-case(@x)=\"XYZ\"]"));
        AreEqual("<a x=\"xyz\"/>", Query(Words, "/r/a[lower-case(@x)=\"xyz\"]"));
        AreEqual("<a x=\"xyz\"/>", Query(Words, "/r/a[concat(@x,\"!\")=\"xyz!\"]"));
        AreEqual("<a x=\"xyz\"/>", Query(Words, "/r/a[string(@x)=\"xyz\"]"));
        AreEqual("<a x=\"abcdef\"/><a x=\"xyz\"/>", Query(Words, "/r/*[local-name()=\"a\"]"));
        AreEqual("<a x=\"abcdef\"/><a x=\"xyz\"/>", Query(Words, "/r/a[count(/r/a)=2]"));
    }

    [TestMethod]
    public void UnknownFunction_Raises2395() =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[starts-with(@x,\"1\")]')",
            2395,
            "XQuery [query()]: There is no function '{http://www.w3.org/2004/07/xpath-functions}:starts-with()'");

    [TestMethod]
    public void WrongArity_Raises2236Or2238()
    {
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[contains(@x)]')",
            2236,
            "XQuery [query()]: There are not enough actual arguments in the call to function \"contains()\".");
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[count(@x,1)]')",
            2238,
            "XQuery [query()]: Too many arguments in call to function 'count()'");
    }

    [TestMethod]
    public void AtomicParameter_PluralArgument_Raises2389() =>
        AreEqual(
            "XQuery [query()]: 'contains()' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'",
            QueryError(ChildValues, "/r/p[contains(b,\"1\")]", 2389).Message);

    /// <summary>
    /// A parameter typed <c>item()?</c> rather than an atomic type quotes the
    /// node static type instead of the atomized one (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void ItemParameter_PluralArgument_Raises2389_QuotingTheNodeType() =>
        AreEqual(
            "XQuery [query()]: 'string()' requires a singleton (or empty sequence), found operand of type 'element(b,xdt:untyped) *'",
            QueryError(ChildValues, "/r/p[string(b)=\"1\"]", 2389).Message);

    // ---- namespaces --------------------------------------------------------

    [TestMethod]
    public void PrefixedNamesResolveInsidePredicates()
    {
        const string Namespaced = "<r xmlns=\"urn:d\" xmlns:p=\"urn:p\"><a p:k=\"1\" k=\"2\">v1</a><a p:k=\"9\">v2</a></r>";
        Assert.Contains(
            "v1",
            Query(Namespaced, "declare default element namespace \"urn:d\"; declare namespace p=\"urn:p\"; /r/a[@p:k=\"1\"]")!);
        Assert.Contains(
            "v2",
            Query(Namespaced, "declare default element namespace \"urn:d\"; /r/a[.=\"v2\"]")!);
    }

    [TestMethod]
    public void UndeclaredPrefix_Raises2229() =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[nosuch:b=\"1\"]')",
            2229,
            "XQuery [query()]: The name \"nosuch\" does not denote a namespace.");

    // ---- the five front doors ----------------------------------------------

    [TestMethod]
    public void ValueReadsThroughAPredicate() =>
        AreEqual("v2", Value(TwoAttributes, "(/r/a[@x=\"2\"])[1]", "nvarchar(10)"));

    /// <summary>
    /// <c>value()</c> carries its own singleton rule, and a filtering predicate
    /// doesn't satisfy it — real types <c>/r/a[@x="1"]</c> as a sequence
    /// whatever the instance holds, so only a <c>(…)[1]</c> wrapper works
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void ValueOverAPluralPath_Raises2389() =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.value('/r/a[@x=\"1\"]', 'nvarchar(10)')",
            2389,
            "XQuery [value()]: 'value()' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'");

    [TestMethod]
    public void ValueOverATrailingPositionalPredicate_IsStillPlural() =>
        _ = new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.value('/r/a[@x=\"1\"][1]', 'nvarchar(10)')", 2389);

    [TestMethod]
    public void ValueOverAPositionallyNarrowedPathAtEveryStep_Reads() =>
        AreEqual("v1", Value(TwoAttributes, "/r[1]/a[1]", "nvarchar(10)"));

    [TestMethod]
    public void ExistAnswersWhetherTheSequenceIsNonEmpty()
    {
        IsTrue(Exists(TwoAttributes, "/r/a[@x=\"1\"]"));
        IsFalse(Exists(TwoAttributes, "/r/a[@x=\"9\"]"));

        // Real's rule is emptiness, not the effective boolean value: a false
        // boolean is still one item (probe-confirmed).
        IsTrue(Exists(TwoAttributes, "false()"));
        IsTrue(Exists(TwoAttributes, "1=2"));
        IsFalse(Exists(TwoAttributes, "()"));
    }

    [TestMethod]
    public void NodesFiltersRowsThroughAPredicate() =>
        AreEqual("v2", new Simulation().ExecuteScalar($"""
            create table dbo.doc (body xml);
            insert dbo.doc values (N'{TwoAttributes}');
            select n.ref.value('(.)[1]', 'nvarchar(10)')
            from dbo.doc cross apply doc.body.nodes('/r/a[@x="2"]') as n(ref)
            """));

    /// <summary>
    /// Serialization is real's: no indentation, and an empty element
    /// self-closes with no space before the slash — neither of which
    /// <c>XPathNavigator.OuterXml</c> would produce.
    /// </summary>
    [TestMethod]
    public void SelectedNodesSerializeTheWayRealReturnsThem()
    {
        AreEqual("<p><b>1</b><b>2</b></p>", Query("<r><p><b>1</b><b>2</b></p></r>", "/r/p"));
        AreEqual("<a x=\"1\"/>", Query("<r><a x=\"1\"/></r>", "/r/a"));
        AreEqual("<r><a x=\"1\">t</a></r>", Query("<r>  <a x=\"1\">t</a>  </r>", "/r"));
    }

    /// <summary>
    /// A <c>.nodes()</c> row re-declares the namespaces its node sat under, so
    /// a relative read against the row resolves the same prefixed names.
    /// </summary>
    [TestMethod]
    public void NodesRowsCarryTheirNamespaceScope() =>
        AreEqual("1", new Simulation().ExecuteScalar("""
            create table dbo.doc (body xml);
            insert dbo.doc values (N'<r xmlns:p="urn:p"><p:a p:k="1">v</p:a></r>');
            select n.ref.value('declare namespace p="urn:p"; (@p:k)[1]', 'nvarchar(10)')
            from dbo.doc cross apply doc.body.nodes('declare namespace p="urn:p"; /r/p:a') as n(ref)
            """));

    [TestMethod]
    public void ModifyTargetsThroughAPredicate()
    {
        var simulation = new Simulation();
        AreEqual("<r><a x=\"1\">v1</a><a x=\"2\">Z</a></r>", simulation.ExecuteScalar(
            $"declare @x xml = '{TwoAttributes}'; set @x.modify('replace value of (/r/a[@x=\"2\"]/text())[1] with \"Z\"'); select @x"));
        AreEqual("<r><a x=\"2\">v2</a></r>", simulation.ExecuteScalar(
            $"declare @x xml = '{TwoAttributes}'; set @x.modify('delete /r/a[@x=\"1\"]'); select @x"));
        AreEqual("<r><a x=\"1\">v1</a><a x=\"2\">v2<c/></a></r>", simulation.ExecuteScalar(
            $"declare @x xml = '{TwoAttributes}'; set @x.modify('insert <c/> into (/r/a[@x=\"2\"])[1]'); select @x"));
    }

    // ---- rejections --------------------------------------------------------

    [TestMethod]
    public void UnsupportedOperators_Raise9335()
    {
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[@x to 3]')",
            9335,
            "XQuery [query()]: The XQuery syntax 'to' is not supported.");
        _ = new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[@x union @x]')", 9335);
    }

    [TestMethod]
    public void MalformedPredicate_Raises2209() =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '{TwoAttributes}'; select @x.query('/r/a[@x =]')",
            2209,
            "XQuery [query()]: Syntax error near ']'");

    /// <summary>
    /// The expression compiles while the statement parses, so a static
    /// diagnostic fires without a row ever being read — matching real, which
    /// reports these at compile time.
    /// </summary>
    [TestMethod]
    public void StaticDiagnosticsFireOverAnEmptyRowset() =>
        _ = new Simulation().AssertSqlError(
            "create table dbo.doc (body xml); select body.query('/r/p[b eq 1]') from dbo.doc", 2389);
}
