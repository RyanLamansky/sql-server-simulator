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
    /// The nvarchar table is extended to the Thai block (U+0E00–U+0E7F) on the
    /// same unified scale, so Thai data (and Latin/Thai mixes) sort byte-exactly
    /// too — Thai letters above Latin, Thai leading vowels just above 'z', Thai
    /// digits between '0' and 'a'. Thai tone-mark combining characters carry the
    /// lowest primary weight rather than SQL Server's secondary-diacritic
    /// treatment (a documented edge that doesn't affect tone-free data). Strings
    /// with any character outside the active repertoire (CP1252, plus Thai for
    /// nvarchar) fall back to the inner <see cref="CultureCollation"/>'s
    /// <see cref="CompareInfo"/> path — close for arbitrary Unicode. Metadata
    /// (name, description, storage encoding) delegates to that same parser-built
    /// inner. See <c>docs/claude/collations.md</c>.
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

        // Unified scale: a single DENSE_RANK over the CP1252 repertoire *and*
        // the Thai block (U+0E00–U+0E7F) under CI_AI (primary) / CI_AS
        // (secondary), so Thai characters interleave with Latin exactly as real
        // SQL Server's SQL_Latin1 nvarchar sort places them (Thai letters rank
        // above all Latin letters; Thai digits between ASCII '0' and 'a'; Thai
        // leading vowels เ แ โ ใ ไ just above Latin 'z' — probe-confirmed). The
        // CP1252-only relative order is identical to the prior byte-indexed
        // tables (DENSE_RANK is monotonic), so the collation stays byte-exact
        // for CP1252; <c>ushort</c> because the union pushes the max rank past
        // 255. Thai weights live in the parallel <see cref="ThaiPrimaryRank"/> /
        // <see cref="ThaiSecondaryRank"/> arrays keyed by <c>cp - 0x0E00</c>.
        private static ReadOnlySpan<ushort> NvarcharPrimaryByteRank =>
        [
            0, 2, 3, 4, 5, 6, 7, 8, 9, 40, 41, 42, 43, 44, 10, 11,
            12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            1, 45, 46, 47, 48, 49, 50, 34, 51, 52, 53, 87, 54, 35, 55, 56,
            115, 120, 122, 124, 126, 128, 130, 132, 134, 136, 57, 58, 88, 89, 90, 59,
            60, 138, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153,
            155, 156, 157, 158, 160, 163, 164, 165, 166, 167, 168, 61, 62, 63, 64, 65,
            66, 138, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153,
            155, 156, 157, 158, 160, 163, 164, 165, 166, 167, 168, 67, 68, 69, 70, 28,
            114, 29, 81, 144, 84, 111, 108, 109, 64, 112, 158, 85, 154, 30, 168, 31,
            32, 79, 80, 82, 83, 110, 37, 38, 78, 162, 158, 86, 154, 33, 168, 167,
            39, 71, 96, 97, 98, 99, 72, 100, 73, 101, 138, 92, 102, 36, 103, 74,
            104, 91, 122, 124, 75, 105, 106, 107, 76, 120, 153, 93, 117, 118, 119, 77,
            138, 138, 138, 138, 138, 138, 139, 141, 143, 143, 143, 143, 147, 147, 147, 147,
            142, 152, 153, 153, 153, 153, 153, 94, 153, 163, 163, 163, 163, 167, 161, 159,
            138, 138, 138, 138, 138, 138, 139, 141, 143, 143, 143, 143, 147, 147, 147, 147,
            142, 152, 153, 153, 153, 153, 153, 95, 153, 163, 163, 163, 163, 167, 161, 167,
        ];

        private static ReadOnlySpan<ushort> NvarcharSecondaryByteRank =>
        [
            0, 2, 3, 4, 5, 6, 7, 8, 9, 47, 48, 49, 50, 51, 10, 11,
            12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            1, 52, 53, 54, 55, 56, 57, 34, 58, 59, 60, 95, 61, 35, 62, 63,
            123, 128, 130, 132, 134, 136, 138, 140, 142, 144, 64, 65, 96, 97, 98, 66,
            67, 146, 155, 156, 158, 160, 165, 167, 168, 169, 174, 175, 176, 177, 178, 180,
            189, 190, 191, 192, 195, 198, 203, 204, 205, 206, 209, 68, 69, 70, 71, 73,
            74, 146, 155, 156, 158, 160, 165, 167, 168, 169, 174, 175, 176, 177, 178, 180,
            189, 190, 191, 192, 195, 198, 203, 204, 205, 206, 209, 75, 76, 77, 78, 28,
            122, 29, 89, 166, 92, 119, 116, 117, 72, 120, 193, 93, 188, 30, 210, 31,
            32, 87, 88, 90, 91, 118, 37, 38, 86, 197, 193, 94, 188, 33, 210, 208,
            46, 79, 104, 105, 106, 107, 80, 108, 81, 109, 147, 100, 110, 36, 111, 82,
            112, 99, 130, 132, 83, 113, 114, 115, 84, 128, 181, 101, 125, 126, 127, 85,
            149, 148, 150, 152, 151, 153, 154, 157, 162, 161, 163, 164, 171, 170, 172, 173,
            159, 179, 183, 182, 184, 186, 185, 102, 187, 200, 199, 201, 202, 207, 196, 194,
            149, 148, 150, 152, 151, 153, 154, 157, 162, 161, 163, 164, 171, 170, 172, 173,
            159, 179, 183, 182, 184, 186, 185, 103, 187, 200, 199, 201, 202, 207, 196, 208,
        ];

        // Thai block weights on the same unified scale, indexed by
        // <c>cp - 0x0E00</c> (U+0E00–U+0E7F). Reserved/unassigned slots and the
        // tone-mark combining characters carry primary rank 1 (lowest, like
        // SPACE); the secondary distinguishes the marks. Consumed only by the
        // nvarchar weight map.
        private static ReadOnlySpan<ushort> ThaiPrimaryRank =>
        [
            1, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188,
            189, 190, 191, 192, 193, 194, 195, 196, 197, 198, 199, 200, 201, 202, 203, 204,
            205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220,
            221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 1, 1, 1, 1, 113,
            169, 170, 171, 172, 173, 232, 233, 1, 1, 1, 1, 1, 1, 1, 234, 235,
            116, 121, 123, 125, 127, 129, 131, 133, 135, 137, 236, 237, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            169, 170, 171, 172, 173, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        ];

        private static ReadOnlySpan<ushort> ThaiSecondaryRank =>
        [
            1, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230,
            231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242, 243, 244, 245, 246,
            247, 248, 249, 250, 251, 252, 253, 254, 255, 256, 257, 258, 259, 260, 261, 262,
            263, 264, 265, 266, 267, 268, 269, 270, 271, 272, 273, 1, 1, 1, 1, 121,
            211, 212, 213, 214, 215, 274, 275, 40, 41, 42, 43, 44, 39, 45, 276, 277,
            124, 129, 131, 133, 135, 137, 139, 141, 143, 145, 278, 279, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            211, 212, 213, 214, 215, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
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
            BuildNvarcharWeights();
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

        // In-repertoire characters whose GetHashCode must fold onto another
        // spelling. Each entry's target is inner-collation-equal to its key
        // (validated by test against an exhaustive ICU scan), and equality
        // can therefore relate an out-of-repertoire spelling to in-repertoire
        // strings using EITHER form — e.g. fullwidth `２` equals both `2`
        // and `²` through the inner fallback, so `2` and `²` must share a
        // hash even though the weight tables keep them unequal (a legal
        // collision). Covers: the ICU-completely-ignorable controls + soft
        // hyphen (fold to empty), NBSP → space, feminine/masculine
        // ordinals → base letter, superscript digits → digits, Thai digits →
        // ASCII digits, vulgar fractions → their FRACTION SLASH compat
        // decompositions (out-of-repertoire targets, deliberately — both
        // spellings then take the inner-hash path together), the CP1252
        // case pairs whose legacy varchar weights are asymmetric
        // (Œ Š Ÿ Ž → lowercase), and Thai SARA AM → its NIKHAHIT + SARA AA
        // compat decomposition (ICU equates the spellings; the weight
        // tables don't). CollationHashConsistencyTests sweeps the
        // repertoire and Unicode blocks to keep this list complete.
        private static readonly FrozenDictionary<char, string> hashFolds = BuildHashFolds();

        // Per-body fast-path set: repertoire minus hashFolds keys. A string
        // whose every character is in this set hashes straight off its
        // weight runs with no canonicalization.
        private static readonly FrozenSet<char> varcharHashClean =
            varcharWeights.Keys.Where(c => !hashFolds.ContainsKey(c)).ToFrozenSet();

        private static readonly FrozenSet<char> nvarcharHashClean =
            nvarcharWeights.Keys.Where(c => !hashFolds.ContainsKey(c)).ToFrozenSet();

        // Lazily-resolved hash folds for runes outside the repertoire,
        // computed by ComputeRuneFold on first sight and cached process-wide
        // (null = no fold; the rune keeps its own identity). Shared by the
        // varchar and nvarchar bodies — both delegate to the same inner
        // CultureCollation, whose equality defines the fold relation.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string?> runeFoldCache = new();

        private static FrozenDictionary<char, string> BuildHashFolds()
        {
            var map = new Dictionary<char, string>
            {
                ['\u00A0'] = " ",
                ['\u00AA'] = "a",
                ['\u00BA'] = "o",
                ['\u00B9'] = "1",
                ['\u00B2'] = "2",
                ['\u00B3'] = "3",
                ['\u00BC'] = "1\u20444",
                ['\u00BD'] = "1\u20442",
                ['\u00BE'] = "3\u20444",
                ['\u0152'] = "\u0153",
                ['\u0160'] = "\u0161",
                ['\u0178'] = "\u00FF",
                ['\u017D'] = "\u017E",
                ['\u0E33'] = "\u0E4D\u0E32",
            };
            for (var d = 0; d <= 9; d++)
                map[(char)(0x0E50 + d)] = ((char)('0' + d)).ToString();
            foreach (var ignorable in (int[])[
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
                0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
                0x7F, 0x81, 0x8D, 0x8F, 0x90, 0x9D, 0xAD])
            {
                map[(char)ignorable] = string.Empty;
            }

            return map.ToFrozenDictionary();
        }

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

        // nvarchar weights span the CP1252 repertoire (decoded per byte from the
        // unified ushort tables) plus the Thai block (keyed by cp - 0x0E00). Both
        // sets share the one unified rank scale, so a Latin/Thai mix compares
        // correctly.
        private static FrozenDictionary<char, (int, int)> BuildNvarcharWeights()
        {
            var encoding = CharSqlType.Cp1252Encoder;
            var map = new Dictionary<char, (int, int)>(NvarcharPrimaryByteRank.Length + ThaiPrimaryRank.Length);
            Span<byte> buffer = stackalloc byte[1];
            for (var b = 1; b < NvarcharPrimaryByteRank.Length; b++)
            {
                buffer[0] = (byte)b;
                var decoded = encoding.GetString(buffer);
                if (decoded.Length == 1)
                    map[decoded[0]] = (NvarcharPrimaryByteRank[b], NvarcharSecondaryByteRank[b]);
            }

            for (var i = 0; i < ThaiPrimaryRank.Length; i++)
                map[(char)(0x0E00 + i)] = (ThaiPrimaryRank[i], ThaiSecondaryRank[i]);

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
            : !this.InRepertoire(x) || !this.InRepertoire(y) ? this.inner.Compare(x, y)
            : CompareInRepertoire(x, y);

        // Equality intentionally diverges from Compare == 0 on the
        // cross-boundary path: Compare routes through the inner
        // CultureCollation's two-pass minimal-punctuation ORDERING, whose
        // tie-break checks only minimal-vs-real at each position and so
        // would equate an apostrophe with a hyphen. Equality uses the
        // inner's plain equality instead — the same equality/ordering
        // split CultureCollation itself has — keeping hyphen and
        // apostrophe distinct marks and staying consistent with the inner
        // GetHashCode the canonicalized hash path delegates to.
        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null
                    && (!this.InRepertoire(x) || !this.InRepertoire(y)
                        ? this.inner.Equals(x, y)
                        : this.CompareInRepertoire(x, y) == 0);

        public override int GetHashCode(string obj)
        {
            // Strings made only of weight-table characters with no
            // hash-fold entry (the overwhelmingly common case — identifiers
            // and CP1252 data) hash directly off their weight runs; the
            // clean set is the repertoire minus HashFolds' keys, so this
            // walk costs the same as InRepertoire.
            var clean = this.varcharStorage ? varcharHashClean : nvarcharHashClean;
            var isClean = true;
            foreach (var ch in obj)
            {
                if (!clean.Contains(ch))
                {
                    isClean = false;
                    break;
                }
            }

            if (isClean)
                return this.WeightRunHash(obj);

            // Everything else canonicalizes first so that any string the
            // equality relation can deem equal to an in-repertoire string
            // (fullwidth spellings, decomposed accents, ICU-ignorable
            // characters, cross-script homoglyphs) hashes exactly like that
            // string. Equals routes such cross-boundary pairs through the
            // inner CultureCollation, whose relation the canonicalizer
            // provably preserves: every substitution it makes is validated
            // inner-equal to what it replaces. A canonical form still
            // outside the repertoire has no in-repertoire equal partner, so
            // the inner hash (consistent with inner equality) covers it.
            var canonical = this.HashCanonicalize(obj);
            return this.InRepertoire(canonical)
                ? this.WeightRunHash(canonical)
                : this.inner.GetHashCode(canonical);
        }

        // Hash the primary weight run, a separator, then the secondary run —
        // mirrors the order Compare consults them, so Compare-equal strings
        // (equal at both levels) always hash equal. Streamed through the
        // cursor so no weight lists are materialized.
        private int WeightRunHash(string s)
        {
            var hash = new HashCode();
            var primary = this.NewCursor(s);
            while (primary.MoveNext())
                hash.Add(primary.Primary);
            hash.Add(-1);
            var secondary = this.NewCursor(s);
            while (secondary.MoveNext())
                hash.Add(secondary.Secondary);
            return hash.ToHashCode();
        }

        // Rewrites a string onto hash-canonical spellings: NFC composes
        // decomposed sequences (e + combining acute → é) into their CP1252
        // characters, then each rune folds through hashFolds (in-repertoire
        // entries) or the lazily-computed rune fold (everything else).
        // Every substitution is inner-collation-equal to what it replaces,
        // so canonicalization preserves the inner equality that governs
        // cross-repertoire-boundary Equals. Lone surrogates skip
        // normalization (it rejects invalid Unicode) and pass through
        // verbatim — the inner collation equates them with nothing.
        private string HashCanonicalize(string s)
        {
            string normalized;
            try
            {
                normalized = s.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                normalized = s;
            }

            var builder = new StringBuilder(normalized.Length);
            for (var i = 0; i < normalized.Length; i++)
            {
                var ch = normalized[i];
                if (char.IsHighSurrogate(ch) && i + 1 < normalized.Length && char.IsLowSurrogate(normalized[i + 1]))
                {
                    var fold = runeFoldCache.GetOrAdd(char.ConvertToUtf32(ch, normalized[i + 1]), ComputeRuneFold, this);
                    _ = fold is null
                        ? builder.Append(ch).Append(normalized[i + 1])
                        : builder.Append(fold);
                    i++;
                }
                else if (hashFolds.TryGetValue(ch, out var inRepFold))
                {
                    _ = builder.Append(inRepFold);
                }
                else if (!nvarcharWeights.ContainsKey(ch))
                {
                    var runeFold = runeFoldCache.GetOrAdd(ch, ComputeRuneFold, this);
                    _ = runeFold is null ? builder.Append(ch) : builder.Append(runeFold);
                }
                else
                {
                    _ = builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        // Resolves the hash fold of a rune outside the repertoire, cached
        // process-wide by the caller. Candidate generation is NFKC + lower
        // (covers fullwidth forms, superscripts, compat ligatures, singleton
        // canonical mappings like the Kelvin sign); a candidate is accepted
        // only when the inner collation confirms it equal, so an NFKC fold
        // ICU disagrees with (long s → "s") never lands. Runes whose compat
        // decomposition points the wrong way (Greek μ vs CP1252 µ, other
        // scripts' decimal digits) fall to a one-time scan of the repertoire
        // for a single-character inner-equal partner. Targets are routed
        // back through hashFolds so a fold can never re-introduce a
        // non-canonical in-repertoire spelling. Returns null (keep the rune
        // verbatim) when the inner collation equates it with nothing we can
        // reach — such runes have no in-repertoire equal partner, and the
        // inner hash covers string pairs built from them.
        private static string? ComputeRuneFold(int rune, SqlLatin1Cp1CiAsCollation self)
        {
            var s = char.ConvertFromUtf32(rune);
            if (self.inner.Equals(s, string.Empty))
                return string.Empty;

            string? candidate = null;
            try
            {
                // Lowercase (not the CA1308-preferred uppercase) is the
                // fold's target case: the repertoire's expansion-bearing
                // letters exist only in lowercase (ß) and every hashFolds
                // target is lowercase; the inner-equality validation below
                // gates any lossy lowering (İ) out.
#pragma warning disable CA1308
                candidate = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
#pragma warning restore CA1308
            }
            catch (ArgumentException)
            {
            }

            if (candidate is not null && candidate != s && candidate.Length > 0
                && AllInNvarcharRepertoire(candidate) && self.inner.Equals(s, candidate))
            {
                return ApplyHashFolds(candidate);
            }

            foreach (var repertoireChar in nvarcharWeights.Keys)
            {
                if (self.inner.Equals(s, repertoireChar.ToString()))
                    return ApplyHashFolds(char.ToLowerInvariant(repertoireChar).ToString());
            }

            return null;
        }

        private static bool AllInNvarcharRepertoire(string s)
        {
            foreach (var ch in s)
            {
                if (!nvarcharWeights.ContainsKey(ch))
                    return false;
            }

            return true;
        }

        private static string ApplyHashFolds(string s)
        {
            var builder = new StringBuilder(s.Length);
            foreach (var ch in s)
                _ = builder.Append(hashFolds.TryGetValue(ch, out var fold) ? fold : ch.ToString());
            return builder.ToString();
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

        // Repertoire is storage-dependent: the varchar body knows only CP1252;
        // the nvarchar body also knows the Thai block. A string outside the
        // active body's repertoire falls back to the inner CultureCollation.
        private bool InRepertoire(string s)
        {
            var weights = this.varcharStorage ? varcharWeights : nvarcharWeights;
            foreach (var ch in s)
            {
                if (!weights.ContainsKey(ch))
                    return false;
            }

            return true;
        }
    }
}
