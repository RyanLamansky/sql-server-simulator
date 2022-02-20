namespace SqlServerSimulator;

internal sealed class SimulatedSqlError
{
    internal SimulatedSqlError(string message, int number, byte @class, byte state)
    {
        this.Message = message;
        this.Number = number;
        this.Class = @class;
        this.State = state;
    }

    public string Message { get; }

    public int Number { get; }

    public byte Class { get; }

    public byte State { get; }
}
