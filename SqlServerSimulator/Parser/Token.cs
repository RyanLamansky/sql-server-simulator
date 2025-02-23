namespace SqlServerSimulator.Parser;

abstract class Token
{
    private protected Token()
    {
    }

#if DEBUG
    public abstract override string ToString();
#endif
}
