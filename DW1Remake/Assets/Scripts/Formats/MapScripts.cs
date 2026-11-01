using System;
using System.Collections.Generic;
using DW1Remake.Disc;

namespace DW1Remake.Formats
{
    /// <summary>
    /// Reads the game's map scripts (/SCN/MAPHEAD.SCN for map 0, /SCN/DG.SCN for the others).
    /// A script is a list of sections (id, offset). Door triggers on the walking grid (values 50..109)
    /// run the section with the same number. We don't run the scripts yet; we only look for their
    /// "change map" command (opcode 0x4B: map, exit, flag) to find out where a door leads.
    /// </summary>
    public sealed class MapScripts
    {
        byte[] _mapHead, _dg;
        uint[] _table;

        public bool Available { get { return _mapHead != null && _dg != null; } }

        public static MapScripts Load(DiscImage disc)
        {
            var s = new MapScripts();
            if (disc.Exists("/SCN/MAPHEAD.SCN") && disc.Exists("/SCN/DG.SCN"))
            {
                s._mapHead = disc.ReadFile("/SCN/MAPHEAD.SCN");
                s._dg = disc.ReadFile("/SCN/DG.SCN");
                s._table = new uint[2048];
                for (int i = 0; i < 2048 && i * 4 + 4 <= s._dg.Length; i++) s._table[i] = BitConverter.ToUInt32(s._dg, i * 4);
            }
            return s;
        }

        // the script of a map (map index = script id)
        bool GetScript(int id, out byte[] data, out int start, out int end)
        {
            data = null; start = 0; end = 0;
            if (id == 0) { data = _mapHead; start = 0; end = _mapHead.Length; return true; }
            if (id < 0 || id >= 2047) return false;
            long a = _table[id], b = _table[id + 1];
            if (a == 0 || b <= a || b > _dg.Length) return false;
            data = _dg; start = (int)a; end = (int)b;
            return true;
        }

        /// <summary>
        /// Which script belongs to a map. Script 0 (MAPHEAD) has one section per map that starts with
        /// the command FB 00 (script id, map id); the first number is the map's own script.
        /// </summary>
        public int ScriptIdForMap(int mapId)
        {
            if (!Available) return mapId + 1;
            var d = _mapHead;
            for (int o = 2; o + 4 <= d.Length; o += 4)
            {
                int id = BitConverter.ToUInt16(d, o);
                if (id == 0xFFFF) break;
                if (id != mapId) continue;
                int off = BitConverter.ToUInt16(d, o + 2);
                if (off + 4 <= d.Length && d[off] == 0xFB && d[off + 1] == 0x00) return BitConverter.ToUInt16(d, off + 2);
                break;
            }
            return mapId + 1;
        }

        /// <summary>The raw bytes of one script (0 = MAPHEAD). Section offsets inside are relative to the start of these bytes.</summary>
        public byte[] GetScriptBytes(int id)
        {
            byte[] d; int s, e;
            if (!Available || !GetScript(id, out d, out s, out e)) return null;
            var r = new byte[e - s];
            Array.Copy(d, s, r, 0, r.Length);
            return r;
        }

        internal static string Pair(int w)
        {
            if (w >= 0x824F && w <= 0x8258) return ((char)('0' + w - 0x824F)).ToString();
            if (w >= 0x8260 && w <= 0x8279) return ((char)('A' + w - 0x8260)).ToString();
            if (w >= 0x8281 && w <= 0x829A) return ((char)('a' + w - 0x8281)).ToString();
            switch (w)
            {
                case 0x8140: return " ";
                case 0x8141: case 0x8143: return ",";
                case 0x8142: case 0x8144: return ".";
                case 0x8145: return "·";
                case 0x8146: return ":";
                case 0x8147: return ";";
                case 0x8148: return "?";
                case 0x8149: return "!";
                case 0x8151: return "_";
                case 0x815E: return "/";
                case 0x8160: return "~";
                case 0x8165: case 0x8166: case 0x8175: return "'";   // 0x8175 is this game's apostrophe (we're, it's)
                case 0x8168: case 0x8176: return "\"";
                case 0x8169: return "(";
                case 0x816A: return ")";
                case 0x817B: return "+";
                case 0x815C: case 0x817C: return "-";
                case 0x8193: return "%";
                case 0x8195: return "&";
                case 0x8196: return "*";
                case 0x8197: return "@";
                case 0x8181: return "=";
            }
            return "?";
        }

        /// <summary>
        /// The text boxes of a script section (signs, NPC talk), in file order. Text starts after the command 1A 00,
        /// lines end with 0D 00 and a box ends with 00. Choices and conditions are not handled yet.
        /// </summary>
        public List<string> GetTextPages(int scriptId, int section)
        {
            var pages = new List<string>();
            if (!Available) return pages;
            byte[] d; int s, e;
            if (!GetScript(scriptId, out d, out s, out e)) return pages;
            var offs = new List<int>(); int found = -1;
            for (int o = s + 2; o + 4 <= e; o += 4)
            {
                int id = BitConverter.ToUInt16(d, o);
                if (id == 0xFFFF) break;
                int off = BitConverter.ToUInt16(d, o + 2);
                offs.Add(off);
                if (id == section && found < 0) found = off;
            }
            if (found < 0) return pages;
            int end = e - s;
            foreach (int off in offs) if (off > found && off < end) end = off;
            int a = s + found, b = s + end;
            for (int i = a; i + 1 < b; i++) if (d[i] == 0xFE && d[i + 1] == 0x00) { b = i; break; }

            for (int i = a; i + 3 < b; i++)
            {
                if (d[i] != 0x1A || d[i + 1] != 0x00 || (d[i + 2] != 0x82 && d[i + 2] != 0x81)) continue;
                var sb = new System.Text.StringBuilder();
                int j = i + 2;
                while (j < b)
                {
                    int c = d[j];
                    if ((c >= 0x81 && c <= 0x9F) || (c >= 0xE0 && c <= 0xFC)) { if (j + 1 >= b) break; sb.Append(Pair(c << 8 | d[j + 1])); j += 2; }
                    else if (c == 0x0D) { sb.Append('\n'); j++; if (j < b && d[j] == 0) j++; }
                    else if (c == 0x04) { sb.Append("?"); j += 2; }      // a value the game fills in (time, name, number...)
                    else if (c == 0x01 || c == 0x02 || c == 0x03) j += 2; // colour / formatting
                    else if (c == 0x00) break;
                    else j++;
                }
                string text = sb.ToString().Trim('\n', ' ');
                if (text.Length > 0) pages.Add(text);
                i = j;
            }
            return pages;
        }

        /// <summary>All "change map" commands found in a script section, in the order they appear.</summary>
        public List<int[]> FindMapChanges(int scriptId, int section, int mapCount)
        {
            var result = new List<int[]>();
            if (!Available) return result;
            byte[] d; int s, e;
            if (!GetScript(scriptId, out d, out s, out e)) return result;

            // section table: (u16 id, u16 offset) pairs from byte 2, ended by id 0xFFFF
            var offs = new List<int>();
            int found = -1;
            for (int o = s + 2; o + 4 <= e; o += 4)
            {
                int id = BitConverter.ToUInt16(d, o);
                if (id == 0xFFFF) break;
                int off = BitConverter.ToUInt16(d, o + 2);
                offs.Add(off);
                if (id == section && found < 0) found = off;
            }
            if (found < 0) return result;
            int end = e - s;
            foreach (int off in offs) if (off > found && off < end) end = off;
            // a section ends with the command FE 00; anything after it belongs to something else
            for (int i = s + found; i + 1 < s + end; i++)
                if (d[i] == 0xFE && d[i + 1] == 0x00) { end = i + 2 - s; break; }

            for (int i = s + found; i + 3 < s + end; i++)
            {
                if (d[i] != 0x4B) continue;
                int map = d[i + 1], exit = d[i + 2];
                if (map < mapCount && exit <= 9) result.Add(new[] { map, exit });
            }
            return result;
        }
    }
}
