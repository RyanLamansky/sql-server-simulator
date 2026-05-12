using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
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

        do
        {
            if (context.GetNextRequired() is not AtPrefixedString variableToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var variableName = variableToken.Value;
            // In skip mode the duplicate-name check (Msg 134) is also gated:
            // real SQL Server defers binding of un-taken IF branches, so a
            // second DECLARE of the same name in an un-taken branch never
            // sees the first DECLARE. Without this gate, the simulator would
            // surface Msg 134 where SQL Server stays silent.
            if (!context.Batch.IsSkipping
                && (context.Batch.Variables.ContainsKey(variableName)
                    || context.Batch.TableVariables.ContainsKey(variableName)))
            {
                throw SimulatedSqlException.VariableAlreadyDeclared(variableName);
            }

            // Optional AS keyword between name and type spec — `DECLARE @v AS INT`.
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.As })
                context.MoveNextRequired();

            // Table variable form: `DECLARE @t TABLE (cols)`. Only one
            // table-variable declaration per statement (probe-confirmed:
            // `DECLARE @t1 TABLE (...), @t2 TABLE (...)` raises Msg 102, and
            // mixing scalar + table in one DECLARE raises Msg 156). The
            // table form must be the only declaration in the statement —
            // a leading scalar (sawScalar = true) means we already passed
            // a `,`, so reject. A trailing `,` after the column list also
            // raises Msg 102.
            if (context.Token is ReservedKeyword { Keyword: Keyword.Table })
            {
                if (sawScalar)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                ParseDeclareTableVariable(context, variableName);
                return context.Token is Operator { Character: ',' }
                    ? throw SimulatedSqlException.SyntaxErrorNear(context)
                    : null;
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

            if (!context.Batch.IsSkipping)
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
        if (context.Token is not Name typeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);

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

        return SqlType.GetByName(typeName, declaredMaxLength, declaredScale, 1, variableName);
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
    /// Parses the column-list body of <c>DECLARE @t TABLE (...)</c> and
    /// registers the resulting <see cref="HeapTable"/> on
    /// <see cref="BatchContext.TableVariables"/>. Cursor on entry: the
    /// <c>TABLE</c> keyword; cursor on exit: one past the closing <c>)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// v1 coverage: column type (with <c>(N)</c>/<c>(p, s)</c> spec) +
    /// <c>NOT NULL</c> / <c>NULL</c> + <c>DEFAULT expr</c> + inline anonymous
    /// <c>PRIMARY KEY</c> + table-level anonymous <c>PRIMARY KEY (cols)</c>.
    /// Probe-confirmed against SQL Server 2025 (2026-05-12) that named
    /// constraints (<c>CONSTRAINT pk1 PRIMARY KEY</c>) and <c>REFERENCES</c>
    /// raise Msg 156 — the simulator surfaces those via
    /// <see cref="SimulatedSqlException.SyntaxErrorNear(ParserContext)"/>. Real SQL Server
    /// accepts <c>UNIQUE</c> / inline <c>CHECK</c> / <c>IDENTITY</c> /
    /// computed columns / <c>rowversion</c> inside table-variable declarations;
    /// the simulator's v1 surfaces those as <see cref="NotSupportedException"/>
    /// with the feature name so the gap is loud rather than silent.
    /// </para>
    /// <para>
    /// Storage: builds a <see cref="HeapTable"/> with
    /// <see cref="HeapTable.IsTableVariable"/> set so DML routes through the
    /// non-transactional mutation path (probe-confirmed: INSERT @t inside
    /// <c>BEGIN TRAN; ROLLBACK</c> leaves rows intact). The table's name is
    /// <c>"@t"</c> with the leading <c>@</c> kept so error wording for NOT
    /// NULL / PK violations renders as <c>table '@t'</c> matching real SQL
    /// Server. The dict key on <see cref="BatchContext.TableVariables"/>
    /// strips the <c>@</c> (matching the
    /// <see cref="BatchContext.Variables"/> dict's convention).
    /// </para>
    /// </remarks>
    private static void ParseDeclareTableVariable(ParserContext context, string variableName)
    {
        context.MoveNextRequired(); // consume TABLE
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var fullName = "@" + variableName;
        var columns = new List<HeapColumn>();
        var pendingKeys = new List<(KeyConstraintKind Kind, int[] Ordinals)>();

        do
        {
            context.MoveNextRequired();

            // Table-level constraint position — only anonymous PRIMARY KEY
            // ships in v1. CONSTRAINT-named forms raise a syntax error
            // matching probe-confirmed Msg 156; other constraint kinds are
            // accepted by real SQL Server in table-variable declarations but
            // not modeled in v1 — surface as NotSupportedException so the
            // gap is loud.
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Primary }:
                    pendingKeys.Add((KeyConstraintKind.PrimaryKey, ParseTableLevelKeyColumns(context, columns, fullName)));
                    continue;
                case ReservedKeyword { Keyword: Keyword.Constraint }:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                case ReservedKeyword { Keyword: var unsupportedKind and (Keyword.Unique or Keyword.Check) }:
                    throw new NotSupportedException($"DECLARE @t TABLE with {unsupportedKind} constraint isn't modeled (deferred from v1; ship in a follow-on).");
            }

            if (context.Token is not Name columnNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var columnName = columnNameToken.Value;
            context.MoveNextRequired();

            // Computed column (`col AS expr`) — not in v1.
            if (context.Token is ReservedKeyword { Keyword: Keyword.As })
                throw new NotSupportedException($"DECLARE @t TABLE with computed columns isn't modeled (deferred from v1; ship in a follow-on).");

            if (context.Token is not Name typeName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
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

            bool? nullable = null;
            Expression? defaultExpression = null;
            var inlinePk = false;
            while (true)
            {
                switch (context.Token)
                {
                    case ReservedKeyword { Keyword: Keyword.Not } when !nullable.HasValue:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        nullable = false;
                        context.MoveNextRequired();
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Null } when !nullable.HasValue:
                        nullable = true;
                        context.MoveNextRequired();
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Default } when defaultExpression is null:
                        context.MoveNextRequired();
                        context.InDefaultClause = true;
                        try { defaultExpression = Expression.Parse(context); }
                        finally { context.InDefaultClause = false; }
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Primary } when !inlinePk:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Key })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextRequired();
                        inlinePk = true;
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Identity }:
                        throw new NotSupportedException("DECLARE @t TABLE with IDENTITY columns isn't modeled (deferred from v1; ship in a follow-on).");
                    case ReservedKeyword { Keyword: Keyword.Unique }:
                        throw new NotSupportedException("DECLARE @t TABLE with UNIQUE constraints isn't modeled (deferred from v1; ship in a follow-on).");
                    case ReservedKeyword { Keyword: Keyword.Check }:
                        throw new NotSupportedException("DECLARE @t TABLE with CHECK constraints isn't modeled (deferred from v1; ship in a follow-on).");
                    case ReservedKeyword { Keyword: Keyword.Constraint }:
                    case ReservedKeyword { Keyword: Keyword.References }:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                break;
            }

            if (inlinePk)
            {
                if (nullable == true)
                    throw SimulatedSqlException.PrimaryKeyOnNullableColumn(fullName);
                nullable = false;
            }
            var actualNullable = nullable ?? true;
            var (resolvedType, maxLength) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, columns.Count + 1, columnName);
            if (resolvedType == SqlType.RowVersion)
                throw new NotSupportedException("DECLARE @t TABLE with rowversion columns isn't modeled (deferred from v1; ship in a follow-on).");
            columns.Add(new HeapColumn(columnName, resolvedType, maxLength, actualNullable, identity: null, defaultExpression));
            if (inlinePk)
                pendingKeys.Add((KeyConstraintKind.PrimaryKey, [columns.Count - 1]));
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return;

        var resolvedKeys = new List<KeyConstraint>(pendingKeys.Count);
        var sawPk = false;
        foreach (var (kind, ordinals) in pendingKeys)
        {
            if (kind == KeyConstraintKind.PrimaryKey)
            {
                if (sawPk)
                    throw SimulatedSqlException.MultiplePrimaryKey(fullName);
                sawPk = true;
                foreach (var ord in ordinals)
                {
                    if (columns[ord].Nullable)
                        throw SimulatedSqlException.PrimaryKeyOnNullableColumn(fullName);
                }
            }
            // v1 table-variable columns are all stored (no computed / no
            // non-persisted), so declaration ordinals match storage ordinals.
            resolvedKeys.Add(new KeyConstraint(
                kind,
                $"PK_TV_{context.CurrentDatabase.AllocateObjectId():X8}",
                ordinals,
                context.CurrentDatabase.AllocateObjectId()));
        }

        var heapTable = new HeapTable(
            fullName,
            [.. columns],
            context.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: context.Batch.CurrentStatement.UtcNow,
            keyConstraints: [.. resolvedKeys],
            checkConstraints: null,
            isTableVariable: true);
        context.Batch.TableVariables[variableName] = heapTable;
    }

    /// <summary>
    /// Parses the column list of a table-level <c>PRIMARY KEY (col1, col2)</c>
    /// constraint inside <c>DECLARE @t TABLE</c>. Cursor on entry: the
    /// <c>PRIMARY</c> keyword; cursor on exit: one past the closing <c>)</c>.
    /// </summary>
    private static int[] ParseTableLevelKeyColumns(ParserContext context, List<HeapColumn> columns, string tableName)
    {
        // Consume PRIMARY KEY.
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Key })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var ordinals = new List<int>();
        do
        {
            if (context.GetNextRequired() is not Name columnNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var found = -1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (Collation.Default.Equals(columns[i].Name, columnNameToken.Value))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
                throw SimulatedSqlException.InvalidColumnName(new MultiPartName(columnNameToken.Value));
            // Promote to NOT NULL — matches probe-confirmed behavior of inline
            // PRIMARY KEY on a column declared without explicit NULL/NOT NULL.
            if (columns[found].Nullable)
                columns[found] = new HeapColumn(columns[found].Name, columns[found].Type, columns[found].MaxLength, nullable: false, identity: columns[found].Identity, defaultExpression: columns[found].Default);
            ordinals.Add(found);
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        _ = tableName; // reserved for future error wording
        return [.. ordinals];
    }
}
