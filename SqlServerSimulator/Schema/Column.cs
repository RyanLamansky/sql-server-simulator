namespace SqlServerSimulator.Schema;

sealed class Column(string name, DataType type, bool nullable)
{
    public string Name = name;

    public DataType Type = type;

    public bool Nullable = nullable;

#if DEBUG
    public override string ToString() => $"{Name} {Type}";
#endif
}
