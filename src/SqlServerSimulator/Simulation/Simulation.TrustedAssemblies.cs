using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The assemblies <c>sp_add_trusted_assembly</c> trusted, keyed by
    /// <see cref="TrustedAssemblyKey"/> of their SHA2_512 hash: server-scoped,
    /// as <c>sys.trusted_assemblies</c> reads the same list from every
    /// database, and what <c>CREATE ASSEMBLY</c> asks under <c>clr strict
    /// security</c>.
    /// </summary>
    internal readonly ConcurrentDictionary<string, TrustedAssembly> TrustedAssemblies = new(StringComparer.Ordinal);

    /// <summary>The upper-case <c>0x…</c> spelling of a 64-byte hash, which keys the list and which the procedures' messages quote.</summary>
    internal static string TrustedAssemblyKey(byte[] hash) => "0x" + Convert.ToHexString(hash);

    /// <summary>
    /// <c>sys.sp_add_trusted_assembly @hash [, @description]</c>: trusts the
    /// assembly whose SHA2_512 hash is <c>@hash</c>. A hash that isn't 64
    /// bytes of binary is Msg 214 state 191, and one already trusted Msg 10345
    /// (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpAddTrustedAssembly(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var hash = TrustedAssemblyHash(arguments);
        var description = arguments.Count > 1 && arguments[1] is { IsDefault: false, Value: { IsNull: false } value }
            ? value.CoerceTo(SqlType.NVarchar).AsString
            : null;
        var simulation = batch.Connection.Simulation;
        var key = TrustedAssemblyKey(hash);
        if (!simulation.TrustedAssemblies.TryAdd(key, new TrustedAssembly(hash, description, batch.CurrentStatement.UtcNow, batch.Connection.Security.Effective.LoginName)))
            throw SimulatedSqlException.AssemblyHashAlreadyTrusted(key);
    }

    /// <summary>
    /// <c>sys.sp_drop_trusted_assembly @hash</c>: takes the hash off the list,
    /// Msg 10346 when it isn't there (probed 2026-10-07 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDropTrustedAssembly(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var key = TrustedAssemblyKey(TrustedAssemblyHash(arguments));
        if (!batch.Connection.Simulation.TrustedAssemblies.TryRemove(key, out _))
            throw SimulatedSqlException.AssemblyHashNotTrusted(key);
    }

    private static byte[] TrustedAssemblyHash(List<ProcArgument> arguments) =>
        arguments is [{ IsDefault: false, Value: { IsNull: false, Type: BinarySqlType or VarbinarySqlType } value }, ..] && value.AsBytes is { Length: 64 } hash
            ? hash
            : throw SimulatedSqlException.TrustedAssemblyHashExpected();
}

/// <summary>One <c>sys.trusted_assemblies</c> row.</summary>
internal sealed class TrustedAssembly(byte[] hash, string? description, DateTime createDate, string createdBy)
{
    public readonly byte[] Hash = hash;
    public readonly string? Description = description;
    public readonly DateTime CreateDate = createDate;
    public readonly string CreatedBy = createdBy;
}
