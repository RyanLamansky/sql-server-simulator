using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CERTENCODED(cert_id)</c> and
/// <c>CERTPRIVATEKEY(cert_id, N'encryption_password' [, N'decryption_password'])</c>:
/// return the public certificate / private key bytes as <c>varbinary(max)</c>.
/// The simulator models no certificate store, so both return NULL — the exact
/// answer real SQL Server gives for a certificate id that doesn't exist
/// (probe-confirmed against SQL Server 2025). Argument-count violations raise
/// the same Msg 174 (<c>CERTENCODED</c>) / Msg 189 (<c>CERTPRIVATEKEY</c>) real
/// does. References:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/certencoded-transact-sql,
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/certprivatekey-transact-sql
/// </summary>
internal sealed class CertificateFunction : Expression
{
    private readonly Expression certId;
    private readonly Expression? password;
    private readonly Expression? decryptionPassword;

    public CertificateFunction(ParserContext context, bool isPrivateKey)
    {
        if (isPrivateKey)
        {
            if (context.Token is Tokens.Operator { Character: ')' })
                throw SimulatedSqlException.FunctionArgumentCountRange("CertPrivateKey", 2, 3);
            this.certId = Parse(context);
            if (context.Token is not Tokens.Operator { Character: ',' })
                throw SimulatedSqlException.FunctionArgumentCountRange("CertPrivateKey", 2, 3);
            this.password = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Tokens.Operator { Character: ',' })
                this.decryptionPassword = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is not Tokens.Operator { Character: ')' })
                throw SimulatedSqlException.FunctionArgumentCountRange("CertPrivateKey", 2, 3);
        }
        else
        {
            if (context.Token is Tokens.Operator { Character: ')' })
                throw SimulatedSqlException.FunctionRequiresNArguments("CertEncoded", 1);
            this.certId = Parse(context);
            if (context.Token is not Tokens.Operator { Character: ')' })
                throw SimulatedSqlException.FunctionRequiresNArguments("CertEncoded", 1);
        }
    }

    public override SqlValue Run(RuntimeContext runtime) => SqlValue.Null(SqlType.VarbinaryMax);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.VarbinaryMax;

    internal override string DebugDisplay() => this.password is null
        ? $"CERTENCODED({this.certId.DebugDisplay()})"
        : $"CERTPRIVATEKEY({this.certId.DebugDisplay()}, …)";

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit)
    {
        this.certId.VisitColumnReferences(visit);
        this.password?.VisitColumnReferences(visit);
        this.decryptionPassword?.VisitColumnReferences(visit);
    }
}
