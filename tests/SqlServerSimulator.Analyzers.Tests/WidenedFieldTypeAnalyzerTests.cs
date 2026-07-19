using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class WidenedFieldTypeAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<WidenedFieldTypeAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task BaseClassDeclared_DerivedAssigned_OnInternalType_Reports() =>
        RunAsync("""
            internal abstract class Base { }
            internal sealed class Derived : Base { }
            internal sealed class Holder
            {
                public static readonly Base {|SSS002:Singleton|} = new Derived();
            }
            """);

    [TestMethod]
    public Task InterfaceDeclared_ConcreteAssigned_OnInternalType_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static readonly IReadOnlyList<int> {|SSS002:items|} = new List<int>();
            }
            """);

    [TestMethod]
    public Task SameTypeDeclaredAndAssigned_DoesNotReport() =>
        RunAsync("""
            internal sealed class Derived
            {
                public static readonly Derived Instance = new Derived();
            }
            """);

    [TestMethod]
    public Task PublicType_DoesNotReport() =>
        RunAsync("""
            public abstract class Base { }
            public sealed class Derived : Base { }
            public sealed class Holder
            {
                public static readonly Base Singleton = new Derived();
            }
            """);

    [TestMethod]
    public Task PublicTypeNestedInInternal_TreatedAsNonPublic_Reports() =>
        RunAsync("""
            internal sealed class Outer
            {
                public sealed class Holder
                {
                    public static readonly object {|SSS002:Singleton|} = new System.Text.StringBuilder();
                }
            }
            """);

    [TestMethod]
    public Task NoInitializer_DoesNotReport() =>
        RunAsync("""
            internal sealed class Holder
            {
                public static readonly object Singleton;
                static Holder() { Singleton = new System.Text.StringBuilder(); }
            }
            """);

    [TestMethod]
    public Task NotReadonly_DoesNotReport() =>
        RunAsync("""
            internal abstract class Base { }
            internal sealed class Derived : Base { }
            internal sealed class Holder
            {
                public static Base Mutable = new Derived();
            }
            """);

    [TestMethod]
    public Task ConstField_DoesNotReport() =>
        RunAsync("""
            internal sealed class Holder
            {
                public const int N = 42;
            }
            """);

    [TestMethod]
    public Task ValueTypeBoxedIntoObject_DoesNotReport() =>
        RunAsync("""
            internal sealed class Holder
            {
                public static readonly object Boxed = 42;
            }
            """);

    [TestMethod]
    public Task FactoryReturningSameType_DoesNotReport() =>
        RunAsync("""
            internal sealed class Holder
            {
                private static System.Text.StringBuilder Make() => new();
                public static readonly System.Text.StringBuilder Builder = Make();
            }
            """);

    [TestMethod]
    public Task NullInitializer_DoesNotReport() =>
        RunAsync("""
            internal sealed class Holder
            {
                public static readonly object? Maybe = null;
            }
            """);

    [TestMethod]
    public Task InstanceField_OnInternalType_Reports() =>
        RunAsync("""
            internal abstract class Base { }
            internal sealed class Derived : Base { }
            internal sealed class Holder
            {
                public readonly Base {|SSS002:Singleton|} = new Derived();
            }
            """);

    [TestMethod]
    public Task TwoFieldsInOneDeclaration_BothInitializedWithDerived_BothReport() =>
        RunAsync("""
            internal abstract class Base { }
            internal sealed class Derived : Base { }
            internal sealed class Holder
            {
                public static readonly Base {|SSS002:A|} = new Derived(), {|SSS002:B|} = new Derived();
            }
            """);
}
