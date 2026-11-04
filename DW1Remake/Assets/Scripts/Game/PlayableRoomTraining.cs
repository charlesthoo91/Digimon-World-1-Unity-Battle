using System;
using System.Collections;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Training at the gym (TRN_REL, trn/*.c): script routine 0x64 7, variable 254 = 0 HP, 1 Offense, 2 Speed, 3 Defense, 4 MP, 5 Brains.
    /// The partner goes to its spot and trains until 1200 frames pass or Triangle is held, then the gains count up and one game hour passes.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        byte[] _trn;   // TRN_REL.BIN: animation table at 26704 (2 bytes each), technique order at 26956, chances right after it
        readonly int[] _trainGain = new int[6]; readonly int[] _trainTotal = new int[6]; bool _trainShow;

        // the spot in map 0x70 and the spot in the other gym, game units (TRN_D_8008F320)
        static readonly int[,] TrainSpot = { { 0x58C, -0x6D0, 0x58C, -0x6D0 }, { -0x3D6, -0xA14, -0x527, 0xCD9 }, { 0x2D, -0x184, 0x32, -0x11F }, { -0x33B, -0x662, -0x33B, -0x662 }, { 0x2AC, 0x868, 0x2AC, 0x868 }, { -0x4E1, 0x3F3, -0x5E3, 0x2CE } };

        int TrainAnim(int kind)
        {
            if (_trn == null) _trn = _disc.ReadFile("/TRN_REL.BIN");
            if (kind == 1) return _trn[26704 + PartnerType * 2];
            int[] slots = { 0x1B, 0, 4, 0x25, 0, 0x16 };
            return slots[kind];
        }

        IEnumerator Training(int kind)
        {
            if (_audio != null) { _audio.Play(8, 9); }
            if (kind < 0 || kind > 5) { _syncBit = true; yield break; }
            Vector3 back = _partner.position;
            Quaternion boyFacing = _player.rotation;
            int side = _mapId == 0x70 ? 0 : 2;
            bool hasSpot = _mapId != 0x6B && _mapId != 0x6C && _mapId != 0xA5 && _mapId != 0x63;      // the second gym's spots (TRN2) are not ported
            Vector3 spot = hasSpot ? new Vector3(TrainSpot[kind, side] * Unit, _partner.position.y, TrainSpot[kind, side + 1] * Unit) : _partner.position;
            if (_anim != null && _anim.HasSlot(10)) _anim.SetSlot(10);
            bool slots = _state.Vars[0xF6] == 1;     // the bonus try: TRN_startSlotSessionIfEnabled, on when variable 0xF6 is 1
            if (slots) StartSlot(SlotStat[kind]);
            float slotAcc = 0f;
            var walk = WalkTo(spot, RunSlot);
            while (walk.MoveNext()) { if (slots) TickSlotFrames(ref slotAcc); yield return walk.Current; }
            if (slots) _slot.Auto = true;            // TRN_startSlotSpin: the reels start by themselves once the partner is at its spot
            int slot = TrainAnim(kind);
            if (_partnerAnim != null && _partnerAnim.HasSlot(slot)) _partnerAnim.SetSlot(slot);
            if (kind == 5 && _anim != null && _anim.HasSlot(9)) _anim.SetSlot(9);
            // 1200 frames or Triangle; with the bonus try, until the slot machine has paid out
            for (float t = 0f; slots ? _slot.Result < 0 : t < 60f && !GameMenus.TriangleKey(); t += Time.deltaTime)
            {
                Face(_player, _partner.position);
                if (slots) TickSlotFrames(ref slotAcc);
                yield return null;
            }
            int bonus = slots ? _slot.Result : 10;
            _slot = null;
            if (_audio != null) _audio.Play(8, 10);
            if (_partnerAnim != null) _partnerAnim.SetSlot(0x12, true);
            if (_anim != null) _anim.SetSlot(IdleSlot);
            _care.Train(kind, _trainGain, _state.Bag[0x21] > 0, bonus);
            int[] order = { 4, 5, 0, 1, 2, 3 };
            Debug.Log("[training] kind " + kind + " bonus " + bonus + " gains " + string.Join("/", System.Array.ConvertAll(_trainGain, g => g.ToString())) + " | stats before " + string.Join("/", System.Array.ConvertAll(order, o => _state.Stats[o].ToString())));
            // TRN_tickPostTrainingStatsBox / renderPostTrainingStatsBox: 100 frames, then one point a frame with sound 0x16; Cross or Triangle adds the rest at once.
            // When all is added the box closes 20 frames later.
            System.Array.Copy(_trainGain, _trainTotal, 6);
            _trainShow = true;
            int wait = 100; float acc = 0f;
            while (System.Array.Exists(_trainGain, g => g > 0))
            {
                if (ActionPressed() || GameMenus.CancelKey() || GameMenus.TriangleKey())
                {
                    for (int i = 0; i < 6; i++) { _state.AddStat(order[i], _trainGain[i]); _trainGain[i] = 0; }
                    break;
                }
                acc += Time.deltaTime;
                while (acc >= 0.05f)
                {
                    acc -= 0.05f;
                    if (wait > 0) { wait--; continue; }
                    for (int i = 0; i < 6; i++)
                        if (_trainGain[i] > 0) { _trainGain[i]--; _state.AddStat(order[i], 1); if (_audio != null) _audio.Play(0, 0x16); break; }
                }
                yield return null;
            }
            Debug.Log("[training] stats after " + string.Join("/", System.Array.ConvertAll(order, o => _state.Stats[o].ToString())));
            yield return new WaitForSeconds(20f / 20f);
            _trainShow = false;
            _player.rotation = boyFacing;
            if (_care.LearnFromTraining(_trn, kind)) Say(DigimonStatsCatalogName(_care.LastLearned) + " mastered!");
            yield return WalkTo(_player.position, RunSlot);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            _armed = true;      // he still stands on the board's trigger: Cross starts the next training at once
        }

        // ---------------------------------------------------------------- the bonus try (trn_slots.c)
        // Three reels of 13 symbols (TRN_D_8008F1AC, TRN_REL.BIN at 27052): 1 HP, 2 MP, 3 Off, 4 Def, 5 Speed, 6 Brains, 7 poop. Cross starts them and stops them one by one.
        // Three poops pay 100, three of the trained stat 40, any other three 20, else 5; the gains are then x payout / 10.
        // The pictures are in the gym map's own texture (VRAM 704,0: 32 x 32 symbols at v 216, the dark strip behind a reel at u 224).
        sealed class SlotMachine
        {
            public int State, Stat, Assist, Payout = -1, Result = -1, Timer; public bool Auto;
            public readonly int[] Pos = new int[3], Scroll = new int[3], Speed = new int[3], Steps = new int[3], Settle = new int[3], Target = new int[3];
        }
        SlotMachine _slot; bool _slotCross; Texture2D _slotTex, _slotShade; string _slotTexMap;
        static readonly int[] SlotStat = { 0, 2, 4, 3, 1, 5 };      // our kind -> the game's stat (0 HP, 1 MP, 2 Off, 3 Def, 4 Speed, 5 Brains)
        const int ReelTable = 27052, AssistTable = 27340;           // TRN_D_8008F1AC and TRN_D_8008F2CC (TRN_REL is loaded at 0x80088800)

        int Reel(int i, int c) { return _trn[ReelTable + i * 13 + c]; }

        // TRN_createSlotMachineBox
        void StartSlot(int stat)
        {
            if (_trn == null) _trn = _disc.ReadFile("/TRN_REL.BIN");
            var rng = new System.Random();
            _slot = new SlotMachine { Stat = stat, Assist = _trn[AssistTable + rng.Next(10)] };
            for (int i = 0; i < 3; i++) _slot.Pos[i] = rng.Next(13);
            _slotCross = false;
            if (_slotTexMap == MapPath) return;
            _slotTexMap = MapPath; _slotTex = null; _slotShade = null;
            byte[] d = _disc.ReadFile(MapPath);
            for (int o = 0; o + 28 < d.Length; o += 4)
            {
                if (BitConverter.ToInt32(d, o) != 0x10 || BitConverter.ToInt32(d, o + 4) != 8) continue;
                int img = o + 8 + BitConverter.ToInt32(d, o + 8);
                if (img < o || img + 12 > d.Length || BitConverter.ToUInt16(d, img + 4) != 704 || BitConverter.ToUInt16(d, img + 6) != 0) continue;
                DW1Remake.Formats.TimImage tim;
                // CLUT (48, 486) for the symbols and (64, 486) for the strip: colour sets 3 and 4 of the picture's colour block
                if (DW1Remake.Formats.TimImage.TryParse(d, o, out tim)) { _slotTex = tim.ToTexture(3); _slotShade = tim.ToTexture(4); }
                break;
            }
        }

        void TickSlotFrames(ref float acc)
        {
            if (ActionPressed()) _slotCross = true;
            acc += Time.deltaTime;
            while (acc >= 0.05f) { acc -= 0.05f; TickSlot(_slotCross); _slotCross = false; }
        }

        void SlotSound(int id) { if (_audio != null) _audio.Play(8, id); }

        // TRN_tickSlotMachine, one game frame
        void TickSlot(bool cross)
        {
            var p = _slot;
            switch (p.State)
            {
                case 0:
                    if (cross || p.Auto)
                    {
                        p.State = 1;
                        for (int i = 0; i < 3; i++) { p.Speed[i] = 0x400; p.Steps[i] = 0; }
                        SlotSound(6);
                    }
                    break;
                case 1: case 2: case 3: case 4:
                    for (int i = 0; i < 3; i++)
                    {
                        p.Settle[i] = 0;
                        p.Scroll[i] += p.Speed[i] >> 6;
                        if (p.Scroll[i] == 0x20) p.Scroll[i]--;
                        if (p.Scroll[i] >= 0x20)
                        {
                            p.Scroll[i] -= 0x20;
                            p.Pos[i] = (p.Pos[i] + 12) % 13;
                            if (i < p.State - 1)
                            {
                                if (p.Steps[i] > 0) p.Steps[i]--;
                                else { p.Scroll[i] = 0; p.Speed[i] = 0; SlotSound(8); }
                            }
                        }
                        if (p.State - 1 == i && cross) ChooseReelStop(i);
                        int t = p.Scroll[i] + (p.Speed[i] >> 6);
                        if (t == 0x20) t--;
                        if (t >= 0x20)
                        {
                            if (i < p.State - 1 && p.Steps[i] <= 0) p.Settle[i]++;
                            else if (i == p.State - 1 && p.Steps[i] <= 0 && cross) p.Settle[i]++;
                        }
                    }
                    if (p.State < 4 && cross) { SlotSound(7); p.State++; }
                    if (p.Speed[0] == 0 && p.Speed[1] == 0 && p.Speed[2] == 0) { p.Timer = 0; p.State++; }
                    break;
                case 5:
                    if (p.Timer == 0)
                    {
                        int a = Reel(0, p.Pos[0]);
                        if (a == Reel(1, p.Pos[1]) && a == Reel(2, p.Pos[2]))
                        {
                            if (a == 7) { SlotSound(0xC); p.Payout = 100; }
                            else if (a - 1 == p.Stat) { SlotSound(0xB); p.Payout = 40; }
                            else { SlotSound(0xB); p.Payout = 20; }
                        }
                        else { SlotSound(0xD); p.Payout = 5; }
                    }
                    else if (p.Timer >= 0x1E || cross) p.State++;
                    p.Timer++;
                    break;
                case 6:
                    p.Result = p.Payout;
                    break;
            }
        }

        // TRN_chooseReelStop: the hidden help (Assist 0 none, 1 lines up the first reel's symbol, 2 aims for three poops)
        void ChooseReelStop(int i)
        {
            var p = _slot;
            if (i == 0)
            {
                if (p.Assist == 0) p.Target[0] = Reel(0, (p.Pos[0] + 12) % 13);
                else if (p.Assist == 1) Shift(0);
                else Scan(0);
            }
            else if (i == 1)
            {
                if (p.Assist == 0) p.Target[1] = Reel(1, (p.Pos[1] + 12) % 13);
                else if (p.Assist == 1) { if (p.Target[0] == 7) Shift(1); else Seek(1, p.Target[0]); }
                else { if (p.Target[0] == 7) Scan(1); else p.Target[1] = -2; }
            }
            else
            {
                if (p.Assist == 0)
                {
                    if (p.Target[0] != p.Target[1] || p.Target[0] != Reel(2, (p.Pos[2] + 12) % 13)) return;
                    p.Steps[2] = UnityEngine.Random.Range(0, 2) + 1;
                }
                else if (p.Assist == 1)
                {
                    if (p.Target[0] != p.Target[1]) return;
                    if (p.Target[0] != 7) Seek(2, p.Target[0]);
                    else if (Reel(2, (p.Pos[2] + 12) % 13) == 7) p.Steps[2] = UnityEngine.Random.Range(0, 2) + 1;
                }
                else
                {
                    if (p.Target[0] == 7 && p.Target[1] == 7) Scan(2); else p.Target[2] = -3;
                }
            }
        }

        void Shift(int i) { var p = _slot; int j = p.Scroll[i] == 0 ? 0 : 1; p.Target[i] = Reel(i, (p.Pos[i] + 13 - j) % 13); }

        void Scan(int i)
        {
            var p = _slot; int j;
            for (j = 1; j < 3; j++) if (Reel(i, (p.Pos[i] + 13 - j) % 13) == 7) break;
            if (j == 3) { p.Steps[i] = 0; p.Target[i] = -1; }
            else { p.Steps[i] = j - 1; p.Target[i] = 7; }
        }

        void Seek(int i, int symbol)
        {
            var p = _slot; int j;
            for (j = 1; j < 3; j++) if (Reel(i, (p.Pos[i] + 13 - j) % 13) == symbol) break;
            if (j == 3) { p.Steps[i] = 0; p.Target[i] = -2; }
            else { p.Steps[i] = j - 1; p.Target[i] = symbol; }
        }

        // TRN_renderSlotMachine: box (-82, -87, 164, 90); reel i centred at x 41 i - 41, the window from y -74 to -10 (PS1 pixels from the middle)
        void DrawSlot()
        {
            if (_slot == null || _slotTex == null) return;
            var p = _slot;
            UiKit.Begin();
            Func<float, float> px = v => 707.5f + v * 4.42f, py = v => 530f + v * 4.42f;
            UiKit.Box(new Rect(px(-82), py(-87), 164 * 4.42f, 90 * 4.42f), UiKit.DialogFill);
            float tw = _slotTex.width, th = _slotTex.height;
            for (int i = 0; i < 3; i++)
            {
                int cx = 41 * i - 41;
                // TRN_SLOT_SPRITE2: 8 x 32 at u 224, scaled 4 x 2, subtracted at half strength (drawn here as a dark wash)
                var o = GUI.color; GUI.color = new Color(0f, 0f, 0f, 0.5f);
                GUI.DrawTextureWithTexCoords(new Rect(px(cx - 16), py(-74), 32 * 4.42f, 64 * 4.42f), _slotShade, new Rect(224f / tw, 1f - 248f / th, 8f / tw, 32f / th));
                GUI.color = o;
                int y = p.Scroll[i] - 0x7A;
                if (p.Settle[i] == 0) { y += p.Speed[i] >> 6; if (p.Scroll[i] + (p.Speed[i] >> 6) == 0x20) y--; }
                else y += 2;
                for (int k = 0; k < 4; k++, y += 0x20)
                {
                    int c = (p.Pos[i] + 11 + k) % 13;
                    int u = (Reel(i, c) - 1) << 5, sy, v, h;
                    if (y < -0x4A) { sy = -0x4A; v = 0x8E - y; h = 0x20 - (v - 0xD8); }
                    else if (y + 0x20 >= -9) { sy = y; v = 0xD8; h = 0x20 - (y + 0x2A); }
                    else { sy = y; v = 0xD8; h = 0x20; }
                    if (y >= -9 || y + 0x20 < -0x49 || h <= 0) continue;
                    GUI.DrawTextureWithTexCoords(new Rect(px(cx - 16), py(sy), 32 * 4.42f, h * 4.42f), _slotTex, new Rect(u / tw, 1f - (v + h) / th, 32f / tw, h / th));
                }
            }
            UiKit.End();
        }

        static string DigimonStatsCatalogName(int tech) { return DW1Remake.Formats.DigimonStatsCatalog.NameOf(tech); }

        // TRN_renderPostTrainingStatsBox: box (-94, -78, 188, 96) in PS1 pixels from the middle; rows 13 apart from y + 9; name at x + 10, number (4 digits, 8 wide) at x + 68, bar at x + 66,
        // blue arrow at x + 130 and the gain (3 digits) at x + 142, a line at x + 122. Game font not decoded: names and digits are our text, in the game's colours.
        void DrawTraining()
        {
            DrawSlot();
            if (!_trainShow) return;
            UiKit.Begin();
            int[] order = { 4, 5, 0, 1, 2, 3 };
            var values = new int[6]; var raised = new bool[6];
            for (int i = 0; i < 6; i++) { values[i] = _state.Stats[order[i]]; raised[i] = _trainTotal[i] > 0; }
            Battle.DrawStatsBox(_battle != null ? _battle.Art : null, -94, -78, 188, values, _trainTotal, raised);
            UiKit.End();
        }
    }
}
