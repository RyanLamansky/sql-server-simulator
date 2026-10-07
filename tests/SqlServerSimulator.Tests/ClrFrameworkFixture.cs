using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SqlServerSimulator;

/// <summary>
/// Compiles a SQLCLR assembly the way one is built for real SQL Server —
/// against the .NET Framework 4.8 reference assemblies, so it references
/// <c>mscorlib</c> and <c>System.Data, Version=4.0.0.0</c> and reaches
/// <c>Microsoft.SqlServer.Server.SqlContext</c> / <c>SqlPipe</c> /
/// <c>SqlDataRecord</c> / <c>SqlMetaData</c> and the routine attributes
/// through the latter, and the in-process provider's
/// <c>System.Data.SqlClient</c> types for the context connection. The same
/// classes registered on SQL Server 2025 are what the procedure,
/// table-valued function, aggregate, user-defined type, trigger and context
/// connection behavior was probed with (2026-09-28).
/// </summary>
/// <remarks>
/// The repo keeps no binary fixtures, so the bytes are compiled once per test
/// run from <see cref="Source"/>. <see cref="ClrAssemblyFixture"/> compiles its
/// smaller assemblies for the scalar binding and the static verification the
/// same way.
/// </remarks>
internal static class ClrFrameworkFixture
{
    private static readonly Lazy<byte[]> assembly = new(() => Compile("simclr", Source));

    /// <summary><c>CREATE ASSEMBLY simclr …</c> over the compiled fixture.</summary>
    public static string CreateAssembly => $"create assembly simclr from {ClrAssemblyFixture.HexLiteral(assembly.Value)} with permission_set = safe";

    /// <summary>A CLR-enabled simulation with the fixture registered, and <paramref name="batches"/> run after it.</summary>
    public static Simulation Simulation(params ReadOnlySpan<string> batches)
    {
        var simulation = ClrAssemblyFixture.TrustingSimulation();
        _ = simulation.ExecuteNonQuery(CreateAssembly);
        simulation.ExecuteBatches(batches);
        return simulation;
    }

    /// <summary>Compiles <paramref name="source"/> as the assembly <paramref name="name"/>, against the .NET Framework 4.8 reference assemblies.</summary>
    public static byte[] Compile(string name, string source)
    {
        var referenceDirectory = Path.Combine(AppContext.BaseDirectory, "net48ref");
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp7_3))],
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
        using System.Data.SqlClient;
        using System.Data.SqlTypes;
        using System.Globalization;
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

            [SqlFunction]
            public static SqlString Culture()
            {
                return new SqlString(CultureInfo.CurrentCulture.Name + "|" + CultureInfo.CurrentUICulture.Name + "|"
                    + new DateTime(2026, 1, 5, 13, 4, 5).ToString() + "|" + (-1234.5).ToString("N") + "|"
                    + (-1234.5m).ToString("C") + "|" + (0.5).ToString("P") + "|" + double.PositiveInfinity.ToString());
            }
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
        [SqlUserDefinedAggregate(Format.UserDefined, MaxByteSize = 8000)]
        public struct Leaky : IBinarySerialize
        {
            private int accumulated, sum, reads, writes;
            public void Init() { sum = 0; }
            public void Accumulate(SqlInt32 v) { accumulated++; if (!v.IsNull) sum += v.Value; }
            public void Merge(Leaky other) { sum += other.sum; }
            public SqlString Terminate() { return new SqlString("sum=" + sum + " accumulated=" + accumulated + " reads=" + reads + " writes=" + writes); }
            public void Read(BinaryReader r) { accumulated = r.ReadInt32(); reads = r.ReadInt32() + 1; writes = r.ReadInt32(); }
            public void Write(BinaryWriter w) { w.Write(accumulated); w.Write(reads); w.Write(writes + 1); }
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

        [Serializable]
        [SqlUserDefinedType(Format.Native, IsByteOrdered = true)]
        public struct Point : INullable
        {
            private int x;
            private int y;
            private bool isNull;
            public bool IsNull { get { return isNull; } }
            public static Point Null { get { Point p = new Point(); p.isNull = true; return p; } }
            public override string ToString() { return isNull ? "NULL" : x + "," + y; }
            public static Point Parse(SqlString s)
            {
                if (s.IsNull) return Null;
                string[] parts = s.Value.Split(',');
                Point p = new Point();
                p.x = int.Parse(parts[0]);
                p.y = int.Parse(parts[1]);
                return p;
            }
            public int X { get { return x; } set { x = value; } }
            public int Y { get { return y; } set { y = value; } }
            public SqlDouble Distance() { return new SqlDouble(Math.Sqrt((double)x * x + (double)y * y)); }
            public SqlDouble DistanceTo(Point other) { double dx = x - other.x, dy = y - other.y; return new SqlDouble(Math.Sqrt(dx * dx + dy * dy)); }
            public static Point Make(SqlInt32 x, SqlInt32 y) { Point p = new Point(); p.x = x.Value; p.y = y.Value; return p; }
            public static SqlString Describe() { return new SqlString("points"); }
            [SqlMethod(IsMutator = true, OnNullCall = false)]
            public void Scale(SqlInt32 factor) { x *= factor.Value; y *= factor.Value; }
            public SqlString Boom() { throw new InvalidOperationException("point boom"); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public struct Unordered : INullable
        {
            private int v;
            private bool isNull;
            public bool IsNull { get { return isNull; } }
            public static Unordered Null { get { Unordered u = new Unordered(); u.isNull = true; return u; } }
            public override string ToString() { return v.ToString(); }
            public static Unordered Parse(SqlString s) { if (s.IsNull) return Null; Unordered u = new Unordered(); u.v = int.Parse(s.Value); return u; }
            public int V { get { return v; } }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native, IsByteOrdered = true)]
        public struct Wide : INullable
        {
            private byte b;
            private short s;
            private int i;
            private long l;
            private bool f;
            private float r;
            private double d;
            private sbyte sb;
            private ushort us;
            private uint ui;
            private ulong ul;
            private bool isNull;
            public bool IsNull { get { return isNull; } }
            public static Wide Null { get { Wide w = new Wide(); w.isNull = true; return w; } }
            public override string ToString() { return b + "|" + s + "|" + i + "|" + l + "|" + f + "|" + r.ToString("R") + "|" + d.ToString("R") + "|" + sb + "|" + us + "|" + ui + "|" + ul; }
            public static Wide Parse(SqlString str)
            {
                if (str.IsNull) return Null;
                string[] p = str.Value.Split('|');
                Wide w = new Wide();
                w.b = byte.Parse(p[0]); w.s = short.Parse(p[1]); w.i = int.Parse(p[2]); w.l = long.Parse(p[3]); w.f = bool.Parse(p[4]);
                w.r = float.Parse(p[5]); w.d = double.Parse(p[6]); w.sb = sbyte.Parse(p[7]); w.us = ushort.Parse(p[8]); w.ui = uint.Parse(p[9]); w.ul = ulong.Parse(p[10]);
                return w;
            }
        }

        public struct Inner { public int a; public short b; }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public struct SqlFields : INullable
        {
            private SqlInt32 a;
            private SqlDouble b;
            private SqlBoolean c;
            private Inner inner;
            private SqlMoney m;
            private SqlDateTime dt;
            private SqlByte sb;
            private SqlInt16 s16;
            private SqlInt64 s64;
            private SqlSingle ss;
            private bool isNull;
            public bool IsNull { get { return isNull; } }
            public static SqlFields Null { get { SqlFields w = new SqlFields(); w.isNull = true; return w; } }
            public override string ToString() { return (a.IsNull ? "null" : a.Value.ToString()) + "|" + (m.IsNull ? "null" : m.Value.ToString()) + "|" + inner.b; }
            public static SqlFields Parse(SqlString str)
            {
                SqlFields w = new SqlFields();
                if (str.Value == "nulls") return w;
                w.a = new SqlInt32(1); w.b = new SqlDouble(2.5); w.c = SqlBoolean.True;
                w.inner.a = 5; w.inner.b = -3; w.m = new SqlMoney(12.5m); w.dt = new SqlDateTime(2020, 1, 2, 3, 4, 5);
                w.sb = new SqlByte(7); w.s16 = new SqlInt16(-2); w.s64 = new SqlInt64(9); w.ss = new SqlSingle(1.5f);
                return w;
            }
        }

        [Serializable]
        [SqlUserDefinedType(Format.UserDefined, MaxByteSize = 20)]
        public class Label : INullable, IBinarySerialize
        {
            private string text;
            private bool isNull;
            public bool IsNull { get { return isNull; } }
            public static Label Null { get { Label l = new Label(); l.isNull = true; return l; } }
            public override string ToString() { return isNull ? "NULL" : text; }
            public static Label Parse(SqlString s) { if (s.IsNull) return Null; Label l = new Label(); l.text = s.Value; return l; }
            public void Read(BinaryReader r) { text = r.ReadString(); }
            public void Write(BinaryWriter w) { w.Write(text); }
            public int Length { get { return text.Length; } }
            public SqlString Upper() { return new SqlString(text.ToUpperInvariant()); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.UserDefined, MaxByteSize = -1)]
        public class BigBlob : INullable, IBinarySerialize
        {
            private string text;
            private bool isNull;
            public bool IsNull { get { return isNull; } }
            public static BigBlob Null { get { BigBlob l = new BigBlob(); l.isNull = true; return l; } }
            public override string ToString() { return isNull ? "NULL" : text; }
            public static BigBlob Parse(SqlString s) { if (s.IsNull) return Null; BigBlob l = new BigBlob(); l.text = s.Value; return l; }
            public void Read(BinaryReader r) { text = r.ReadString(); }
            public void Write(BinaryWriter w) { w.Write(text); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public struct UdtNoParse : INullable
        {
            private int v;
            public bool IsNull { get { return false; } }
            public static UdtNoParse Null { get { return new UdtNoParse(); } }
            public override string ToString() { return v.ToString(); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public struct UdtNoNull : INullable
        {
            private int v;
            public bool IsNull { get { return false; } }
            public override string ToString() { return v.ToString(); }
            public static UdtNoNull Parse(SqlString s) { return new UdtNoNull(); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public struct UdtNotNullable
        {
            private int v;
            public static UdtNotNullable Null { get { return new UdtNotNullable(); } }
            public override string ToString() { return v.ToString(); }
            public static UdtNotNullable Parse(SqlString s) { return new UdtNotNullable(); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public struct UdtNativeRef : INullable
        {
            private string v;
            public bool IsNull { get { return false; } }
            public static UdtNativeRef Null { get { return new UdtNativeRef(); } }
            public override string ToString() { return v; }
            public static UdtNativeRef Parse(SqlString s) { return new UdtNativeRef(); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public struct UdtNativeDecimal : INullable
        {
            private decimal v;
            public bool IsNull { get { return false; } }
            public static UdtNativeDecimal Null { get { return new UdtNativeDecimal(); } }
            public override string ToString() { return v.ToString(); }
            public static UdtNativeDecimal Parse(SqlString s) { return new UdtNativeDecimal(); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.Native)]
        public class UdtNativeClass : INullable
        {
            private int v;
            public bool IsNull { get { return false; } }
            public static UdtNativeClass Null { get { return new UdtNativeClass(); } }
            public override string ToString() { return v.ToString(); }
            public static UdtNativeClass Parse(SqlString s) { return new UdtNativeClass(); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.UserDefined, MaxByteSize = 10)]
        public struct UdtNoSerialize : INullable
        {
            private int v;
            public bool IsNull { get { return false; } }
            public static UdtNoSerialize Null { get { return new UdtNoSerialize(); } }
            public override string ToString() { return v.ToString(); }
            public static UdtNoSerialize Parse(SqlString s) { return new UdtNoSerialize(); }
        }

        [Serializable]
        public struct UdtNoAttr : INullable
        {
            private int v;
            public bool IsNull { get { return false; } }
            public static UdtNoAttr Null { get { return new UdtNoAttr(); } }
            public override string ToString() { return v.ToString(); }
            public static UdtNoAttr Parse(SqlString s) { return new UdtNoAttr(); }
        }

        [Serializable]
        [SqlUserDefinedType(Format.UserDefined)]
        public class UdtNoMax : INullable, IBinarySerialize
        {
            public bool IsNull { get { return false; } }
            public static UdtNoMax Null { get { return new UdtNoMax(); } }
            public override string ToString() { return "x"; }
            public static UdtNoMax Parse(SqlString s) { return new UdtNoMax(); }
            public void Read(BinaryReader r) { }
            public void Write(BinaryWriter w) { }
        }

        public static class UdtFuncs
        {
            [SqlFunction]
            public static SqlInt32 PointSum(Point p) { return p.IsNull ? SqlInt32.Null : new SqlInt32(p.X + p.Y); }

            [SqlFunction]
            public static Point MakePoint(SqlInt32 x, SqlInt32 y) { return Point.Make(x, y); }
        }

        public static class Trig
        {
            public static void Report()
            {
                SqlTriggerContext tc = SqlContext.TriggerContext;
                StringBuilder sb = new StringBuilder();
                sb.Append("action=").Append(tc.TriggerAction).Append(" cols=").Append(tc.ColumnCount).Append(" upd=");
                for (int i = 0; i < tc.ColumnCount; i++) sb.Append(tc.IsUpdatedColumn(i) ? '1' : '0');
                SqlContext.Pipe.Send(sb.ToString());
            }

            public static void Throws() { throw new InvalidOperationException("trigger boom"); }

            public static void Rows()
            {
                SqlDataRecord rec = new SqlDataRecord(new SqlMetaData("n", SqlDbType.Int));
                rec.SetInt32(0, 7);
                SqlContext.Pipe.Send(rec);
            }

            public static void Ddl()
            {
                SqlTriggerContext tc = SqlContext.TriggerContext;
                SqlContext.Pipe.Send("action=" + tc.TriggerAction + " cols=" + tc.ColumnCount + " event=" + (tc.EventData.Value.Contains("<EventType>CREATE_TABLE</EventType>") ? "create_table" : "other"));
            }

            public static void EventDataOnDml() { SqlContext.Pipe.Send(SqlContext.TriggerContext.EventData == null ? "null" : "not null"); }

            public static void BadColumn() { SqlContext.Pipe.Send(SqlContext.TriggerContext.IsUpdatedColumn(99).ToString()); }

            public static int ReturnsInt() { return 1; }

            public static void TakesArg(SqlInt32 a) { }

            [SqlProcedure]
            public static void ProcReadsTrigger() { SqlContext.Pipe.Send("proc trig=" + (SqlContext.TriggerContext != null)); }

            [SqlFunction]
            public static SqlBoolean FuncTrig() { return new SqlBoolean(SqlContext.TriggerContext != null); }
        }

        public static class Bcl
        {
            [SqlFunction]
            public static SqlInt32 ParseInt(SqlString s) { return int.Parse(s.Value); }

            [SqlFunction]
            public static SqlInt32 Unbox(SqlString s) { object o = s.Value; return (int)o; }

            [SqlFunction]
            public static SqlDateTime ParseDate(SqlString s) { return DateTime.Parse(s.Value, CultureInfo.InvariantCulture); }

            [SqlFunction]
            public static SqlDateTime ParseDateExact(SqlString s) { return DateTime.ParseExact(s.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture); }

            [SqlFunction]
            public static SqlBinary Base64(SqlString s) { return Convert.FromBase64String(s.Value); }
        }

        public static class Ctx
        {
            static SqlConnection Open() { SqlConnection c = new SqlConnection("context connection=true"); c.Open(); return c; }

            static void Send(string s) { SqlContext.Pipe.Send(s); }

            static string Show(object o) { return o == null ? "(null)" : o is DBNull ? "DBNull" : o.GetType().Name + ":" + Convert.ToString(o, CultureInfo.InvariantCulture); }

            [SqlProcedure]
            public static void Scalar(SqlString sql) { using (SqlConnection c = Open()) { Send(Show(new SqlCommand(sql.Value, c).ExecuteScalar())); } }

            [SqlProcedure]
            public static void NonQuery(SqlString sql) { using (SqlConnection c = Open()) { Send("affected=" + new SqlCommand(sql.Value, c).ExecuteNonQuery()); } }

            [SqlProcedure]
            public static void Reader(SqlString sql)
            {
                using (SqlConnection c = Open())
                using (SqlDataReader r = new SqlCommand(sql.Value, c).ExecuteReader())
                {
                    do
                    {
                        StringBuilder h = new StringBuilder("cols");
                        for (int i = 0; i < r.FieldCount; i++)
                            h.Append(' ').Append(r.GetName(i)).Append(':').Append(r.GetDataTypeName(i)).Append(':').Append(r.GetFieldType(i).Name).Append(':').Append(r.GetProviderSpecificFieldType(i).Name);
                        Send(h.ToString());
                        while (r.Read())
                        {
                            StringBuilder b = new StringBuilder("row");
                            for (int i = 0; i < r.FieldCount; i++)
                                b.Append(' ').Append(r.IsDBNull(i) ? "NULL" : Show(r.GetValue(i))).Append('/').Append(r.GetSqlValue(i).GetType().Name);
                            Send(b.ToString());
                        }
                    } while (r.NextResult());
                    Send("recs=" + r.RecordsAffected);
                }
            }

            [SqlProcedure]
            public static void Typed()
            {
                using (SqlConnection c = Open())
                using (SqlDataReader r = new SqlCommand("select cast(1 as int), cast(2 as bigint), cast(1.5 as decimal(5,2)), 'v', cast('2020-01-02' as date), cast('12:34' as time), cast('<a/>' as xml), cast(null as int), cast(7 as sql_variant)", c).ExecuteReader())
                {
                    r.Read();
                    Send(r.GetInt32(0) + " " + r.GetInt64(1) + " " + r.GetDecimal(2).ToString(CultureInfo.InvariantCulture) + " " + r.GetString(3) + " " + r.GetSqlString(3).LCID + " " + r.GetDateTime(4).ToString("s") + " " + r.GetTimeSpan(5) + " " + r.GetSqlXml(6).Value + " " + r.GetSqlInt32(7).IsNull + " " + Show(r.GetSqlValue(8)));
                    try { r.GetInt32(7); } catch (SqlNullValueException e) { Send(e.Message); }
                    try { r.GetString(0); } catch (InvalidCastException e) { Send(e.Message); }
                }
            }

            [SqlProcedure]
            public static void ExecSend(SqlString sql) { using (SqlConnection c = Open()) { SqlContext.Pipe.ExecuteAndSend(new SqlCommand(sql.Value, c)); Send("after"); } }

            [SqlProcedure]
            public static void SendReader(SqlString sql) { using (SqlConnection c = Open()) { SqlDataReader r = new SqlCommand(sql.Value, c).ExecuteReader(); r.Read(); SqlContext.Pipe.Send(r); r.Close(); } }

            [SqlProcedure]
            public static void Params(SqlInt32 a, out SqlInt32 b)
            {
                using (SqlConnection c = Open())
                {
                    SqlCommand cmd = new SqlCommand("set @o = @i * 2; set @s = replicate(N'z', 10); select sql_variant_property(@t, 'BaseType')", c);
                    cmd.Parameters.AddWithValue("@i", a.Value);
                    cmd.Parameters.AddWithValue("t", "hello");
                    SqlParameter o = cmd.Parameters.Add("@o", SqlDbType.Int);
                    o.Direction = ParameterDirection.Output;
                    SqlParameter s = cmd.Parameters.Add("@s", SqlDbType.NVarChar, 4);
                    s.Direction = ParameterDirection.Output;
                    Send(Show(cmd.ExecuteScalar()) + " " + Show(o.Value) + " " + Show(o.SqlValue) + " " + Show(s.Value));
                    b = (SqlInt32)o.SqlValue;
                }
            }

            [SqlProcedure]
            public static void DecimalParams()
            {
                using (SqlConnection c = Open())
                {
                    SqlCommand cmd = new SqlCommand("select concat(cast(sql_variant_property(@p, 'BaseType') as nvarchar(20)), N' ', cast(sql_variant_property(@q, 'BaseType') as nvarchar(20)), N' ', cast(sql_variant_property(@q, 'Scale') as int))", c);
                    cmd.Parameters.AddWithValue("@p", 12.345m);
                    cmd.Parameters.Add("@q", SqlDbType.Decimal).Value = 12.345m;
                    Send(Show(cmd.ExecuteScalar()));
                }
            }

            [SqlProcedure]
            public static void StoredProc()
            {
                using (SqlConnection c = Open())
                {
                    SqlCommand cmd = new SqlCommand("dbo.tp", c);
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@a", 4);
                    SqlParameter o = cmd.Parameters.Add("@b", SqlDbType.Int);
                    o.Direction = ParameterDirection.Output;
                    SqlParameter rv = cmd.Parameters.Add("@rv", SqlDbType.Int);
                    rv.Direction = ParameterDirection.ReturnValue;
                    Send("n=" + cmd.ExecuteNonQuery() + " b=" + Show(o.Value) + " rv=" + Show(rv.Value));
                }
            }

            [SqlProcedure]
            public static void TwoConns() { using (SqlConnection c = Open()) using (SqlConnection d = Open()) { } }

            [SqlProcedure]
            public static void TwoReaders() { using (SqlConnection c = Open()) using (SqlDataReader r = new SqlCommand("select 1", c).ExecuteReader()) { new SqlCommand("select 2", c).ExecuteScalar(); } }

            [SqlProcedure]
            public static void NonContext() { using (SqlConnection c = new SqlConnection("data source=.;integrated security=true")) { c.Open(); } }

            [SqlProcedure]
            public static void Catch(SqlString sql)
            {
                using (SqlConnection c = Open())
                {
                    try { new SqlCommand(sql.Value, c).ExecuteNonQuery(); Send("no error"); }
                    catch (SqlException e) { Send("caught " + e.Number + " " + e.Class + " " + e.State + " " + e.LineNumber + " " + e.Message); }
                    Send("then " + Show(new SqlCommand("select @@trancount", c).ExecuteScalar()));
                }
            }

            [SqlProcedure]
            public static void Messages() { using (SqlConnection c = Open()) { c.InfoMessage += delegate(object s, SqlInfoMessageEventArgs e) { Send("info: " + e.Message); }; new SqlCommand("print 'hello'; raiserror('ten', 10, 1)", c).ExecuteNonQuery(); } }

            [SqlProcedure]
            public static void Tran(SqlBoolean commit)
            {
                using (SqlConnection c = Open())
                {
                    SqlTransaction t = c.BeginTransaction();
                    Send(Show(new SqlCommand("insert log values (42); select @@trancount", c, t).ExecuteScalar()));
                    if (commit.Value) t.Commit(); else t.Rollback();
                }
            }

            [SqlProcedure]
            public static void Props()
            {
                using (SqlConnection c = new SqlConnection("context connection=true"))
                {
                    Send(c.State + " [" + c.Database + "]");
                    c.Open();
                    Send(c.State + " [" + c.Database + "] " + c.ServerVersion);
                    c.ChangeDatabase("master");
                    Send(Show(new SqlCommand("select db_name()", c).ExecuteScalar()));
                }
            }

            [SqlFunction(DataAccess = DataAccessKind.Read)]
            public static SqlInt32 FnRead(SqlString sql) { using (SqlConnection c = Open()) { return new SqlInt32((int)new SqlCommand(sql.Value, c).ExecuteScalar()); } }

            [SqlFunction]
            public static SqlInt32 FnNone(SqlString sql) { using (SqlConnection c = Open()) { return new SqlInt32((int)new SqlCommand(sql.Value, c).ExecuteScalar()); } }

            [SqlFunction(SystemDataAccess = SystemDataAccessKind.Read)]
            public static SqlInt32 FnSys(SqlString sql) { using (SqlConnection c = Open()) { return new SqlInt32((int)new SqlCommand(sql.Value, c).ExecuteScalar()); } }

            [SqlFunction(DataAccess = DataAccessKind.Read)]
            public static SqlInt32 FnWrite(SqlString sql) { using (SqlConnection c = Open()) { return new SqlInt32(new SqlCommand(sql.Value, c).ExecuteNonQuery()); } }

            [SqlFunction(DataAccess = DataAccessKind.Read)]
            public static SqlBoolean FnPipe() { return new SqlBoolean(SqlContext.Pipe == null && SqlContext.TriggerContext == null); }

            [SqlFunction(DataAccess = DataAccessKind.Read, FillRowMethodName = "FillRow")]
            public static IEnumerable TvfRead(SqlString sql)
            {
                ArrayList l = new ArrayList();
                using (SqlConnection c = Open()) using (SqlDataReader r = new SqlCommand(sql.Value, c).ExecuteReader()) { while (r.Read()) l.Add(r.GetInt32(0)); }
                return l;
            }

            public static void FillRow(object row, out SqlInt32 v) { v = new SqlInt32((int)row); }

            public static void TrigCount()
            {
                using (SqlConnection c = Open())
                {
                    Send("ins=" + Show(new SqlCommand("select count(*) from inserted", c).ExecuteScalar()) + " del=" + Show(new SqlCommand("select count(*) from deleted", c).ExecuteScalar()));
                }
            }

            public static void TrigSend() { using (SqlConnection c = Open()) { SqlContext.Pipe.ExecuteAndSend(new SqlCommand("select a from inserted order by a", c)); } }

            public static void TrigLog() { using (SqlConnection c = Open()) { new SqlCommand("insert log select a from inserted", c).ExecuteNonQuery(); } }

            public static void TrigNest() { using (SqlConnection c = Open()) { Send(Show(new SqlCommand("select @@nestlevel * 100 + @@trancount * 10 + trigger_nestlevel()", c).ExecuteScalar())); } }

            public static void TrigRollback() { using (SqlConnection c = Open()) { new SqlCommand("rollback", c).ExecuteNonQuery(); } }

            public static void TrigCatch() { using (SqlConnection c = Open()) { try { new SqlCommand("select 1/0", c).ExecuteNonQuery(); } catch (SqlException) { } } }

            public static void TrigDdl() { using (SqlConnection c = Open()) { Send(Show(new SqlCommand("select eventdata().value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(100)')", c).ExecuteScalar())); } }
        }
        """;
}
