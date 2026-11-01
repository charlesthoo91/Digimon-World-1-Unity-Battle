using System;
using System.Collections.Generic;
using System.Text;

namespace DW1Remake.Formats
{
    /// <summary>A pile on a map; Size 0 = none.</summary>
    [Serializable]
    public sealed class PoopPile { public int Map, X, Y, Size; }

    /// <summary>The game's PartnerPara: the care numbers kept for the partner (decompilation: include/dw/params.h). Happiness, Discipline, Age, Weight and lives live in ScriptState.</summary>
    [Serializable]
    public sealed class PartnerPara
    {
        public int Condition;                                     // PartnerCare.Sleepy, Tired, Hungry, Poopy, Unhappy, Injured, Sick
        public int SleepyHour, SleepyMinute, WakeupHour, WakeupMinute, HoursAwakeDefault, HoursAsleepDefault, TimeAwakeToday;
        public int SicknessCounter, MissedSleepHours, TirednessSleepTimer;
        public int PoopLevel, PoopingTimer, Virus;
        public int Tiredness, SubTiredness, TirednessHungerTimer;
        public int TimesBeingSick, SicknessTries, AreaEffectTimer, SicknessTimer, InjuryTimer;
        public int NextHungerHour, Energy, FoodLevel, StarvationTimer, RefusedFavFood, EmptyStomachTimer;
        public int Lifetime;                                      // hours left to live
        public int TrainBoostFlag, TrainBoostValue, TrainBoostTimer;
        public int CareMistakes, Battles, EvoTimer;               // EvoTimer = hours since the last evolution
        public int SukaHp, SukaMp, SukaOff, SukaDef, SukaSpeed, SukaBrain;   // the stats before turning into Sukamon
        public int ItemScoldFlag, EvolutionTarget = -1, ImmortalHour = -1, RaisedCount = 1, CurrentPoop;
        public bool NanimonTrigger, HasImmortalHour, IsNaturalDeath;
        public int[] Upgrades = new int[6];                       // gym upgrade counters: HP, MP, Off, Def, Speed, Brains
        public PoopPile[] Piles = NewPiles();                     // WORLD_POOP
        static PoopPile[] NewPiles() { var a = new PoopPile[100]; for (int i = 0; i < a.Length; i++) a[i] = new PoopPile(); return a; }
    }

    /// <summary>Story progress: the game's script variables (bytes) and flags (bits). A new game starts with everything 0.</summary>
    public sealed class ScriptState
    {
        public byte[] Vars = new byte[256];
        public byte[] Flags = new byte[100];     // 800 flags
        // Partner values the scripts read and set (0x34 set, 0x35 add, 0x36 reduce). 0-3 = offense, defense, speed, brains; 4,5 = max HP, max MP; 6,7 = current HP, MP;
        // the numbers 14-19 (merit, battles started and fled, tournaments) stay here too. See GetStat for the others.
        public ushort[] Stats = new ushort[32];
        public PartnerPara Para = new PartnerPara();
        public int TamerLevel;
        public Action AfterStatChange;            // set by PartnerCare
        public Action<int, int> OnSound;          // set by the room
        public Action<int> OnBgm;
        public int Money;                         // bits; 0x2A adds, 0x2B takes away (the opening takes 50000 from 0, so the game starts at 0)
        public byte[] Bag = new byte[128];        // how many of each item the player carries (item number = position in the game's item list)
        public int BagKinds() { int n = 0; foreach (var b in Bag) if (b > 0) n++; return n; }
        public bool AddItem(int id, int n)
        {
            if (id < 0 || id >= Bag.Length || n <= 0) return false;
            if (Bag[id] == 0 && ItemSlots > 0 && BagKinds() >= ItemSlots) return false;   // no free slot
            if (Bag[id] + n > 99) return false;
            Bag[id] += (byte)n; return true;
        }
        public void RemoveItem(int id, int n) { if (id >= 0 && id < Bag.Length) Bag[id] = (byte)Math.Max(0, Bag[id] - n); }
        public int Happiness = 50, Discipline = 50;   // happiness -100 to 100, discipline 0 to 100; both start at 50 (initializePartner)
        public int Age, Weight = 15, Life = 3;        // Life = the partner's lives
        public float PlayMinutes;                    // time played
        // names and numbers the game's text boxes insert (text codes 4-12)
        public string PlayerName = "A", PartnerName = "B";
        public System.Func<int, string> SpeciesNameOf, ItemNameOf;   // Digimon / item names, read from the disc by the room
        public System.Func<int, int> TimeValue;                      // arg 0x69 month, 0x6A day, 0x6B hour, 0x6C minute
        public string CodeText(int code, int arg, int speaker = 255)
        {
            switch (code)
            {
                case 0x05: return PlayerName;    // the boy ("B, this isn't a dream" - Jijimon to the boy)
                case 0x06: return PartnerName;   // the partner ("Take Agumon and explore", "Are you alright, Agumon" - checked in about 40 lines)
                case 0x07: return SpeciesNameOf != null ? SpeciesNameOf(Vars[arg]) : "";
                case 0x09: return ItemNameOf != null ? ItemNameOf(Vars[arg]) : "";
                case 0x0A: return Money.ToString();
                case 0x04: return TimeValue != null ? TimeValue(arg).ToString() : "0";
                default: return "";   // codes not understood yet are left out
            }
        }
        public int ItemSlots;                     // 0x23: how many items the bag holds (10 at the start, 20/30 with each Dimensional Key Chain)
        public bool[] Cards = new bool[256];      // cards collected (command 0x2F n gives card n)
        public bool[] Techs = new bool[256];      // techniques the partner knows (0x2D teaches one)
        public bool[] Raised = new bool[180];     // Digimon types you have raised (the Chart page shows only these)
        public List<int> TechSet = new List<int>();   // the (up to 3) techniques picked on the Tech page; the battle uses these
        static readonly short[] StatLimits = { 999, 999, 999, 999, 9999, 9999, 9999, 9999, 100, 100, 999, 999, 999, 999, 9999, 9999, 9999, 999, 999, 999, 99, 10 };   // STATS_LIMIT_ARRAY

        /// <summary>A partner value by the scripts' number (getStatsPointer): 8 tiredness, 9 happiness, 10 discipline, 11 energy, 12 virus bar, 13 lifetime, 20 weight, 21 tamer level, 22 lives.</summary>
        public int GetStat(int i)
        {
            switch (i)
            {
                case 8: return Para.Tiredness;
                case 9: return Happiness;
                case 10: return Discipline;
                case 11: return Para.Energy;
                case 12: return Para.Virus;
                case 13: return Para.Lifetime;
                case 20: return Weight;
                case 21: return TamerLevel;
                case 22: return Life;
                default: return i >= 0 && i < Stats.Length ? Stats[i] : 0;
            }
        }
        void PutStat(int i, int v)
        {
            switch (i)
            {
                case 8: Para.Tiredness = v; break;
                case 9: Happiness = v; break;
                case 10: Discipline = v; break;
                case 11: Para.Energy = v; break;
                case 12: Para.Virus = v; break;
                case 13: Para.Lifetime = v; break;
                case 20: Weight = v; break;
                case 21: TamerLevel = v; break;
                case 22: Life = v; break;
                default: if (i >= 0 && i < Stats.Length) Stats[i] = (ushort)Math.Max(0, v); break;
            }
            if (AfterStatChange != null) AfterStatChange();
        }
        // enforceStatsLimits: HP and MP stop at their maximum, the rest at the table
        int Limited(int i, int v)
        {
            int cap = i == 6 ? Stats[4] : i == 7 ? Stats[5] : i >= 0 && i < StatLimits.Length ? StatLimits[i] : 9999;
            return v > cap ? cap : v;
        }
        public void SetStat(int i, int v) { PutStat(i, Limited(i, v)); }
        public void AddStat(int i, int v) { PutStat(i, Limited(i, GetStat(i) + v)); }
        public void ReduceStat(int i, int v) { PutStat(i, Math.Max(i == 9 ? -100 : 0, GetStat(i) - (short)v)); }
        public bool GetFlag(int i) { return i >= 0 && i < 800 && (Flags[i >> 3] & (1 << (i & 7))) != 0; }
        public void SetFlag(int i, bool on)
        {
            if (i < 0 || i >= 800) return;
            if (on) Flags[i >> 3] |= (byte)(1 << (i & 7)); else Flags[i >> 3] &= (byte)~(1 << (i & 7));
        }
    }

    /// <summary>One thing a script wants to happen, in order: show a text box, or change map.</summary>
    public sealed class ScriptAction
    {
        public string Text;                  // text box
        public List<string> Options;         // a menu: the player must pick one (then call Choose)
        public int Speaker = 255;            // who talks (0xFD = the player, 0xFC = the partner, 255 = nobody, otherwise a map Digimon's script number)
        public int Cmd = -1;                 // cutscene command (0x4C face, 0x4D angle, 0x4E walk, 0x4F camera, 0x6C place, 0x56 animation, 0x67 wait, 0x4A wait for all)
        public int Actor, Target, Frames;    // Actor: 0xFD = the player, 0xFC = the partner, otherwise the map Digimon whose script number matches
        public float X, Z, Angle;            // game units / game angle (4096 = full turn)
        public int Map = -1, Exit, Arrive = 255;   // map change (Arrive = script section to run on arrival, 255 = none)
    }

    /// <summary>
    /// Runs the game's own map scripts (the DG.SCN bytecode). Supported: flags, variables, if / switch / jump / call,
    /// text boxes and map changes. Commands for sound, items, fights, walking and so on are skipped by size for now.
    /// </summary>
    public sealed class ScriptRunner
    {
        // size in bytes of each command (including the command byte) for the ones that are simply skipped
        static readonly Dictionary<int, int> Len = new Dictionary<int, int>
        {
            {0x11,2},{0x1B,2},{0x21,2},{0x22,2},{0x23,2},{0x24,4},{0x25,2},{0x26,4},{0x27,2},
            {0x28,4},{0x29,4},{0x2A,6},{0x2B,6},{0x2C,10},{0x2D,2},{0x2E,2},{0x2F,2},{0x30,2},{0x31,4},{0x32,4},{0x33,4},
            {0x34,4},{0x35,4},{0x36,4},{0x37,2},{0x38,6},{0x39,6},{0x3A,4},{0x3B,4},{0x3C,4},{0x3D,2},{0x3E,2},{0x3F,4},
            {0x46,2},{0x48,2},{0x49,2},{0x4A,2},{0x4C,4},{0x4D,4},{0x4E,8},{0x4F,6},{0x50,4},{0x51,4},{0x52,8},
            {0x53,4},{0x54,2},{0x55,8},{0x56,4},{0x57,4},{0x58,2},
            {0x5A,4},{0x5B,2},{0x5C,2},{0x5D,2},{0x5E,2},{0x5F,2},
            {0x64,2},{0x65,2},{0x66,2},{0x67,4},{0x68,4},{0x69,2},{0x6A,4},{0x6B,2},{0x6C,8},{0x6D,4},{0x6E,8},{0x6F,4},
            {0x70,4},{0x71,10},{0x72,6},{0x73,6},{0x74,6},{0x75,12},{0x76,2},{0x77,6},{0x78,2},{0x79,2},{0x7A,4},{0x7B,2},
            {0x7C,8},{0x7D,10},{0x7E,4},{0xFB,6},{0xFC,2},{0xFD,2}
        };

        static readonly Random Rng = new Random();
        static int S16(byte[] d, int o) { return (short)U16(d, o); }
        static int U16(byte[] d, int o) { return o + 1 < d.Length ? d[o] | d[o + 1] << 8 : 0; }

        static int SectionStart(byte[] d, int section)
        {
            if (d == null) return -1;
            for (int o = 2; o + 4 <= d.Length; o += 4)
            {
                int id = U16(d, o);
                if (id == 0xFFFF) break;
                if (id == section) return U16(d, o + 2);
            }
            return -1;
        }

        static bool Compare(int mode, int a, int b)
        {
            switch (mode & 7)
            {
                case 0: return a == b;
                case 1: return a != b;
                case 2: return a >= b;
                case 3: return a <= b;
                case 4: return a > b;
                case 5: return a < b;
            }
            return false;
        }


        readonly MapScripts _scripts; readonly ScriptState st; readonly List<string> log;
        readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();
        readonly Stack<int[]> stack = new Stack<int[]>();
        byte[] d; int curId, p, steps;
        int speaker = 255;
        int[] menuOffsets; List<string> menuOptions; int[] awaiting;
        /// <summary>Digimon slots the script switched on (command 0x47: Digimon type, slot in the map's Digimon list, flag).</summary>
        public readonly List<int[]> Spawns = new List<int[]>();

        public ScriptRunner(MapScripts scripts, ScriptState state, int scriptId, int section, List<string> notes)
        {
            _scripts = scripts; st = state; log = notes;
            curId = scriptId; d = Get(scriptId);
            p = SectionStart(d, section);
        }

        byte[] Get(int id) { byte[] b; if (!cache.TryGetValue(id, out b)) { b = _scripts.GetScriptBytes(id); cache[id] = b; } return b; }
        void Note(string m) { if (log != null) log.Add(m); }
        int Long(int o) { return d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24; }

        /// <summary>After a menu action: the player picked option i (0 based).</summary>
        public void Choose(int i)
        {
            if (awaiting == null) return;
            if (i < 0) i = 0; if (i >= awaiting.Length) i = awaiting.Length - 1;
            p = awaiting[i]; awaiting = null;
        }

        /// <summary>The next thing the script wants (text box, menu, map change), or null when it has finished.</summary>
        public ScriptAction Next()
        {
            if (awaiting != null) return null;
            while (steps++ < 20000)
            {
                if (d == null || p < 0 || p >= d.Length) return null;
                int op = d[p];
                switch (op)
                {
                    case 0xFE: case 0xFF: p = -1; return null;
                    case 0x10:
                    {
                        int n = d[p + 1]; menuOffsets = new int[n];
                        for (int i = 0; i < n; i++) menuOffsets[i] = U16(d, p + 2 + i * 2);
                        menuOptions = null; p += 2 + 2 * n; continue;
                    }
                    case 0x12:
                    {
                        p += 2;
                        if (menuOffsets != null && menuOffsets.Length > 0)
                        {
                            var opts = menuOptions ?? new List<string>();
                            while (opts.Count < menuOffsets.Length) opts.Add("?");
                            awaiting = menuOffsets; menuOffsets = null; menuOptions = null;
                            return new ScriptAction { Options = opts };
                        }
                        continue;
                    }
                    case 0x13: { int o = U16(d, p + 2); stack.Push(new[] { curId, p + 4 }); p = o; continue; }
                    case 0x14:
                    {
                        int sid = U16(d, p + 2), sec = U16(d, p + 4);
                        var nd = Get(sid); int np = SectionStart(nd, sec);
                        if (np < 0) { Note("call to missing script " + sid + "/" + sec); p = -1; return null; }
                        stack.Push(new[] { curId, p + 6 }); curId = sid; d = nd; p = np; continue;
                    }
                    case 0x15:
                    {
                        if (stack.Count == 0) { p = -1; return null; }
                        var r = stack.Pop(); curId = r[0]; d = Get(curId); p = r[1]; continue;
                    }
                    case 0x16: p = U16(d, p + 2); continue;
                    case 0x17:
                    {
                        int sid = U16(d, p + 2), sec = U16(d, p + 4);
                        var nd = Get(sid); int np = SectionStart(nd, sec);
                        if (np < 0) { Note("jump to missing script " + sid + "/" + sec); p = -1; return null; }
                        curId = sid; d = nd; p = np; continue;
                    }
                    case 0x18:
                    {
                        int v = st.Vars[d[p + 1]]; int count = U16(d, p + 2);
                        if (count <= 0) { p = -1; return null; }
                        if (v >= count) v = count - 1;
                        p = U16(d, p + 4 + v * 2); continue;
                    }
                    case 0x19:
                    {
                        int q = p + 2; bool s1 = false; int jumpTo = -1; int guard = 0;
                        while (q + 1 < d.Length && guard++ < 64)
                        {
                            int c = d[q]; q += 2;
                            if (c == 0x19) break;
                            int t = c & 0x38; bool s0;
                            if (t == 0) { bool f = st.GetFlag(U16(d, q)); q += 2; s0 = (c & 7) == 0 ? f : !f; }
                            else if (t == 8) { s0 = Compare(c, st.Vars[d[q]], d[q + 1]); q += 2; }
                            else if (t == 32)
                            {
                                // game state: c&7 = what to test, then the thing tested, the compare mode (same modes as variables), and the value
                                int thing = d[q], mode = d[q + 1];
                                switch (c & 7)
                                {
                                    case 0: s0 = Compare(mode, st.GetStat(thing), thing == 9 ? (short)U16(d, q + 2) : U16(d, q + 2)); q += 4; break;   // partner value (happiness can be below 0)
                                    case 2: s0 = mode == 1 ? !st.Techs[thing] : st.Techs[thing]; q += 2; break;   // technique known
                                    case 4: s0 = Compare(mode, thing < st.Bag.Length ? st.Bag[thing] : 0, U16(d, q + 2)); q += 4; break;       // how many of an item
                                    case 5: s0 = Compare(mode, st.Money, Long(q + 2)); q += 6; break;   // Bits
                                    default: s0 = false; q += 2; Note("game-state condition " + (c & 7) + " treated as false"); break;
                                }
                            }
                            else if (t == 16 || t == 24)
                            {
                                int off = U16(d, q); q += 2;
                                if ((t == 16 && s1) || (t == 24 && !s1)) { jumpTo = off; break; }
                                continue;
                            }
                            else { Note("unknown condition type " + c); p = -1; return null; }
                            if ((c & 0x80) != 0) s1 = s1 && s0;
                            else if ((c & 0x40) != 0) s1 = s1 || s0;
                            else s1 = s0;
                        }
                        p = jumpTo >= 0 ? jumpTo : q;
                        continue;
                    }
                    case 0x1B: speaker = d[p + 1]; p += 2; continue;
                    case 0x1A:
                    {
                        var sb = new StringBuilder();
                        int j = p + 2;
                        while (j < d.Length)
                        {
                            int c = d[j];
                            if ((c >= 0x81 && c <= 0x9F) || (c >= 0xE0 && c <= 0xFC)) { if (j + 1 >= d.Length) break; sb.Append(MapScripts.Pair(c << 8 | d[j + 1])); j += 2; }
                            else if (c == 0x0D) { sb.Append('\n'); j += 2; }
                            else if (c == 0x01) j += 2;                       // colour
                            else if (c == 0x00) { j += 2; break; }
                            else if (c < 0x20) { sb.Append(st.CodeText(c, d[j + 1], speaker)); j += 2; } // a value the game fills in (names, numbers, time...)
                            else j++;
                        }
                        p = j;
                        string text = sb.ToString().Trim('\n', ' ');
                        if (menuOffsets != null && menuOptions == null) { menuOptions = new List<string>(text.Split('\n')); continue; }
                        if (text.Length > 0) return new ScriptAction { Text = text, Speaker = speaker };
                        continue;
                    }
                    case 0x65: st.Para.Condition &= ~d[p + 1]; p += 2; continue;      // remove conditions
                    case 0x69: st.Stats[6] = (ushort)Math.Max(1, st.Stats[6] - st.Stats[4] * st.Vars[d[p + 1]] / 100); p += 2; continue;   // damage: percent of max HP
                    case 0x7A: st.Vars[d[p + 3]] = st.Vars[d[p + 2]]; p += 4; continue;   // copy a variable
                    case 0x5A: if (st.OnSound != null) st.OnSound(d[p + 2], d[p + 3]); p += 4; continue;   // play a sound: bank, sound
                    case 0x5D: if (st.OnBgm != null) st.OnBgm(d[p + 1]); p += 2; continue;                 // music
                    case 0x5E: if (st.OnBgm != null) st.OnBgm(0xFF); p += 2; continue;                     // music off
                    case 0x1C: st.SetFlag(U16(d, p + 2), true); p += 4; continue;
                    case 0x1D: st.SetFlag(U16(d, p + 2), false); p += 4; continue;
                    case 0x1E: st.Vars[d[p + 2]] = d[p + 3]; p += 4; continue;
                    case 0x23: st.ItemSlots = d[p + 1]; p += 2; continue;
                    case 0x24: st.Vars[d[p + 2]] = (byte)Rng.Next(d[p + 3] + 1); p += 4; continue;   // random number 0..n into a variable
                    case 0x28: st.SetFlag(0, !st.AddItem(d[p + 2], d[p + 3])); p += 4; continue;     // give item; flag 0 = did not fit
                    case 0x29: st.RemoveItem(d[p + 2], d[p + 3]); p += 4; continue;                  // take item
                    case 0x2A: st.Money += Long(p + 2); p += 6; continue;
                    case 0x2B: st.Money = Math.Max(0, st.Money - Long(p + 2)); p += 6; continue;
                    case 0x2F: st.Cards[d[p + 1]] = true; p += 2; continue;   // a card for the Card List
                    case 0x2D: st.Techs[d[p + 1]] = true; p += 2; continue;                          // teach a technique
                    case 0x34: st.SetStat(d[p + 1], U16(d, p + 2)); p += 4; continue;
                    case 0x35: st.AddStat(d[p + 1], U16(d, p + 2)); p += 4; continue;
                    case 0x36: st.ReduceStat(d[p + 1], U16(d, p + 2)); p += 4; continue;
                    case 0x1F: st.Vars[d[p + 2]] = (byte)Math.Min(255, st.Vars[d[p + 2]] + d[p + 3]); p += 4; continue;
                    case 0x20: st.Vars[d[p + 2]] = (byte)Math.Max(0, st.Vars[d[p + 2]] - d[p + 3]); p += 4; continue;
                    case 0x47: Spawns.Add(new int[] { d[p + 1], d[p + 2], d[p + 3] }); p += 4; continue;
                    case 0x49: { var a = new ScriptAction { Cmd = op, Frames = d[p + 1] }; p += 2; return a; }   // callDigimonRoutine
                    case 0x64: { var a = new ScriptAction { Cmd = op, Actor = st.Vars[0xFE], Frames = d[p + 1] }; p += 2; return a; }   // var 254 = the actor (set just before)
                    // 0x27 closes the text window (it just follows the lines), 0x66 starts the fight: variables 250 (0xFA) = 1 for a scripted fight (no running away),
                    // 251-253 (0xFB-0xFD) = the enemies' actor numbers (0xFF = none; unset = the Digimon being talked to)
                    case 0x66: { var a = new ScriptAction { Cmd = 0x66, Actor = st.Vars[0xFB], Target = st.Vars[0xFC], Frames = st.Vars[0xFA] }; p += 2; return a; }
                    case 0x48: { var a = new ScriptAction { Cmd = 0x48, Actor = d[p + 1] }; p += 2; return a; }     // an actor leaves the map (Agumon walks away and joins the city)
                    case 0x4A: { p += 2; return new ScriptAction { Cmd = 0x4A }; }
                    case 0x4C: { var a = new ScriptAction { Cmd = op, Actor = d[p + 2], Target = d[p + 3] }; p += 4; return a; }
                    case 0x4D: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], Angle = U16(d, p + 2) }; p += 4; return a; }
                    case 0x4E: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 8; return a; }
                    case 0x4F: { var a = new ScriptAction { Cmd = op, Frames = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 6; return a; }
                    case 0x6C: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 8; return a; }
                    case 0x50: { var a = new ScriptAction { Cmd = op, Actor = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }              // camera to an actor
                    case 0x51: case 0x53: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], Target = d[p + 3] }; p += 4; return a; }   // walk to an actor (0x53 with the camera)
                    case 0x52: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 8; return a; }   // walk, the camera goes along
                    case 0x6D: case 0x6F: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], Target = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }   // glide to an actor
                    case 0x6E: { var a = new ScriptAction { Cmd = 0x52, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 8; return a; }
                    case 0x72: case 0x73: { var a = new ScriptAction { Cmd = 0x72, Actor = d[p + 1], X = S16(d, p + 2), Frames = d[p + 4] }; p += 6; return a; }   // move along one axis
                    case 0x58: { var a = new ScriptAction { Map = st.Vars[d[p + 1]], Exit = st.Vars[d[p + 1] + 1], Arrive = 255 }; p = -1; return a; }             // warp, map and exit in variables
                    case 0x56: { var a = new ScriptAction { Cmd = op, Actor = d[p + 2], Target = d[p + 3] }; p += 4; return a; }
                    case 0x67: { var a = new ScriptAction { Cmd = op, Frames = U16(d, p + 2) }; p += 4; return a; }
                    case 0x4B:
                    {
                        var a = new ScriptAction { Map = d[p + 1], Exit = d[p + 2], Arrive = d[p + 3] };
                        p = -1; return a;
                    }
                }
                int len;
                if (Len.TryGetValue(op, out len)) { p += len; continue; }
                Note("unknown command 0x" + op.ToString("X2") + " at " + p + " in script " + curId + " - stopped");
                p = -1; return null;
            }
            return null;
        }
    }
}
