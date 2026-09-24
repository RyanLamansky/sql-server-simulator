using System.IO.Compression;
using System.Text;

namespace SltRunner;

/// <summary>
/// One captured record: exactly what the differential comparison discriminates
/// on, and nothing else. Rows hold already-rendered <see cref="Render.Raw"/>
/// strings, so a replay-side <c>Flatten(..., Render.Raw)</c> over them returns
/// the captured strings unchanged (Render.Raw of a string is the string).
/// </summary>
sealed class RefRecord
{
    public int Line;
    public string Kind = "";              // "statement" | "query"
    public uint SqlHash;                  // FNV-1a 32 of the record's SQL text
    public bool Skipped;                  // AppliesToUs was false: slot kept so alignment holds

    public bool Ok;
    public int RowsAffected = -1;
    public string[] FieldTypes = [];
    public string[] DataTypeNames = [];
    public List<object[]> Rows = new();   // elements are strings (rendered at capture)
    public bool RealMatchesFile;          // stored-file tie-breaker, precomputed at capture

    public int ErrNumber;
    public string ErrKind = "";
    public string ErrMessage = "";

    public bool KnownDivergent;
    public string DivClass = "";          // "+"-joined divergence classes seen at capture
    public string DivSimErr = "";         // sim's error key at capture ("" when the sim succeeded)
}

/// <summary>The per-script reference file: header plus one record per parsed record, in parse order.</summary>
sealed class ScriptReference
{
    public int Version;
    public long CapturedUtcTicks;
    public string ServerVersion = "";
    public bool IgnoreHalt;
    public string Rel = "";
    /// <summary>"" when the whole script was captured; otherwise why capture stopped early.</summary>
    public string TruncatedReason = "";
    public List<RefRecord> Records = new();
}

static class Reference
{
    public const int FormatVersion = 1;
    const int Magic = 0x52544C53; // "SLTR"

    /// <summary>FNV-1a 32 over the UTF-16 code units: the alignment guard's SQL fingerprint.</summary>
    public static uint Hash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s)
        {
            h = (h ^ (byte)c) * 16777619u;
            h = (h ^ (byte)(c >> 8)) * 16777619u;
        }
        return h;
    }

    /// <summary>Mirror the script's relative path under the reference directory, with a .ref extension.</summary>
    public static string PathFor(string refDir, string rel)
    {
        var clean = rel.Replace('\\', '/').TrimStart('/');
        return Path.Combine(refDir, Path.ChangeExtension(clean, ".ref"));
    }

    public static void Write(string path, ScriptReference r)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new BinaryWriter(gz, Encoding.UTF8);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(r.CapturedUtcTicks);
        w.Write(r.ServerVersion ?? "");
        w.Write(r.IgnoreHalt);
        w.Write(r.Rel ?? "");
        foreach (var rec in r.Records)
        {
            w.Write((byte)1);
            WriteRecord(w, rec);
        }
        w.Write((byte)0);
        w.Write(r.TruncatedReason ?? "");
    }

    static void WriteRecord(BinaryWriter w, RefRecord rec)
    {
        w.Write(rec.Line);
        w.Write(rec.Kind == "query");
        w.Write(rec.SqlHash);
        w.Write(rec.Skipped);
        if (rec.Skipped) return;
        w.Write(rec.Ok);
        if (rec.Ok)
        {
            w.Write(rec.RowsAffected);
            w.Write(rec.FieldTypes.Length);
            for (var i = 0; i < rec.FieldTypes.Length; i++)
            {
                w.Write(rec.FieldTypes[i] ?? "");
                w.Write(rec.DataTypeNames[i] ?? "");
            }
            // Rows are jagged on purpose: a record with several result sets can
            // hand back rows of differing widths, and a rectangular encoding
            // would silently re-shape them.
            w.Write(rec.Rows.Count);
            foreach (var row in rec.Rows)
            {
                w.Write(row.Length);
                foreach (var v in row) w.Write((string)v ?? "");
            }
            w.Write(rec.RealMatchesFile);
        }
        else
        {
            w.Write(rec.ErrNumber);
            w.Write(rec.ErrKind ?? "");
            w.Write(rec.ErrMessage ?? "");
        }
        w.Write(rec.KnownDivergent);
        if (rec.KnownDivergent)
        {
            w.Write(rec.DivClass ?? "");
            w.Write(rec.DivSimErr ?? "");
        }
    }

    public static ScriptReference Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        var magic = r.ReadInt32();
        if (magic != Magic) throw new InvalidDataException($"not a reference file: {path}");
        var sref = new ScriptReference { Version = r.ReadInt32() };
        if (sref.Version != FormatVersion)
            throw new InvalidDataException($"reference format v{sref.Version}, runner expects v{FormatVersion}: {path}");
        sref.CapturedUtcTicks = r.ReadInt64();
        sref.ServerVersion = r.ReadString();
        sref.IgnoreHalt = r.ReadBoolean();
        sref.Rel = r.ReadString();
        while (r.ReadByte() == 1)
            sref.Records.Add(ReadRecord(r));
        sref.TruncatedReason = r.ReadString();
        return sref;
    }

    static RefRecord ReadRecord(BinaryReader r)
    {
        var rec = new RefRecord
        {
            Line = r.ReadInt32(),
            Kind = r.ReadBoolean() ? "query" : "statement",
            SqlHash = r.ReadUInt32(),
            Skipped = r.ReadBoolean(),
        };
        if (rec.Skipped) return rec;
        rec.Ok = r.ReadBoolean();
        if (rec.Ok)
        {
            rec.RowsAffected = r.ReadInt32();
            var cols = r.ReadInt32();
            rec.FieldTypes = new string[cols];
            rec.DataTypeNames = new string[cols];
            for (var i = 0; i < cols; i++)
            {
                rec.FieldTypes[i] = r.ReadString();
                rec.DataTypeNames[i] = r.ReadString();
            }
            var rows = r.ReadInt32();
            rec.Rows = new List<object[]>(rows);
            for (var i = 0; i < rows; i++)
            {
                var w = r.ReadInt32();
                var row = new object[w];
                for (var j = 0; j < w; j++) row[j] = r.ReadString();
                rec.Rows.Add(row);
            }
            rec.RealMatchesFile = r.ReadBoolean();
        }
        else
        {
            rec.ErrNumber = r.ReadInt32();
            rec.ErrKind = r.ReadString();
            rec.ErrMessage = r.ReadString();
        }
        rec.KnownDivergent = r.ReadBoolean();
        if (rec.KnownDivergent)
        {
            rec.DivClass = r.ReadString();
            rec.DivSimErr = r.ReadString();
        }
        return rec;
    }
}

/// <summary>The reference no longer describes the script on disk: never absorbed silently.</summary>
sealed class StaleReferenceException : Exception
{
    public StaleReferenceException(string message) : base(message) { }
}
