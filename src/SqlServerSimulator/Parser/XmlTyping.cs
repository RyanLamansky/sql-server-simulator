using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The schema collection and <c>DOCUMENT</c> facet an <c>xml</c> expression
/// is typed by — part of the type to real, which keeps it through a column
/// reference, a variable, a typed <c>CAST</c>, a view, a derived table, a CTE
/// and the value-selecting forms, and refuses a <c>CASE</c> or <c>UNION
/// ALL</c> mixing two (Msg 526) (probed 2026-10-15 against SQL Server 2025).
/// The default is an untyped <c>xml</c>, or a value of any other type.
/// </summary>
internal readonly struct XmlTyping(XmlSchemaCollection? collection, bool document) : IEquatable<XmlTyping>
{
    public readonly XmlSchemaCollection? Collection = collection;

    /// <summary><c>xml(DOCUMENT …)</c>; false for <c>CONTENT</c>, the default.</summary>
    public readonly bool Document = document;

    public bool IsTyped => this.Collection is not null;

    public bool Equals(XmlTyping other) => ReferenceEquals(this.Collection, other.Collection) && this.Document == other.Document;

    public override bool Equals(object? obj) => obj is XmlTyping other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Collection, this.Document);

    public static bool operator ==(XmlTyping left, XmlTyping right) => left.Equals(right);

    public static bool operator !=(XmlTyping left, XmlTyping right) => !left.Equals(right);
}
