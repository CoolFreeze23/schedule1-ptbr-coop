using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MelonLoader;

namespace S1PortuguesBR
{
    /// <summary>Text table and lookup: exact, case-folded, templates, then per-segment.</summary>
    public static class Translator
    {
        private sealed class Template
        {
            public string Source;
            public string Target;
            public Regex Regex;
            public string[] Holes;
        }

        private static readonly Regex HolePattern = new Regex(@"\{\d+\}|<[A-Z][A-Z0-9_]*>", RegexOptions.Compiled);
        private static readonly Regex SegmentSplit = new Regex(@"(<[^>]+>|\r?\n)", RegexOptions.Compiled);
        private static readonly Regex Money = new Regex(@"(?<![\w$R])(-?)\$\s?(\d[\d,]*)(\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex Clock = new Regex(@"\b(\d{1,2}):(\d{2})\s?([AaPp])\.?[Mm]\.?\b", RegexOptions.Compiled);
        private static readonly Regex BareHour = new Regex(@"\b(\d{1,2})\s?([AaPp])[Mm]\b", RegexOptions.Compiled);
        private static readonly Regex Weekday = new Regex(@"^(.+) (Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)$", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Feet = new Regex(@"\b(\d+(?:\.\d+)?)\s?ft\b", RegexOptions.Compiled);
        private static readonly Regex Mph = new Regex(@"\b(\d+(?:\.\d+)?)\s?mph\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Fahrenheit = new Regex(@"(-?\d+(?:\.\d+)?)\s?°F\b", RegexOptions.Compiled);
        private static readonly Regex OnlyNumbers = new Regex(@"^[\s\d.,:;%+\-$xX/()#°]*$", RegexOptions.Compiled);
        private static readonly char[] TrimPunctuation = { ':', '.', '!', '?', ' ', '…' };

        private static readonly Dictionary<string, string> Exact = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Folded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Translated = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Template>> ByPrefix = new Dictionary<string, List<Template>>(StringComparer.Ordinal);
        private static readonly List<Template> Unprefixed = new List<Template>();
        private static readonly Dictionary<string, string> Memo = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);
        private const int MemoLimit = 40000;
        private const int PrefixLength = 3;

        public static bool Enabled { get; set; }
        public static bool LogMissing { get; set; }
        public static int EntryCount => Exact.Count;
        public static int TemplateCount { get; private set; }

        public static void Load()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pt-BR.json"))
            using (var doc = JsonDocument.Parse(s))
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    Add(p.Name, p.Value.GetString());

            foreach ((string en, string pt) in MixNames.AllPairs())
                if (!Exact.ContainsKey(en))
                {
                    Exact[en] = pt;
                    Translated.Add(pt);
                }

            string overridePath = Path.Combine(Mod.DataDir, "overrides.json");
            if (File.Exists(overridePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(overridePath));
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    Add(p.Name, p.Value.GetString());
            }
        }

        private static void Add(string en, string pt)
        {
            if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(pt))
                return;

            Exact[en] = pt;
            Folded[en] = pt;
            if (en != pt)
            {
                Translated.Add(pt);
                Reverse[pt] = en;
            }

            if (HolePattern.IsMatch(en))
                AddTemplate(en, pt);
        }

        private static void AddTemplate(string en, string pt)
        {
            // A template with almost no fixed text would swallow unrelated strings.
            string literal = HolePattern.Replace(en, "");
            int letters = literal.Count(char.IsLetter);
            if (letters < 4 || Regex.IsMatch(en, @"\}\{|>\{\d+\}\{"))
                return;

            var holes = new List<string>();
            var pattern = new StringBuilder("^");
            int last = 0;
            foreach (Match m in HolePattern.Matches(en))
            {
                pattern.Append(Regex.Escape(en.Substring(last, m.Index - last)));
                pattern.Append("(.+?)");
                holes.Add(m.Value);
                last = m.Index + m.Length;
            }
            pattern.Append(Regex.Escape(en.Substring(last))).Append('$');

            var t = new Template
            {
                Source = en,
                Target = pt,
                Holes = holes.ToArray(),
                Regex = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant)
            };

            int firstHole = HolePattern.Match(en).Index;
            if (firstHole >= PrefixLength)
            {
                string key = en.Substring(0, PrefixLength);
                if (!ByPrefix.TryGetValue(key, out var list))
                    ByPrefix[key] = list = new List<Template>();
                list.Add(t);
            }
            else
            {
                Unprefixed.Add(t);
            }
            TemplateCount++;
        }

        /// <summary>The string to show on screen for an English source string.</summary>
        public static string Display(string s)
        {
            if (!Enabled || string.IsNullOrEmpty(s))
                return s;
            if (Memo.TryGetValue(s, out string cached))
                return cached;
            if (Translated.Contains(s))
                return s;

            string result = Localize(Core(s, 0) ?? Unmatched(s));
            if (Memo.Count >= MemoLimit)
                Memo.Clear();
            Memo[s] = result;
            Memo[result] = result; // Already-localized text coming back through a setter stays as it is.
            return result;
        }

        /// <summary>English source of a translated string, when it is known exactly.</summary>
        public static string ToEnglish(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return Reverse.TryGetValue(s, out string en) ? en : s;
        }

        public static void ClearMemo() => Memo.Clear();

        private static string Unmatched(string s)
        {
            if (LogMissing && Missing.Count < 20000 && !OnlyNumbers.IsMatch(s) && Missing.Add(s))
                Mod.QueueMissing(s);
            return s;
        }

        private static string Core(string s, int depth)
        {
            if (Exact.TryGetValue(s, out string hit))
                return hit;
            if (OnlyNumbers.IsMatch(s))
                return s;

            string trimmed = s.Trim();
            if (trimmed.Length != s.Length && trimmed.Length > 0 && Exact.TryGetValue(trimmed, out hit))
                return s.Replace(trimmed, hit);

            if (Folded.TryGetValue(trimmed, out hit))
                return s.Replace(trimmed, MatchCase(trimmed, hit));

            if (depth > 3)
                return null;

            string viaTemplate = FromTemplates(s, depth);
            if (viaTemplate != null)
                return viaTemplate;

            string viaBuiltIn = FromBuiltIns(s, depth);
            if (viaBuiltIn != null)
                return viaBuiltIn;

            return FromSegments(s, depth);
        }

        private static string MatchCase(string src, string pt)
        {
            bool hasLetter = false;
            foreach (char c in src)
            {
                if (!char.IsLetter(c))
                    continue;
                hasLetter = true;
                if (!char.IsUpper(c))
                    return pt;
            }
            return hasLetter && src.Length > 1 ? Upper(pt) : pt;
        }

        // The mod runtime has invariant globalization, where casing only covers ASCII.
        private static string Upper(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inTag = false;
            foreach (char c in s)
            {
                if (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                if (inTag) { sb.Append(c); continue; }
                int i = "áàâãéêíóôõúüç".IndexOf(c);
                sb.Append(i >= 0 ? "ÁÀÂÃÉÊÍÓÔÕÚÜÇ"[i] : char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        private static string FromTemplates(string s, int depth)
        {
            if (s.Length >= PrefixLength && ByPrefix.TryGetValue(s.Substring(0, PrefixLength), out var list))
                foreach (Template t in list)
                {
                    string r = Apply(t, s, depth);
                    if (r != null)
                        return r;
                }
            foreach (Template t in Unprefixed)
            {
                string r = Apply(t, s, depth);
                if (r != null)
                    return r;
            }
            return null;
        }

        private static string Apply(Template t, string s, int depth)
        {
            Match m = t.Regex.Match(s);
            if (!m.Success)
                return null;
            string result = t.Target;
            for (int i = 0; i < t.Holes.Length; i++)
            {
                string captured = m.Groups[i + 1].Value;
                result = result.Replace(t.Holes[i], Core(captured, depth + 1) ?? captured);
            }
            return result;
        }

        private static readonly (Regex, string)[] BuiltIns =
        {
            (new Regex(@"^(\d+)x (.+)$", RegexOptions.Singleline | RegexOptions.Compiled), "$1x {2}"),
            (new Regex(@"^(.+) x(\d+)$", RegexOptions.Singleline | RegexOptions.Compiled), "{1} x$2"),
            (new Regex(@"^(.+) \((\d+)\)$", RegexOptions.Singleline | RegexOptions.Compiled), "{1} ($2)"),
            (new Regex(@"^(.+) \(([^()]+)\)$", RegexOptions.Singleline | RegexOptions.Compiled), "{1} ({2})"),
        };

        private static string FromBuiltIns(string s, int depth)
        {
            foreach ((Regex regex, string shape) in BuiltIns)
            {
                Match m = regex.Match(s);
                if (!m.Success)
                    continue;
                bool any = false;
                string result = Regex.Replace(shape, @"\{(\d)\}", g =>
                {
                    string part = m.Groups[g.Groups[1].Value[0] - '0'].Value;
                    string tr = Core(part, depth + 1);
                    any |= tr != null && tr != part;
                    return tr ?? part;
                });
                if (any)
                    return Regex.Replace(result, @"\$(\d)", g => m.Groups[g.Groups[1].Value[0] - '0'].Value);
            }

            Match day = Weekday.Match(s);
            if (day.Success)
            {
                string dayName = Core(day.Groups[2].Value, depth + 1);
                if (dayName != null)
                    return dayName + " " + day.Groups[1].Value;
            }

            // Quest and job lists prefix each line with a bullet.
            if (s.Length > 2 && (s[0] == '•' || s[0] == '-' ) && s[1] == ' ')
            {
                string rest = Core(s.Substring(2), depth + 1);
                if (rest != null)
                    return s.Substring(0, 2) + rest;
            }

            // Punctuation appended by the caller: "Price:" or "Loading..."
            string core = s.TrimEnd(TrimPunctuation);
            if (core.Length > 1 && core.Length < s.Length && Exact.TryGetValue(core, out string hit))
                return hit + s.Substring(core.Length);
            if (core.Length > 1 && core.Length < s.Length && Folded.TryGetValue(core, out hit))
                return MatchCase(core, hit) + s.Substring(core.Length);
            return null;
        }

        /// <summary>Rich text and multi-line strings: translate the runs between tags and line breaks.</summary>
        private static string FromSegments(string s, int depth)
        {
            if (s.IndexOf('<') < 0 && s.IndexOf('\n') < 0)
                return null;

            string[] parts = SegmentSplit.Split(s);
            if (parts.Length < 2)
                return null;

            bool all = true, any = false;
            var sb = new StringBuilder(s.Length + 16);
            foreach (string part in parts)
            {
                if (part.Length == 0 || part[0] == '<' && part.EndsWith(">") || part == "\n" || part == "\r\n" || OnlyNumbers.IsMatch(part))
                {
                    sb.Append(part);
                    continue;
                }
                string tr = Core(part, depth + 1);
                if (tr == null)
                {
                    all = false;
                    sb.Append(part);
                }
                else
                {
                    any = true;
                    sb.Append(tr);
                }
            }
            if (!any)
                return null;
            if (!all)
                Unmatched(s);
            return sb.ToString();
        }

        // ---- Brazilian formats: R$ 1.234,56 and 24-hour time ----

        private static string Localize(string s)
        {
            if (s.IndexOf('$') >= 0)
                s = Money.Replace(s, m =>
                {
                    string whole = m.Groups[2].Value.Replace(",", ".");
                    string dec = m.Groups[3].Success ? "," + m.Groups[3].Value.Substring(1) : "";
                    return m.Groups[1].Value + "R$ " + whole + dec;
                });
            if (s.IndexOf(':') >= 0)
                s = Clock.Replace(s, m =>
                {
                    int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) % 12;
                    if (char.ToUpperInvariant(m.Groups[3].Value[0]) == 'P')
                        h += 12;
                    return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.Groups[2].Value;
                });
            if (s.Contains("ft"))
                s = Feet.Replace(s, m => Math.Max(1, (int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 0.3048)) + " m");
            if (s.IndexOf("mph", StringComparison.OrdinalIgnoreCase) >= 0)
                s = Mph.Replace(s, m => (int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1.609) + " km/h");
            if (s.Contains("°F"))
                s = Fahrenheit.Replace(s, m => (int)Math.Round((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 32) / 1.8) + "°C");
            if (s.IndexOf('M') >= 0 || s.IndexOf('m') >= 0)
                s = BareHour.Replace(s, m =>
                {
                    int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) % 12;
                    if (char.ToUpperInvariant(m.Groups[2].Value[0]) == 'P')
                        h += 12;
                    return h + "h";
                });
            return s;
        }
    }
}
