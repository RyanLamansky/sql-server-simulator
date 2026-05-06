using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>CHECKSUM_AGG(expr)</c>: an order-independent XOR-fold of the
/// per-row checksums (SQL Server's documented algorithm rotates and XORs).
/// NULL operands are skipped. Empty input → 0 (per SQL Server probe).
/// Result type is <see cref="SqlType.Int32"/>. Implementation here is a
/// simple integer-XOR rotate over <see cref="int"/> hashes of the operand
/// values; bit-for-bit match with SQL Server's CHECKSUM is not guaranteed
/// (different domains require slightly different bit-twiddles), but the
/// "order-independent, NULL-skipping, integer hash" contract is preserved.
/// </summary>
internal sealed class ChecksumAggAggregator : Aggregator
{
    private int folded;

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        // Plain XOR keeps the fold commutative — same multiset of inputs
        // produces the same checksum regardless of arrival order, matching
        // SQL Server's documented order-independence guarantee.
        this.folded ^= value.GetHashCode();
    }

    public override SqlValue Result() => SqlValue.FromInt32(this.folded);
}
