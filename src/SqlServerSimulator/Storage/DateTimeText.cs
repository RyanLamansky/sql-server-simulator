namespace SqlServerSimulator.Storage;

/// <summary>
/// How a date-time string failed to parse. The legacy <c>datetime</c> /
/// <c>smalldatetime</c> pair reports the two differently — Msg 241 (or 295)
/// for a string it can't read, Msg 242 for one it reads into a value that
/// doesn't exist — where the four newer types report Msg 241 for both.
/// </summary>
internal enum DateTimeTextError
{
    None,
    Syntax,
    Range,
}

/// <summary>
/// A date-time string read the way SQL Server's CAST / CONVERT (style 0)
/// reads one: a date, a time, or both, and an optional offset. The date is
/// null when the string named none (the target supplies 1900-01-01), and
/// <see cref="TimeTicks"/> can reach a whole day when more than seven
/// fractional digits rounded up past midnight — which <c>time</c> clamps
/// and the date-bearing types carry into the next day.
/// </summary>
internal readonly struct DateTimeText(DateOnly? date, long timeTicks, TimeSpan? offset)
{
    public readonly DateOnly? Date = date;
    public readonly long TimeTicks = timeTicks;
    public readonly TimeSpan? Offset = offset;

    /// <summary>The date, or the 1900-01-01 base a time-only string implies.</summary>
    public DateOnly DateOrBase => this.Date ?? new DateOnly(1900, 1, 1);

    /// <summary>The instant, with a rounded-up midnight carried into the next day.</summary>
    public DateTime DateTime => this.DateOrBase.ToDateTime(TimeOnly.MinValue).AddTicks(this.TimeTicks);

    /// <summary>
    /// Reads <paramref name="text"/> for one of the date-time types, probed
    /// 2026-09-24 against SQL Server 2025 over a matrix of some two hundred
    /// strings. <paramref name="legacy"/> selects <c>datetime</c> /
    /// <c>smalldatetime</c>'s rules, which differ from the newer types' in
    /// both directions:
    /// <list type="bullet">
    /// <item>Only the legacy pair takes a time before the date
    /// (<c>'10:00 2024-01-01'</c>), mixed date separators
    /// (<c>'2024-12/31'</c>), a fraction written after the minutes
    /// (<c>'10:00.5'</c>), or a <c>Z</c> after an ISO <c>T</c> time.</item>
    /// <item>Only the newer types take more than three fractional digits (a
    /// digit past the seventh rounds), an offset (<c>±h:mm</c>, at most
    /// fourteen hours), a <c>Z</c> after any time, or a space before the ISO
    /// <c>T</c>.</item>
    /// </list>
    /// Everything else is shared: numeric dates in the session's
    /// <c>SET DATEFORMAT</c> order (see <c>OrderNumericDate</c>), with a
    /// one- or two-digit year pivoting at 50; year-first ISO forms; the six-
    /// and eight-digit unseparated forms; a bare four-digit year; English month
    /// names, three-letter or in full, around a day and year in the orders real
    /// takes; times of <c>h[:m[:s[.fraction | :milliseconds]]]</c> with an
    /// optional <c>AM</c> / <c>PM</c>, the hour alone allowed only beside one;
    /// spaces around every separator; and an empty string as 1900-01-01.
    /// </summary>
    public static DateTimeTextError TryParse(ReadOnlySpan<char> text, bool legacy, out DateTimeText value)
    {
        value = default;
        // The newer types take a tab wherever they take a space; to the legacy
        // pair a tab is text it can't read (probed 2026-09-24).
        var scanner = new Scanner(legacy ? text.Trim(' ') : text.Trim(" \t"), tabsAreSpaces: !legacy);
        if (scanner.AtEnd)
        {
            value = new DateTimeText(null, 0, null);
            return DateTimeTextError.None;
        }

        DateOnly? date = null;
        var time = default(TimeValue);
        var hasTime = false;
        var isoT = false;
        TimeSpan? offset = null;

        if (scanner.StartsTime())
        {
            var timeError = scanner.ParseTime(legacy, requireSeconds: false, out time);
            if (timeError != DateTimeTextError.None)
                return timeError;
            hasTime = true;
            _ = scanner.SkipSpaces();
            if (!scanner.AtEnd && scanner.Peek != '+' && scanner.Peek != '-' && !scanner.PeekZ())
            {
                // A date after the time: the legacy pair's alone.
                if (!legacy)
                    return DateTimeTextError.Syntax;
                var dateError = scanner.ParseDate(legacy, out var dateAfter, out _);
                if (dateError != DateTimeTextError.None)
                    return dateError;
                date = dateAfter;
            }
        }
        else if (scanner.Peek is 'T' or 't' && scanner.DigitFollows())
        {
            // A T with no date before it reads as a date the legacy pair can't
            // make sense of.
            return legacy ? DateTimeTextError.Range : DateTimeTextError.Syntax;
        }
        else
        {
            var dateError = scanner.ParseDate(legacy, out var parsed, out var isoDashes);
            if (dateError != DateTimeTextError.None)
                return dateError;
            date = parsed;

            var beforeSeparator = scanner.Position;
            var spaced = scanner.SkipSpaces();
            if (scanner.Peek is 'T' or 't')
            {
                // The ISO T: after a year-first dashed date, and followed by a
                // time with seconds. The newer types also take a space before
                // it.
                if (!isoDashes || (spaced && legacy))
                    return DateTimeTextError.Syntax;
                scanner.Advance();
                var timeError = scanner.ParseTime(legacy, requireSeconds: true, out time);
                if (timeError != DateTimeTextError.None)
                    return timeError;
                hasTime = true;
                isoT = true;
                if (scanner.PeekZ())
                {
                    scanner.Advance();
                    offset = TimeSpan.Zero;
                }
            }
            else if (scanner.PeekZ() && !spaced)
            {
                if (!isoDashes)
                    return DateTimeTextError.Syntax;
                scanner.Advance();
                offset = TimeSpan.Zero;
            }
            else if (!scanner.AtEnd)
            {
                if (!spaced && scanner.Position == beforeSeparator && !scanner.StartsTime())
                    return DateTimeTextError.Syntax;
                if (!scanner.StartsTime())
                {
                    // A bare hour, or AM / PM with no hour, ending the string: a
                    // time the legacy pair reads as out of range.
                    return legacy && scanner.IsLegacyRangeTail() ? DateTimeTextError.Range : DateTimeTextError.Syntax;
                }
                var timeError = scanner.ParseTime(legacy, requireSeconds: false, out time);
                if (timeError != DateTimeTextError.None)
                    return timeError;
                hasTime = true;
            }
        }

        if (hasTime && offset is null)
        {
            // An ISO T time takes its Z or offset attached.
            if (scanner.SkipSpaces() && isoT && (scanner.PeekZ() || scanner.Peek is '+' or '-'))
                return DateTimeTextError.Syntax;
            if (scanner.PeekZ())
            {
                if (legacy)
                    return DateTimeTextError.Syntax;
                scanner.Advance();
                offset = TimeSpan.Zero;
            }
            else if (scanner.Peek is '+' or '-')
            {
                // The legacy pair reads a minus as a range it can't place and a
                // plus as nothing at all.
                if (legacy)
                    return scanner.Peek == '-' ? DateTimeTextError.Range : DateTimeTextError.Syntax;
                var offsetError = scanner.ParseOffset(out var parsedOffset);
                if (offsetError != DateTimeTextError.None)
                    return offsetError;
                offset = parsedOffset;
            }
        }

        _ = scanner.SkipSpaces();
        if (!scanner.AtEnd)
            return legacy && scanner.IsLegacyRangeTail() ? DateTimeTextError.Range : DateTimeTextError.Syntax;

        value = new DateTimeText(date, hasTime ? time.Ticks : 0, offset);
        return DateTimeTextError.None;
    }

    private readonly struct TimeValue(long ticks)
    {
        public readonly long Ticks = ticks;
    }

    private ref struct Scanner(ReadOnlySpan<char> text, bool tabsAreSpaces)
    {
        private readonly ReadOnlySpan<char> text = text;
        private readonly bool tabsAreSpaces = tabsAreSpaces;
        public int Position;

        public readonly bool AtEnd => this.Position >= this.text.Length;

        public readonly char Peek => this.AtEnd ? '\0' : this.text[this.Position];

        public void Advance() => this.Position++;

        public readonly bool PeekZ() => this.Peek is 'Z' or 'z' && !IsLetterAt(this.text, this.Position + 1);

        /// <summary>Skips spaces, reporting whether there were any.</summary>
        public bool SkipSpaces()
        {
            var start = this.Position;
            while (!this.AtEnd && this.IsSpace(this.text[this.Position]))
                this.Position++;
            return this.Position > start;
        }

        private readonly bool IsSpace(char c) => c == ' ' || (c == '\t' && this.tabsAreSpaces);

        public readonly bool StartsNumber() => !this.AtEnd && char.IsAsciiDigit(this.text[this.Position]);

        public readonly bool DigitFollows() =>
            this.Position + 1 < this.text.Length && char.IsAsciiDigit(this.text[this.Position + 1]);

        /// <summary>
        /// Whether all that's left is one number or one AM / PM, after any
        /// spaces, periods, commas and minus signs — which the legacy pair
        /// reads as a value it can't place, where other leftovers are text it
        /// can't read (<c>'2024-12-31 23'</c>, <c>'2024-12-31 .5'</c>,
        /// <c>'2024-06-15 -5'</c> and <c>'10:00:00,5'</c> are Msg 242;
        /// <c>'2024-01-01x'</c> is 241).
        /// </summary>
        public readonly bool IsLegacyRangeTail()
        {
            var i = this.Position;
            while (i < this.text.Length && (this.text[i] is '.' or ',' or '-' || this.IsSpace(this.text[i])))
                i++;
            if (MeridiemAt(this.text, i, out var length) != 0)
            {
                i += length;
            }
            else
            {
                var start = i;
                while (i < this.text.Length && char.IsAsciiDigit(this.text[i]))
                    i++;
                if (i == start)
                    return false;
            }
            while (i < this.text.Length && this.IsSpace(this.text[i]))
                i++;
            return i == this.text.Length;
        }

        /// <summary>
        /// Whether a time starts here: a number followed, past any spaces, by
        /// a colon or by AM / PM.
        /// </summary>
        public readonly bool StartsTime()
        {
            var i = this.Position;
            var digits = 0;
            while (i < this.text.Length && char.IsAsciiDigit(this.text[i]))
            {
                i++;
                digits++;
            }
            if (digits is 0 or > 2)
                return false;
            while (i < this.text.Length && this.IsSpace(this.text[i]))
                i++;
            return (i < this.text.Length && this.text[i] == ':') || MeridiemAt(this.text, i, out _) != 0;
        }

        private int ReadNumber(out int digits)
        {
            var value = 0;
            digits = 0;
            while (!this.AtEnd && char.IsAsciiDigit(this.text[this.Position]))
            {
                if (digits < 9)
                    value = (value * 10) + (this.text[this.Position] - '0');
                this.Position++;
                digits++;
            }
            return value;
        }

        /// <summary>Consumes a separator with optional spaces around it, returning it or <c>'\0'</c>.</summary>
        private char ReadDateSeparator()
        {
            var start = this.Position;
            _ = this.SkipSpaces();
            if (this.Peek is '-' or '/' or '.')
            {
                var separator = this.Peek;
                this.Position++;
                _ = this.SkipSpaces();
                return separator;
            }
            this.Position = start;
            return '\0';
        }

        /// <summary>Reads an English month name, three-letter or in full, returning 1–12 or 0.</summary>
        private int ReadMonthName()
        {
            var start = this.Position;
            while (!this.AtEnd && char.IsAsciiLetter(this.text[this.Position]))
                this.Position++;
            var month = MonthNumber(this.text[start..this.Position]);
            if (month == 0)
                this.Position = start;
            return month;
        }

        public readonly bool StartsMonthName()
        {
            var i = this.Position;
            while (i < this.text.Length && char.IsAsciiLetter(this.text[i]))
                i++;
            return i > this.Position && MonthNumber(this.text[this.Position..i]) != 0;
        }

        /// <summary>
        /// Reads the date part. <paramref name="isoDashes"/> reports a year-first
        /// date written with dashes, the one form an ISO <c>T</c> or a
        /// <c>Z</c> may follow.
        /// </summary>
        public DateTimeTextError ParseDate(bool legacy, out DateOnly date, out bool isoDashes)
        {
            date = default;
            isoDashes = false;

            if (this.StartsMonthName())
                return this.ParseMonthFirst(legacy, out date);
            if (!this.StartsNumber())
                return DateTimeTextError.Syntax;

            var first = this.ReadNumber(out var firstDigits);
            var afterFirst = this.Position;
            _ = this.SkipSpaces();

            // A number then a month name: day-month-year or year-month-day.
            if (this.StartsMonthName())
            {
                var month = this.ReadMonthName();
                var afterMonth = this.Position;
                _ = this.SkipSpaces();
                if (this.Peek == ',')
                {
                    this.Position++;
                    _ = this.SkipSpaces();
                }
                if (!this.StartsNumber() || this.StartsTime())
                {
                    // A year and a month alone: the first of the month.
                    this.Position = afterMonth;
                    return firstDigits == 4 ? Build(first, month, 1, out date) : DateTimeTextError.Syntax;
                }
                var last = this.ReadNumber(out var lastDigits);
                return firstDigits == 4 && lastDigits <= 2 ? Build(first, month, last, out date)
                    : firstDigits <= 2 && lastDigits is not (3 or > 4) ? Build(Year(last, lastDigits), month, first, out date)
                    : DateTimeTextError.Syntax;
            }

            this.Position = afterFirst;
            var separator = this.ReadDateSeparator();
            if (separator == '\0')
            {
                // Unseparated: yyyymmdd, yymmdd, or a year alone.
                return firstDigits switch
                {
                    4 => Build(first, 1, 1, out date),
                    6 => Build(Year(first / 10000, 2), first / 100 % 100, first % 100, out date),
                    8 => Build(first / 10000, first / 100 % 100, first % 100, out date),
                    _ => DateTimeTextError.Syntax,
                };
            }

            // A number, a separator, then a month name: 5-Jan-2024 or 2024-Jan-05.
            if (this.StartsMonthName())
            {
                var month = this.ReadMonthName();
                var afterMonth = this.Position;
                if (this.ReadDateSeparator() != separator || !this.StartsNumber())
                {
                    // 2024-Jun, the first of the month to the legacy pair alone.
                    this.Position = afterMonth;
                    return legacy && firstDigits == 4 ? Build(first, month, 1, out date) : DateTimeTextError.Syntax;
                }
                var last = this.ReadNumber(out var lastDigits);
                return firstDigits == 4 && lastDigits <= 2 ? Build(first, month, last, out date)
                    : firstDigits <= 2 && lastDigits is not (3 or > 4) ? Build(Year(last, lastDigits), month, first, out date)
                    : DateTimeTextError.Syntax;
            }

            if (!this.StartsNumber())
                return DateTimeTextError.Syntax;
            var second = this.ReadNumber(out var secondDigits);
            var secondSeparator = this.ReadDateSeparator();
            if (secondSeparator == '\0' || !this.StartsNumber())
                return DateTimeTextError.Syntax;
            // The newer types want one separator throughout.
            if (secondSeparator != separator && !legacy)
                return DateTimeTextError.Syntax;
            var third = this.ReadNumber(out var thirdDigits);
            return this.OrderNumericDate(legacy, first, firstDigits, second, secondDigits, third, thirdDigits, separator == '-' && secondSeparator == '-', out date, out isoDashes);
        }

        /// <summary>
        /// Maps a three-part numeric date to year, month and day under the
        /// session's <c>SET DATEFORMAT</c> (<see cref="DateOrder.Current"/>),
        /// probed 2026-09-24 against SQL Server 2025 for all six orders:
        /// <list type="bullet">
        /// <item>A four-digit part is the year wherever it stands, and the other
        /// two follow the order's month / day sequence — except that the newer
        /// types read a year-first date as year-month-day under every order, as
        /// the legacy pair does when an ISO <c>T</c> time or a <c>Z</c> follows
        /// (so <c>'2024-11-12'</c> is 12 November to a <c>datetime</c> under
        /// <c>dmy</c>).</item>
        /// <item>The newer types take a year-last date only under <c>mdy</c>,
        /// <c>dmy</c> and <c>ymd</c>, a year-middle one only under <c>myd</c>
        /// and <c>dym</c>, and none of the two-digit-year forms under
        /// <c>ydm</c>.</item>
        /// <item>With no four-digit part the parts fill the order's positions.</item>
        /// </list>
        /// </summary>
        private readonly DateTimeTextError OrderNumericDate(
            bool legacy, int first, int firstDigits, int second, int secondDigits, int third, int thirdDigits, bool dashes, out DateOnly date, out bool isoDashes)
        {
            date = default;
            isoDashes = false;
            var order = DateOrder.Current;

            if (firstDigits == 4)
            {
                if (secondDigits > 2 || thirdDigits > 2)
                    return DateTimeTextError.Syntax;
                isoDashes = dashes;
                return !legacy || order.MonthBeforeDay || this.IsoSuffixFollows()
                    ? Build(first, second, third, out date)
                    : Build(first, third, second, out date);
            }
            if (thirdDigits == 4)
            {
                if (firstDigits > 2 || secondDigits > 2 || (!legacy && order != DateOrder.Mdy && order != DateOrder.Dmy && order != DateOrder.Ymd))
                    return DateTimeTextError.Syntax;
                return order.MonthBeforeDay ? Build(third, first, second, out date) : Build(third, second, first, out date);
            }
            if (secondDigits == 4)
            {
                if (firstDigits > 2 || thirdDigits > 2 || (!legacy && order.YearPosition != 1))
                    return DateTimeTextError.Syntax;
                return order.MonthBeforeDay ? Build(second, first, third, out date) : Build(second, third, first, out date);
            }
            if (firstDigits > 2 || secondDigits > 2 || thirdDigits > 2 || (!legacy && order == DateOrder.Ydm))
                return DateTimeTextError.Syntax;

            Span<int> parts = [first, second, third];
            Span<int> digits = [firstDigits, secondDigits, thirdDigits];
            var year = Year(parts[order.YearPosition], digits[order.YearPosition]);
            var earlier = order.YearPosition == 0 ? parts[1] : parts[0];
            var later = order.YearPosition == 2 ? parts[1] : parts[2];
            return order.MonthBeforeDay ? Build(year, earlier, later, out date) : Build(year, later, earlier, out date);
        }

        /// <summary>Whether an ISO <c>T</c> time or a <c>Z</c> follows directly.</summary>
        private readonly bool IsoSuffixFollows() =>
            (this.Peek is 'T' or 't' && this.DigitFollows()) || this.PeekZ();

        /// <summary>Jan 5 2024, Jan 5, 2024, Jan 5 24, January 2024.</summary>
        private DateTimeTextError ParseMonthFirst(bool legacy, out DateOnly date)
        {
            date = default;
            var month = this.ReadMonthName();
            if (this.Peek is '-' or '.')
            {
                // Jan-05-2024 and Jun. 15 2024 read to the legacy pair as a value
                // it can't place.
                return legacy ? DateTimeTextError.Range : DateTimeTextError.Syntax;
            }
            _ = this.SkipSpaces();
            if (!this.StartsNumber())
                return DateTimeTextError.Syntax;
            var first = this.ReadNumber(out var firstDigits);
            if (firstDigits == 4)
            {
                // Month then year, then perhaps the day: Jan 2024, Jun 2024 15.
                var afterYear = this.Position;
                _ = this.SkipSpaces();
                if (this.StartsNumber() && !this.StartsTime())
                {
                    var day = this.ReadNumber(out var dayDigits);
                    return dayDigits <= 2 ? Build(first, month, day, out date) : DateTimeTextError.Syntax;
                }
                this.Position = afterYear;
                return Build(first, month, 1, out date);
            }
            _ = this.SkipSpaces();
            if (this.Peek == ',')
            {
                this.Position++;
                _ = this.SkipSpaces();
            }
            if (!this.StartsNumber() || firstDigits > 2)
                return DateTimeTextError.Syntax;
            var year = this.ReadNumber(out var yearDigits);
            return yearDigits is 3 or > 4 ? DateTimeTextError.Syntax : Build(Year(year, yearDigits), month, first, out date);
        }

        /// <summary>
        /// Reads <c>h[:m[:s[.fraction | :milliseconds]]] [AM | PM]</c>, the hour
        /// alone only beside AM / PM; <paramref name="requireSeconds"/> for a
        /// time after an ISO <c>T</c>.
        /// </summary>
        public DateTimeTextError ParseTime(bool legacy, bool requireSeconds, out TimeValue time)
        {
            time = default;
            var hour = this.ReadNumber(out var hourDigits);
            if (hourDigits is 0 or > 2)
                return DateTimeTextError.Syntax;
            int minute = 0, second = 0;
            long fractionTicks = 0;
            var hasMinute = false;
            var hasSecond = false;
            var fractionOverflow = false;

            var afterHour = this.Position;
            _ = this.SkipSpaces();
            if (this.Peek == ':')
            {
                this.Position++;
                _ = this.SkipSpaces();
                if (!this.StartsNumber())
                    return DateTimeTextError.Syntax;
                minute = this.ReadNumber(out var minuteDigits);
                if (minuteDigits > 2)
                    return DateTimeTextError.Syntax;
                hasMinute = true;

                var afterMinute = this.Position;
                _ = this.SkipSpaces();
                if (this.Peek == ':')
                {
                    this.Position++;
                    _ = this.SkipSpaces();
                    if (!this.StartsNumber())
                        return DateTimeTextError.Syntax;
                    second = this.ReadNumber(out var secondDigits);
                    if (secondDigits > 2)
                        return DateTimeTextError.Syntax;
                    hasSecond = true;

                    if (this.Peek == '.')
                    {
                        this.Position++;
                        var fractionError = this.ReadFraction(legacy, out fractionTicks, out fractionOverflow);
                        if (fractionError != DateTimeTextError.None)
                            return fractionError;
                    }
                    else if (this.Peek == ':')
                    {
                        // Milliseconds after a colon: a count, not a fraction.
                        this.Position++;
                        var milliseconds = this.ReadNumber(out var millisecondDigits);
                        if (millisecondDigits is 0 or > 3)
                            return DateTimeTextError.Syntax;
                        fractionTicks = milliseconds * TimeSpan.TicksPerMillisecond;
                    }
                }
                else if (this.Position == afterMinute && this.Peek == '.')
                {
                    // A fraction straight after the minutes: the legacy pair
                    // reads it as the seconds' fraction.
                    if (!legacy)
                        return DateTimeTextError.Syntax;
                    this.Position++;
                    var fractionError = this.ReadFraction(legacy, out fractionTicks, out fractionOverflow);
                    if (fractionError != DateTimeTextError.None)
                        return fractionError;
                }
                else
                {
                    this.Position = afterMinute;
                }
            }
            else
            {
                this.Position = afterHour;
            }

            if (requireSeconds && !hasSecond)
                return DateTimeTextError.Syntax;

            var meridiemStart = this.Position;
            _ = this.SkipSpaces();
            var meridiem = MeridiemAt(this.text, this.Position, out var meridiemLength);
            if (meridiem != 0)
            {
                this.Position += meridiemLength;
            }
            else
            {
                this.Position = meridiemStart;
                if (!hasMinute)
                    return DateTimeTextError.Syntax;
            }

            // Past this point the string read; what's left is whether its values exist.
            if (minute > 59 || second > 59)
                return DateTimeTextError.Range;
            switch (meridiem)
            {
                case 'A':
                    if (hour > 12)
                        return DateTimeTextError.Range;
                    if (hour == 12)
                        hour = 0;
                    break;
                case 'P':
                    if (hour is 0 or > 23)
                        return DateTimeTextError.Range;
                    if (hour < 12)
                        hour += 12;
                    break;
                default:
                    if (hour > 23)
                        return DateTimeTextError.Range;
                    break;
            }

            var ticks = (hour * TimeSpan.TicksPerHour) + (minute * TimeSpan.TicksPerMinute) + (second * TimeSpan.TicksPerSecond) + fractionTicks;
            if (fractionOverflow)
                ticks++;
            time = new TimeValue(ticks);
            return DateTimeTextError.None;
        }

        /// <summary>
        /// Reads fractional-second digits into ticks. The legacy pair takes at
        /// most three; the newer types any number, rounding half up at the
        /// seventh (<paramref name="roundedUp"/> reports the extra tick).
        /// </summary>
        private DateTimeTextError ReadFraction(bool legacy, out long ticks, out bool roundedUp)
        {
            ticks = 0;
            roundedUp = false;
            var digits = 0;
            while (!this.AtEnd && char.IsAsciiDigit(this.text[this.Position]))
            {
                var digit = this.text[this.Position] - '0';
                if (digits < 7)
                    ticks = (ticks * 10) + digit;
                else if (digits == 7)
                    roundedUp = digit >= 5;
                digits++;
                this.Position++;
            }
            // The legacy pair takes at most three digits, the newer types nine.
            if (digits == 0 || digits > (legacy ? 3 : 9))
                return DateTimeTextError.Syntax;
            for (var i = Math.Min(digits, 7); i < 7; i++)
                ticks *= 10;
            return DateTimeTextError.None;
        }

        /// <summary>Reads <c>±h:mm</c>, at most fourteen hours either way.</summary>
        public DateTimeTextError ParseOffset(out TimeSpan offset)
        {
            offset = default;
            var negative = this.Peek == '-';
            this.Position++;
            _ = this.SkipSpaces();
            var hours = this.ReadNumber(out var hourDigits);
            if (hourDigits is 0 or > 2 || this.Peek != ':')
                return DateTimeTextError.Syntax;
            this.Position++;
            var minutes = this.ReadNumber(out var minuteDigits);
            if (minuteDigits != 2 || minutes > 59 || (hours * 60) + minutes > 14 * 60)
                return DateTimeTextError.Syntax;
            offset = new TimeSpan(negative ? -hours : hours, negative ? -minutes : minutes, 0);
            return DateTimeTextError.None;
        }
    }

    private static bool IsLetterAt(ReadOnlySpan<char> text, int index) =>
        index < text.Length && char.IsAsciiLetter(text[index]);

    /// <summary>Returns <c>'A'</c> or <c>'P'</c> for an AM / PM at <paramref name="index"/>, else 0.</summary>
    private static char MeridiemAt(ReadOnlySpan<char> text, int index, out int length)
    {
        length = 2;
        if (index + 1 >= text.Length || text[index + 1] is not ('M' or 'm') || IsLetterAt(text, index + 2))
            return '\0';
        return text[index] switch
        {
            'A' or 'a' => 'A',
            'P' or 'p' => 'P',
            _ => '\0',
        };
    }

    private static readonly string[] MonthNames =
    [
        "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE",
        "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER",
    ];

    /// <summary>1–12 for an English month name, three-letter or in full (case aside); 0 otherwise.</summary>
    private static int MonthNumber(ReadOnlySpan<char> word)
    {
        for (var i = 0; i < MonthNames.Length; i++)
        {
            var name = MonthNames[i].AsSpan();
            if ((word.Length == 3 || word.Length == name.Length) && word.Equals(name[..word.Length], StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return 0;
    }

    /// <summary>A written year: one or two digits pivot at 50 (49 is 2049, 50 is 1950).</summary>
    private static int Year(int value, int digits) =>
        digits > 2 ? value : value < 50 ? 2000 + value : 1900 + value;

    private static DateTimeTextError Build(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return DateTimeTextError.Range;
        date = new DateOnly(year, month, day);
        return DateTimeTextError.None;
    }
}
