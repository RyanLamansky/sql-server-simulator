using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
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
    private static ResultSetsContract? ParseExecuteOptions(BatchContext batch, bool insertExecSource)
    {
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
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Recompile })
            {
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
    /// <c>(column_name data_type [COLLATE …] [NULL | NOT NULL], …)</c> list.
    /// The <c>AS OBJECT</c> / <c>AS TYPE</c> / <c>AS FOR XML</c> shorthands
    /// real also accepts here aren't built yet.
    /// </summary>
    private static ResultSetShape ParseResultSetShape(BatchContext batch)
    {
        var context = batch.Parser;
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            throw new NotSupportedException("WITH RESULT SETS: the AS OBJECT / AS TYPE / AS FOR XML result-set definition forms aren't modeled; use the explicit column list.");
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
                context.MoveNextOptional();
            }
            var (resolvedType, resolvedMaxLength, _) = ResolveTypeReference(
                batch, qualifiedTypeName, typeLeaf, declaredMaxLength, declaredScale,
                index: types.Count + 1, columnName: columnName.Value);

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
    /// Renders a declared type the way Msg 8114 spells it — the canonical
    /// type name plus the declaration's own width trailer.
    /// </summary>
    private static string FormatDeclaredTypeName(string typeWord, int? declaredMaxLength, int? declaredScale) =>
        declaredMaxLength is not { } length ? typeWord
        : declaredScale is { } scale ? $"{typeWord}({length},{scale})"
        : length == SqlType.MaxLengthSentinel ? $"{typeWord}(max)"
        : $"{typeWord}({length})";

    /// <summary>
    /// Layers an <c>EXEC … WITH RESULT SETS</c> contract over the outcomes an
    /// invoked module produced: each result set is renamed / retyped to its
    /// declaration, and the declared and actual set counts are reconciled.
    /// Non-result outcomes (row counts, dynamic-SQL scope markers) pass
    /// through and don't count toward the contract — probe-confirmed: a
    /// procedure whose only statement is an INSERT satisfies
    /// <c>RESULT SETS NONE</c>.
    /// </summary>
    /// <remarks>
    /// The per-row conversion / NOT NULL checks fire as the rows are drained
    /// rather than up front, so a row-level violation surfaces mid-stream the
    /// way real's does. A <em>set</em>-level violation doesn't: this iterator
    /// raises after yielding the sets that matched, but the dispatch loop
    /// materializes a statement's outcomes before yielding any of them, so the
    /// whole EXECUTE fails where real would have streamed the earlier sets
    /// first.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> ApplyResultSetsContract(
        IEnumerable<SimulatedStatementOutcome> outcomes,
        ResultSetsContract contract)
    {
        var sent = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome is not SimulatedQueryResult query)
            {
                yield return outcome;
                continue;
            }
            if (contract.Shapes is not { } shapes)
            {
                yield return query;
                continue;
            }
            yield return sent == shapes.Length
                ? throw AttributeToOrigin(SimulatedSqlException.ResultSetsTooManySent(shapes.Length), query)
                : ProjectResultSet(query, shapes[sent], ++sent);
        }
        if (contract.Shapes is { } declared && sent < declared.Length)
            throw SimulatedSqlException.ResultSetsTooFewSent(declared.Length, sent);
    }

    /// <summary>
    /// Re-labels one result set to its declared shape. The column count and
    /// per-column convertibility are checked here (before any row is read,
    /// matching real); the values themselves convert lazily in
    /// <see cref="ConvertResultSetRows"/>.
    /// </summary>
    private static SimulatedSqlResultSet ProjectResultSet(SimulatedQueryResult source, ResultSetShape shape, int setNumber)
    {
        var sourceSchema = source.Schema;
        if (sourceSchema.Length != shape.Types.Length)
        {
            throw AttributeToOrigin(
                SimulatedSqlException.ResultSetsColumnCountMismatch(shape.Types.Length, setNumber, sourceSchema.Length), source);
        }
        for (var i = 0; i < sourceSchema.Length; i++)
        {
            if (!IsImplicitlyConvertible(sourceSchema[i], shape.Types[i]))
            {
                throw AttributeToOrigin(
                    SimulatedSqlException.ResultSetsNoConversion(shape.BareTypeNames[i], i + 1, setNumber, sourceSchema[i].SqlServerName),
                    source);
            }
        }
        return new SimulatedSqlResultSet(shape.Types, shape.Names, ConvertResultSetRows(source, shape, setNumber))
        {
            ClientTextSize = source.ClientTextSize,
            ColumnNullability = shape.Nullability,
            ColumnReportsNumeric = shape.ReportsNumeric,
            OriginLine = source.OriginLine,
            OriginProcedure = source.OriginProcedure,
        };
    }

    /// <summary>
    /// Streams the source rows through the declared column types. Conversion
    /// reuses the CAST value path (so the varchar asterisk fallback, silent
    /// truncation and rounding all behave as they do in a CAST) but reports
    /// every failure as Msg 8114 naming both decorated type names, which is
    /// what real does here regardless of which conversion rule was violated.
    /// </summary>
    private static IEnumerable<SqlValue[]> ConvertResultSetRows(SimulatedQueryResult source, ResultSetShape shape, int setNumber)
    {
        using var cursor = source.CreateCursor();
        while (cursor.MoveNext())
        {
            var row = new SqlValue[shape.Types.Length];
            for (var i = 0; i < row.Length; i++)
            {
                var value = cursor[i];
                if (value.IsNull)
                {
                    if (!shape.Nullability[i])
                        throw AttributeToOrigin(SimulatedSqlException.ResultSetsNullInNonNullableColumn(i + 1, setNumber), source);
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
                        SimulatedSqlException.ConvertingDataTypeError(value.Type.ToString()!, shape.TypeNames[i]), source);
                }
            }
            yield return row;
        }
    }

    /// <summary>
    /// Stamps the producing statement's line and procedure onto a contract
    /// violation so it reads as raised where the rows came from — real
    /// attributes Msg 11535 / 11537 / 11538 / 11553 and the conversion
    /// failure to the module's own SELECT, not to the EXECUTE statement
    /// (Msg 11536 is the exception and is left for the EXECUTE's own frame).
    /// A result produced outside a dispatch frame carries no origin and falls
    /// back to that same default.
    /// </summary>
    private static SimulatedSqlException AttributeToOrigin(SimulatedSqlException exception, SimulatedQueryResult source)
    {
        if (source.OriginLine != 0)
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
