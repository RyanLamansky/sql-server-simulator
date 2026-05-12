using System.Data.Common;
using System.Runtime.CompilerServices;

namespace SqlServerSimulator;

/// <summary>
/// C# 14 extension members exposing <c>SqlParameter.TypeName</c>-shaped
/// surface for table-valued-parameter binding without taking a dependency
/// on <c>Microsoft.Data.SqlClient</c>. ADO.NET callers using a simulator
/// <see cref="DbParameter"/> set the user-defined-table-type name via
/// <c>parameter.TypeName = "dbo.MyType"</c>; the simulator's command
/// execution path reads the same property to materialize the parameter's
/// <see cref="System.Data.DataTable"/> or <see cref="System.Data.IDataReader"/>
/// value into the per-batch
/// <see cref="Parser.BatchContext.TableVariables"/> dict.
/// </summary>
/// <remarks>
/// <para>
/// Backed by a process-wide <see cref="ConditionalWeakTable{TKey, TValue}"/>
/// so storage is keyed by parameter-instance identity and unreferenced
/// parameters get reclaimed automatically. Setting the empty string clears
/// the mapping — matching <c>SqlParameter.TypeName</c>'s defaults-to-empty
/// behavior (probe-confirmed: <c>SqlParameter.TypeName</c> defaults to
/// <c>""</c>, not <c>null</c>).
/// </para>
/// <para>
/// The extension applies to any <see cref="DbParameter"/> (not just the
/// simulator's internal subclass) so callers don't need to know which
/// concrete parameter type their <c>DbCommand</c> handed back. Using the
/// extension on a real <c>SqlParameter</c> is harmless — it writes to the
/// simulator's table without affecting the real instance's own
/// <c>TypeName</c> property, but real <c>SqlParameter</c> consumers don't
/// reach the simulator's binding path anyway.
/// </para>
/// </remarks>
public static class TableValuedParameterExtensions
{
#pragma warning disable IDE0052 // Accessed inside extension(...) block; analyzer doesn't yet trace through the new C# 14 form.
    private static readonly ConditionalWeakTable<DbParameter, string> TypeNames = [];
#pragma warning restore IDE0052

#pragma warning disable CA1034 // The extension(...) block is the canonical C# 14 syntax for grouping extension members; suppressing the nested-type warning is the documented path.
    extension(DbParameter parameter)
    {
        /// <summary>
        /// Gets or sets the user-defined-table-type name (<c>schema.name</c>)
        /// for a table-valued parameter. Mirrors <c>SqlParameter.TypeName</c>:
        /// default is the empty string, and setting null or empty clears the
        /// mapping. The simulator's command-execution path consults this
        /// alongside <see cref="DbParameter.Value"/> — when both a non-empty
        /// <c>TypeName</c> and a <see cref="System.Data.DataTable"/> /
        /// <see cref="System.Data.IDataReader"/>-typed <see cref="DbParameter.Value"/>
        /// are present, the parameter is bound as a TVP rather than a scalar.
        /// </summary>
        public string TypeName
        {
            get => TypeNames.TryGetValue(parameter, out var name) ? name : "";
            set
            {
                if (string.IsNullOrEmpty(value))
                    _ = TypeNames.Remove(parameter);
                else
                    TypeNames.AddOrUpdate(parameter, value);
            }
        }
    }
#pragma warning restore CA1034
}
