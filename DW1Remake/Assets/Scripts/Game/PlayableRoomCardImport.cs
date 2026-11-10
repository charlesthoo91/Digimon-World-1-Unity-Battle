using System.Collections;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (MODS > IMPORT SAVE): a PS1 memory card file (an emulator's .mcd / .mcr / .mc / .srm, a DexDrive .gme, a PSP .vmp, or one
    /// save as .mcs) turned into a save of this game. Each Digimon World save is one 8 KB block "BASLUS-01032DMR" + slot digit. It is read by
    /// the layout the game itself writes (SaveFile and SavegamePayload in main_menu.c; writeSavegame / loadSavegame): a 0x200 header, then
    /// the save twice (0xF00 each); the first copy is used if its checksum is right, else the second, else none (the game's Continue).
    /// The payload offsets below are the ones found on real cards (the decompilation's struct is 0x18 short before the checksum).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        sealed class CardSave { public int Slot; public SaveGameData Save; }

        const int CardSize = 0x20000, BlockSize = 0x2000, SaveCopy = 0xF00;

        // the 128 KB card inside the file: raw (MC at 0), DexDrive .gme (0xF40 header), PSP .vmp (0x80 header)
        static byte[] CardImage(byte[] f)
        {
            foreach (int at in new[] { 0, 0xF40, 0x80 })
            {
                if (f.Length < at + CardSize || f[at] != 'M' || f[at + 1] != 'C') continue;
                var c = new byte[CardSize];
                System.Array.Copy(f, at, c, 0, CardSize);
                return c;
            }
            return null;
        }

        static string FrameName(byte[] f, int at)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 20 && at + 0x0A + i < f.Length && f[at + 0x0A + i] != 0; i++) sb.Append((char)f[at + 0x0A + i]);
            return sb.ToString();
        }

        const string SaveFilePrefix = "BASLUS-01032DMR";       // SAVE_FILE_NAME (US)

        /// <summary>Every playable Digimon World save in the file (null = not a memory card file).</summary>
        List<CardSave> ReadCardFile(byte[] f)
        {
            var blocks = new List<KeyValuePair<int, int>>();     // (slot digit, block start in 'data')
            byte[] data = CardImage(f);
            if (data != null)
            {
                // the directory: frame n (0x80 bytes) for block n; 0x51 = the first block of a file
                for (int b = 1; b < 16; b++)
                {
                    int fr = 0x80 * b;
                    if (data[fr] != 0x51) continue;
                    string name = FrameName(data, fr);
                    if (name.Length == SaveFilePrefix.Length + 1 && name.StartsWith(SaveFilePrefix)) blocks.Add(new KeyValuePair<int, int>(HexDigit(name[name.Length - 1]), BlockSize * b));
                }
            }
            else if (f.Length >= 0x80 + BlockSize && f[0] == 0x51 && FrameName(f, 0).StartsWith(SaveFilePrefix))
            {
                // .mcs: one directory frame, then the block
                data = f; string name = FrameName(f, 0);
                blocks.Add(new KeyValuePair<int, int>(HexDigit(name[name.Length - 1]), 0x80));
            }
            else return null;
            var list = new List<CardSave>();
            foreach (var kv in blocks)
            {
                int at = -1;
                for (int copy = 0; copy < 2 && at < 0; copy++) if (ChecksumOk(data, kv.Value + 0x200 + SaveCopy * copy)) at = kv.Value + 0x200 + SaveCopy * copy;
                if (at < 0) { Debug.LogWarning("[card] save " + kv.Key + ": both copies have a wrong checksum, skipped (as the game's Continue)"); continue; }
                if (I32(data, at) == 0) continue;       // mainState 0: the file NEW GAME made before any save, nothing played yet
                try { list.Add(new CardSave { Slot = kv.Key, Save = FromCard(data, at) }); }
                catch (System.Exception e) { Debug.LogWarning("[card] save " + kv.Key + ": " + e.Message); }
            }
            return list;
        }

        static int HexDigit(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0; }
        static int I32(byte[] b, int at) { return System.BitConverter.ToInt32(b, at); }
        static int I16(byte[] b, int at) { return System.BitConverter.ToInt16(b, at); }

        // createSavegameChecksum: the byte sum of the first 0x4E4 bytes plus the 40 registrations (0xA00), kept at +0x4FC
        static bool ChecksumOk(byte[] b, int s)
        {
            if (s + SaveCopy > b.Length) return false;
            int sum = 0;
            for (int i = 0; i < 0x4E4; i++) sum += b[s + i];
            for (int i = 0; i < 0xA00; i++) sum += b[s + 0x500 + i];
            return sum == I32(b, s + 0x4FC);
        }

        // names on the card are SJIS: full-width letters, digits and space become ours; plain ASCII stays
        static string CardText(byte[] b, int at, int len)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++)
            {
                int c = b[at + i];
                if (c == 0) break;
                if (c < 0x80) { sb.Append((char)c); continue; }
                if (i + 1 >= len) break;
                int d = b[at + ++i];
                if (c == 0x82 && d >= 0x4F && d <= 0x58) sb.Append((char)('0' + d - 0x4F));
                else if (c == 0x82 && d >= 0x60 && d <= 0x79) sb.Append((char)('A' + d - 0x60));
                else if (c == 0x82 && d >= 0x81 && d <= 0x9A) sb.Append((char)('a' + d - 0x81));
                else if (c == 0x81 && d == 0x40) sb.Append(' ');
            }
            return sb.ToString();
        }

        // a technique number from an animation number (0x2E + its place in the Digimon's list; 0xFF = none)
        int TechOfAnim(int type, int anim)
        {
            if (anim < 0x2E || anim >= 0x2E + 16) return -1;
            var info = DigimonStatsCatalog.Get(_disc, type);
            int t = info != null ? info.RawMoves[anim - 0x2E] : 0xFF;
            return t == 0xFF ? -1 : t;
        }

        // loadSavegame, field by field, into our save
        SaveGameData FromCard(byte[] b, int s)
        {
            var d = new SaveGameData();
            var st = new ScriptState();
            d.MainState = I32(b, s);
            // tamerPosition (game units) -> Unity (x, -y, z) x 0.01
            d.X = I32(b, s + 4) * 0.01f; d.Y = -I32(b, s + 8) * 0.01f; d.Z = I32(b, s + 12) * 0.01f;
            var para = new PartnerPara();
            for (int i = 0; i < 100; i++)
            {
                int p = s + 0x24 + 4 * i;       // worldPoop: map, x, y, size
                para.Piles[i] = new PoopPile { Map = b[p], X = b[p + 1], Y = b[p + 2], Size = (sbyte)b[p + 3] };
            }
            d.Money = I32(b, s + 0x1B4);
            d.PartnerType = I32(b, s + 0x1B8);
            for (int t = 0; t < 128; t++) st.Techs[t] = (b[s + 0x1BC + (t >> 3)] & (1 << (t & 7))) != 0;      // learnedMoves (hasMove)
            int year = I16(b, s + 0x1D0), day = I16(b, s + 0x1D2), hour = I16(b, s + 0x1D4), minute = I16(b, s + 0x1D6);
            d.Day = year * 30 + day;
            d.Clock = hour + minute / 60f;
            d.PlayMinutes = (ushort)I16(b, s + 0x1DA) * 60 + (ushort)I16(b, s + 0x1DC);
            // partnerPara: condition, then 64 halfwords (params.h)
            int q = s + 0x1E4;
            System.Func<int, int> h = k => I16(b, q + 2 * k);
            para.Condition = I32(b, s + 0x1E0);
            para.SleepyHour = h(0); para.SleepyMinute = h(1); para.WakeupHour = h(2); para.WakeupMinute = h(3);
            para.HoursAwakeDefault = h(4); para.HoursAsleepDefault = h(5); para.TimeAwakeToday = h(6); para.SicknessCounter = h(7);
            para.MissedSleepHours = h(8); para.TirednessSleepTimer = h(9); para.PoopLevel = h(10); para.Virus = h(13); para.PoopingTimer = h(14);
            para.Tiredness = h(15); para.SubTiredness = h(16); para.TirednessHungerTimer = h(17);
            int discipline = h(18), happiness = h(19);
            para.TimesBeingSick = h(22); para.SicknessTries = h(23); para.AreaEffectTimer = h(24); para.SicknessTimer = h(25); para.InjuryTimer = h(26);
            para.NextHungerHour = h(27); para.Energy = h(28); para.FoodLevel = h(29); para.StarvationTimer = h(30);
            int weight = h(31);
            para.RefusedFavFood = h(32); para.EmptyStomachTimer = h(33); para.Lifetime = h(34);
            int age = h(35);
            para.TrainBoostFlag = h(36); para.TrainBoostValue = h(37); para.TrainBoostTimer = h(38);
            para.CareMistakes = h(39); para.Battles = h(40); para.EvoTimer = h(41);
            para.FishCaught = h(45);
            // upgrade counters HP, MP, Off, (the bugged Brains one), Def, Speed, Brains
            para.Upgrades[0] = h(49); para.Upgrades[1] = h(50); para.Upgrades[2] = h(51); para.Upgrades[3] = h(53); para.Upgrades[4] = h(54); para.Upgrades[5] = h(55);
            para.SukaHp = h(57); para.SukaMp = h(58); para.SukaOff = h(59); para.SukaDef = h(60); para.SukaSpeed = h(61); para.SukaBrain = h(62);
            // merit, battles started, fled, tournaments won, tournament wins, tournaments lost = script stats 14-19
            for (int i = 0; i < 6; i++) st.Stats[14 + i] = (ushort)Mathf.Clamp(I16(b, s + 0x264 + 2 * i), 0, 9999);
            // stats: off, def, speed, brains, movesPrio[4], moves[4] (animations), max HP, max MP; then current HP, MP
            for (int i = 0; i < 4; i++) st.Stats[i] = (ushort)Mathf.Clamp(I16(b, s + 0x270 + 2 * i), 0, 9999);
            st.Stats[4] = (ushort)Mathf.Clamp(I16(b, s + 0x280), 0, 9999); st.Stats[5] = (ushort)Mathf.Clamp(I16(b, s + 0x282), 0, 9999);
            st.Stats[6] = (ushort)Mathf.Clamp(I16(b, s + 0x284), 0, 9999); st.Stats[7] = (ushort)Mathf.Clamp(I16(b, s + 0x286), 0, 9999);
            var set = new List<int>();
            for (int i = 0; i < 3; i++) set.Add(TechOfAnim(d.PartnerType, b[s + 0x27C + i]));
            // scriptState (0x17C bytes): card shop offers 0-5, recycle stock 6-0x53, Item Keeper 0x54-0xD3, cards (a nibble each), flags, variables 0-34
            int ss = s + 0x290;
            System.Array.Copy(b, ss, st.CardOffers, 0, 6);
            System.Array.Copy(b, ss + 6, st.Recycle, 0, 0x4E);
            System.Array.Copy(b, ss + 0x54, st.Stored, 0, 128);
            for (int c = 0; c < 66; c++) { int v = b[ss + 0xD4 + c / 2]; st.CardCount[c] = (byte)((c & 1) == 0 ? v & 0xF : v >> 4); st.Cards[c] = st.CardCount[c] > 0; }
            System.Array.Copy(b, ss + 0xF5, st.Flags, 0, 100);
            System.Array.Copy(b, ss + 0x159, st.Vars, 0, 0x17C - 0x159);
            // setDigimonRaised / hasDigimonRaised: flag 512 + type, for types below 63
            for (int t = 0; t < 63 && t < st.Raised.Length; t++) st.Raised[t] = st.GetFlag(512 + t);
            // the bag: 30 slots (type, amount; 0xFF = empty) and its size
            int inv = s + 0x40C;
            for (int i = 0; i < 30; i++)
            {
                int type = b[inv + i];
                st.Slots[i] = (byte)type;
                if (type != 0xFF && type < st.Bag.Length) st.Bag[type] = b[inv + 30 + i];
            }
            st.ItemSlots = b[inv + 90];
            d.PlayerName = CardText(b, s + 0x467, 20); d.PartnerName = CardText(b, s + 0x47B, 20);
            d.Life = (sbyte)b[s + 0x48F]; d.TamerLevel = (sbyte)b[s + 0x490]; para.RaisedCount = b[s + 0x491];
            int screen = b[s + 0x49A];
            d.PrevScreen = b[s + 0x49B]; d.CurExit = b[s + 0x49C]; d.PrevExit = b[s + 0x49D];
            if (screen >= _mapNames.Count) throw new System.Exception("unknown map " + screen);
            d.Map = _mapNames[screen];
            // battleRegistrationData: 40 x 0x40 (hp, mp, off, def, speed, brains, discipline, name, type, three move animations)
            var reg = ScriptState.NewRegistry();
            for (int i = 0; i < 40; i++)
            {
                int r = s + 0x500 + 0x40 * i;
                var e = reg[i];
                e.Hp = I16(b, r); e.Mp = I16(b, r + 2); e.Off = I16(b, r + 4); e.Def = I16(b, r + 6); e.Speed = I16(b, r + 8); e.Brain = I16(b, r + 10);
                e.Discipline = I16(b, r + 12); e.Name = CardText(b, r + 14, 14); e.Type = b[r + 28];
                for (int k = 0; k < 3; k++) e.Moves[k] = e.Type != 0 ? TechOfAnim(e.Type, b[r + 29 + k]) : -1;
            }
            // into our save (as SaveGameData.From does)
            d.Vars = st.Vars; d.Flags = st.Flags; d.Bag = st.Bag; d.Stored = st.Stored; d.Slots = st.Slots; d.Recycle = st.Recycle;
            d.CardCount = st.CardCount; d.CardOffers = st.CardOffers; d.Techs = st.Techs; d.Cards = st.Cards; d.Raised = st.Raised; d.TechSet = set;
            d.Stats = new int[st.Stats.Length];
            for (int i = 0; i < d.Stats.Length; i++) d.Stats[i] = st.Stats[i];
            d.ItemSlots = st.ItemSlots; d.Happiness = happiness; d.Discipline = discipline; d.Age = age; d.Weight = weight;
            d.Para = para; d.Registry = reg;
            return d;
        }

        // ours: which save of the card file to bring in (laid out like the slot list)
        IEnumerator PickCardSave(List<CardSave> saves, System.Action<int> result)
        {
            var s = new SysScreen();
            HighlightCursor(s, 7, Mathf.Max(0, saves.Count - 5));
            s.Refresh = v =>
            {
                v.Texts.Clear(); v.Boxes.Clear(); v.Dim.Clear();
                v.Text("MEMORY CARD FILE", 0x3A, 0x18); v.Box(0x30, 0x13, 0xE0, 0x16);
                for (int i = 0; i < 6; i++)
                {
                    int n = v.Scroll + i, y = 0x29 + 0x24 * i;
                    if (n >= saves.Count) break;
                    var d = saves[n].Save;
                    var sp = DigimonStatsCatalog.Get(_disc, d.PartnerType);
                    v.Text(d.PlayerName ?? "", 0x58, y + 5); v.Text(sp != null ? sp.Name : "", 0xA6, y + 5);
                    v.Box(0x30, y, 0xE0, 0x24);
                }
            };
            s.Refresh(s);
            while (true)
            {
                int answer = 0;
                yield return Ask(s, a => answer = a);
                if (answer != 1) { result(-1); yield break; }
                int k = s.Scroll + s.Pos;
                if (k < saves.Count) { result(k); yield break; }
            }
        }
    }
}
