namespace SqlServerSimulator.Storage;

/// <summary>
/// A one-entry memo over <see cref="SqlValue.CoerceTo"/> for a <em>string</em>
/// source, held by the expression node that does the coercion.
/// </summary>
/// <remarks>
/// <para>
/// Comparing a column against a written date is the shape this exists for:
/// <c>WHERE OrderDate &gt;= '2015-01-01'</c> promotes to <c>date</c>, so the
/// literal was parsed — <c>DateTimeParse</c>, the full style grammar — once per
/// scanned row. On a 73k-row filtered join feeding a <c>SELECT … INTO</c> that
/// parse was <strong>65%</strong> of the statement's CPU, more than the join,
/// the projection and the write together.
/// </para>
/// <para>
/// The memo keys on the source string's <em>reference</em>, its declared type
/// and the target type, all by reference. That is deliberately stricter than
/// value equality: a string is immutable, so an identical reference under an
/// identical (source type, target type) pair is the same call to a function of
/// nothing else, and two equal-but-distinct strings simply miss and re-coerce.
/// A literal or a parameter hands out the same instance for every row, which is
/// what makes one entry enough; a genuinely per-row string misses every time and
/// costs one reference compare over the old behavior.
/// </para>
/// <para>
/// Only a string source crossing <em>out</em> of the string category is
/// admitted. A string source is what makes the key sound — the payload behind a
/// non-string value can be a <c>byte[]</c>, whose contents an identity test
/// wouldn't cover — and a non-string target is what makes the entry worth
/// allocating: those are the parses (the date/time grammar, decimal, GUID),
/// while a string-to-string promotion re-tags the same instance and would pay an
/// entry per row for nothing on an operand that varies. A NULL is passed
/// straight through — <see cref="SqlValue.CoerceTo"/> re-types it without work.
/// </para>
/// <para>
/// A failing coercion is never memoized, so an error surfaces from the same row
/// it always did, as many times as it always did.
/// </para>
/// <para>
/// A cached plan is shared across sessions, so the memo is read and written
/// concurrently. The entry is immutable and published with
/// <see cref="Volatile"/>; two threads racing to fill it compute the same value
/// and one wins, which is why no lock is taken.
/// </para>
/// </remarks>
internal sealed class StringCoercionMemo
{
    private Entry? memo;

    public SqlValue Coerce(SqlValue value, SqlType target)
    {
        if (value.IsNull
            || value.Type.Category != SqlTypeCategory.String
            || target.Category == SqlTypeCategory.String)
        {
            return value.CoerceTo(target);
        }

        var text = value.AsString;
        var current = Volatile.Read(ref this.memo);
        if (current is not null
            && ReferenceEquals(current.Target, target)
            && ReferenceEquals(current.SourceType, value.Type)
            && ReferenceEquals(current.Source, text))
        {
            return current.Result;
        }

        var coerced = value.CoerceTo(target);
        Volatile.Write(ref this.memo, new Entry(value.Type, text, target, coerced));
        return coerced;
    }

    private sealed class Entry(SqlType sourceType, string source, SqlType target, SqlValue result)
    {
        public readonly string Source = source;
        public readonly SqlType SourceType = sourceType;
        public readonly SqlType Target = target;
        public readonly SqlValue Result = result;
    }
}
