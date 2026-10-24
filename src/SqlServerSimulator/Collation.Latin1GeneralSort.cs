using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator;

internal abstract partial class Collation
{
    /// <summary>
    /// The byte-exact sort body for every name whose Unicode data real orders
    /// by the Latin1-General weight table: the Windows <c>Latin1_General</c>
    /// names, unversioned and <c>_100_</c> (each version its own table), and the
    /// <c>SQL_Latin1_General</c> names over code pages 1, 850 and 437 — the
    /// simulator's default collation among them. Neither table matches .NET's
    /// <see cref="CompareInfo"/> ordering. Each was extracted as dense ranks of
    /// <c>NCHAR(n) + N'a'</c> under the name's <c>_CI_AI</c>, <c>_CI_AS</c> and
    /// <c>_CS_AI</c> forms over U+0000–U+024F, the code page 1252 repertoire,
    /// Thai, Latin Extended Additional and General Punctuation (probed
    /// 2026-10-02 against SQL Server 2025):
    /// <list type="bullet">
    /// <item>The levels compare in Windows' own order, each over the whole
    /// string before the next: the letters (<c>'à'</c> &lt; <c>'Ao'</c>), their
    /// accents (<c>'cafe'</c> &lt; <c>'café'</c>), their case — lowercase first,
    /// a superscript after its digit — then the minimal-weight characters. An
    /// <c>_AI</c> name skips the accents and a <c>_CI</c> name the case.</item>
    /// <item>Apostrophe, hyphen, the dashes, soft hyphen and the controls weigh
    /// only at that last level (<c>'coop'</c> &lt; <c>'co-op'</c>); the
    /// characters a version doesn't know weigh nothing at all; the Thai tone
    /// marks are accents on the letter before them.</item>
    /// <item>The Latin ligatures expand to their letters at every level
    /// (<c>'æ'</c> = <c>'ae'</c>, <c>'ß'</c> = <c>'ss'</c>, <c>'ǅ'</c> =
    /// <c>'Dž'</c>).</item>
    /// <item>The default collation's <b>varchar</b> data takes SQL sort order
    /// 52 instead: per character with no minimal-weight characters (<c>'coop'</c>
    /// &gt; <c>'co-op'</c>), CHAR(0) weighted below everything, and only æ, Æ
    /// and ß expanding, a ligature's last letter weighed above every
    /// accent.</item>
    /// </list>
    /// A character outside the table's repertoire weighs as the table text the
    /// inner <see cref="CultureCollation"/> calls it equal to, as an accent, or
    /// as a foreign element placed by that collation's order (see
    /// <see cref="Outsider"/>), so every string has one weight sequence.
    /// Metadata (name, description, storage encoding) delegates to that same
    /// parser-built inner. See <c>docs/claude/collations.md</c>.
    /// </summary>
    internal sealed partial class Latin1GeneralTableCollation : Collation
    {
        internal const string DefaultName = "SQL_Latin1_General_CP1_CI_AS";

        // Dense ranks indexed by CP1252 byte (0..255; byte 0 ranks 0, below every other). The
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

        // Sort order 51 (SQL_Latin1_General_CP1_CS_AS): the dense rank of CHAR(n) + 'a',
        // which orders a case pair uppercase first — its case level.
        private static ReadOnlySpan<ushort> SortOrder51ByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 49, 50, 51, 52, 53, 54,
            55, 143, 159, 161, 165, 167, 177, 179, 181, 183, 193, 195, 197, 199, 201, 205,
            219, 221, 223, 225, 228, 230, 240, 242, 244, 246, 251, 56, 57, 58, 59, 60,
            61, 144, 160, 162, 166, 168, 178, 180, 182, 184, 194, 196, 198, 200, 202, 206,
            220, 222, 224, 226, 229, 231, 241, 243, 245, 247, 252, 62, 63, 64, 65, 66,
            67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82,
            83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98,
            99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114,
            115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130,
            145, 147, 149, 151, 153, 155, 157, 163, 169, 171, 173, 175, 185, 187, 189, 191,
            253, 203, 207, 209, 211, 213, 215, 131, 217, 232, 234, 236, 238, 248, 255, 227,
            146, 148, 150, 152, 154, 156, 158, 164, 170, 172, 174, 176, 186, 188, 190, 192,
            254, 204, 208, 210, 212, 214, 216, 132, 218, 233, 235, 237, 239, 249, 256, 250,
        ];

        // Sort order 54 (SQL_Latin1_General_CP1_CI_AI): the same rank, which folds
        // accents but holds the cedilla apart — its second level.
        private static ReadOnlySpan<ushort> SortOrder54ByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 49, 50, 51, 52, 53, 54,
            55, 143, 145, 146, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159,
            160, 161, 162, 163, 165, 166, 167, 168, 169, 170, 171, 56, 57, 58, 59, 60,
            61, 143, 145, 146, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159,
            160, 161, 162, 163, 165, 166, 167, 168, 169, 170, 171, 62, 63, 64, 65, 66,
            67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82,
            83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98,
            99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114,
            115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130,
            143, 143, 143, 143, 143, 143, 144, 147, 149, 149, 149, 149, 153, 153, 153, 153,
            172, 158, 159, 159, 159, 159, 159, 131, 159, 166, 166, 166, 166, 170, 173, 164,
            143, 143, 143, 143, 143, 143, 144, 147, 149, 149, 149, 149, 153, 153, 153, 153,
            172, 158, 159, 159, 159, 159, 159, 132, 159, 166, 166, 166, 166, 170, 173, 170,
        ];

        // The code page 850 and 437 names' varchar sort orders: the dense rank of
        // each byte's character + 'a' under the name's _CI_AI, _CI_AS and _CS_AS
        // forms (probed 2026-10-02 against SQL Server 2025), which fold the
        // cedilla onto c at their letters as 52's adjusted rank does.
        private static ReadOnlySpan<ushort> CodePage850CiAiByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 49, 50, 51, 52, 53, 54,
            55, 142, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157,
            158, 159, 160, 161, 163, 164, 165, 166, 167, 168, 169, 56, 57, 58, 59, 60,
            61, 142, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157,
            158, 159, 160, 161, 163, 164, 165, 166, 167, 168, 169, 62, 63, 64, 65, 66,
            145, 164, 147, 142, 142, 142, 142, 145, 147, 147, 147, 151, 151, 151, 142, 142,
            147, 143, 143, 157, 157, 157, 164, 164, 168, 157, 164, 157, 67, 157, 68, 69,
            142, 151, 157, 164, 156, 156, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
            80, 81, 82, 83, 84, 142, 142, 142, 85, 86, 87, 88, 89, 90, 91, 92,
            93, 94, 95, 96, 97, 98, 142, 142, 99, 100, 101, 102, 103, 104, 105, 106,
            170, 170, 147, 147, 147, 151, 151, 151, 151, 107, 108, 109, 110, 111, 151, 112,
            157, 162, 157, 157, 157, 157, 113, 171, 171, 164, 164, 164, 168, 168, 114, 115,
            116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131,
        ];

        private static ReadOnlySpan<ushort> CodePage850CiAsByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 49, 50, 51, 52, 53, 54,
            55, 142, 150, 151, 153, 154, 159, 160, 161, 162, 168, 169, 170, 171, 172, 174,
            181, 182, 183, 184, 186, 187, 192, 193, 194, 195, 198, 56, 57, 58, 59, 60,
            61, 142, 150, 151, 153, 154, 159, 160, 161, 162, 168, 169, 170, 171, 172, 174,
            181, 182, 183, 184, 186, 187, 192, 193, 194, 195, 198, 62, 63, 64, 65, 66,
            152, 191, 156, 145, 147, 143, 148, 152, 157, 158, 155, 166, 165, 163, 147, 148,
            156, 149, 149, 177, 179, 175, 190, 188, 197, 179, 191, 180, 67, 180, 68, 69,
            144, 164, 176, 189, 173, 173, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
            80, 81, 82, 83, 84, 144, 145, 143, 85, 86, 87, 88, 89, 90, 91, 92,
            93, 94, 95, 96, 97, 98, 146, 146, 99, 100, 101, 102, 103, 104, 105, 106,
            199, 199, 157, 158, 155, 167, 164, 165, 166, 107, 108, 109, 110, 111, 163, 112,
            176, 185, 177, 175, 178, 178, 113, 200, 200, 189, 190, 188, 196, 196, 114, 115,
            116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131,
        ];

        private static ReadOnlySpan<ushort> CodePage850CsAsByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 49, 50, 51, 52, 53, 54,
            55, 142, 158, 160, 164, 166, 176, 178, 180, 182, 193, 195, 197, 199, 201, 205,
            219, 221, 223, 225, 228, 230, 240, 242, 244, 246, 251, 56, 57, 58, 59, 60,
            61, 143, 159, 161, 165, 167, 177, 179, 181, 183, 194, 196, 198, 200, 202, 206,
            220, 222, 224, 226, 229, 231, 241, 243, 245, 247, 252, 62, 63, 64, 65, 66,
            162, 239, 171, 149, 153, 145, 155, 163, 173, 175, 169, 191, 189, 185, 152, 154,
            170, 157, 156, 212, 216, 208, 237, 233, 250, 215, 238, 218, 67, 217, 68, 69,
            147, 187, 210, 235, 204, 203, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
            80, 81, 82, 83, 84, 146, 148, 144, 85, 86, 87, 88, 89, 90, 91, 92,
            93, 94, 95, 96, 97, 98, 151, 150, 99, 100, 101, 102, 103, 104, 105, 106,
            254, 253, 172, 174, 168, 192, 186, 188, 190, 107, 108, 109, 110, 111, 184, 112,
            209, 227, 211, 207, 214, 213, 113, 255, 256, 234, 236, 232, 249, 248, 114, 115,
            116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131,
        ];

        private static ReadOnlySpan<ushort> CodePage437CiAiByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 49, 50, 51, 52, 53, 54,
            55, 159, 161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 173, 174,
            175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 56, 57, 58, 59, 60,
            61, 159, 161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 173, 174,
            175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 62, 63, 64, 65, 66,
            162, 180, 164, 159, 159, 159, 159, 162, 164, 164, 164, 168, 168, 168, 159, 159,
            164, 160, 160, 174, 174, 174, 180, 180, 184, 174, 180, 67, 68, 69, 70, 71,
            159, 168, 174, 180, 173, 173, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
            82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
            98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
            114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129,
            186, 187, 188, 189, 190, 190, 191, 192, 193, 194, 195, 196, 130, 131, 197, 132,
            133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148,
        ];

        private static ReadOnlySpan<ushort> CodePage437CiAsByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 49, 50, 51, 52, 53, 54,
            55, 159, 166, 167, 169, 170, 175, 176, 177, 178, 183, 184, 185, 186, 187, 189,
            194, 195, 196, 197, 198, 199, 204, 205, 206, 207, 209, 56, 57, 58, 59, 60,
            61, 159, 166, 167, 169, 170, 175, 176, 177, 178, 183, 184, 185, 186, 187, 189,
            194, 195, 196, 197, 198, 199, 204, 205, 206, 207, 209, 62, 63, 64, 65, 66,
            168, 203, 172, 162, 163, 160, 164, 168, 173, 174, 171, 182, 181, 179, 163, 164,
            172, 165, 165, 192, 193, 190, 202, 200, 208, 193, 203, 67, 68, 69, 70, 71,
            161, 180, 191, 201, 188, 188, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
            82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
            98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
            114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129,
            210, 211, 212, 213, 214, 214, 215, 216, 217, 218, 219, 220, 130, 131, 221, 132,
            133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148,
        ];

        private static ReadOnlySpan<ushort> CodePage437CsAsByteRank =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 49, 50, 51, 52, 53, 54,
            55, 159, 170, 172, 176, 178, 185, 187, 189, 191, 197, 199, 201, 203, 205, 209,
            216, 218, 220, 222, 224, 226, 233, 235, 237, 239, 242, 56, 57, 58, 59, 60,
            61, 160, 171, 173, 177, 179, 186, 188, 190, 192, 198, 200, 202, 204, 206, 210,
            217, 219, 221, 223, 225, 227, 234, 236, 238, 240, 243, 62, 63, 64, 65, 66,
            174, 232, 182, 163, 165, 161, 167, 175, 183, 184, 180, 196, 195, 193, 164, 166,
            181, 169, 168, 213, 215, 211, 230, 228, 241, 214, 231, 67, 68, 69, 70, 71,
            162, 194, 212, 229, 208, 207, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
            82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
            98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
            114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129,
            244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255, 130, 131, 256, 132,
            133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148,
        ];

        // Ligatures the sort expands to their letters. The Unicode tables
        // expand the whole Latin set, each letter in its own case — the _100_
        // table the db and qp digraphs too, which the unversioned one doesn't
        // know; sort order 52 expands only æ / Æ / ß (œ Œ þ Þ are single-weight
        // letters there), and has no case level to keep.
        private static readonly FrozenDictionary<char, string> unicodeExpansion = new Dictionary<char, string>
        {
            { 'Æ', "AE" }, { 'æ', "ae" }, { 'Þ', "TH" }, { 'þ', "th" }, { 'ß', "ss" }, { 'Œ', "OE" }, { 'œ', "oe" },
            { 'Ĳ', "IJ" }, { 'ĳ', "ij" }, { 'Ǆ', "DŽ" }, { 'ǅ', "Dž" }, { 'ǆ', "dž" }, { 'Ǉ', "LJ" }, { 'ǈ', "Lj" }, { 'ǉ', "lj" },
            { 'Ǌ', "NJ" }, { 'ǋ', "Nj" }, { 'ǌ', "nj" }, { 'Ǳ', "DZ" }, { 'ǲ', "Dz" }, { 'ǳ', "dz" }, { 'ȸ', "db" }, { 'ȹ', "qp" },
            { 'Ǽ', "AE" }, { 'ǽ', "ae" }, { 'Ǣ', "AE" }, { 'ǣ', "ae" },
        }.ToFrozenDictionary();

        // The ligatures under an accent, which expand to their letters at the
        // primary and case levels but weigh their last letter's accent above
        // every real one, the acute below the macron: 'aë' < 'ǽ' < 'ǣ' < 'ãe'
        // (probed 2026-10-02 against SQL Server 2025).
        private static readonly FrozenDictionary<char, int> accentedLigature = new Dictionary<char, int>
        {
            { 'Ǽ', 1 }, { 'ǽ', 1 }, { 'Ǣ', 2 }, { 'ǣ', 2 },
        }.ToFrozenDictionary();

        private static readonly FrozenDictionary<char, string> sortOrder52Expansion = new Dictionary<char, string>
        {
            { 'æ', "ae" }, { 'Æ', "AE" }, { 'ß', "ss" },
        }.ToFrozenDictionary();

        // Code page 437's byte 0xE1 is a single letter of its own sort order.
        private static readonly FrozenDictionary<char, string> codePage437Expansion = new Dictionary<char, string>
        {
            { 'æ', "ae" }, { 'Æ', "AE" },
        }.ToFrozenDictionary();

        // The SQL names' varchar sort orders share sort order 52's letters
        // (the accent-folded rank, the cedilla folded onto c) and its
        // ligatures, and differ in what follows them (probed 2026-10-02
        // against SQL Server 2025): 52 weighs the accents, 51 the accents and
        // then the case, uppercase first, and 54 only the cedilla. The Pref
        // name's 53 is 52 with uppercase preferred in a sort's final tiebreak.
        private static readonly WeightTable sortOrder52 = WeightTable.FromSortOrder(1252, VarcharPrimaryByteRank, VarcharSecondaryByteRank, default, sortOrder52Expansion);

        private static readonly WeightTable sortOrder51 = WeightTable.FromSortOrder(1252, VarcharPrimaryByteRank, VarcharSecondaryByteRank, SortOrder51ByteRank, sortOrder52Expansion);

        private static readonly WeightTable sortOrder54 = WeightTable.FromSortOrder(1252, VarcharPrimaryByteRank, SortOrder54ByteRank, default, sortOrder52Expansion);

        // Code pages 850 and 437 follow the same shapes: _CS_AS weighs its full
        // case rank as 51 does, _CI_AS its accents as 52 does, and _CI_AI its
        // letters alone.
        private static readonly WeightTable codePage850CsAs = WeightTable.FromSortOrder(850, CodePage850CiAiByteRank, CodePage850CiAsByteRank, CodePage850CsAsByteRank, sortOrder52Expansion);

        private static readonly WeightTable codePage850CiAs = WeightTable.FromSortOrder(850, CodePage850CiAiByteRank, CodePage850CiAsByteRank, default, sortOrder52Expansion);

        private static readonly WeightTable codePage850CiAi = WeightTable.FromSortOrder(850, CodePage850CiAiByteRank, CodePage850CiAiByteRank, default, sortOrder52Expansion);

        private static readonly WeightTable codePage437CsAs = WeightTable.FromSortOrder(437, CodePage437CiAiByteRank, CodePage437CiAsByteRank, CodePage437CsAsByteRank, codePage437Expansion);

        private static readonly WeightTable codePage437CiAs = WeightTable.FromSortOrder(437, CodePage437CiAiByteRank, CodePage437CiAsByteRank, default, codePage437Expansion);

        private static readonly WeightTable codePage437CiAi = WeightTable.FromSortOrder(437, CodePage437CiAiByteRank, CodePage437CiAiByteRank, default, codePage437Expansion);

        private static readonly WeightTable unicode80 = WeightTable.FromRecords(Version80Weights, unicodeExpansion);

        private static readonly WeightTable unicode100 = WeightTable.FromRecords(Version100Weights, unicodeExpansion);

        // The parser-built CultureCollation for this name, supplying the
        // metadata (description, storage encoding) and the equality and order
        // that place characters past the table's repertoire (Outsider). Typed
        // as the sealed concrete body so the JIT devirtualizes the delegated
        // calls.
        private readonly CultureCollation inner;

        // The inner body's CompareInfo and the name's strengths in it, which
        // order foreign elements (ForeignOptions).
        private readonly CompareInfo compareInfo;
        private readonly CompareOptions matchingOptions;
        private readonly WeightTable table;
        private readonly Collation? varcharBody;

        // The name's strengths: case weighs only under a _CS name, the accent
        // level only under an _AS one.
        private readonly bool caseSensitive;
        private readonly bool accentSensitive;

        // How each character past the table weighs, resolved on first sight
        // and kept per body, since each name's strengths decide which table
        // text the inner body calls a character equal to.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Outsider> outsiders = new();

        // The table letters both orders agree on, in the inner body's order of
        // letters alone, with their table weights (AnchorOf); built on the
        // first foreign element.
        private (string Letter, int Weight)[]? anchors;

        // Built by the parser (Collation.Parser.cs) for every name the table
        // serves, handing in the freshly-constructed CultureCollation. A
        // Windows name's varchar data compares as Unicode, so the body serves
        // both storage families; a SQL name's varchar data keeps its own sort
        // order — the code page 1 names' in a sort order table, the rest in the
        // culture body's varchar sibling.
        internal Latin1GeneralTableCollation(CultureCollation inner, int? version)
        {
            this.inner = inner;
            (this.compareInfo, this.matchingOptions) = inner.LinguisticMatching.GetValueOrDefault();
            this.table = version is >= 100 ? unicode100 : unicode80;
            this.caseSensitive = inner.CaseSensitive;
            this.accentSensitive = inner.AccentSensitive;
            this.varcharBody = SortOrderFor(inner.Name) is var (sortOrder, caseLevel, prefersUppercase)
                ? new Latin1GeneralTableCollation(inner, sortOrder, caseLevel, prefersUppercase)
                : inner.Name.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase) ? inner.ForVarcharStorage() : null;
        }

        // The varchar sort order of a SQL name, with whether it weighs a case
        // level and prefers uppercase in a sort's final tiebreak.
        private static (WeightTable Table, bool CaseLevel, bool PrefersUppercase)? SortOrderFor(string name)
        {
            Span<char> folded = stackalloc char[name.Length];
            _ = name.AsSpan().ToUpperInvariant(folded);
            return folded switch
            {
                "SQL_LATIN1_GENERAL_CP1_CI_AI" => (sortOrder54, false, false),
                "SQL_LATIN1_GENERAL_CP1_CI_AS" => (sortOrder52, false, false),
                "SQL_LATIN1_GENERAL_CP1_CS_AS" => (sortOrder51, true, false),
                "SQL_LATIN1_GENERAL_CP437_CI_AI" => (codePage437CiAi, false, false),
                "SQL_LATIN1_GENERAL_CP437_CI_AS" => (codePage437CiAs, false, false),
                "SQL_LATIN1_GENERAL_CP437_CS_AS" => (codePage437CsAs, true, false),
                "SQL_LATIN1_GENERAL_CP850_CI_AI" => (codePage850CiAi, false, false),
                "SQL_LATIN1_GENERAL_CP850_CI_AS" => (codePage850CiAs, false, false),
                "SQL_LATIN1_GENERAL_CP850_CS_AS" => (codePage850CsAs, true, false),
                "SQL_LATIN1_GENERAL_PREF_CP1_CI_AS" => (sortOrder52, false, true),
                "SQL_LATIN1_GENERAL_PREF_CP437_CI_AS" => (codePage437CiAs, false, true),
                "SQL_LATIN1_GENERAL_PREF_CP850_CI_AS" => (codePage850CiAs, false, true),
                _ => null,
            };
        }

        // A sort order's varchar body weighs every level its table holds,
        // whatever the name's own strengths.
        private Latin1GeneralTableCollation(CultureCollation inner, WeightTable varcharTable, bool caseLevel, bool prefersUppercase)
        {
            this.inner = inner;
            (this.compareInfo, this.matchingOptions) = inner.LinguisticMatching.GetValueOrDefault();
            this.table = varcharTable;
            this.caseSensitive = caseLevel;
            this.accentSensitive = true;
            this.prefersUppercase = prefersUppercase;
        }

        // The Pref name's varchar data: equal under the collation, ordered
        // uppercase first by a sort's final tiebreak (see PreferenceCompare).
        private readonly bool prefersUppercase;

        internal override int PreferenceCompare(string x, string y)
        {
            if (!this.prefersUppercase)
                return 0;
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                if (x[i] == y[i])
                    continue;
                var xUpper = char.IsUpper(x[i]);
                if (xUpper != char.IsUpper(y[i]))
                    return xUpper ? -1 : 1;
            }
            return 0;
        }

        public override string Name => this.inner.Name;

        public override string Description => this.inner.Description;

        public override bool CaseSensitive => this.caseSensitive;

        internal override int AnsiCodePage => this.inner.AnsiCodePage;

        internal override bool IsSupplementaryCharacterAware => this.inner.IsSupplementaryCharacterAware;

        internal override (CompareInfo Info, CompareOptions Options)? LinguisticMatching => this.inner.LinguisticMatching;

        // Every text weighs by the table, a character past its repertoire as
        // its Outsider, so every match goes through it.
        internal override bool MatchesByTable(ReadOnlySpan<char> text) => true;

        // A run matches where its collation elements equal the subject's at
        // the name's strengths and end on a character boundary, so a ligature
        // matches its letters and half of one matches nothing — N'ß' LIKE N'ss'
        // and CHARINDEX(N'ss', N'aßb') find it, N'ß' LIKE N's%' doesn't — and
        // the characters weighing nothing right after it ride along (probed
        // 2026-10-02 against SQL Server 2025) — after an anchored run weighing
        // nothing too, so an emoji the unversioned names don't know is LIKE
        // itself.
        internal override int TableMatch(ReadOnlySpan<char> subject, ReadOnlySpan<char> run, bool anchored)
        {
            var target = new MatchCursor(run, this);
            var have = new MatchCursor(subject, this);
            var matchedEnd = -1;
            while (target.MoveNext())
            {
                if (!have.MoveNext() || !this.SameElement(ref target, ref have))
                    return -1;
                matchedEnd = have.CharEnd;
            }
            if (matchedEnd < 0)
            {
                if (!anchored)
                    return 0;
                matchedEnd = 0;
            }
            else if (have.MoveNext() && have.CharStart < matchedEnd)
            {
                return -1;
            }
            while (matchedEnd < subject.Length && this.WeighsNothingAt(subject, matchedEnd, out var length))
                matchedEnd += length;
            return matchedEnd;
        }

        internal override bool BeginsTableMatch(ReadOnlySpan<char> s, int index)
        {
            var entry = this.table.Lookup(s[index]);
            if (entry.Kind != WeightKind.Absent)
                return entry.Kind is not (WeightKind.Weightless or WeightKind.Diacritic);
            var outsider = this.OutsiderOf(RuneAt(s, index, out _));
            return outsider.Spelling is not { Length: 0 } && outsider.Mark == 0;
        }

        // Whether the character at s[i] weighs nothing — a weightless table
        // character, or one past the table whose spelling is empty.
        private bool WeighsNothingAt(ReadOnlySpan<char> s, int i, out int length)
        {
            length = 1;
            var entry = this.table.Lookup(s[i]);
            if (entry.Kind != WeightKind.Absent)
                return entry.Kind == WeightKind.Weightless;
            return this.OutsiderOf(RuneAt(s, i, out length)).Spelling is { Length: 0 };
        }

        private bool SameElement(ref MatchCursor x, ref MatchCursor y) =>
            x.Minimal == y.Minimal
            && (x.Minimal
                ? x.Secondary == y.Secondary
                : x.IsForeign || y.IsForeign
                    ? x.IsForeign && y.IsForeign && x.Primary == y.Primary && this.compareInfo.Compare(x.ForeignText, y.ForeignText, this.matchingOptions) == 0
                    : x.Primary == y.Primary
                        && (this.table.LegacySortOrder
                            ? (this.caseSensitive ? x.Case == y.Case : x.Secondary == y.Secondary) && x.Tertiary == y.Tertiary
                            : (!this.accentSensitive || x.Secondary == y.Secondary) && (!this.caseSensitive || x.Case == y.Case)));

        // The collation elements a match compares, minimal-weight characters
        // included, each tagged with the character span it came from — an
        // expansion's letters share their ligature's, and a character past the
        // table's spelling shares that character's.
        private ref struct MatchCursor(ReadOnlySpan<char> s, Latin1GeneralTableCollation owner)
        {
            private readonly ReadOnlySpan<char> s = s;

            private readonly Latin1GeneralTableCollation owner = owner;

            private readonly WeightTable table = owner.table;

            private int index;

            private string? expansion;

            private int expansionPos;

            // The table text a character past the table weighs as, read ahead
            // of the string's own characters, and that character's span.
            private string? spelling;

            private int spellingPos;

            private int spelledStart;

            internal bool Minimal;

            internal int Primary;

            internal int Secondary;

            internal int Tertiary;

            internal int Case;

            internal int CharStart;

            internal int CharEnd;

            // A foreign element's text in the string; empty for a table element.
            private int foreignStart;

            private int foreignLength;

            internal readonly bool IsForeign => this.foreignLength != 0;

            internal readonly ReadOnlySpan<char> ForeignText => this.s.Slice(this.foreignStart, this.foreignLength);

            internal bool MoveNext()
            {
                if (this.expansion is not null)
                {
                    this.Expanded();
                    return true;
                }

                while (true)
                {
                    char ch;
                    int start;
                    var spelled = this.spelling is not null;
                    if (spelled)
                    {
                        if (this.spellingPos == this.spelling!.Length)
                        {
                            this.spelling = null;
                            continue;
                        }
                        ch = this.spelling[this.spellingPos++];
                        start = this.spelledStart;
                    }
                    else if (this.index < this.s.Length)
                    {
                        start = this.index;
                        ch = this.s[this.index++];
                    }
                    else
                    {
                        return false;
                    }

                    var entry = this.table.Lookup(ch);
                    this.foreignLength = 0;
                    switch (entry.Kind)
                    {
                        case WeightKind.Weighted:
                            var accents = 0;
                            if (!spelled && this.index < this.s.Length && this.s[this.index] >= FirstMark)
                                entry = this.owner.WithMarks(this.s, ref this.index, entry, out accents);
                            (this.Minimal, this.Primary, this.Secondary, this.Tertiary, this.Case) = (false, entry.Primary, (entry.Secondary * MarkSpan) + accents, 0, entry.Case);
                            (this.CharStart, this.CharEnd) = (start, this.index);
                            return true;
                        case WeightKind.MinimalWeight:
                            (this.Minimal, this.Primary, this.Secondary, this.Tertiary, this.Case) = (true, 0, entry.Secondary, 0, 0);
                            (this.CharStart, this.CharEnd) = (start, this.index);
                            return true;
                        case WeightKind.Expanding:
                            this.expansion = this.table.Expansions[ch];
                            this.expansionPos = 0;
                            (this.CharStart, this.CharEnd) = (start, this.index);
                            this.Expanded();
                            return true;
                        case WeightKind.Absent when !spelled:
                            var rune = RuneAt(this.s, start, out var length);
                            this.index = start + length;
                            var outsider = this.owner.OutsiderOf(rune);
                            if (outsider.Spelling is { } spelling)
                            {
                                if (spelling.Length != 0)
                                    (this.spelling, this.spellingPos, this.spelledStart) = (spelling, 0, start);
                                continue;
                            }
                            if (outsider.Mark != 0)
                                continue;
                            this.index = this.owner.PastMarks(this.s, this.index);
                            (this.Minimal, this.Primary, this.Secondary, this.Tertiary, this.Case) = (false, outsider.Anchor, 0, 0, 0);
                            (this.foreignStart, this.foreignLength) = (start, this.index - start);
                            (this.CharStart, this.CharEnd) = (start, this.index);
                            return true;
                        default:
                            continue;
                    }
                }
            }

            private void Expanded()
            {
                var letter = this.table.Lookup(this.expansion![this.expansionPos++]);
                (this.Minimal, this.Primary, this.Secondary, this.Tertiary, this.Case) = (false, letter.Primary, letter.Secondary * MarkSpan, 1, letter.Case);
                this.foreignLength = 0;
                if (this.expansionPos >= this.expansion.Length)
                {
                    if (this.table.LegacySortOrder)
                        (this.Secondary, this.Case) = (LigatureAccent, LigatureAccent);
                    this.expansion = null;
                }
            }
        }

        internal override SurrogateMatching SurrogateMatching => this.inner.SurrogateMatching;

        internal override WeightlessCharacters Weightless => this.inner.Weightless;

        internal override Encoding StorageEncoding => this.inner.StorageEncoding;

        internal override Collation ForVarcharStorage() => this.varcharBody ?? this;

        internal override bool WeightsNul => this.table.LegacySortOrder;

        // Its letters and digits order alphabetically, and only ligatures
        // weigh as more than one character, never two characters as one; a
        // character past its repertoire matches and orders as its Outsider,
        // which LIKE matches to a run's character only where it weighs as that
        // character, so the table matches and orders every text by the same
        // weights, a run's spaces and punctuation included.
        internal override bool SortsAsciiPrefixesTogether => true;

        internal override bool SortsPrintableAsciiPrefixesTogether => true;

        /// <summary>Whether this is varchar data under sort order 52 (or the Pref name's 53, which weighs alike).</summary>
        internal bool UsesSortOrder52 => ReferenceEquals(this.table, sortOrder52);

        /// <summary>Whether this body orders by a Unicode table, and which: the <c>_100_</c> one or the unversioned.</summary>
        internal bool? UsesVersion100Table => ReferenceEquals(this.table, unicode100) ? true : ReferenceEquals(this.table, unicode80) ? false : null;

        public override int Compare(string? x, string? y) =>
            x is null ? (y is null ? 0 : -1)
            : y is null ? 1
            : this.CompareWeights(x, y);

        // Identical text is equal under any collation, which settles the
        // common identifier match (a column or alias spelled as declared)
        // without the weight walks and the minimal-weight tiebreak behind them.
        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && (string.Equals(x, y, StringComparison.Ordinal) || this.CompareWeights(x, y) == 0);

        public override int GetHashCode(string obj) => this.WeightRunHash(obj);

        // Hash the primary weight run, a separator, then (where the name weighs
        // accents) the secondary run — mirrors the order Compare consults them,
        // so Compare-equal strings always hash equal; a foreign element adds
        // the inner body's hash of its text at the same strength. A trailing
        // run of elements weighing as a space is left out, as the padding
        // Compare applies ignores it (the _100_ table's narrow no-break space
        // weighs as one). Streamed through the cursor so no weight lists are
        // materialized.
        private int WeightRunHash(string s)
        {
            var hash = new HashCode();
            var space = this.table.Space.Primary;
            var primary = new WeightCursor(s, this);
            var elements = 0;
            var kept = 0;
            var pendingSpaces = 0;
            while (primary.MoveNext() is var element and not Element.End)
            {
                elements++;
                if (element == Element.Table && primary.Primary == space)
                {
                    pendingSpaces++;
                    continue;
                }
                for (; pendingSpaces > 0; pendingSpaces--)
                    hash.Add(space);
                hash.Add(primary.Primary);
                if (element == Element.Foreign)
                    hash.Add(this.compareInfo.GetHashCode(primary.ForeignText, this.ForeignOptions(Level.Primary)));
                kept = elements;
            }
            hash.Add(-1);
            if (this.accentSensitive)
            {
                var secondary = new WeightCursor(s, this);
                for (var i = 0; i < kept; i++)
                {
                    hash.Add(secondary.MoveNext() == Element.Foreign
                        ? this.compareInfo.GetHashCode(secondary.ForeignText, this.ForeignOptions(Level.Secondary))
                        : secondary.Secondary);
                }
            }
            return hash.ToHashCode();
        }

        /// <summary>
        /// How a character past the table's repertoire weighs. Real's table
        /// weighs every character the simulator's leaves out, and the width and
        /// compatibility variants of its own characters as those characters —
        /// an ideographic space as a space, a fullwidth hyphen as a hyphen
        /// (probed 2026-10-24 against SQL Server 2025) — so a character the
        /// inner body calls equal to its compatibility form in the table weighs
        /// as that form (<see cref="Spelling"/>, empty for one weighing
        /// nothing); a combining mark the letter before it doesn't compose with
        /// weighs as an accent on it (<see cref="Mark"/>); and anything else is
        /// a foreign element sorting just above <see cref="Anchor"/>, among
        /// other foreign elements by the inner body's order. Every string then
        /// has one weight sequence, so the order is one total order, which an
        /// index's key order, a sort and a seek can all rely on. Comparing a
        /// pair holding such a character by the inner body instead mixes two
        /// orders that disagree about the table's own characters — the inner
        /// body weighs a no-break space as a space, the table apart from one —
        /// so an ideographic-space spelling would be equal to both.
        /// </summary>
        private readonly struct Outsider(string? spelling, int mark, int anchor)
        {
            public readonly string? Spelling = spelling;
            public readonly int Mark = mark;
            public readonly int Anchor = anchor;
        }

        private Outsider OutsiderOf(int rune) => this.outsiders.GetOrAdd(rune, static (rune, self) => self.ResolveOutsider(rune), this);

        private Outsider ResolveOutsider(int rune)
        {
            // A lone surrogate half reaches here as its own code unit.
            var text = rune <= char.MaxValue ? ((char)rune).ToString() : char.ConvertFromUtf32(rune);
            if (this.inner.Equals(text, string.Empty))
                return new(string.Empty, 0, 0);
            if (this.SpellingOf(text) is { } spelling)
                return new(spelling, 0, 0);
            if (CharUnicodeInfo.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
                return new(null, rune is >= 0x300 and <= 0x36F ? 1 + rune - 0x300 : 113 + (rune % 900), 0);
            return new(null, 0, this.AnchorOf(text));
        }

        // The table text the inner body's CompareInfo calls a character equal
        // to under the name's strengths: its compatibility form — the
        // fullwidth, small, superscript and wide variants' plain characters —
        // where the table weighs every character of it. CompareInfo rather
        // than the body's own equality, which weighs a hyphen or apostrophe
        // apart and so its fullwidth form, which the table weighs as one. A
        // width variant — the ideographic space, a small or fullwidth form —
        // weighs as its plain form under a case-sensitive name too, as every
        // table name is width-insensitive, where CompareInfo's width option
        // leaves the ideographic space apart.
        private string? SpellingOf(string text)
        {
            string compatible;
            try
            {
                compatible = text.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (compatible.Length == 0 || string.Equals(compatible, text, StringComparison.Ordinal) || !this.table.Covers(compatible))
                return null;
            if (this.compareInfo.Compare(text, compatible, this.matchingOptions) == 0)
                return compatible;
            return text[0] is '\u3000' or (>= '\uFE50' and <= '\uFE6F') or (>= '\uFF00' and <= '\uFFEF')
                && this.compareInfo.Compare(text, compatible, this.matchingOptions | CompareOptions.IgnoreCase) == 0
                && char.IsUpper(text[0]) == char.IsUpper(compatible[0])
                ? compatible
                : null;
        }

        // Where a foreign element sorts: just above the table weight of the
        // last letter the inner body places at or below it by letters alone,
        // among the letters both orders put in one order — so a script the
        // table doesn't know follows the table's letters the inner body puts
        // ahead of it and precedes those it puts after it (Greek, Cyrillic
        // and the ideographs follow the Latin letters and Greek precedes Thai,
        // as on real, probed 2026-10-24 against SQL Server 2025), a letter the
        // two orders place far apart (the micro sign, a symbol to the table, a
        // Greek letter to the inner body) moving no foreign element with it;
        // and foreign elements keep the inner body's order among themselves.
        private int AnchorOf(string text)
        {
            var anchors = this.anchors ??= this.BuildAnchors();
            var options = this.ForeignOptions(Level.Primary);
            int low = 0, high = anchors.Length;
            while (low < high)
            {
                var middle = (int)((uint)(low + high) >> 1);
                if (this.compareInfo.Compare(anchors[middle].Letter, text, options) <= 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low == 0 ? int.MinValue : anchors[low - 1].Weight;
        }

        // The table's letters in the inner body's order, cut to the longest
        // run whose table weights never fall — the letters the two orders
        // agree on.
        private (string Letter, int Weight)[] BuildAnchors()
        {
            var options = this.ForeignOptions(Level.Primary);
            var letters = new List<(string Text, int Weight)>();
            foreach (var ch in this.table.Repertoire)
            {
                if (this.table.Lookup(ch) is { Kind: WeightKind.Weighted } entry)
                    letters.Add((ch.ToString(), entry.Primary));
            }
            letters.Sort((x, y) => this.compareInfo.Compare(x.Text, y.Text, options));

            // Patience sorting: tails[k] is the index of the smallest last
            // weight of a non-falling run of length k + 1.
            var tails = new List<int>();
            var previous = new int[letters.Count];
            for (var i = 0; i < letters.Count; i++)
            {
                int low = 0, high = tails.Count;
                while (low < high)
                {
                    var middle = (low + high) >> 1;
                    if (letters[tails[middle]].Weight <= letters[i].Weight)
                        low = middle + 1;
                    else
                        high = middle;
                }
                previous[i] = low > 0 ? tails[low - 1] : -1;
                if (low == tails.Count)
                    tails.Add(i);
                else
                    tails[low] = i;
            }
            var run = new (string Letter, int Weight)[tails.Count];
            for (int k = run.Length - 1, i = tails.Count == 0 ? -1 : tails[^1]; k >= 0; k--, i = previous[i])
                run[k] = letters[i];
            return run;
        }

        // The inner body's comparison of foreign elements at each level: the
        // letters alone, then their accents, then the name's own strengths.
        private CompareOptions ForeignOptions(Level level) => level switch
        {
            Level.Primary => this.matchingOptions | CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace,
            Level.Secondary => this.matchingOptions | CompareOptions.IgnoreCase,
            _ => this.matchingOptions,
        };

        // The code point at s[i], a surrogate pair read whole.
        private static int RuneAt(ReadOnlySpan<char> s, int i, out int length)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                length = 2;
                return char.ConvertToUtf32(ch, s[i + 1]);
            }
            length = 1;
            return ch;
        }

        // The accent a combining mark past the table at s[i] weighs as, 0 for
        // any other character.
        private int MarkAt(ReadOnlySpan<char> s, int i, out int length)
        {
            length = 1;
            return this.table.Lookup(s[i]).Kind == WeightKind.Absent ? this.OutsiderOf(RuneAt(s, i, out length)).Mark : 0;
        }

        // Past the combining marks beyond the table starting at s[i].
        private int PastMarks(ReadOnlySpan<char> s, int i)
        {
            while (i < s.Length && this.MarkAt(s, i, out var length) != 0)
                i += length;
            return i;
        }

        // No character below it is a combining mark or a tone mark, so a
        // letter followed by one reads no further: the per-character walk's
        // one test for the marks WithMarks reads.
        private const char FirstMark = '\u0300';

        // A letter and the marks after it: the table character a letter and
        // combining marks past the table compose to, as real weighs 's' +
        // U+0307 as 'ṡ', and the accent weight the marks left add. index
        // stands just past the letter and moves past the marks.
        private WeightEntry WithMarks(ReadOnlySpan<char> s, ref int index, WeightEntry letter, out int accents)
        {
            letter = this.ComposedWithMarks(s, ref index, letter);
            accents = this.AccentsAfter(s, ref index);
            return letter;
        }

        // The table character a letter and the combining marks past the table
        // after it compose to, moving index past the marks where they do.
        private WeightEntry ComposedWithMarks(ReadOnlySpan<char> s, ref int index, WeightEntry letter)
        {
            var end = this.PastMarks(s, index);
            if (end == index)
                return letter;
            string composed;
            try
            {
                composed = s[(index - 1)..end].ToString().Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return letter;
            }
            if (composed.Length != 1 || this.table.Lookup(composed[0]) is not { Kind: WeightKind.Weighted } entry)
                return letter;
            index = end;
            return entry;
        }

        // The accent weight a letter's following marks add: a Thai tone mark
        // the table holds, or combining marks past the table it didn't compose
        // with, each an accent on the letter below the next letter's own —
        // real weighs a tone mark as an accent on the letter, so 'a' + mark +
        // 'b' sorts after 'ab' and before 'áb' (probed 2026-10-02 against SQL
        // Server 2025).
        private int AccentsAfter(ReadOnlySpan<char> s, ref int index)
        {
            if (index < s.Length && this.table.Lookup(s[index]) is { Kind: WeightKind.Diacritic } mark)
            {
                index++;
                return mark.Secondary;
            }
            var accents = 0;
            while (index < s.Length && this.MarkAt(s, index, out var length) is var accent and not 0)
            {
                index += length;
                accents += accent;
            }
            return Math.Min(accents, MarkSpan - 1);
        }

        // The levels in Windows' own order — the letters, their diacritics,
        // their case, then the minimal-weight characters — each consulted only
        // where the name weighs it: Latin1_General_CS_AS orders 'ab' < 'Aa' on
        // the letters and 'aB' < 'Ab' on the first case difference, lowercase
        // first, and a ligature carries the case of its letters ('Æ' = 'AE',
        // probed 2026-10-02 against SQL Server 2025).
        private int CompareWeights(string x, string y)
        {
            var primary = this.CompareLevel(x, y, Level.Primary);
            if (primary != 0)
                return primary;

            // Sort order 51's case rank is the full one, uppercase before every
            // accented form: 'AÉ' < 'ae' and 'Eé' < 'eE' (probed 2026-10-02
            // against SQL Server 2025), so it stands in for the accent level.
            if (this.table.LegacySortOrder && this.caseSensitive)
            {
                var full = this.CompareLevel(x, y, Level.Case);
                return full != 0 ? full : this.CompareLevel(x, y, Level.Tertiary);
            }

            var secondary = this.accentSensitive ? this.CompareLevel(x, y, Level.Secondary) : 0;
            if (secondary != 0)
                return secondary;
            var letterCase = this.caseSensitive ? this.CompareLevel(x, y, Level.Case) : 0;
            if (letterCase != 0)
                return letterCase;

            // Sort order 52 resolves a remaining tie by the ligature tertiary (a
            // ligature sorts just after its expansion: 'ae' < 'æ', 'ss' < 'ß');
            // it has no minimal-weight characters. The Unicode tables treat a
            // ligature as equal to its expansion and instead break the tie on
            // the minimal-weight characters.
            return this.table.LegacySortOrder ? this.CompareLevel(x, y, Level.Tertiary) : this.MinimalWeightTiebreak(x, y);
        }

        // Above every rank the secondary tables hold.
        private const int LigatureAccent = int.MaxValue;

        // A Thai tone mark's accent weight rides on the letter before it, so a
        // letter's accent weight leaves room for one mark below it.
        private const int MarkSpan = 1024;

        private enum Level
        {
            Primary,
            Secondary,
            Tertiary,
            Case,
        }

        // What a WeightCursor's MoveNext reached.
        private enum Element : byte
        {
            End,
            Table,
            Foreign,
        }

        // Walks both operands through parallel weight cursors, comparing one
        // level at a time. Once a shared prefix ties, the longer run's rest is
        // compared against a space, the padding real applies — so under
        // sort order 52 a control character (weighted below the space,
        // CHAR(0) lowest of all) sorts a string before its own prefix: 'a' +
        // CHAR(0) < 'a' (probed 2026-09-26 against SQL Server 2025), and under
        // the Unicode tables, where nothing weighs below the space, a run of
        // spaces the minimal-weight characters hide from the trailing-space
        // trim adds nothing: N'aE -' ties a Æ followed by an apostrophe on the
        // letters (probed 2026-10-02 against SQL Server 2025). The cursors are
        // ref structs over the source strings — no weight lists are
        // allocated, so the common single-pass primary comparison is
        // allocation-free.
        private int CompareLevel(string x, string y, Level level)
        {
            var cx = new WeightCursor(x, this);
            var cy = new WeightCursor(y, this);
            while (true)
            {
                var ex = cx.MoveNext();
                var ey = cy.MoveNext();
                if (ex == Element.Table && ey == Element.Table)
                {
                    var wx = cx.Weight(level);
                    var wy = cy.Weight(level);
                    if (wx != wy)
                        return wx < wy ? -1 : 1;
                    continue;
                }
                if (ex == Element.End || ey == Element.End)
                {
                    if (ex == ey)
                        return 0;
                    var padded = this.table.Space.Weight(level);
                    var hasX = ex != Element.End;
                    var rest = hasX ? cx : cy;
                    var element = hasX ? ex : ey;
                    do
                    {
                        // A foreign element meets the padding as it meets the
                        // space's own element: below it only where its anchor is.
                        var order = element == Element.Foreign ? (level == Level.Primary && rest.Primary < padded ? -1 : 1) : rest.Weight(level).CompareTo(padded);
                        if (order != 0)
                            return (order < 0) == hasX ? -1 : 1;
                    }
                    while ((element = rest.MoveNext()) != Element.End);
                    return 0;
                }
                var compared = this.CompareForeign(ref cx, ex, ref cy, ey, level);
                if (compared != 0)
                    return compared < 0 ? -1 : 1;
            }
        }

        // Two elements at least one of which is foreign: a foreign element
        // sorts just above the table weight it's anchored at, and two with one
        // anchor by the inner body at the level's strength.
        private int CompareForeign(ref WeightCursor x, Element ex, ref WeightCursor y, Element ey, Level level)
        {
            if (level == Level.Primary && x.Primary != y.Primary)
                return x.Primary < y.Primary ? -1 : 1;
            if (ex != ey)
                return ex == Element.Foreign ? 1 : -1;
            return level == Level.Tertiary ? 0 : this.compareInfo.Compare(x.ForeignText, y.ForeignText, this.ForeignOptions(level));
        }

        // Yields the (primary, secondary, case, tertiary) weight of each
        // collation element of a string in order: minimal-weight and
        // weightless characters are skipped, a ligature expands to its letters
        // (tertiary 1, plain characters 0), a Thai tone mark folds into the
        // accent weight of the letter before it, and a character past the
        // table weighs as its Outsider — its spelling's elements, an accent on
        // the letter before it, or a foreign element of its own. One element
        // per MoveNext.
        private ref struct WeightCursor(string s, Latin1GeneralTableCollation owner)
        {
            // The string being read: the cursor's own, or the table text a
            // character past the table weighs as while that is read.
            private string s = s;

            private readonly Latin1GeneralTableCollation owner = owner;

            private readonly WeightTable table = owner.table;

            private int index;

            private string? expansion;

            private int expansionPos;

            private char ligature;

            // While a spelling is read, the string it stands in and the
            // position past its character, read again once the spelling ends.
            private string? resume;

            private int resumeIndex;

            internal int Primary;

            internal int Secondary;

            internal int Tertiary;

            internal int Case;

            // The last foreign element's text in the string, which no
            // spelling holds.
            private int foreignStart;

            private int foreignLength;

            internal readonly ReadOnlySpan<char> ForeignText => this.s.AsSpan(this.foreignStart, this.foreignLength);

            internal readonly int Weight(Level level) => level switch
            {
                Level.Primary => this.Primary,
                Level.Secondary => this.Secondary,
                Level.Case => this.Case,
                _ => this.Tertiary,
            };

            internal Element MoveNext()
            {
                if (this.expansion is not null)
                {
                    this.EmitExpanded();
                    return Element.Table;
                }

                while (true)
                {
                    if (this.index >= this.s.Length)
                    {
                        if (this.resume is null)
                            return Element.End;
                        (this.s, this.index, this.resume) = (this.resume, this.resumeIndex, null);
                        continue;
                    }

                    var ch = this.s[this.index++];
                    var entry = this.table.Lookup(ch);
                    switch (entry.Kind)
                    {
                        case WeightKind.Weighted:
                            var accents = 0;
                            if (this.index < this.s.Length && this.s[this.index] >= FirstMark)
                                entry = this.owner.WithMarks(this.s, ref this.index, entry, out accents);
                            this.Primary = entry.Primary;
                            this.Secondary = (entry.Secondary * MarkSpan) + accents;
                            this.Tertiary = 0;
                            this.Case = entry.Case;
                            return Element.Table;
                        case WeightKind.Expanding:
                            this.expansion = this.table.Expansions[ch];
                            this.expansionPos = 0;
                            this.ligature = ch;
                            this.EmitExpanded();
                            return Element.Table;
                        case WeightKind.Absent:
                            var start = this.index - 1;
                            var rune = RuneAt(this.s, start, out var length);
                            this.index = start + length;
                            var outsider = this.owner.OutsiderOf(rune);
                            if (outsider.Spelling is { } spelling)
                            {
                                // The table covers every spelling, so none
                                // holds a spelling of its own.
                                if (spelling.Length != 0)
                                    (this.resume, this.resumeIndex, this.s, this.index) = (this.s, this.index, spelling, 0);
                                continue;
                            }
                            // A combining mark with no table letter to ride on
                            // weighs nothing at these levels.
                            if (outsider.Mark != 0)
                                continue;
                            this.index = this.owner.PastMarks(this.s, this.index);
                            (this.Primary, this.Secondary, this.Tertiary, this.Case) = (outsider.Anchor, 0, 0, 0);
                            (this.foreignStart, this.foreignLength) = (start, this.index - start);
                            return Element.Foreign;
                        default:
                            // Minimal-weight, weightless, and a tone mark with no
                            // letter before it: nothing at these levels.
                            continue;
                    }
                }
            }

            private void EmitExpanded()
            {
                var letter = this.table.Lookup(this.expansion![this.expansionPos++]);
                this.Primary = letter.Primary;
                this.Secondary = letter.Secondary * MarkSpan;
                this.Tertiary = 1;
                this.Case = letter.Case;
                if (this.expansionPos >= this.expansion.Length)
                {
                    // The SQL sort orders mark a ligature's last letter with
                    // an accent weight above every real accent — on 51's case
                    // rank too, which carries its accents: 'sš' < 'ß', 'aË' <
                    // 'æ', 'AÉ' < 'Æ', while 'æ' < 'àe' on the first letter
                    // (probed 2026-10-02 against SQL Server 2025).
                    if (this.table.LegacySortOrder)
                    {
                        this.Secondary = LigatureAccent;
                        this.Case = LigatureAccent;
                    }
                    else
                    {
                        this.Secondary = accentedLigature.TryGetValue(this.ligature, out var accent)
                            ? LigatureAccent - 3 + accent
                            : (letter.Secondary * MarkSpan) + (this.index < this.s.Length && this.s[this.index] >= FirstMark ? this.owner.AccentsAfter(this.s, ref this.index) : 0);
                    }
                    this.expansion = null;
                }
            }
        }

        // Reached only when every weighed level ties: the two strings can
        // differ only in their minimal-weight characters. Compare those
        // characters alone, each tagged by the count of letters preceding it —
        // a string with fewer or later minimal marks sorts first (absence
        // before presence: 'coop' < 'co-op'), and the raw length difference an
        // expansion leaves behind (e.g. 'ß' vs 'ss') is ignored.
        private int MinimalWeightTiebreak(string x, string y)
        {
            var keyX = this.MinimalWeightKey(x);
            var keyY = this.MinimalWeightKey(y);
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

        private List<(int Preceding, int Rank)> MinimalWeightKey(string s)
        {
            var key = new List<(int, int)>();
            var preceding = 0;
            this.AddMinimalWeights(s, key, ref preceding);
            return key;
        }

        private void AddMinimalWeights(ReadOnlySpan<char> s, List<(int, int)> key, ref int preceding)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var entry = this.table.Lookup(s[i]);
                switch (entry.Kind)
                {
                    case WeightKind.MinimalWeight:
                        key.Add((preceding, entry.Secondary));
                        break;
                    case WeightKind.Expanding:
                        // Count the expanded letters so marks align across a ligature.
                        preceding += this.table.Expansions[s[i]].Length;
                        break;
                    case WeightKind.Weighted:
                        preceding++;
                        break;
                    case WeightKind.Absent:
                        // A character past the table counts as its spelling's
                        // characters, a foreign element as a letter, a mark as
                        // nothing.
                        var outsider = this.OutsiderOf(RuneAt(s, i, out var length));
                        i += length - 1;
                        if (outsider.Spelling is { } spelling)
                            this.AddMinimalWeights(spelling, key, ref preceding);
                        else if (outsider.Mark == 0)
                            preceding++;
                        break;
                }
            }
        }

        private enum WeightKind : byte
        {
            Absent,
            Weighted,
            MinimalWeight,
            Weightless,
            Expanding,
            Diacritic,
        }

        private readonly struct WeightEntry(WeightKind kind, int primary, int secondary, int letterCase)
        {
            public readonly WeightKind Kind = kind;
            public readonly int Primary = primary;
            public readonly int Secondary = secondary;
            public readonly int Case = letterCase;

            public int Weight(Level level) => level switch
            {
                Level.Primary => this.Primary,
                Level.Secondary => this.Secondary * MarkSpan,
                Level.Case => this.Case,
                _ => 0,
            };
        }

        /// <summary>
        /// One weight table: the entries of U+0000–U+024F in an array, which
        /// nearly all text stays inside — the dictionary lookups were most of a
        /// sort's time — and the rest in a frozen dictionary.
        /// </summary>
        private sealed class WeightTable
        {
            private const int LowLimit = 0x250;

            private readonly WeightEntry[] low;

            private readonly FrozenDictionary<char, WeightEntry> high;

            public readonly FrozenDictionary<char, string> Expansions;

            public readonly bool LegacySortOrder;

            public readonly WeightEntry Space;

            public readonly char[] Repertoire;

            private WeightTable(Dictionary<char, WeightEntry> entries, FrozenDictionary<char, string> expansions, bool legacySortOrder)
            {
                this.low = new WeightEntry[LowLimit];
                var high = new Dictionary<char, WeightEntry>();
                foreach (var (ch, entry) in entries)
                {
                    if (ch < LowLimit)
                        this.low[ch] = entry;
                    else
                        high[ch] = entry;
                }
                this.high = high.ToFrozenDictionary();
                this.Expansions = expansions;
                this.LegacySortOrder = legacySortOrder;
                this.Space = entries[' '];
                this.Repertoire = [.. entries.Keys];
            }

            public WeightEntry Lookup(char ch) =>
                ch < LowLimit ? this.low[ch] : this.high.GetValueOrDefault(ch);

            /// <summary>Whether every character of <paramref name="s"/> is in the table's repertoire.</summary>
            public bool Covers(ReadOnlySpan<char> s)
            {
                foreach (var ch in s)
                {
                    if (this.Lookup(ch).Kind == WeightKind.Absent)
                        return false;
                }

                return true;
            }

            /// <summary>
            /// The Unicode table from its records — code point, kind (0 weighted,
            /// 1 minimal-weight, 2 weightless, 3 expanding, 4 tone mark), then the
            /// three ranks.
            /// </summary>
            public static WeightTable FromRecords(ReadOnlySpan<ushort> records, FrozenDictionary<char, string> expansions)
            {
                var entries = new Dictionary<char, WeightEntry>(records.Length / 5);
                for (var i = 0; i < records.Length; i += 5)
                {
                    var kind = records[i + 1] switch
                    {
                        0 => WeightKind.Weighted,
                        1 => WeightKind.MinimalWeight,
                        2 => WeightKind.Weightless,
                        3 => WeightKind.Expanding,
                        _ => WeightKind.Diacritic,
                    };
                    entries[(char)records[i]] = new(kind, records[i + 2], records[i + 3], records[i + 4]);
                }
                return new(entries, expansions, legacySortOrder: false);
            }

            // Decode each CP1252 byte to its .NET char. The 0x80-0x9F window
            // decodes to scattered BMP code points (€ ƒ Ÿ …); the rest are
            // identity for ASCII / Latin-1. Byte 0 is a character like any
            // other to the varchar sort, weighted below every other (rank 0 at
            // both levels): 'a' + CHAR(0) + 'b' < 'ab' and CHAR(0) <> ''
            // (probed 2026-09-26 against SQL Server 2025). A sort order with no
            // case level passes an empty span for it.
            public static WeightTable FromSortOrder(int codePage, ReadOnlySpan<byte> primary, ReadOnlySpan<byte> secondary, ReadOnlySpan<ushort> letterCase, FrozenDictionary<char, string> expansions) =>
                FromSortOrder(codePage, Widen(primary), Widen(secondary), letterCase, expansions);

            public static WeightTable FromSortOrder(int codePage, ReadOnlySpan<byte> primary, ReadOnlySpan<ushort> secondary, ReadOnlySpan<ushort> letterCase, FrozenDictionary<char, string> expansions) =>
                FromSortOrder(codePage, Widen(primary), secondary, letterCase, expansions);

            public static WeightTable FromSortOrder(int codePage, ReadOnlySpan<ushort> primary, ReadOnlySpan<ushort> secondary, ReadOnlySpan<ushort> letterCase, FrozenDictionary<char, string> expansions)
            {
                var encoding = AnsiEncoding(codePage);
                var entries = new Dictionary<char, WeightEntry>(primary.Length);
                Span<byte> buffer = stackalloc byte[1];
                for (var b = 0; b < primary.Length; b++)
                {
                    buffer[0] = (byte)b;
                    var decoded = encoding.GetString(buffer);
                    if (decoded.Length == 1)
                        entries[decoded[0]] = new(expansions.ContainsKey(decoded[0]) ? WeightKind.Expanding : WeightKind.Weighted, primary[b], secondary[b], letterCase.IsEmpty ? 0 : letterCase[b]);
                }
                return new(entries, expansions, legacySortOrder: true);
            }

            private static ReadOnlySpan<ushort> Widen(ReadOnlySpan<byte> bytes)
            {
                var widened = new ushort[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                    widened[i] = bytes[i];
                return widened;
            }
        }
    }
}
