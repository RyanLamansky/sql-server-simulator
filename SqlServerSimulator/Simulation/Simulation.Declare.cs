using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DECLARE @v TYPE [= expr] [, @w TYPE [= expr] ...]</c>.
    /// Variables register on <see cref="BatchContext.Variables"/> with their
    /// declared type and (optional) initializer-evaluated value, defaulting
    /// to typed NULL. Re-declaring an existing name (including a name
    /// occupied by a SqlClient parameter) raises Msg 134.
    /// </summary>
    /// <remarks>
    /// On entry the cursor is on the <c>DECLARE</c> keyword. On return the
    /// cursor sits on the first token after the last declaration — typically
    /// a <c>;</c>, the next statement keyword, or end of batch.
    /// </remarks>
    private static int? TryParseDeclare(ParserContext context)
    {
        var rowsAffected = (int?)null;
        var sawScalar = false;
        var variableIndex = 0;

        do
        {
            variableIndex++;
            if (context.GetNextRequired() is not AtPrefixedString variableToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var variableName = variableToken.Value;
            // Variable declarations are compile-scoped batch-wide: a DECLARE
            // inside an un-taken IF branch still registers its slot, and a
            // duplicate name raises Msg 134 even when either declaration
            // sits in a dead branch (probe-confirmed against SQL Server
            // 2025). Only the initializer is execution-scoped.
            if (context.Batch.Variables.ContainsKey(variableName)
                || context.Batch.TableVariables.ContainsKey(variableName))
            {
                throw SimulatedSqlException.VariableAlreadyDeclared(variableName);
            }

            // Optional AS keyword between name and type spec — `DECLARE @v AS INT`.
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.As })
                context.MoveNextRequired();

            switch (context.Token)
            {
                // Cursor variables (`DECLARE @c CURSOR`) aren't modeled — named
                // cursors only.
                case ReservedKeyword { Keyword: Keyword.Cursor }:
                    throw new NotSupportedException("Cursor variables (DECLARE @c CURSOR / cursor-typed parameters) aren't modeled; use a named cursor.");

                // Table variable form: `DECLARE @t TABLE (cols)`. Only one
                // table-variable declaration per statement (probe-confirmed:
                // `DECLARE @t1 TABLE (...), @t2 TABLE (...)` raises Msg 102, and
                // mixing scalar + table in one DECLARE raises Msg 156). The
                // table form must be the only declaration in the statement —
                // a leading scalar (sawScalar = true) means we already passed
                // a `,`, so reject. A trailing `,` after the column list also
                // raises Msg 102.
                case ReservedKeyword { Keyword: Keyword.Table }:
                    if (sawScalar)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    ParseDeclareTableVariable(context, variableName);
                    return context.Token is Operator { Character: ',' }
                        ? throw SimulatedSqlException.SyntaxErrorNear(context)
                        : null;
            }

            // User-defined table type form: `DECLARE @t [schema.]MyType`.
            // Distinct from inline TABLE — multi-variable DECLARE is allowed
            // (probe-confirmed: `DECLARE @t1 dbo.MyType, @t2 dbo.MyType`
            // works, unlike the inline TABLE form which raises Msg 102).
            // A multi-part name unambiguously means user-defined type (built-
            // in scalars are 1-part only); for 1-part names, table-type
            // lookup runs first with fallback to the scalar path.
            if (context.Token is Name firstNameToken && TryParseDeclareTableTypeVariable(context, firstNameToken, variableName, variableIndex))
            {
                sawScalar = true;
                continue;
            }

            var (declaredType, declaredMaxLength) = ParseDeclareTypeSpec(context, variableName);

            // Optional initializer.
            var initialValue = SqlValue.Null(declaredType);
            var hasInitializer = context.Token is Operator { Character: '=' };
            if (hasInitializer)
            {
                context.MoveNextRequired();
                var initExpression = Expression.Parse(context);
                if (!context.Batch.IsSkipping)
                {
                    initialValue = Parser.Expressions.Cast.ApplyCoercion(initExpression.Run(new RuntimeContext(NoColumnResolver, context.Batch)), declaredType, declaredMaxLength);
                    rowsAffected = 1; // initializer counts as one row for @@ROWCOUNT (probe-confirmed)
                }
            }

            context.Batch.Variables[variableName] = new VariableSlot(declaredType, declaredMaxLength, initialValue, parameter: null);
            sawScalar = true;
        } while (context.Token is Operator { Character: ',' });

        return rowsAffected;
    }

    /// <summary>
    /// Parses a SqlType reference following a variable name in <c>DECLARE</c>:
    /// a type-name token plus optional <c>(N)</c> / <c>(p, s)</c> spec,
    /// resolving via <see cref="SqlType.GetByName"/>. On entry the cursor is
    /// on the type-name token; on return it sits one past the type spec.
    /// Length/scale information beyond the SqlType (e.g. <c>varchar(N)</c>'s
    /// max-length) is captured by length-bearing singleton variants of the
    /// type itself when applicable.
    /// </summary>
    private static (SqlType Type, int? MaxLength) ParseDeclareTypeSpec(ParserContext context, string variableName)
    {
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var qualifiedTypeName = BatchContext.ParseObjectName(context);
        var typeName = (Name)context.Token;

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
                    if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    declaredScale = scaleValue.AsInt32;
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

        var (resolved, maxLength, _) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale, 1, variableName);
        return (resolved, maxLength);
    }

    /// <summary>
    /// Column resolver passed when running an expression that has no FROM
    /// clause (DECLARE initializer, SET RHS). Any column reference in such
    /// an expression should fail at evaluate time as an unknown identifier;
    /// this resolver provides a default by raising Msg 207.
    /// </summary>
    internal static SqlValue NoColumnResolver(MultiPartName name) =>
        throw SimulatedSqlException.InvalidColumnName(name);

    /// <summary>
    /// Tries to bind <paramref name="variableName"/> to a user-defined
    /// <see cref="TableType"/>. Returns true on success (a fresh
    /// <see cref="HeapTable"/> clone is registered on
    /// <see cref="BatchContext.TableVariables"/> and the cursor sits past
    /// the type name); returns false to fall through to the scalar parser.
    /// Multi-part names always commit (a miss raises Msg 2715 — probe-
    /// confirmed); 1-part names only commit on a successful TableTypes
    /// lookup so built-in scalars still resolve.
    /// </summary>
    private static bool TryParseDeclareTableTypeVariable(ParserContext context, Name firstNameToken, string variableName, int variableIndex)
    {
        // Peek for `.` continuation. Multi-part means user-defined type
        // (built-in scalars are always 1-part).
        var checkpoint = context.SaveCheckpoint();
        var sawDot = context.MoveNext() && context.Token is Operator { Character: '.' };
        context.RestoreCheckpoint(checkpoint);

        if (sawDot)
        {
            var objectName = BatchContext.ParseObjectName(context);
            if (context.Batch.TryResolveTableType(objectName, out var tableType))
            {
                context.MoveNextOptional();
                RegisterTableTypeVariable(context, variableName, tableType);
                return true;
            }
            // Multi-part may also resolve to an alias type — fall through to
            // the scalar parser when so. ParseDeclareTypeSpec re-parses the
            // multi-part name itself (cursor restored).
            if (context.Batch.TryResolveAliasType(objectName, out _))
            {
                context.RestoreCheckpoint(checkpoint);
                return false;
            }
            throw SimulatedSqlException.CannotFindDataType(variableIndex, objectName.ToString(), "@" + variableName);
        }

        // 1-part name. Try TableType lookup; on miss, fall through to scalar
        // (which itself may resolve to a 1-part alias type or a built-in).
        if (!context.Batch.TryResolveTableType(new MultiPartName(firstNameToken.Value), out var singleType))
            return false;
        context.MoveNextOptional();
        RegisterTableTypeVariable(context, variableName, singleType);
        return true;
    }

    private static void RegisterTableTypeVariable(ParserContext context, string variableName, TableType tableType) =>
        context.Batch.TableVariables[variableName] = tableType.Clone("@" + variableName, context.Batch);

    /// <summary>
    /// Parses the column-list body of <c>DECLARE @t TABLE (...)</c> and
    /// registers the resulting <see cref="HeapTable"/> on
    /// <see cref="BatchContext.TableVariables"/>. Cursor on entry: the
    /// <c>TABLE</c> keyword; cursor on exit: one past the closing <c>)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reuses the CREATE TABLE column-list parser via the shared
    /// <c>ParseColumnList(..., isTableVariable: true)</c> entry point. Two
    /// shapes are gated off for <c>@t</c> via the flag (Msg 102, probe-
    /// confirmed against SQL Server 2025): <c>CONSTRAINT name</c> (real SQL
    /// Server's grammar disallows named constraints in table-variable
    /// declarations) and <c>REFERENCES</c> (foreign keys). All other column
    /// shapes work: IDENTITY, NOT NULL/NULL, DEFAULT, inline + table-level
    /// PRIMARY KEY / UNIQUE / CHECK, computed columns (with optional PERSISTED),
    /// rowversion. Statement-level atomicity for <c>@t</c> mutations is
    /// modeled via the per-statement <see cref="BatchContext.CurrentTableVarUndoLog"/>;
    /// transaction-scoped <c>ROLLBACK</c> does NOT undo <c>@t</c> writes
    /// (probe-confirmed: real SQL Server's table variables are
    /// non-transactional).
    /// </para>
    /// <para>
    /// Storage: builds a <see cref="HeapTable"/> with
    /// <see cref="HeapTable.IsTableVariable"/> set so DML routes through the
    /// non-transactional mutation path. The table's name is <c>"@t"</c> with
    /// the leading <c>@</c> kept so error wording for NOT NULL / PK / UNIQUE
    /// / CHECK violations renders as <c>table '@t'</c> matching real SQL
    /// Server. The dict key on <see cref="BatchContext.TableVariables"/>
    /// strips the <c>@</c> (matching the
    /// <see cref="BatchContext.Variables"/> dict's convention).
    /// </para>
    /// </remarks>
    private static void ParseDeclareTableVariable(ParserContext context, string variableName)
    {
        var fullName = "@" + variableName;
        // The false return (skip mode) is ignored here: table-variable
        // declarations are compile-scoped batch-wide like scalar DECLAREs,
        // so an un-taken IF branch still registers @t (probe-confirmed
        // against SQL Server 2025). Only CREATE FUNCTION's RETURNS @r TABLE
        // caller uses the skip signal, to avoid registering the function.
        _ = TryParseTableVariableColumnsAndConstraints(context, fullName, out var columns, out var keyConstraints, out var checkConstraints);

        var heapTable = new HeapTable(
            fullName,
            columns,
            context.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: context.Batch.CurrentStatement.UtcNow,
            keyConstraints: keyConstraints,
            checkConstraints: checkConstraints,
            isTableVariable: true);
        context.Batch.TableVariables[variableName] = heapTable;
    }

    /// <summary>
    /// Shared <c>TABLE (column-list)</c> parser for <c>DECLARE @t TABLE</c>
    /// and <c>CREATE FUNCTION ... RETURNS @r TABLE</c>. Cursor on entry: the
    /// <c>TABLE</c> reserved keyword. Cursor on exit: one token past the
    /// closing <c>)</c>. <paramref name="fullName"/> is the surface name
    /// reported in error messages (e.g. <c>"@r"</c>). The out-params are
    /// populated unconditionally; the return value is <see langword="false"/>
    /// under <see cref="BatchContext.IsSkipping"/> (an un-taken IF branch) so
    /// the CREATE FUNCTION caller can avoid registering the function, while
    /// DECLARE registers its compile-scoped table variable either way.
    /// </summary>
    private static bool TryParseTableVariableColumnsAndConstraints(
        ParserContext context,
        string fullName,
        out HeapColumn[] resolvedColumns,
        out KeyConstraint[] keyConstraints,
        out CheckConstraint[] checkConstraints)
    {
        resolvedColumns = [];
        keyConstraints = [];
        checkConstraints = [];

        context.MoveNextRequired(); // consume TABLE
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)>();

        if (!ParseColumnList(context, fullName, isTableVariable: true, isTableType: false, heapColumns, pendingKeys, pendingChecks, pendingComputed))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextOptional();

        // Pass 2: resolve computed columns now that every column name has
        // been seen. Same two-pass discipline CREATE TABLE uses — pulls the
        // declared length off the resolved type for var-length string/binary
        // families so EnforceMaxLength sees the same cap that GetSqlType
        // inferred.
        SqlType ResolveComputedReference(MultiPartName reference)
        {
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } existing && context.Batch.CurrentDatabase.Collation.Equals(existing.Name, reference.Leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, fullName)
                        : existing.Type;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && context.Batch.CurrentDatabase.Collation.Equals(pending.Name, reference.Leaf))
                            throw SimulatedSqlException.ComputedColumnReferencedInComputed(pending.Name, fullName);
                    }
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pending in pendingComputed)
        {
            var resolvedType = pending.Expression.GetSqlType(context.Batch, ResolveComputedReference);
            int? computedMaxLength = resolvedType switch
            {
                VarcharSqlType v when v.length > 0 => v.length,
                NVarcharSqlType nv when nv.length > 0 => nv.length,
                VarbinarySqlType vb when vb.length > 0 => vb.length,
                _ => null,
            };
            heapColumns[pending.Index] = new HeapColumn(
                pending.Name,
                resolvedType,
                maxLength: computedMaxLength,
                nullable: pending.Nullable,
                computedExpression: pending.Expression,
                isPersisted: pending.Persisted,
                computedDefinition: pending.Definition);
        }

        // Inline column-level CHECK predicates may only reference their
        // owning column — Msg 8141. Same structural walk as CREATE TABLE.
        foreach (var pending in pendingChecks)
        {
            if (pending.InlineColumn is not { } owningColumn)
                continue;
            pending.Predicate.VisitOperandExpressions(op =>
                op.VisitColumnReferences(name =>
                {
                    if (!context.Batch.CurrentDatabase.Collation.Equals(name.Leaf, owningColumn))
                        throw SimulatedSqlException.InlineCheckReferencesAnotherColumn(owningColumn, fullName);
                }));
        }

        resolvedColumns = [.. heapColumns!];
        keyConstraints = ResolveKeyConstraints(fullName, heapColumns!, pendingKeys, context.CurrentDatabase);
        checkConstraints = ResolveCheckConstraints(fullName, pendingChecks, context.CurrentDatabase);
        return !context.Batch.IsSkipping;
    }
}
