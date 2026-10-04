using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs <c>DBCC CHECKIDENT ( table [, NORESEED | RESEED [, value]] )
    /// [WITH NO_INFOMSGS]</c>, peeking past <c>DBCC</c> and restoring the cursor
    /// on any other subcommand. Probed 2026-09-24 against SQL Server 2025:
    /// <list type="bullet">
    /// <item>The bare and <c>NORESEED</c> forms report the current identity
    /// value and the identity column's maximum (its minimum for a negative
    /// increment) as the class-0 Msg 7998, and the bare and value-less
    /// <c>RESEED</c> forms then raise the identity to that maximum when it has
    /// fallen behind.</item>
    /// <item><c>RESEED, value</c> reports the value it replaces (Msg 7989) and
    /// sets it — see <see cref="IdentityState.Reseed"/> for the next row's
    /// value — or, past the column type's range, follows the report with
    /// Msg 2560.</item>
    /// <item>Msg 2528 closes every successful run; <c>WITH NO_INFOMSGS</c>
    /// silences the informational messages but not an error.</item>
    /// </list>
    /// </summary>
    private static bool TryParseCheckIdent(ParserContext context, BatchContext batch, out SimulatedStatementOutcome? outcome)
    {
        outcome = null;
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.CheckIdent })
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // The table is a string holding a name, or the name written bare; a
        // variable, or any argument another type of value, is Msg 2560 naming
        // its position (probed 2026-10-04 against SQL Server 2025).
        context.MoveNextRequired();
        string writtenName;
        MultiPartName tableName;
        SimulatedSqlException? refusal = null;
        if (context.Token is Literal { Value: { IsNull: false } text } && SqlType.IsStringCategory(text.Type))
        {
            writtenName = text.AsString;
            tableName = Parser.Expressions.ObjectId.TryParseObjectName(writtenName, out var parsed) ? parsed : default;
            context.MoveNextRequired();
        }
        else if (context.Token is AtPrefixedString or Literal or Numeric)
        {
            writtenName = "";
            tableName = default;
            refusal = SimulatedSqlException.DbccParameterIsIncorrect(1);
            context.MoveNextRequired();
        }
        else
        {
            tableName = BatchContext.ParseObjectName(context);
            writtenName = tableName.ToString();
            context.MoveNextRequired();
        }

        var reseed = true;
        Int128? newValue = null;
        if (context.Token is Operator { Character: ',' })
        {
            switch ((context.GetNextRequired() as UnquotedString)?.ContextualKeyword)
            {
                case ContextualKeyword.NoReseed:
                    reseed = false;
                    context.MoveNextRequired();
                    break;
                case ContextualKeyword.Reseed:
                    if (context.GetNextRequired() is Operator { Character: ',' })
                    {
                        context.MoveNextRequired();
                        if (context.Token is Literal)
                        {
                            refusal ??= SimulatedSqlException.DbccParameterIsIncorrect(3);
                            context.MoveNextRequired();
                        }
                        else
                        {
                            newValue = ParseReseedValue(context, batch);
                        }
                    }
                    break;
                default:
                    // Any other word is Msg 2560 at the second parameter
                    // (probed 2026-10-04 against SQL Server 2025).
                    if (context.Token is not Name)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    refusal ??= SimulatedSqlException.DbccParameterIsIncorrect(2, 6);
                    context.MoveNextRequired();
                    break;
            }
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // NO_INFOMSGS is the one option it takes; any other is Msg 2532.
        var informational = true;
        var afterParen = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            while (true)
            {
                var option = context.GetNextRequired<Name>();
                if (!BuiltInToken.Equals(option.Value, "NO_INFOMSGS"))
                    refusal ??= SimulatedSqlException.DbccWithOptionNotValid();
                informational = false;
                var afterOption = context.SaveCheckpoint();
                if (context.MoveNext() && context.Token is Operator { Character: ',' })
                    continue;
                context.RestoreCheckpoint(afterOption);
                break;
            }
        }
        else
        {
            context.RestoreCheckpoint(afterParen);
        }

        if (batch.IsSkipping)
            return true;
        if (refusal is not null)
            throw refusal;

        if (tableName.Count == 0 || !batch.TryResolveTable(tableName, out var table))
        {
            // A view or another object by the name is Msg 5239 (probed
            // 2026-10-04 against SQL Server 2025).
            if (tableName.Count > 0 && batch.TryResolveSchema(tableName, out var schema) && schema.TryFindInSharedNamespace(tableName.Leaf, out var other))
                throw SimulatedSqlException.DbccObjectTypeNotSupported(other.ObjectId, other.Name);
            throw SimulatedSqlException.CannotFindTableOrObject(writtenName);
        }
        // It takes the table's control — ownership, db_owner or db_ddladmin —
        // ALTER not sufficing, else Msg 2557 state 5 (probed 2026-10-04).
        var database = batch.DatabaseFor(table);
        if (!table.IsTableVariable && table.Name is not ['#', ..] && !PermissionEnforcement.HasObjectControl(batch, database, table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.DbccObjectPermissionDenied(batch.Connection.Security.Effective.DatabasePrincipalName, "CHECKIDENT", table.Name, 5);
        var ordinal = Array.FindIndex(table.Columns, column => column.Identity is not null);
        if (ordinal < 0)
            throw SimulatedSqlException.NoIdentityColumn(table.Name);
        var column = table.Columns[ordinal];
        var identity = column.Identity!;

        var reported = identity.Reported;
        if (newValue is { } value)
        {
            if (informational)
                batch.AppendInfoError(@class: 0, state: 3, number: 7989, message: $"Checking identity information: current identity value '{Render(reported)}'.");
            if (!IdentityState.Fits(value, column.Type))
                throw SimulatedSqlException.DbccParameterIsIncorrect(3, 14);
            Reseed(value);
        }
        else
        {
            var extreme = ColumnExtreme(table, ordinal, highest: identity.Increment >= 0);
            if (informational)
                batch.AppendInfoError(@class: 0, state: 3, number: 7998, message: $"Checking identity information: current identity value '{Render(reported)}', current column value '{Render(extreme)}'.");
            if (reseed && extreme is { } bound && (reported is not { } current || (identity.Increment >= 0 ? current < bound : current > bound)))
                Reseed(bound);
        }

        if (informational)
            batch.AppendInfoError(@class: 0, state: 1, number: 2528, message: "DBCC execution completed. If DBCC printed error messages, contact your system administrator.");
        return true;

        void Reseed(Int128 value)
        {
            context.Connection.CurrentTransaction?.UndoLog.RecordIdentityReseed(identity, identity.ReseedSnapshot());
            identity.Reseed(value);
        }

        static string Render(Int128? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
    }

    /// <summary>A reseed value: a number, signed or not, or a variable holding one.</summary>
    private static Int128 ParseReseedValue(ParserContext context, BatchContext batch)
    {
        var negate = false;
        if (context.Token is Operator { Character: '-' or '+' } sign)
        {
            negate = sign.Character == '-';
            context.MoveNextRequired();
        }
        var value = context.Token switch
        {
            Numeric numeric => numeric.Value,
            AtPrefixedString variable => batch.GetVariableSlot(variable.Value).Value,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextRequired();
        var number = value.IsNull ? 0 : IdentityState.FromSqlValue(value);
        return negate ? -number : number;
    }

    /// <summary>The identity column's largest (or smallest) stored value, or null over no rows.</summary>
    private static Int128? ColumnExtreme(HeapTable table, int ordinal, bool highest)
    {
        Int128? extreme = null;
        var storageOrdinal = table.StorageOrdinals[ordinal];
        foreach (var row in table.Heap.EnumerateRows())
        {
            var value = RowDecoder.DecodeColumn(table.StoredColumns, row, storageOrdinal, table.Heap);
            if (value.IsNull)
                continue;
            var number = IdentityState.FromSqlValue(value);
            if (extreme is not { } current || (highest ? number > current : number < current))
                extreme = number;
        }
        return extreme;
    }
}
