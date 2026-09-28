using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SqlServerSimulator;

/// <summary>
/// Compiles a SQLCLR assembly the way one is built for real SQL Server —
/// against the .NET Framework 4.8 reference assemblies, so it references
/// <c>mscorlib</c> and <c>System.Data, Version=4.0.0.0</c> and reaches
/// <c>Microsoft.SqlServer.Server.SqlContext</c> / <c>SqlPipe</c> /
/// <c>SqlDataRecord</c> / <c>SqlMetaData</c> and the routine attributes
/// through the latter. The same source registered on SQL Server 2025 is what
/// the procedure, table-valued function and aggregate behavior was probed
/// with (2026-09-28).
/// </summary>
/// <remarks>
/// The repo keeps no binary fixtures, so the bytes are compiled once per test
/// run from <see cref="Source"/>. <see cref="ClrAssemblyFixture"/> covers the
/// scalar binding with emitted .NET-targeted assemblies; this one covers the
/// surface only a Framework-shaped assembly exercises.
/// </remarks>
internal static class ClrFrameworkFixture
{
    private static readonly Lazy<byte[]> assembly = new(Compile);

    /// <summary><c>CREATE ASSEMBLY simclr …</c> over the compiled fixture.</summary>
    public static string CreateAssembly => $"create assembly simclr from {ClrAssemblyFixture.HexLiteral(assembly.Value)} with permission_set = safe";

    /// <summary>A CLR-enabled simulation with the fixture registered, and <paramref name="batches"/> run after it.</summary>
    public static Simulation Simulation(params ReadOnlySpan<string> batches)
    {
        var simulation = new Simulation { EnableClr = true };
        _ = simulation.ExecuteNonQuery(CreateAssembly);
        simulation.ExecuteBatches(batches);
        return simulation;
    }

    private static byte[] Compile()
    {
        var referenceDirectory = Path.Combine(AppContext.BaseDirectory, "net48ref");
        var compilation = CSharpCompilation.Create(
            "simclr",
            [CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.CSharp7_3))],
            new[] { "mscorlib.dll", "System.dll", "System.Data.dll", "System.Xml.dll" }.Select(file => MetadataReference.CreateFromFile(Path.Combine(referenceDirectory, file))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        return result.Success
            ? image.ToArray()
            : throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
    }

    private const string Source = """
        using System;
        using System.Collections;
        using System.Data;
        using System.Data.SqlTypes;
        using System.IO;
        using System.Text;
        using Microsoft.SqlServer.Server;

        public static class Procs
        {
            [SqlProcedure]
            public static void Hello() { SqlContext.Pipe.Send("hello from clr"); }

            [SqlProcedure]
            public static int AddOut(SqlInt32 a, SqlInt32 b, out SqlInt32 sum) { sum = a + b; return 7; }

            [SqlProcedure]
            public static void RefParam(ref SqlString s) { s = s.IsNull ? new SqlString("was null") : s + new SqlString("!"); }

            [SqlProcedure]
            public static void ByValue(SqlInt32 a) { SqlContext.Pipe.Send("got " + a.ToString()); }

            [SqlProcedure]
            public static string ReturnsString() { return "x"; }

            [SqlProcedure]
            public static void Rows(SqlInt32 n)
            {
                SqlDataRecord rec = new SqlDataRecord(
                    new SqlMetaData("n", SqlDbType.Int),
                    new SqlMetaData("d", SqlDbType.Decimal, 10, 2),
                    new SqlMetaData("s", SqlDbType.VarChar, 20),
                    new SqlMetaData("m", SqlDbType.NVarChar, SqlMetaData.Max));
                SqlContext.Pipe.SendResultsStart(rec);
                for (int i = 1; i <= n.Value; i++)
                {
                    rec.SetInt32(0, i);
                    rec.SetDecimal(1, i / 4m);
                    rec.SetSqlString(2, new SqlString("r" + i));
                    if (i % 2 == 0) rec.SetDBNull(3); else rec.SetString(3, "max" + i);
                    SqlContext.Pipe.SendResultsRow(rec);
                }
                SqlContext.Pipe.SendResultsEnd();
            }

            [SqlProcedure]
            public static void Mixed()
            {
                SqlContext.Pipe.Send("before");
                SqlDataRecord rec = new SqlDataRecord(new SqlMetaData("x", SqlDbType.Int));
                rec.SetValue(0, 42);
                SqlContext.Pipe.Send(rec);
                SqlContext.Pipe.Send("after");
            }

            [SqlProcedure]
            public static void Throws() { throw new InvalidOperationException("proc boom"); }

            [SqlProcedure]
            public static void RowWithoutStart()
            {
                SqlDataRecord rec = new SqlDataRecord(new SqlMetaData("x", SqlDbType.Int));
                SqlContext.Pipe.SendResultsRow(rec);
            }

            [SqlProcedure]
            public static void StartNoEnd()
            {
                SqlDataRecord rec = new SqlDataRecord(new SqlMetaData("x", SqlDbType.Int));
                SqlContext.Pipe.SendResultsStart(rec);
                rec.SetInt32(0, 5);
                SqlContext.Pipe.SendResultsRow(rec);
            }

            [SqlProcedure]
            public static void ThrowAfterRows()
            {
                SqlDataRecord rec = new SqlDataRecord(new SqlMetaData("x", SqlDbType.Int));
                SqlContext.Pipe.SendResultsStart(rec);
                rec.SetInt32(0, 1);
                SqlContext.Pipe.SendResultsRow(rec);
                throw new InvalidOperationException("after rows");
            }

            [SqlProcedure]
            public static void SetValueWrongKind()
            {
                SqlDataRecord rec = new SqlDataRecord(new SqlMetaData("x", SqlDbType.BigInt));
                rec.SetValue(0, 5);
                SqlContext.Pipe.Send(rec);
            }
        }

        public static class Tvfs
        {
            [SqlFunction(FillRowMethodName = "FillSeries")]
            public static IEnumerable Series(SqlInt32 count)
            {
                ArrayList list = new ArrayList();
                if (count.IsNull) return list;
                for (int i = 1; i <= count.Value; i++) list.Add(i);
                return list;
            }

            public static void FillSeries(object row, out SqlInt32 n, out SqlString label)
            {
                int i = (int)row;
                n = new SqlInt32(i);
                label = i == 2 ? SqlString.Null : new SqlString("item " + i);
            }

            [SqlFunction(FillRowMethodName = "FillSplit")]
            public static IEnumerable Split(SqlString s, SqlString sep)
            {
                if (s.IsNull) yield break;
                foreach (string part in s.Value.Split(new string[] { sep.Value }, StringSplitOptions.None))
                    yield return part;
            }

            public static void FillSplit(object row, out SqlString part) { part = new SqlString((string)row); }

            [SqlFunction]
            public static IEnumerable NoFill(SqlInt32 count) { return new ArrayList(); }

            [SqlFunction(FillRowMethodName = "FillThrows")]
            public static IEnumerable ThrowsInFill(SqlInt32 count) { ArrayList l = new ArrayList(); l.Add(1); return l; }

            public static void FillThrows(object row, out SqlInt32 n) { throw new ArgumentException("fill boom"); }

            [SqlFunction(FillRowMethodName = "FillSeries")]
            public static IEnumerable ThrowsInInit(SqlInt32 count) { throw new ArgumentException("init boom"); }
        }

        public static class Funcs
        {
            [SqlFunction]
            public static SqlBoolean PipeIsNull() { return new SqlBoolean(SqlContext.Pipe == null); }

            [SqlFunction]
            public static SqlString Long(SqlInt32 n) { return new SqlString(new string('y', n.Value)); }
        }

        [Serializable]
        [SqlUserDefinedAggregate(Format.Native)]
        public struct SumSquares
        {
            private long total;
            private bool any;
            public void Init() { total = 0; any = false; }
            public void Accumulate(SqlInt32 v) { if (!v.IsNull) { total += (long)v.Value * v.Value; any = true; } }
            public void Merge(SumSquares other) { total += other.total; any |= other.any; }
            public SqlInt64 Terminate() { return any ? new SqlInt64(total) : SqlInt64.Null; }
        }

        [Serializable]
        [SqlUserDefinedAggregate(Format.UserDefined, MaxByteSize = 8000)]
        public class Concat : IBinarySerialize
        {
            private StringBuilder sb;
            public void Init() { sb = new StringBuilder(); }
            public void Accumulate(SqlString v, SqlString sep)
            {
                if (v.IsNull) return;
                if (sb.Length > 0) sb.Append(sep.IsNull ? "," : sep.Value);
                sb.Append(v.Value);
            }
            public void Merge(Concat other) { sb.Append(other.sb.ToString()); }
            public SqlString Terminate() { return new SqlString(sb.ToString()); }
            public void Read(BinaryReader r) { sb = new StringBuilder(r.ReadString()); }
            public void Write(BinaryWriter w) { w.Write(sb.ToString()); }
        }

        [Serializable]
        [SqlUserDefinedAggregate(Format.Native)]
        public struct CountAll
        {
            private int n;
            public void Init() { n = 0; }
            public void Accumulate(SqlString v) { n++; }
            public void Merge(CountAll other) { n += other.n; }
            public SqlInt32 Terminate() { return new SqlInt32(n); }
        }

        [Serializable]
        [SqlUserDefinedAggregate(Format.Native)]
        public struct AggThrows
        {
            private int n;
            public void Init() { n = 0; }
            public void Accumulate(SqlInt32 v) { if (v.Value == 3) throw new InvalidOperationException("agg boom"); n++; }
            public void Merge(AggThrows other) { n += other.n; }
            public SqlInt32 Terminate() { return new SqlInt32(n); }
        }

        [Serializable]
        [SqlUserDefinedAggregate(Format.Native)]
        public struct NoMerge
        {
            private int n;
            public void Init() { n = 0; }
            public void Accumulate(SqlInt32 v) { n++; }
            public SqlInt32 Terminate() { return new SqlInt32(n); }
        }

        [Serializable]
        public struct NoAttr
        {
            private int n;
            public void Init() { n = 0; }
            public void Accumulate(SqlInt32 v) { n++; }
            public void Merge(NoAttr other) { n += other.n; }
            public SqlInt32 Terminate() { return new SqlInt32(n); }
        }

        [Serializable]
        [SqlUserDefinedAggregate(Format.Native)]
        public struct NativeWithRef
        {
            private string s;
            public void Init() { s = ""; }
            public void Accumulate(SqlInt32 v) { }
            public void Merge(NativeWithRef other) { }
            public SqlInt32 Terminate() { return new SqlInt32(0); }
        }
        """;
}
