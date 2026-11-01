using System;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Disc;
using UnityEngine;

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
        public List<int> Moves = new List<int>();             // the record's whole move list; the finishing move is the last one
        public List<int> Learnable = new List<int>();         // technique numbers it can use: Agumon 2, 0, 4, 5, 3, 43, 41 = Spit Fire, Fire Tower, Magma Bomb, ...
        public int SignatureId = -1; // the finishing move's technique number

        /// <summary>The animation slot of a technique: the game plays slot 46 + the technique's place in this list (Agumon: Spit Fire 46, Pepper Breath 53). -1 = not in the list.</summary>
        public int MoveSlot(int tech) { int i = Moves.IndexOf(tech); return i < 0 ? -1 : FirstMoveSlot + i; }
        public const int FirstMoveSlot = 46;

        public string LevelName { get { string[] n = { "?", "Baby I", "Baby II", "Rookie", "Champion", "Ultimate", "Mega" }; return Level >= 0 && Level < n.Length ? n[Level] : "?"; } }
        public string AttributeName { get { return Attribute == 1 ? "Data" : Attribute == 2 ? "Vaccine" : Attribute == 3 ? "Virus" : "Free"; } }
    }

    /// <summary>One row of the game's technique table (exe 0x8012623C, 16 bytes each). Numbers 0-57 are the Tech page techniques, 58-112 the finishing moves.</summary>
    public sealed class TechData
    {
        public int Id; public string Name;
        public float Reach;          // metres; the game keeps it squared in game units (Spit Fire 400 units)
        public int Power, Mp;        // MP is stored divided by 3
        public int HitFx;            // +7: goes to the game's hit-effect call (plus 16); what it selects is not decoded
        public int Range;            // +8: 1 short, 2 long, 3 wide, 4 self
        public int Element;          // +9: 0 fire, 1 battle, 2 air, 3 earth, 4 ice, 5 mech, 6 filth
        public int Status;           // +10: 0 none, 1 poison, 2 confuse, 3 stun, 4 flat
        public int Accuracy;         // +11: base chance to hit
        public int StatusChance;     // +12: percent
        public int Flags;            // +13: bit 2 = the game holds the fight for 110 frames while it plays; bit 1 not decoded
    }

    /// <summary>Reads DIGIMON_DATA (exe 0x8012CEB4, 52 bytes each) and the technique table with its name pointers (exe 0x80126054). Cached after the first read.</summary>
    public static class DigimonStatsCatalog
    {
        const uint DataAddr = 0x8012ceb4, TechNamePointers = 0x80126054, TechTable = 0x8012623c;
        const int Stride = 52, TechListStart = 35, TechStride = 16, TechCount = 121;

        static byte[] _exe; static uint _load;
        static TechData[] _techs;

        static void Init(DiscImage disc)
        {
            if (_exe != null) return;
            _exe = disc.ReadFile("/SLUS_010.32");
            _load = BitConverter.ToUInt32(_exe, 0x18);
            _techs = new TechData[TechCount];
            for (int i = 0; i < TechCount; i++)
            {
                int r = (int)(TechTable - _load + 0x800) + i * TechStride;
                int p = (int)(BitConverter.ToUInt32(_exe, (int)(TechNamePointers - _load + 0x800) + i * 4) - _load + 0x800);
                int j = p; while (j < _exe.Length && _exe[j] != 0) j++;
                _techs[i] = new TechData
                {
                    Id = i, Name = Encoding.ASCII.GetString(_exe, p, j - p),
                    Reach = Mathf.Sqrt(BitConverter.ToUInt32(_exe, r)) * 0.01f,
                    Power = BitConverter.ToUInt16(_exe, r + 4), Mp = _exe[r + 6] * 3, HitFx = _exe[r + 7], Range = _exe[r + 8], Element = _exe[r + 9],
                    Status = _exe[r + 10], Accuracy = _exe[r + 11], StatusChance = _exe[r + 12], Flags = _exe[r + 13]
                };
            }
        }

        /// <summary>Row n of the technique table once the catalog has been read (call Get first); null if there is none.</summary>
        public static TechData TechOf(int n) { return _techs != null && n >= 0 && n < _techs.Length ? _techs[n] : null; }

        /// <summary>Name of technique n once the catalog has been read (call Get first).</summary>
        public static string NameOf(int n) { var t = TechOf(n); return t != null ? t.Name : "?"; }

        public static DigimonStats Get(DiscImage disc, int type)
        {
            Init(disc);
            int d = (int)(DataAddr - _load + 0x800) + type * Stride;
            if (type < 0 || d + Stride > _exe.Length) return null;
            var s = new DigimonStats { Type = type, Level = _exe[d + 29], Attribute = _exe[d + 28] };
            int n = 0; while (n < 20 && _exe[d + n] >= 32 && _exe[d + n] < 127) n++;
            s.Name = Encoding.ASCII.GetString(_exe, d, n);
            // the record lists the usable techniques, then the finishing move, all as numbers in the technique table
            for (int i = TechListStart; i < Stride - 1; i++) { if (_exe[d + i] == 0xFF) break; s.Moves.Add(_exe[d + i]); }
            if (s.Moves.Count > 0)
            {
                s.SignatureId = s.Moves[s.Moves.Count - 1]; s.SignatureTech = NameOf(s.SignatureId);
                for (int i = 0; i < s.Moves.Count - 1; i++) s.Learnable.Add(s.Moves[i]);
            }
            return s;
        }
    }
}
