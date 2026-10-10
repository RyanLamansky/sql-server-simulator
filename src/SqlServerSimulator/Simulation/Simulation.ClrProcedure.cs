using System.Data;
using System.Data.SqlTypes;
using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the <c>EXTERNAL NAME assembly.[class].method</c> tail of a
    /// <c>CREATE PROCEDURE</c>, binds the method, and stores the procedure.
    /// Cursor on entry: the <c>EXTERNAL</c> word after <c>AS</c>.
    /// </summary>
    /// <remarks>
    /// Binding follows real's order (probed 2026-09-28 against SQL Server
    /// 2025): the assembly, class and method (Msg 6528 / 6505 / 6506), the
    /// parameter count (Msg 6550), the return type (Msg 6567 unless
    /// <c>void</c>, <c>int</c>, <c>int?</c> or <see cref="SqlInt32"/>), then each
    /// parameter — an <c>OUTPUT</c> parameter must be a CLR <c>ref</c> /
    /// <c>out</c> one and the reverse, or Msg 6580 precedes the Msg 6552 its
    /// type mismatch would raise. <c>ALTER</c> over a T-SQL procedure is Msg
    /// 6530, and a CLR procedure takes no <c>RECOMPILE</c> or
    /// <c>ENCRYPTION</c> option (Msg 155).
    /// </remarks>
    private static bool ParseClrProcedureTail(
        ParserContext context,
        Schema schema,
        MultiPartName procName,
        short groupNumber,
        List<ProcedureParameter> parameters,
        List<SimulatedSqlException> declarationErrors,
        string? executeAsClause,
        string? refusedOption,
        bool isAlter,
        bool createOrAlter)
    {
        var externalName = ParseExternalName(context, 3);
        if (context.Batch.IsSkipping)
            return true;

        if (refusedOption is not null)
            throw SimulatedSqlException.ExternalProcedureOptionRefused(refusedOption);

        // A CLR procedure takes no group number: `name;N` creates the
        // procedure itself, which then answers to any number — though a name
        // already taken reports Msg 2714 at state 51 there (probed 2026-09-28
        // against SQL Server 2025).
        if (groupNumber > 1 && !isAlter && !createOrAlter && schema.HasNameInSharedNamespace(procName.Leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(procName.Leaf, state: 51);

        context.Batch.LockDefinitionName(schema, procName.Leaf);
        CheckModuleDdlPermission(
            context, "CREATE PROCEDURE", procName, schema, isAlter, createOrAlter,
            schema.Procedures.GetValueOrDefault(procName.Leaf));
        var replaced = (Procedure?)ResolveModuleAlterTarget(
            context, schema, procName, isAlter, createOrAlter,
            schema.Procedures.TryGetValue(procName.Leaf, out var existing) ? existing : null);
        if (replaced is { ClrEntry: null })
            throw SimulatedSqlException.ClrAlterIncompatible(procName.Leaf);

        var (assembly, type) = ResolveClrClass(context, externalName[0], externalName[1]);
        var method = ResolveClrMethod(type, externalName[2], externalName[1], assembly.Name);
        if (HeldDeclarationErrors(declarationErrors) is { } held)
            throw held;
        var methodParameters = method.GetParameters();
        if (methodParameters.Length != parameters.Count)
            throw SimulatedSqlException.ClrParameterCountMismatch("CREATE PROCEDURE");
        if (method.ReturnType != typeof(void) && method.ReturnType != typeof(int)
            && method.ReturnType != typeof(int?) && method.ReturnType != typeof(SqlInt32))
        {
            throw SimulatedSqlException.ClrProcedureReturnType();
        }

        for (var i = 0; i < methodParameters.Length; i++)
        {
            var declared = parameters[i];
            var clrType = methodParameters[i].ParameterType;
            var mismatch = SimulatedSqlException.ClrParameterTypeMismatch("CREATE PROCEDURE", procName.Leaf, "@" + declared.Name);
            if (declared.IsOutput != clrType.IsByRef)
                throw SimulatedSqlException.Aggregate([SimulatedSqlException.ClrOutputDeclarationMismatch(i + 1), mismatch]);
            if (declared.TableType is not null || declared.IsCursor
                || !ClrTypeMarshaller.Matches(declared.Type, clrType.IsByRef ? clrType.GetElementType()! : clrType))
            {
                throw mismatch;
            }
        }

        var procedure = new Procedure(
            schema,
            procName.Leaf,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            bodyText: "",
            createDate: replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow)
        {
            ClrEntry = new ClrEntryPoint(assembly, externalName[1], externalName[2], type, method),
            ExecuteAsClause = executeAsClause,
            ExecuteAsPrincipalId = ResolveExecuteAsPrincipalId(context, executeAsClause),
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
        };
        if (replaced is not null)
            procedure.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        context.Batch.LockDefinition(schema, procedure, created: true);
        schema.Procedures[procName.Leaf] = procedure;
        RecordSlotUndo(context, schema.Procedures, procName.Leaf, replaced);
        if (replaced is not null)
            RebindExtendedProperties(context.Batch, replaced, procedure);
        RecordDdlEvent(context, replaced is null ? "CREATE_PROCEDURE" : "ALTER_PROCEDURE", schema.Name, procName.Leaf, "PROCEDURE");
        return true;
    }

    /// <summary>
    /// Runs a CLR procedure's method over the bound parameter
    /// <paramref name="variables"/>, adding what its <c>SqlContext.Pipe</c>
    /// sends to <paramref name="outcomes"/>, writing its <c>ref</c> /
    /// <c>out</c> values back into the variables, and setting the return
    /// status on <paramref name="frame"/>. Answers the error that ended it, if
    /// one did, having left the variables as they were.
    /// </summary>
    /// <remarks>
    /// What the pipe sends is attributed as real attributes it (probed
    /// 2026-09-28 against SQL Server 2025): a <c>Send(string)</c> is a class-0
    /// state-2 message at line 0 naming the procedure as the call spelled it;
    /// a result set the procedure started and never ended is closed when it
    /// returns. A throw is Msg 6522 state 1 at line 0 under the same name, and
    /// an <c>nvarchar(n)</c> output value longer than <c>n</c> is the same
    /// error over the server's TruncationException, the caller's variable
    /// keeping its value.
    /// </remarks>
    private static SimulatedSqlException? RunClrProcedure(
        BatchContext outerBatch,
        Procedure procedure,
        ClrEntryPoint entry,
        Dictionary<string, VariableSlot> variables,
        ProcFrame frame,
        List<SimulatedStatementOutcome> outcomes,
        string attributionName)
    {
        var connection = outerBatch.Connection;
        if (!connection.Simulation.EnableClr)
            return Attributed(SimulatedSqlException.ClrExecutionDisabled());

        var method = entry.Method!;
        var clrParameters = method.GetParameters();
        var arguments = new object?[clrParameters.Length];
        for (var i = 0; i < clrParameters.Length; i++)
        {
            var clrType = clrParameters[i].ParameterType;
            arguments[i] = ClrTypeMarshaller.ToClr(
                variables[procedure.Parameters[i].Name].Value, clrType.IsByRef ? clrType.GetElementType()! : clrType);
        }

        var pipe = new ClrProcedurePipe(outerBatch, attributionName, outcomes);
        object? result = null;
        string? report = null;
        SimulatedSqlException? ending;
        connection.NestingLevel++;
        var contextConnection = entry.Assembly.UsesServerContext
            ? new ClrContextConnection(outerBatch, procedure.Name, entry.Assembly, pipe, triggerFrame: null, isFunction: false)
            : null;
        try
        {
            using (CultureScope.Clr())
            using (entry.Assembly.UsesServerContext ? ClrHost.Enter(pipe.Sink, contextConnection: contextConnection) : default(ClrHost.RoutineScope?))
                result = method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            report = ClrExceptionReport.Describe(ex.InnerException, method);
        }
        finally
        {
            connection.NestingLevel--;
            ending = contextConnection?.Leave(report);
        }

        if (report is not null)
        {
            pipe.Settle(completed: false);
            return Attributed(ending ?? SimulatedSqlException.ClrRoutineThrew(procedure.Name, report, state: 1));
        }

        pipe.Settle(completed: true);
        if (ending is not null)
            return Attributed(ending);

        var written = new SqlValue?[clrParameters.Length];
        for (var i = 0; i < clrParameters.Length; i++)
        {
            if (!clrParameters[i].ParameterType.IsByRef)
                continue;
            var parameter = procedure.Parameters[i];
            var value = ClrTypeMarshaller.FromClr(arguments[i], parameter.Type);
            if (ClrTypeMarshaller.OverflowedWidth(value, parameter.Type) is { } width)
                return Attributed(SimulatedSqlException.ClrRoutineThrew(procedure.Name, ClrExceptionReport.Truncation(value.AsString.Length, width), state: 1));
            written[i] = value;
        }

        for (var i = 0; i < written.Length; i++)
        {
            if (written[i] is { } value)
                variables[procedure.Parameters[i].Name].Value = value;
        }

        frame.ReturnCode = result switch
        {
            int status => status,
            SqlInt32 { IsNull: false } status => status.Value,
            _ => 0,
        };
        return null;

        SimulatedSqlException Attributed(SimulatedSqlException error)
        {
            error.PreserveDiagnostics(0, attributionName);
            return error;
        }
    }

    /// <summary>
    /// Collects what one CLR procedure call sends through its pipe into the
    /// call's outcome list: messages as they arrive, and each result set once
    /// it ends, its column types built from the <c>SqlMetaData</c> the
    /// procedure declared.
    /// </summary>
    internal sealed class ClrProcedurePipe
    {
        public readonly ClrPipeSink Sink;

        private readonly BatchContext batch;
        private readonly string attributionName;
        private readonly List<SimulatedStatementOutcome> outcomes;
        private readonly int lineNumber;
        private SqlType[]? schema;
        private string[]? names;
        private List<SqlValue[]>? rows;

        /// <param name="batch">The calling batch.</param>
        /// <param name="attributionName">The module name a message names.</param>
        /// <param name="outcomes">Where messages and result sets land.</param>
        /// <param name="lineNumber">
        /// The line a message reports: 0 from a procedure, 1 from a trigger
        /// (probed 2026-09-28 against SQL Server 2025).
        /// </param>
        public ClrProcedurePipe(BatchContext batch, string attributionName, List<SimulatedStatementOutcome> outcomes, int lineNumber = 0)
        {
            this.batch = batch;
            this.attributionName = attributionName;
            this.outcomes = outcomes;
            this.lineNumber = lineNumber;
            this.Sink = new ClrPipeSink(this.Message, this.Start, this.Row, this.End);
        }

        /// <summary>
        /// Adds what a context-connection command sent through
        /// <c>SqlPipe.ExecuteAndSend</c> or <c>SqlPipe.Send(SqlDataReader)</c>,
        /// as it came.
        /// </summary>
        public void Forward(SimulatedStatementOutcome outcome) => this.outcomes.Add(outcome);

        private void Message(string text) => this.outcomes.Add(new SimulatedInfoOutcome(new SimulatedError(
            @class: 0,
            lineNumber: this.lineNumber,
            message: text,
            number: 0,
            procedure: this.attributionName,
            server: this.batch.Connection.DataSource,
            source: "SqlServerSimulator",
            state: 2)));

        private void Start((string Name, SqlDbType Type, long MaxLength, byte Precision, byte Scale)[] columns)
        {
            using var culture = CultureScope.Engine();
            var collation = this.batch.CurrentDatabase.Collation;
            this.schema = new SqlType[columns.Length];
            this.names = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                this.schema[i] = ClrTypeMarshaller.ColumnType(columns[i].Type, columns[i].MaxLength, columns[i].Precision, columns[i].Scale, collation);
                this.names[i] = columns[i].Name;
            }

            this.rows = [];
        }

        private void Row(object?[] values)
        {
            using var culture = CultureScope.Engine();
            var row = new SqlValue[this.schema!.Length];
            for (var i = 0; i < row.Length; i++)
                row[i] = ClrTypeMarshaller.FromRecordValue(values[i], this.schema[i]);
            this.rows!.Add(row);
        }

        private void End()
        {
            this.outcomes.Add(new SimulatedSqlResultSet(this.schema!, this.names!, this.rows!));
            this.schema = null;
            this.names = null;
            this.rows = null;
        }

        /// <summary>
        /// Settles a result set the procedure started and never ended: a
        /// procedure that returns sends it, rows and all, and one that throws
        /// before sending a row sends no result set at all, and one that throws
        /// after sending rows keeps them, the error cutting the result set
        /// short (all probed 2026-09-28 against SQL Server 2025).
        /// </summary>
        public void Settle(bool completed)
        {
            if (this.rows is null)
                return;
            if (completed || this.rows.Count > 0)
            {
                var cutShort = !completed;
                this.End();
                if (cutShort)
                    ((SimulatedSqlResultSet)this.outcomes[^1]).EndedByError = true;
            }
            else
            {
                this.rows = null;
            }
        }
    }
}
