using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

abstract class Name : StringToken
{
    private protected Name(StringBuilder buffer)
        : base(buffer)
    {
    }
}
