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
        /// <summary>The name in the current language (Tr: OPTIONS > LANGUAGE); NameEn is the disc's.</summary>
        public string Name { get { return Tr.Text(NameEn, "Digimon name"); } set { NameEn = value; } }
        public string NameEn;
        public int Level;            // record byte +29: 1 Baby I, 2 Baby II, 3 Rookie, 4 Champion, 5 Ultimate, 6 Mega
        public int Attribute;        // record byte +28: 1 Data, 2 Vaccine, 3 Virus (matches "is a Data Type Digimon" in the scripts)
        public string SignatureTech; // the Digimon's own finishing move (Agumon: Pepper Breath); null for babies
        public int Radius, Height;            // record +24, +26: its size in game units
        public int BoneCount;                 // record +20 (DigimonPara.boneCount)
        public int DropItem, DropChance;      // record +33, +34: what it can leave after a fight, percent
        public int[] Specials = new int[3];   // record +30: its element types (same numbers as a technique's element, 255 = none): Agumon fire and battle
        public List<int> EvolvesFrom = new List<int>(), EvolvesTo = new List<int>();   // exe 0x8012B661, 11 bytes per Digimon: from[5], to[6], 255 = none
        public int[] RawMoves = new int[16];                  // record +35: all 16 entries, 0xFF = none
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
        public int Id;
        public string Name { get { return Tr.Text(NameEn, "technique name"); } set { NameEn = value; } }
        public string NameEn;
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

    /// <summary>How a partner is raised (RAISE_DATA, exe 0x801225BC, 28 bytes each, 66 partners).</summary>
    public sealed class RaiseData
    {
        public int[] HungerTimes = new int[8];                   // +0: hours it gets hungry (Rookie and up), -1 = none
        public int EnergyCap, EnergyThreshold, EnergyUsage;      // +8, +9, +10
        public int PoopTimer, PoopSize, FavoriteFood;            // +12 (s16), +16, +17
        public int SleepCycle, FavoredRegion, TrainingType;      // +18, +19, +20
        public int DefaultWeight;                                // +21
        public int ViewX, ViewY, ViewZ;                          // +22, +24, +26: where the model stands for the Status page camera (renderDigiviceEntity)
    }

    /// <summary>EVOLUTION_REQS row, exe 0x8012ABEC, 28 bytes each, by Digimon; -1 = not asked.</summary>
    public sealed class EvoRequirement { public int Digimon, Hp, Mp, Off, Def, Speed, Brain, Care, Weight, Discipline, Happiness, Battles, Techs, Flags; }

    /// <summary>EVOLUTION_STATS_GAINS row, exe 0x8012B2D0, 14 bytes each: what each stat moves towards; Brain is the percentage /10 when the stats are scaled.</summary>
    public sealed class EvoGain { public int Hp, Mp, Off, Def, Speed, Brain, Target; }

    /// <summary>A sleep pattern (SLEEP_PATTERN, exe 0x80122CF4, 6 bytes each, 8 patterns; 6 and 7 are the babies').</summary>
    public sealed class SleepPattern { public int SleepyHour, SleepyMinute, WakeupHour, WakeupMinute, AwakeHours, SleepyHours; }

    /// <summary>Reads DIGIMON_DATA (exe 0x8012CEB4, 52 bytes each) and the technique table with its name pointers (exe 0x80126054). Cached after the first read.</summary>
    public static class DigimonStatsCatalog
    {
        const uint DataAddr = 0x8012ceb4, TechNamePointers = 0x80126054, TechTable = 0x8012623c;
        const uint Effectiveness = 0x80125f70, LearnChances = 0x80125fa4, EvolutionTable = 0x8012b661;
        const uint RaiseTable = 0x801225bc, SleepTable = 0x80122cf4;
        const int Stride = 52, TechListStart = 35, TechStride = 16, TechCount = 121, RaiseStride = 28, RaiseCount = 66;

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

        /// <summary>Damage scale (10 = normal) of a technique's element against one element type of the target. Table of 7 x 7 bytes.</summary>
        public static int Effect(int element, int special) { return _exe == null || special > 6 || element > 6 ? 10 : _exe[(int)(Effectiveness - _load + 0x800) + element * 7 + special]; }

        /// <summary>Chance in percent to learn technique n, by which of the Digimon's three element types matches it. Table of 3 bytes per technique.</summary>
        public static int LearnChance(int n, int specialIndex) { return _exe == null ? 0 : _exe[(int)(LearnChances - _load + 0x800) + n * 3 + specialIndex]; }

        /// <summary>Row n of the technique table once the catalog has been read (call Get first); null if there is none.</summary>
        public static TechData TechOf(int n) { return _techs != null && n >= 0 && n < _techs.Length ? _techs[n] : null; }

        /// <summary>Name of technique n once the catalog has been read (call Get first).</summary>
        public static string NameOf(int n) { var t = TechOf(n); return t != null ? t.Name : "?"; }

        /// <summary>Row of the raise table; null for a Digimon that cannot be a partner.</summary>
        public static RaiseData RaiseOf(DiscImage disc, int type)
        {
            Init(disc);
            if (type < 0 || type >= RaiseCount) return null;
            int r = (int)(RaiseTable - _load + 0x800) + type * RaiseStride;
            var d = new RaiseData
            {
                EnergyCap = _exe[r + 8], EnergyThreshold = _exe[r + 9], EnergyUsage = _exe[r + 10],
                PoopTimer = BitConverter.ToInt16(_exe, r + 12), PoopSize = _exe[r + 16], FavoriteFood = _exe[r + 17],
                SleepCycle = _exe[r + 18], FavoredRegion = _exe[r + 19], TrainingType = _exe[r + 20], DefaultWeight = _exe[r + 21],
                ViewX = BitConverter.ToInt16(_exe, r + 22), ViewY = BitConverter.ToInt16(_exe, r + 24), ViewZ = BitConverter.ToInt16(_exe, r + 26)
            };
            for (int i = 0; i < 8; i++) d.HungerTimes[i] = (sbyte)_exe[r + i];
            return d;
        }

        public static EvoRequirement ReqOf(DiscImage disc, int type)
        {
            Init(disc);
            int r = (int)(0x8012abec - _load + 0x800) + type * 28;
            System.Func<int, int> w = i => BitConverter.ToInt16(_exe, r + i * 2);
            return new EvoRequirement { Digimon = w(0), Hp = w(1), Mp = w(2), Off = w(3), Def = w(4), Speed = w(5), Brain = w(6), Care = w(7), Weight = w(8), Discipline = w(9), Happiness = w(10), Battles = w(11), Techs = w(12), Flags = _exe[r + 26] };
        }

        public static EvoGain GainOf(DiscImage disc, int type)
        {
            Init(disc);
            int r = (int)(0x8012b2d0 - _load + 0x800) + type * 14;
            System.Func<int, int> w = i => BitConverter.ToInt16(_exe, r + i * 2);
            return new EvoGain { Hp = w(0), Mp = w(1), Off = w(2), Def = w(3), Speed = w(4), Brain = w(5), Target = w(6) };
        }

        /// <summary>The Digimon an evolution item makes (exe 0x80127C5C, item - 0x47).</summary>
        public static int EvoItemTarget(DiscImage disc, int item) { Init(disc); return _exe[(int)(0x80127c5c - _load + 0x800) + item - 0x47]; }

        /// <summary>ITEM_TAKE_DISTANCE (exe 0x80122D24, 66 bytes, index type - 1): how far from the boy the partner stops to eat, in tenths.</summary>
        public static int TakeDistanceOf(DiscImage disc, int type) { Init(disc); return (sbyte)_exe[(int)(0x80122d24 - _load + 0x800) + Mathf.Clamp(type - 1, 0, 65)]; }

        /// <summary>toilet n of a map (TOILET_DATA, exe 0x80122E10, 8 bytes each): the door spot, then the spot inside, in game units. n from 1.</summary>
        public static int[] ToiletOf(DiscImage disc, int n)
        {
            Init(disc);
            int r = (int)(0x80122e10 - _load + 0x800) + (n - 1) * 8;
            return new int[] { BitConverter.ToInt16(_exe, r), BitConverter.ToInt16(_exe, r + 2), BitConverter.ToInt16(_exe, r + 4), BitConverter.ToInt16(_exe, r + 6) };
        }

        /// <summary>The toilet number of a map (MAP_ENTRIES +14), 0 = none.</summary>
        public static int ToiletIdOf(DiscImage disc, int map) { Init(disc); return _exe[(int)(0x801292d4 - _load + 0x800) + map * 16 + 14]; }

        /// <summary>A zero-ended string in the program.</summary>
        public static string ExeText(DiscImage disc, uint addr)
        {
            Init(disc);
            int p = (int)(addr - _load + 0x800), j = p; while (_exe[j] != 0) j++;
            return Tr.Text(Encoding.ASCII.GetString(_exe, p, j - p), "exe 0x" + addr.ToString("X8"));
        }

        public static SleepPattern SleepOf(DiscImage disc, int cycle)
        {
            Init(disc);
            int r = (int)(SleepTable - _load + 0x800) + cycle * 6;
            return new SleepPattern { SleepyHour = _exe[r], SleepyMinute = _exe[r + 1], WakeupHour = _exe[r + 2], WakeupMinute = _exe[r + 3], AwakeHours = _exe[r + 4], SleepyHours = _exe[r + 5] };
        }

        public static DigimonStats Get(DiscImage disc, int type)
        {
            Init(disc);
            int d = (int)(DataAddr - _load + 0x800) + type * Stride;
            if (type < 0 || d + Stride > _exe.Length) return null;
            var s = new DigimonStats { Type = type, Level = _exe[d + 29], Attribute = _exe[d + 28] };
            s.Radius = BitConverter.ToUInt16(_exe, d + 24);
            s.Height = BitConverter.ToUInt16(_exe, d + 26);
            s.BoneCount = BitConverter.ToInt32(_exe, d + 20);
            s.DropItem = _exe[d + 33]; s.DropChance = _exe[d + 34];
            int ev = (int)(EvolutionTable - _load + 0x800) + type * 11;
            for (int i = 0; i < 5; i++) if (_exe[ev + i] != 0xFF) s.EvolvesFrom.Add(_exe[ev + i]);
            for (int i = 5; i < 11; i++) if (_exe[ev + i] != 0xFF) s.EvolvesTo.Add(_exe[ev + i]);
            for (int i = 0; i < 3; i++) s.Specials[i] = _exe[d + 30 + i];
            int n = 0; while (n < 20 && _exe[d + n] >= 32 && _exe[d + n] < 127) n++;
            s.Name = Encoding.ASCII.GetString(_exe, d, n);
            // the record lists the usable techniques, then the finishing move, all as numbers in the technique table
            for (int i = 0; i < 16; i++) s.RawMoves[i] = _exe[d + TechListStart + i];
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
