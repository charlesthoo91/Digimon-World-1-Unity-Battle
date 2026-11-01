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
        readonly int[] _trainGain = new int[6]; bool _trainShow;

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
            int side = _mapId == 0x70 ? 0 : 2;
            bool hasSpot = _mapId != 0x6B && _mapId != 0x6C && _mapId != 0xA5 && _mapId != 0x63;      // the second gym's spots (TRN2) are not ported
            Vector3 spot = hasSpot ? new Vector3(TrainSpot[kind, side] * Unit, _partner.position.y, TrainSpot[kind, side + 1] * Unit) : _partner.position;
            if (_anim != null && _anim.HasSlot(10)) _anim.SetSlot(10);
            yield return WalkTo(spot, RunSlot);
            int slot = TrainAnim(kind);
            if (_partnerAnim != null && _partnerAnim.HasSlot(slot)) _partnerAnim.SetSlot(slot);
            if (kind == 5 && _anim != null && _anim.HasSlot(9)) _anim.SetSlot(9);
            for (float t = 0f; t < 60f && !GameMenus.TriangleKey(); t += Time.deltaTime)     // 1200 frames, or Triangle
            {
                Face(_player, _partner.position);
                yield return null;
            }
            if (_audio != null) _audio.Play(8, 10);
            if (_partnerAnim != null) _partnerAnim.SetSlot(0x12, true);
            if (_anim != null) _anim.SetSlot(IdleSlot);
            _care.Train(kind, _trainGain, _state.Bag[0x21] > 0);
            int[] order = { 4, 5, 0, 1, 2, 3 };
            _trainShow = true;
            bool any = true;
            yield return new WaitForSeconds(1f);
            while (any)
            {
                any = false;
                for (int i = 0; i < 6; i++)
                    if (_trainGain[i] > 0)
                    {
                        _trainGain[i]--; _state.AddStat(order[i], 1); any = true;
                        if (_audio != null) _audio.Play(0, 0x16);
                        break;
                    }
                if (any) yield return new WaitForSeconds(0.05f);
            }
            yield return new WaitForSeconds(1f);
            _trainShow = false;
            if (_care.LearnFromTraining(_trn, kind)) Say(DigimonStatsCatalogName(_care.LastLearned) + " mastered!");
            yield return WalkTo(_player.position, RunSlot);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        static string DigimonStatsCatalogName(int tech) { return DW1Remake.Formats.DigimonStatsCatalog.NameOf(tech); }

        void DrawTraining()
        {
            if (!_trainShow) return;
            UiKit.Begin();
            string[] names = { "HP", "MP", "Off", "Def", "Speed", "Brain" };
            int[] order = { 4, 5, 0, 1, 2, 3 };
            var box = new Rect(330, 60, 760, 520);
            UiKit.Box(box, new Color(0.12f, 0.2f, 0.16f, 0.95f));
            for (int i = 0; i < 6; i++)
            {
                float y = box.y + 30 + i * 78;
                UiKit.Text(new Rect(box.x + 40, y, 240, 70), names[i], 50, UiKit.Yellow, TextAnchor.MiddleLeft);
                UiKit.Text(new Rect(box.x + 270, y, 200, 70), _state.Stats[order[i]].ToString(), 48, Color.white, TextAnchor.MiddleRight);
                if (_trainGain[i] > 0) UiKit.Text(new Rect(box.x + 520, y, 200, 70), "^ " + _trainGain[i], 50, new Color(0.35f, 0.75f, 1f), TextAnchor.MiddleLeft);
            }
            UiKit.End();
        }
    }
}
