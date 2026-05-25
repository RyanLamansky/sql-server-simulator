using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal abstract partial class Collation
{
    /// <summary>
    /// Byte-exact sort body for <c>SQL_Latin1_General_CP1_CI_AS</c> — the
    /// simulator's default collation. Real SQL Server sorts this collation's
    /// non-Unicode (<c>varchar</c>/<c>char</c>) and Unicode (<c>nvarchar</c>/
    /// <c>nchar</c>) data through two different multi-level weight tables, and
    /// neither matches .NET's <see cref="CompareInfo"/> ordering. This body
    /// reproduces both for the CP1252 repertoire from probe-extracted rank
    /// tables (DENSE_RANK over <c>CHAR(n)</c> / the decoded char on SQL Server
    /// 2025, under both the CI_AS and the accent-insensitive CI_AI form):
    /// <list type="bullet">
    /// <item>A two-level comparison — primary by the accent-folded rank
    /// (so <c>'à'</c> &lt; <c>'Ao'</c>: base letter <c>a</c> before <c>Ao</c>),
    /// then a secondary accent tie-break (so <c>'cafe'</c> &lt; <c>'café'</c>,
    /// <c>'az'</c> &lt; <c>'àz'</c>). Case is folded at both levels (CI).</item>
    /// <item><b>varchar</b> (SQL sort order 52): pure per-character; no ignorable
    /// characters and no ligature expansion (<c>'coop'</c> &gt; <c>'co-op'</c>,
    /// <c>'æ'</c> ≠ <c>'ae'</c>).</item>
    /// <item><b>nvarchar</b> (Unicode weights): control characters plus
    /// apostrophe, hyphen, the dashes, and soft-hyphen are minimal-weight
    /// (ignored at the primary/secondary levels, consulted only to break a
    /// remaining tie, so <c>'coop'</c> &lt; <c>'co-op'</c>); the Latin ligatures
    /// <c>æ Æ œ Œ ß þ Þ</c> expand to their base letters (<c>'æ'</c> = <c>'ae'</c>,
    /// <c>'ß'</c> = <c>'ss'</c>, <c>'þ'</c> = <c>'th'</c>).</item>
    /// </list>
    /// Strings containing any non-CP1252 character fall back to the inner
    /// <see cref="CultureCollation"/>'s <see cref="CompareInfo"/> path — close
    /// for arbitrary Unicode, exact for the CP1252 universe this collation is
    /// built for. Metadata (name, description, storage encoding) delegates to
    /// that same parser-built inner. See <c>docs/claude/collations.md</c>.
    /// </summary>
    internal sealed class SqlLatin1Cp1CiAsCollation : Collation
    {
        internal const string CollationName = "SQL_Latin1_General_CP1_CI_AS";

        // Dense ranks indexed by CP1252 byte (1..255; index 0 unused). The
        // "Primary" arrays come from the accent-insensitive CI_AI form (so
        // accent variants of a base letter share a rank); the "Secondary"
        // arrays from the accent-sensitive CI_AS form (the within-base-letter
        // tie-break). Both fold case. Probe-extracted on SQL Server 2025.
        // One hand-adjustment: the legacy varchar CI_AI collation classifies
        // cedilla (Ç/ç, 0xC7/0xE7) as a distinct primary letter, but its CI_AS
        // *sort* folds it onto c at the primary level (probe-confirmed:
        // 'Çm' < 'cn'), so those two entries are pinned to c's primary rank
        // (145) rather than the raw CI_AI value (146). nvarchar already folds
        // cedilla in its CI_AI table, so its arrays are untouched.
        private static ReadOnlySpan<byte> VarcharPrimaryByteRank =>
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
            32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
            132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 48, 49, 50, 51, 52, 53,
            54, 142, 144, 145, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158,
            159, 160, 161, 162, 164, 165, 166, 167, 168, 169, 170, 55, 56, 57, 58, 59,
            60, 142, 144, 145, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158,
            159, 160, 161, 162, 164, 165, 166, 167, 168, 169, 170, 61, 62, 63, 64, 65,
            66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
            82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
            98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
            114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129,
            142, 142, 142, 142, 142, 142, 143, 145, 148, 148, 148, 148, 152, 152, 152, 152,
            171, 157, 158, 158, 158, 158, 158, 130, 158, 165, 165, 165, 165, 169, 172, 163,
            142, 142, 142, 142, 142, 142, 143, 145, 148, 148, 148, 148, 152, 152, 152, 152,
            171, 157, 158, 158, 158, 158, 158, 131, 158, 165, 165, 165, 165, 169, 172, 169,
        ];

        private static ReadOnlySpan<byte> VarcharSecondaryByteRank =>
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
            32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
            132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 48, 49, 50, 51, 52, 53,
            54, 142, 150, 151, 153, 154, 159, 160, 161, 162, 167, 168, 169, 170, 171, 173,
            180, 181, 182, 183, 185, 186, 191, 192, 193, 194, 197, 55, 56, 57, 58, 59,
            60, 142, 150, 151, 153, 154, 159, 160, 161, 162, 167, 168, 169, 170, 171, 173,
            180, 181, 182, 183, 185, 186, 191, 192, 193, 194, 197, 61, 62, 63, 64, 65,
            66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
            82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
            98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
            114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129,
            143, 144, 145, 146, 147, 148, 149, 152, 155, 156, 157, 158, 163, 164, 165, 166,
            198, 172, 174, 175, 176, 177, 178, 130, 179, 187, 188, 189, 190, 195, 199, 184,
            143, 144, 145, 146, 147, 148, 149, 152, 155, 156, 157, 158, 163, 164, 165, 166,
            198, 172, 174, 175, 176, 177, 178, 131, 179, 187, 188, 189, 190, 195, 199, 196,
        ];

        private static ReadOnlySpan<byte> NvarcharPrimaryByteRank =>
        [
            0, 2, 3, 4, 5, 6, 7, 8, 9, 40, 41, 42, 43, 44, 10, 11,
            12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            1, 45, 46, 47, 48, 49, 50, 34, 51, 52, 53, 87, 54, 35, 55, 56,
            114, 118, 119, 120, 121, 122, 123, 124, 125, 126, 57, 58, 88, 89, 90, 59,
            60, 127, 129, 130, 131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142,
            144, 145, 146, 147, 149, 152, 153, 154, 155, 156, 157, 61, 62, 63, 64, 65,
            66, 127, 129, 130, 131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142,
            144, 145, 146, 147, 149, 152, 153, 154, 155, 156, 157, 67, 68, 69, 70, 28,
            113, 29, 81, 133, 84, 111, 108, 109, 64, 112, 147, 85, 143, 30, 157, 31,
            32, 79, 80, 82, 83, 110, 37, 38, 78, 151, 147, 86, 143, 33, 157, 156,
            39, 71, 96, 97, 98, 99, 72, 100, 73, 101, 127, 92, 102, 36, 103, 74,
            104, 91, 119, 120, 75, 105, 106, 107, 76, 118, 142, 93, 115, 116, 117, 77,
            127, 127, 127, 127, 127, 127, 128, 130, 132, 132, 132, 132, 136, 136, 136, 136,
            131, 141, 142, 142, 142, 142, 142, 94, 142, 152, 152, 152, 152, 156, 150, 148,
            127, 127, 127, 127, 127, 127, 128, 130, 132, 132, 132, 132, 136, 136, 136, 136,
            131, 141, 142, 142, 142, 142, 142, 95, 142, 152, 152, 152, 152, 156, 150, 156,
        ];

        private static ReadOnlySpan<byte> NvarcharSecondaryByteRank =>
        [
            0, 2, 3, 4, 5, 6, 7, 8, 9, 40, 41, 42, 43, 44, 10, 11,
            12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            1, 45, 46, 47, 48, 49, 50, 34, 51, 52, 53, 88, 54, 35, 55, 56,
            115, 119, 120, 121, 122, 123, 124, 125, 126, 127, 57, 58, 89, 90, 91, 59,
            60, 128, 137, 138, 140, 142, 147, 149, 150, 151, 156, 157, 158, 159, 160, 162,
            171, 172, 173, 174, 177, 180, 185, 186, 187, 188, 191, 61, 62, 63, 64, 66,
            67, 128, 137, 138, 140, 142, 147, 149, 150, 151, 156, 157, 158, 159, 160, 162,
            171, 172, 173, 174, 177, 180, 185, 186, 187, 188, 191, 68, 69, 70, 71, 28,
            114, 29, 82, 148, 85, 112, 109, 110, 65, 113, 175, 86, 170, 30, 192, 31,
            32, 80, 81, 83, 84, 111, 37, 38, 79, 179, 175, 87, 170, 33, 192, 190,
            39, 72, 97, 98, 99, 100, 73, 101, 74, 102, 129, 93, 103, 36, 104, 75,
            105, 92, 120, 121, 76, 106, 107, 108, 77, 119, 163, 94, 116, 117, 118, 78,
            131, 130, 132, 134, 133, 135, 136, 139, 144, 143, 145, 146, 153, 152, 154, 155,
            141, 161, 165, 164, 166, 168, 167, 95, 169, 182, 181, 183, 184, 189, 178, 176,
            131, 130, 132, 134, 133, 135, 136, 139, 144, 143, 145, 146, 153, 152, 154, 155,
            141, 161, 165, 164, 166, 168, 167, 96, 169, 182, 181, 183, 184, 189, 178, 190,
        ];

        // CP1252 bytes the nvarchar sort gives minimal (primary-zero) weight:
        // the C0 controls except TAB/LF/CR, the C1 controls / unassigned bytes,
        // apostrophe (0x27), hyphen (0x2D), en/em dash (0x96/0x97), and
        // soft-hyphen (0xAD). Probe-confirmed via the 'aa' < 'aXa' < 'aaa' test
        // against the decoded character.
        private static ReadOnlySpan<byte> NvarcharIgnorableBytes =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
            26, 27, 28, 29, 30, 31, 0x27, 0x2D, 0x7F, 0x81, 0x8D, 0x8F, 0x90, 0x96, 0x97, 0x9D, 0xAD,
        ];

        private static readonly FrozenDictionary<char, (int Primary, int Secondary)> varcharWeights =
            BuildWeights(VarcharPrimaryByteRank, VarcharSecondaryByteRank);
        private static readonly FrozenDictionary<char, (int Primary, int Secondary)> nvarcharWeights =
            BuildWeights(NvarcharPrimaryByteRank, NvarcharSecondaryByteRank);
        private static readonly FrozenSet<char> nvarcharIgnorable = BuildIgnorableChars();

        // Ligatures the sort expands to their base letters at the primary level
        // (case folds, so the lower-case base suffices). nvarchar expands the
        // full Latin set; the legacy varchar sort order 52 expands only æ / Æ / ß
        // (œ Œ þ Þ are single-weight letters there). Probe-confirmed.
        private static readonly FrozenDictionary<char, string> nvarcharExpansion = new Dictionary<char, string>
        {
            { 'æ', "ae" }, { 'Æ', "ae" }, { 'œ', "oe" }, { 'Œ', "oe" }, { 'ß', "ss" }, { 'þ', "th" }, { 'Þ', "th" },
        }.ToFrozenDictionary();

        private static readonly FrozenDictionary<char, string> varcharExpansion = new Dictionary<char, string>
        {
            { 'æ', "ae" }, { 'Æ', "ae" }, { 'ß', "ss" },
        }.ToFrozenDictionary();

        // The parser-built CultureCollation for this name, supplying the
        // metadata (description, storage encoding) and the CompareInfo fallback
        // for non-CP1252 input. Typed as the sealed concrete body so the JIT
        // devirtualizes the delegated calls. The nvarchar instance owns its
        // varchar sibling; the varchar sibling's own sibling reference is null.
        private readonly CultureCollation inner;
        private readonly bool varcharStorage;
        private readonly SqlLatin1Cp1CiAsCollation? varcharBody;

        // Built by the parser (Collation.Parser.cs) when it resolves this
        // collation name, handing in the freshly-constructed CultureCollation.
        internal SqlLatin1Cp1CiAsCollation(CultureCollation inner)
        {
            this.inner = inner;
            this.varcharStorage = false;
            this.varcharBody = new SqlLatin1Cp1CiAsCollation(inner, varcharStorage: true);
        }

        private SqlLatin1Cp1CiAsCollation(CultureCollation inner, bool varcharStorage)
        {
            this.inner = inner;
            this.varcharStorage = varcharStorage;
        }

        // Decode each CP1252 byte to its .NET char and map char -> (primary,
        // secondary). The 0x80-0x9F window decodes to scattered BMP code points
        // (€ ƒ Ÿ …); the rest are identity for ASCII / Latin-1.
        private static FrozenDictionary<char, (int, int)> BuildWeights(ReadOnlySpan<byte> primary, ReadOnlySpan<byte> secondary)
        {
            var encoding = CharSqlType.Cp1252Encoder;
            var map = new Dictionary<char, (int, int)>(primary.Length);
            Span<byte> buffer = stackalloc byte[1];
            for (var b = 1; b < primary.Length; b++)
            {
                buffer[0] = (byte)b;
                var decoded = encoding.GetString(buffer);
                if (decoded.Length == 1)
                    map[decoded[0]] = (primary[b], secondary[b]);
            }

            return map.ToFrozenDictionary();
        }

        private static FrozenSet<char> BuildIgnorableChars()
        {
            var encoding = CharSqlType.Cp1252Encoder;
            var set = new HashSet<char>();
            Span<byte> buffer = stackalloc byte[1];
            foreach (var b in NvarcharIgnorableBytes)
            {
                buffer[0] = b;
                var decoded = encoding.GetString(buffer);
                if (decoded.Length == 1)
                    _ = set.Add(decoded[0]);
            }

            return set.ToFrozenSet();
        }

        public override string Name => CollationName;

        public override string Description => this.inner.Description;

        public override bool CaseSensitive => false;

        internal override bool IsSupplementaryCharacterAware => false;

        internal override Encoding StorageEncoding => this.inner.StorageEncoding;

        internal override Collation ForVarcharStorage() => this.varcharBody ?? this;

        public override int Compare(string? x, string? y) =>
            x is null ? (y is null ? 0 : -1)
            : y is null ? 1
            : !AllCp1252(x) || !AllCp1252(y) ? this.inner.Compare(x, y)
            : CompareInRepertoire(x, y);

        public override bool Equals(string? x, string? y) =>
            x is null ? y is null : y is not null && this.Compare(x, y) == 0;

        public override int GetHashCode(string obj)
        {
            if (!AllCp1252(obj))
                return this.inner.GetHashCode(obj);

            // Hash the primary weight run, a separator, then the secondary run —
            // mirrors the order Compare consults them, so Compare-equal strings
            // (equal at both levels) always hash equal. Streamed through the
            // cursor so no weight lists are materialized.
            var hash = new HashCode();
            var primary = this.NewCursor(obj);
            while (primary.MoveNext())
                hash.Add(primary.Primary);
            hash.Add(-1);
            var secondary = this.NewCursor(obj);
            while (secondary.MoveNext())
                hash.Add(secondary.Secondary);
            return hash.ToHashCode();
        }

        private int CompareInRepertoire(string x, string y)
        {
            var primary = this.CompareLevel(x, y, Level.Primary);
            if (primary != 0)
                return primary;
            var secondary = this.CompareLevel(x, y, Level.Secondary);
            if (secondary != 0)
                return secondary;

            // varchar resolves a remaining tie by the ligature tertiary (a
            // ligature sorts just after its expansion: 'ae' < 'æ', 'ss' < 'ß');
            // it has no minimal-weight characters. nvarchar treats a ligature as
            // equal to its expansion and instead breaks the tie on its
            // minimal-weight characters.
            return this.varcharStorage ? this.CompareLevel(x, y, Level.Tertiary) : NvarcharIgnorableTiebreak(x, y);
        }

        private enum Level
        {
            Primary,
            Secondary,
            Tertiary,
        }

        // Walks both operands through parallel weight cursors, comparing one
        // level at a time. The shorter weight run sorts first once a shared
        // prefix ties (matching the old list-length tie-break). The cursors are
        // ref structs over the source strings — no weight lists are allocated,
        // so the common single-pass primary comparison is allocation-free.
        private int CompareLevel(string x, string y, Level level)
        {
            var cx = this.NewCursor(x);
            var cy = this.NewCursor(y);
            while (true)
            {
                var hasX = cx.MoveNext();
                var hasY = cy.MoveNext();
                if (!hasX || !hasY)
                    return (hasX ? 1 : 0) - (hasY ? 1 : 0);
                var wx = level switch { Level.Primary => cx.Primary, Level.Secondary => cx.Secondary, _ => cx.Tertiary };
                var wy = level switch { Level.Primary => cy.Primary, Level.Secondary => cy.Secondary, _ => cy.Tertiary };
                if (wx != wy)
                    return wx < wy ? -1 : 1;
            }
        }

        private WeightCursor NewCursor(string s) =>
            this.varcharStorage
                ? new WeightCursor(s, varcharWeights, varcharExpansion, skipIgnorable: false)
                : new WeightCursor(s, nvarcharWeights, nvarcharExpansion, skipIgnorable: true);

        // Yields the (primary, secondary, tertiary) weight of each collation
        // element of a string in order: ignorables (nvarchar) are skipped,
        // ligatures expand to their base letters with tertiary 1, plain
        // characters carry tertiary 0. One element per MoveNext; the expansion
        // of a ligature surfaces as consecutive elements.
        private ref struct WeightCursor
        {
            private readonly string s;

            private readonly FrozenDictionary<char, (int Primary, int Secondary)> weights;

            private readonly FrozenDictionary<char, string> expansions;

            private readonly bool skipIgnorable;

            private int index;

            private string? expansion;

            private int expansionPos;

            internal int Primary;

            internal int Secondary;

            internal int Tertiary;

            internal WeightCursor(string s, FrozenDictionary<char, (int Primary, int Secondary)> weights, FrozenDictionary<char, string> expansions, bool skipIgnorable)
            {
                this.s = s;
                this.weights = weights;
                this.expansions = expansions;
                this.skipIgnorable = skipIgnorable;
            }

            internal bool MoveNext()
            {
                if (this.expansion is not null)
                {
                    this.Emit(this.expansion[this.expansionPos++], tertiary: 1);
                    if (this.expansionPos >= this.expansion.Length)
                        this.expansion = null;
                    return true;
                }

                while (this.index < this.s.Length)
                {
                    var ch = this.s[this.index++];
                    if (this.skipIgnorable && nvarcharIgnorable.Contains(ch))
                        continue;
                    if (this.expansions.TryGetValue(ch, out var exp))
                    {
                        this.expansion = exp;
                        this.expansionPos = 0;
                        this.Emit(exp[this.expansionPos++], tertiary: 1);
                        if (this.expansionPos >= exp.Length)
                            this.expansion = null;
                        return true;
                    }

                    this.Emit(ch, tertiary: 0);
                    return true;
                }

                return false;
            }

            private void Emit(char ch, int tertiary)
            {
                var (primary, secondary) = this.weights[ch];
                this.Primary = primary;
                this.Secondary = secondary;
                this.Tertiary = tertiary;
            }
        }

        // Reached only when the primary and secondary keys are equal: the two
        // strings can differ only in their minimal-weight characters. Compare
        // those characters alone, each tagged by the count of real characters
        // preceding it — a string with fewer or later minimal marks sorts first
        // (absence before presence: 'coop' < 'co-op'), and the raw length
        // difference an expansion leaves behind (e.g. 'ß' vs 'ss') is ignored.
        private static int NvarcharIgnorableTiebreak(string x, string y)
        {
            var keyX = IgnorableKey(x);
            var keyY = IgnorableKey(y);
            var shared = Math.Min(keyX.Count, keyY.Count);
            for (var k = 0; k < shared; k++)
            {
                var positionDelta = keyX[k].Preceding - keyY[k].Preceding;
                if (positionDelta != 0)
                    return positionDelta < 0 ? -1 : 1;
                var rankDelta = keyX[k].Rank - keyY[k].Rank;
                if (rankDelta != 0)
                    return rankDelta < 0 ? -1 : 1;
            }

            return keyX.Count.CompareTo(keyY.Count);
        }

        private static List<(int Preceding, int Rank)> IgnorableKey(string s)
        {
            var key = new List<(int, int)>();
            var preceding = 0;
            foreach (var ch in s)
            {
                if (nvarcharIgnorable.Contains(ch))
                    key.Add((preceding, nvarcharWeights[ch].Secondary));
                else if (nvarcharExpansion.TryGetValue(ch, out var expansion))
                    preceding += expansion.Length;   // count expanded units so ignorables align across a ligature
                else
                    preceding++;
            }

            return key;
        }

        private static bool AllCp1252(string s)
        {
            foreach (var ch in s)
            {
                if (!varcharWeights.ContainsKey(ch))
                    return false;
            }

            return true;
        }
    }
}
