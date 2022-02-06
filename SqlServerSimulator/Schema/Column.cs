namespace SqlServerSimulator.Schema;

class Column
{
    public Column(string name, string type, bool nullable)
    {
        this.Name = name;
        this.Type = type;
        this.Nullable = nullable;
    }

    public string Name;

    public string Type;

    public bool Nullable;

#if DEBUG
    public override string ToString() => $"{Name} {Type}";
#endif
}
