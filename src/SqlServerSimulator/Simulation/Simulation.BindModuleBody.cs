using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Binds a module body at <c>CREATE</c> / <c>ALTER</c> time: the body is
    /// re-tokenized on a throwaway child <see cref="BatchContext"/> that runs
    /// in skip mode (<see cref="BatchContext.SkipModeFlag"/>) with
    /// <see cref="BatchContext.CreateTimeBinding"/> set, so every statement
    /// parses and resolves its names but nothing executes. A binder error the
    /// pass raises propagates out of the <c>CREATE</c>, which leaves the module
    /// uncreated (and an <c>ALTER</c>'s previous body in place, since every
    /// caller binds before it touches the schema dict).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What defers</strong> is exactly what skip mode already defers:
    /// a FROM-clause table or schema-qualified function that doesn't resolve
    /// becomes a placeholder source (<c>FromSource.DeferredPlaceholder</c>) and
    /// makes the whole statement's column binding lenient, and a missing DML
    /// target / DROP target raises Msg 208, which the dispatch loop swallows.
    /// That mirrors real SQL Server's deferred name resolution: probe-confirmed
    /// (SQL Server 2025, 2026-08-01) that a procedure body naming a missing
    /// table, a missing column on a missing table, a missing table-valued
    /// function, a missing INSERT / MERGE / DROP target, a <c>#temp</c> that
    /// doesn't exist yet, a missing database, an <c>EXEC</c> of a missing
    /// procedure, or a not-yet-created scalar UDF (which is what makes a
    /// recursive UDF legal) all create successfully.
    /// </para>
    /// <para>
    /// <strong>What binds</strong> is everything else the parser checks — the
    /// probed set includes Msg 207 (invalid column on an existing table),
    /// 8116 (legacy LOB as a string-scalar argument), 10700 (writing a READONLY
    /// TVP), 8120 (GROUP BY containment), 147, 174, 156 / 102, 137, 134, 135,
    /// 209, 306, 110, 108, 402, 8144 and 178.
    /// </para>
    /// <para>
    /// <strong>Errors that don't abort the CREATE</strong>: a
    /// <see cref="NotSupportedException"/> names a feature the simulator hasn't
    /// built rather than something real's binder rejects, so raising it at
    /// CREATE would refuse a module real accepts — it is swallowed here and
    /// surfaces at invocation as before. A swallowed deferred-name error
    /// abandons the rest of the pass for the reason recorded on
    /// <see cref="BatchContext.CreateTimeBinding"/>.
    /// </para>
    /// </remarks>
    /// <param name="outerContext">The <c>CREATE</c> statement's own parser context.</param>
    /// <param name="bodyText">The captured body source; an empty body binds to nothing.</param>
    /// <param name="moduleName">
    /// Unqualified module name. Real attributes a bind error to the bare leaf
    /// even for a schema-qualified module (probe-confirmed: <c>Procedure p8a</c>,
    /// not <c>dbo.p8a</c>) — the opposite of the schema-qualified attribution an
    /// invocation-time body error carries for a procedure.
    /// </param>
    /// <param name="bodyLineOffset">
    /// Newlines between the <c>CREATE</c> statement's first character and the
    /// body's start. Msg 111 forces the <c>CREATE</c> to open its batch, so a
    /// statement-relative line is also the batch-relative line real reports.
    /// </param>
    /// <param name="buildBindBatch">
    /// Builds the child batch over the synthesized body command — one of the
    /// per-kind <see cref="BatchContext"/> constructors, so the body sees the
    /// same frame (and therefore the same <c>RETURN</c> / <c>INSERTED</c> /
    /// TVP rules) it will see when it runs.
    /// </param>
    private void BindModuleBodyAtCreate(
        ParserContext outerContext,
        string bodyText,
        string moduleName,
        int bodyLineOffset,
        Func<SimulatedDbCommand, BatchContext> buildBindBatch)
    {
        if (string.IsNullOrEmpty(bodyText))
            return;

        var connection = outerContext.Batch.Connection;
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // bodyText is the simulator's own captured body span
        bodyCommand.CommandText = bodyText;
#pragma warning restore CA2100

        var bindBatch = buildBindBatch(bodyCommand);
        bindBatch.SkipModeFlag = true;
        bindBatch.CreateTimeBinding = true;
        bindBatch.LineOffset = bodyLineOffset;
        bindBatch.ErrorProcedureName = moduleName;
        // A FROM-less projection over GETDATE() / SYSDATETIME() evaluates its
        // static type against the statement freeze; adopt the CREATE's own so
        // the bind reads a live instant rather than year 1.
        bindBatch.AdoptStatementFreezeFrom(outerContext.Batch);

        // Nesting counts while the bind walks the body so a body that calls
        // back into module parsing can't recurse without bound.
        connection.NestingLevel++;
        try
        {
            var parser = bindBatch.Parser;
            parser.MoveNextOptional();
            foreach (var _ in DispatchStatementsUntil(bindBatch, endKeyword: null))
            {
                // Skip mode yields no outcomes; the enumeration exists only to
                // drive the parse.
            }
        }
        catch (NotSupportedException)
        {
            // An unmodeled feature in the body is a simulator gap, not real's
            // binder speaking. Keep the module; the gap surfaces at invocation.
        }
        finally
        {
            connection.NestingLevel--;
        }
    }

    /// <summary>
    /// Binds a stored-procedure body at CREATE time. Parameters seed the bind
    /// batch exactly as an invocation does — scalar parameters as typed NULL
    /// variable slots, a table-valued parameter as an empty READONLY clone of
    /// its type (which is what lets a body writing to it raise Msg 10700), and
    /// a cursor parameter as an unallocated cursor variable.
    /// </summary>
    private void BindProcedureBodyAtCreate(
        ParserContext outerContext,
        string procedureName,
        IReadOnlyList<ProcedureParameter> parameters,
        string bodyText,
        int bodyLineOffset)
    {
        var outerBatch = outerContext.Batch;
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var tableVariables = new Dictionary<string, HeapTable>(BatchContext.VariableNameComparer);
        foreach (var param in parameters)
        {
            if (param.IsCursor)
                continue;
            if (param.TableType is { } tvpType)
            {
                tableVariables[param.Name] = tvpType.Clone("@" + param.Name, outerBatch, isTableValuedParameter: true);
                continue;
            }
            variables[param.Name] = new VariableSlot(
                param.Type, param.DeclaredMaxLength, SqlValue.Null(param.Type), parameter: null);
        }

        BindModuleBodyAtCreate(outerContext, bodyText, procedureName, bodyLineOffset, bodyCommand =>
        {
            var batch = new BatchContext(bodyCommand, variables, new ProcFrame(procedureName), tableVariables);
            foreach (var param in parameters)
            {
                if (param.IsCursor)
                    batch.CursorVariables[param.Name] = null;
            }
            return batch;
        });
    }

    /// <summary>
    /// Binds a scalar-UDF body at CREATE time. The <see cref="UdfFrame"/> is
    /// what makes value-form <c>RETURN</c> legal inside the body, so the bind
    /// must carry one for the same reason the invocation does.
    /// </summary>
    private void BindScalarFunctionBodyAtCreate(
        ParserContext outerContext,
        string functionName,
        IReadOnlyList<UdfParameter> parameters,
        SqlType returnType,
        string bodyText,
        int bodyLineOffset)
    {
        var variables = SeedFunctionParameters(parameters);
        BindModuleBodyAtCreate(outerContext, bodyText, functionName, bodyLineOffset,
            bodyCommand => new BatchContext(bodyCommand, variables, new UdfFrame(returnType)));
    }

    /// <summary>
    /// Binds a multi-statement-TVF body at CREATE time. The return table seeds
    /// the bind batch's table variables so <c>INSERT INTO @r</c> resolves, and
    /// the batch deliberately carries no frame — that absence is what raises
    /// Msg 178 on a value-form <c>RETURN</c>, which real also reports at CREATE.
    /// </summary>
    private void BindMultiStatementTvfBodyAtCreate(
        ParserContext outerContext,
        string functionName,
        IReadOnlyList<UdfParameter> parameters,
        string returnVariableName,
        HeapColumn[] outputColumns,
        KeyConstraint[] keyConstraints,
        CheckConstraint[] checkConstraints,
        string bodyText,
        int bodyLineOffset)
    {
        var outerBatch = outerContext.Batch;
        var variables = SeedFunctionParameters(parameters);
        var returnTable = new HeapTable(
            "@" + returnVariableName,
            outputColumns,
            outerBatch.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: outerBatch.CurrentStatement.UtcNow,
            keyConstraints: keyConstraints,
            checkConstraints: checkConstraints,
            isTableVariable: true);

        BindModuleBodyAtCreate(outerContext, bodyText, functionName, bodyLineOffset, bodyCommand =>
        {
            var batch = new BatchContext(bodyCommand, variables);
            batch.TableVariables[returnVariableName] = returnTable;
            return batch;
        });
    }

    /// <summary>
    /// Binds a trigger body at CREATE time against <paramref name="frame"/>'s
    /// empty <c>INSERTED</c> / <c>DELETED</c> pseudo-tables, so a bad column on
    /// either one reports Msg 207 the way real does.
    /// </summary>
    private void BindTriggerBodyAtCreate(
        ParserContext outerContext,
        string triggerName,
        TriggerFrame frame,
        string bodyText,
        int bodyLineOffset)
        => BindModuleBodyAtCreate(outerContext, bodyText, triggerName, bodyLineOffset,
            bodyCommand => new BatchContext(bodyCommand, frame));

    /// <summary>
    /// Seeds a function's declared parameters as typed NULL variable slots, so
    /// a body reference to <c>@p</c> binds instead of raising Msg 137.
    /// </summary>
    private static Dictionary<string, VariableSlot> SeedFunctionParameters(IReadOnlyList<UdfParameter> parameters)
    {
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        foreach (var param in parameters)
            variables[param.Name] = new VariableSlot(param.Type, declaredMaxLength: null, SqlValue.Null(param.Type), parameter: null);
        return variables;
    }
}
