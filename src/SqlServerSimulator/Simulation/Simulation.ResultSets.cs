using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the optional <c>WITH &lt;execute_option&gt; [, …]</c> trailer of
    /// an <c>EXECUTE</c> statement — <c>RECOMPILE</c> (accepted and
    /// discarded; the simulator has no plan-reuse decision to override) and
    /// the three <c>RESULT SETS</c> forms. Returns the parsed result-set
    /// contract, or <see langword="null"/> when the statement carried no
    /// <c>RESULT SETS</c> option.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cursor on entry: the statement's trailing terminator, which is the
    /// <c>WITH</c> keyword when the clause is present. The <c>WITH</c> is
    /// consumed only when the token after it opens an execute option, so a
    /// following statement that legitimately starts with <c>WITH</c> (a CTE)
    /// still reaches the dispatch loop untouched.
    /// </para>
    /// <para>
    /// Grammar notes, probe-confirmed against SQL Server 2025: options may
    /// appear in either order (<c>WITH RECOMPILE, RESULT SETS …</c> and the
    /// reverse both parse), a second <c>RESULT SETS</c> is Msg 102, and each
    /// result-set definition carries its own parentheses — so the single-set
    /// column-list form needs the doubled <c>((…))</c> and a bare <c>(…)</c>
    /// fails at the first column name.
    /// </para>
    /// </remarks>
    private static ResultSetsContract? ParseExecuteOptions(BatchContext batch, bool insertExecSource, bool characterStringForm = false) =>
        ParseExecuteOptions(batch, insertExecSource, out _, characterStringForm);

    /// <inheritdoc cref="ParseExecuteOptions(BatchContext, bool, bool)"/>
    /// <param name="batch">The batch whose parser stands after the call's arguments.</param>
    /// <param name="insertExecSource">Whether the EXECUTE is an <c>INSERT … EXEC</c> source.</param>
    /// <param name="recompile">Whether the list carries <c>RECOMPILE</c>.</param>
    /// <param name="characterStringForm">
    /// Whether the EXECUTE runs a character string (<c>EXEC ('…')</c>), whose
    /// one option is <c>RESULT SETS</c>: <c>RECOMPILE</c> is Msg 102 on the
    /// word and a second option Msg 102 on its comma (probed 2026-10-07
    /// against SQL Server 2025), where <c>sp_executesql</c> takes both.
    /// </param>
    private static ResultSetsContract? ParseExecuteOptions(BatchContext batch, bool insertExecSource, out bool recompile, bool characterStringForm = false)
    {
        recompile = false;
        var context = batch.Parser;
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return null;

        var checkpoint = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Recompile or ContextualKeyword.Result })
        {
            context.RestoreCheckpoint(checkpoint);
            return null;
        }

        ResultSetsContract? contract = null;
        while (true)
        {
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Recompile } && !characterStringForm)
            {
                recompile = true;
                context.MoveNextOptional();
            }
            else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Result } && contract is null)
            {
                if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Sets })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                // Real rejects the clause outright when the EXECUTE is an
                // INSERT … EXEC source, and does it one token late — the
                // reported token is SETS, not WITH (probe-confirmed).
                if (insertExecSource)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                contract = ParseResultSetsBody(batch);
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            if (context.Token is not Operator { Character: ',' })
                break;
            if (characterStringForm)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }

        // Nothing but a statement boundary may follow the option list — real
        // reports the stray token (Msg 102). The check is scoped to statements
        // that actually carried a WITH clause, so the bare-identifier argument
        // form EXEC accepts elsewhere is untouched.
        return IsStatementBoundary(context.Token)
            ? contract
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses what follows <c>RESULT SETS</c>: <c>UNDEFINED</c>,
    /// <c>NONE</c>, or the parenthesized list of result-set definitions.
    /// </summary>
    private static ResultSetsContract ParseResultSetsBody(BatchContext batch)
    {
        var context = batch.Parser;
        switch (context.Token)
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Undefined }:
                context.MoveNextOptional();
                return ResultSetsContract.Undefined;
            case UnquotedString { ContextualKeyword: ContextualKeyword.None }:
                context.MoveNextOptional();
                return new ResultSetsContract([]);
            case Operator { Character: '(' }:
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        var shapes = new List<ResultSetShape>();
        context.MoveNextRequired();
        while (true)
        {
            shapes.Add(ParseResultSetShape(batch));
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new ResultSetsContract([.. shapes]);
    }

    /// <summary>
    /// Parses one result-set definition: the parenthesized
    /// <c>(column_name data_type [COLLATE …] [NULL | NOT NULL], …)</c> list,
    /// or one of the <c>AS OBJECT</c> / <c>AS TYPE</c> / <c>AS FOR XML</c>
    /// shorthands (<see cref="ParseResultSetShorthand"/>).
    /// </summary>
    private static ResultSetShape ParseResultSetShape(BatchContext batch)
    {
        var context = batch.Parser;
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            return ParseResultSetShorthand(batch);
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var names = new List<string>();
        var types = new List<SqlType>();
        var bareTypeNames = new List<string>();
        var typeNames = new List<string>();
        var maxLengths = new List<int?>();
        var nullability = new List<bool>();
        var reportsNumeric = new List<bool>();
        while (true)
        {
            if (context.Token is not Name columnName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(columnName.Value);
            context.MoveNextRequired();

            var (qualifiedTypeName, typeLeaf) = TypeNameSynonyms.ReadTypeName(context);
            var isNumericSpelling = Cast.ReportsNumeric(typeLeaf);
            reportsNumeric.Add(isNumericSpelling);
            context.MoveNextOptional();

            int? declaredMaxLength = null;
            int? declaredScale = null;
            if (context.Token is Operator { Character: '(' })
            {
                declaredMaxLength = context.GetNextRequired() switch
                {
                    Numeric { Value: { IsNull: false } lengthValue } => lengthValue.AsInt32,
                    UnquotedString { ContextualKeyword: ContextualKeyword.Max } => SqlType.MaxLengthSentinel,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                switch (context.GetNextRequired())
                {
                    case Operator { Character: ',' }:
                        _ = context.GetNextRequired();
                        declaredScale = TypeNameSynonyms.ReadSecondTypeArgument(context, typeLeaf);
                        if (context.GetNextRequired() is not Operator { Character: ')' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        break;
                    case Operator { Character: ')' }:
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                context.MoveNextOptional();
            }
            var (resolvedType, resolvedMaxLength, _, _) = ResolveTypeReference(
                batch, qualifiedTypeName, typeLeaf, declaredMaxLength, declaredScale,
                index: types.Count + 1, TypeSpecSite.Column, columnName: columnName.Value);

            // Real's messages spell the declared type canonically, not as
            // written: an uppercase NVARCHAR(2) and the ANSI synonym
            // `national character varying(2)` both report `nvarchar(2)`. Only
            // the numeric / decimal pair, which share one SqlType, needs the
            // as-written word to survive.
            var bareTypeName = isNumericSpelling ? "numeric" : resolvedType.SqlServerName;
            bareTypeNames.Add(bareTypeName);
            typeNames.Add(FormatDeclaredTypeName(bareTypeName, declaredMaxLength, declaredScale));

            // Optional COLLATE: pins the declared string type's collation, so
            // the projected column reports it the way a CAST … COLLATE would.
            if (context.Token is ReservedKeyword { Keyword: Keyword.Collate })
            {
                context.MoveNextRequired();
                var collationName = CollateExpression.ResolvePseudoCollationName(context.Token switch
                {
                    Name collationToken => collationToken.Value,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                }, batch);
                if (!Collation.IsRecognized(collationName))
                    throw new NotSupportedException($"COLLATE: collation '{collationName}' isn't on the simulator's recognized list.");
                resolvedType = resolvedType.WithCollation(Collation.Get(collationName), Coercibility.Implicit);
                context.MoveNextOptional();
            }

            types.Add(resolvedType);
            maxLengths.Add(resolvedMaxLength);

            // Nullability defaults to nullable when the declaration omits it
            // (probe-confirmed via sp_describe_first_result_set: is_nullable
            // reads 1 for a bare `x int`).
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Not }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    nullability.Add(false);
                    context.MoveNextRequired();
                    break;
                case ReservedKeyword { Keyword: Keyword.Null }:
                    nullability.Add(true);
                    context.MoveNextRequired();
                    break;
                default:
                    nullability.Add(true);
                    break;
            }

            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        return new ResultSetShape([.. names], [.. types], [.. bareTypeNames], [.. typeNames], [.. maxLengths], [.. nullability], [.. reportsNumeric]);
    }

    /// <summary>
    /// The three shorthand result-set definitions, cursor on their <c>AS</c>
    /// (probed 2026-09-27 against SQL Server 2025): <c>AS OBJECT</c> takes the
    /// columns of a table, view or table-valued function — identity and
    /// computed columns included, each column's own nullability standing as
    /// the declared one — and <c>AS TYPE</c> those of a table type; either
    /// name has at most two parts, and one that names nothing of the kind is
    /// <strong>Msg 11533</strong> / <strong>11534</strong> echoing it as
    /// written, a three-part name included. <c>AS FOR XML</c> declares the one
    /// <c>ntext</c> column a <c>FOR XML</c> query sends, under the same name.
    /// </summary>
    private static ResultSetShape ParseResultSetShorthand(BatchContext batch)
    {
        var context = batch.Parser;
        HeapColumn[] columns;
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.For }:
                if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Xml })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                columns = [new HeapColumn(Selection.ForXmlColumnName, SqlType.NText, maxLength: null, nullable: true)];
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Object }:
                {
                    context.MoveNextRequired();
                    var name = BatchContext.ParseObjectName(context);
                    context.MoveNextRequired();
                    columns = ResultSetObjectColumns(batch, name) ?? throw SimulatedSqlException.ResultSetsInvalidObjectName(name.ToString());
                    break;
                }
            case UnquotedString { ContextualKeyword: ContextualKeyword.Type }:
                {
                    context.MoveNextRequired();
                    var name = BatchContext.ParseObjectName(context);
                    context.MoveNextRequired();
                    columns = name.Count <= 2 && batch.TryResolveTableType(name, out var tableType)
                        ? tableType.Columns
                        : throw SimulatedSqlException.ResultSetsInvalidTableType(name.ToString());
                    break;
                }
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        var names = new string[columns.Length];
        var types = new SqlType[columns.Length];
        var bareTypeNames = new string[columns.Length];
        var typeNames = new string[columns.Length];
        var maxLengths = new int?[columns.Length];
        var nullability = new bool[columns.Length];
        var reportsNumeric = new bool[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            names[i] = column.Name;
            types[i] = column.Type;
            reportsNumeric[i] = column.SpelledNumeric;
            bareTypeNames[i] = column.TypeName;
            typeNames[i] = column.Type.ToString()!;
            maxLengths[i] = column.MaxLength;
            nullability[i] = column.Nullable;
        }
        return new ResultSetShape(names, types, bareTypeNames, typeNames, maxLengths, nullability, reportsNumeric);
    }

    /// <summary>
    /// The columns <c>AS OBJECT</c> reads off a table, view or table-valued
    /// function; null when the name, of at most two parts, resolves to none.
    /// </summary>
    private static HeapColumn[]? ResultSetObjectColumns(BatchContext batch, MultiPartName name)
    {
        if (name.Count > 2)
            return null;
        if (batch.TryResolveView(name, out var view))
            return view.OutputColumns;
        if (batch.TryResolveTable(name, out var table) && !BatchContext.IsTableVariableName(table.Name))
            return table.Columns;
        var qualified = name.Count == 1 ? new MultiPartName(Database.DefaultSchemaName).WithAddedPart(name.Leaf) : name;
        return batch.TryResolveFunction(qualified, out var function)
            ? function switch
            {
                InlineTableValuedFunction inline => inline.OutputColumns,
                MultiStatementTableValuedFunction multiStatement => multiStatement.OutputColumns,
                ClrTableValuedFunction clr => clr.OutputColumns,
                _ => null,
            }
            : null;
    }

    /// <summary>
    /// Renders a declared type the way Msg 8114 spells it — the canonical
    /// type name plus the declaration's own width trailer.
    /// </summary>
    private static string FormatDeclaredTypeName(string typeWord, int? declaredMaxLength, int? declaredScale) =>
        declaredMaxLength is not { } length ? typeWord
        : declaredScale is { } scale ? $"{typeWord}({length},{scale})"
        : length == SqlType.MaxLengthSentinel ? $"{typeWord}(max)"
        : $"{typeWord}({length})";

    /// <summary>
    /// The sink an <c>EXEC … WITH RESULT SETS</c> hands the batch it runs:
    /// the contract's declared sets, or the caller's own sink when the clause
    /// declares none (<c>UNDEFINED</c>, or no clause); null for a call whose
    /// sets never reach the client, an <c>INSERT … EXEC</c> source's.
    /// </summary>
    private static ResultSetsSink? ResultSetsSinkFor(BatchContext batch, ResultSetsContract? contract, bool insertExecSource) =>
        insertExecSource ? null
        : contract?.Shapes is { } shapes ? new ResultSetsSink(shapes, batch.ResultSetsSink)
        : batch.ResultSetsSink;

    /// <summary>
    /// Layers an <c>EXEC … WITH RESULT SETS</c> contract over the outcomes an
    /// invoked module produced, for the result sets its own statements didn't
    /// already send through <paramref name="sink"/> — a <c>SELECT</c> of the
    /// module claims its set and converts its rows as it produces them (see
    /// <see cref="SendThroughResultSets"/>), while a set some other producer
    /// sent (a DML <c>OUTPUT</c>, a system procedure's, a trigger's) is claimed
    /// here and converts as its reader reads it. Non-result outcomes (row
    /// counts, dynamic-SQL scope markers) pass through and don't count toward
    /// the contract — probe-confirmed: a procedure whose only statement is an
    /// INSERT satisfies <c>RESULT SETS NONE</c>. Fewer sets than declared is
    /// the <c>EXECUTE</c>'s own Msg 11536 once the module ends.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ApplyResultSetsContract(
        IEnumerable<SimulatedStatementOutcome> outcomes,
        ResultSetsSink? sink)
    {
        if (sink is null)
        {
            foreach (var outcome in outcomes)
                yield return outcome;
            yield break;
        }
        foreach (var outcome in outcomes)
        {
            if (outcome is not SimulatedQueryResult query
                || (query is SimulatedSqlResultSet { SentThrough: { } through } && through.Reaches(sink)))
            {
                yield return outcome;
                continue;
            }
            var claim = ClaimResultSet(sink, query.Schema, alone: true, query);
            yield return ConvertedResultSet(query, claim, through: null, query);
        }
        if (sink.Sent < sink.Shapes.Length)
            throw SimulatedSqlException.ResultSetsTooFewSent(sink.Shapes.Length, sink.Sent);
    }

    /// <summary>
    /// A <c>SELECT</c>'s claim on the next declared set of
    /// <paramref name="sink"/> and of every sink enclosing it, each judging
    /// the columns the one inside it declared: past the declared count is Msg
    /// 11535, a column count that differs Msg 11537 and a type no implicit
    /// conversion reaches Msg 11538, raised by the statement before it sends
    /// anything, as real raises them (probed 2026-10-09 against SQL Server
    /// 2025: a <c>TRY</c> around the module's <c>SELECT</c> catches each, and
    /// the client sees no column metadata).
    /// </summary>
    internal static ResultSetClaim[] ClaimResultSet(ResultSetsSink sink, SqlType[] schema) =>
        ClaimResultSet(sink, schema, alone: false, origin: null);

    private static ResultSetClaim[] ClaimResultSet(ResultSetsSink sink, SqlType[] schema, bool alone, SimulatedQueryResult? origin)
    {
        var depth = 0;
        for (var level = sink; level is not null && (depth == 0 || !alone); level = level.Outer)
            depth++;
        var claims = new ResultSetClaim[depth];
        var sentTypes = schema;
        var at = sink;
        for (var i = 0; i < claims.Length; i++, at = at.Outer!)
        {
            if (at.Sent == at.Shapes.Length)
                throw AttributeToOrigin(SimulatedSqlException.ResultSetsTooManySent(at.Shapes.Length), origin);
            var shape = at.Shapes[at.Sent];
            var setNumber = ++at.Sent;
            if (sentTypes.Length != shape.Types.Length)
            {
                throw AttributeToOrigin(
                    SimulatedSqlException.ResultSetsColumnCountMismatch(shape.Types.Length, setNumber, sentTypes.Length), origin);
            }
            for (var c = 0; c < sentTypes.Length; c++)
            {
                if (!IsImplicitlyConvertible(sentTypes[c], shape.Types[c]))
                {
                    throw AttributeToOrigin(
                        SimulatedSqlException.ResultSetsNoConversion(shape.BareTypeNames[c], c + 1, setNumber, sentTypes[c].SqlServerName),
                        origin);
                }
            }
            claims[i] = new ResultSetClaim(shape, setNumber);
            sentTypes = shape.Types;
        }
        return claims;
    }

    /// <summary>
    /// A <c>SELECT</c>'s rows as the sinks it claimed (see
    /// <see cref="ClaimResultSet(ResultSetsSink, SqlType[])"/>) send them, converted
    /// as the statement produces each, so a value that won't convert is the
    /// statement's own run-time error — caught by a <c>TRY</c> around it inside
    /// the module, ending the module there, and failing the batch as a
    /// conversion failure does (probed 2026-10-09 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlResultSet SendThroughResultSets(SimulatedSqlResultSet produced, ResultSetClaim[] claims, ResultSetsSink sink) =>
        ConvertedResultSet(produced, claims, sink, origin: null);

    private static SimulatedSqlResultSet ConvertedResultSet(SimulatedQueryResult source, ResultSetClaim[] claims, ResultSetsSink? through, SimulatedQueryResult? origin)
    {
        var rows = source is SimulatedSqlResultSet resultSet ? resultSet.RowValues : CursorRows(source);
        foreach (var claim in claims)
            rows = ConvertResultSetRows(rows, claim.Shape, claim.SetNumber, origin);
        var last = claims[^1].Shape;
        var converted = new SimulatedSqlResultSet(last.Types, last.Names, rows)
        {
            ClientTextSize = source.ClientTextSize,
            ColumnNullability = last.Nullability,
            ColumnReportsNumeric = last.ReportsNumeric,
            OriginLine = source.OriginLine,
            OriginProcedure = source.OriginProcedure,
            SentThrough = through,
        };
        // Rows another statement still produces as they are read — a DML
        // statement's OUTPUT — go on being produced for the consumer of the
        // converted set.
        if (source is SimulatedSqlResultSet { Stream: { } stream })
        {
            converted.Stream = stream;
            stream.Result = converted;
        }
        return converted;
    }

    /// <summary>A result that is not a <see cref="SimulatedSqlResultSet"/>'s rows, read through its cursor.</summary>
    private static IEnumerable<SqlValue[]> CursorRows(SimulatedQueryResult source)
    {
        using var cursor = source.CreateCursor();
        var width = source.Schema.Length;
        while (cursor.MoveNext())
        {
            var row = new SqlValue[width];
            for (var i = 0; i < row.Length; i++)
                row[i] = cursor[i];
            yield return row;
        }
    }

    /// <summary>
    /// Streams the source rows through the declared column types. Conversion
    /// reuses the CAST value path (so the varchar asterisk fallback, silent
    /// truncation and rounding all behave as they do in a CAST) but reports
    /// every failure as Msg 8114 state 2 naming both decorated type names,
    /// which is what real does here regardless of which conversion rule was
    /// violated (state probed 2026-10-02 against SQL Server 2025) — a failure
    /// that ends the batch and rolls its transaction back uncaught, and dooms
    /// it caught, as a conversion failure does (probed 2026-10-09).
    /// </summary>
    private static IEnumerable<SqlValue[]> ConvertResultSetRows(IEnumerable<SqlValue[]> source, ResultSetShape shape, int setNumber, SimulatedQueryResult? origin)
    {
        foreach (var sent in source)
        {
            var row = new SqlValue[shape.Types.Length];
            for (var i = 0; i < row.Length; i++)
            {
                var value = sent[i];
                if (value.IsNull)
                {
                    if (!shape.Nullability[i])
                        throw AttributeToOrigin(SimulatedSqlException.ResultSetsNullInNonNullableColumn(i + 1, setNumber), origin);
                    row[i] = SqlValue.Null(shape.Types[i]);
                    continue;
                }
                try
                {
                    row[i] = Cast.ApplyCoercion(value, shape.Types[i], shape.MaxLengths[i]);
                }
                catch (SimulatedSqlException ex) when (Cast.IsConversionFailure(ex.Number))
                {
                    throw AttributeToOrigin(
                        SimulatedSqlException.ConvertingDataTypeError(value.Type.ToString()!, shape.TypeNames[i], state: 2).AbortingAsUnderXactAbort(), origin);
                }
            }
            yield return row;
        }
    }

    /// <summary>
    /// Stamps the producing statement's line and procedure onto a contract
    /// violation raised outside that statement — as a reader reads a set the
    /// <c>EXECUTE</c> claimed rather than the module's own <c>SELECT</c> —
    /// so it reads as raised where the rows came from, as real attributes
    /// Msg 11535 / 11537 / 11538 / 11553 and the conversion failure (Msg 11536
    /// is the exception and is left for the EXECUTE's own frame). A result
    /// produced outside a dispatch frame carries no origin and falls back to
    /// that same default; a statement's own claim has none, its dispatch
    /// stamping it.
    /// </summary>
    private static SimulatedSqlException AttributeToOrigin(SimulatedSqlException exception, SimulatedQueryResult? source)
    {
        if (source is { OriginLine: not 0 })
            exception.PreserveDiagnostics(source.OriginLine, source.OriginProcedure);
        return exception;
    }

    /// <summary>
    /// SQL Server's implicit-conversion matrix, as <c>WITH RESULT SETS</c>
    /// applies it: a declared type the run-time type can't reach implicitly is
    /// Msg 11538 even when an explicit <c>CAST</c> between the two would be
    /// legal (<c>xml</c> → <c>varchar</c> and <c>varchar</c> →
    /// <c>varbinary</c> are the notable rejections). Probed cell-by-cell
    /// against SQL Server 2025 over a 21 × 21 type grid.
    /// </summary>
    private static bool IsImplicitlyConvertible(SqlType source, SqlType target)
    {
        var sourceFamily = ConversionFamilyOf(source);
        var targetFamily = ConversionFamilyOf(target);
        return sourceFamily switch
        {
            ConversionFamily.Numeric => targetFamily
                is ConversionFamily.Numeric or ConversionFamily.Char or ConversionFamily.NChar
                or ConversionFamily.Binary or ConversionFamily.DateTime or ConversionFamily.Variant,
            // char / varchar reach every family but binary — image included,
            // which their Unicode counterparts don't reach.
            ConversionFamily.Char => targetFamily is not ConversionFamily.Binary,
            ConversionFamily.NChar => targetFamily is not (ConversionFamily.Binary or ConversionFamily.Image),
            ConversionFamily.Text or ConversionFamily.NText => targetFamily
                is ConversionFamily.Char or ConversionFamily.NChar or ConversionFamily.Text
                or ConversionFamily.NText or ConversionFamily.Xml,
            // Binary reaches the exact numerics but not float / real, and
            // reaches the UDTs, whose storage form is binary.
            ConversionFamily.Binary => targetFamily switch
            {
                ConversionFamily.Numeric => !SqlType.IsApproximateNumericCategory(target),
                ConversionFamily.Char or ConversionFamily.NChar or ConversionFamily.Binary
                    or ConversionFamily.Image or ConversionFamily.DateTime or ConversionFamily.Guid
                    or ConversionFamily.Xml or ConversionFamily.Variant or ConversionFamily.Udt => true,
                _ => false,
            },
            ConversionFamily.Image => targetFamily is ConversionFamily.Binary or ConversionFamily.Image,
            // The date/time families cross freely except date ↔ time, which
            // have no overlapping component to carry.
            ConversionFamily.DateTime or ConversionFamily.DateTime2 => targetFamily
                is ConversionFamily.Char or ConversionFamily.NChar or ConversionFamily.DateTime
                or ConversionFamily.Date or ConversionFamily.Time or ConversionFamily.DateTime2
                or ConversionFamily.Variant,
            ConversionFamily.Date => targetFamily
                is ConversionFamily.Char or ConversionFamily.NChar or ConversionFamily.DateTime
                or ConversionFamily.Date or ConversionFamily.DateTime2 or ConversionFamily.Variant,
            ConversionFamily.Time => targetFamily
                is ConversionFamily.Char or ConversionFamily.NChar or ConversionFamily.DateTime
                or ConversionFamily.Time or ConversionFamily.DateTime2 or ConversionFamily.Variant,
            ConversionFamily.Guid => targetFamily
                is ConversionFamily.Char or ConversionFamily.NChar or ConversionFamily.Binary
                or ConversionFamily.Guid or ConversionFamily.Variant,
            // xml, sql_variant and the UDTs (hierarchyid, geometry, geography)
            // reach only their own exact type.
            _ => source.GetType() == target.GetType(),
        };
    }

    /// <summary>
    /// Buckets a <see cref="SqlType"/> for <see cref="IsImplicitlyConvertible"/>.
    /// Deliberately finer than <see cref="SqlTypeCategory"/>, which folds
    /// <c>xml</c> and the spatial types into the string bucket and splits the
    /// numerics four ways — neither grouping matches the conversion matrix.
    /// </summary>
    private static ConversionFamily ConversionFamilyOf(SqlType type) => type switch
    {
        CharSqlType or VarcharSqlType => ConversionFamily.Char,
        NCharSqlType or NVarcharSqlType or SystemNameSqlType => ConversionFamily.NChar,
        TextSqlType => ConversionFamily.Text,
        NTextSqlType => ConversionFamily.NText,
        // rowversion rides the binary family: real treats timestamp more
        // narrowly than varbinary here (it declines nvarchar and sql_variant),
        // which the simulator doesn't reproduce.
        BinarySqlType or VarbinarySqlType or RowVersionSqlType => ConversionFamily.Binary,
        ImageSqlType => ConversionFamily.Image,
        DateTimeSqlType or SmallDateTimeSqlType => ConversionFamily.DateTime,
        DateSqlType => ConversionFamily.Date,
        TimeSqlType => ConversionFamily.Time,
        DateTime2SqlType or DateTimeOffsetSqlType => ConversionFamily.DateTime2,
        UniqueIdentifierSqlType => ConversionFamily.Guid,
        XmlSqlType => ConversionFamily.Xml,
        SqlVariantSqlType => ConversionFamily.Variant,
        _ when SqlType.IsIntegerCategory(type) || SqlType.IsExactNumericCategory(type)
            || SqlType.IsApproximateNumericCategory(type) => ConversionFamily.Numeric,
        _ => ConversionFamily.Udt,
    };

    /// <summary>Buckets for <see cref="IsImplicitlyConvertible"/>'s matrix.</summary>
    private enum ConversionFamily : byte
    {
        Numeric,
        Char,
        NChar,
        Text,
        NText,
        Binary,
        Image,
        DateTime,
        Date,
        Time,
        DateTime2,
        Guid,
        Xml,
        Variant,
        Udt,
    }
}

/// <summary>
/// A parsed <c>WITH RESULT SETS</c> clause. <see cref="Shapes"/> is
/// <see langword="null"/> for the <c>UNDEFINED</c> form (the module's own
/// metadata stands), empty for <c>NONE</c>, and one entry per declared set
/// otherwise.
/// </summary>
internal sealed class ResultSetsContract(ResultSetShape[]? shapes)
{
    public static readonly ResultSetsContract Undefined = new(null);

    public readonly ResultSetShape[]? Shapes = shapes;
}

/// <summary>
/// What the batch an <c>EXEC … WITH RESULT SETS</c> runs sends its result sets
/// through: the declared sets, how many have been claimed, and the sink of an
/// enclosing call's clause, whose declarations each set meets in turn.
/// </summary>
internal sealed class ResultSetsSink(ResultSetShape[] shapes, ResultSetsSink? outer)
{
    public readonly ResultSetShape[] Shapes = shapes;
    public readonly ResultSetsSink? Outer = outer;
    public int Sent;

    /// <summary>Whether a set sent through this sink was sent through <paramref name="sink"/> too.</summary>
    public bool Reaches(ResultSetsSink sink)
    {
        for (var level = this; level is not null; level = level.Outer)
        {
            if (level == sink)
                return true;
        }
        return false;
    }
}

/// <summary>One sink's declared set a result set claimed, and its number.</summary>
internal readonly struct ResultSetClaim(ResultSetShape shape, int setNumber)
{
    public readonly ResultSetShape Shape = shape;
    public readonly int SetNumber = setNumber;
}

/// <summary>
/// One result set's declared column shape. The parallel arrays are ordered by
/// column: <see cref="BareTypeNames"/> and <see cref="TypeNames"/> hold the
/// canonical declared type name undecorated (Msg 11538) and with its width
/// trailer (Msg 8114), <see cref="MaxLengths"/> carries the bounded-string / varbinary
/// width the CAST path enforces, and <see cref="Nullability"/> is
/// <see langword="true"/> for a nullable column (the default when the
/// declaration says neither <c>NULL</c> nor <c>NOT NULL</c>).
/// </summary>
internal sealed class ResultSetShape(
    string[] names,
    SqlType[] types,
    string[] bareTypeNames,
    string[] typeNames,
    int?[] maxLengths,
    bool[] nullability,
    bool[] reportsNumeric)
{
    public readonly string[] Names = names;
    public readonly SqlType[] Types = types;
    public readonly string[] BareTypeNames = bareTypeNames;
    public readonly string[] TypeNames = typeNames;
    public readonly int?[] MaxLengths = maxLengths;
    public readonly bool[] Nullability = nullability;
    public readonly bool[] ReportsNumeric = reportsNumeric;
}
