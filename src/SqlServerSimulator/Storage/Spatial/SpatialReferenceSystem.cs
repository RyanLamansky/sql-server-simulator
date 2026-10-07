using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// One row of <c>sys.spatial_reference_systems</c> — the 393 reference
/// systems a stock SQL Server 2025 instance lists, real's rows verbatim
/// (probed 2026-10-06) — and the ellipsoid and unit a <c>geography</c> under
/// that SRID measures in.
/// </summary>
/// <remarks>
/// Real measures a <c>geography</c> on its SRID's own ellipsoid and reports
/// the measure in its SRID's unit: <c>STLength</c> and <c>STDistance</c>
/// divide the metres by <see cref="UnitConversionFactor"/>, <c>STArea</c> by
/// its square, so SRID 104001 — the unit sphere, in radians — answers a
/// one-degree square in radians squared and a Clarke's-foot SRID in square
/// feet.
/// </remarks>
internal sealed partial class SpatialReferenceSystem
{
    public readonly int Srid;
    public readonly string AuthorityName;
    public readonly int AuthoritySrid;
    public readonly string WellKnownText;
    public readonly string UnitOfMeasure;
    public readonly double UnitConversionFactor;
    public readonly SpatialDatum Datum;

    private SpatialReferenceSystem(int srid, string authorityName, int authoritySrid, string wellKnownText, string unitOfMeasure, double unitConversionFactor)
    {
        this.Srid = srid;
        this.AuthorityName = authorityName;
        this.AuthoritySrid = authoritySrid;
        this.WellKnownText = wellKnownText;
        this.UnitOfMeasure = unitOfMeasure;
        this.UnitConversionFactor = unitConversionFactor;
        var ellipsoid = EllipsoidPattern.Match(wellKnownText);
        var inverseFlattening = double.Parse(ellipsoid.Groups[3].Value, CultureInfo.InvariantCulture);
        // Real measures a GRS 1980 SRID exactly as it measures WGS 84 (4269
        // and 4258 answer 4326's digits), the two differing only in the
        // eleventh digit of the flattening.
        this.Datum = ellipsoid.Groups[1].Value is "WGS 84" or "GRS 1980"
            ? SpatialDatum.Wgs84
            : new SpatialDatum(
                double.Parse(ellipsoid.Groups[2].Value, CultureInfo.InvariantCulture),
                inverseFlattening == 0 ? 0 : 1.0 / inverseFlattening);
    }

    [GeneratedRegex(@"(?:ELLIPSOID|SPHEROID)\[""([^""]*)"", ([0-9.eE+-]+), ([0-9.eE+-]+)\]")]
    private static partial Regex EllipsoidPattern { get; }

    /// <summary>Every row, ascending by SRID.</summary>
    public static readonly SpatialReferenceSystem[] All = Load();

    private static readonly FrozenDictionary<int, SpatialReferenceSystem> BySrid = All.ToFrozenDictionary(static system => system.Srid);

    /// <summary>The row for <paramref name="srid"/>, or null for an SRID no row lists.</summary>
    public static SpatialReferenceSystem? Find(int srid) => BySrid.GetValueOrDefault(srid);

    private static SpatialReferenceSystem[] Load()
    {
        using var stream = typeof(SpatialReferenceSystem).Assembly.GetManifestResourceStream("SqlServerSimulator.Spatial.SpatialReferenceSystems.tsv")
            ?? throw new InvalidOperationException("The spatial reference systems resource is missing.");
        using var reader = new StreamReader(stream);
        var rows = new List<SpatialReferenceSystem>();
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            var fields = line.Split('\t');
            rows.Add(new SpatialReferenceSystem(
                int.Parse(fields[0], CultureInfo.InvariantCulture),
                fields[1],
                int.Parse(fields[2], CultureInfo.InvariantCulture),
                fields[3],
                fields[4],
                double.Parse(fields[5], CultureInfo.InvariantCulture)));
        }
        return [.. rows];
    }
}
