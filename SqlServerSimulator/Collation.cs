namespace SqlServerSimulator;

/// <summary>
/// The SQL Server equivalent to .NET's <see cref="IComparer{T}"/> for strings.
/// </summary>
internal abstract class Collation : IComparer<string>, IEqualityComparer<string>
{
    private protected Collation()
    {
    }

    public abstract string Name { get; }

    internal static readonly Collation Default = new SQL_Latin1_General_CP1_CI_AS();

    public abstract int Compare(string? x, string? y);

    public virtual bool Equals(string? x, string? y) => this.Compare(x, y) == 0;

    public abstract int GetHashCode(string obj);

    private sealed class SQL_Latin1_General_CP1_CI_AS : Collation
    {
        public override string Name => "SQL_Latin1_General_CP1_CI_AS";

        public override int Compare(string? x, string? y) => StringComparer.InvariantCultureIgnoreCase.Compare(x, y);

        public override int GetHashCode(string obj) => StringComparer.InvariantCultureIgnoreCase.GetHashCode(obj);
    }
}
