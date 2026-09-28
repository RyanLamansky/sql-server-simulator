using System.Data.SqlTypes;
using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// A member of a CLR user-defined type read in a query: a property or field
/// (<c>@p.X</c>, <c>t.col.X</c>), an instance method (<c>@p.Distance()</c>) or
/// a static one through the type (<c>Point::Make(1, 2)</c>,
/// <c>Point::[Null]</c>).
/// </summary>
/// <remarks>
/// <para>
/// The member binds while the statement compiles, since the receiver's type is
/// known there (probed 2026-09-28 against SQL Server 2025): a missing property
/// or field is Msg 6592, a missing method Msg 6506 state 10, a method called
/// with the wrong argument count Msg 174, an instance property named through
/// the type Msg 6584, and a mutator method read for its value Msg 6200.
/// </para>
/// <para>
/// A NULL receiver reads NULL without calling in. Arguments convert to the
/// method's parameter types as a function's do, a NULL of a CLR type passing
/// that type's <c>Null</c>; a string result is <c>nvarchar(4000)</c>, longer
/// being Msg 6522 like any other throw.
/// </para>
/// </remarks>
internal sealed class ClrTypeMemberCall : Expression
{
    private readonly ClrUserDefinedType udt;
    private readonly Expression? receiver;
    private readonly MemberInfo member;
    private readonly Expression[] arguments;
    private readonly SqlType[] parameterTypes;
    private readonly SqlType resultType;

    private ClrTypeMemberCall(ClrUserDefinedType udt, Expression? receiver, MemberInfo member, Expression[] arguments, SqlType[] parameterTypes, SqlType resultType)
    {
        this.udt = udt;
        this.receiver = receiver;
        this.member = member;
        this.arguments = arguments;
        this.parameterTypes = parameterTypes;
        this.resultType = resultType;
    }

    /// <summary>
    /// The CLR type <paramref name="receiver"/> evaluates to, when it names a
    /// value a member can be read from: a variable, a column the query scope
    /// binds, or any other expression of the type. <see langword="null"/>
    /// otherwise — including every name that doesn't bind as a column, which
    /// is what leaves <c>alias.column</c> an ordinary reference.
    /// </summary>
    public static ClrUdtSqlType? ReceiverType(Expression receiver, ParserContext context)
    {
        try
        {
            switch (receiver)
            {
                case VariableReference variable:
                    return variable.DeclaredType as ClrUdtSqlType;
                case Reference reference:
                    if (reference.ReferencedName.Count > 2)
                        return null;
                    if (context.DeclaredColumnTypes is { } declaredColumnType)
                        return declaredColumnType(reference.ReferencedName) as ClrUdtSqlType;
                    if (context.ScopeSources is { Length: > 0 } sources)
                    {
                        var (source, column) = Selection.FindSourceColumn(sources, reference.ReferencedName);
                        if (source >= 0)
                            return sources[source].Columns[column].Type as ClrUdtSqlType;
                    }

                    return context.OuterTypeResolver is { } resolveType ? resolveType(reference.ReferencedName) as ClrUdtSqlType : null;
                default:
                    return receiver.GetSqlType(context.Batch, context.OuterTypeResolver ?? (name => throw SimulatedSqlException.InvalidColumnName(name))) as ClrUdtSqlType;
            }
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a member of <paramref name="receiver"/>. Cursor enters on the
    /// member's name; on return it sits on the closing <c>)</c> of a method
    /// call, or still on the name of a property.
    /// </summary>
    public static ClrTypeMemberCall ParseInstance(Expression receiver, ClrUdtSqlType type, string memberName, ParserContext context) =>
        Parse(type.Udt, receiver, memberName, context);

    /// <summary>
    /// Parses <c>type::member</c>. Cursor enters on the member's name; on
    /// return it sits on the closing <c>)</c> of a method call, or still on the
    /// name of a property.
    /// </summary>
    public static ClrTypeMemberCall ParseStatic(ClrUdtSqlType type, ParserContext context) =>
        context.Token is Name name
            ? Parse(type.Udt, null, name.Value, context)
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    private static ClrTypeMemberCall Parse(ClrUserDefinedType udt, Expression? receiver, string memberName, ParserContext context)
    {
        var isStatic = receiver is null;
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is not Operator { Character: '(' })
        {
            context.RestoreCheckpoint(checkpoint);
            var dataMember = udt.FindDataMember(memberName, isStatic)
                ?? throw (isStatic && udt.FindDataMember(memberName, isStatic: false) is not null
                    ? SimulatedSqlException.ClrPropertyNotStatic(memberName, udt.ClassName, udt.Assembly.Name)
                    : SimulatedSqlException.ClrPropertyNotFound(memberName, udt.ClassName, udt.Assembly.Name));

            var memberType = dataMember is PropertyInfo property ? property.PropertyType : ((FieldInfo)dataMember).FieldType;
            return new ClrTypeMemberCall(udt, receiver, dataMember, [], [], ResultTypeOf(memberType, memberName, context));
        }

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

        var candidates = udt.FindMethods(memberName, isStatic);
        if (candidates.Length == 0)
            throw SimulatedSqlException.ClrMethodNotFound(memberName, udt.ClassName, udt.Assembly.Name, state: 10);
        var method = Array.Find(candidates, candidate => candidate.GetParameters().Length == arguments.Count)
            ?? throw SimulatedSqlException.FunctionRequiresNArguments(memberName, candidates[0].GetParameters().Length);
        if (IsMutator(method))
            throw SimulatedSqlException.ClrMutatorInReadOnlyContext(memberName, udt.ClassName, udt.Assembly.Name);

        var parameters = method.GetParameters();
        var parameterTypes = new SqlType[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            parameterTypes[i] = ResultTypeOf(parameters[i].ParameterType, memberName, context);
        return new ClrTypeMemberCall(udt, receiver, method, [.. arguments], parameterTypes, ResultTypeOf(method.ReturnType, memberName, context));
    }

    /// <summary>Whether <paramref name="method"/> is marked <c>SqlMethod(IsMutator = true)</c>.</summary>
    internal static bool IsMutator(MethodInfo method)
    {
        if (ClrAttributes.Find(method, ClrAttributes.SqlMethod) is not { } attribute)
            return false;
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.MemberName == "IsMutator" && argument.TypedValue.Value is true)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The T-SQL type a CLR member's type reads as: the
    /// <see cref="System.Data.SqlTypes"/> structs and plain primitives as a
    /// SQLCLR routine binds them, a string as <c>nvarchar(4000)</c>, a binary
    /// as <c>varbinary(8000)</c>, and a registered CLR type as itself.
    /// </summary>
    internal static SqlType ResultTypeOf(Type clrType, string memberName, ParserContext context)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        var collation = context.Batch.CurrentDatabase.Collation;
        return type == typeof(SqlString) || type == typeof(string) || type == typeof(SqlChars) ? NVarcharSqlType.Get(4000, collation, Coercibility.CoercibleDefault)
            : type == typeof(SqlInt32) || type == typeof(int) ? SqlType.Int32
            : type == typeof(SqlInt64) || type == typeof(long) ? SqlType.BigInt
            : type == typeof(SqlInt16) || type == typeof(short) ? SqlType.SmallInt
            : type == typeof(SqlByte) || type == typeof(byte) ? SqlType.TinyInt
            : type == typeof(SqlBoolean) || type == typeof(bool) ? SqlType.Bit
            : type == typeof(SqlDouble) || type == typeof(double) ? SqlType.Float
            : type == typeof(SqlSingle) || type == typeof(float) ? SqlType.Real
            : type == typeof(SqlDecimal) || type == typeof(decimal) ? SqlType.GetDecimal(18, 0)
            : type == typeof(SqlMoney) ? SqlType.Money
            : type == typeof(SqlDateTime) || type == typeof(DateTime) ? SqlType.DateTime
            : type == typeof(SqlGuid) || type == typeof(Guid) ? SqlType.UniqueIdentifier
            : type == typeof(SqlBinary) || type == typeof(byte[]) || type == typeof(SqlBytes) ? VarbinarySqlType.Get(8000)
            : type == typeof(SqlXml) ? SqlType.Xml
            : ClrUserDefinedType.FindByClrType(context.Batch.CurrentDatabase, type)?.SqlType
                ?? throw new NotSupportedException($"CLR type member '{memberName}' uses {type.FullName}, which the simulator does not map to a T-SQL type.");
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.resultType;

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (runtime.Batch.IsSkipping)
            return SqlValue.Null(this.resultType);

        object? target = null;
        if (this.receiver is not null)
        {
            var value = this.receiver.Run(runtime);
            if (value.IsNull)
                return SqlValue.Null(this.resultType);
            target = this.udt.Deserialize(value.AsClrUdtBytes);
        }

        object? result;
        switch (this.member)
        {
            case PropertyInfo property:
                result = this.udt.Invoke(property.GetMethod!, target, null);
                break;
            case FieldInfo field:
                result = field.GetValue(target);
                break;
            default:
                var method = (MethodInfo)this.member;
                var parameters = method.GetParameters();
                var values = new object?[parameters.Length];
                for (var i = 0; i < values.Length; i++)
                    values[i] = ArgumentToClr(this.arguments[i].Run(runtime).CoerceTo(this.parameterTypes[i]), parameters[i].ParameterType);
                result = this.udt.Invoke(method, target, values);
                break;
        }

        return ResultFromClr(result, this.resultType, this.udt.Name);
    }

    /// <summary>A value marshalled into a CLR member's parameter of <paramref name="clrType"/>.</summary>
    internal static object? ArgumentToClr(SqlValue value, Type clrType)
    {
        if (value.Type is ClrUdtSqlType udtType)
            return udtType.Udt.ToClr(value);
        if (typeof(INullable).IsAssignableFrom(clrType))
            return ClrTypeMarshaller.ToClr(value, clrType);
        if (value.IsNull)
            return null;
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return Type.GetTypeCode(type) switch
        {
            TypeCode.String => value.AsString,
            TypeCode.Int32 => value.AsInt32,
            TypeCode.Int64 => value.AsInt64,
            TypeCode.Int16 => value.AsInt16,
            TypeCode.Byte => value.AsByte,
            TypeCode.Boolean => value.AsBoolean,
            TypeCode.Double => value.AsDouble,
            TypeCode.Single => value.AsSingle,
            TypeCode.DateTime => value.AsDateTime,
            _ => type == typeof(byte[]) ? value.AsBytes
                : type == typeof(Guid) ? value.AsGuid
                : throw new NotSupportedException($"A CLR parameter of type {type.FullName} is not modeled."),
        };
    }

    /// <summary>
    /// A CLR member's result as a value of <paramref name="resultType"/>; a
    /// string longer than <c>nvarchar(4000)</c> is the server's truncation
    /// error inside Msg 6522 naming <paramref name="typeName"/>, at state 1
    /// where a throw from the member itself is state 2 (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal static SqlValue ResultFromClr(object? result, SqlType resultType, string typeName)
    {
        if (resultType is ClrUdtSqlType udtType)
            return udtType.Udt.FromClr(result);
        var value = ClrTypeMarshaller.FromRecordValue(result, resultType);
        return ClrTypeMarshaller.OverflowedWidth(value, resultType) is { } width
            ? throw SimulatedSqlException.ClrRoutineThrew(typeName, ClrExceptionReport.Truncation(value.AsString.Length, width), state: 1)
            : value;
    }

    internal override string DebugDisplay() => this.receiver is null
        ? $"{this.udt.Name}::{this.member.Name}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})"
        : $"{this.receiver.DebugDisplay()}.{this.member.Name}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";

    internal override void Describe(NodeShape shape) =>
        shape.Local(this.udt).Local(this.member.Name).Local(this.member.MetadataToken).Child(this.receiver).Children(this.arguments);
}
