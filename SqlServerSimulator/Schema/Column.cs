namespace SqlServerSimulator.Schema
{
    class Column
    {
        public Column(string name, string type)
        {
            this.Name = name;
            this.Type = type;
        }

        public string Name;

        public string Type;

#if DEBUG
        public override string ToString() => $"{Name} {Type}";
#endif
    }
}
