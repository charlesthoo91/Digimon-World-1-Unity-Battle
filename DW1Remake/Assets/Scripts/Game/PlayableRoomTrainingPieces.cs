using System;
using System.Collections;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The map pieces the training moves and shakes (rocks, dirt, boxes, TRN_tick*Training and TRN2_tick*Training: setMapObjectsFlag, moveMapObjects,
    /// moveMapObjectsWithLimit, storeMapObjectPosition / loadMapObjectPosition, resetMapObjectAnimation). Each routine below follows one tick function
    /// once a game frame (20 a second): `yield return null` = one frame. The particles and dust clouds the same functions start are not ported.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        sealed class TrainRun
        {
            public int Kind, Map, A, B;                       // MAIN_D_8013536C / 6E (or B4 / B6 in TRN2): the pieces that move
            public short[] X0, Y0;                            // TRN_D_8008F368 / 8F388: where they stood
            public bool WantEnd, Blocks, ShowDone;            // the loop wants to end; the routine ends only at a cycle's end (Defense); it has ended
            public IEnumerator Show;
            public int Count;                                 // HP: MAIN_D_8013537A / 120, the pieces' way back
            public int StepX, StepY, RetX, RetY;              // HP: one push every 120 frames and the push back
            public int FlagA, FlagB;                          // HP map 0x63: the pieces shown while it trains
            public bool Returned;
        }

        Vector3 _trainSpot;       // where the partner trains (TRN_D_8008F320)

        // ---------------------------------------------------------------- the game's map-object functions on our pieces

        void PieceFlags(int start, int count, int flag) { for (int i = 0; i < count; i++) SetPieceVisible(start + i, flag); }

        void PiecesShift(int start, int count, int dx, int dy)
        {
            if (_overlay == null) return;
            for (int i = 0; i < count; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= _overlay.Instances.Length) continue;
                _overlay.Instances[idx].X += dx; _overlay.Instances[idx].Y += dy;
                ReplacePiece(idx);
            }
        }

        // moveMapObjectsWithLimit: 1 when the first piece has reached the limit in the direction it moves
        bool PiecesShiftLimit(int start, int count, int dx, int dy, int limX, int limY)
        {
            PiecesShift(start, count, dx, dy);
            if (_overlay == null || start < 0 || start >= _overlay.Instances.Length) return true;
            var first = _overlay.Instances[start];
            if (dx > 0) { if (first.X >= limX) return true; }
            else if (dx < 0) { if (first.X <= limX) return true; }
            if (dy > 0) { if (first.Y >= limY) return true; }
            else if (dy < 0) { if (first.Y <= limY) return true; }
            return false;
        }

        // storeMapObjectPosition / loadMapObjectPosition
        void PieceStore(TrainRun r)
        {
            r.X0 = new short[Math.Max(r.B, 1)]; r.Y0 = new short[Math.Max(r.B, 1)];
            for (int i = 0; i < r.B; i++)
            {
                int idx = r.A + i;
                if (_overlay == null || idx < 0 || idx >= _overlay.Instances.Length) continue;
                r.X0[i] = (short)_overlay.Instances[idx].X; r.Y0[i] = (short)_overlay.Instances[idx].Y;
            }
        }

        void PieceLoad(TrainRun r)
        {
            for (int i = 0; i < r.B; i++)
            {
                int idx = r.A + i;
                if (_overlay == null || idx < 0 || idx >= _overlay.Instances.Length) continue;
                _overlay.Instances[idx].X = r.X0[i]; _overlay.Instances[idx].Y = r.Y0[i];
                ReplacePiece(idx);
            }
        }

        void TrainSound(int id) { if (_audio != null) _audio.Play(8, id); }

        // ---------------------------------------------------------------- one routine per training and map

        // the maps and trainings that move pieces; null = this one moves none
        TrainRun BeginPieces(int kind)
        {
            if (_overlay == null) return null;
            var r = new TrainRun { Kind = kind, Map = _mapId };
            switch (kind)
            {
                case 0:
                    // TRN_setupHpTraining (0x70), TRN2_setupHpTraining (0x6B, 0x63)
                    if (_mapId == 0x70) { r.A = 0x53; r.B = 0x10; r.StepX = 1; r.StepY = -2; r.RetX = -1; r.RetY = 2; }
                    else if (_mapId == 0x6B) { r.A = 0xA; r.B = 2; r.StepX = 0; r.StepY = -1; r.RetX = -1; r.RetY = 2; }
                    else if (_mapId == 0x63) { r.A = 0; r.B = 2; r.StepX = -1; r.StepY = -1; r.RetX = 1; r.RetY = 1; r.FlagA = 0xF; r.FlagB = 0x11; }
                    else return null;
                    r.Show = HpPieces(r);
                    break;
                case 4:
                    if (_mapId != 0x70) return null;
                    r.A = 0xA4; r.B = 8; r.Show = MpPieces(r);
                    break;
                case 1:
                    if (_mapId == 0x70) { r.A = 0x64; r.B = 8; r.Show = OffPieces(r); }
                    else if (_mapId == 0x4E) { r.A = 0x2B; r.B = 0x1E; r.Show = OffPiecesFlags(r, 0x27, 4, -1, 0); }
                    else if (_mapId == 0x6B) { r.A = 0xC; r.B = 3; r.Show = OffPiecesFlags(r, 0x27, 4, 0xF, 1); }
                    else return null;
                    break;
                case 3:
                    if (_mapId == 0x70) { r.A = 0x47; r.B = 0xD; r.Blocks = true; r.Show = DefPieces(r); }
                    else if (_mapId == 0x6C) { r.A = 4; r.B = 3; r.Show = Def108Pieces(r); }
                    else if (_mapId == 0x63) { r.A = 0; r.B = 2; r.Show = Def99Pieces(r); }
                    else return null;
                    break;
                case 2:
                    if (_mapId != 0x6C) return null;
                    r.A = 0x38; r.B = 3; r.Show = null;
                    break;
                default: return null;
            }
            PieceStore(r);
            return r;
        }

        // the frame the partner has reached its spot (the arrival step of each tick function)
        void PiecesArrive(TrainRun r)
        {
            if (r == null) return;
            if (r.Kind == 0 && r.Map == 0x63) PieceFlags(r.FlagA, r.FlagB, 0);
            if (r.Kind == 4)
            {
                // TRN_tickMpTraining case 2: the dirt pieces swap, then the new ones go to the partner's height on the painted map
                PieceFlags(0x93, 0x10, 1); PieceFlags(0xA3, 0x18, 0);
                int h = _menus.PartnerInfo != null ? _menus.PartnerInfo.Height : 100;
                PiecesShift(r.A, r.B, 0, (int)MapPixelOf(_partner.position + Vector3.up * h * Unit).y - 0x14);
            }
        }

        // the frame the partner has gone to the first stop of a Speed training
        void PiecesFirstStop(TrainRun r)
        {
            if (r != null && r.Kind == 2 && r.Map == 0x6C) PieceFlags(r.A, r.B, 0);       // TRN2_tickSpeedTraining case 1
        }

        // the frame the training ends and the gains are given
        void PiecesAward(TrainRun r)
        {
            if (r == null) return;
            switch (r.Kind)
            {
                case 0:
                    // TRN_tickHpTraining case 2 end: MAIN_D_8013537A %= 0x4b0, then / 120 = how many pushes to take back
                    if (r.Map == 0x63) PieceFlags(r.FlagA, r.FlagB, 1);
                    break;
                case 4:
                    PieceFlags(0x93, 0x10, 0); PieceFlags(0xA3, 0x18, 1); PieceLoad(r);
                    break;
                case 1:
                    if (r.Map == 0x70) PieceLoad(r);
                    else { PieceFlags(0x27, 4, 0); PieceFlags(r.A, r.B, 1); }
                    break;
                case 3:
                    if (r.Map == 0x6C) { PieceFlags(4, 3, 0); PieceFlags(7, 0x31, 1); }
                    else if (r.Map == 0x63) { PieceFlags(0xE, 1, 1); PieceFlags(0x21, 1, 1); }
                    break;
                case 2:
                    if (r.Map == 0x6C) PieceFlags(r.A, r.B, 1);
                    break;
            }
        }

        // one game frame of the way back; true when it is over (case 5 of the HP ticks: the pieces creep back while the partner walks)
        bool PiecesReturn(TrainRun r)
        {
            if (r == null || r.Kind != 0) return true;
            r.Count--;
            if (r.Count > 0) PiecesShift(r.A, r.B, r.RetX, r.RetY);
            return r.Count <= 0;
        }

        // the end of the tick function: loadMapObjectPosition
        void PiecesFinish(TrainRun r)
        {
            if (r == null) return;
            if (r.Kind == 0 || (r.Kind == 3 && r.Map != 0x6C) || r.Kind == 4) PieceLoad(r);
            if (r.Kind == 3 && r.Map == 0x6C) PieceLoad(r);
        }

        // TRN_tickHpTraining / TRN2_tickHpTrainingMap107 / Map99 case 2: a push every 120 frames and a shake of 2 either side every frame
        IEnumerator HpPieces(TrainRun r)
        {
            int n = 0;
            for (;;)
            {
                if (n % 0x78 == 0 && n < 0x4B1) PiecesShift(r.A, r.B, r.StepX, r.StepY);
                if (n % 2 == 0) PiecesShift(r.A, r.B, 2, 0); else PiecesShift(r.A, r.B, -2, 0);
                if (n % 10 == 0) TrainSound(0);
                n++;
                r.Count = (n % 0x4B0) / 120;
                yield return null;
            }
        }

        // TRN_tickMpTraining: the pieces do not move while it trains
        IEnumerator MpPieces(TrainRun r) { yield break; }

        // TRN_tickOffenseTraining cases 2 and 3 (map 0x70): the hit comes when the animation has run its length (TRN_TRAINING_ANIM_IDS [type][1]), then the pieces shake for 41 frames
        IEnumerator OffPieces(TrainRun r)
        {
            int hit = _trn[26704 + PartnerType * 2 + 1], c = 0, n = 0;
            for (;;)
            {
                c++; n++;
                if (c == hit)
                {
                    c = 0; TrainSound(5);
                    for (;;)
                    {
                        c++; n++;
                        if (c < 4) PiecesShift(r.A, r.B, -4, 2);
                        else if (c < 7) PiecesShift(r.A, r.B, 4, -2);
                        else if (n % 2 == 0) PiecesShift(r.A, r.B, (100 - c) / 20, -((100 - c) / 50));
                        else PiecesShift(r.A, r.B, -((100 - c) / 20), (100 - c) / 50);
                        if (c >= 0x29)
                        {
                            c = 0;
                            if (_partnerAnim != null) _partnerAnim.SetSlot(TrainAnim(1));
                            break;
                        }
                        yield return null;
                    }
                }
                yield return null;
            }
        }

        // TRN_tickOffenseTraining cases 7 and 8 (map 0x4E) and TRN2_tickOffenseTraining cases 2 and 3 (map 0x6B): at the hit the pieces switch, 46 frames later they switch back
        IEnumerator OffPiecesFlags(TrainRun r, int groupA, int groupB, int otherA, int otherB)
        {
            int hit = _trn[26704 + PartnerType * 2 + 1], c = 0;
            for (;;)
            {
                c++;
                if (c == hit)
                {
                    if (r.Map == 0x4E) { PieceFlags(groupA, groupB, 1); PieceFlags(r.A, r.B, 0); TrainSound(0x10); }
                    else { TrainSound(0xF); PieceFlags(r.A, r.B, 0); PieceFlags(otherA, otherB, 1); }
                    ResetPieceAnimation(r.A, r.A);
                    c = 0;
                    for (;;)
                    {
                        c++;
                        if (c == 0x2E)
                        {
                            PieceFlags(groupA, groupB, 0); PieceFlags(r.A, r.B, 1);
                            if (_partnerAnim != null) _partnerAnim.SetSlot(TrainAnim(1));
                            c = 0;
                            break;
                        }
                        yield return null;
                    }
                }
                yield return null;
            }
        }

        // TRN_tickDefenseTraining (map 0x70): the piece slides out and back in steps (cases 2 to 9), the partner is pushed along for two of them,
        // then it walks to its spot again (case 10); it ends only there
        IEnumerator DefPieces(TrainRun r)
        {
            int x0 = r.X0[0];
            int[,] steps = { { 0x32, 0x64 }, { -0x32, 0x32 }, { 0x32, 0x5A }, { -0x1E, 0x3C }, { 0x14, 0x5A }, { -0xA, 0x46 }, { 5, 0x5A }, { -4, 0 } };
            Vector3 spot = _partner.position;
            for (;;)
            {
                for (int s = 0; s < steps.GetLength(0); s++)
                {
                    int dx = steps[s, 0], lim = x0 + steps[s, 1];
                    if (s == 7) lim = x0;
                    for (;;)
                    {
                        if (s == 1 || s == 2)
                        {
                            // loc->vx += 0x64: the partner is shoved 100 game units a frame
                            _partner.position += new Vector3(0x64 * Unit, 0f, 0f);
                            _partner.rotation = Quaternion.Euler(0, 0x400 * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0);
                        }
                        bool done = PiecesShiftLimit(r.A, r.B, dx, 0, lim, 0);
                        if (done) break;
                        yield return null;
                    }
                    if (s == 0) TrainSound(4);
                    yield return null;
                }
                PieceLoad(r);
                // case 10: back to the spot, then out of the loop when the training is over, else another go
                var walk = WalkTo(_trainSpot, RunSlot);
                while (walk.MoveNext()) yield return null;
                if (r.WantEnd) { r.ShowDone = true; yield break; }
                _partner.rotation = Quaternion.Euler(0, 0x400 * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0);
                if (_partnerAnim != null) _partnerAnim.SetSlot(TrainAnim(3));
            }
        }

        // TRN2_tickDefenseTrainingMap108: the pieces 7 to 0x37 play again every 30 frames (after 4 for the first)
        IEnumerator Def108Pieces(TrainRun r)
        {
            PieceFlags(4, 3, 1); PieceFlags(7, 0x31, 0); ResetPieceAnimation(7, 0x31);
            for (;;)
            {
                for (int cc = 0; cc < 4; cc++) yield return null;
                TrainSound(4);
                for (int cc = 0; cc < 0x1E; cc++) yield return null;
                ResetPieceAnimation(7, 0x31);
            }
        }

        // TRN2_tickDefenseTrainingMap99: the piece drops 40 a frame to 200, the other one plays, 30 frames later all is put back
        IEnumerator Def99Pieces(TrainRun r)
        {
            PieceFlags(0xE, 1, 0);
            for (;;)
            {
                while (!PiecesShiftLimit(r.A, r.B, 0, 0x28, 0, 0xC8)) yield return null;
                PieceFlags(0xE, 1, 1); PieceFlags(0x21, 1, 0); ResetPieceAnimation(0x21, 1); TrainSound(0x10);
                for (int cc = 0; cc < 0x1E; cc++) yield return null;
                PieceLoad(r);
                PieceFlags(0xE, 1, 0); PieceFlags(0x21, 1, 1);
                yield return null;
            }
        }
    }
}
