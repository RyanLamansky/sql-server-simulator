using System.Data.Common;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// One entry in <see cref="BatchContext.Variables"/>: a declared variable
/// or a SqlClient-parameter-seeded variable. Holds the declared SqlType
/// (so <c>SET @v = expr</c> can coerce the RHS through the existing CAST
/// machinery before storing) plus the current value (mutable as the batch
/// runs). When the slot was seeded from a <see cref="DbParameter"/>, the
/// parameter reference is retained so end-of-batch processing can write the
/// final value back to <see cref="DbParameter.Value"/> for
/// <c>InputOutput</c> / <c>Output</c> direction parameters.
/// </summary>
internal sealed class VariableSlot(SqlType declaredType, int? declaredMaxLength, SqlValue value, DbParameter? parameter)
{
    public readonly SqlType DeclaredType = declaredType;

    /// <summary>
    /// Declared max length for variable-length string / binary types (e.g.
    /// <c>varchar(3)</c> stores 3); null for fixed-length and unbounded
    /// types. Routes through <c>Cast.ApplyCoercion</c> on every assignment
    /// so <c>SET @v(varchar(3)) = 'hello'</c> truncates to <c>'hel'</c>.
    /// </summary>
    public readonly int? DeclaredMaxLength = declaredMaxLength;

    public SqlValue Value = value;

    public readonly DbParameter? Parameter = parameter;

    /// <summary>
    /// The XML schema collection an <c>xml(&lt;collection&gt;)</c> declaration
    /// bound this variable to, or null for untyped <c>xml</c> and every other
    /// type. Read by an XML instance method against the variable, whose static
    /// cardinality rules the binding narrows.
    /// </summary>
    public Schemas.XmlSchemaCollection? XmlSchemaCollection;

    /// <summary>The declaration wrote <c>xml(DOCUMENT …)</c>; see <c>HeapColumn.XmlDocument</c>.</summary>
    public bool XmlDocument;

    /// <summary>
    /// For a <c>DECLARE</c>d <c>xml(&lt;collection&gt;)</c> variable, its name
    /// and its batch's <see cref="BatchContext.XmlSchemaAlterationsAtStart"/>,
    /// which <see cref="Assign"/> holds the collection against; null otherwise.
    /// </summary>
    public TypedXmlDeclaration? XmlBinding;

    /// <summary>
    /// The variable was declared <c>numeric</c> rather than <c>decimal</c>,
    /// which a reference reports as its type's name (probed 2026-09-24).
    /// </summary>
    public bool SpelledNumeric;

    /// <summary>The user alias type the variable was declared with; see <c>HeapColumn.AliasType</c>.</summary>
    public Schemas.AliasType? AliasType;

    /// <summary>
    /// The masked columns the variable's last assignment read, tracked only
    /// while a scalar UDF body is analyzed for the mask its result takes
    /// (<see cref="UdfFrame.AnalyzesReturnMask"/>); null everywhere else.
    /// </summary>
    public DataMask? Mask;

    /// <summary>
    /// Stores <paramref name="value"/>, validating and canonicalizing it first
    /// when this slot carries an <c>xml(&lt;collection&gt;)</c> binding — real
    /// does that on an assignment to a typed variable exactly as it does on a
    /// write to a typed column, so <c>DECLARE @x xml(c) = '&lt;c&gt;1.500&lt;/c&gt;'</c>
    /// reads back <c>&lt;c&gt;1.5&lt;/c&gt;</c> (probe-confirmed).
    /// </summary>
    /// <remarks>
    /// A variable whose collection was altered after its batch began refuses
    /// any assignment, a NULL included, with Msg 6323 — which rolls the
    /// transaction back and ends the batch past any <c>TRY</c> — while merely
    /// declaring one is fine (probed 2026-10-07 against SQL Server 2025, the
    /// alteration in the batch's own text or in an <c>EXEC</c> it ran).
    /// </remarks>
    public void Assign(SqlValue value)
    {
        if (this.XmlBinding is { } binding && this.XmlSchemaCollection is { } collection && collection.AlteredAt > binding.AlterationsAtStart)
            throw SimulatedSqlException.XmlSchemaCollectionAlteredDuringBatch(binding.VariableName);
        this.Value = value.Type is XmlSqlType
            ? Expressions.Cast.ValidateTypedXml(value, this.XmlSchemaCollection, this.XmlDocument)
            : value;
    }
}

/// <summary>What <see cref="VariableSlot.XmlBinding"/> records of a typed xml variable's declaration.</summary>
internal sealed class TypedXmlDeclaration(string variableName, long alterationsAtStart)
{
    public readonly string VariableName = variableName;
    public readonly long AlterationsAtStart = alterationsAtStart;
}
