using System.Collections.Frozen;
using System.Globalization;

namespace SqlServerSimulator.Parser.FullText;

// The data sets and shape readers the word breaker consults. The lists were
// read off sys.dm_fts_parser's output for LCID 1033 on SQL Server 2025
// (probed 2026-09-29) by sweeping candidate inputs — see
// docs/claude/full-text.md for the sweeps.
internal static partial class FullTextWordBreaker
{
    /// <summary>
    /// The tokens real keeps whole although their punctuation would break
    /// them: <c>c#</c> and <c>j#</c>, <c>c++</c> and <c>j++</c>, <c>.net</c>,
    /// two units and two exclamations — while <c>f#</c>, <c>g++</c> and every
    /// other letter break. A sweep of every one- to three-letter word and a
    /// list of common words, each followed by <c>!</c>, found only the two
    /// exclamations.
    /// </summary>
    private static class Lexicon
    {
        private static readonly string[] Entries = ["c#", "c++", "j#", "j++", ".net", "km/h", "m/s", "it!", "yahoo!"];

        /// <summary>The length of the entry at <paramref name="start"/>, or 0.</summary>
        public static int Match(string s, int start)
        {
            foreach (var entry in Entries)
            {
                if (start + entry.Length > s.Length
                    || string.Compare(s, start, entry, 0, entry.Length, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    continue;
                }
                var end = start + entry.Length;
                if (!entry.EndsWith('#') && !entry.EndsWith('+'))
                {
                    if (end < s.Length && IsWordChar(s[end]))
                        continue;
                    return entry.Length;
                }
                // Repeating the final symbol (`c##`, `c+++`) still reads as the
                // entry; a letter or digit after it does not (`c#s`, `c++11`).
                while (end < s.Length && s[end] == entry[^1])
                    end++;
                if (end < s.Length && IsWordChar(s[end]))
                    continue;
                return entry.Length;
            }
            return 0;
        }
    }

    /// <summary>
    /// The emoticons real keeps as terms: an eye (<c>:</c>, <c>;</c> or
    /// <c>%</c>), an optional nose (<c>-</c> or <c>^</c>) and a mouth
    /// (<c>)</c>, <c>(</c>, <c>/</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c>, or
    /// <c>D</c> after a nose) — the matrix a probe over every pairing found.
    /// </summary>
    private static class Emoticon
    {
        public static int Match(string s, int index)
        {
            if (index + 1 >= s.Length || s[index] is not (':' or ';' or '%'))
                return 0;
            var next = index + 1;
            var nose = s[next] is '-' or '^';
            if (nose)
                next++;
            if (next >= s.Length)
                return 0;
            var mouth = s[next] is ')' or '(' or '/' or '|' or '<' or '>' || (nose && s[next] == 'D');
            return mouth && (next + 1 == s.Length || !IsWordChar(s[next + 1])) ? next + 1 - index : 0;
        }
    }

    /// <summary>
    /// Words after which a period closes no sentence, matched case-sensitively
    /// as real does (<c>Mr.</c> and <c>mr.</c> differ). The set is what real
    /// reported for a sweep of every one- to three-letter word in lower,
    /// capitalized and upper case, the English stopwords, and a few hundred
    /// longer abbreviations and common words — so a longer abbreviation
    /// outside the sweep still ends a sentence here.
    /// </summary>
    private static class Abbreviations
    {
        private static readonly FrozenSet<string> Entries = (
            "A ab Abb abb Abbr abbr Abl abl Abp abp Abr abr Abs abs Abt abt ac Acc acc Ack ack Adj adj Adm adm Adv adv Afg Afr " +
            "Agt agt al Ala Alb Alc alc Ald Alg alg Alt alt Am Amt amt an Anc anc And and Ang Ann ann Anon anon Ans ans Ant Aor " +
            "aor ap App app Approx approx Appt appt Apr Apt apt aq Ar ar Arg arg Ariz Ark Arm Arr arr As Asg asg Assn assn Asst " +
            "asst Atl Atm atm Att att Attn attn Aud aud Aug aug Aus Aux aux Av av Ave ave Avg avg Avn avn az B Bab Bal bal Bap " +
            "Bbl bbl bd Bds bds Bef bef Benj bg Bhd bhd Bhn Bhu Bib bk Bkg bkg Bks bks bl Bld bld Bldg bldg Blk blk Blvd blvd bm " +
            "Bn bn Bol Bor bor Bot bot bp Br br Brig Bro bro Bros bros Bsh bsh Bsk bsk Bt bu Bul bul Bur bur Bvt bvt bx C c ca " +
            "Cal cal Calif Can Capt Card Cav cav cc Ccw ccw Cdr Cen cen Cert cert Cet cet Cf cf Ch ch Chap chap Chas Chg chg Chi " +
            "Chl chl Chm chm Chr Cia Cie cie Cir cir Cit cit Civ civ ck cl Cld cld Clk clk Clm clm Cm cm Cmd cmd Cmdr Cml cml Co " +
            "co Col col Colo Com com Con Cong cong Conn Cont cont Cop cop Cor cor Corp corp Corr corr Cos cos cp Cpd cpd Cpl Cpt " +
            "cr cs Csk csk Ct ct Ctf ctf Ctg ctg Ctn ctn Ctr ctr cu Cum cum cv Cvt cvt cw Cwt cwt D Da Dat dat Dbl dbl dd Deb deb " +
            "Dec dec Def def Deg deg Del del Dem dem Den Dep dep Dept dept Der der Det det Dev dev Dft dft Dia dia Dif dif Dil " +
            "dil Dir dir Dis dis Dist dist Div div dk Dlr dlr dm dn Doc doc Dol dol Dom dom Dor Doz doz Dpt dpt Dr dr Drs Dt Du " +
            "Dup dup Dwt dwt dy dz E ea Ec ed Eds eds Educ educ Edw Eff eff el Elev elev Emp emp Enc enc Encl encl Eng eng Enl " +
            "enl Ens Ep Eph eq Eqn eqn Equiv equiv Esk Esp esp Esq esq Est est Etc etc Eth Eur Evan evan Evg evg Ex ex Exc exc " +
            "Excl excl Exp exp Exr exr Ext ext F Fac fac Fam fam Far Fcp fcp Fcy fcy Feb Fec fec Fem fem ff Fgt fgt Fig fig Figs " +
            "figs Fin fl Fla Fld fld fm fn Fol fol fp Fr fr Fri Frl Frs Frt frt Ft ft Fth fth Fut fut Fwd fwd G Ga ga Gal Gaz gaz " +
            "gd Gds gds Gen gen Geo Ger ger Gib Gk gl Gld gld gm Gn Gnd gnd Gov gov Govt govt Gr gr Gro gro Gt gt Gtd gtd Gtt gtt " +
            "Gyn gyn H Hab hab Hag hd Heb hf Hgb hgb Hgt hgt Hld hld hm ho Hon Hor hor Hos Hosp hosp hp Hr hr ht Hts Hwy hwy Hyd " +
            "hyd I Ia ib Ibid ibid Ice Id Ign ign Ill Illus illus in Inc inc Incl incl Ind ind Inf inf Inj inj Inq inq Ins ins " +
            "Inst inst Int int Inter inter Intl intl Inv inv Ion Ir Ire Is Isl isl Isr It J Jam Jan Jas Jav Jb Jct jct Jdt Jer Jg " +
            "Jl Jm Jn Jnr jnr Jnt jnt Jos Jr jr jt Jul Jun jun Juv juv K Kan kc Km km Kmh kmh kn Knt Kor kr kt Ky L La Lab Lam " +
            "Lat lat Lav lav lb Lbs lbs Ld ld Ldg ldg Lea lea Leb Lev lg Lge lge li Lib Lim lim Lin lin Liq liq ll Loc loc Loq " +
            "loq Lt lt Ltd ltd Lux lv Lyr lyr M Mad Mag mag Maj Mal Man Mar Mass mc Md Mdm Me Med med Mer mer Messrs Metall " +
            "metall Mex Mfd mfd Mfg mfg Mfr mfr mg Mgmt mgmt Mgr mgr Mgt mgt Mhz mhz mi Mich Mid mid Min min Minn Misc misc Miss " +
            "mk Mkt mkt ml Mlle MM mm Mme Mo mo Mol mol Mon mon Mont Mor Mos mos Moz Mph mph Mr Mrs MS Ms Msg msg Msgr MSS Mss Mt " +
            "mt Mtg mtg Mtn mtn Mts mts Mus mus Mxd mxd Myc myc N Na Nat nat Natl natl Nav nav Nb Ne ne Neb Neg neg Neh Nep Nev " +
            "Nic Nig No no Nol nol Nom nom Nor Nos nos Nov nt Num num nw O Ob ob Obj obj Obl obl Obs obs Oc oc Occ occ Oct oct " +
            "Okla OM Ont Op op Opp opp Opt opt Ord ord Ore Org org Orig orig oz P Pa Pac Pak Pal Pam pam Par par Pct pct pd Per " +
            "per Pet pf PFC Pfc Pfd pfd Pfg pfg Pg pg ph Phil phil Phr phr pk Pkg pkg Pkt pkt Pl pl Plf plf Pln pln Pls pls Plu " +
            "plu pm Pmk pmk Pmt pmt Pol pol Por por Pos pos Poss poss PP pp Ppd ppd Pph pph Ppt ppt Pr pr Pref pref Pres pres Prf " +
            "prf Prof prof ps Psf psf pt Pta pta Ptg ptg Pty pty Pvt Pwr pwr Pwt pwt Pxt pxt Q ql qn qq qr Qrs qrs qt Qto qto Qtr " +
            "qtr Qty qty qu Que R Ra Rad rad Rct rct Rd rd Re Rec rec Recd recd Ref ref Reg reg Rel rel Rem rem Rep rep Req req " +
            "Res res Resp resp Ret ret Rev rev rf Rit rit Riv riv Rm rm Rms rms Rnd rnd ro Rom rom Rpt rpt Rt rt Rte rte Rul rul " +
            "Rus Rv Rwy rwy ry S Sab Sat Sax sb Sc sc Sch sch Sci sci Scr scr sd se Sec sec Sed sed Sel sel Sem sem Sen sen Sep " +
            "sep Sept Seq seq Ser ser sf Sfz sfz Sgd sgd Sgt sh Shr shr Sht sht Sib Sic Sig sig sk Skr Skt sl Sld sld sm Smt Soc " +
            "soc Sol sol Som Sou sou Sow Sp sp Spec spec Spp spp Spr spr Spt spt sq Sr sr Sra Srta ss Ssp ssp St st Sta sta Std " +
            "std Ste Stg stg Stk stk Str str Subj subj Suf suf Sun Supp supp Supt supt Sur sur Sw sw Swe Syl syl Sym sym Syn syn " +
            "Syr T Tab tab Tas Tbs tbs Tbsp tbsp Tec tec Tel tel Tenn Ter ter Terr terr Tex Tfr tfr Tgt tgt Th Thos Thur Thurs " +
            "Tit tk Tkt tkt Tlr tlr tn Tng tng tp Tpk tpk tr Trans trans Trp trp Tsp tsp Tu Tues Tun Tur Twp twp Typ typ U Ult " +
            "ult Unb unb Uni Univ univ Unp unp Uns uns Usu usu Ut ux V Va Vac vac Val val Var var Vat vb Vbs vbs Veg veg Vel vel " +
            "Ven Ver ver Vert vert Vic vic Vil vil Vis vis Viz viz vo Voc voc Vol vol Vols vols Vou vou vs Vss vss Vt Vul vv W " +
            "Wash wd Wed wh Whf whf Whs whs Wid wid Wis wk Wm wm Wmk wmk Wpn wpn wt Wyo X Y yd Yel yel Yeo yeo yr Yrs yrs Yug Z").Split(' ').ToFrozenSet(StringComparer.Ordinal);

        public static bool Contains(string word) => Entries.Contains(word);
    }

    /// <summary>
    /// The dotted abbreviations real keeps as one term without their final
    /// period: <c>e.g.</c> → <c>e.g</c>, where a run of single letters such as
    /// <c>u.s.a.</c> becomes the acronym pair instead.
    /// </summary>
    private static class DottedAbbreviations
    {
        private static readonly string[] Entries = ["e.g.", "i.e.", "ph.d."];

        public static bool TryGet(string s, int start, out int length, out string canonical)
        {
            foreach (var entry in Entries)
            {
                if (start + entry.Length <= s.Length
                    && string.Compare(s, start, entry, 0, entry.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (start + entry.Length == s.Length || !IsWordChar(s[start + entry.Length])))
                {
                    length = entry.Length;
                    canonical = s.Substring(start, entry.Length - 1);
                    return true;
                }
            }
            length = 0;
            canonical = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// When a dotted chain of words is one compound (<c>file.txt</c>,
    /// <c>www.example.com</c>): real keeps the chain whole when its last
    /// segment reads as a file extension or a top-level domain.
    /// </summary>
    private static class Domains
    {
        /// <summary>
        /// Extensions that make a compound after any first segment: any
        /// three-character segment starting with a letter, plus these.
        /// </summary>
        private static readonly FrozenSet<string> Extensions = new[]
        {
            "asmx", "ashx", "aspx", "csproj", "dd", "docx", "dotx", "ds", "fx", "gz", "html", "ht", "java", "jpeg", "js", "mpeg",
            "php3", "pl", "pm", "pptx", "ps", "qt", "rm", "sh", "shtml", "tiff", "ub", "vm", "xlsx",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Top-level domains, which make a compound only after a segment of
        /// three characters or more (<c>abc.de</c> is one, <c>ab.de</c> is two
        /// words).
        /// </summary>
        private static readonly FrozenSet<string> TopLevelDomains = new[]
        {
            "ac", "ad", "ae", "af", "ag", "ai", "al", "am", "an", "ao", "ap", "aq", "ar", "as", "at", "au", "aw", "ax", "az", "ba", "bb",
            "bd", "be", "bf", "bg", "bh", "bi", "bj", "bl", "bm", "bn", "bo", "br", "bs", "bt", "bu", "bv", "bw", "bx", "by", "bz", "ca",
            "cc", "cd", "cf", "cg", "ch", "ci", "ck", "cl", "cm", "cn", "co", "cp", "cr", "cs", "cu", "cv", "cx", "cy", "cz", "dd", "de",
            "dg", "dj", "dk", "dm", "do", "ds", "dy", "dz", "ea", "ec", "ee", "ef", "eg", "eh", "em", "ep", "er", "es", "et", "eu", "ev",
            "ew", "fi", "fj", "fk", "fl", "fm", "fo", "fr", "fx", "ga", "gb", "gc", "gd", "ge", "gf", "gg", "gh", "gi", "gl", "gm", "gn",
            "gp", "gq", "gr", "gs", "gt", "gu", "gw", "gy", "gz", "hk", "hm", "hn", "hr", "ht", "hu", "ib", "ic", "id", "ie", "il", "im",
            "in", "io", "iq", "ir", "is", "it", "ja", "je", "jm", "jo", "jp", "js", "ke", "kg", "kh", "ki", "km", "kn", "kp", "kr", "kw",
            "ky", "kz", "la", "lb", "lc", "lf", "li", "lk", "lr", "ls", "lt", "lu", "lv", "ly", "ma", "mc", "md", "me", "mf", "mg", "mh",
            "mk", "ml", "mm", "mn", "mo", "mp", "mq", "mr", "ms", "mt", "mu", "mv", "mw", "mx", "my", "mz", "na", "nc", "ne", "nf", "ng",
            "ni", "nl", "no", "np", "nr", "nt", "nu", "nz", "oa", "om", "pa", "pe", "pf", "pg", "ph", "pi", "pk", "pl", "pm", "pn", "pr",
            "ps", "pt", "pw", "py", "qa", "qt", "ra", "rb", "rc", "re", "rh", "ri", "rl", "rm", "rn", "ro", "rp", "rs", "ru", "rw", "sa",
            "sb", "sc", "sd", "se", "sf", "sg", "sh", "si", "sj", "sk", "sl", "sm", "sn", "so", "sr", "st", "su", "sv", "sy", "sz", "ta",
            "tc", "td", "tf", "tg", "th", "tj", "tk", "tl", "tm", "tn", "to", "tp", "tr", "tt", "tv", "tw", "tz", "ua", "ub", "ug", "uk",
            "um", "us", "uy", "uz", "va", "vc", "ve", "vg", "vi", "vm", "vn", "vu", "wf", "wg", "wl", "wo", "ws", "wv", "ye", "yt", "yu",
            "yv", "za", "zm", "zr", "zw",
            "aero", "coop", "info", "museum", "name", "post", "travel",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How many leading segments form the compound — the longest prefix
        /// whose last segment qualifies — or 0 when none does.
        /// </summary>
        public static int CompoundLength(List<string> segments)
        {
            for (var keep = segments.Count; keep >= 2; keep--)
            {
                if (Qualifies(segments[keep - 1], segments[keep - 2]))
                    return keep;
            }
            return 0;
        }

        private static bool Qualifies(string segment, string previous)
        {
            if (segment.Length == 3 && char.IsAsciiLetter(segment[0]) && char.IsAsciiLetterOrDigit(segment[1]) && char.IsAsciiLetterOrDigit(segment[2]))
                return true;
            if (Extensions.Contains(segment))
                return true;
            // A second-level label such as `co` in `example.co.uk` passes the
            // length test on the label before it.
            return TopLevelDomains.Contains(segment) && (previous.Length >= 3 || previous is "co" or "ac" or "or" or "ne" or "go");
        }
    }

    /// <summary>
    /// Currencies an amount carries: symbols on either side (<c>$5</c>,
    /// <c>5€</c>), optionally spaced from it (<c>$ 5</c>), and letter codes —
    /// ISO 4217, matched in upper case only (<c>USD5</c>; <c>usd5</c> is a
    /// plain word) — plus the handful of local spellings real knows. The
    /// normalized companion ends in the currency, lower-cased
    /// (<c>nn5$</c>, <c>nn5usd</c>).
    /// </summary>
    private static class Currency
    {
        /// <summary>Non-letter symbols, and the letter-led ones spelled with one.</summary>
        private static readonly string[] Symbols = ["R$", "C$", "$", "€", "£", "¥", "₩", "¢", "₪", "฿", "₫", "₦", "₡"];

        private static readonly FrozenSet<string> Codes = new[]
        {
            // ISO 4217 and the pre-euro codes: each of the sixty probed read
            // as a currency (and made-up ones such as ABC did not), so the
            // rest of the standard list is taken on that evidence.
            "AED", "AFN", "ALL", "AMD", "ANG", "AOA", "ARS", "ATS", "AUD", "AWG", "AZN", "BAM", "BBD", "BDT", "BEF", "BGN", "BHD",
            "BIF", "BMD", "BND", "BOB", "BRL", "BSD", "BTN", "BWP", "BYN", "BYR", "BZD", "CAD", "CDF", "CHF", "CLP", "CNY", "COP",
            "CRC", "CUP", "CVE", "CYP", "CZK", "DEM", "DJF", "DKK", "DOP", "DZD", "EEK", "EGP", "ERN", "ESP", "ETB", "EUR", "FIM",
            "FJD", "FKP", "FRF", "GBP", "GEL", "GHS", "GIP", "GMD", "GNF", "GRD", "GTQ", "GYD", "HKD", "HNL", "HRK", "HTG", "HUF",
            "IDR", "IEP", "ILS", "INR", "IQD", "IRR", "ISK", "ITL", "JMD", "JOD", "JPY", "KES", "KGS", "KHR", "KMF", "KPW", "KRW",
            "KWD", "KYD", "KZT", "LAK", "LBP", "LKR", "LRD", "LSL", "LTL", "LUF", "LVL", "LYD", "MAD", "MDL", "MGA", "MKD", "MMK",
            "MNT", "MOP", "MRU", "MTL", "MUR", "MVR", "MWK", "MXN", "MYR", "MZN", "NAD", "NGN", "NIO", "NLG", "NOK", "NPR", "NZD",
            "OMR", "PAB", "PEN", "PGK", "PHP", "PKR", "PLN", "PTE", "PYG", "QAR", "RON", "RSD", "RUB", "RWF", "SAR", "SBD", "SCR",
            "SDG", "SEK", "SGD", "SHP", "SIT", "SKK", "SLL", "SOS", "SRD", "STN", "SVC", "SYP", "SZL", "THB", "TJS", "TMT", "TND",
            "TOP", "TRY", "TTD", "TWD", "TZS", "UAH", "UGX", "USD", "UYU", "UZS", "VES", "VND", "VUV", "WST", "XAF", "XAG", "XAU",
            "XCD", "XDR", "XOF", "XPF", "YER", "ZAR", "ZMW", "ZWL",
            // Local spellings and the single capitals real reads as currency.
            "DM", "Ft", "Kč", "L", "P", "Q", "R", "S", "kr", "lei", "zł", "ƒ", "лв",
        }.ToFrozenSet(StringComparer.Ordinal);

        /// <summary>A symbol or code at <paramref name="index"/> ahead of an amount, or 0.</summary>
        public static int MatchSymbolPrefix(string s, int index)
        {
            foreach (var symbol in Symbols)
            {
                if (string.CompareOrdinal(s, index, symbol, 0, symbol.Length) == 0)
                {
                    if (char.IsLetter(symbol[0]) && index > 0 && IsWordChar(s[index - 1]))
                        continue;
                    return symbol.Length;
                }
            }
            if (index > 0 && IsWordChar(s[index - 1]))
                return 0;
            var end = index;
            while (end < s.Length && char.IsLetter(s[end]))
                end++;
            return end > index && end < s.Length && IsAsciiDigit(s[end]) && Codes.Contains(s[index..end]) ? end - index : 0;
        }

        /// <summary>A symbol or code at <paramref name="index"/> after an amount, or 0.</summary>
        public static int MatchSymbolSuffix(string s, int index)
        {
            foreach (var symbol in Symbols)
            {
                if (string.CompareOrdinal(s, index, symbol, 0, symbol.Length) == 0)
                    return symbol.Length;
            }
            var end = index;
            while (end < s.Length && char.IsLetter(s[end]))
                end++;
            return end > index && (end == s.Length || !IsWordChar(s[end])) && Codes.Contains(s[index..end]) ? end - index : 0;
        }

        /// <summary>A whole chunk that is a currency written apart from its amount.</summary>
        public static bool IsSpacedPrefix(string chunk)
        {
            if (Array.IndexOf(Symbols, chunk) >= 0)
                return true;
            // A lone capital (`R 5`) stays a letter.
            return chunk.Length > 1 && Codes.Contains(chunk);
        }

#pragma warning disable CA1308 // real's normalized forms are lower case
        public static string Suffix(string currency) => currency.ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>
    /// A number as real reads it: digits, optionally grouped by commas in
    /// threes, optionally with one decimal point.
    /// </summary>
    private readonly struct NumberShape(int length, string normalized, bool isDecorated)
    {
        public readonly int Length = length;

        /// <summary>The <c>nn</c> companion's body: <c>1,000.50</c> → <c>1000d5</c>.</summary>
        public readonly string Normalized = normalized;

        /// <summary>True when grouping or a decimal point was read.</summary>
        public readonly bool IsDecorated = isDecorated;

        public static bool TryRead(string s, int index, out NumberShape number, bool allowGrouping, bool allowDecimal)
        {
            number = default;
            var start = index;
            while (index < s.Length && IsAsciiDigit(s[index]))
                index++;
            var integerLength = index - start;
            if (integerLength == 0)
                return false;
            var integerDigits = s[start..index];
            var decorated = false;

            // Grouping: a lead of one to three digits, then groups of exactly three.
            if (allowGrouping && integerLength <= 3)
            {
                while (index + 3 < s.Length && s[index] == ',' && IsAsciiDigit(s[index + 1]) && IsAsciiDigit(s[index + 2]) && IsAsciiDigit(s[index + 3])
                    && (index + 4 >= s.Length || !IsAsciiDigit(s[index + 4])))
                {
                    integerDigits += s.Substring(index + 1, 3);
                    index += 4;
                    decorated = true;
                }
            }

            // One decimal point, unless the run is one of several dotted
            // digit groups (`1.2.3`, an address), which break apart.
            var fraction = string.Empty;
            var precededByDigitsDot = start >= 2 && s[start - 1] == '.' && RunBeforeIsDigits(s, start - 1);
            if (allowDecimal && !precededByDigitsDot && index + 1 < s.Length && s[index] == '.' && IsAsciiDigit(s[index + 1]))
            {
                var fractionStart = index + 1;
                var fractionEnd = fractionStart;
                while (fractionEnd < s.Length && IsAsciiDigit(s[fractionEnd]))
                    fractionEnd++;
                var anotherGroup = fractionEnd + 1 < s.Length && s[fractionEnd] == '.' && IsAsciiDigit(s[fractionEnd + 1]);
                if (!anotherGroup)
                {
                    fraction = s[fractionStart..fractionEnd];
                    index = fractionEnd;
                    decorated = true;
                }
            }

            var normalized = NormalizeInteger(integerDigits);
            var trimmedFraction = fraction.TrimEnd('0');
            if (trimmedFraction.Length > 0)
                normalized += "d" + trimmedFraction;
            number = new NumberShape(index - start, normalized, decorated);
            return true;
        }

        private static bool RunBeforeIsDigits(string s, int dot)
        {
            var i = dot - 1;
            while (i >= 0 && IsWordChar(s[i]))
            {
                if (!IsAsciiDigit(s[i]))
                    return false;
                i--;
            }
            return i < dot - 1;
        }

        /// <summary>
        /// Drops leading zeros, keeping one for zero itself — except that real
        /// keeps <c>000</c>, a thousands group, whole.
        /// </summary>
        public static string NormalizeInteger(string digits)
        {
            if (digits == "000")
                return digits;
            var trimmed = digits.TrimStart('0');
            return trimmed.Length == 0 ? "0" : trimmed;
        }
    }

    /// <summary>
    /// A clock time: <c>h:mm</c>, <c>h:mm:ss</c> or <c>hhmm</c> with an
    /// <c>h</c> (<c>10h30</c>), optionally followed straight away by a
    /// meridiem (<c>10:30am</c>).
    /// </summary>
    private readonly struct TimeShape(int consumed, int hour, int minute, int second, bool hasMinutes, bool hasMeridiem, bool isPm)
    {
        public readonly int Consumed = consumed;
        public readonly int Hour = hour;
        public readonly int Minute = minute;
        public readonly int Second = second;
        public readonly bool HasMinutes = hasMinutes;
        public readonly bool HasMeridiem = hasMeridiem;
        public readonly bool IsPm = isPm;

        public static bool TryParse(ReadOnlySpan<char> s, out TimeShape time, bool allowSeconds)
        {
            time = default;
            var index = 0;
            while (index < s.Length && IsAsciiDigit(s[index]))
                index++;
            if (index is 0 or > 2)
                return false;
            var hour = int.Parse(s[..index], CultureInfo.InvariantCulture);
            if (hour > 24)
                return false;
            int minute = 0, second = 0;
            var hasMinutes = false;
            if (index + 2 < s.Length && s[index] is ':' or 'h' && IsAsciiDigit(s[index + 1]) && IsAsciiDigit(s[index + 2])
                && (index + 3 >= s.Length || !IsAsciiDigit(s[index + 3])))
            {
                var separator = s[index];
                minute = int.Parse(s.Slice(index + 1, 2), CultureInfo.InvariantCulture);
                if (minute > 59)
                    return false;
                index += 3;
                hasMinutes = true;
                if (separator == ':' && allowSeconds && index + 2 < s.Length && s[index] == ':' && IsAsciiDigit(s[index + 1]) && IsAsciiDigit(s[index + 2])
                    && (index + 3 >= s.Length || !IsAsciiDigit(s[index + 3])))
                {
                    second = int.Parse(s.Slice(index + 1, 2), CultureInfo.InvariantCulture);
                    if (second > 59)
                        return false;
                    index += 3;
                }
            }
            var meridiemLength = Meridiem.Match(s[index..].ToString(), out var pm, out var oclock);
            var hasMeridiem = meridiemLength > 0 && !oclock && hasMinutes;
            if (hasMeridiem)
                index += meridiemLength;
            time = new TimeShape(index, hour, minute, second, hasMinutes, hasMeridiem, pm);
            return true;
        }

        /// <summary><c>10am</c>, <c>5PM</c>: an hour and a meridiem written as one word.</summary>
        public static bool TryHourMeridiem(string word, out int hour, out bool pm)
        {
            hour = 0;
            pm = false;
            var digits = 0;
            while (digits < word.Length && IsAsciiDigit(word[digits]))
                digits++;
            if (digits is 0 or > 2 || word.Length != digits + 2)
                return false;
            var suffix = word.AsSpan(digits);
            if (suffix.Equals("am", StringComparison.OrdinalIgnoreCase))
                pm = false;
            else if (suffix.Equals("pm", StringComparison.OrdinalIgnoreCase))
                pm = true;
            else
                return false;
            hour = int.Parse(word.AsSpan(0, digits), CultureInfo.InvariantCulture);
            return hour <= 24;
        }

        public static int To24(int hour, bool pm) =>
            pm ? (hour < 12 ? hour + 12 : hour) : (hour == 12 ? 0 : hour % 24);

        public static string Format(int hour, int minute, int second) =>
            string.Create(CultureInfo.InvariantCulture, $"tt24{hour:00}{minute:00}{second:00}");
    }

    /// <summary><c>am</c> / <c>pm</c> in their spellings, and <c>o'clock</c>.</summary>
    private static class Meridiem
    {
        private static readonly string[] AmForms = ["a.m.", "am"];
        private static readonly string[] PmForms = ["p.m.", "pm"];

        public static int Match(string s, out bool pm, out bool oclock)
        {
            pm = false;
            oclock = false;
            foreach (var form in AmForms)
            {
                if (s.StartsWith(form, StringComparison.OrdinalIgnoreCase) && Ends(s, form.Length))
                    return form.Length;
            }
            foreach (var form in PmForms)
            {
                if (s.StartsWith(form, StringComparison.OrdinalIgnoreCase) && Ends(s, form.Length))
                {
                    pm = true;
                    return form.Length;
                }
            }
            if (s.StartsWith("o'clock", StringComparison.OrdinalIgnoreCase) && Ends(s, 7))
            {
                oclock = true;
                return 7;
            }
            return 0;
        }

        private static bool Ends(string s, int length) => length == s.Length || !IsWordChar(s[length]);
    }

    /// <summary>Capitalized month names and their abbreviations, with or without a period.</summary>
    private static class MonthNames
    {
        private static readonly string[] Full =
        [
            "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December",
        ];

        public static bool TryGet(string word, out int month)
        {
            var bare = word.EndsWith('.') ? word[..^1] : word;
            for (var i = 0; i < Full.Length; i++)
            {
                if (string.Equals(bare, Full[i], StringComparison.Ordinal)
                    || (bare.Length == 3 && Full[i].StartsWith(bare, StringComparison.Ordinal)))
                {
                    month = i + 1;
                    return true;
                }
            }
            month = bare is "Sept" ? 9 : 0;
            return month != 0;
        }
    }

    /// <summary>
    /// A numeric date, read the way real reads it: a four-digit year first is
    /// year-month-day (year-day-month with periods), a four-digit year last is
    /// day-month-year, and the two fields swap when the one read as the month
    /// is over 12. Years run 1000 to 2999; a two-digit year yields both
    /// centuries.
    /// </summary>
    private readonly struct DateShape(int length, int[] years, int month, int day, string[] parts)
    {
        public readonly int Length = length;
        public readonly int[] Years = years;
        public readonly int Month = month;
        public readonly int Day = day;
        public readonly string[] Parts = parts;

        public static bool TryParse(string s, int start, out DateShape date)
        {
            date = default;
            var fields = new string[3];
            var index = start;
            var separator = '\0';
            for (var f = 0; f < 3; f++)
            {
                var fieldStart = index;
                while (index < s.Length && IsAsciiDigit(s[index]))
                    index++;
                if (index == fieldStart || index - fieldStart > 4)
                    return false;
                fields[f] = s[fieldStart..index];
                if (f == 2)
                    break;
                if (index >= s.Length || s[index] is not ('-' or '/' or '.'))
                    return false;
                if (f == 0)
                    separator = s[index];
                else if (s[index] != separator)
                    return false;
                index++;
            }
            if (index < s.Length && (IsWordChar(s[index]) || (s[index] == separator && index + 1 < s.Length && IsAsciiDigit(s[index + 1]))))
                return false;

            if (!Interpret(fields, separator, out var years, out var month, out var day))
                return false;
            date = new DateShape(index - start, years, month, day, fields);
            return true;
        }

        private static bool Interpret(string[] f, char separator, out int[] years, out int month, out int day)
        {
            years = [];
            month = day = 0;
            var a = int.Parse(f[0], CultureInfo.InvariantCulture);
            var b = int.Parse(f[1], CultureInfo.InvariantCulture);
            var c = int.Parse(f[2], CultureInfo.InvariantCulture);
            if (f[0].Length == 4)
            {
                if (f[1].Length > 2 || f[2].Length > 2 || !TryYear(f[0], out years))
                    return false;
                return separator == '.' ? Valid(c, b, out month, out day) : Valid(b, c, out month, out day) || Valid(c, b, out month, out day);
            }
            if (f[0].Length > 2 || f[1].Length > 2)
                return false;
            if (f[2].Length == 4)
                return TryYear(f[2], out years) && (Valid(b, a, out month, out day) || Valid(a, b, out month, out day));
            if (f[2].Length == 2 && TryYear(f[2], out years) && (Valid(b, a, out month, out day) || Valid(a, b, out month, out day)))
                return true;
            return separator == '-' && f[0].Length == 2 && f[2].Length <= 2 && TryYear(f[0], out years) && Valid(b, c, out month, out day);
        }

        private static bool Valid(int candidateMonth, int candidateDay, out int month, out int day)
        {
            month = candidateMonth;
            day = candidateDay;
            return month is >= 1 and <= 12 && day is >= 1 and <= 31;
        }

        public static bool TryYear(string text, out int[] years)
        {
            years = [];
            if (!AllDigits(text))
                return false;
            var value = int.Parse(text, CultureInfo.InvariantCulture);
            if (text.Length == 4 && value is >= 1000 and <= 2999)
            {
                years = [value];
                return true;
            }
            if (text.Length == 2)
            {
                years = [1900 + value, 2000 + value];
                return true;
            }
            return false;
        }

        public static string Format(int year, int month, int day) =>
            string.Create(CultureInfo.InvariantCulture, $"dd{year:0000}{month:00}{day:00}");
    }
}
