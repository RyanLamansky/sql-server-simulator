// The shapes the context connection's commands and results cross into the
// embedded Microsoft.SqlServer.Server build in: framework tuples only, declared
// there under the same aliases, so the two assemblies share no type.

// A command: its text (a procedure's name for CommandType.StoredProcedure),
// its parameters, and whether its outcomes go straight to the routine's client
// (SqlPipe.ExecuteAndSend) rather than back to the caller.
global using ContextCommand = (
    string Text,
    bool Procedure,
    (string Name, System.Data.SqlDbType Type, bool Typed, int Size, byte Precision, byte Scale, System.Data.ParameterDirection Direction, object? Value)[] Parameters,
    bool ToPipe);

// What a command produced, in order — each item a ContextResultSet or a
// ContextMessage — with its rows-affected total (-1 when no statement counted
// any) and each parameter's value after it ran, in the SqlTypes form.
global using ContextResult = (object[] Items, int RecordsAffected, object?[] OutputValues);

// One result set: per column its name, type name, GetFieldType and
// GetProviderSpecificFieldType, then the rows in the provider-specific form,
// whether the error after it cut it short (which the reader raises from the
// Read that runs out of rows), and the simulator's own handle to it, which
// SqlPipe.Send(SqlDataReader) passes back.
global using ContextResultSet = (string[] Names, string[] TypeNames, System.Type[] FieldTypes, System.Type[] SqlFieldTypes, object?[][] Rows, bool EndedByError, object Handle);

// One error or informational message.
global using ContextMessage = (int Number, byte Class, byte State, int LineNumber, string Procedure, string Message, string Server);
