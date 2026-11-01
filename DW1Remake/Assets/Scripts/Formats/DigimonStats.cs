using System;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Disc;

namespace DW1Remake.Formats
{
    /// <summary>What the game program says about a Digimon besides its model: stage, type and its own technique.</summary>
    public sealed class DigimonStats
    {
        public int Type;
        public string Name;
        public int Level;            // record byte +29: 1 Baby I, 2 Baby II, 3 Rookie, 4 Champion, 5 Ultimate, 6 Mega
        public int Attribute;        // record byte +28: 1 Data, 2 Vaccine, 3 Virus (matches "is a Data Type Digimon" in the scripts)
        public string SignatureTech; // the Digimon's own finishing move (Agumon: Pepper Breath); null for babies
        public List<int> Learnable = new List<int>();         // technique numbers (position in the name list) it can use: Agumon 2, 0, 4, 5, 3, 43, 41 = Spit Fire, Fire Tower, Magma Bomb, ...
        public List<string> LearnableNames = new List<string>();
        public int SignatureId = -1; // the finishing move's number in the name list (the record stores it plus 6)

        public string LevelName { get { string[] n = { "?", "Baby I", "Baby II", "Rookie", "Champion", "Ultimate", "Mega" }; return Level >= 0 && Level < n.Length ? n[Level] : "?"; } }
        public string AttributeName { get { return Attribute == 1 ? "Data" : Attribute == 2 ? "Vaccine" : Attribute == 3 ? "Virus" : "Free"; } }
    }

    /// <summary>Reads DIGIMON_DATA (exe 0x8012CEB4, 52 bytes each) and the technique names (exe 0x80124C6C). Cached after the first read.</summary>
    public static class DigimonStatsCatalog
    {
        const uint DataAddr = 0x8012ceb4, TechNamesAddr = 0x80124c6c;
        const int Stride = 52, TechListStart = 35, TechIdBase = 6;   // technique number = name position + 6 (ids 0-5 are not named)

        static byte[] _exe; static uint _load;
        static List<string> _techNames;

        static void Init(DiscImage disc)
        {
            if (_exe != null) return;
            _exe = disc.ReadFile("/SLUS_010.32");
            _load = BitConverter.ToUInt32(_exe, 0x18);
            _techNames = new List<string>();
            int o = (int)(TechNamesAddr - _load + 0x800);
            while (o < _exe.Length)
            {
                int j = o; while (j < _exe.Length && _exe[j] != 0) j++;
                string s = Encoding.ASCII.GetString(_exe, o, j - o);
                if (s.StartsWith("Small Recovery") || _techNames.Count > 200) break;
                _techNames.Add(s);
                o = (j + 4) & ~3;
            }
        }

        /// <summary>Name of technique n once the catalog has been read (call Get first).</summary>
        public static string NameOf(int n) { return _techNames != null && n >= 0 && n < _techNames.Count ? _techNames[n] : "?"; }

        /// <summary>Name of technique number n (its position in the name list).</summary>
        public static string TechName(DiscImage disc, int n)
        {
            Init(disc);
            return n >= 0 && n < _techNames.Count ? _techNames[n] : null;
        }

        public static DigimonStats Get(DiscImage disc, int type)
        {
            Init(disc);
            int d = (int)(DataAddr - _load + 0x800) + type * Stride;
            if (type < 0 || d + Stride > _exe.Length) return null;
            var s = new DigimonStats { Type = type, Level = _exe[d + 29], Attribute = _exe[d + 28] };
            int n = 0; while (n < 20 && _exe[d + n] >= 32 && _exe[d + n] < 127) n++;
            s.Name = Encoding.ASCII.GetString(_exe, d, n);
            // the record lists the usable techniques (their number = position in the name list), then the finishing move stored as number + 6
            var ids = new List<int>();
            for (int i = TechListStart; i < Stride - 1; i++) { if (_exe[d + i] == 0xFF) break; ids.Add(_exe[d + i]); }
            if (ids.Count > 0)
            {
                int sig = ids[ids.Count - 1] - TechIdBase;
                if (sig >= 0 && sig < _techNames.Count) { s.SignatureId = sig; s.SignatureTech = _techNames[sig]; }
                for (int i = 0; i < ids.Count - 1; i++) if (ids[i] < _techNames.Count) { s.Learnable.Add(ids[i]); s.LearnableNames.Add(_techNames[ids[i]]); }
                if (s.SignatureId < 0 && ids.Count == 1) { }   // babies: a single entry that is not a technique
            }
            return s;
        }
    }
}
