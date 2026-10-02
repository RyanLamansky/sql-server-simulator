using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The encoder fallback a single-byte code page narrows with: .NET's own
/// best-fit table for the code page, plus the handful of characters real's
/// conversion maps that the table leaves to <c>?</c> — the figure, narrow
/// no-break and thin spaces, two dashes, and the reversed and double-prime
/// quotation marks — identically under code pages 1252, 850 and 437 (probed
/// 2026-10-02 against SQL Server 2025 over U+0080–U+04FF, U+1E00–U+22FF,
/// U+2500–U+25FF, U+FB00–U+FB4F and U+FF00–U+FFEF; nothing else differs).
/// Code page 874's .NET table lacks a further dozen of the punctuation fits
/// the others carry, which real makes there too (probed 2026-10-02).
/// </summary>
/// <remarks>
/// A fallback runs only for a character the code page lacks, so the encode of
/// mappable text — every row the default collation stores — never reaches it.
/// </remarks>
internal sealed class WindowsBestFitFallback : EncoderFallback
{
    private readonly Encoding bestFit;

    private WindowsBestFitFallback(Encoding bestFit) => this.bestFit = bestFit;

    /// <summary>
    /// <paramref name="encoding"/>, which carries .NET's best-fit fallback,
    /// rebuilt to narrow through this one while decoding as before.
    /// </summary>
    public static Encoding Wrap(Encoding encoding) =>
        Encoding.GetEncoding(encoding.CodePage, new WindowsBestFitFallback(encoding), encoding.DecoderFallback);

    public override int MaxCharCount => 2;

    public override EncoderFallbackBuffer CreateFallbackBuffer() => new Buffer(this.bestFit);

    /// <summary>The characters real maps that .NET's table doesn't.</summary>
    private static char? Supplement(char unknown) => unknown switch
    {
        '\u2007' or '\u202F' => '\u00A0',
        '\u2008' or '\u2009' or '\u200A' => ' ',
        '\u2012' or '\u2015' => '-',
        '\u201B' => '\'',
        '\u201F' or '\u2033' or '\u2036' => '"',
        _ => null,
    };

    /// <summary>The fits real makes where the code page's own .NET table gives up, which only code page 874's does.</summary>
    private static char LastResort(char unknown) => unknown switch
    {
        >= '\u2000' and <= '\u2006' => ' ',
        '\u2010' or '\u2011' => '-',
        '\u201A' => ',',
        '\u201E' => '"',
        '\u2024' => '.',
        '\u2032' or '\u2035' => '\'',
        '\u2044' => '/',
        '\u2236' => ':',
        _ => '?',
    };

    private sealed class Buffer(Encoding bestFit) : EncoderFallbackBuffer
    {
        private char first;
        private char second;
        private int count;
        private int position;

        public override int Remaining => this.count - this.position;

        public override bool Fallback(char charUnknown, int index)
        {
            var fit = BestFit(bestFit, [charUnknown]);
            this.first = Supplement(charUnknown) ?? (fit == '?' ? LastResort(charUnknown) : fit);
            (this.count, this.position) = (1, 0);
            return true;
        }

        // A supplementary character narrows to one '?' per UTF-16 unit, as
        // real's conversion does.
        public override bool Fallback(char charUnknownHigh, char charUnknownLow, int index)
        {
            this.first = BestFit(bestFit, [charUnknownHigh]);
            this.second = BestFit(bestFit, [charUnknownLow]);
            (this.count, this.position) = (2, 0);
            return true;
        }

        public override char GetNextChar() =>
            this.position >= this.count ? '\0' : this.position++ == 0 ? this.first : this.second;

        public override bool MovePrevious()
        {
            if (this.position == 0)
                return false;
            this.position--;
            return true;
        }

        public override void Reset() => (this.count, this.position) = (0, 0);

        /// <summary>What .NET's own best fit makes of the character, read back as the one character it narrowed to.</summary>
        private static char BestFit(Encoding bestFit, ReadOnlySpan<char> unknown)
        {
            Span<byte> narrowed = stackalloc byte[4];
            var written = bestFit.GetBytes(unknown, narrowed);
            Span<char> read = stackalloc char[4];
            return written == 0 || bestFit.GetChars(narrowed[..1], read) == 0 ? '?' : read[0];
        }
    }
}
