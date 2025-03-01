namespace SqlServerSimulator.Parser;

abstract class Token
{
    private protected Token()
    {
    }

    // This is used for various error messages even though tokens are not directly accessible to user code.
    public abstract override string ToString();
}
