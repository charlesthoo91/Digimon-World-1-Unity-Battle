using System;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The Triangle menu (main/game_menu.c, US build: addGameMenu, tickTriangleMenu, tickGameMenu, renderGameMenu, renderDateDigits, renderTriangleCursor).
    /// UI box 0 at (-0x42, -0x50) 0x84 x 0x6E (0x95 with the fishing rod), colour 1, features 2, growing out of the boy (createMenuBox). Inside: the two
    /// lines GAME_MENU_LINES, "Year" / "Day" (STR_YEAR_DAY, text colour 3) with their digits, the cursor (8 pieces, SELECTION_CURSOR_*), and per option its
    /// picture (MENU_OPTIONS: the selected one flips to its second frame, 0x14 to the right, 5 frames of every 10) and its label (GAME_MENU_TEXT_SPRITES).
    /// All pictures come from texture page (896, 256): ETCDAT/ETCTIM.BIN's fifth picture (VRAM 896, 448), colour sets 12 and 13 (CLUT y 0x1F0 + n).
    /// Positions are PS1 pixels from the middle of the screen.
    /// </summary>
    public sealed class TriangleMenuView : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 0 };
        public int Selection = 1;                     // MENU_OPTIONS[0].unknown: 1 Item, 2 Digimon, 3 Player, 4 Praise, 5 Scold, 6 Sleep, 7 Fishing
        public int Count = 7;                         // MENU_OPTION_COUNT
        public readonly bool[] Disabled = new bool[8];
        public int Year = 1, Day = 1;                 // YEAR + 1, DAY + 1

        struct Spr { public int X, Y, W, H, TexX, TexY, Clut; }
        readonly Spr[] _opt = new Spr[8], _label = new Spr[8];
        readonly int[] _cu0 = new int[8], _cu1 = new int[8], _cv0 = new int[8], _cv1 = new int[8], _cx = new int[8], _cy = new int[8], _cw = new int[8], _ch = new int[8];
        string _yearDay;
        TimImage _sheet;
        readonly Texture2D[] _tex = new Texture2D[16];
        float _acc; int _frames;

        public static TriangleMenuView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32") || !disc.Exists("/ETCDAT/ETCTIM.BIN")) return null;
            var v = new TriangleMenuView();
            v.Init(disc, font);
            // MENU_OPTIONS 0x80123E88: {x, y, unknown, disabled, clutY, width, height, texX, texY}, 12 bytes
            for (int i = 0; i < 8; i++)
            {
                int o = v.At(0x80123E88) + 12 * i;
                v._opt[i] = new Spr { X = v.S16(o), Y = v.S16(o + 2), Clut = (sbyte)v.Exe[o + 7], W = v.Exe[o + 8], H = v.Exe[o + 9], TexX = v.Exe[o + 10], TexY = v.Exe[o + 11] };
            }
            // GAME_MENU_TEXT_SPRITES 0x80123EE8: {x, y, width, height, texX, texY}, 8 bytes
            for (int i = 0; i < 8; i++)
            {
                int o = v.At(0x80123EE8) + 8 * i;
                v._label[i] = new Spr { X = v.S16(o), Y = v.S16(o + 2), W = v.Exe[o + 4], H = v.Exe[o + 5], TexX = v.Exe[o + 6], TexY = v.Exe[o + 7] };
            }
            Action<uint, int[]> bytes = (a, dst) => { for (int i = 0; i < 8; i++) dst[i] = v.Exe[v.At(a) + i]; };
            Action<uint, int[]> sbytes = (a, dst) => { for (int i = 0; i < 8; i++) dst[i] = (sbyte)v.Exe[v.At(a) + i]; };
            bytes(0x80134250, v._cu0); bytes(0x80134258, v._cu1); bytes(0x80134260, v._cv0); bytes(0x80134268, v._cv1);
            sbytes(0x80134270, v._cx); sbytes(0x80134278, v._cy); sbytes(0x80134280, v._cw); sbytes(0x80134288, v._ch);
            v._yearDay = v.Str(0x80134298);
            TimImage.TryParse(disc.ReadFile("/ETCDAT/ETCTIM.BIN"), 32544, out v._sheet);
            return v;
        }

        public bool Showing { get { return Box.State != 0; } }

        /// <summary>addGameMenu + createMenuBox(0, -0x42, -0x50, 0x84, 0x6E, 2): grows out of the boy's square.</summary>
        /// <param name="fishing">HAS_FISHING_ROD (getFishingAvailability): 0 seven options; 1 the eighth, Fishing, greyed; 2 Fishing usable. With it the box is 0x95 high</param>
        public void Open(RectInt boy, bool sleepy, int selection = 1, int fishing = 0)
        {
            Count = fishing != 0 ? 8 : 7; Selection = selection < 1 || selection >= Count ? 1 : selection;
            for (int i = 0; i < 8; i++) Disabled[i] = false;
            Disabled[6] = !sleepy;
            Disabled[7] = fishing == 1;
            CloseNow(Box);
            Open(Box, new RectInt(-0x42, -0x50, 0x84, fishing != 0 ? 0x95 : 0x6E), boy, 0x20);
            Box.Color = 1;          // createAnimatedUIBox(id, 1, features, ...)
        }

        /// <summary>closeUIBoxIfOpen: it shrinks back into the boy.</summary>
        public void Shut(RectInt boy) { Box.Origin = boy; Close(Box); }

        /// <summary>tickGameMenu: Up / Down by 3, Left / Right by 1, both wrapping (sound 2 when it changes).</summary>
        public void Move(int step, int side)
        {
            int sel = Selection;
            if (step < 0) { sel -= 3; if (sel <= 0) sel += (Count + 1) / 3 * 3; if (sel >= Count) sel -= 3; }
            else if (step > 0) { sel += 3; if (sel >= Count) sel -= (Count + 1) / 3 * 3; if (sel <= 0) sel += 3; }
            else if (side < 0) { sel -= 1; if (sel <= 0) sel = Count - 1; }
            else if (side > 0) { sel += 1; if (sel >= Count) sel = 1; }
            if (sel != Selection) { Selection = sel; Snd(2); }
        }

        public bool Ready { get { return Box.State == 1; } }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); _frames++; }
            }
            BuildPage(_yearDay, px => { if (Font != null) Font.Render(_yearDay ?? "", px, 256, 256, 0, 0xE8 - 0xD0); });
            Begin();
            DrawBox(Box, () => Content());
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        Texture2D Set(int row) { return Pal(_sheet, _tex, row); }

        // renderRectPolyFT4 on page (896, 256): v counts from VRAM y 256, the picture starts at 448 (v 0xC0)
        void Rect4(int z, int x, int y, int w, int h, int u, int v, int clutRow, bool off)
        {
            var tex = Set(clutRow);
            if (tex == null || w <= 0 || h <= 0) return;
            int row = v - 0xC0;
            Color c = off ? new Color(0x32 / 128f, 0x32 / 128f, 0x32 / 128f, 1f) : Color.white;
            Rect uv = new Rect(u / (float)tex.width, 1f - (row + h) / (float)tex.height, w / (float)tex.width, h / (float)tex.height);
            Add(z, () => { var o = GUI.color; GUI.color = c; GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, h * 4.42f), tex, uv); GUI.color = o; });
        }

        void Content()
        {
            Lines(5, 0x80123F28, 2);
            DateDigits();
            int yOff = Count == 7 ? -0x28 : 0;
            Cursor(Selection, yOff);
            for (int i = 1; i < Count; i++)
            {
                var s = _opt[i];
                int hi = i == Selection && _frames % 10 < 5 ? 0x14 : 0;
                Rect4(6, s.X - 0x42, s.Y - 0x50 + yOff, s.W, s.H, s.TexX + hi, s.TexY + 0xC0, s.Clut, Disabled[i]);
                var l = _label[i];
                Rect4(6, l.X - 0x42, yOff + l.Y - 0x50, l.W, l.H, l.TexX, l.TexY + 0xBF, 13, Disabled[i]);     // CLUT 0x7F50 = (256, 509)
            }
            // US: "Year" (u 0, 0x24 wide) at (-0x3C, -0x49), "Day" (u 0x24, 0x1C wide) at (6, -0x49), text colour 3, with a shadow
            ShadowText(6, 3, -0x3C, -0x49, 0x24, 0, 0xE7 - 0xD0);
            ShadowText(6, 3, 6, -0x49, 0x1C, 0x24, 0xE7 - 0xD0);
        }

        // renderString(colour, x, y, w, 12, u, v, layer, 1): the page slice in TEXT_COLORS[colour] over a black copy 1 pixel down and right
        void ShadowText(int z, int colour, int x, int y, int w, int u, int v)
        {
            PageText(z, colour, x, y, w, 12, u, v);
            var tex = Page;
            if (tex == null) return;
            Rect uv = new Rect(u / 256f, 1f - (v + 12) / 256f, w / 256f, 12 / 256f);
            Add(z, () => { var o = GUI.color; GUI.color = Color.black; GUI.DrawTextureWithTexCoords(new Rect(X(x + 1), Y(y + 1), w * 4.42f, 12 * 4.42f), tex, uv); GUI.color = o; });
        }

        // renderDateDigits (US): DAY + 1 in 2 places from x 0x25, YEAR + 1 in up to 3 from x -0x16, 9 apart, 7 x 12 each, colour set 12
        void DateDigits()
        {
            Digits(Day, 2, 0x25);
            Digits(Year, 3, -0x16);
        }

        void Digits(int value, int max, int x0)
        {
            string s = Mathf.Max(0, value).ToString();
            if (s.Length > max) s = s.Substring(s.Length - max);
            for (int j = 0; j < s.Length; j++)
            {
                int d = s[j] - '0', tx, ty = 0x34;
                if (d == 0) tx = 0x78;
                else if (d < 5) { tx = (d - 1) * 8 + 0x60; ty = 0x28; }
                else tx = (d - 5) * 8 + 0x50;
                Rect4(5, x0 + j * 9, -0x4A, 7, 12, tx + 1, ty + 0xC0, 12, false);
            }
        }

        // renderTriangleCursor: 8 pieces round the option (corners 4 x 4, edges stretched), page (896, 256) v 251-255, colour set 12, layer 5
        void Cursor(int sel, int yOff)
        {
            var tex = Set(12);
            if (tex == null || sel < 0 || sel >= 8) return;
            int bx = _opt[sel].X - 0x46, by = _opt[sel].Y - 0x53;
            for (int i = 0; i < 8; i++)
            {
                float u0 = _cu0[i], u1 = _cu1[i], v0 = _cv0[i] - 0xC0, v1 = _cv1[i] - 0xC0;
                int x = bx + _cx[i], y = by + _cy[i] + yOff, w = _cw[i], h = _ch[i];
                // a piece whose u or v runs backwards is drawn mirrored
                Rect uv = new Rect(u0 / tex.width, 1f - v1 / tex.height, (u1 - u0) / tex.width, (v1 - v0) / tex.height);
                Add(5, () => GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, h * 4.42f), tex, uv));
            }
        }
    }
}
