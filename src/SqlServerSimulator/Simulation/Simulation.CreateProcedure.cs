using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Counts newline characters in <paramref name="text"/> over the
    /// half-open range <c>[<paramref name="start"/>, <paramref name="end"/>)</c>
    /// — the number of lines a body's start sits below the first line of the
    /// batch that created it, which <see cref="Schemas.Procedure.BodyLineOffset"/>
    /// adds to body error lines. Real numbers a module's lines from its batch,
    /// so blank lines and comments ahead of the <c>CREATE</c> count
    /// (probe-confirmed against SQL Server 2025, 2026-09-23).
    /// CR is folded into its following LF (CRLF and LF count identically),
    /// matching <see cref="Parser.Token.LineNumber"/>.
    /// </summary>
    private static int CountNewlines(string text, int start, int end)
    {
        var count = 0;
        for (var i = start; i < end; i++)
        {
            if (text[i] == '\n')
                count++;
        }

        return count;
    }

    /// <summary>
    /// Parses <c>CREATE [OR ALTER] PROCEDURE schema.name [(@p1 type [=
    /// default] [OUTPUT], ...)] [WITH options] AS body</c>. The body source
    /// is captured between the <c>AS</c> keyword (exclusive) and the
    /// trailing statement boundary, then re-tokenized per call inside a
    /// child <see cref="BatchContext"/> with parameters seeded as variables.
    /// Stored in the target <see cref="Schema.Procedures"/> dict keyed by
    /// name. Probed against SQL Server 2025 (2026-05-12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Body capture</strong>: unlike scalar UDFs (which require an
    /// outer <c>BEGIN ... END</c> wrapping), procedures accept either form.
    /// The body span runs from the first token after <c>AS</c> to end-of-
    /// batch. Empty bodies are legal — <c>CREATE PROC p AS</c> with nothing
    /// after <c>AS</c> succeeds and produces no result sets when invoked
    /// (probe-confirmed). Parens around the parameter list are also optional
    /// (<c>CREATE PROC p (@x int) AS</c> equivalent to <c>CREATE PROC p @x
    /// int AS</c>).
    /// </para>
    /// <para>
    /// <strong>OR ALTER</strong>: the modern <c>CREATE OR ALTER PROCEDURE</c>
    /// syntax does an upsert — creates when missing, replaces when present
    /// (preserving the <see cref="SchemaObject.ObjectId"/> and
    /// <see cref="SchemaObject.CreateDate"/>, advancing
    /// <see cref="SchemaObject.ModifyDate"/>). Pure
    /// <c>CREATE PROCEDURE</c> on an existing name raises Msg 2714; pure
    /// <c>ALTER PROCEDURE</c> on a missing name raises Msg 208, and on a name
    /// another object kind holds, Msg 2010.
    /// </para>
    /// <para>
    /// <strong>Fidelity gaps</strong>: <c>FOR REPLICATION</c> parses and is
    /// silently ignored. <c>WITH RECOMPILE</c> compiles the body on every
    /// call, which is what sends its scalar inlining failures each time (see
    /// <see cref="ModulePlan"/>).
    /// </para>
    /// </remarks>
    private static bool TryParseCreateProcedure(ParserContext context, bool isAlter, bool createOrAlter)
    {
        if (context.Batch.BlockDepth > 0 || context.Batch.HasDispatchedStatement)
        {
            // Real attributes the refusal to the procedure it would have
            // created, by its bare name (probed 2026-09-26 against SQL
            // Server 2025).
            var misplaced = SimulatedSqlException.MustBeFirstStatementInBatch("CREATE/ALTER PROCEDURE");
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextRequired();
            if (context.Token is Name)
                misplaced.Errors[0].Procedure = BatchContext.ParseObjectName(context).Leaf;
            context.RestoreCheckpoint(checkpoint);
            throw misplaced;
        }

        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var procName = BatchContext.ParseObjectName(context);
        // Every error from here on names the procedure — its name collision and
        // its parameter list's included (probed 2026-09-26 against SQL Server
        // 2025). The statement is its batch's only one, so nothing after it
        // inherits this.
        context.Batch.ErrorProcedureName = procName.Leaf;
        RejectQualifiedModuleName(procName, "PROCEDURE");
        var schema = ResolveModuleSchema(context, procName, isAlter);

        context.MoveNextRequired();
        var groupNumber = ParseProcedureGroupNumber(context, procName.Leaf);

        // Optional parenthesized parameter list. Inside or outside parens,
        // parameter parsing is identical — the only difference is the
        // terminator (`)` vs the WITH/AS keyword).
        var openParen = context.Token is Operator { Character: '(' };
        if (openParen)
            context.MoveNextRequired();

        var parameters = new List<ProcedureParameter>();
        var declarationErrors = new List<SimulatedSqlException>();
        while (true)
        {
            if (openParen && context.Token is Operator { Character: ')' })
            {
                context.MoveNextRequired();
                break;
            }
            // Without parens, the WITH / AS keyword (or end-of-stream) ends
            // the parameter list. WITH is reserved; AS is reserved; parser
            // sees them as ReservedKeyword tokens.
            if (!openParen && context.Token is ReservedKeyword { Keyword: Keyword.With or Keyword.As })
                break;

            var parameter = ParseProcedureParameter(context, parameters.Count + 1, declarationErrors);
            if (parameters.Exists(declared => BatchContext.VariableNameComparer.Equals(declared.Name, parameter.Name)))
                throw SimulatedSqlException.VariableAlreadyDeclared(parameter.Name);
            parameters.Add(parameter);

            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            if (openParen && context.Token is Operator { Character: ')' })
            {
                context.MoveNextRequired();
                break;
            }
            if (!openParen && context.Token is ReservedKeyword { Keyword: Keyword.With or Keyword.As })
                break;
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // Optional WITH option-list before AS: EXECUTE AS is captured and
        // applied as an impersonation frame around the body at invocation, and
        // NATIVE_COMPILATION admits a BEGIN ATOMIC body, which runs as the
        // regular BEGIN…END flow.
        var options = ParseModuleOptions(context, ModuleOptionHost.Procedure, procName.Leaf);
        var executeAsClause = options.ExecuteAs;
        RequireExecuteAsUser(context, executeAsClause);
        var nativelyCompiled = options.NativeCompilation;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Capture body source from the first token after AS to end-of-batch
        // (or whatever the outer dispatch considers the statement boundary).
        var commandText = context.Command.CommandText;
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.External })
            return ParseClrProcedureTail(context, schema, procName, groupNumber, parameters, declarationErrors, executeAsClause, options.RefusedByExternalModule, isAlter, createOrAlter);

        // Empty body is legal — `CREATE PROC p AS` with nothing after AS
        // succeeds in real SQL Server. The body capture below produces an
        // empty string, which the per-call invocation handles cleanly.
        // A natively compiled procedure's body is one BEGIN ATOMIC block
        // (Msg 10783, probed 2026-10-02 against SQL Server 2025).
        if (nativelyCompiled && !IsAtomicBlockAhead(context))
            throw SimulatedSqlException.NativeModuleBodyNotAtomic();
        var bodyStart = context.Token?.StartIndex ?? commandText.Length;
        var bodyEnd = commandText.Length;
        while (context.Token is not null)
        {
            bodyEnd = context.Token.EndIndex;
            context.MoveNextOptional();
        }
        var bodyText = commandText[bodyStart..bodyEnd];

        if (context.Batch.IsSkipping)
            return true;

        // DDL gate: db-scope CREATE PROCEDURE + schema ALTER when the statement
        // creates (Msg 262 state 18 / Msg 2760), object ALTER when it replaces
        // an existing proc (Msg 3701 state 20).
        CheckModuleDdlPermission(
            context, "CREATE PROCEDURE", procName, schema, isAlter, createOrAlter,
            schema.Procedures.GetValueOrDefault(procName.Leaf));

        // Newlines before the body start, so body errors — at bind time below
        // and per call later — report a line relative to the whole CREATE
        // statement (probe-confirmed).
        var bodyLineOffset = CountNewlines(commandText, 0, bodyStart);

        // Bind the body before anything touches the schema dict, so a binder
        // error leaves the procedure uncreated and an ALTER's previous body
        // standing. Ahead of the name-collision gates too: probe-confirmed that
        // real reports a body error rather than Msg 2714 for a plain CREATE over
        // an existing name, and rather than Msg 208 for a bare ALTER of a name
        // that doesn't exist.
        BindBehindDeclarationErrors(HeldDeclarationErrors(declarationErrors), () => context.Simulation.BindProcedureBodyAtCreate(context, procName.Leaf, parameters, bodyText, bodyLineOffset, nativelyCompiled));
        // A natively compiled procedure is schema-bound, so its names are
        // two-part (Msg 4512, probed 2026-10-02 against SQL Server 2025).
        if (nativelyCompiled)
            SchemaBinding.EnforceBody(context.CurrentDatabase, "procedure", $"{schema.Name}.{procName.Leaf}", bodyText);

        if (groupNumber > 1)
        {
            StoreNumberedProcedure(context, schema, procName, groupNumber, isAlter, createOrAlter, new Procedure(
                schema, procName.Leaf, 0, [.. parameters], bodyText, context.Batch.CurrentStatement.UtcNow, bodyLineOffset)
            {
                DefinitionText = options.Encryption ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
                ExecuteAsClause = executeAsClause,
                ExecuteAsPrincipalId = ResolveExecuteAsPrincipalId(context, executeAsClause),
                UsesQuotedIdentifier = context.QuotedIdentifiers,
                UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
                GroupNumber = groupNumber,
                RecompilesEveryCall = options.Recompile,
                IsNativelyCompiled = nativelyCompiled,
            });
            return true;
        }

        // CREATE-only (no OR ALTER) collides with any existing object of the
        // same name (procs share the namespace with tables / views /
        // functions); either ALTER leg over a name another kind holds is Msg
        // 2010, and bare ALTER on a free name is Msg 208. The helper also
        // takes Sch-M on the instance being replaced, so a concurrent EXEC
        // (holding Sch-S via TryResolveProcedure) blocks the swap.
        var replaced = (Procedure?)ResolveModuleAlterTarget(
            context, schema, procName, isAlter, createOrAlter,
            schema.Procedures.TryGetValue(procName.Leaf, out var existing) ? existing : null);
        // A T-SQL body can't replace a CLR procedure (probed 2026-09-28
        // against SQL Server 2025); the reverse is the CLR tail's Msg 6530.
        if (replaced is { ClrEntry: not null })
            throw SimulatedSqlException.CannotAlterIncompatibleObjectType(procName);

        var procedure = new Procedure(
            schema,
            procName.Leaf,
            // ALTER preserves the existing object_id (probe-confirmed). CREATE
            // and CREATE OR ALTER (on a missing name) allocate a fresh id.
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            bodyText,
            createDate: replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow,
            bodyLineOffset: bodyLineOffset)
        {
            DefinitionText = options.Encryption ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
            ExecuteAsClause = executeAsClause,
            ExecuteAsPrincipalId = ResolveExecuteAsPrincipalId(context, executeAsClause),
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            // The group's numbered procedures stay with it across an ALTER.
            Numbered = replaced?.Numbered,
            RecompilesEveryCall = options.Recompile,
            IsNativelyCompiled = nativelyCompiled,
        };
        if (replaced is not null)
            procedure.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        schema.Procedures[procName.Leaf] = procedure;
        RecordSlotUndo(context, schema.Procedures, procName.Leaf, replaced);
        if (replaced is not null)
            RebindExtendedProperties(context.Batch, replaced, procedure);
        RecordDdlEvent(context, replaced is null ? "CREATE_PROCEDURE" : "ALTER_PROCEDURE", schema.Name, procName.Leaf, "PROCEDURE");
        return true;
    }

    /// <summary>
    /// Reads a numbered procedure's <c>;N</c> after its name, answering 1 when
    /// there is none. Cursor on entry: the token after the name; on exit, the
    /// token after the number. A number outside 1 to 32767 is Msg 1005, whose
    /// text carries its line (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static short ParseProcedureGroupNumber(ParserContext context, string procedureName)
    {
        if (context.Token is not Operator { Character: ';' })
            return 1;
        if (context.GetNextRequired() is not Numeric number)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var written = context.Command.CommandText[number.StartIndex..number.EndIndex];
        if (!short.TryParse(written, out var groupNumber) || groupNumber < 1)
        {
            var error = SimulatedSqlException.InvalidProcedureNumber(number.LineNumber + context.Batch.LineOffset, written);
            error.Errors[0].Procedure = procedureName;
            throw error;
        }
        context.MoveNextRequired();
        return groupNumber;
    }

    /// <summary>
    /// Stores <paramref name="numbered"/> as number
    /// <see cref="Procedure.GroupNumber"/> of the group <paramref name="name"/>
    /// names, under its object id. Creating one needs the group's number-1
    /// procedure (Msg 2730) and a number not yet taken (Msg 2004); altering one
    /// needs it to exist (Msg 208, state 7) — all naming the group as their
    /// procedure (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static void StoreNumberedProcedure(
        ParserContext context, Schema schema, MultiPartName name, short groupNumber, bool isAlter, bool createOrAlter, Procedure numbered)
    {
        var leaf = name.Leaf;
        var group = schema.Procedures.GetValueOrDefault(leaf);
        var existing = group?.Numbered?.GetValueOrDefault(groupNumber);
        SimulatedSqlException? refusal = null;
        if (group is null || (isAlter && existing is null))
        {
            refusal = isAlter
                ? SimulatedSqlException.InvalidObjectName(name, state: 7)
                : SimulatedSqlException.NumberedProcedureWithoutGroupOne(leaf, groupNumber);
        }
        else if (existing is not null && !isAlter && !createOrAlter)
        {
            refusal = SimulatedSqlException.ProcedureGroupNumberTaken(leaf, groupNumber);
        }
        if (refusal is not null)
        {
            refusal.Errors[0].Procedure = leaf;
            throw refusal;
        }

        var stored = new Procedure(schema, leaf, group!.ObjectId, numbered.Parameters, numbered.BodyText, existing?.CreateDate ?? numbered.CreateDate, numbered.BodyLineOffset)
        {
            DefinitionText = numbered.DefinitionText,
            ExecuteAsClause = numbered.ExecuteAsClause,
            ExecuteAsPrincipalId = numbered.ExecuteAsPrincipalId,
            UsesQuotedIdentifier = numbered.UsesQuotedIdentifier,
            UsesAnsiNulls = numbered.UsesAnsiNulls,
            GroupNumber = groupNumber,
        };
        if (existing is not null)
            stored.ModifyDate = numbered.CreateDate;
        var hadGroup = group.Numbered is not null;
        (group.Numbered ??= [])[groupNumber] = stored;
        RecordDdlUndo(context, () =>
        {
            if (existing is not null)
                group.Numbered![groupNumber] = existing;
            else if (hadGroup)
                _ = group.Numbered!.Remove(groupNumber);
            else
                group.Numbered = null;
        });
        RecordDdlEvent(context, existing is null ? "CREATE_PROCEDURE" : "ALTER_PROCEDURE", schema.Name, leaf, "PROCEDURE");
    }

    /// <summary>
    /// Parses one entry in a <c>CREATE PROCEDURE</c> parameter list:
    /// <c>@name type [= default] [OUTPUT]</c>. Cursor on entry: the leading
    /// <c>@</c> or parameter name token. Cursor on exit: the trailing
    /// separator (<c>,</c>, <c>)</c>, or the <c>WITH</c>/<c>AS</c> keyword).
    /// </summary>
    private static ProcedureParameter ParseProcedureParameter(ParserContext context, int ordinal, List<SimulatedSqlException> declarationErrors)
    {
        if (context.Token is not AtPrefixedString variable)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = variable.Value;
        context.MoveNextRequired();

        // Cursor parameter: `@c CURSOR VARYING OUTPUT`, the two options
        // required and in that order (Msg 1051 otherwise; probed 2026-09-29
        // against SQL Server 2025).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Cursor })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Varying })
                throw SimulatedSqlException.CursorParameterNeedsVaryingOutput();
            if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
                throw SimulatedSqlException.CursorParameterNeedsVaryingOutput();
            context.MoveNextRequired();
            return new ProcedureParameter(name, SqlType.Int32, declaredMaxLength: null, defaultExpression: null, isOutput: true, isCursor: true);
        }

        // Try table-valued-parameter binding first. A multi-part name (e.g.
        // `dbo.MyType`) unambiguously means user-defined type; a 1-part name
        // checks TableTypes first with fallback to the scalar parser.
        var tableType = TryResolveProcedureTableTypeParameter(context);
        if (tableType is not null)
        {
            // READONLY is mandatory after a TVP parameter (probe-confirmed:
            // Msg 352 if missing). DEFAULT / OUTPUT shapes raise Msg 102
            // because the grammar after a TVP-type parameter only permits
            // READONLY (probe-confirmed wording: "Incorrect syntax near
            // '=' / 'output'").
            if (context.Token is Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.ReadOnly })
                throw SimulatedSqlException.TableValuedParameterMustBeReadOnly("@" + name);
            context.MoveNextRequired();
            // After READONLY no further trailers are accepted (no = default,
            // no OUTPUT).
            return new ProcedureParameter(name, SqlType.Int32, declaredMaxLength: null, defaultExpression: null, isOutput: false, tableType: tableType);
        }

        var spelledNumeric = IsNumericTypeWord(context.Token);
        SqlType paramType;
        int? declaredMaxLength;
        AliasType? aliasType;
        var typeResolved = true;
        try
        {
            (paramType, declaredMaxLength, aliasType) = ParseProcedureParameterType(context, ordinal, "@" + name);
        }
        catch (SimulatedSqlException error) when (error.Number == 2715)
        {
            // Held with the rest of the list's declaration errors — see
            // HeldDeclarationErrors.
            declarationErrors.Add(error);
            (paramType, declaredMaxLength, aliasType) = (SqlType.Int32, null, null);
            typeResolved = false;
        }
        spelledNumeric = aliasType?.SpelledNumeric ?? spelledNumeric;

        Expression? defaultExpression = null;
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            defaultExpression = ParseParameterDefault(context);
            if (typeResolved)
                NoteUnassignableDefault(context.Batch, defaultExpression, paramType, declarationErrors);
        }
        var readOnly = NoteReadOnlyScalarParameter(context, variable, declarationErrors);

        // `OUTPUT` (with the synonym `OUT`) marks the parameter as a writeback
        // slot. Real SQL Server treats `OUT` and `OUTPUT` as equivalent; both
        // surface as ContextualKeyword on the tokenizer side (neither is
        // reserved).
        var isOutput = false;
        if (!readOnly && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
        {
            isOutput = true;
            context.MoveNextRequired();
        }

        return new ProcedureParameter(name, paramType, declaredMaxLength, defaultExpression, isOutput) { SpelledNumeric = spelledNumeric, AliasType = aliasType };
    }

    /// <summary>
    /// A procedure or function parameter's default, which is a constant and
    /// nothing more (probed 2026-10-02 against SQL Server 2025): a literal, a
    /// number with an optional leading <c>-</c> (a <c>+</c> is Msg 102),
    /// <c>NULL</c> or <c>DEFAULT</c> (both NULL), an <c>@@</c> function, or a
    /// bare or bracketed name, which is the <c>nvarchar</c> string of its
    /// text. What follows is the declaration's own business, so <c>1 + 1</c>
    /// and <c>GETDATE()</c> are Msg 102 at the <c>+</c> and the <c>(</c>.
    /// </summary>
    private static Expression ParseParameterDefault(ParserContext context)
    {
        Expression value;
        switch (context.Token)
        {
            case DoubleAtPrefixedString:
                return Expression.Parse(context);
            case ReservedKeyword { Keyword: Keyword.Null or Keyword.Default }:
                value = new Parser.Expressions.Value();
                break;
            case Literal literal:
                value = new Parser.Expressions.Value(literal.Value);
                break;
            case Numeric number:
                value = new Parser.Expressions.Value(number.Value);
                break;
            case Operator { Character: '-' }:
                if (context.GetNextRequired() is not Numeric negated)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                value = new Parser.Expressions.Value(NegateLiteral(negated.Value));
                break;
            case Name name:
                value = new Parser.Expressions.Value(SqlValue.FromNVarchar(NVarcharSqlType.Get(Math.Max(name.Value.Length, 1), context.CurrentDatabase.Collation, Coercibility.CoercibleDefault), name.Value));
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return value;
    }

    /// <summary>
    /// Probes the cursor for a user-defined table type reference. Returns
    /// the matched <see cref="TableType"/> with the cursor advanced past the
    /// type name; returns null (cursor unchanged) for any other shape so the
    /// caller falls through to the scalar parameter-type parser.
    /// </summary>
    private static TableType? TryResolveProcedureTableTypeParameter(ParserContext context)
    {
        if (context.Token is not Name firstName)
            return null;

        // Multi-part detection: peek for `.` without permanently advancing.
        var checkpoint = context.SaveCheckpoint();
        var sawDot = context.MoveNext() && context.Token is Operator { Character: '.' };
        context.RestoreCheckpoint(checkpoint);

        if (sawDot)
        {
            // Multi-part name: could be a user-defined table type OR a
            // schema-qualified alias type (UDDT). Try table types first;
            // on miss, restore the checkpoint so the scalar parameter-type
            // parser can resolve the alias.
            var beforeParse = context.SaveCheckpoint();
            var objectName = BatchContext.ParseObjectName(context);
            if (context.Batch.TryResolveTableType(objectName, out var tableType))
            {
                context.MoveNextOptional();
                return tableType;
            }
            context.RestoreCheckpoint(beforeParse);
            return null;
        }

        // 1-part: try TableTypes, fall through to scalar on miss.
        if (!context.Batch.TryResolveTableType(new MultiPartName(firstName.Value), out var singleType))
            return null;
        context.MoveNextOptional();
        return singleType;
    }

    /// <summary>
    /// Parses a procedure parameter type. Same grammar as <c>CREATE
    /// FUNCTION</c>'s parameter type (a type name with optional <c>(N)</c>
    /// or <c>(N, S)</c> or <c>(MAX)</c>) — returns both the resolved
    /// <see cref="SqlType"/> and the declared length (passed through to
    /// <see cref="ProcedureParameter.DeclaredMaxLength"/> for catalog-view
    /// surfaces).
    /// </summary>
    private static (SqlType Type, int? DeclaredMaxLength, AliasType? Alias) ParseProcedureParameterType(ParserContext context, int ordinal, string parameterName)
    {
        var (qualifiedTypeName, typeName) = TypeNameSynonyms.ReadTypeName(context);
        context.MoveNextRequired();

        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    _ = context.GetNextRequired();
                    declaredScale = TypeNameSynonyms.ReadSecondTypeArgument(context, typeName);
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }

        var (resolvedType, resolvedMaxLength, _, alias) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: ordinal, TypeSpecSite.Scalar, columnName: parameterName);
        return (resolvedType, resolvedMaxLength, alias);
    }

    /// <summary>
    /// Whether the cursor sits on <c>BEGIN ATOMIC</c>, leaving it there.
    /// </summary>
    private static bool IsAtomicBlockAhead(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Begin })
            return false;
        var checkpoint = context.SaveCheckpoint();
        var atomic = context.GetNextOptional() is UnquotedString { ContextualKeyword: ContextualKeyword.Atomic };
        context.RestoreCheckpoint(checkpoint);
        return atomic;
    }
}
