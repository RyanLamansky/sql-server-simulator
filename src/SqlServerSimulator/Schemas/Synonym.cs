using System.Text;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A <c>CREATE SYNONYM</c> alias: a name in a schema that forwards to a base
/// object (the synonym's target). Resolution redirects a reference to the
/// synonym onto <see cref="BaseObject"/> — e.g. <c>SELECT * FROM syn</c> reads
/// the table / view <c>syn FOR t</c> named, <c>EXEC syn</c> runs the procedure
/// it names, and <c>SELECT dbo.syn(1)</c> calls the scalar function.
/// </summary>
/// <remarks>
/// The synonym is a pure name indirection — it stores no columns and no body —
/// but it is a first-class schema object: it takes an <see cref="SchemaObject.ObjectId"/>,
/// occupies the shared object-name namespace (Msg 2714 in both directions), and
/// projects through <c>sys.synonyms</c> / <c>sys.objects</c> with type
/// <c>'SN'</c>. The base object is stored as written and resolved at each use,
/// so a synonym over a missing base creates successfully and raises Msg 5313
/// on first use (real's deferred-resolution semantic).
/// </remarks>
internal sealed class Synonym(Schema schema, string name, int objectId, DateTime createDate, MultiPartName baseObject)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    public override string ObjectTypeCode => "SN";
    public override string ObjectTypeDescription => "SYNONYM";

    /// <summary>The target object as written in <c>CREATE SYNONYM name FOR &lt;target&gt;</c>.</summary>
    public readonly MultiPartName BaseObject = baseObject;

    /// <summary>
    /// <c>sys.synonyms.base_object_name</c>: the base object's name with every
    /// segment bracket-quoted, in the shape real SQL Server stores it —
    /// <c>[synbase]</c> for a base written unqualified, <c>[dbo].[synbase]</c>
    /// for a 2-part base, <c>[otherdb].[dbo].[synbase]</c> for a 3-part one
    /// (probe-confirmed: the written qualification is preserved verbatim, not
    /// expanded to a full 3-part name).
    /// </summary>
    public string BaseObjectName
    {
        get
        {
            var builder = new StringBuilder();
            for (var i = 0; i < this.BaseObject.Count; i++)
            {
                if (i > 0)
                    _ = builder.Append('.');
                _ = builder.Append('[').Append(this.BaseObject[i].Replace("]", "]]", StringComparison.Ordinal)).Append(']');
            }
            return builder.ToString();
        }
    }
}
