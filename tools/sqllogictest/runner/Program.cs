using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlServerSimulator;

namespace SltRunner;

sealed class ExecResult
{
    public bool Ok;
    public string ErrKind = "";     // exception type
    public int ErrNumber;           // SQL Msg number when available
    public string ErrMessage = "";
    public int RowsAffected = -1;
    public List<object[]> Rows = new();
    public string[] FieldTypes = [];
    public string[] DataTypeNames = [];
    public bool TimedOut;
}

/// <summary>
/// Everything one script's run accumulates. Held per script rather than in
/// statics so replay can run scripts in parallel in one process; the counters
/// fold into the global tallies under a lock when the script finishes, which
/// leaves single-threaded (differential) results bit-identical to the
/// statics-based version.
/// </summary>
sealed class ScriptCtx
{
    public readonly Dictionary<string, int> Classes = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> BothErrorPairs = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> FileAgreement = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> ErrBreakdown = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Emitted = new(StringComparer.Ordinal);
    public readonly List<string> Findings = new();      // serialized, flushed once at script end
    public readonly List<string> Register = new();      // known-divergent register lines
    public readonly List<string> RecordClasses = new(); // divergence classes bumped for the current record
    readonly List<string> emittedThisRecord = new();
    int findingMark;

    public long Records, Queries, Statements, Skipped;
    public int SimTimeouts;
    public bool RowsAffectedLogged;

    public void Bump(string k)
    {
        Classes[k] = Classes.TryGetValue(k, out var c) ? c + 1 : 1;
        if (Program.IsDivergent(k)) RecordClasses.Add(k);
    }

    public void BumpIn(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var c) ? c + 1 : 1;

    public void BeginRecord()
    {
        RecordClasses.Clear();
        emittedThisRecord.Clear();
        findingMark = Findings.Count;
    }

    /// <summary>The record's divergence signature: "" when it agreed on every axis.</summary>
    public string RecordDivergence() => RecordClasses.Count == 0 ? "" : string.Join("+", RecordClasses);

    public void Emit(object o) => Emit(o, forced: false);

    public void Emit(object o, bool forced)
    {
        var cls = o.GetType().GetProperty("cls")?.GetValue(o) as string ?? "?";
        var n = Emitted.TryGetValue(cls, out var c) ? c : 0;
        Emitted[cls] = n + 1;
        emittedThisRecord.Add(cls);
        if (!forced && n >= Program.EmitCap) return;
        Findings.Add(JsonSerializer.Serialize(o, Program.JsonOpts));
    }

    /// <summary>
    /// Drop everything this record emitted, cap accounting included: used when a
    /// divergence is exactly the one the reference recorded, so the register --
    /// not the findings log -- carries it.
    /// </summary>
    public void RollbackRecordFindings()
    {
        Findings.RemoveRange(findingMark, Findings.Count - findingMark);
        foreach (var k in emittedThisRecord)
            if (Emitted.TryGetValue(k, out var c) && c > 0) Emitted[k] = c - 1;
        emittedThisRecord.Clear();
    }
}

static class Program
{
    // The live server comes from SLT_SERVER, a SqlClient connection string
    // naming the server and credentials (catalog, timeout and pooling are set
    // here). Only the differential, capture and --repl modes open it.
    static string ServerCs => Environment.GetEnvironmentVariable("SLT_SERVER")
        ?? throw new InvalidOperationException("Set SLT_SERVER to a SqlClient connection string for the reference SQL Server (see README.md).");
    static string MasterCs => new SqlConnectionStringBuilder(ServerCs) { InitialCatalog = "master", ConnectTimeout = 15, Pooling = true }.ConnectionString;
    // Per-process database name, so several shards can sweep the same server
    // concurrently without dropping each other's work database.
    static string RealDb = "slt_work";
    static string WorkCs => new SqlConnectionStringBuilder(ServerCs) { InitialCatalog = RealDb, ConnectTimeout = 15, Pooling = false }.ConnectionString;

    static StreamWriter logStream;
    static StreamWriter registerStream;
    static readonly Dictionary<string, int> Classes = new(StringComparer.Ordinal);
    static readonly Dictionary<string, int> BothErrorPairs = new(StringComparer.Ordinal);
    static readonly Dictionary<string, int> FileAgreement = new(StringComparer.Ordinal);
    static readonly Dictionary<string, int> ErrBreakdown = new(StringComparer.Ordinal);
    static long totalRecords, totalQueries, totalStatements, skipped;
    static int simTimeouts;
    static int scriptsDone;
    static int staleReferences;
    internal static int EmitCap = 6;
    static string serverVersion = "";
    static readonly object Sync = new();

    internal static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// Classes that mean the two sides disagreed on some axis. Membership decides
    /// what the capture freezes into the known-divergent register and what replay
    /// re-checks against it; the agreement classes (match, match_after_sort,
    /// both_ok, both_error*_same_msg) are deliberately absent.
    /// </summary>
    internal static bool IsDivergent(string cls)
    {
        var i = cls.IndexOf(':');
        var suffix = i < 0 ? cls : cls[(i + 1)..];
        return suffix switch
        {
            "OVERPERMISSIVE_real_error_sim_ok" or "sim_error_real_ok" or "sim_timeout_perf"
            or "both_error_diff_msg" or "ORDER_DIFF_nosort" or "VALUE_DIFF" or "float_epsilon_diff"
            or "row_count_diff" or "column_count_diff" or "clr_type_diff" or "store_type_name_diff"
            or "rowsaffected_diff" or "sim_timeout" => true,
            _ => false,
        };
    }

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--repl")
            return Repl(args[1]);
        // Script paths in a file list are relative to the data directory, which
        // the scripts make the working directory.
        var root = Environment.CurrentDirectory;
        var listFile = Arg(args, "--files") ?? throw new ArgumentException("--files required");
        var outDir = Arg(args, "--out") ?? Path.Combine(root, "results");
        Directory.CreateDirectory(outDir);
        var maxRecords = int.Parse(Arg(args, "--max-records") ?? "2000000", CultureInfo.InvariantCulture);
        ScriptParser.IgnoreHalt = args.Contains("--ignore-halt");
        EmitCap = int.Parse(Arg(args, "--emit-cap") ?? "6", CultureInfo.InvariantCulture);
        RealDb = Arg(args, "--db") ?? "slt_work";
        var captureDir = Arg(args, "--capture");
        var replayDir = Arg(args, "--replay");
        var shardTag = Arg(args, "--shard-tag") ?? "";
        var parallel = int.Parse(Arg(args, "--parallel") ?? Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (captureDir is not null && replayDir is not null)
            throw new ArgumentException("--capture and --replay are different modes");

        var files = File.ReadAllLines(listFile)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        var jsonlPath = Path.Combine(outDir, "findings.jsonl");
        logStream = new StreamWriter(jsonlPath, append: false);

        var sw = Stopwatch.StartNew();
        var header = new StringBuilder();
        if (replayDir is not null)
        {
            header.Append(CultureInfo.InvariantCulture, $"mode=replay ref={replayDir} parallel={parallel} serverGC={System.Runtime.GCSettings.IsServerGC}");
            ReplayAll(files, root, replayDir, maxRecords, parallel, header);
        }
        else
        {
            if (captureDir is not null)
            {
                Directory.CreateDirectory(captureDir);
                serverVersion = ReadServerVersion();
                registerStream = new StreamWriter(RegisterPath(captureDir, shardTag), append: false);
                // First line only: the reference file keeps the whole @@VERSION.
                header.Append(CultureInfo.InvariantCulture, $"mode=capture ref={captureDir} register={Path.GetFileName(RegisterPath(captureDir, shardTag))} server=\"{serverVersion.Split('\n')[0].Trim()}\"");
            }
            else
            {
                header.Append("mode=differential");
            }
            DifferentialAll(files, root, maxRecords, captureDir, sw);
        }
        sw.Stop();

        logStream.Flush();
        logStream.Dispose();
        registerStream?.Flush();
        registerStream?.Dispose();

        var summary = new StringBuilder();
        summary.AppendLine(header.ToString());
        summary.AppendLine(CultureInfo.InvariantCulture, $"scripts={files.Length} records={totalRecords} queries={totalQueries} statements={totalStatements} skipped(conditional)={skipped}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"elapsed={sw.Elapsed.TotalSeconds:F1}s  records/sec={totalRecords / Math.Max(1.0, sw.Elapsed.TotalSeconds):F1}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"sim timeouts={simTimeouts}");
        if (staleReferences > 0)
            summary.AppendLine(CultureInfo.InvariantCulture, $"STALE REFERENCES={staleReferences} -- re-capture before trusting this run");
        summary.AppendLine("--- outcome classes ---");
        foreach (var kv in Classes.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            summary.AppendLine(CultureInfo.InvariantCulture, $"{kv.Value,10}  {kv.Key}");
        summary.AppendLine("--- sim-error / type-divergence breakdown ---");
        foreach (var kv in ErrBreakdown.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            summary.AppendLine(CultureInfo.InvariantCulture, $"{kv.Value,10}  {kv.Key}");
        summary.AppendLine("--- both-error msg pairs (real -> sim), top 60 ---");
        foreach (var kv in BothErrorPairs.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(60))
            summary.AppendLine(CultureInfo.InvariantCulture, $"{kv.Value,10}  {kv.Key}");
        summary.AppendLine("--- stored-file agreement (query records where both engines succeeded) ---");
        foreach (var kv in FileAgreement.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            summary.AppendLine(CultureInfo.InvariantCulture, $"{kv.Value,10}  {kv.Key}");

        var summaryText = summary.ToString();
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summaryText);
        Console.WriteLine(summaryText);
        return staleReferences > 0 ? 2 : 0;
    }

    // ---------------------------------------------------------------- driving

    static void DifferentialAll(string[] files, string root, int maxRecords, string captureDir, Stopwatch sw)
    {
        var scriptIndex = 0;
        foreach (var rel in files)
        {
            scriptIndex++;
            var path = Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                Console.Error.WriteLine($"[skip empty/missing] {rel}");
                continue;
            }
            var ctx = new ScriptCtx();
            try
            {
                RunScript(ctx, rel, path, maxRecords, captureDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[script aborted] {rel}: {ex.GetType().Name}: {ex.Message}");
                ctx.BeginRecord();
                ctx.Emit(new { cls = "script_abort", script = rel, err = ex.GetType().Name + ": " + ex.Message });
            }
            Merge(ctx);
            Console.Error.WriteLine($"[{scriptIndex}/{files.Length}] {rel}  recs={totalRecords} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        }
    }

    static void ReplayAll(string[] files, string root, string refDir, int maxRecords, int parallel, StringBuilder header)
    {
        // Take the halt policy from the reference: it decides which records exist
        // at all, so replaying under a different one would misalign every script.
        var stamped = false;
        foreach (var rel in files)
        {
            var rp = Reference.PathFor(refDir, rel);
            if (!File.Exists(rp)) continue;
            var probe = Reference.Read(rp);
            ScriptParser.IgnoreHalt = probe.IgnoreHalt;
            header.Append(CultureInfo.InvariantCulture, $" ignore-halt={probe.IgnoreHalt} captured={new DateTime(probe.CapturedUtcTicks, DateTimeKind.Utc):u} server=\"{probe.ServerVersion.Split('\n')[0].Trim()}\"");
            stamped = true;
            break;
        }
        if (!stamped) throw new StaleReferenceException($"no reference files found under {refDir} for this list");

        // Script threads are dedicated rather than pool threads: each record's
        // execution runs on a pool thread of its own (the timeout wrapper), and
        // pool starvation would show up as fake sim timeouts.
        ThreadPool.SetMinThreads(parallel * 4 + 16, parallel * 4 + 16);
        var queue = new Queue<string>(files);
        var workers = new List<Thread>();
        var sw = Stopwatch.StartNew();
        for (var w = 0; w < Math.Max(1, parallel); w++)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    string rel;
                    lock (queue)
                    {
                        if (queue.Count == 0) return;
                        rel = queue.Dequeue();
                    }
                    var path = Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel);
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    {
                        lock (Sync) Console.Error.WriteLine($"[skip empty/missing] {rel}");
                        continue;
                    }
                    var ctx = new ScriptCtx();
                    try
                    {
                        ReplayScript(ctx, rel, path, refDir, maxRecords);
                    }
                    catch (StaleReferenceException ex)
                    {
                        lock (Sync)
                        {
                            staleReferences++;
                            Console.Error.WriteLine($"[STALE REFERENCE] {rel}: {ex.Message}");
                        }
                        ctx.BeginRecord();
                        ctx.Emit(new { cls = "stale_reference", script = rel, err = ex.Message }, forced: true);
                        ctx.Bump("stale_reference");
                    }
                    catch (Exception ex)
                    {
                        lock (Sync) Console.Error.WriteLine($"[script aborted] {rel}: {ex.GetType().Name}: {ex.Message}");
                        ctx.BeginRecord();
                        ctx.Emit(new { cls = "script_abort", script = rel, err = ex.GetType().Name + ": " + ex.Message });
                    }
                    Merge(ctx);
                    lock (Sync)
                        Console.Error.WriteLine($"[{scriptsDone}/{files.Length}] {rel}  recs={totalRecords} elapsed={sw.Elapsed.TotalSeconds:F1}s");
                }
            }, 16 * 1024 * 1024);
            t.IsBackground = false;
            workers.Add(t);
            t.Start();
        }
        foreach (var t in workers) t.Join();
    }

    static void Merge(ScriptCtx ctx)
    {
        lock (Sync)
        {
            foreach (var kv in ctx.Classes) Classes[kv.Key] = Classes.TryGetValue(kv.Key, out var a) ? a + kv.Value : kv.Value;
            foreach (var kv in ctx.BothErrorPairs) BothErrorPairs[kv.Key] = BothErrorPairs.TryGetValue(kv.Key, out var b) ? b + kv.Value : kv.Value;
            foreach (var kv in ctx.FileAgreement) FileAgreement[kv.Key] = FileAgreement.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;
            foreach (var kv in ctx.ErrBreakdown) ErrBreakdown[kv.Key] = ErrBreakdown.TryGetValue(kv.Key, out var d) ? d + kv.Value : kv.Value;
            totalRecords += ctx.Records;
            totalQueries += ctx.Queries;
            totalStatements += ctx.Statements;
            skipped += ctx.Skipped;
            simTimeouts += ctx.SimTimeouts;
            scriptsDone++;
            foreach (var line in ctx.Findings) logStream!.WriteLine(line);
            logStream!.Flush();
            if (registerStream is not null)
            {
                foreach (var line in ctx.Register) registerStream.WriteLine(line);
                registerStream.Flush();
            }
        }
    }

    /// <summary>Ad-hoc probe: run ";;"-separated batches on sim and real, print both.</summary>
    static int Repl(string file)
    {
        var text = File.ReadAllText(file);
        var batches = text.Split(";;", StringSplitOptions.RemoveEmptyEntries);
        using var master = new SqlConnection(MasterCs);
        master.Open();
        Exec(master, $"IF DB_ID('{RealDb}') IS NOT NULL BEGIN ALTER DATABASE [{RealDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{RealDb}]; END");
        Exec(master, $"CREATE DATABASE [{RealDb}]");
        var sim = new Simulation();
        using var simConn = sim.CreateDbConnection();
        simConn.Open();
        using var realConn = new SqlConnection(WorkCs);
        realConn.Open();
        foreach (var raw in batches)
        {
            var sql = raw.Trim();
            if (sql.Length == 0) continue;
            Console.WriteLine("### " + sql.Replace("\n", " "));
            if (Environment.GetEnvironmentVariable("SLT_DIAG") == "1")
            {
                try
                {
                    using var dcmd = simConn.CreateCommand();
                    dcmd.CommandText = sql;
                    using var dr = dcmd.ExecuteReader();
                    var sets = 0;
                    do { sets++; Console.WriteLine($"  DIAG set#{sets} fieldCount={dr.FieldCount} hasRows={dr.HasRows}"); while (dr.Read()) { } } while (dr.NextResult());
                    Console.WriteLine($"  DIAG totalSets={sets} recordsAffected={dr.RecordsAffected}");
                }
                catch (Exception dex) { Console.WriteLine($"  DIAG threw {dex.GetType().Name}: {Trunc(dex.Message, 200)}"); }
            }
            if (Environment.GetEnvironmentVariable("SLT_NONQUERY") == "1")
            {
                foreach (var (lbl, cn) in new[] { ("REAL", (DbConnection)realConn), ("SIM ", simConn) })
                {
                    try
                    {
                        using var nc = cn.CreateCommand();
                        nc.CommandText = sql;
                        Console.WriteLine($"  NONQUERY {lbl}: {nc.ExecuteNonQuery()}");
                    }
                    catch (Exception nex) { Console.WriteLine($"  NONQUERY {lbl}: threw {nex.GetType().Name}: {Trunc(nex.Message, 160)}"); }
                }
            }
            foreach (var (label, conn, isSim) in new[] { ("REAL", (DbConnection)realConn, false), ("SIM ", simConn, true) })
            {
                var r = Run(conn, sql, isSim);
                if (!r.Ok) { Console.WriteLine($"  {label}: ERROR {ErrKey(r)}: {Trunc(r.ErrMessage, 300)}"); continue; }
                Console.WriteLine($"  {label}: ok cols=[{string.Join(",", r.FieldTypes.Zip(r.DataTypeNames).Select(t => t.Second + "/" + t.First))}] rows={r.Rows.Count} affected={r.RowsAffected}");
                foreach (var line in Sample(r)) Console.WriteLine("        " + line);
            }
            Console.WriteLine();
        }
        return 0;
    }

    static string Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static string RegisterPath(string refDir, string shardTag) =>
        Path.Combine(refDir, shardTag.Length == 0 ? "known-divergent.jsonl" : $"known-divergent-{shardTag}.jsonl");

    static string ReadServerVersion()
    {
        using var master = new SqlConnection(MasterCs);
        master.Open();
        using var cmd = master.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION";
        return (cmd.ExecuteScalar() as string ?? "").Replace("\r", "").Trim();
    }

    // ----------------------------------------------------------- differential

    static void RunScript(ScriptCtx ctx, string rel, string path, int maxRecords, string captureDir)
    {
        var records = ScriptParser.Parse(path);
        if (records.Count == 0) return;

        ScriptReference sref = captureDir is null ? null : new ScriptReference
        {
            CapturedUtcTicks = DateTime.UtcNow.Ticks,
            ServerVersion = serverVersion,
            IgnoreHalt = ScriptParser.IgnoreHalt,
            Rel = rel,
        };

        try
        {
            // Fresh real database.
            using (var master = new SqlConnection(MasterCs))
            {
                master.Open();
                Exec(master, $"IF DB_ID('{RealDb}') IS NOT NULL BEGIN ALTER DATABASE [{RealDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{RealDb}]; END");
                Exec(master, $"CREATE DATABASE [{RealDb}]");
            }

            var sim = new Simulation();
            using var simConn = sim.CreateDbConnection();
            simConn.Open();
            using var realConn = new SqlConnection(WorkCs);
            realConn.Open();

            var tainted = false;
            var n = 0;
            foreach (var rec in records)
            {
                if (n++ >= maxRecords) { if (sref is not null) sref.TruncatedReason = "max-records"; break; }
                ctx.Records++;
                if (!ScriptParser.AppliesToUs(rec))
                {
                    ctx.Skipped++;
                    sref?.Records.Add(new RefRecord { Line = rec.Line, Kind = rec.Kind, SqlHash = Reference.Hash(rec.Sql), Skipped = true });
                    continue;
                }

                var real = Run(realConn, rec.Sql, simulator: false);
                var simr = Run(simConn, rec.Sql, simulator: true);

                var refRec = sref is null ? null : Capture(rec, real);

                if (simr.TimedOut)
                {
                    ctx.SimTimeouts++;
                    ctx.BeginRecord();
                    ctx.Emit(new { cls = "sim_timeout", script = rel, line = rec.Line, sql = rec.Sql });
                    ctx.Bump("sim_timeout");
                    if (refRec is not null)
                    {
                        MarkDivergent(ctx, rec, refRec, simr, rel, real);
                        sref!.Records.Add(refRec);
                        sref.TruncatedReason = "sim_timeout";
                    }
                    break; // sim connection state is unknown; abandon the script
                }

                if (rec.Kind == "statement") ctx.Statements++; else ctx.Queries++;

                ctx.BeginRecord();
                Compare(ctx, rel, rec, real, simr, ref tainted, refRec?.RealMatchesFile);
                if (refRec is not null)
                {
                    MarkDivergent(ctx, rec, refRec, simr, rel, real);
                    sref!.Records.Add(refRec);
                }
            }
        }
        catch
        {
            // A partial reference is still worth keeping -- naming why it stops
            // short is what keeps replay from reporting it as a stale one.
            if (sref is not null) sref.TruncatedReason = "script_abort";
            throw;
        }
        finally
        {
            if (sref is not null) Reference.Write(Reference.PathFor(captureDir!, rel), sref);
        }
    }

    /// <summary>Freeze real's outcome for one record: exactly the axes Compare reads.</summary>
    static RefRecord Capture(SltRecord rec, ExecResult real)
    {
        var r = new RefRecord
        {
            Line = rec.Line,
            Kind = rec.Kind,
            SqlHash = Reference.Hash(rec.Sql),
            Ok = real.Ok,
        };
        if (real.Ok)
        {
            r.RowsAffected = real.RowsAffected;
            r.FieldTypes = real.FieldTypes;
            r.DataTypeNames = real.DataTypeNames;
            r.Rows = new List<object[]>(real.Rows.Count);
            foreach (var row in real.Rows)
            {
                var line = new object[row.Length];
                for (var i = 0; i < row.Length; i++) line[i] = Render.Raw(row[i]);
                r.Rows.Add(line);
            }
            // The stored-file tie-breaker is precomputed here (and unconditionally,
            // taint included) so replay never needs the canonical SLT rendering of
            // real's side -- it only ever has the rendered strings.
            if (rec.Kind == "query" && rec.TypeString.Length > 0)
                r.RealMatchesFile = MatchesFile(Canonical(real, rec), rec);
        }
        else
        {
            r.ErrNumber = real.ErrNumber;
            r.ErrKind = real.ErrKind;
            r.ErrMessage = Trunc(real.ErrMessage, 400);
        }
        return r;
    }

    static void MarkDivergent(ScriptCtx ctx, SltRecord rec, RefRecord refRec, ExecResult sim, string rel, ExecResult real)
    {
        var div = ctx.RecordDivergence();
        if (div.Length == 0) return;
        refRec.KnownDivergent = true;
        refRec.DivClass = div;
        refRec.DivSimErr = sim.Ok ? "" : ErrKey(sim);
        // The register is a census, not a sample: no emit cap, so a change in the
        // known-divergent set is always visible.
        ctx.Register.Add(JsonSerializer.Serialize(new
        {
            script = rel,
            line = rec.Line,
            kind = rec.Kind,
            cls = div,
            simErr = refRec.DivSimErr,
            simMsg = sim.Ok ? "" : Trunc(sim.ErrMessage, 400),
            realErr = real.Ok ? "" : ErrKey(real),
            realMsg = real.Ok ? "" : Trunc(real.ErrMessage, 400),
            sql = rec.Sql,
        }, JsonOpts));
    }

    // ---------------------------------------------------------------- replay

    static void ReplayScript(ScriptCtx ctx, string rel, string path, string refDir, int maxRecords)
    {
        var records = ScriptParser.Parse(path);
        if (records.Count == 0) return;

        var refPath = Reference.PathFor(refDir, rel);
        if (!File.Exists(refPath))
            throw new StaleReferenceException($"no reference at {refPath} -- re-capture");
        var sref = Reference.Read(refPath);
        if (sref.IgnoreHalt != ScriptParser.IgnoreHalt)
            throw new StaleReferenceException($"reference captured with --ignore-halt={sref.IgnoreHalt}, replaying with {ScriptParser.IgnoreHalt} -- re-capture");

        var sim = new Simulation();
        using var simConn = sim.CreateDbConnection();
        simConn.Open();

        var tainted = false;
        var n = 0;
        var stoppedEarly = false;
        foreach (var rec in records)
        {
            if (n >= maxRecords) { stoppedEarly = true; break; }
            if (n >= sref.Records.Count)
            {
                if (sref.TruncatedReason.Length == 0)
                    throw new StaleReferenceException($"script has {records.Count} records, reference has {sref.Records.Count} -- re-capture");
                // Capture stopped early on purpose; the sim got further this time.
                ctx.BeginRecord();
                ctx.Emit(new { cls = "ref_truncated", script = rel, line = rec.Line, reason = sref.TruncatedReason, refRecords = sref.Records.Count, scriptRecords = records.Count }, forced: true);
                ctx.Bump("ref_truncated");
                stoppedEarly = true;
                break;
            }
            var refRec = sref.Records[n];
            n++;

            if (refRec.Line != rec.Line || refRec.Kind != rec.Kind || refRec.SqlHash != Reference.Hash(rec.Sql))
                throw new StaleReferenceException(
                    $"reference stale -- re-capture: record #{n} is {rec.Kind}@{rec.Line} hash {Reference.Hash(rec.Sql):x8}, reference has {refRec.Kind}@{refRec.Line} hash {refRec.SqlHash:x8}");

            ctx.Records++;
            var applies = ScriptParser.AppliesToUs(rec);
            if (applies != !refRec.Skipped)
                throw new StaleReferenceException($"reference stale -- re-capture: record #{n} ({rec.Kind}@{rec.Line}) skip state flipped (reference skipped={refRec.Skipped})");
            if (!applies) { ctx.Skipped++; continue; }

            var simr = Run(simConn, rec.Sql, simulator: true);
            if (simr.TimedOut)
            {
                ctx.SimTimeouts++;
                ctx.BeginRecord();
                ctx.Emit(new { cls = "sim_timeout", script = rel, line = rec.Line, sql = rec.Sql });
                ctx.Bump("sim_timeout");
                CheckRegister(ctx, rel, rec, refRec, simr);
                stoppedEarly = true;
                break; // sim connection state is unknown; abandon the script
            }

            if (rec.Kind == "statement") ctx.Statements++; else ctx.Queries++;

            var real = Rehydrate(refRec);
            ctx.BeginRecord();
            Compare(ctx, rel, rec, real, simr, ref tainted, refRec.RealMatchesFile);
            CheckRegister(ctx, rel, rec, refRec, simr);
        }

        // A script that lost records is as stale as one whose text changed: the
        // loop would otherwise just end, leaving the missing records uncompared
        // and the run looking clean.
        if (!stoppedEarly && n < sref.Records.Count)
            throw new StaleReferenceException($"script has {records.Count} records, reference has {sref.Records.Count} -- re-capture");
    }

    /// <summary>
    /// The reference's real side as an ExecResult. Rows hold the strings rendered
    /// at capture, so Flatten/Sample over them behave exactly as they did against
    /// the live rows (Render.Raw of a string is the string).
    /// </summary>
    static ExecResult Rehydrate(RefRecord r) => new()
    {
        Ok = r.Ok,
        ErrKind = r.ErrKind,
        ErrNumber = r.ErrNumber,
        ErrMessage = r.ErrMessage,
        RowsAffected = r.RowsAffected,
        Rows = r.Rows,
        FieldTypes = r.FieldTypes,
        DataTypeNames = r.DataTypeNames,
    };

    /// <summary>
    /// Reconcile this record against the known-divergent register. The ordinary
    /// classes are tallied either way, so a replay summary lines up with the
    /// capture's differential summary record for record; what the register adds is
    /// whether the divergence is the frozen one (findings suppressed) or a changed
    /// one (always reported).
    /// </summary>
    static void CheckRegister(ScriptCtx ctx, string rel, SltRecord rec, RefRecord refRec, ExecResult sim)
    {
        var div = ctx.RecordDivergence();
        var simErr = sim.Ok ? "" : ErrKey(sim);
        if (!refRec.KnownDivergent)
        {
            if (div.Length > 0) ctx.Bump("newly_divergent");
            return;
        }
        if (div == refRec.DivClass && simErr == refRec.DivSimErr)
        {
            ctx.Bump("known_divergent_match");
            ctx.RollbackRecordFindings();
            return;
        }
        ctx.Bump("known_divergent_changed");
        ctx.Emit(new
        {
            cls = "known_divergent_changed",
            script = rel,
            line = rec.Line,
            kind = rec.Kind,
            wasClass = refRec.DivClass,
            wasSimErr = refRec.DivSimErr,
            nowClass = div.Length == 0 ? "(agrees)" : div,
            nowSimErr = simErr,
            nowSimMsg = sim.Ok ? "" : Trunc(sim.ErrMessage, 400),
            sql = rec.Sql,
        }, forced: true);
    }

    // ------------------------------------------------------------ comparison

    /// <param name="realMatchesFileOverride">
    /// Precomputed stored-file verdict for real's side. Supplied on the capture and
    /// replay paths (replay holds only rendered strings, so it cannot recompute it);
    /// null on a plain differential run, which computes it as before.
    /// </param>
    static void Compare(ScriptCtx ctx, string rel, SltRecord rec, ExecResult real, ExecResult sim, ref bool tainted, bool? realMatchesFileOverride)
    {
        var isQuery = rec.Kind == "query";
        var prefix = isQuery ? "q" : "s";

        if (!real.Ok && !sim.Ok)
        {
            // A statement record that errored on both sides can still have applied
            // a different prefix of its batch on each engine (real compiles the
            // whole batch first, the simulator dispatches statement by statement),
            // so table state may now differ: taint from here on.
            if (!isQuery) tainted = true;
            ctx.Bump(prefix + ":both_error");
            ctx.BumpIn(ctx.BothErrorPairs, $"real:{ErrKey(real)} | sim:{ErrKey(sim)}");
            if (real.ErrNumber != 0 && real.ErrNumber == sim.ErrNumber)
                ctx.Bump(prefix + ":both_error_same_msg");
            else
            {
                ctx.Bump(prefix + ":both_error_diff_msg");
                ctx.Emit(new { cls = "both_error_diff_msg", script = rel, line = rec.Line, sql = rec.Sql, real = ErrKey(real), realMsg = Trunc(real.ErrMessage, 250), sim = ErrKey(sim), simMsg = Trunc(sim.ErrMessage, 250), tainted });
            }
            if (!rec.ExpectOk && !isQuery) ctx.Bump("s:both_error_file_expected_error");
            return;
        }

        if (!real.Ok && sim.Ok)
        {
            ctx.Bump(prefix + ":OVERPERMISSIVE_real_error_sim_ok");
            ctx.Emit(new
            {
                cls = "over_permissive",
                script = rel,
                line = rec.Line,
                kind = rec.Kind,
                fileExpect = isQuery ? "query" : (rec.ExpectOk ? "ok" : "error"),
                sql = rec.Sql,
                realErr = ErrKey(real),
                realMsg = Trunc(real.ErrMessage, 400),
                simRows = isQuery ? Sample(sim) : null,
                simRowsAffected = sim.RowsAffected,
                tainted,
            });
            if (!isQuery) tainted = true;
            return;
        }

        if (real.Ok && !sim.Ok && sim.ErrNumber == -2)
        {
            ctx.Bump(prefix + ":sim_timeout_perf");
            ctx.Emit(new { cls = "sim_timeout_perf", script = rel, line = rec.Line, sql = rec.Sql, realRows = real.Rows.Count, tainted });
            return;
        }

        if (real.Ok && !sim.Ok)
        {
            ctx.Bump(prefix + ":sim_error_real_ok");
            ctx.BumpIn(ctx.ErrBreakdown, prefix + ":sim_error_real_ok " + ErrKey(sim));
            ctx.Emit(new
            {
                cls = "sim_error_real_ok",
                script = rel,
                line = rec.Line,
                kind = rec.Kind,
                sql = rec.Sql,
                simErr = ErrKey(sim),
                simMsg = Trunc(sim.ErrMessage, 400),
                realRows = isQuery ? Sample(real) : null,
                tainted,
            });
            if (!isQuery) tainted = true;
            return;
        }

        // Both succeeded.
        if (!isQuery)
        {
            ctx.Bump("s:both_ok");
            if (real.RowsAffected != sim.RowsAffected)
            {
                // One root (SimulatedDbDataReader.RecordsAffected semantics), not one
                // finding per statement: count them all, log one exemplar per script.
                ctx.Bump("s:rowsaffected_diff");
                if (ctx.RowsAffectedLogged)
                    return;
                ctx.RowsAffectedLogged = true;
                ctx.Emit(new { cls = "rows_affected_diff", script = rel, line = rec.Line, sql = rec.Sql, real = real.RowsAffected, sim = sim.RowsAffected, tainted });
            }
            return;
        }

        // Column shape / CLR types.
        if (real.FieldTypes.Length != sim.FieldTypes.Length)
        {
            ctx.Bump("q:column_count_diff");
            ctx.Emit(new { cls = "column_count_diff", script = rel, line = rec.Line, sql = rec.Sql, real = real.FieldTypes, sim = sim.FieldTypes, tainted });
            return;
        }
        for (var i = 0; i < real.FieldTypes.Length; i++)
        {
            if (real.FieldTypes[i] != sim.FieldTypes[i])
            {
                ctx.Bump("q:clr_type_diff");
                ctx.BumpIn(ctx.ErrBreakdown, "clr_type_diff real=" + real.DataTypeNames[i] + " sim=" + sim.DataTypeNames[i]);
                ctx.Emit(new { cls = "clr_type_diff", script = rel, line = rec.Line, sql = rec.Sql, col = i, real = real.FieldTypes[i], sim = sim.FieldTypes[i], realDbType = real.DataTypeNames[i], simDbType = sim.DataTypeNames[i], tainted });
                break;
            }
        }
        for (var i = 0; i < real.DataTypeNames.Length; i++)
        {
            if (real.DataTypeNames[i] != sim.DataTypeNames[i])
            {
                ctx.Bump("q:store_type_name_diff");
                ctx.Emit(new { cls = "store_type_name_diff", script = rel, line = rec.Line, sql = rec.Sql, col = i, real = real.DataTypeNames[i], sim = sim.DataTypeNames[i], tainted });
                break;
            }
        }

        var realRaw = Flatten(real, Render.Raw);
        var simRaw = Flatten(sim, Render.Raw);

        if (real.Rows.Count != sim.Rows.Count)
        {
            ctx.Bump("q:row_count_diff");
            ctx.Emit(new { cls = "row_count_diff", script = rel, line = rec.Line, sql = rec.Sql, realRows = real.Rows.Count, simRows = sim.Rows.Count, realSample = Sample(real), simSample = Sample(sim), tainted });
            return;
        }

        var ordered = SequenceEqual(realRaw, simRaw);
        if (!ordered)
        {
            var cols = real.FieldTypes.Length;
            var sortedReal = SortLike(realRaw, rec.SortMode, cols);
            var sortedSim = SortLike(simRaw, rec.SortMode, cols);
            if (SequenceEqual(sortedReal, sortedSim))
            {
                // Only a "nosort" record asserts order; rowsort/valuesort records
                // are explicitly order-insensitive, so a permutation is agreement.
                if (rec.SortMode == "nosort")
                {
                    ctx.Bump("q:ORDER_DIFF_nosort");
                    ctx.Emit(new { cls = "order_diff_nosort", script = rel, line = rec.Line, sortMode = rec.SortMode, sql = rec.Sql, realSample = Sample(real), simSample = Sample(sim), tainted });
                }
                else
                {
                    ctx.Bump("q:match_after_sort");
                }
            }
            else if (NearEqual(sortedReal, sortedSim))
            {
                ctx.Bump("q:float_epsilon_diff");
                ctx.Emit(new { cls = "float_epsilon_diff", script = rel, line = rec.Line, sql = rec.Sql, realSample = Sample(real), simSample = Sample(sim), tainted });
            }
            else
            {
                ctx.Bump("q:VALUE_DIFF");
                ctx.Emit(new { cls = "value_diff", script = rel, line = rec.Line, sortMode = rec.SortMode, sql = rec.Sql, firstDiff = FirstDiff(sortedReal, sortedSim), realSample = Sample(real), simSample = Sample(sim), tainted });
            }
        }
        else
        {
            ctx.Bump("q:match");
        }

        // Stored-file tie-breaker signal (metadata only).
        if (!tainted && rec.TypeString.Length > 0)
        {
            var realMatchesFile = realMatchesFileOverride ?? MatchesFile(Canonical(real, rec), rec);
            var simMatchesFile = MatchesFile(Canonical(sim, rec), rec);
            ctx.BumpIn(ctx.FileAgreement, (realMatchesFile ? "real=file" : "real!=file") + " " + (simMatchesFile ? "sim=file" : "sim!=file"));
        }
    }

    static bool MatchesFile(List<string> canon, SltRecord rec) =>
        rec.ExpectedValueCount >= 0
            ? canon.Count == rec.ExpectedValueCount && Render.Md5(canon) == rec.ExpectedHash
            : canon.Count == rec.Expected.Count && canon.SequenceEqual(rec.Expected, StringComparer.Ordinal);

    static List<string> Canonical(ExecResult r, SltRecord rec)
    {
        var vals = new List<string>(r.Rows.Count * Math.Max(1, rec.TypeString.Length));
        foreach (var row in r.Rows)
            for (var i = 0; i < row.Length; i++)
                vals.Add(Render.Slt(row[i], i < rec.TypeString.Length ? rec.TypeString[i] : 'T'));
        return SortLike(vals, rec.SortMode, rec.TypeString.Length);
    }

    static List<string> Flatten(ExecResult r, Func<object, string> render)
    {
        var vals = new List<string>(r.Rows.Count * Math.Max(1, r.FieldTypes.Length));
        foreach (var row in r.Rows)
            foreach (var v in row)
                vals.Add(render(v));
        return vals;
    }

    static List<string> SortLike(List<string> values, string mode, int cols = 0)
    {
        if (mode == "valuesort")
        {
            var copy = new List<string>(values);
            copy.Sort(StringComparer.Ordinal);
            return copy;
        }
        if (mode == "rowsort")
        {
            if (cols <= 0) { var c0 = new List<string>(values); c0.Sort(StringComparer.Ordinal); return c0; }
            var rows = new List<string>();
            for (var i = 0; i + cols <= values.Count; i += cols)
                rows.Add(string.Join('\u0001', values.GetRange(i, cols)));
            rows.Sort(StringComparer.Ordinal);
            var flat = new List<string>(values.Count);
            foreach (var r in rows) flat.AddRange(r.Split('\u0001'));
            return flat;
        }
        return values;
    }

    static bool SequenceEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    static bool NearEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (string.Equals(a[i], b[i], StringComparison.Ordinal)) continue;
            if (!double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!double.TryParse(b[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            var scale = Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)));
            if (Math.Abs(x - y) > 1e-9 * scale) return false;
        }
        return true;
    }

    static string FirstDiff(List<string> a, List<string> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return $"[{i}] real={a[i]} sim={b[i]}";
        return "(length)";
    }

    static string[] Sample(ExecResult r)
    {
        var outp = new List<string>();
        for (var i = 0; i < Math.Min(8, r.Rows.Count); i++)
            outp.Add(string.Join(" | ", r.Rows[i].Select(Render.Raw)));
        if (r.Rows.Count > 8) outp.Add($"... ({r.Rows.Count} rows)");
        return [.. outp];
    }

    static string ErrKey(ExecResult r) => r.ErrNumber != 0 ? "Msg" + r.ErrNumber.ToString(CultureInfo.InvariantCulture) : r.ErrKind;

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    static void Exec(SqlConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        cmd.ExecuteNonQuery();
    }

    static ExecResult Run(DbConnection conn, string sql, bool simulator)
    {
        var result = new ExecResult();
        var task = Task.Run(() =>
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = simulator ? 15 : 30;
                using var reader = cmd.ExecuteReader();
                var any = false;
                do
                {
                    // Read() must be called even when FieldCount is 0: the simulator
                    // materializes a statement's error on the first Read, not at
                    // ExecuteReader/NextResult the way SqlClient does.
                    if (reader.FieldCount > 0 && !any)
                    {
                        any = true;
                        result.FieldTypes = new string[reader.FieldCount];
                        result.DataTypeNames = new string[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            try { result.FieldTypes[i] = reader.GetFieldType(i)?.Name ?? "?"; } catch { result.FieldTypes[i] = "?"; }
                            try { result.DataTypeNames[i] = reader.GetDataTypeName(i) ?? "?"; } catch { result.DataTypeNames[i] = "?"; }
                        }
                    }
                    while (reader.Read())
                    {
                        if (reader.FieldCount == 0) continue;
                        var row = new object[reader.FieldCount];
                        reader.GetValues(row);
                        result.Rows.Add(row);
                        if (result.Rows.Count > 200000) throw new InvalidOperationException("result too large");
                    }
                }
                while (reader.NextResult());
                result.RowsAffected = reader.RecordsAffected;
                result.Ok = true;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.ErrKind = ex.GetType().Name;
                result.ErrMessage = ex.Message;
                result.ErrNumber = ex switch
                {
                    SqlException se => se.Number,
                    SimulatedSqlException sse => sse.Number,
                    _ => 0,
                };
            }
        });

        if (!task.Wait(TimeSpan.FromSeconds(simulator ? 40 : 60)))
        {
            result.TimedOut = true;
            result.Ok = false;
            result.ErrKind = "Timeout";
        }
        return result;
    }
}
