using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CERTENCODED(cert_id)</c> and
/// <c>CERTPRIVATEKEY(cert_id, N'encryption_password' [, N'decryption_password'])</c>:
/// return the public certificate / private key bytes as <c>varbinary(max)</c>.
/// For an id that names no certificate both return NULL, real's answer
/// (probe-confirmed against SQL Server 2025); the simulator's certificates
/// carry no X.509 encoding to export, so one that exists raises
/// <see cref="NotSupportedException"/>. Argument-count violations raise
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
            // A bare NULL has no type to accept (Msg 8116, probed 2026-09-24).
            if (IsUntypedNullLiteral(this.certId))
                throw SimulatedSqlException.InvalidArgumentDataType("NULL", 1, "CertEncoded");
        }
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var id = this.certId.Run(runtime);
        if (!id.IsNull && id.Type is Int32SqlType && runtime.Batch.CurrentDatabase.EncryptionKeys.CertificateById(id.AsInt32) is not null)
            throw new NotSupportedException($"{(this.password is null ? "CERTENCODED" : "CERTPRIVATEKEY")} of a certificate isn't modeled by the simulator, whose certificates carry no X.509 encoding.");
        return SqlValue.Null(SqlType.VarbinaryMax);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.VarbinaryMax;

    internal override string DebugDisplay() => this.password is null
        ? $"CERTENCODED({this.certId.DebugDisplay()})"
        : $"CERTPRIVATEKEY({this.certId.DebugDisplay()}, …)";

    internal override void Describe(NodeShape shape) => shape.Child(this.certId).Child(this.password).Child(this.decryptionPassword);
}
