using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class WidenedParameterTypeAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<WidenedParameterTypeAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task IEnumerableParameter_ListAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IEnumerable<int> {|SSS010:items|}) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task ICollectionParameter_ListAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(ICollection<int> {|SSS010:items|}) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task IListParameter_ListAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IList<int> {|SSS010:items|}) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task ISetParameter_HashSetAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(ISet<string> {|SSS010:names|}) { }
                internal static void Call() => Take(new HashSet<string>());
            }
            """);

    [TestMethod]
    public Task IDictionaryParameter_DictionaryAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IDictionary<string, int> {|SSS010:map|}) { }
                internal static void Call() => Take(new Dictionary<string, int>());
            }
            """);

    [TestMethod]
    public Task IReadOnlyCollectionParameter_ListAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyCollection<int> {|SSS010:items|}) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task IReadOnlyListParameter_ArrayAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                internal static void Call() => Take(new int[0]);
            }
            """);

    /// <remarks>
    /// The default reference assemblies predate <c>IReadOnlySet&lt;T&gt;</c>, so
    /// the test declares it in the namespace the rule keys on — which is also
    /// what proves the match is name-plus-namespace rather than a metadata
    /// identity.
    /// </remarks>
    [TestMethod]
    public Task IReadOnlySetParameter_ConcreteSetAtEveryCallSite_Reports() =>
        RunAsync("""
            namespace System.Collections.Generic
            {
                internal interface IReadOnlySet<T> { }
                internal sealed class Names : IReadOnlySet<string> { }
                internal sealed class Holder
                {
                    private static void Take(IReadOnlySet<string> {|SSS010:names|}) { }
                    internal static void Call() => Take(new Names());
                }
            }
            """);

    [TestMethod]
    public Task IReadOnlyDictionaryParameter_DictionaryAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyDictionary<string, int> {|SSS010:map|}) { }
                internal static void Call() => Take(new Dictionary<string, int>());
            }
            """);

    [TestMethod]
    public Task NonGenericIListParameter_ListAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections;
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IList {|SSS010:items|}) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task ImmutableInterfaceParameter_ImmutableListAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Immutable;
            internal sealed class Holder
            {
                private static void Take(IImmutableList<int> {|SSS010:items|}) { }
                internal static void Call() => Take(ImmutableList<int>.Empty);
            }
            """);

    [TestMethod]
    public Task ConstructorParameter_HashSetAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                internal Holder(ISet<string> {|SSS010:names|}) { }
                internal static Holder Create() => new Holder(new HashSet<string>());
            }
            """);

    [TestMethod]
    public Task PrimaryConstructorParameter_HashSetAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Parser(ISet<string> {|SSS010:names|})
            {
                internal int Count => names.Count;
            }
            internal sealed class Holder
            {
                internal static Parser Create() => new Parser(new HashSet<string>());
            }
            """);

    [TestMethod]
    public Task BaseConstructorInitializerArgument_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal class Base
            {
                internal Base(IReadOnlyList<int> {|SSS010:items|}) { }
            }
            internal sealed class Derived : Base
            {
                internal Derived() : base(new List<int>()) { }
            }
            """);

    [TestMethod]
    public Task LocalFunctionParameter_ListAtEveryCallSite_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                internal static void Call()
                {
                    Take(new List<int>());
                    static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                }
            }
            """);

    [TestMethod]
    public Task StructCollectionArgument_BoxedAtEveryCall_Reports() =>
        RunAsync("""
            using System.Collections;
            using System.Collections.Generic;
            internal readonly struct Bag : IReadOnlyList<int>
            {
                public int this[int index] => 0;
                public int Count => 0;
                public IEnumerator<int> GetEnumerator() => null;
                IEnumerator IEnumerable.GetEnumerator() => null;
            }
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                internal static void Call() => Take(new Bag());
            }
            """);

    [TestMethod]
    public Task ImmutableArrayArgument_BoxedAtEveryCall_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            using System.Collections.Immutable;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                internal static void Call() => Take(ImmutableArray<int>.Empty);
            }
            """);

    [TestMethod]
    public Task NullCallSiteBesideConcreteOne_StillReports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                internal static void Call()
                {
                    Take(new List<int>());
                    Take(null);
                }
            }
            """);

    [TestMethod]
    public Task OnlyNullCallSites_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items) { }
                internal static void Call() => Take(null);
            }
            """);

    [TestMethod]
    public Task NullCallSiteWithStructArgument_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            using System.Collections.Immutable;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items) { }
                internal static void Call()
                {
                    Take(ImmutableArray<int>.Empty);
                    Take(null);
                }
            }
            """);

    [TestMethod]
    public Task NullDefaultWithStructArgument_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            using System.Collections.Immutable;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items = null) { }
                internal static void Call() => Take(ImmutableArray<int>.Empty);
            }
            """);

    [TestMethod]
    public Task NullDefaultWithReferenceArgument_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> {|SSS010:items|} = null) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task OmittedOptionalArgument_ContributesNothing_StillReports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> {|SSS010:items|} = null) { }
                internal static void Call()
                {
                    Take();
                    Take(new List<int>());
                }
            }
            """);

    [TestMethod]
    public Task DefaultLiteralArgument_ContributesNothing_StillReports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                internal static void Call()
                {
                    Take(default);
                    Take(new List<int>());
                }
            }
            """);

    [TestMethod]
    public Task TwoDifferentConcreteTypes_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items) { }
                internal static void Call()
                {
                    Take(new List<int>());
                    Take(new int[0]);
                }
            }
            """);

    [TestMethod]
    public Task InterfaceTypedArgument_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items) { }
                internal static void Call(IReadOnlyList<int> forwarded) => Take(forwarded);
            }
            """);

    [TestMethod]
    public Task ExplicitCastToInterfaceAtCallSite_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items) { }
                internal static void Call() => Take((IReadOnlyList<int>)new List<int>());
            }
            """);

    [TestMethod]
    public Task InterfaceTypedForwarder_ShieldsCalleeUntilTheForwarderIsFixed() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Inner(IReadOnlyList<int> items) { }
                private static void Outer(IReadOnlyList<int> {|SSS010:items|}) => Inner(items);
                internal static void Call() => Outer(new List<int>());
            }
            """);

    [TestMethod]
    public Task ConcreteForwarder_CascadesToTheCallee() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Inner(IReadOnlyList<int> {|SSS010:items|}) { }
                private static void Outer(List<int> items) => Inner(items);
                internal static void Call() => Outer(new List<int>());
            }
            """);

    [TestMethod]
    public Task NoCallSites_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items) { }
            }
            """);

    [TestMethod]
    public Task PublicMethodOnPublicType_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            public sealed class Holder
            {
                public static void Take(IReadOnlyList<int> items) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task ProtectedMethodOnPublicType_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            public class Holder
            {
                protected static void Take(IReadOnlyList<int> items) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task InternalMethodOnPublicType_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            public sealed class Holder
            {
                internal static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task PublicMethodOnTypeNestedInInternal_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Outer
            {
                public sealed class Holder
                {
                    public static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
                    internal static void Call() => Take(new List<int>());
                }
            }
            """);

    [TestMethod]
    public Task VirtualMethod_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal class Holder
            {
                internal virtual void Take(IReadOnlyList<int> items) { }
                internal static void Call(Holder holder) => holder.Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task OverridingMethod_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal abstract class Base
            {
                internal abstract void Take(IReadOnlyList<int> items);
            }
            internal sealed class Derived : Base
            {
                internal override void Take(IReadOnlyList<int> items) { }
                internal static void Call(Derived derived) => derived.Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task ImplicitInterfaceImplementation_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal interface ITaker
            {
                void Take(IReadOnlyList<int> items);
            }
            internal sealed class Holder : ITaker
            {
                public void Take(IReadOnlyList<int> items) { }
                internal static void Call(Holder holder) => holder.Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task ExplicitInterfaceImplementation_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal interface ITaker
            {
                void Take(IReadOnlyList<int> items);
            }
            internal sealed class Holder : ITaker
            {
                void ITaker.Take(IReadOnlyList<int> items) { }
                internal static void Call(ITaker taker) => taker.Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task MethodGroupConvertedToDelegate_DoesNotReport() =>
        RunAsync("""
            using System;
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IReadOnlyList<int> items) { }
                internal static Action<IReadOnlyList<int>> Handler = Take;
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task ExplicitlyTypedLambdaParameter_DoesNotReport() =>
        RunAsync("""
            using System;
            using System.Collections.Generic;
            internal sealed class Holder
            {
                internal static Action<IReadOnlyList<int>> Handler = (IReadOnlyList<int> items) => { };
                internal static void Call() => Handler(new List<int>());
            }
            """);

    [TestMethod]
    public Task ByReferenceParameter_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(in IReadOnlyList<int> items) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    /// <remarks>
    /// A <c>params</c> parameter can only name a collection interface under
    /// C# 13's params collections, which is past the harness's default language
    /// version — hence the one test that raises it.
    /// </remarks>
    [TestMethod]
    public Task ParamsCollectionParameter_DoesNotReport() =>
        new PreviewLanguageTest
        {
            TestCode = """
                using System.Collections.Generic;
                internal sealed class Holder
                {
                    private static void Take(params IReadOnlyList<int> items) { }
                    internal static void Call() => Take(new List<int>());
                }
                """,
        }.RunAsync(this.TestContext.CancellationToken);

    /// <summary>
    /// The standard harness with the language version lifted to preview.
    /// </summary>
    private sealed class PreviewLanguageTest : CSharpAnalyzerTest<WidenedParameterTypeAnalyzer, DefaultVerifier>
    {
        protected override ParseOptions CreateParseOptions() =>
            ((CSharpParseOptions)base.CreateParseOptions()).WithLanguageVersion(LanguageVersion.Preview);
    }

    [TestMethod]
    public Task ParameterTypeMentioningTypeParameter_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take<T>(IReadOnlyList<T> items) { }
                internal static void Call() => Take(new List<int>());
            }
            """);

    [TestMethod]
    public Task GenericMethodWithClosedParameterType_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take<T>(IReadOnlyList<int> {|SSS010:items|}, T other) { }
                internal static void Call() => Take(new List<int>(), "x");
            }
            """);

    [TestMethod]
    public Task NonCollectionInterfaceInTheSameNamespace_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private static void Take(IComparer<int> comparer) { }
                internal static void Call() => Take(Comparer<int>.Default);
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
                    private static void Take(IReadOnlyList<int> items) { }
                    internal static void Call() => Take(new Items());
                }
            }
            """);

    [TestMethod]
    public Task PartialMethod_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed partial class Holder
            {
                private static partial void Take(IReadOnlyList<int> items);
                internal static void Call() => Take(new List<int>());
            }
            internal sealed partial class Holder
            {
                private static partial void Take(IReadOnlyList<int> items) { }
            }
            """);

    [TestMethod]
    public Task CallInAnotherType_IsStillSeen_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                internal static void Take(IReadOnlyList<int> {|SSS010:items|}) { }
            }
            internal sealed class Caller
            {
                internal static void Call() => Holder.Take(new List<int>());
            }
            """);
}
