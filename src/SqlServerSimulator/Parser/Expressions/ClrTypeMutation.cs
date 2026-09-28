using System.Reflection;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The value a CLR user-defined type holds after a mutation — <c>SET @p.X =
/// 10</c>, <c>SET @p.Scale(3)</c>, <c>UPDATE t SET p.Y = 5</c> or <c>UPDATE t
/// SET p.Scale(2)</c>: the receiver's current value, deserialized, with the
/// property or field assigned or the mutator method called, serialized again.
/// The statement then stores it as it would any assigned value.
/// </summary>
/// <remarks>
/// Probed 2026-09-28 against SQL Server 2025: a method not marked
/// <c>SqlMethod(IsMutator = true)</c> is Msg 6201, a member the class lacks
/// Msg 6592 (a property) or 6506 (a method), and a NULL receiver Msg 5302
/// naming the member and the receiver as written.
/// </remarks>
internal sealed class ClrTypeMutation : Expression
{
    private readonly ClrUserDefinedType udt;
    private readonly Expression receiver;
    private readonly string receiverName;
    private readonly MemberInfo member;
    private readonly Expression[] arguments;
    private readonly SqlType[] argumentTypes;

    private ClrTypeMutation(ClrUserDefinedType udt, Expression receiver, string receiverName, MemberInfo member, Expression[] arguments, SqlType[] argumentTypes)
    {
        this.udt = udt;
        this.receiver = receiver;
        this.receiverName = receiverName;
        this.member = member;
        this.arguments = arguments;
        this.argumentTypes = argumentTypes;
    }

    /// <summary>
    /// Parses the mutation of <paramref name="memberName"/> on
    /// <paramref name="receiver"/>. Cursor enters on the token after the
    /// member's name — the <c>(</c> of a method call or the <c>=</c> of an
    /// assignment — and leaves on the first token past the mutation.
    /// </summary>
    public static ClrTypeMutation Parse(Expression receiver, string receiverName, ClrUdtSqlType type, string memberName, ParserContext context)
    {
        var udt = type.Udt;
        if (context.Token is Operator { Character: '=' })
        {
            var dataMember = udt.FindDataMember(memberName, isStatic: false)
                ?? throw SimulatedSqlException.ClrPropertyNotFound(memberName, udt.ClassName, udt.Assembly.Name);
            var memberType = dataMember is PropertyInfo property ? property.PropertyType : ((FieldInfo)dataMember).FieldType;
            context.MoveNextRequired();
            var value = Parse(context);
            return new ClrTypeMutation(udt, receiver, receiverName, dataMember, [value], [ClrTypeMemberCall.ResultTypeOf(memberType, memberName, context)]);
        }

        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var candidates = udt.FindMethods(memberName, isStatic: false);
        if (candidates.Length == 0)
            throw SimulatedSqlException.ClrMethodNotFound(memberName, udt.ClassName, udt.Assembly.Name, state: 10);

        var arguments = new List<Expression>();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
        {
            arguments.Add(Parse(context));
            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                arguments.Add(Parse(context));
            }

            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        context.MoveNextOptional();
        var method = Array.Find(candidates, candidate => candidate.GetParameters().Length == arguments.Count)
            ?? throw SimulatedSqlException.FunctionRequiresNArguments(memberName, candidates[0].GetParameters().Length);
        if (!ClrTypeMemberCall.IsMutator(method))
            throw SimulatedSqlException.ClrNotMutator(memberName, udt.ClassName, udt.Assembly.Name);

        var parameters = method.GetParameters();
        var argumentTypes = new SqlType[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            argumentTypes[i] = ClrTypeMemberCall.ResultTypeOf(parameters[i].ParameterType, memberName, context);
        return new ClrTypeMutation(udt, receiver, receiverName, method, [.. arguments], argumentTypes);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.udt.SqlType;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var current = this.receiver.Run(runtime);
        if (current.IsNull)
            throw SimulatedSqlException.ClrMutatorOnNull(this.member.Name, this.receiverName);

        var instance = this.udt.Deserialize(current.AsClrUdtBytes);
        var values = new object?[this.arguments.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var clrType = this.member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => ((MethodInfo)this.member).GetParameters()[i].ParameterType,
            };
            values[i] = ClrTypeMemberCall.ArgumentToClr(this.arguments[i].Run(runtime).CoerceTo(this.argumentTypes[i]), clrType);
        }

        switch (this.member)
        {
            case PropertyInfo property:
                _ = this.udt.Invoke(property.SetMethod ?? throw SimulatedSqlException.ClrPropertyNotFound(property.Name, this.udt.ClassName, this.udt.Assembly.Name), instance, values);
                break;
            case FieldInfo field:
                field.SetValue(instance, values[0]);
                break;
            default:
                _ = this.udt.Invoke((MethodInfo)this.member, instance, values);
                break;
        }

        return this.udt.FromClr(instance);
    }

    internal override string DebugDisplay() => $"{this.receiver.DebugDisplay()}.{this.member.Name}<-({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";

    internal override void Describe(NodeShape shape) =>
        shape.Local(this.udt).Local(this.member.Name).Local(this.member.MetadataToken).Local(this.receiverName).Child(this.receiver).Children(this.arguments);
}
