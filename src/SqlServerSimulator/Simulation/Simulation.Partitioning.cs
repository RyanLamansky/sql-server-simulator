using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// CREATE / ALTER / DROP PARTITION FUNCTION and PARTITION SCHEME. What each
// statement checks, in which order, and which failures still spend an id was
// probed 2026-09-27 against SQL Server 2025; see docs/claude/partitioning.md.
partial class Simulation
{
    /// <summary>Partition function past this many boundaries is Msg 7719 (15,000 partitions).</summary>
    private const int MaxPartitionBoundaries = 14999;

    /// <summary>
    /// Routes <c>CREATE PARTITION FUNCTION</c> / <c>CREATE PARTITION SCHEME</c>.
    /// Entered with the cursor on <c>PARTITION</c>.
    /// </summary>
    private static bool TryParseCreatePartition(ParserContext context) =>
        context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Function } => TryParseCreatePartitionFunction(context),
            Name word when word.Value.Equals("SCHEME", StringComparison.OrdinalIgnoreCase) => TryParseCreatePartitionScheme(context),
            _ => false,
        };

    /// <summary>Routes <c>ALTER PARTITION FUNCTION</c> / <c>ALTER PARTITION SCHEME</c>, cursor on <c>PARTITION</c>.</summary>
    private static bool TryParseAlterPartition(ParserContext context) =>
        context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Function } => TryParseAlterPartitionFunction(context),
            Name word when word.Value.Equals("SCHEME", StringComparison.OrdinalIgnoreCase) => TryParseAlterPartitionScheme(context),
            _ => false,
        };

    /// <summary>Routes <c>DROP PARTITION FUNCTION</c> / <c>DROP PARTITION SCHEME</c>, cursor on <c>PARTITION</c>.</summary>
    private static bool TryParseDropPartition(ParserContext context)
    {
        var isFunction = context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Function } => true,
            Name word when word.Value.Equals("SCHEME", StringComparison.OrdinalIgnoreCase) => false,
            _ => (bool?)null,
        };
        if (isFunction is null)
            return false;
        var name = ReadPartitionObjectName(context);
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, database))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (isFunction.Value)
        {
            if (!database.PartitionFunctions.TryGetValue(name, out var function))
                throw SimulatedSqlException.PartitionObjectNotFound("drop", "function", name);
            if (database.PartitionSchemes.EnumerateValues().Any(scheme => ReferenceEquals(scheme.Function, function)))
                throw SimulatedSqlException.PartitionFunctionInUse(function.Name);
            if (SchemaBinding.FindPartitionFunctionReference(database, function.Name) is { } module)
                throw SimulatedSqlException.PartitionFunctionReferenced(name, module.Name);
            _ = database.PartitionFunctions.TryRemove(function.Name, out _);
            RecordSlotUndo<PartitionFunction>(context, database.PartitionFunctions, function.Name, function);
            RecordDdlEvent(context, "DROP_PARTITION_FUNCTION", null, function.Name, "PARTITION FUNCTION");
        }
        else
        {
            if (!database.PartitionSchemes.TryGetValue(name, out var scheme))
                throw SimulatedSqlException.PartitionObjectNotFound("drop", "scheme", name);
            if (PartitionSchemeIsInUse(database, scheme))
                throw SimulatedSqlException.PartitionSchemeInUse(scheme.Name);
            _ = database.PartitionSchemes.TryRemove(scheme.Name, out _);
            RecordSlotUndo<PartitionScheme>(context, database.PartitionSchemes, scheme.Name, scheme);
            RecordDdlEvent(context, "DROP_PARTITION_SCHEME", null, scheme.Name, "PARTITION SCHEME");
        }
        return true;
    }

    /// <summary>
    /// Reads the one-part name a partition function or scheme statement names,
    /// the cursor moving onto it. A qualified name is Msg 102 at the dot, as a
    /// second name in a list is at the comma, which is what the caller's next
    /// token check then reports.
    /// </summary>
    private static string ReadPartitionObjectName(ParserContext context) =>
        context.GetNextRequired() switch
        {
            Name name => name.Value,
            ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

    /// <summary>
    /// <c>CREATE PARTITION FUNCTION name ( type ) AS RANGE [ LEFT | RIGHT ] FOR
    /// VALUES ( [ value [, …] ] )</c>, cursor on <c>FUNCTION</c>. A boundary is
    /// any constant expression, converted to the parameter type as an
    /// assignment converts; the list is sorted, with Msg 7709 when it wasn't
    /// written that way.
    /// </summary>
    private static bool TryParseCreatePartitionFunction(ParserContext context)
    {
        var name = ReadPartitionObjectName(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var (qualifiedTypeName, typeName) = TypeNameSynonyms.ReadTypeName(context);
        context.MoveNextRequired();
        var (declaredMaxLength, declaredScale) = ReadPartitionTypeArguments(context, typeName);
        string? collationName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Collate })
        {
            collationName = context.GetNextRequired() is Name collation
                ? Parser.Expressions.CollateExpression.ResolvePseudoCollationName(collation.Value, context.Batch)
                : throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        var multipleParameters = context.Token is Operator { Character: ',' };
        if (multipleParameters)
        {
            // The rest of the list only has to be well formed.
            while (context.Token is not Operator { Character: ')' })
                context.MoveNextRequired();
        }
        else if (context.Token is not Operator { Character: ')' })
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Range })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var boundaryOnRight = false;
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Left }:
                context.MoveNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.Right }:
                boundaryOnRight = true;
                context.MoveNextRequired();
                break;
        }
        if (context.Token is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Values })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var values = new List<Expression>();
        if (context.GetNextRequired() is not Operator { Character: ')' })
        {
            while (true)
            {
                values.Add(Expression.Parse(context));
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
        }
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, database))
            throw SimulatedSqlException.UserDoesNotHavePermission();

        // Every attempt past the parse spends an id, whatever fails next.
        var functionId = database.AllocatePartitionFunctionId();
        if (multipleParameters)
            throw SimulatedSqlException.PartitionFunctionMultipleParameters();
        var (parameterType, maxLength) = ResolvePartitionParameterType(context, qualifiedTypeName, typeName, declaredMaxLength, declaredScale, collationName);

        var converted = new SqlValue[values.Count];
        for (var i = 0; i < values.Count; i++)
            converted[i] = ConvertPartitionBoundary(context.Batch, values[i], parameterType, maxLength, i + 1);
        var order = new int[converted.Length];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) => PartitionFunction.Compare(converted[a], converted[b]) is var comparison and not 0 ? comparison : a.CompareTo(b));
        for (var i = 1; i < order.Length; i++)
        {
            if (PartitionFunction.Compare(converted[order[i - 1]], converted[order[i]]) == 0)
                throw SimulatedSqlException.PartitionDuplicateBoundaries(Math.Min(order[i - 1], order[i]) + 1, Math.Max(order[i - 1], order[i]) + 1);
        }
        if (converted.Length > MaxPartitionBoundaries)
            throw SimulatedSqlException.PartitionFunctionTooManyPartitions();
        if (database.PartitionFunctions.ContainsKey(name))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, state: 58);
        for (var i = 0; i < order.Length; i++)
        {
            if (order[i] != i)
            {
                context.Batch.AppendInfoError(0, 1, 7709, SimulatedSqlException.PartitionValuesNotSortedMessage(name));
                break;
            }
        }

        var function = new PartitionFunction(
            name, functionId, parameterType, maxLength, boundaryOnRight,
            Array.ConvertAll(order, index => converted[index]), context.Batch.CurrentStatement.UtcNow);
        if (!database.PartitionFunctions.TryAdd(name, function))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, state: 58);
        RecordSlotUndo<PartitionFunction>(context, database.PartitionFunctions, name, null);
        RecordDdlEvent(context, "CREATE_PARTITION_FUNCTION", null, name, "PARTITION FUNCTION");
        return true;
    }

    /// <summary>
    /// Reads a partition function parameter type's optional <c>(n [, s])</c>
    /// or <c>(max)</c>, the cursor on the token after the type name and left on
    /// the token past the arguments.
    /// </summary>
    private static (int? MaxLength, int? Scale) ReadPartitionTypeArguments(ParserContext context, Name typeName)
    {
        if (context.Token is not Operator { Character: '(' })
            return (null, null);
        var lengthToken = context.GetNextRequired();
        int? declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
            ? numericValue.AsInt32
            : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                ? SqlType.MaxLengthSentinel
                : throw SimulatedSqlException.SyntaxErrorNear(context);
        int? declaredScale = null;
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
        return (declaredMaxLength, declaredScale);
    }

    /// <summary>
    /// Resolves a partition function's parameter type, refusing what real
    /// refuses with Msg 7704: the LOB, MAX, <c>timestamp</c>, <c>json</c> and
    /// <c>vector</c> types at state 1, a name that isn't a type at state 2, and
    /// the CLR types and alias types at state 3. A string type takes the
    /// written collation, else the database's.
    /// </summary>
    private static (SqlType Type, int? MaxLength) ResolvePartitionParameterType(
        ParserContext context, MultiPartName qualifiedTypeName, Name typeName, int? declaredMaxLength, int? declaredScale, string? collationName)
    {
        SqlType type;
        int? maxLength;
        AliasType? alias;
        try
        {
            (type, maxLength, _, alias) = ResolveTypeReference(
                context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale, index: 1, TypeSpecSite.Scalar, columnName: typeName.Value);
        }
        catch (SimulatedSqlException error) when (error.Number == 2715)
        {
            throw SimulatedSqlException.PartitionFunctionInvalidType(typeName.Value, state: 2);
        }
        if (alias is not null || type is SpatialSqlType or HierarchyIdSqlType)
            throw SimulatedSqlException.PartitionFunctionInvalidType(alias?.Name ?? type.SqlServerName, state: 3);
        if (type is TextSqlType or XmlSqlType or JsonSqlType or VectorSqlType or RowVersionSqlType
            || type == SqlType.NText || type == SqlType.Image || maxLength == SqlType.MaxLengthSentinel)
        {
            throw SimulatedSqlException.PartitionFunctionInvalidType(type.ToString()!.Replace("(MAX)", "(max)", StringComparison.Ordinal), state: 1);
        }
        if (type.Category == SqlTypeCategory.String)
        {
            var collation = (collationName is not null ? Collation.TryGet(collationName) : null) ?? context.CurrentDatabase.Collation;
            type = type.WithCollation(collation, Coercibility.Implicit);
        }
        return (type, maxLength);
    }

    /// <summary>
    /// Evaluates a boundary expression and converts it to the parameter type
    /// as an assignment would. Anything the conversion refuses — an illegal
    /// pair, a failed parse, an overflow — is Msg 7705 at the value's written
    /// ordinal, and a string or binary longer than the parameter is Msg 7720.
    /// </summary>
    private static SqlValue ConvertPartitionBoundary(BatchContext batch, Expression expression, SqlType type, int? maxLength, int ordinal)
    {
        var raw = expression.Run(new RuntimeContext(NoColumnResolver, batch));
        if (raw.IsNull)
            return SqlValue.Null(type);
        if (maxLength is > 0 && raw.Type.Category == type.Category)
        {
            var length = type.Category switch
            {
                SqlTypeCategory.String => raw.AsString.Length,
                _ when raw.Type is VarbinarySqlType or BinarySqlType => raw.AsBytes.Length,
                _ => 0,
            };
            if (length > maxLength)
                throw SimulatedSqlException.PartitionRangeValueTruncated(ordinal);
        }
        try
        {
            AssignmentRules.RequireAssignable(expression, raw.Type, type);
            return raw.CoerceTo(type);
        }
        catch (Exception error) when (error is SimulatedSqlException or OverflowException)
        {
            throw SimulatedSqlException.PartitionRangeValueNotConvertible(ordinal);
        }
    }

    /// <summary>
    /// <c>ALTER PARTITION FUNCTION name() { SPLIT | MERGE } RANGE ( value )</c>,
    /// cursor on <c>FUNCTION</c>. A split places the new partition — the one
    /// holding the new boundary value — on each dependent scheme's
    /// <c>NEXT USED</c> filegroup, and a merge drops the partition holding
    /// the merged boundary value along with its filegroup slot.
    /// </summary>
    private static bool TryParseAlterPartitionFunction(ParserContext context)
    {
        var name = ReadPartitionObjectName(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var split = context.GetNextRequired() switch
        {
            Name word when word.Value.Equals("SPLIT", StringComparison.OrdinalIgnoreCase) => true,
            ReservedKeyword { Keyword: Keyword.Merge } => false,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Range })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var value = Expression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, database))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (!database.PartitionFunctions.TryGetValue(name, out var function))
            throw SimulatedSqlException.PartitionObjectNotFound("alter", "function", name);
        var boundary = ConvertPartitionBoundary(context.Batch, value, function.ParameterType, function.DeclaredMaxLength, 1);
        var boundaries = function.Boundaries;
        var position = Array.FindIndex(boundaries, existing => PartitionFunction.Compare(existing, boundary) >= 0);
        if (position < 0)
            position = boundaries.Length;
        var found = position < boundaries.Length && PartitionFunction.Compare(boundaries[position], boundary) == 0;
        var schemes = database.PartitionSchemes.EnumerateValues()
            .Where(scheme => ReferenceEquals(scheme.Function, function))
            .OrderBy(scheme => scheme.DataSpaceId)
            .ToArray();
        if (split)
        {
            if (found)
                throw SimulatedSqlException.PartitionSplitDuplicate(position + 1);
            if (boundaries.Length >= MaxPartitionBoundaries)
                throw SimulatedSqlException.PartitionFunctionTooManyPartitions();
            if (Array.Find(schemes, scheme => scheme.NextUsed is null) is { } lacking)
                throw SimulatedSqlException.NoNextUsedFilegroup(lacking.Name);
        }
        else if (boundaries.Length == 0)
        {
            throw SimulatedSqlException.PartitionFunctionZeroPartitions();
        }
        else if (!found)
        {
            throw SimulatedSqlException.PartitionRangeValueNotFound();
        }

        RecordPartitionFunctionUndo(context, function, schemes);
        // The partition holding the boundary value: the one it closes under
        // RANGE LEFT, the one it opens under RANGE RIGHT.
        var slot = function.BoundaryOnRight ? position + 1 : position;
        if (split)
        {
            function.Boundaries = [.. boundaries[..position], boundary, .. boundaries[position..]];
            foreach (var scheme in schemes)
            {
                scheme.Destinations.Insert(slot, scheme.NextUsed!.Value);
                scheme.NextUsed = null;
            }
        }
        else
        {
            function.Boundaries = [.. boundaries[..position], .. boundaries[(position + 1)..]];
            foreach (var scheme in schemes)
                scheme.Destinations.RemoveAt(slot);
        }
        function.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        RecordDdlEvent(context, "ALTER_PARTITION_FUNCTION", null, function.Name, "PARTITION FUNCTION");
        return true;
    }

    /// <summary>Logs how to put a partition function and its schemes back as they stand, for a rollback.</summary>
    private static void RecordPartitionFunctionUndo(ParserContext context, PartitionFunction function, PartitionScheme[] schemes)
    {
        if (context.Connection.CurrentTransaction is null)
            return;
        var boundaries = function.Boundaries;
        var modifyDate = function.ModifyDate;
        var schemeState = Array.ConvertAll(schemes, scheme => (scheme, Destinations: scheme.Destinations.ToArray(), scheme.NextUsed));
        RecordDdlUndo(context, () =>
        {
            function.Boundaries = boundaries;
            function.ModifyDate = modifyDate;
            foreach (var (scheme, destinations, nextUsed) in schemeState)
            {
                scheme.Destinations.Clear();
                scheme.Destinations.AddRange(destinations);
                scheme.NextUsed = nextUsed;
            }
        });
    }

    /// <summary>
    /// <c>CREATE PARTITION SCHEME name AS PARTITION function [ ALL ] TO ( filegroup [, …] )</c>,
    /// cursor on <c>SCHEME</c>. One filegroup per partition, and one more
    /// becomes the <c>NEXT USED</c> filegroup (Msg 7712); any past that are
    /// ignored (Msg 7713). The id is spent once the function resolves, the
    /// name is free among schemes and enough filegroups are listed.
    /// </summary>
    private static bool TryParseCreatePartitionScheme(ParserContext context)
    {
        var name = ReadPartitionObjectName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var functionName = ReadPartitionObjectName(context);
        var all = context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All };
        if (all)
            context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var filegroups = new List<string>();
        while (true)
        {
            context.MoveNextRequired();
            filegroups.Add(ReadFilegroupName(context));
            if (context.GetNextRequired() is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, database))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (!database.PartitionFunctions.TryGetValue(functionName, out var function))
            throw SimulatedSqlException.PartitionInvalidObjectName(functionName, state: 58);
        if (database.PartitionSchemes.ContainsKey(name))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, state: 58);
        var fanout = function.Fanout;
        if (!all && filegroups.Count < fanout)
            throw SimulatedSqlException.PartitionSchemeTooFewFilegroups(function.Name, name);
        var dataSpaceId = database.AllocatePartitionSchemeId();
        if (all && filegroups.Count > 1)
            throw SimulatedSqlException.PartitionSchemeAllWithSeveral();
        var filegroupIds = filegroups.ConvertAll(filegroup =>
            database.Filegroups.TryGetValue(filegroup, out var id) ? id : throw SimulatedSqlException.PartitionInvalidObjectName(filegroup, state: 58));
        if (database.Filegroups.ContainsKey(name))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, state: 58);

        List<int> destinations;
        int? nextUsed;
        if (all)
        {
            destinations = [.. Enumerable.Repeat(filegroupIds[0], fanout)];
            nextUsed = filegroupIds[0];
        }
        else
        {
            destinations = filegroupIds.GetRange(0, fanout);
            nextUsed = filegroupIds.Count > fanout ? filegroupIds[fanout] : null;
        }
        var scheme = new PartitionScheme(name, dataSpaceId, function, destinations, nextUsed);
        if (!database.PartitionSchemes.TryAdd(name, scheme))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, state: 58);
        RecordSlotUndo<PartitionScheme>(context, database.PartitionSchemes, name, null);
        if (nextUsed is { } nextUsedId)
        {
            // Named as the filegroup was created, not as written here.
            var nextUsedName = database.Filegroups.First(entry => entry.Value == nextUsedId).Key;
            context.Batch.AppendInfoError(0, 1, 7712, SimulatedSqlException.PartitionSchemeCreatedMessage(name, nextUsedName));
            if (!all && filegroups.Count > fanout + 1)
                context.Batch.AppendInfoError(0, 1, 7713, SimulatedSqlException.PartitionSchemeIgnoredFilegroupsMessage(filegroups.Count - fanout - 1));
        }
        RecordDdlEvent(context, "CREATE_PARTITION_SCHEME", null, name, "PARTITION SCHEME");
        return true;
    }

    /// <summary>
    /// A filegroup name in a scheme's list or <c>NEXT USED</c>: an identifier,
    /// bracketed or quoted, or a string literal; the bare reserved word
    /// <c>PRIMARY</c> is Msg 156.
    /// </summary>
    private static string ReadFilegroupName(ParserContext context) => context.Token switch
    {
        Name name => name.Value,
        Literal { Value: { IsNull: false } value } when value.Type.Category == SqlTypeCategory.String => value.AsString,
        ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// <c>ALTER PARTITION SCHEME name NEXT USED [ filegroup ]</c>, cursor on
    /// <c>SCHEME</c>: marks the filegroup the next split uses, or clears the
    /// mark when none is named — the class-0 Msg 7710 when there was none.
    /// </summary>
    private static bool TryParseAlterPartitionScheme(ParserContext context)
    {
        var name = ReadPartitionObjectName(context);
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Next })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name { Value: var usedWord } || !usedWord.Equals("USED", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        string? filegroup = null;
        if (context.GetNextOptional() is Name or Literal or ReservedKeyword { Keyword: Keyword.Primary }
            && !IsStatementBoundary(context.Token))
        {
            filegroup = ReadFilegroupName(context);
            context.MoveNextOptional();
        }
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, database))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (!database.PartitionSchemes.TryGetValue(name, out var scheme))
            throw SimulatedSqlException.PartitionObjectNotFound("alter", "scheme", name);
        int? nextUsed = null;
        if (filegroup is not null)
        {
            nextUsed = database.Filegroups.TryGetValue(filegroup, out var id)
                ? id
                : throw SimulatedSqlException.PartitionInvalidObjectName(filegroup, state: 61);
        }
        else if (scheme.NextUsed is null)
        {
            context.Batch.AppendInfoError(0, 1, 7710, SimulatedSqlException.NoNextUsedFilegroupMessage(scheme.Name));
        }
        var previous = scheme.NextUsed;
        RecordDdlUndo(context, () => scheme.NextUsed = previous);
        scheme.NextUsed = nextUsed;
        RecordDdlEvent(context, "ALTER_PARTITION_SCHEME", null, scheme.Name, "PARTITION SCHEME");
        return true;
    }

    /// <summary>Whether a table or index in <paramref name="database"/> is placed on <paramref name="scheme"/>.</summary>
    private static bool PartitionSchemeIsInUse(Database database, PartitionScheme scheme)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (ReferenceEquals(table.Partitioning?.Scheme, scheme)
                    || table.Indexes.Exists(index => ReferenceEquals(index.Partitioning?.Scheme, scheme))
                    || table.KeyConstraints.Exists(key => ReferenceEquals(key.Partitioning?.Scheme, scheme)))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
