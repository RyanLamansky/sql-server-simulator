using System.Data;
using System.Text;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The context connection of one SQLCLR routine call: what the embedded
    /// <c>System.Data.SqlClient</c> surface runs a command through, and the
    /// routine-level state the server keeps around those commands. Built when a
    /// procedure or trigger runs, or a function marked
    /// <c>DataAccessKind.Read</c> / <c>SystemDataAccessKind.Read</c>, in an
    /// assembly that references <c>System.Data</c>; the call closes it with
    /// <see cref="Leave"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each command is a batch of its own in the calling session, one level
    /// deeper in <c>@@NESTLEVEL</c>, with no procedure frame: it continues past
    /// a statement's error as a client batch does, and every error it met comes
    /// back to the routine as a <c>SqlException</c> rather than reaching the
    /// client. A trigger's commands carry its frame, so they read
    /// <c>INSERTED</c> / <c>DELETED</c>, <c>EVENTDATA()</c> and
    /// <c>COLUMNS_UPDATED()</c>, which dynamic SQL inside them does not. The
    /// commands of one call share its <c>#temp</c> scope, database and
    /// <c>SET</c> options, which revert when the routine returns (all probed
    /// 2026-09-28 against SQL Server 2025).
    /// </para>
    /// <para>
    /// A transaction the caller held on entry may not be ended inside:
    /// a <c>ROLLBACK</c> is Msg 3994 and a <c>COMMIT</c> that would end it Msg
    /// 3990, and either — or an error that ends a transaction — leaves it
    /// ended, which the routine's return turns into Msg 3991 and its throw into
    /// Msg 6549, rolling it back. A <c>@@TRANCOUNT</c> the routine leaves
    /// changed is Msg 3992. A transaction the routine began itself, with none
    /// held on entry, rolls back silently when it returns.
    /// </para>
    /// </remarks>
    internal sealed class ClrContextConnection
    {
        private readonly Simulation simulation;
        private readonly SimulatedDbConnection connection;
        private readonly string routineName;
        private readonly TriggerFrame? triggerFrame;
        private readonly bool isFunction;
        private readonly bool restrictsUserData;
        private readonly ClrProcedurePipe? pipe;
        private readonly ClrContextConnection? enclosing;
        private readonly Database enteredDatabase;
        private readonly bool enteredNoCount;
        private readonly SimulatedDbConnection.SessionOptionScope enteredOptions;
        private readonly SimulatedDbTransaction? entryTransaction;
        private readonly List<HeapTable> tempTables = [];
        private int tempTableScopeId;

        /// <summary>Whether the routine's assembly is <c>SAFE</c>, which refuses any connection but this one.</summary>
        public readonly bool Safe;

        /// <summary>The version the connection reports, <c>major.minor.build</c> as <c>SqlConnection.ServerVersion</c> spells it.</summary>
        public readonly string ServerVersion = ReferenceBuild.MajorMinorBuild;

        /// <summary>
        /// Whether a transaction was open when the routine was entered — an
        /// explicit one, or the unit a trigger's firing statement runs in.
        /// </summary>
        public readonly bool HasEntryTransaction;

        /// <summary><c>@@TRANCOUNT</c> on entry.</summary>
        public readonly int EntryTranCount;

        /// <summary>Set once a command ended, or tried to end, the transaction held on entry.</summary>
        public bool EntryTransactionEnded;

        /// <param name="outerBatch">The batch that called the routine.</param>
        /// <param name="routineName">The routine's name, as its errors report it.</param>
        /// <param name="assembly">The routine's assembly.</param>
        /// <param name="pipe">A procedure's or trigger's pipe, where <c>SqlPipe.ExecuteAndSend</c> delivers.</param>
        /// <param name="triggerFrame">A trigger's frame, whose pseudo-tables its commands read.</param>
        /// <param name="isFunction">
        /// True for a function, whose commands may not write: a side-effecting
        /// statement is Msg 443 at state 2.
        /// </param>
        /// <param name="restrictsUserData">
        /// True for a function marked <c>SystemDataAccessKind.Read</c> but not
        /// <c>DataAccessKind.Read</c>, whose commands read no user object.
        /// </param>
        public ClrContextConnection(BatchContext outerBatch, string routineName, SqlAssembly assembly, ClrProcedurePipe? pipe, TriggerFrame? triggerFrame, bool isFunction, bool restrictsUserData = false)
        {
            this.restrictsUserData = restrictsUserData;
            this.connection = outerBatch.Connection;
            this.simulation = this.connection.Simulation;
            this.routineName = routineName;
            this.Safe = assembly.PermissionSet == AssemblyPermissionSet.Safe;
            this.pipe = pipe;
            this.triggerFrame = triggerFrame;
            this.isFunction = isFunction;
            this.enteredDatabase = this.connection.CurrentDatabase;
            this.enteredNoCount = this.connection.NoCount;
            this.enteredOptions = new SimulatedDbConnection.SessionOptionScope(this.connection);
            this.entryTransaction = this.connection.CurrentTransaction;
            this.EntryTranCount = this.entryTransaction?.TranCount ?? 0;
            this.HasEntryTransaction = this.entryTransaction is not null
                || (triggerFrame is not null && this.connection.TriggerStatementUndoLog is not null);
            this.enclosing = this.connection.ClrContext;
            this.connection.ClrContext = this;
        }

        /// <summary>The current database's name, which an open <c>SqlConnection.Database</c> reports.</summary>
        public string DatabaseName() => this.connection.CurrentDatabase.Name;

        /// <summary>
        /// Runs one command, answering the result sets, messages and errors it
        /// produced in order — or, for <c>SqlPipe.ExecuteAndSend</c>, sending
        /// them to the routine's client and answering only the errors.
        /// </summary>
        public ContextResult Execute(ContextCommand command)
        {
            var collation = this.connection.CurrentDatabase.Collation;
            var parameters = command.Parameters;
            var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
            var items = new List<object>();
            try
            {
                foreach (var parameter in parameters)
                {
                    var type = ClrTypeMarshaller.ContextParameterType(parameter.Type, parameter.Typed, parameter.Size, parameter.Precision, parameter.Scale, parameter.Value, collation);
                    var value = parameter.Direction is ParameterDirection.Output or ParameterDirection.ReturnValue
                        ? SqlValue.Null(type)
                        : ClrTypeMarshaller.ContextParameterValue(parameter.Value, type, parameter.Size);
                    variables[VariableKey(parameter.Name)] = new VariableSlot(type, DeclaredLength(type), value, parameter: null);
                }
            }
            catch (SimulatedSqlException bindError)
            {
                AddErrors(items, bindError);
                return ([.. items], -1, new object?[parameters.Length]);
            }

            var text = command.Procedure ? ExecText(command) : command.Text;
            var recordsAffected = this.Run(text, variables, command.ToPipe, items);

            var outputs = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Direction != ParameterDirection.Input)
                    outputs[i] = ClrTypeMarshaller.ToContextValue(variables[VariableKey(parameters[i].Name)].Value, collation);
            }

            return ([.. items], recordsAffected, outputs);
        }

        /// <summary>
        /// Sends <paramref name="handle"/>, a result set a command answered,
        /// to the routine's client from row <paramref name="fromRow"/> on, as
        /// <c>SqlPipe.Send(SqlDataReader)</c> does.
        /// </summary>
        public void Send(object handle, int fromRow)
        {
            var resultSet = (SimulatedSqlResultSet)handle;
            List<SqlValue[]> rows = [.. resultSet.RowValues.Skip(fromRow)];
            this.pipe!.Forward(new SimulatedSqlResultSet(resultSet.Schema, resultSet.ColumnNames, rows)
            {
                ColumnNullability = resultSet.ColumnNullability,
                ColumnReportsNumeric = resultSet.ColumnReportsNumeric,
            });
        }

        /// <summary>
        /// Closes the context when the routine is done: restores what its
        /// commands changed for the routine's duration, and settles the
        /// transaction. Answers the error the call ends with in place of — or,
        /// for a routine that returned, instead of — its own: Msg 6549 over
        /// <paramref name="report"/> when the routine threw with the entry
        /// transaction ended, Msg 3991 or 3992 when it returned so.
        /// </summary>
        /// <param name="report">The report of what the routine threw, or <see langword="null"/> when it returned.</param>
        public SimulatedSqlException? Leave(string? report)
        {
            var connection = this.connection;
            connection.ClrContext = this.enclosing;
            connection.CurrentDatabase = this.enteredDatabase;
            connection.NoCount = this.enteredNoCount;
            this.enteredOptions.Restore(connection);
            for (var i = this.tempTables.Count - 1; i >= 0; i--)
                connection.RemoveTempTable(this.tempTables[i]);

            var current = connection.CurrentTransaction;
            if (!this.HasEntryTransaction)
            {
                // A transaction the routine began and left open goes quietly.
                current?.Rollback();
                return null;
            }

            var ended = this.EntryTransactionEnded || !ReferenceEquals(current, this.entryTransaction);
            var left = current?.TranCount ?? 0;
            if (!ended && left == this.EntryTranCount)
                return null;

            // Under a TRY the transaction is left uncommittable, as an
            // XACT_ABORT error there leaves it (probed 2026-09-28 against SQL
            // Server 2025: the CATCH runs, and the batch's end reports Msg
            // 3998).
            if (connection.OpenTryFrames > 0 && current is not null)
                current.Doomed = true;
            else
                current?.Rollback();
            return report is not null ? SimulatedSqlException.ClrRoutineThrewEndingTransaction(this.routineName, report)
                : ended ? SimulatedSqlException.ClrContextTransactionEnded(this.routineName)
                : SimulatedSqlException.ClrTransactionCountChanged(this.routineName, this.EntryTranCount, left);
        }

        /// <summary>Runs <paramref name="text"/> as a batch, adding what it produced to <paramref name="items"/> and answering its rows-affected total.</summary>
        private int Run(string text, Dictionary<string, VariableSlot> variables, bool toPipe, List<object> items)
        {
            var connection = this.connection;
            var collation = connection.CurrentDatabase.Collation;
            using var command = new SimulatedDbCommand(this.simulation, connection);
#pragma warning disable CA2100 // the routine's own command text, as a client batch's is the client's
            command.CommandText = text;
#pragma warning restore CA2100
            if (this.tempTableScopeId == 0)
                this.tempTableScopeId = ++connection.LastTempTableScopeId;
            var batch = new BatchContext(command, variables, this.triggerFrame) { ContinueOnError = true, RestrictsUserData = this.restrictsUserData };
            batch.JoinTempTableScope(this.tempTableScopeId, this.tempTables);

            var outcomes = new List<SimulatedStatementOutcome>();
            SimulatedSqlException? failure = null;
            if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            {
                failure = SimulatedSqlException.MaximumNestingLevelExceeded();
            }
            else
            {
                connection.NestingLevel++;
                try
                {
                    failure = this.Compile(command, variables);
                    if (failure is null)
                    {
                        batch.Parser.MoveNextOptional();
                        foreach (var outcome in this.simulation.DispatchStatementsUntil(batch, endKeyword: null))
                            outcomes.Add(outcome);
                    }
                }
                catch (SimulatedSqlException ex)
                {
                    failure = ex;
                }
                finally
                {
                    connection.NestingLevel--;
                }
            }

            var recordsAffected = -1;
            foreach (var outcome in outcomes)
            {
                if (outcome.ClientRecordsAffected >= 0)
                    recordsAffected = Math.Max(recordsAffected, 0) + outcome.ClientRecordsAffected;
                if (toPipe)
                {
                    this.pipe!.Forward(outcome);
                    if (outcome is SimulatedErrorOutcome forwardedError)
                        AddErrors(items, forwardedError.Exception);
                    continue;
                }

                switch (outcome)
                {
                    case SimulatedSqlResultSet resultSet:
                        items.Add(ToItem(resultSet, collation));
                        break;
                    case SimulatedInfoOutcome info:
                        items.Add(ToItem(info.Message));
                        break;
                    case SimulatedErrorOutcome error:
                        AddErrors(items, error.Exception);
                        break;
                    default:
                        break;
                }
            }

            if (failure is not null)
            {
                if (toPipe)
                    this.pipe!.Forward(new SimulatedErrorOutcome(failure));
                AddErrors(items, failure);
            }

            return recordsAffected;
        }

        /// <summary>
        /// Compiles the command's text as the server does before running any
        /// of it, answering what stops it. A function's command is also walked
        /// for a statement that writes, which is Msg 443 there.
        /// </summary>
        private SimulatedSqlException? Compile(SimulatedDbCommand command, Dictionary<string, VariableSlot> variables)
        {
            var compile = new BatchContext(command, new Dictionary<string, VariableSlot>(variables, BatchContext.VariableNameComparer), this.triggerFrame);
            if (this.isFunction)
                compile.FunctionBodyShape = new FunctionBodyShape { ContextConnection = true };
            if (this.simulation.CompileBatch(compile, key: null) is { } compileError)
                return compileError;
            if (compile.FunctionBodyShape is { } shape)
            {
                foreach (var (_, violation) in shape.Violations)
                {
                    if (violation.Number == 443)
                        return violation;
                }
            }

            return null;
        }

        /// <summary>The width a sized parameter holds a value to, as a declared variable does.</summary>
        private static int? DeclaredLength(SqlType type) => type switch
        {
            NVarcharSqlType { length: > 0 } sized => sized.length,
            VarcharSqlType { length: > 0 } sized => sized.length,
            NCharSqlType { length: > 0 } sized => sized.length,
            CharSqlType { length: > 0 } sized => sized.length,
            VarbinarySqlType { length: > 0 } sized => sized.length,
            BinarySqlType { length: > 0 } sized => sized.length,
            _ => null,
        };

        private static string VariableKey(string name) => name.StartsWith('@') ? name[1..] : name;

        /// <summary>The <c>EXEC</c> a <c>CommandType.StoredProcedure</c> command stands for.</summary>
        private static string ExecText(ContextCommand command)
        {
            var text = new StringBuilder("EXEC ");
            foreach (var parameter in command.Parameters)
            {
                if (parameter.Direction == ParameterDirection.ReturnValue)
                {
                    _ = text.Append(parameter.Name).Append(" = ");
                    break;
                }
            }

            _ = text.Append(command.Text);
            var first = true;
            foreach (var parameter in command.Parameters)
            {
                if (parameter.Direction == ParameterDirection.ReturnValue)
                    continue;
                _ = text.Append(first ? " " : ", ").Append(parameter.Name).Append(" = ").Append(parameter.Name);
                if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                    _ = text.Append(" OUTPUT");
                first = false;
            }

            return text.ToString();
        }

        private static void AddErrors(List<object> items, SimulatedSqlException exception)
        {
            foreach (var error in exception.Errors)
                items.Add(ToItem(error));
        }

        /// <summary>
        /// A message as the in-process provider reports it: at line 0, where
        /// the command's own text is concerned (probed 2026-09-28 against SQL
        /// Server 2025).
        /// </summary>
        private static ContextMessage ToItem(SimulatedError error) =>
            (error.Number, error.Class, error.State, 0, error.Procedure, error.Message, error.Server);

        private static ContextResultSet ToItem(SimulatedSqlResultSet resultSet, Collation collation)
        {
            var schema = resultSet.Schema;
            var typeNames = new string[schema.Length];
            var fieldTypes = new Type[schema.Length];
            var sqlFieldTypes = new Type[schema.Length];
            for (var i = 0; i < schema.Length; i++)
            {
                typeNames[i] = schema[i] == SqlType.SystemName ? "nvarchar"
                    : resultSet.ColumnReportsNumeric is { } numeric && numeric[i] ? "numeric"
                    : schema[i].SqlServerName;
                (fieldTypes[i], sqlFieldTypes[i]) = ClrTypeMarshaller.ContextFieldTypes(schema[i]);
            }

            var rows = new List<object?[]>();
            foreach (var row in resultSet.RowValues)
            {
                var values = new object?[row.Length];
                for (var i = 0; i < row.Length; i++)
                    values[i] = ClrTypeMarshaller.ToContextValue(row[i], collation);
                rows.Add(values);
            }

            return (resultSet.ColumnNames, typeNames, fieldTypes, sqlFieldTypes, [.. rows], resultSet.EndedByError, resultSet);
        }
    }
}
