using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Resolves a type-reference position (CREATE TABLE column type, DECLARE
    /// <c>@v</c>, procedure / function / sequence parameter, ALTER TABLE
    /// ALTER COLUMN, OPENJSON column, sp_executesql parameter) to its
    /// concrete <see cref="SqlType"/> + max-length pair. Checks the per-
    /// database <see cref="Schema.AliasTypes"/> dictionary first; if the
    /// qualified name matches a registered <see cref="AliasType"/>, expands
    /// to its underlying type. Otherwise falls back to the built-in lookup
    /// in <see cref="SqlType.GetByName"/>.
    /// </summary>
    /// <param name="batch">The current per-batch context (carries the schema dict).</param>
    /// <param name="qualifiedTypeName">
    /// The 1- or 2-part dotted type reference parsed from source.
    /// </param>
    /// <param name="leafToken">
    /// The leaf <see cref="Name"/> token from <paramref name="qualifiedTypeName"/>
    /// — used as the input to <see cref="SqlType.GetByName"/> when the alias
    /// lookup misses. The leaf carries the line-number metadata
    /// <see cref="SqlType.GetByName"/> threads into Msg-1001/2716/etc.
    /// </param>
    /// <param name="declaredMaxLength">
    /// Length / precision parsed from the optional <c>(N[, S])</c> trailer
    /// at the consumer site. Non-null when the consumer wrote a width
    /// parameter — for alias-typed references, this raises Msg 2716
    /// verbatim (probe-confirmed against SQL Server 2025) since alias
    /// length is fixed at CREATE TYPE time.
    /// </param>
    /// <param name="declaredScale">
    /// Scale parsed from the optional <c>(N, S)</c> trailer. Like
    /// <paramref name="declaredMaxLength"/>, non-null on an alias reference
    /// raises Msg 2716.
    /// </param>
    /// <param name="index">
    /// 1-based column / parameter index for Msg 2715 / 2716 message
    /// composition (e.g. <c>"Column, parameter, or variable #N"</c>).
    /// </param>
    /// <param name="columnName">
    /// Column / parameter / variable name for Msg 131 width-overflow
    /// composition. Null at sites that don't carry a name (CAST/CONVERT
    /// reach a different message path).
    /// </param>
    /// <returns>
    /// The resolved <see cref="SqlType"/> and its associated max-length (in
    /// bytes for varchar / varbinary, UCS-2 code units for nvarchar, or null
    /// for fixed-shape types); plus the alias's
    /// <see cref="AliasType.IsNullable"/> when the reference resolved to an
    /// alias, otherwise null. The third tuple element is the signal column
    /// declarations use to default their nullability from the alias when the
    /// column itself omits a <c>NULL</c> / <c>NOT NULL</c> marker — matches
    /// probe behavior against SQL Server 2025.
    /// </returns>
    internal static (SqlType Type, int? MaxLength, bool? AliasNullable) ResolveTypeReference(
        BatchContext batch,
        MultiPartName qualifiedTypeName,
        Name leafToken,
        int? declaredMaxLength,
        int? declaredScale,
        int index,
        string? columnName)
    {
        if (!batch.TryResolveAliasType(qualifiedTypeName, out var alias))
        {
            var (resolved, maxLength) = SqlType.GetByName(leafToken, declaredMaxLength, declaredScale, index, columnName);
            return (resolved, maxLength, null);
        }
        return declaredMaxLength is not null || declaredScale is not null
            ? throw SimulatedSqlException.CannotSpecifyColumnWidthOnAlias(
                $"{alias.Schema.Name}.{alias.Name}", index)
            : ((SqlType Type, int? MaxLength, bool? AliasNullable))(alias.UnderlyingType, alias.DeclaredMaxLength, alias.IsNullable);
    }
}
