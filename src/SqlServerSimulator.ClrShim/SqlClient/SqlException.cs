using System.Collections;
using System.Data.Common;

#pragma warning disable IDE0130 // The namespace is .NET Framework's own, which a SQLCLR assembly's references name.

namespace System.Data.SqlClient;

/// <summary>
/// An error a context-connection command raised, as the in-process provider
/// reports it: the first error's number, class, state and procedure, a line
/// of 0, and every error the command raised in <see cref="Errors"/> (probed
/// 2026-09-28 against SQL Server 2025).
/// </summary>
public sealed class SqlException : DbException
{
    internal SqlException(SqlErrorCollection errors)
        : base(errors.JoinedMessages())
    {
        this.Errors = errors;
        this.HResult = unchecked((int)0x80131904);
    }

    public SqlErrorCollection Errors { get; }

    public byte Class => this.Errors[0].Class;

    public Guid ClientConnectionId => Guid.Empty;

    public int LineNumber => this.Errors[0].LineNumber;

    public int Number => this.Errors[0].Number;

    public string Procedure => this.Errors[0].Procedure;

    public string Server => this.Errors[0].Server;

    public override string Source => this.Errors[0].Source;

    public byte State => this.Errors[0].State;
}

/// <summary>One error or message a context-connection command raised.</summary>
public sealed class SqlError
{
    internal SqlError(int number, byte errorClass, byte state, int lineNumber, string procedure, string message, string server)
    {
        this.Number = number;
        this.Class = errorClass;
        this.State = state;
        this.LineNumber = lineNumber;
        this.Procedure = procedure;
        this.Message = message;
        this.Server = server;
    }

    public byte Class { get; }

    public int LineNumber { get; }

    public string Message { get; }

    public int Number { get; }

    public string Procedure { get; }

    public string Server { get; }

    public string Source => ".Net SqlClient Data Provider";

    public byte State { get; }

    public override string ToString() => "System.Data.SqlClient.SqlError: " + this.Message;
}

/// <summary>The errors or messages one command raised, in order.</summary>
public sealed class SqlErrorCollection : ICollection
{
    private readonly List<SqlError> errors;

    internal SqlErrorCollection(List<SqlError> errors) => this.errors = errors;

    public int Count => this.errors.Count;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public SqlError this[int index] => this.errors[index];

    public void CopyTo(Array array, int index) => ((ICollection)this.errors).CopyTo(array, index);

    public void CopyTo(SqlError[] array, int index) => this.errors.CopyTo(array, index);

    public IEnumerator GetEnumerator() => this.errors.GetEnumerator();

    internal string JoinedMessages() => string.Join("\r\n", this.errors.Select(error => error.Message));
}

/// <summary>What <see cref="SqlConnection.InfoMessage"/> hands its handlers.</summary>
public sealed class SqlInfoMessageEventArgs : EventArgs
{
    internal SqlInfoMessageEventArgs(SqlErrorCollection errors) => this.Errors = errors;

    public SqlErrorCollection Errors { get; }

    public string Message => this.Errors.JoinedMessages();

    public string Source => ".Net SqlClient Data Provider";

    public override string ToString() => this.Message;
}

public delegate void SqlInfoMessageEventHandler(object sender, SqlInfoMessageEventArgs e);
