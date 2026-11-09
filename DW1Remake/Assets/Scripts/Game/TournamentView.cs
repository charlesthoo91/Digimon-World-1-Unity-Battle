using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The arena's boxes. Registration (dget/dget.c, DGET_REL.BIN, US build): box 1 "Tournament" at (-54, -98) 108 x 20
    /// (buildScheduleLabels, renderTournamentTextbox), box 2 the schedule at (-0x82, -0x3E) 0x104 x 0x7C, five days by six rows
    /// (buildScheduleEntries, renderTournamentSchedule), box 3 a cup's seven info lines at (-0x7E, y) 0xFC x 0x63 (initTournamentInfo,
    /// renderTournamentInfo). The fight (std/std_main.c, STD_REL.BIN): the 99 counter (STD_renderCounterDigits, the digits of STDTIM.BIN's
    /// picture at VRAM (832, 0), colour set (16, 480)) and the time-up box 0 at (-132, -27) 264 x 54 (STD_addTimeoutWindow,
    /// STD_renderTimeoutWindow). Positions are PS1 pixels from the middle of the screen.
    /// </summary>
    public sealed class TournamentView : UiBoxPage
    {
        public readonly UiBox Label = new UiBox { Id = 1 }, Grid = new UiBox { Id = 2 }, Info = new UiBox { Id = 3 }, TimeUp = new UiBox { Id = 0 };

        // DGET_BUFFER: 5 days x 6 cups (0xFF none; bit 0x80 can enter, 0x40 can enter but is too strong for it, low 6 bits the cup)
        public readonly byte[] Cells = new byte[30];
        public int Col, Row;                    // TOURNAMENT_SELECTED_COLUMN / _ROW
        public int InfoSlot;                    // the text rows box 3 uses (registerTextbox: 0, or 8 after a cup was chosen)
        public string[] Grades = new string[23];  // TOURNAMENT_GRADES (exe 0x8013013C)
        public int FirstDay;                    // DAY when the schedule opened (0-29)
        public string InfoText = "";            // the cup's seven lines (its cup data entry 0)
        public string TitleText = "Tournament"; // renderTournamentTextbox (US): drawString("Tournament")

        // the fight's counter and the time-up box
        public bool ShowTimer; public int Timer;
        public string GaveText = "", DamageText = "", PartnerName = "", EnemyName = "";
        public int DamageByPartner, DamageByEnemy;   // STD_DAMAGE[1] (what the partner dealt), STD_DAMAGE[0]

        TimImage _digits; int _digitsX, _digitsY, _digitsW; Texture2D _digitsTex;
        TimImage _frame; Texture2D _frameTex;      // STDDAT/TIME.TIM (VRAM 704, 120; its colour set at (512, 255))
        public bool ShowTimeUpWord;                // STD_renderTimeoutText: the "time up" picture (u 0, v 8, 0x90 x 0x18 at (-0x48, -0xC))
        float _acc;

        const int LabelV = 8 * 12, DaysV = 9 * 12, RowsV = 10 * 12;

        public static TournamentView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new TournamentView();
            v.Init(disc, font);
            for (int i = 0; i < 23; i++) v.Grades[i] = v.Str(BitConverter.ToUInt32(v.Exe, v.At(0x8013013C) + 4 * i));
            v.LoadDigits();
            if (disc.Exists("/STDDAT/TIME.TIM")) TimImage.TryParse(disc.ReadFile("/STDDAT/TIME.TIM"), 0, out v._frame);
            return v;
        }

        // STD_loadTIMToVRAM(STDTIM.BIN): six TIM files back to back; the counter's digits are the one at VRAM (832, 0)
        void LoadDigits()
        {
            if (!Disc.Exists("/STDDAT/STDTIM.BIN")) return;
            byte[] d = Disc.ReadFile("/STDDAT/STDTIM.BIN");
            int p = 0;
            for (int i = 0; i < 6 && p + 8 < d.Length; i++)
            {
                TimImage t;
                if (!TimImage.TryParse(d, p, out t)) break;
                int flag = BitConverter.ToInt32(d, p + 4), o = p + 8, cx = 0, cy = 0;
                int cw = 0;
                if ((flag & 8) != 0) { cx = BitConverter.ToUInt16(d, o + 4); cy = BitConverter.ToUInt16(d, o + 6); cw = BitConverter.ToUInt16(d, o + 8); o += BitConverter.ToInt32(d, o); }
                int ix = BitConverter.ToUInt16(d, o + 4), iy = BitConverter.ToUInt16(d, o + 6);
                if (ix == 832 && iy == 0) { _digits = t; _digitsX = cx; _digitsY = cy; _digitsW = cw; }
                p = t.EndOffset;
            }
        }

        // GetClut(0x10, 0x1E0): the colour set at x 16 on row 480, counted from the picture's own colour block (16 colours a set)
        Texture2D DigitsTex()
        {
            if (_digits == null) return null;
            if (_digitsTex != null) return _digitsTex;
            int perRow = Mathf.Max(1, _digitsW / 16);
            int row = (480 - _digitsY) * perRow + (16 - _digitsX) / 16;
            if (row < 0 || row >= _digits.PaletteCount) row = 0;
            _digitsTex = _digits.ToTexture(row);
            return _digitsTex;
        }

        public bool Showing { get { return Label.State != 0 || Grid.State != 0 || Info.State != 0 || TimeUp.State != 0; } }

        /// <summary>buildScheduleLabels + buildScheduleEntries: box 1 and box 2 grow out of the owner (setupBoxOrigin(variable 254)).</summary>
        public void OpenSchedule(RectInt origin, int day)
        {
            FirstDay = day; Col = 0; Row = 0;
            Open(Label, new RectInt(-54, -98, 108, 20), origin, 0xE1);
            Open(Grid, new RectInt(-0x82, -0x3E, 0x104, 0x7C), origin, 0xE1);
        }

        /// <summary>initTournamentInfo(arg): arg 1 out of the owner (flags 0xE1, y -0x4F, rows from 8); arg 0 out of the chosen cell (0xC1, y -0x31, rows from 0).</summary>
        public void OpenInfo(bool fromOwner, RectInt origin, string text)
        {
            InfoText = text ?? "";
            InfoSlot = fromOwner ? 8 : 0;
            if (!fromOwner)
            {
                int x = Grid.Rect.x + 4 + Col * 51 + 3, y = Grid.Rect.y + 3 + Row * 16 + 0x16;
                origin = new RectInt(x, y, 0x2A, 0xD);
            }
            Open(Info, new RectInt(-0x7E, fromOwner ? -0x4F : -0x31, 0xFC, 0x63), origin, fromOwner ? 0xE1 : 0xC1);
        }

        /// <summary>STD_addTimeoutWindow: grows from a 20 x 20 square in the middle.</summary>
        public void OpenTimeUp() { Open(TimeUp, new RectInt(-132, -27, 264, 54), new RectInt(-10, -10, 20, 20), 0x20); }

        public void Draw()
        {
            if (!Showing && !ShowTimer && !ShowTimeUpWord) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Label); Step(Grid); Step(Info); Step(TimeUp); }
            }
            var key = new System.Text.StringBuilder().Append(FirstDay).Append(':').Append(InfoSlot).Append(':').Append(InfoText).Append(':')
                .Append(PartnerName).Append(EnemyName).Append(TimeUp.State != 0 ? "|T" : "");
            foreach (var c in Cells) key.Append(c).Append(',');
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                Color32 white = Font.Colour(1);
                if (TimeUp.State != 0) { DrawTimeUpText(px, white); return; }
                DrawCoded(px, TitleText, 0, LabelV, white);
                DrawCoded(px, DaysRow(), 0, DaysV, white);
                for (int r = 0; r < 6; r++) DrawCoded(px, CellsRow(r), 0, RowsV + r * 12, white);
                if (Info.State != 0)
                {
                    var lines = InfoText.Split('\n');
                    for (int i = 0; i < 7 && i < lines.Length; i++) DrawCoded(px, lines[i], 0, (InfoSlot + i) * 12, white);
                }
            });
            Begin();
            DrawBox(Label, () => String(5, Label.Rect.x + 6, Label.Rect.y + 3, 0x54, 0, LabelV));
            DrawBox(Grid, DrawGrid);
            DrawBox(Info, DrawInfo);
            DrawBox(TimeUp, DrawTimeUp);
            if (ShowTimer) DrawTimer();
            if (ShowTimeUpWord) { var tex = DigitsTex(); if (tex != null) Picture(5, tex, -0x48, -0xC, 0x90, 0x18, 0, 8); }
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // buildScheduleLabels (US): colour 1, then each day's number (day % 30 + 1) in two places, and a full-width space after the first one when it is under 10
        string DaysRow()
        {
            var sb = new System.Text.StringBuilder(Ink(1));
            for (int i = 0; i < 5; i++)
            {
                int dayOfMonth = (FirstDay + i) % 30;
                sb.Append(Digits(dayOfMonth + 1, 2));
                if (i == 0 && dayOfMonth + 1 < 10) sb.Append('　');
            }
            return sb.ToString();
        }

        // buildScheduleEntries (US): each day is 01 + colour (3 cannot enter, 7 can but is too strong for it, 1 can) + the grade, or 01 01 and a full-width space
        string CellsRow(int r)
        {
            var sb = new System.Text.StringBuilder();
            for (int c = 0; c < 5; c++)
            {
                byte e = Cells[c * 6 + r];
                if (e == 0xFF) { sb.Append(Ink(1)).Append('　'); continue; }
                int colour = (e & 0xC0) == 0 ? 3 : (e & 0x40) != 0 ? 7 : 1;
                string g = Grades[e & 0x3F] ?? "";
                sb.Append(Ink(colour));
                if (g.Length == 2) sb.Append(Gap).Append(g).Append(Gap); else sb.Append(g);
            }
            return sb.ToString();
        }

        // renderTournamentSchedule (US)
        void DrawGrid()
        {
            int z = 6 - Grid.Id, bx = Grid.Rect.x, by = Grid.Rect.y, x = bx + 4, y = by + 3;
            for (int i = 0, sx = 0x34; i < 5; i++, sx += 0x33) { VLine(z, bx + sx, by + 2, 0x13); VLine(z, bx + sx, by + 0x16, 0x64); }
            HLine(z, bx + 3, by + 0x14, 0xFE);
            SelectionCursor(z, x + Col * 51 + 3, y + Row * 16 + 0x16, 0x2A, 0xD);
            int hx = x + 0xC, hy = y + 2;
            for (int i = 0; i < 5; i++) String(z, hx + 0x33 * i, hy, 0x10, 0x10 * i, DaysV);
            for (int r = 0; r < 6; r++)
                for (int i = 0; i < 5; i++) String(z, x + 0x14 + i * 0x33, y + 0x18 + r * 0x10, 0xC, i * 0xC, RowsV + r * 12);
        }

        // renderTournamentInfo: seven rows, 13 pixels apart
        void DrawInfo()
        {
            int z = 6 - Info.Id;
            for (int i = 0; i < 7; i++) String(z, Info.Rect.x + 6, Info.Rect.y + 3 + 13 * i, 0xF0, 0, (InfoSlot + i) * 12);
        }

        // renderVerticalLine: black, 0xA08769, black, side by side
        void VLine(int z, int x, int y, int h)
        {
            LineCmd(z, Bgr(0x020202), x, y, x, y + h - 1);
            LineCmd(z, Bgr(0xA08769), x + 1, y, x + 1, y + h - 1);
            LineCmd(z, Bgr(0x020202), x + 2, y, x + 2, y + h - 1);
        }

        // STD_addTimeoutWindow: "Gave" at (6, 0), "Damage" at (0, 12), the names centred in 120 pixels at rows 24 and 36
        void DrawTimeUpText(Color32[] px, Color32 white)
        {
            DrawCoded(px, GaveText, 6, 0, white);
            DrawCoded(px, DamageText, 0, 12, white);
            DrawCoded(px, PartnerName, Mathf.Max(0, (120 - PartnerName.Length * 6) / 2), 24, white);
            DrawCoded(px, EnemyName, Mathf.Max(0, (120 - EnemyName.Length * 6) / 2), 36, white);
        }

        // STD_renderTimeoutWindow: the two names, "Gave Damage" (48 x 24) in the middle, each one's damage under its name
        void DrawTimeUp()
        {
            int z = 6 - TimeUp.Id, x = TimeUp.Rect.x, y = TimeUp.Rect.y;
            String(z, x + 6, y + 6, 120, 0, 24);
            String(z, x + 138, y + 6, 120, 0, 36);
            String(z, x + 108, y + 24, 48, 0, 0);
            String(z, x + 108, y + 36, 48, 0, 12);
            int d = DamageByPartner.ToString().Length;
            StatDigits(z, x + 42 + (48 - d * 12) / 2, y + 30, d, DamageByPartner);
            d = DamageByEnemy.ToString().Length;
            StatDigits(z, x + 174 + (48 - d * 12) / 2, y + 30, d, DamageByEnemy);
        }

        // STD_renderIntroStatNumber: 12 x 12 digits of page (832, 0) row v 32, colour set (16, 480), right-aligned
        void StatDigits(int z, int x, int y, int digits, int value)
        {
            var tex = DigitsTex();
            if (tex == null) return;
            string s = Mathf.Max(0, value).ToString();
            for (int i = 0; i < s.Length; i++)
            {
                int place = s.Length - 1 - i;
                Picture(z, tex, x + ((digits - 1) - place) * 12, y, 12, 12, (s[i] - '0') * 12, 32);
            }
        }

        // STD_renderCounterDigits(-0xD, -0x61, 2, STD_TIMER, 3): two 8 x 0x12 end pieces (u 0x78 / 0x88, v 0x30), the digits (u digit x 12, v 0x30,
        // 12 x 15, 14 apart, no leading zero) and TIME.TIM's frame (VRAM 704, 120: 0x42 x 0x1D at (-0x21, -0x68))
        void DrawTimer()
        {
            var tex = DigitsTex();
            if (tex == null) return;
            Picture(3, tex, -0x1C, -0x63, 8, 0x12, 0x78, 0x30);
            Picture(3, tex, 0x14, -0x63, 8, 0x12, 0x88, 0x30);
            string s = Mathf.Clamp(Timer, 0, 99).ToString();
            for (int i = 0; i < s.Length; i++)
            {
                int digit = s[i] - '0';
                Picture(3, tex, -0xD + (2 - s.Length + i) * 14, -0x61, 0xC, 0xF, digit * 12, 0x30);
            }
            if (_frame != null)
            {
                if (_frameTex == null) _frameTex = _frame.ToTexture(0);
                Picture(3, _frameTex, -0x21, -0x68, 0x42, 0x1D, 0, 0);
            }
        }
    }
}
