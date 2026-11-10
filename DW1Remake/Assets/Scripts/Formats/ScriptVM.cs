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
    /// <summary>One Competition Battle registration (registerBattleData, SavegamePayload.battleRegistrationData; 40 slots, the last one is
    /// Machinedramon's from the game-clear save). Type 0 = empty. Moves = the partner's three chosen techniques (technique numbers here).</summary>
    [Serializable]
    public sealed class RegisteredDigimon { public int Type, Hp, Mp, Off, Def, Speed, Brain, Discipline; public string Name = ""; public int[] Moves = new int[3]; }

    public sealed class ScriptState
    {
        public RegisteredDigimon[] Registry = NewRegistry();
        public static RegisteredDigimon[] NewRegistry() { var r = new RegisteredDigimon[40]; for (int i = 0; i < r.Length; i++) r[i] = new RegisteredDigimon(); return r; }
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
        public Action<int, int, int> OnSpawnItem;
        public Action<int, int, int, int, int, int> OnSpawnChest;
        public Action<int, int, int, int> OnBlockTiles;
        public int Money;                         // bits; 0x2A adds, 0x2B takes away (the opening takes 50000 from 0, so the game starts at 0)
        public byte[] Bag = new byte[128];        // how many of each item the player carries (item number = position in the game's item list)
        public byte[] Stored = new byte[128];     // the Item Keeper's counts (script state smth[0x54 + item], tickItemKeeper)
        public byte[] Recycle = new byte[0x4E];   // the recycle shop's stock (script state smth[6 + n]: how many of RECYCLABLE_ITEMS[n]; handleItemLoss fills it)
        public byte[] RecyclableItems = new byte[0x4E];   // RECYCLABLE_ITEMS (exe 0x8012FE78), set by the room
        public byte[] CardOffers = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };   // the card shop's six cards (script state smth[0-5], the daily roll)
        /// <summary>dailyPStatTrigger (script_common.c), at every new day and when a game starts: the card shop gets six cards (1-0x40 except 4). The game's
        /// test for a repeat compares with the slot being filled (still 0xFF), not the earlier ones, so a card can come twice. Variables 0x1C-0x1F count up
        /// (unless 0xFF) and variable 2 loses its top bit.</summary>
        public void DailyTrigger(Func<int, int> random)
        {
            for (int i = 0; i < 6; i++) CardOffers[i] = 0xFF;
            for (int i = 0; i < 6; i++)
            {
                int r;
                do { r = random(0x40) + 1; } while (r == 4 || r == CardOffers[i]);
                CardOffers[i] = (byte)r;
            }
            for (int i = 0x1C; i < 0x20; i++) if (Vars[i] != 0xFF) Vars[i]++;
            if (Vars[2] != 0xFF) Vars[2] &= 0x7F;
        }
        public int RecycleId(int item) { for (int i = 0; i < RecyclableItems.Length; i++) if (RecyclableItems[i] == item) return i; return 0xFF; }    // getRecycleId
        public int BagKinds() { int n = 0; foreach (var b in Bag) if (b > 0) n++; return n; }
        /// <summary>giveItem(type, 0): whether n more of this item would fit, without adding them.</summary>
        public bool CanAddItem(int id, int n = 1)
        {
            if (id < 0 || id >= Bag.Length || n <= 0) return false;
            if (Bag[id] == 0 && ItemSlots > 0 && BagKinds() >= ItemSlots) return false;
            return Bag[id] + n <= 99;
        }
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
        public System.Func<int, string> TournamentNameOf, BgmNameOf;      // TOURNAMENT_NAMES (text code 0x12), read from the exe by the room
        public System.Func<int, string> SpeciesNameOf, ItemNameOf, TechNameOf;   // Digimon / item / technique names, read from the disc by the room
        public string CodeText(int code, int arg, int speaker = 255)
        {
            switch (code)
            {
                case 0x05: return PlayerName;    // the boy ("B, this isn't a dream" - Jijimon to the boy)
                case 0x06: return PartnerName;   // the partner ("Take Agumon and explore", "Are you alright, Agumon" - checked in about 40 lines)
                case 0x07: return SpeciesNameOf != null ? SpeciesNameOf(Vars[arg]) : "";
                case 0x08: return TechNameOf != null ? TechNameOf(Vars[arg]) : "";   // MOVE_NAMES[variable] (Forget text of lostAllLives)
                case 0x09: return ItemNameOf != null ? ItemNameOf(Vars[arg]) : "";
                case 0x0A: return GameDigits ? Game.ItemMenuView.Digits(Money, 6) : Money.ToString();     // intToStringSJIS(MONEY, 6, 0)
                case 0x10: return ShopVariable.ToString();                                                   // intToStringSJIS(SHOP_VARIABLE, 5, 1)
                case 0x04: return Vars[arg].ToString();                                                       // intToStringSJIS(readPStat(arg), 3, 1): the variable's value (0x25 STORE_DATE fills the date ones)
                case 0x0B: return GameDigits ? Game.ItemMenuView.Digits(Stats[14], 4) : Stats[14].ToString();   // intToStringSJIS(MERIT, 4, 0)
                case 0x11: return BgmNameOf != null ? BgmNameOf(Vars[arg]) : "";                              // BGM_TRACK_NAMES[readPStat(arg)] (exe 0x8012FEC8)
                case 0x12: return TournamentNameOf != null ? TournamentNameOf(Vars[arg]) : "";                  // TOURNAMENT_NAMES[readPStat(arg)] (exe 0x801300E0)
                default: return "";   // codes not understood yet are left out
            }
        }
        public Action<int> OnDamage;              // 0x69 dealt this much to the partner (the room shows the number)
        public int ShopVariable;                  // SHOP_VARIABLE (shown by text code 0x10): a baby partner's shop discount, merit values, Monochromon's total profit (built-ins 0x1E / 0x1F / 0x38)
        public bool GameDigits;                   // while set, code 0x0A gives the game's padded number (the shop's Bits box draws it with drawString's spaces)
        public int ItemSlots;                     // 0x23: how many items the bag holds (10 at the start, 20/30 with each Dimensional Key Chain)
        /// <summary>INVENTORY.types: the bag's slots in their order (0xFF = an empty slot); the counts stay in Bag. SyncSlots keeps the two in step.</summary>
        public byte[] Slots = NewSlots();
        static byte[] NewSlots() { var a = new byte[30]; for (int i = 0; i < a.Length; i++) a[i] = 0xFF; return a; }

        /// <summary>A slot whose item ran out becomes empty (removeItem); an item in the bag without a slot takes the first empty one (giveItem).</summary>
        public void SyncSlots()
        {
            int size = Math.Min(Slots.Length, ItemSlots > 0 ? ItemSlots : 10);
            for (int i = 0; i < Slots.Length; i++) if (Slots[i] != 0xFF && (Slots[i] >= Bag.Length || Bag[Slots[i]] == 0)) Slots[i] = 0xFF;
            for (int id = 0; id < Bag.Length; id++)
            {
                if (Bag[id] == 0 || Array.IndexOf(Slots, (byte)id) >= 0) continue;
                for (int i = 0; i < size; i++) if (Slots[i] == 0xFF) { Slots[i] = (byte)id; break; }
            }
        }

        public bool[] Cards = new bool[256];      // cards collected (command 0x2F n gives card n)
        public byte[] CardCount = new byte[256];  // getCardAmount: how many of each card, 0-9 (0x2F adds one, 0x30 takes one)
        public int SectionOnExit = -1;            // 0x7B: the section to run after the next map change
        public byte Var0Reset;                    // MAIN_D_80134FE7 (built-in 0x19 sets it): variable 0 gets it when a script ends, on a script map change and on 0x7B
        // what the room tells the scripts (script_interp.c): the map, the partner's type, a Digimon's DIGIMON_DATA type (+28: 1 Data, 2 Vaccine, 3 Virus), the date
        public Func<int> MapIdNow, PartnerTypeNow;
        public Func<int, int> AttributeOf;
        public Func<int[]> DateNow;                     // year, day (0-29), hour, minute
        public Action<int, int, int, int> AdvanceDate;  // 0x37: to that date (skipHours when it is later)

        // dateToSeconds / secondsToDate (script_value.c): minutes since year 0, a year = 30 days
        public static int DateToMinutes(int y, int d, int h, int m) { return m + h * 60 + d * 24 * 60 + y * 30 * 24 * 60; }
        public static void MinutesToDate(int t, out int y, out int d, out int h, out int m)
        {
            if (t >= 255 * 43200) t = 59 + 23 * 60 + 14 * 1440 + 25 * 43200;
            m = t % 60; t /= 60; h = t % 24; t /= 24; d = t % 30; y = t / 30;
        }
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
        public bool Selection;               // the options are the whole box (scriptShowSelection), no question text above them
        public int Speaker = 255;            // who talks (0xFD = the player, 0xFC = the partner, 255 = nobody, otherwise a map Digimon's script number)
        public int Cmd = -1;                 // cutscene command (0x4C face, 0x4D angle, 0x4E walk, 0x4F camera, 0x6C place, 0x56 animation, 0x67 wait, 0x4A wait for all)
        public int Actor, Target, Frames;    // Actor: 0xFD = the player, 0xFC = the partner, otherwise the map Digimon whose script number matches
        public float X, Z, Angle;            // game units / game angle (4096 = full turn)
        public float Y;                      // game units (0x7D: the height of the point)
        public int Anim;                     // 0x4E / 0x51-0x53: 0 walk, 1 run (scriptSetEntityWalking)
        public int Map = -1, Exit, Arrive = 255;   // map change (Arrive = script section to run on arrival, 255 = none)
        public bool Custom;                  // the text goes into the box 0x26 set up (MAIN_D_80135000 == 2: showTextbox(0, 0xFF), no name)
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
        int boxMode;   // MAIN_D_80135000: 2 after 0x26 (SET_TEXTBOX_SIZE), 1 after 0x10 (SET_SELECTION), 0 after 0x1B (SET_DIALOG_OWNER)
        int[] menuOffsets; List<string> menuOptions; int[] awaiting; int cancelAt = -1;
        /// <summary>Digimon slots the script switched on (command 0x47: Digimon type, slot in the map's Digimon list, flag).</summary>
        public readonly List<int[]> Spawns = new List<int[]>();

        public ScriptRunner(MapScripts scripts, ScriptState state, int scriptId, int section, List<string> notes)
        {
            _scripts = scripts; st = state; log = notes;
            curId = scriptId; d = Get(scriptId);
            p = SectionStart(d, section);
        }

        /// <summary>Text colour code n (string control 01 n) kept in decoded text as this character plus n; GameFont draws in that colour, Plain drops it.</summary>
        public const char ColourMark = (char)0xE000;

        /// <summary>The text without its colour codes (for text not drawn with the game font).</summary>
        public static string Plain(string s)
        {
            if (s == null || !HasMark(s)) return s;
            var sb = new StringBuilder();
            foreach (char ch in s) if (ch < ColourMark || ch > ColourMark + 15) sb.Append(ch);
            return sb.ToString();
        }
        static bool HasMark(string s) { foreach (char ch in s) if (ch >= ColourMark && ch <= ColourMark + 15) return true; return false; }

        // the text of a 0x1A command starting at j; 'end' = just after it; colours: keep the 01 n codes (ColourMark + n)
        static string Decode(byte[] d, int j, ScriptState st, int speaker, out int end, bool colours = false)
        {
            var sb = new StringBuilder();
            // showTextbox (US 0x80101688, machine code): the text ends only where a line break (0D 00) is followed by 00 00. A 00 00 anywhere
            // else is copied into the line like a character and the line's drawing stops at it, so the rest of that line is not seen and the
            // text goes on. GKYO02's Gekomon ("You can collect" 00 00 ...) sets flag 0x7A after such a line (the merit shop needs it).
            bool afterBreak = false, hidden = false;
            while (j < d.Length)
            {
                int c = d[j];
                bool wasBreak = afterBreak; afterBreak = false;
                if ((c >= 0x81 && c <= 0x9F) || (c >= 0xE0 && c <= 0xFC)) { if (j + 1 >= d.Length) break; if (!hidden) sb.Append(MapScripts.Pair(c << 8 | d[j + 1])); j += 2; }
                else if (c == 0x0D) { sb.Append('\n'); j += 2; afterBreak = true; hidden = false; }
                else if (c == 0x00) { j += 2; if (wasBreak) break; hidden = true; }
                else if (hidden) j += c < 0x20 ? 2 : 1;
                else if (c == 0x01) { if (colours) sb.Append((char)(ColourMark + (d[j + 1] & 15))); j += 2; }      // setTextColor
                else if (c < 0x20) { sb.Append(st.CodeText(c, d[j + 1], speaker)); j += 2; } // a value the game fills in (names, numbers, time...)
                else j++;
            }
            end = j;
            return sb.ToString().Trim('\n', ' ');
        }

        /// <summary>The text that starts at byte j of a script (after its 1A 00), as showTextbox reads it (the cup data of the arena scripts).</summary>
        public static string TextAt(byte[] d, int j, ScriptState st, int speaker, bool colours = false)
        {
            int end;
            return d != null && j >= 0 && j < d.Length ? Decode(d, j, st, speaker, out end, colours) : null;
        }

        /// <summary>Whether script 'scriptId' has a section with that number.</summary>
        public static bool HasSection(MapScripts scripts, int scriptId, int section)
        {
            return SectionStart(scripts.GetScriptBytes(scriptId), section) >= 0;
        }

        /// <summary>resolveMapHeadEntry: text idx of script 0's section (a table of u16 offsets, 4 bytes apart), as showMapHeadTextbox shows it.</summary>
        public static string MapHeadText(MapScripts scripts, ScriptState st, int section, int idx, int speaker, bool colours = false)
        {
            byte[] s = scripts.GetScriptBytes(MapScripts.DgZero);
            int at = SectionStart(s, section);
            if (at < 0) return null;
            int off = U16(s, at + idx * 4 + 2), end;
            return off + 2 < s.Length ? Decode(s, off + 2, st, speaker, out end, colours) : null;
        }

        byte[] Get(int id) { byte[] b; if (!cache.TryGetValue(id, out b)) { b = _scripts.GetScriptBytes(id); cache[id] = b; } return b; }
        void Note(string m) { if (log != null) log.Add(m); }
        int Long(int o) { return d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24; }

        /// <summary>After a menu action: the player picked option i (0 based).</summary>
        public void Choose(int i)
        {
            cancelAt = -1;
            if (awaiting == null) return;
            if (i < 0) i = 0; if (i >= awaiting.Length) i = awaiting.Length - 1;
            p = awaiting[i]; awaiting = null;
        }

        /// <summary>tickSelectionDialogue, Circle with flag 0x31 off: no option is taken, the script goes on after the selection.</summary>
        public void CancelChoice()
        {
            if (awaiting == null || cancelAt < 0) return;
            p = cancelAt; awaiting = null; cancelAt = -1;
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
                        boxMode = 1;
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
                                    case 1: s0 = Compare(mode, st.CardCount[thing], U16(d, q + 2)); q += 4; break;   // scriptCompareCard: how many of a card
                                    case 2: s0 = mode != 0 ? !st.Techs[thing] : st.Techs[thing]; q += 2; break;   // scriptCompareMove: technique known (non-zero = not)
                                    case 3: s0 = mode != 0 ? (st.Para.Condition & thing) == 0 : (st.Para.Condition & thing) != 0; q += 2; break;   // scriptCompareCondition: a condition bit
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
                    case 0x1B: speaker = d[p + 1]; boxMode = 0; p += 2; continue;
                    // SET_TEXTBOX_SIZE (scriptSetTextboxSize): origin id, columns, rows; a box of its own size in the middle of the screen
                    case 0x26: { boxMode = 2; var a = new ScriptAction { Cmd = 0x26, Actor = d[p + 1], Target = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }
                    case 0x1A:
                    {
                        int j;
                        string text = Decode(d, p + 2, st, speaker, out j, true);
                        p = j;
                        // scriptShowSelection (command 0x10): the text right after the offsets is the list of options, shown at once as the selection box.
                        // Circle (when flag 0x31 is off) goes on 2 bytes after it (endPointer: past the 12 00 most selections have there)
                        if (menuOffsets != null)
                        {
                            var opts = new List<string>(text.Split('\n'));
                            while (opts.Count < menuOffsets.Length) opts.Add("?");
                            awaiting = menuOffsets; menuOffsets = null; menuOptions = null; cancelAt = j + 2;
                            return new ScriptAction { Options = opts, Speaker = speaker, Selection = true };
                        }
                        // SHOW_TEXTBOX: with MAIN_D_80135000 == 2 the text goes into 0x26's box with no speaker (showTextbox(0, 0xFF))
                        if (text.Length > 0) return boxMode == 2 ? new ScriptAction { Text = text, Speaker = 0xFF, Custom = true } : new ScriptAction { Text = text, Speaker = speaker };
                        continue;
                    }
                    case 0x74: if (st.OnSpawnItem != null) st.OnSpawnItem(d[p + 1], S16(d, p + 2), S16(d, p + 4)); p += 6; continue;
                    case 0x75: if (st.OnSpawnChest != null) st.OnSpawnChest(d[p + 1], S16(d, p + 2), S16(d, p + 4), S16(d, p + 6), S16(d, p + 8), U16(d, p + 10)); p += 12; continue;
                    case 0x7C: if (st.OnBlockTiles != null) st.OnBlockTiles(S16(d, p + 2), S16(d, p + 4), d[p + 6], d[p + 7]); p += 8; continue;
                    case 0x65: st.Para.Condition &= ~d[p + 1]; p += 2; continue;      // remove conditions
                    case 0x69:
                    {
                        // SCRIPT_OP_DEAL_DAMAGE: max HP x variable / 100, never below 1 HP left; addEntityText puts the number over the partner
                        int dmg = st.Stats[4] * st.Vars[d[p + 1]] / 100;
                        if (st.Stats[6] - (short)dmg < 1) dmg = st.Stats[6] - 1;
                        st.Stats[6] = (ushort)(st.Stats[6] - dmg);
                        if (st.OnDamage != null) st.OnDamage(dmg);
                        p += 2; continue;
                    }
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
                    case 0x2A: st.Money = Math.Min(st.Money + Long(p + 2), 999999); p += 6; continue;      // ADD_MONEY, at most 999999
                    case 0x2B: st.Money = Math.Max(0, st.Money - Long(p + 2)); p += 6; continue;
                    case 0x2F: if (st.CardCount[d[p + 1]] < 9) st.CardCount[d[p + 1]]++; st.Cards[d[p + 1]] = true; p += 2; continue;   // a card for the Card List (up to 9)
                    case 0x30: if (st.CardCount[d[p + 1]] > 0) st.CardCount[d[p + 1]]--; st.Cards[d[p + 1]] = st.CardCount[d[p + 1]] > 0; p += 2; continue;   // take a card
                    case 0x31: st.Stats[14] = (ushort)Math.Min(9999, U16(d, p + 2)); p += 4; continue;                   // merit (prosperity points): set
                    case 0x32: st.Stats[14] = (ushort)Math.Min(9999, st.Stats[14] + U16(d, p + 2)); p += 4; continue;    // add
                    case 0x33: st.Stats[14] = 0; p += 4; continue;          // "reduce": the game stores minus the value and then clamps it at 0
                    case 0x21: st.Vars[d[p + 1]] = (byte)(st.MapIdNow != null ? st.MapIdNow() : 0); p += 2; continue;            // STORE_MAP_ID
                    case 0x22: st.Vars[d[p + 1]] = (byte)(st.PartnerTypeNow != null ? st.PartnerTypeNow() : 0); p += 2; continue; // STORE_DIGIMON_TYPE
                    case 0x3F: st.Vars[d[p + 3]] = (byte)(st.AttributeOf != null ? st.AttributeOf(st.Vars[d[p + 2]]) : 0); p += 4; continue;   // STORE_DIGIMON_VALUE: DIGIMON_DATA[var].type
                    case 0x25:      // STORE_DATE: year, day, hour, minute into four variables
                    {
                        var dt = st.DateNow != null ? st.DateNow() : new[] { 0, 0, 0, 0 };
                        for (int i = 0; i < 4; i++) st.Vars[(d[p + 1] + i) & 0xFF] = (byte)dt[i];
                        p += 2; continue;
                    }
                    case 0x2C:      // COMPARE_DATE: the date in 4 variables against a fixed date; sets or clears a flag
                    {
                        int v = d[p + 1], flag = U16(d, p + 2), mode = d[p + 4];
                        int now = ScriptState.DateToMinutes(st.Vars[v], st.Vars[(v + 1) & 0xFF], st.Vars[(v + 2) & 0xFF], st.Vars[(v + 3) & 0xFF]);
                        int at = ScriptState.DateToMinutes(d[p + 5], d[p + 6], d[p + 7], d[p + 8]);
                        st.SetFlag(flag, Compare(mode, now, at));
                        p += 10; continue;
                    }
                    case 0x37:      // ADVANCE_TO_DATE_AT: the clock goes to the date in 4 variables
                    {
                        int v = d[p + 1];
                        if (st.AdvanceDate != null) st.AdvanceDate(st.Vars[v], st.Vars[(v + 1) & 0xFF], st.Vars[(v + 2) & 0xFF], st.Vars[(v + 3) & 0xFF]);
                        p += 2; continue;
                    }
                    case 0x38: case 0x39:      // ADD_MINUTES_TO_DATE_AT (0x39 takes them away, not below 0)
                    {
                        int v = d[p + 1], n = Long(p + 2);
                        int t = ScriptState.DateToMinutes(st.Vars[v], st.Vars[(v + 1) & 0xFF], st.Vars[(v + 2) & 0xFF], st.Vars[(v + 3) & 0xFF]);
                        t = op == 0x38 ? t + n : Math.Max(0, t - n);
                        int y, dd, h, m; ScriptState.MinutesToDate(t, out y, out dd, out h, out m);
                        st.Vars[v] = (byte)y; st.Vars[(v + 1) & 0xFF] = (byte)dd; st.Vars[(v + 2) & 0xFF] = (byte)h; st.Vars[(v + 3) & 0xFF] = (byte)m;
                        p += 6; continue;
                    }
                    case 0x7B: st.SectionOnExit = d[p + 1]; st.Vars[0] = st.Var0Reset; p += 2; continue;      // SECTION_ON_EXIT, then writePStat(0, MAIN_D_80134FE7)
                    case 0x6A: { var a = new ScriptAction { Cmd = 0x6A, Actor = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }   // SET_AUTOTALK: actor, 1 = it talks when the boy touches it
                    case 0x57: { var a = new ScriptAction { Cmd = 0x57, Actor = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }   // SET_OBJ_VISIBILITY: map piece, flag (1 = hidden)
                    // ROTATE_3D_OBJECT: door slot, _, signed direction (SCRIPT_MOVEMENT[11], tickRotateDoor)
                    case 0x70: { var a = new ScriptAction { Cmd = 0x70, Actor = d[p + 1], Frames = (sbyte)d[p + 3] }; p += 4; return a; }
                    // MOVE_OBJECT_TO: slot, map piece, (speed, unused), signed steps, _, x, y on the painted map (SCRIPT_MOVEMENT[12 + slot], tickMoveObjectTo)
                    case 0x71: { var a = new ScriptAction { Cmd = 0x71, Actor = d[p + 1], Target = d[p + 2], Frames = (sbyte)d[p + 4], X = S16(d, p + 6), Z = S16(d, p + 8) }; p += 10; return a; }
                    case 0x76: { p += 2; return new ScriptAction { Cmd = 0x76 }; }                                                   // SPAWN_BOULDER
                    case 0x77: { var a = new ScriptAction { Cmd = 0x77, X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 6; return a; }   // MOVE_BOULDER, then the script stops for this frame
                    case 0x78: { p += 2; return new ScriptAction { Cmd = 0x78 }; }                                                   // DESPAWN_BOULDER
                    // SPAWN_SPRITE_AT_LOCATION: count, x, y, z, first map piece
                    case 0x7D: { var a = new ScriptAction { Cmd = 0x7D, Frames = d[p + 1], X = S16(d, p + 2), Y = S16(d, p + 4), Z = S16(d, p + 6), Target = S16(d, p + 8) }; p += 10; return a; }
                    // SPAWN_SPRITE_AT_ENTITY: actor, its body part (model node), map piece
                    case 0x7E: { var a = new ScriptAction { Cmd = 0x7E, Actor = d[p + 1], Frames = (sbyte)d[p + 2], Target = d[p + 3] }; p += 4; return a; }
                    case 0x54: { var a = new ScriptAction { Cmd = 0x54, Actor = d[p + 1] }; p += 2; return a; }                      // RESET_ENTITY_ORIGIN
                    // scriptInstructionFBtoFF 0xFB: _, u16 script, u16 map (CURRENT_SCRIPT_ID, CURRENT_SCREEN_ID), then loadMap; the room does it
                    case 0xFB: { var a = new ScriptAction { Cmd = 0xFB, Actor = U16(d, p + 2), Frames = U16(d, p + 4) }; p += 6; return a; }
                    case 0x2D: st.Techs[d[p + 1]] = true; p += 2; continue;                          // teach a technique
                    case 0x34: st.SetStat(d[p + 1], U16(d, p + 2)); p += 4; continue;
                    case 0x35: st.AddStat(d[p + 1], U16(d, p + 2)); p += 4; continue;
                    case 0x36: st.ReduceStat(d[p + 1], U16(d, p + 2)); p += 4; continue;
                    case 0x1F: st.Vars[d[p + 2]] = (byte)Math.Min(255, st.Vars[d[p + 2]] + d[p + 3]); p += 4; continue;
                    case 0x20: st.Vars[d[p + 2]] = (byte)Math.Max(0, st.Vars[d[p + 2]] - d[p + 3]); p += 4; continue;
                    // scriptSetDigimon: type, slot, autotalk; kept for the map setup, and returned so a running script can switch the Digimon on
                    case 0x47: { Spawns.Add(new int[] { d[p + 1], d[p + 2], d[p + 3] }); var a = new ScriptAction { Cmd = 0x47, Actor = d[p + 1], Target = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }
                    case 0x49: { var a = new ScriptAction { Cmd = op, Frames = d[p + 1] }; p += 2; return a; }   // callDigimonRoutine
                    case 0x64: { var a = new ScriptAction { Cmd = op, Actor = st.Vars[0xFE], Frames = d[p + 1] }; p += 2; return a; }   // var 254 = the actor (set just before)
                    // 0x27 closes the text window (it just follows the lines), 0x66 starts the fight: variables 250 (0xFA) = 1 for a scripted fight (no running away),
                    // 251-253 (0xFB-0xFD) = the enemies' actor numbers (0xFF = none; unset = the Digimon being talked to)
                    case 0x66: { var a = new ScriptAction { Cmd = 0x66, Actor = st.Vars[0xFB], Target = st.Vars[0xFC], Frames = st.Vars[0xFA] }; p += 2; return a; }
                    case 0x48: { var a = new ScriptAction { Cmd = 0x48, Actor = d[p + 1] }; p += 2; return a; }     // an actor leaves the map (Agumon walks away and joins the city)
                    case 0x4A: { var a = new ScriptAction { Cmd = 0x4A, Actor = d[p + 1] }; p += 2; return a; }    // WAIT_FOR_ENTITY: who (0xFF all, 0xC8-0xD6 other slots)
                    case 0x27: { var a = new ScriptAction { Cmd = 0x27, Actor = d[p + 1] }; p += 2; return a; }    // FADEOUT_HUD: closeBox(n), then the script waits a frame
                    case 0x6B: { p++; while (p < d.Length && d[p] != 0xFF) p++; p++; continue; }                   // DATA: a table built-ins read; up to its 0xFF
                    case 0x4C: { var a = new ScriptAction { Cmd = op, Actor = d[p + 2], Target = d[p + 3] }; p += 4; return a; }
                    case 0x4D: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], Angle = U16(d, p + 2) }; p += 4; return a; }
                    case 0x4E: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4), Anim = d[p + 6] }; p += 8; return a; }
                    case 0x4F: { var a = new ScriptAction { Cmd = op, Frames = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 6; return a; }
                    case 0x6C: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4), Frames = d[p + 6] }; p += 8; return a; }   // ENTITY_MOVE_TO: id, x, z, speed (frames)
                    case 0x50: { var a = new ScriptAction { Cmd = op, Actor = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }              // camera to an actor
                    case 0x51: case 0x53: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], Anim = d[p + 2], Target = d[p + 3] }; p += 4; return a; }   // walk to an actor (0x53 with the camera)
                    case 0x52: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4), Anim = d[p + 6] }; p += 8; return a; }   // walk, the camera goes along
                    case 0x6D: case 0x6F: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], Target = d[p + 2], Frames = d[p + 3] }; p += 4; return a; }   // glide to an actor
                    case 0x6E: { var a = new ScriptAction { Cmd = 0x52, Actor = d[p + 1], X = S16(d, p + 2), Z = S16(d, p + 4) }; p += 8; return a; }
                    case 0x72: case 0x73: { var a = new ScriptAction { Cmd = op, Actor = d[p + 1], X = S16(d, p + 2), Target = d[p + 4], Frames = d[p + 5] }; p += 6; return a; }   // ENTITY_MOVE_TO_AXIS (0x73 with the camera): id, s16 value, axis, speed
                    case 0x58: { var a = new ScriptAction { Map = st.Vars[d[p + 1]], Exit = st.Vars[d[p + 1] + 1], Arrive = 255 }; p = -1; return a; }             // warp, map and exit in variables
                    case 0x56: { var a = new ScriptAction { Cmd = op, Actor = d[p + 2], Target = d[p + 3] }; p += 4; return a; }
                    case 0x67: { var a = new ScriptAction { Cmd = op, Frames = U16(d, p + 2) }; p += 4; return a; }
                    case 0x4B:
                    {
                        // Ours (user's request 2026-11-09): OGRE10 after the OGRE11 fight (script 136 section 6). Whamon says "I'm going straight to the
                        // city. I'll give you a ride if you want.", then "Whamon joins the city." and 4B 87 00 FF sends you to map 135, the Freezeland
                        // shore. Here the script goes on at offset 3656 instead: the same script's own ride to the city (section 81, "Going back to the
                        // city"), which picks the city's dock map from the flags
                        if (curId == 136 && p == 3532 && d[p + 1] == 0x87) { p = 3656; continue; }
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
