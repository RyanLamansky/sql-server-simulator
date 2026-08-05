using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What binding an <c>xml</c> value to a schema collection changes about an
/// XQuery expression: its <b>static cardinality</b>. Real narrows a named child
/// step to a singleton when the collection declares that element at most once,
/// which is what lets <c>.value()</c> read a path it refuses over untyped
/// <c>xml</c> (Msg 2389). AdventureWorks' own <c>Person.vAdditionalContactInfo</c>
/// is the shape this exists for: it reads
/// <c>(act:telephoneNumber)[1]/act:number</c> off a <c>.nodes()</c> row, so the
/// binding has to survive the hop through <c>.nodes()</c> as well.
/// <para>Probed against SQL Server 2025 on 2026-08-05.</para>
/// </summary>
[TestClass]
public sealed class TypedXmlCardinalityTests
{
    /// <summary>
    /// <c>t:b</c> is declared once per <c>t:a</c>; <c>t:m</c> is unbounded, and
    /// <c>t:a</c> itself is unbounded under <c>t:r</c>.
    /// </summary>
    private const string Collection = """
        create xml schema collection tc as N'<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
          targetNamespace="urn:t" xmlns:t="urn:t" elementFormDefault="qualified">
          <xsd:element name="r"><xsd:complexType><xsd:sequence>
            <xsd:element name="a" minOccurs="0" maxOccurs="unbounded"><xsd:complexType><xsd:sequence>
              <xsd:element name="b" type="xsd:string" minOccurs="0" maxOccurs="1"/>
              <xsd:element name="m" type="xsd:string" minOccurs="0" maxOccurs="unbounded"/>
            </xsd:sequence></xsd:complexType></xsd:element>
          </xsd:sequence></xsd:complexType></xsd:element></xsd:schema>'
        """;

    private const string Prolog = "declare namespace t=\"urn:t\"; ";

    private static Simulation WithTypedColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Collection);
        _ = sim.ExecuteNonQuery("""
            create table tt (id int, x xml(tc), u xml);
            insert tt values (1,
                N'<t:r xmlns:t="urn:t"><t:a><t:b>hi</t:b><t:m>m1</t:m><t:m>m2</t:m></t:a></t:r>',
                N'<r><a><b>hi</b></a></r>');
            """);
        return sim;
    }

    /// <summary>The direct form: a typed column's own <c>.value()</c>.</summary>
    [TestMethod]
    public void SingletonUnderTheSchema_Answers()
        => AreEqual("hi", WithTypedColumn().ExecuteScalar(
            $"select x.value('{Prolog}(/t:r/t:a)[1]/t:b', 'nvarchar(50)') from tt"));

    /// <summary>
    /// The AdventureWorks shape — the binding reaches the <c>.value()</c>
    /// through the node column <c>.nodes()</c> produced.
    /// </summary>
    [TestMethod]
    public void ThroughANodesRow_Answers()
        => AreEqual("hi", WithTypedColumn().ExecuteScalar(
            $"select c.ref.value('{Prolog}(t:a)[1]/t:b', 'nvarchar(50)') "
            + $"from tt outer apply x.nodes('{Prolog}/t:r') as c(ref)"));

    /// <summary>
    /// A variable takes the binding too — <c>DECLARE @x xml(&lt;collection&gt;)</c>
    /// is the form real accepts alongside the column one.
    /// </summary>
    [TestMethod]
    public void TypedVariable_Answers()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Collection);
        AreEqual("vv", sim.ExecuteScalar(
            "declare @v xml(tc) = N'<t:r xmlns:t=\"urn:t\"><t:a><t:b>vv</t:b></t:a></t:r>'; "
            + $"select @v.value('{Prolog}(/t:r/t:a)[1]/t:b', 'nvarchar(50)')"));
    }

    /// <summary>
    /// The binding narrows only what the schema says is narrow: a repeatable
    /// element stays a sequence, and so does a path whose earlier step is one.
    /// </summary>
    [TestMethod]
    [DataRow("(/t:r/t:a)[1]/t:m")]
    [DataRow("/t:r/t:a/t:b")]
    public void PluralUnderTheSchema_IsStillMsg2389(string path)
        => _ = WithTypedColumn().AssertSqlError($"select x.value('{Prolog}{path}', 'nvarchar(50)') from tt", 2389);

    /// <summary>Untyped <c>xml</c> is untouched — the same paths refuse and answer exactly as before.</summary>
    [TestMethod]
    public void UntypedColumn_IsUnchanged()
    {
        var sim = WithTypedColumn();
        _ = sim.AssertSqlError("select u.value('(/r/a)[1]/b', 'nvarchar(50)') from tt", 2389);
        AreEqual("hi", sim.ExecuteScalar("select u.value('(/r/a/b)[1]', 'nvarchar(50)') from tt"));
    }

    /// <summary>An untyped variable likewise.</summary>
    [TestMethod]
    public void UntypedVariable_IsUnchanged()
        => _ = new Simulation().AssertSqlError(
            "declare @w xml = N'<r><a><b>ww</b></a></r>'; select @w.value('(/r/a)[1]/b', 'nvarchar(50)')", 2389);

    /// <summary>
    /// The other three methods have no singleton requirement, so the binding
    /// changes nothing they answer.
    /// </summary>
    [TestMethod]
    public void TheOtherMethodsAreUnaffected()
    {
        var sim = WithTypedColumn();
        IsTrue((bool)sim.ExecuteScalar($"select x.exist('{Prolog}/t:r/t:a') from tt")!);
        AreEqual("<t:b xmlns:t=\"urn:t\">hi</t:b>", sim.ExecuteScalar($"select x.query('{Prolog}(/t:r/t:a)[1]/t:b') from tt"));
    }

    /// <summary>
    /// A global element declaration carries no occurrence of its own — its
    /// cardinality comes from wherever it is referenced — so naming one does
    /// not narrow anything. <c>t:r</c> is this collection's global element.
    /// </summary>
    [TestMethod]
    public void GlobalElementDeclaration_DoesNotNarrow()
        => _ = WithTypedColumn().AssertSqlError($"select x.value('{Prolog}/t:r', 'nvarchar(50)') from tt", 2389);

    /// <summary>An XSD the reader can't get through leaves the value untyped rather than failing the query.</summary>
    [TestMethod]
    public void UnparseableSchemaText_FallsBackToUntyped()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection bad as N'<xsd:schema'");
        _ = sim.ExecuteNonQuery("create table bt (x xml(bad)); insert bt values (N'<r><a><b>hi</b></a></r>');");
        _ = sim.AssertSqlError("select x.value('(/r/a)[1]/b', 'nvarchar(50)') from bt", 2389);
    }
}
