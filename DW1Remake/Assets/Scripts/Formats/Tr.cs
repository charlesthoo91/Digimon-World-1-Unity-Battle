using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// Ours: the Traditional Chinese translation (OPTIONS > LANGUAGE, docs/TRANSLATION-PLAN.md). The game still reads its English from the
    /// player's disc; each English line is looked up by its fingerprint in the shipped file Resources/Translation/zh-Hant.txt
    /// (made by tools/text_build.py: fingerprint, tab, the Chinese; no English in it). A line with no translation, or whose Chinese has
    /// lost one of the English line's codes, stays English.
    /// Codes are kept in the Chinese as tokens: {cN} text colour N (01 N), {XX:YY} a value the game fills in (05 the boy's name,
    /// 06 the partner's...), and \n a line break. tools/tr_common.py makes the same fingerprints and templates; keep the two in step.
    /// </summary>
    public static class Tr
    {
        static Dictionary<ulong, string> _zh;
        static bool _tried;
        static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        static readonly HashSet<string> _missing = new HashSet<string>();
        static int _cacheLang = -1;

        /// <summary>The width of a string in the game font's PS1 pixels without any translation (GameFont sets it), to keep padded texts centred.</summary>
        public static Func<string, int> MeasureRaw;
        /// <summary>Names typed by the player (the boy, the partner): never translated, even when they spell a word of the game.</summary>
        public static string PlayerName, PartnerName;

        /// <summary>The translation is there (the shipped file loaded).</summary>
        public static bool Ready { get { Load(); return _zh != null && _zh.Count > 0; } }
        /// <summary>LANGUAGE is Traditional Chinese and the translation is there.</summary>
        public static bool Chinese
        {
            get
            {
                // read once a frame: every drawn and measured string asks, and the setting is a PlayerPrefs read
                int f = Time.frameCount;
                if (f != _langFrame) { _langFrame = f; _lang = Game.ModSettings.Language; }
                return _lang == 1 && Ready;
            }
        }
        static int _langFrame = -1, _lang;

        static void Load()
        {
            if (_tried) return;
            _tried = true;
            var ta = Resources.Load<TextAsset>("Translation/zh-Hant");
            if (ta == null) { Debug.LogWarning("[tr] no Translation/zh-Hant in Resources: Chinese is off"); return; }
            _zh = new Dictionary<ulong, string>();
            foreach (var raw in ta.text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int tab = line.IndexOf('\t');
                if (tab != 16 || line.StartsWith("#")) continue;
                ulong k;
                if (!ulong.TryParse(line.Substring(0, 16), System.Globalization.NumberStyles.HexNumber, null, out k)) continue;
                _zh[k] = line.Substring(17).Replace("{br}", "\n");
            }
            Debug.Log("[tr] Chinese lines: " + _zh.Count);
        }

        /// <summary>FNV-1a 64 over the string's UTF-16 code units, low byte first (tr_common.fingerprint).</summary>
        public static ulong Fingerprint(string s)
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (char c in s)
            {
                h ^= (byte)(c & 0xFF); h *= 0x100000001B3UL;
                h ^= (byte)(c >> 8); h *= 0x100000001B3UL;
            }
            return h;
        }

        /// <summary>A character that holds a Shift-JIS code (the readers that keep the code: StatusView, curling) becomes what MapScripts.Pair
        /// makes of it, so every reader of the same bytes gives the same key.</summary>
        public static string Normalise(string s)
        {
            bool any = false;
            foreach (char c in s) if (c >= 0x8140 && c <= 0x84FF) { any = true; break; }
            if (!any) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c >= 0x8140 && c <= 0x84FF ? MapScripts.Pair(c) : c.ToString());
            return sb.ToString();
        }

        static string Find(string key)
        {
            string zh;
            return _zh != null && _zh.TryGetValue(Fingerprint(key), out zh) ? zh : null;
        }

        static void Missing(string where, string en)
        {
            if (!Debug.isDebugBuild || where == null || !_missing.Add(en)) return;
            Debug.Log("[tr] missing: " + where + " \"" + en.Replace("\n", "{br}") + "\"");
        }

        /// <summary>A script text's template (ScriptRunner.Decode: codes still as tokens): the Chinese template when there is one with the same
        /// codes, else the English one.</summary>
        public static string Template(string en, string where)
        {
            if (!Chinese || string.IsNullOrEmpty(en)) return en;
            string zh = Find(en);
            if (zh == null) { Missing(where, en); return en; }
            if (!SameTokens(en, zh)) { if (Debug.isDebugBuild && _missing.Add(en)) Debug.LogWarning("[tr] codes differ, English kept: " + where); return en; }
            return zh;
        }

        /// <summary>A plain text of the disc (names, exe and overlay strings) in the current language. Spaces round it are kept, and a padded
        /// text stays centred (area names). 'where' names the source for the dev-build "[tr] missing" line (null = no line: the font's own check).</summary>
        public static string Text(string en, string where = null)
        {
            if (string.IsNullOrEmpty(en) || !Chinese) return en;
            if (_cacheLang != 1) { _cache.Clear(); _cacheLang = 1; }
            string r;
            if (_cache.TryGetValue(en, out r)) return r;
            r = en;
            if (en != PlayerName && en != PartnerName && HasLetters(en))
            {
                int a = 0, b = en.Length;
                while (a < b && en[a] == ' ') a++;
                while (b > a && en[b - 1] == ' ') b--;
                string core = en.Substring(a, b - a);
                string zh = Find(Normalise(core));
                if (zh != null && SameTokens(core, zh))
                {
                    zh = ExpandColours(zh);
                    if (a == 0 && b == en.Length) r = zh;
                    else
                    {
                        // keep the text centred in its padding: the width the English took, shared out on both sides
                        int sp = MeasureRaw != null ? Math.Max(1, MeasureRaw(" ")) : 6;
                        int enW = MeasureRaw != null ? MeasureRaw(core) : core.Length * 7, zhW = Width(zh);
                        int lead = a, trail = en.Length - b;
                        if (lead > 0 && trail > 0) { int extra = (enW - zhW) / sp; lead = Math.Max(0, a + extra / 2); trail = Math.Max(0, trail + extra - extra / 2); }
                        else if (lead > 0) lead = Math.Max(0, a + (enW - zhW) / sp);
                        else trail = Math.Max(0, trail + (enW - zhW) / sp);
                        r = new string(' ', lead) + zh + new string(' ', trail);
                    }
                }
                else Missing(where, core);
            }
            if (_cache.Count > 20000) _cache.Clear();
            _cache[en] = r;
            return r;
        }

        /// <summary>Ours: one of our own texts with values in it ({0}, {1}...: translation/ours.tsv keeps the same places), in the current language.</summary>
        public static string Format(string en, params object[] args)
        {
            string t = Text(en, "ours");
            try { return string.Format(t, args); } catch (FormatException) { return string.Format(en, args); }
        }

        static bool HasLetters(string s) { int n = 0; foreach (char c in s) if (c < 0x80 && char.IsLetter(c) && ++n >= 2) return true; return false; }

        /// <summary>Width of a translated text: the Chinese characters 12 pixels each, the rest by the game font.</summary>
        static int Width(string s)
        {
            int w = 0; var rest = new StringBuilder();
            foreach (char c in s) { if (IsCjk(c)) w += 12; else rest.Append(c); }
            return w + (MeasureRaw != null ? MeasureRaw(rest.ToString()) : rest.Length * 7);
        }

        /// <summary>A character drawn from the Chinese glyphs (ideographs, kana, full-width punctuation).</summary>
        public static bool IsCjk(char c) { return c >= 0x2E80 && c < 0xE000 || c >= 0xFF00 && c <= 0xFFEF || c == '…' || c == '—' || c >= 0x3000 && c <= 0x303F; }
        /// <summary>A text's length in the half-width cells the game's centring sums count (len / 2, len x 6): a Chinese character takes two.</summary>
        public static int Cells(string s) { if (s == null) return 0; int n = 0; foreach (char c in s) n += IsCjk(c) ? 2 : 1; return n; }
        public static bool HasCjk(string s) { if (s == null) return false; foreach (char c in s) if (IsCjk(c)) return true; return false; }

        // the tokens of a template ({cN}, {XX:YY}), sorted: a translation must keep the same ones
        static List<string> Tokens(string s)
        {
            var l = new List<string>();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '{') continue;
                int j = s.IndexOf('}', i);
                if (j < 0) break;
                l.Add(s.Substring(i, j - i + 1)); i = j;
            }
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        static bool SameTokens(string en, string zh)
        {
            if (en.IndexOf('{') < 0 && zh.IndexOf('{') < 0) return true;
            var a = Tokens(en); var b = Tokens(zh);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>A template made into the text shown: {cN} -> the colour mark (or nothing), {XX:YY} -> code(XX, YY).</summary>
        public static string Expand(string t, bool colours, Func<int, int, string> code)
        {
            if (t.IndexOf('{') < 0) return t;
            var sb = new StringBuilder(t.Length + 16);
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                int j = c == '{' ? t.IndexOf('}', i) : -1;
                if (j < 0) { sb.Append(c); continue; }
                string tok = t.Substring(i + 1, j - i - 1);
                if (tok.Length >= 2 && tok[0] == 'c') { int n; if (int.TryParse(tok.Substring(1), out n)) { if (colours) sb.Append((char)(ScriptRunner.ColourMark + (n & 15))); } }
                else if (tok.Length == 5 && tok[2] == ':')
                {
                    int x = Convert.ToInt32(tok.Substring(0, 2), 16), y = Convert.ToInt32(tok.Substring(3, 2), 16);
                    if (code != null) sb.Append(code(x, y));
                }
                else sb.Append(t, i, j - i + 1);
                i = j;
            }
            return sb.ToString();
        }

        static string ExpandColours(string t) { return Expand(t, true, null); }

        /// <summary>Line breaking by character for a row with Chinese in it: rows wider than maxPx are broken between two Chinese
        /// characters (or at a space between Latin words), never before closing punctuation. English rows are left as the disc has them.</summary>
        public static List<string> Wrap(string row, int maxPx, Func<string, int> measure)
        {
            var o = new List<string>();
            if (!HasCjk(row) || measure(row) <= maxPx) { o.Add(row); return o; }
            int start = 0;
            while (start < row.Length)
            {
                int end = start, lastOk = -1;
                while (end < row.Length && measure(row.Substring(start, end - start + 1)) <= maxPx)
                {
                    end++;
                    if (end < row.Length && CanBreakBefore(row, end)) lastOk = end;
                }
                if (end >= row.Length) { o.Add(row.Substring(start)); break; }
                int cut = lastOk > start ? lastOk : Math.Max(end, start + 1);
                o.Add(row.Substring(start, cut - start).TrimEnd(' '));
                start = cut;
                while (start < row.Length && row[start] == ' ') start++;
            }
            return o;
        }

        static bool CanBreakBefore(string s, int i)
        {
            char c = s[i], p = s[i - 1];
            if ("，。、！？；：」』）…—,.!?;:)".IndexOf(c) >= 0) return false;
            if ("「『（(".IndexOf(p) >= 0) return false;
            if (IsCjk(c) || IsCjk(p)) return true;
            return p == ' ';
        }
    }
}
