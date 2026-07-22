using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A <c>CREATE SYNONYM</c> alias: a name in a schema that forwards to a base
/// object (the synonym's target). Resolution redirects a FROM-source
/// reference to the synonym onto <see cref="BaseObject"/> — e.g.
/// <c>SELECT * FROM syn</c> reads the table / view <c>syn FOR t</c> named.
/// </summary>
/// <remarks>
/// Kept deliberately lightweight — a synonym is a pure name indirection, not
/// a materialized object. Catalog projection (<c>sys.synonyms</c> /
/// <c>sys.objects</c>), <c>OBJECT_ID('syn')</c>, and synonym targets for EXEC
/// / function / sequence references are not modeled; the resolver covers the
/// FROM-source table / view path only (see <c>BatchContext.TryResolveTable</c>
/// / <c>TryResolveView</c>).
/// </remarks>
internal sealed class Synonym(string name, MultiPartName baseObject)
{
    public readonly string Name = name;

    /// <summary>The target object as written in <c>CREATE SYNONYM name FOR &lt;target&gt;</c>.</summary>
    public readonly MultiPartName BaseObject = baseObject;
}
