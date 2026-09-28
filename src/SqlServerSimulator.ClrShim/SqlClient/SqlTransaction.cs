using System.Data.Common;
using System.Runtime.CompilerServices;

#pragma warning disable IDE0130 // The namespace is .NET Framework's own, which a SQLCLR assembly's references name.

namespace System.Data.SqlClient;

/// <summary>
/// A transaction <see cref="SqlConnection.BeginTransaction()"/> started on the
/// context connection: <c>BEGIN TRANSACTION</c> in the calling session, so it
/// nests inside one the caller holds, and its <c>ROLLBACK</c> meets the
/// server's refusal to end a transaction the routine didn't start (probed
/// 2026-09-28 against SQL Server 2025).
/// </summary>
public sealed class SqlTransaction : DbTransaction
{
    private const string Completed = "This SqlTransaction has completed; it is no longer usable.";

    private readonly IsolationLevel isolationLevel;

    internal SqlTransaction(SqlConnection connection, IsolationLevel isolationLevel)
    {
        this.Connection = connection;
        this.isolationLevel = isolationLevel;
    }

    public new SqlConnection? Connection { get; private set; }

    public override IsolationLevel IsolationLevel => this.Connection is null ? throw new InvalidOperationException(Completed) : this.isolationLevel;

    protected override DbConnection? DbConnection => this.Connection;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Commit() => this.End("COMMIT TRANSACTION");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Rollback() => this.End("ROLLBACK TRANSACTION");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Rollback(string transactionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(transactionName);
        (this.Connection ?? throw new InvalidOperationException(Completed)).RunChecked("ROLLBACK TRANSACTION " + Bracket(transactionName));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Save(string savePointName)
    {
        ArgumentException.ThrowIfNullOrEmpty(savePointName);
        (this.Connection ?? throw new InvalidOperationException(Completed)).RunChecked("SAVE TRANSACTION " + Bracket(savePointName));
    }

    private void End(string statement)
    {
        var owner = this.Connection ?? throw new InvalidOperationException(Completed);
        this.Connection = null;
        owner.PendingTransaction = null;
        owner.RunChecked(statement);
    }

    private static string Bracket(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
