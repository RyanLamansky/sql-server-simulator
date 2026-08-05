using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class WidenedReturnTypeAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<WidenedReturnTypeAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task IReadOnlyListReturn_OneConcreteReturn_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make()
                {
                    return new List<int>();
                }
            }
            """);

    [TestMethod]
    public Task ExpressionBodiedMember_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task IEnumerableReturn_MaterializedList_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IEnumerable<int>|} Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task ISetReturn_HashSet_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:ISet<string>|} Make() => new HashSet<string>();
            }
            """);

    [TestMethod]
    public Task IDictionaryReturn_Dictionary_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyDictionary<string, int>|} Make() => new Dictionary<string, int>();
            }
            """);

    [TestMethod]
    public Task ArrayReturnedThroughInterface_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make() => new int[4];
            }
            """);

    [TestMethod]
    public Task SeveralReturnsOfOneConcreteType_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make(bool empty)
                {
                    if (empty)
                        return new List<int>();
                    var items = new List<int> { 1 };
                    return items;
                }
            }
            """);

    [TestMethod]
    public Task LocalFunctionReturn_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                internal static int Count()
                {
                    return Make().Count;
                    static {|SSS011:IReadOnlyList<int>|} Make() => new List<int>();
                }
            }
            """);

    [TestMethod]
    public Task StructCollectionReturn_BoxedAtEveryCall_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            using System.Collections.Immutable;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make() => ImmutableArray<int>.Empty;
            }
            """);

    [TestMethod]
    public Task NullReturnBesideConcreteOne_StillReports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make(bool none)
                {
                    if (none)
                        return null;
                    return new List<int>();
                }
            }
            """);

    [TestMethod]
    public Task NullReturnWithStructReturn_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            using System.Collections.Immutable;
            internal sealed class Holder
            {
                private static IReadOnlyList<int> Make(bool none)
                {
                    if (none)
                        return null;
                    return ImmutableArray<int>.Empty;
                }
            }
            """);

    [TestMethod]
    public Task OnlyNullReturns_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IReadOnlyList<int> Make() => null;
            }
            """);

    [TestMethod]
    public Task DefaultReturn_ContributesNothing_StillReports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make(bool none)
                {
                    if (none)
                        return default;
                    return new List<int>();
                }
            }
            """);

    [TestMethod]
    public Task ThrowOnlyBody_DoesNotReport() =>
        RunAsync("""
            using System;
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IReadOnlyList<int> Make() => throw new NotSupportedException();
            }
            """);

    [TestMethod]
    public Task TwoDifferentConcreteReturns_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IReadOnlyList<int> Make(bool array)
                {
                    if (array)
                        return new int[4];
                    return new List<int>();
                }
            }
            """);

    [TestMethod]
    public Task InterfaceTypedReturn_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IReadOnlyList<int> Make(IReadOnlyList<int> forwarded) => forwarded;
            }
            """);

    [TestMethod]
    public Task IteratorMethod_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IEnumerable<int> Make()
                {
                    yield return 1;
                }
            }
            """);

    [TestMethod]
    public Task IteratorWithYieldBreak_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IEnumerable<int> Make(bool stop)
                {
                    if (stop)
                        yield break;
                    yield return 1;
                }
            }
            """);

    [TestMethod]
    public Task YieldInsideNestedLocalFunction_DoesNotExemptTheOuterMethod_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            using System.Linq;
            internal sealed class Holder
            {
                private static {|SSS011:IReadOnlyList<int>|} Make()
                {
                    return Inner().ToList();
                    static IEnumerable<int> Inner()
                    {
                        yield return 1;
                    }
                }
            }
            """);

    [TestMethod]
    public Task ReturnInsideNestedLambda_IsNotTheMethodsEvidence_DoesNotReport() =>
        RunAsync("""
            using System;
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IReadOnlyList<int> Make(IReadOnlyList<int> given)
                {
                    Func<List<int>> factory = () => new List<int>();
                    _ = factory;
                    return given;
                }
            }
            """);

    [TestMethod]
    public Task PublicMethodOnPublicType_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            public sealed class Holder
            {
                public static IReadOnlyList<int> Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task InternalMethodOnPublicType_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            public sealed class Holder
            {
                internal static {|SSS011:IReadOnlyList<int>|} Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task VirtualMethod_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal class Holder
            {
                internal virtual IReadOnlyList<int> Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task OverridingMethod_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal abstract class Base
            {
                internal abstract IReadOnlyList<int> Make();
            }
            internal sealed class Derived : Base
            {
                internal override IReadOnlyList<int> Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task ImplicitInterfaceImplementation_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal interface IMaker
            {
                IReadOnlyList<int> Make();
            }
            internal sealed class Holder : IMaker
            {
                public IReadOnlyList<int> Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task ExplicitInterfaceImplementation_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal interface IMaker
            {
                IReadOnlyList<int> Make();
            }
            internal sealed class Holder : IMaker
            {
                IReadOnlyList<int> IMaker.Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task PartialMethod_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed partial class Holder
            {
                private static partial IReadOnlyList<int> Make();
            }
            internal sealed partial class Holder
            {
                private static partial IReadOnlyList<int> Make() => new List<int>();
            }
            """);

    [TestMethod]
    public Task ReturnTypeMentioningTypeParameter_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IReadOnlyList<T> Make<T>(T item) => new List<T> { item };
            }
            """);

    [TestMethod]
    public Task NonCollectionInterfaceReturn_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static IComparer<int> Make() => Comparer<int>.Default;
            }
            """);

    [TestMethod]
    public Task ProjectLocalInterfaceOfTheSameName_DoesNotReport() =>
        RunAsync("""
            namespace Local
            {
                internal interface IReadOnlyList<T> { }
                internal sealed class Items : IReadOnlyList<int> { }
                internal sealed class Holder
                {
                    private static IReadOnlyList<int> Make() => new Items();
                }
            }
            """);

    [TestMethod]
    public Task AsyncTaskOfInterface_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            internal sealed class Holder
            {
                private static async Task<IReadOnlyList<int>> Make()
                {
                    await Task.Yield();
                    return new List<int>();
                }
            }
            """);
}
