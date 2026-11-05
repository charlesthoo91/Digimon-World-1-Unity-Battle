using System;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The game's UI boxes (main/ui.c, US build), shared by the item list box and the dialogue box: UI_BOX_DATA's states, the box growing out of
    /// a small square in 4 frames (renderUIBoxAnim) or appearing at once (createStaticUIBox), renderUIBoxStatic / renderUIBoxBorder, and the
    /// selection cursor (renderSelectionCursor). Positions are PS1 pixels from the middle of the screen.
    /// </summary>
    public abstract class UiBoxPage : PsPage
    {
        // UI_BOX_DATA: state 0 none, 2 growing, 1 open, 3 shrinking; frame 0-5
        public sealed class UiBox { public RectInt Rect, Origin; public int State, Frame, Color, Features, Id; }

        public Action<int> Sound;

        // UI_BOX_COLORS
        static readonly Color32[] BoxColours = { new Color32(0, 0, 0, 255), new Color32(0x2D, 0x38, 0x40, 255) };

        /// <summary>createAnimatedUIBox: sound 0, then 4 frames of growing outline.</summary>
        public void Open(UiBox b, RectInt rect, RectInt origin, int flags)
        {
            int features = (flags >> 4) & 3;
            b.Rect = rect; b.Origin = origin; b.Features = features; b.Color = features == 0 ? 1 : 0;
            b.State = 2; b.Frame = 0;
            if (Sound != null) Sound(0);
        }

        /// <summary>createStaticUIBox: open at once, no sound.</summary>
        public void OpenStatic(UiBox b, RectInt rect, int flags)
        {
            int features = (flags >> 4) & 3;
            b.Rect = rect; b.Origin = rect; b.Features = features; b.Color = features == 0 ? 1 : 0;
            b.State = 1; b.Frame = 0;
        }

        /// <summary>triggerBoxCloseFlag -> removeAnimatedUIBox: it shrinks back into where it came from.</summary>
        public void Close(UiBox b) { if (b.State != 0) b.State = 3; }

        /// <summary>removeStaticUIBox: gone at once.</summary>
        public void CloseNow(UiBox b) { b.State = 0; b.Frame = 0; }

        protected void Snd(int n) { if (Sound != null) Sound(n); }

        // renderUIBoxAnimated, once a game frame (20 a second)
        protected void Step(UiBox b)
        {
            if (b.State == 2) { b.Frame++; if (b.Frame > 4) b.State = 1; }
            else if (b.State == 3)
            {
                b.Frame--;
                if (b.Frame == 4 && Sound != null) Sound(1);
                if (b.Frame <= 0) { b.Frame = 0; b.State = 0; }
            }
        }

        // renderUIBoxStatic (or the outline of renderUIBoxAnim while it grows); ordering-table slot 6 - box id
        protected void DrawBox(UiBox b, Action content)
        {
            if (b.State == 0) return;
            int z = 6 - b.Id;
            if (b.State != 1)
            {
                var s = b.Origin; var f = b.Rect; int k = Mathf.Clamp(b.Frame, 0, 4);
                int x0 = s.x + k * (f.x - s.x) / 4, y0 = s.y + k * (f.y - s.y) / 4;
                int x1 = s.xMax + k * (f.xMax - s.xMax) / 4, y2 = s.yMax + k * (f.yMax - s.yMax) / 4;
                var grey = Bgr(0x808080);
                Line3(z, grey, x0, y0, x1, y0, x1, y2);
                Line3(z, grey, x1, y2, x0, y2, x0, y0);
                return;
            }
            var r = b.Rect;
            content();
            Border(z, r);
            Color black = Bgr(0x020202);
            LineCmd(z, black, r.x + 3, r.y + 3, r.x + 3, r.y + r.height - 3);
            LineCmd(z, black, r.x + r.width - 4, r.y + 3, r.x + r.width - 4, r.y + r.height - 3);
            if ((b.Features & 1) != 0)
            {
                LineCmd(z, black, r.x + 3, r.y + 13, r.x + r.width - 3, r.y + 13);
                LineCmd(z, Bgr(0xFAD990), r.x + 3, r.y + 14, r.x + r.width - 3, r.y + 14);
                LineCmd(z, black, r.x + 3, r.y + 15, r.x + r.width - 3, r.y + 15);
            }
            var c = BoxColours[Mathf.Clamp(b.Color, 0, 1)];
            Box(z, r.x + 4, r.y + 3, r.width - 8, r.height - 3, c.r, c.g, c.b, (b.Features & 2) != 0 ? 1 : 0);
        }

        // renderUIBoxBorder: four 4 x 4 corners from SYSTEM_W (CLUT (96,492)), then black / gold / brown lines
        void Border(int z, RectInt r)
        {
            int[] cu = { 0x78, 0x7C, 0x78, 0x7C }, cv = { 0x10, 0x10, 0x14, 0x14 };
            for (int i = 0; i < 4; i++) SysIcon(z, i % 2 == 0 ? r.x : r.x + r.width - 4, i < 2 ? r.y : r.y + r.height - 4, 4, 4, cu[i], cv[i] + 0x80, 4);
            Color black = Bgr(0x020202), gold = Bgr(0xFAD990), brown = Bgr(0xC59F4A);
            int x = r.x + 4, x1 = r.x + r.width - 4, y = r.y;
            LineCmd(z, black, x, y, x1, y); LineCmd(z, gold, x, y + 1, x1, y + 1); LineCmd(z, black, x, y + 2, x1, y + 2);
            y = r.y + r.height - 3;
            LineCmd(z, black, x, y, x1, y); LineCmd(z, gold, x, y + 1, x1, y + 1); LineCmd(z, black, x, y + 2, x1, y + 2);
            int ya = r.y + 4, yb = r.y + r.height - 3;
            LineCmd(z, black, r.x, ya, r.x, yb); LineCmd(z, brown, r.x + 1, ya, r.x + 1, yb); LineCmd(z, gold, r.x + 2, ya, r.x + 2, yb);
            int xr = r.x + r.width - 3;
            LineCmd(z, gold, xr, ya, xr, yb); LineCmd(z, brown, xr + 1, ya, xr + 1, yb); LineCmd(z, black, xr + 2, ya, xr + 2, yb);
        }

        // renderSelectionCursor: a light and a dark edge, and a grey half-blended fill
        protected void SelectionCursor(int z, int x, int y, int w, int h)
        {
            Line3(z, Bgr(0xB0B0B0), x, y + h - 1, x, y, x + w - 1, y);
            Line3(z, Bgr(0x121212), x + w, y, x + w, y + h, x, y + h);
            Box(z, x + 1, y + 1, w - 1, h - 1, 0x80, 0x80, 0x80, 1);
        }

        // renderString with a shadow: the page slice in TEXT_COLORS[0], and a black copy 1 pixel down and right under it
        protected void String(int z, int x, int y, int w, int u, int v, bool shadow = true)
        {
            var tex = Page;
            if (tex == null || w <= 0) return;
            Color t = TextColor[0];
            Rect uv = new Rect(u / 256f, 1f - (v + 12) / 256f, w / 256f, 12 / 256f);
            Add(z, () => { var o = GUI.color; GUI.color = t; GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, 12 * 4.42f), tex, uv); GUI.color = o; });
            if (shadow) Add(z, () => { var o = GUI.color; GUI.color = Color.black; GUI.DrawTextureWithTexCoords(new Rect(X(x + 1), Y(y + 1), w * 4.42f, 12 * 4.42f), tex, uv); GUI.color = o; });
        }

        // renderMenuTab: SYSTEM_W u 0xD4 / 0xE2 / 0xDB, v 0x8C, colour set (96, 492); a tab that is not chosen (layer 1) is dimmed and a pixel shorter
        protected void MenuTab(int x, int w, int layer)
        {
            int h = layer == 1 ? 15 : 16;
            var tex = SysPal(4);
            Color c = (layer & 1) != 0 ? new Color(0x32 / 128f, 0x32 / 128f, 0x32 / 128f, 1f) : Color.white;
            Picture(5, tex, x, -0x68, 7, h, 0xD4, 0x8C - 0x80, c);
            for (int i = 0; i < (w - 0xE) / 4; i++) Picture(5, tex, x + 7 + i * 4, -0x68, 4, h, 0xE2, 0x8C - 0x80, c);
            Picture(5, tex, x + w - 9, -0x68, 7, h, 0xDB, 0x8C - 0x80, c);
        }

        // ---- the text: drawString (US, script_textbox.c) with the codes the menus use
        public const char Gap = '\u000F', Tab16 = '\u0016', Tab17 = '\u0017', Tab18 = '\u0018', Tab19 = '\u0019', Tab1A = '\u001A', Tab1B = '\u001B', Tab1C = '\u001C';
        const char Mark = (char)0xE000;      // a colour code (01 n), as GameFont keeps it
        protected static string Ink(int n) { return ((char)(Mark + n)).ToString(); }

        /// <summary>intToStringSJIS: 'digits' places; a leading 0 is a space (flag 0) or left out (flag 1); the last place is always written.</summary>
        public static string Digits(int value, int digits, bool skip = false)
        {
            var sb = new System.Text.StringBuilder();
            bool started = false;
            for (int d = digits; d > 0; d--)
            {
                int div = 1; for (int k = 1; k < d; k++) div *= 10;
                int c = (value / div) % 10;
                if (d != 1 && c == 0 && !started) { if (!skip) sb.Append(' '); continue; }
                if (c != 0) started = true;
                sb.Append((char)('0' + c));
            }
            return sb.ToString();
        }

        // drawString into the page: 0x0F clears 6 pixels and moves on 6; 0x16 / 0x18 / 0x19 / 0x1A / 0x1C clear an area and jump to x 0x69 / 0x60 /
        // 0x9C / 0xB4 / 0x64; a space (0x8140) clears 12 pixels and moves on by the last glyph's advance (the US code never sets it for a space;
        // at the start of a string ours uses 0); any other glyph is drawn whole (drawGlyph) and moves on by its own width
        protected void DrawCoded(Color32[] px, string s, int x, int y, Color32 ink)
        {
            if (Font == null || s == null) return;
            int pos = 0, adv = 0;
            foreach (char ch in s)
            {
                if (ch >= Mark && ch <= Mark + 15) { ink = Font.Colour(ch - Mark); continue; }
                switch (ch)
                {
                    case Gap: Clear(px, x + pos, y, 6); pos += 6; continue;
                    case Tab16: Clear(px, x + pos - 6, y, 0x69); pos = 0x69; continue;
                    case Tab17: Clear(px, x + pos, y, 0x69); pos = 0x69; continue;
                    case Tab18: Clear(px, x + pos, y, 0x60); pos = 0x60; continue;
                    case Tab19: Clear(px, x + pos, y, 0x9C); pos = 0x9C; continue;
                    case Tab1A: Clear(px, x + pos, y, 0xB4); pos = 0xB4; continue;
                    case Tab1B: Clear(px, x + pos, y, 0xA0); pos = 0xA0; continue;
                    case Tab1C: Clear(px, x + pos, y, 0x64); pos = 0x64; continue;
                    case '\n': return;
                    case ' ': case '　': Clear(px, x + pos, y, 12); pos += adv; continue;
                }
                string one = ch.ToString();
                Font.Render(one, px, 256, 256, x + pos, y, ink);
                adv = Font.Measure(one);
                pos += adv;
            }
        }

        protected static void Clear(Color32[] px, int x, int y, int w)
        {
            for (int r = 0; r < 12; r++)
                for (int c = 0; c < w; c++)
                {
                    int u = x + c, v = y + r;
                    if (u < 0 || u >= 256 || v < 0 || v >= 256) continue;
                    px[(255 - v) * 256 + u] = new Color32(0, 0, 0, 0);
                }
        }

        // ITEM_MENU_HEADER_UVS (exe 0x8012FE24): the item menus' headers on SYSTEM_W; renderItemMenuSprite (colour set (96, 492))
        readonly int[,] _hdr = new int[7, 4];
        protected void LoadHeaders() { for (int i = 0; i < 7; i++) for (int k = 0; k < 4; k++) _hdr[i, k] = S16(At(0x8012FE24) + i * 8 + k * 2); }
        protected void Header(int z, int idx, int x, int y) { SysIcon(z, x, y, _hdr[idx, 2], _hdr[idx, 3], _hdr[idx, 0] - 0x200, _hdr[idx, 1] - 0x100, 4); }

        // renderItemMenuScrollBar: the bar's rect, the list's length, its top row and how many rows show
        protected void ScrollBarAt(int z, int x, int y, int w, int h, int count, int top, int visible)
        {
            int rows = Mathf.Max(count, visible);
            int track = h * 100 / rows, thumbY = y + 1 + track * top / 100;
            track = track * visible / 100;
            Color shadow = Bgr(0x020202), light = Bgr(0xA08769);
            Line3(z, shadow, x + w + 1, y, x, y, x, y + h);
            Line3(z, light, x, y + h + 1, x + w + 1, y + h + 1, x + w + 1, y + 1);
            Line3(z, light, x + w, thumbY, x + 1, thumbY, x + 1, thumbY + track - 2);
            Line3(z, shadow, x + 1, thumbY + track - 1, x + w, thumbY + track - 1, x + w, thumbY + 1);
            Box(z, x + 2, thumbY + 1, w - 2, track - 2, 0x5B, 0x70, 0x80);
            Box(z, x + 1, y + 1, w, h, 0x35, 0x4B, 0x5C);
        }

        // renderHorizontalLine: black, 0xA08769, black, one under the other
        protected void HLine(int z, int x, int y, int w)
        {
            LineCmd(z, Bgr(0x020202), x, y, x + w - 1, y);
            LineCmd(z, Bgr(0xA08769), x, y + 1, x + w - 1, y + 1);
            LineCmd(z, Bgr(0x020202), x, y + 2, x + w - 1, y + 2);
        }

        // renderInsetWithoutBox: light bottom and right edges, dark top and left
        protected void Inset(int z, int x, int y, int w, int h)
        {
            Line3(z, Bgr(0xA08769), x + 1, y + h - 1, x + w - 1, y + h - 1, x + w - 1, y);
            Line3(z, Bgr(0x020202), x + w - 1, y, x, y, x, y + h - 1);
        }

        // renderKeeperTableBox: light top and left edges, dark bottom and right
        protected void TableBox(int z, int x, int y, int w, int h)
        {
            Line3(z, Bgr(0xA08769), x + w - 1, y, x, y, x, y + h - 1);
            Line3(z, Bgr(0x020202), x, y + h - 1, x + w - 1, y + h - 1, x + w - 1, y);
        }

        // drawLine3P: two lines, (a)-(b) and (b)-(c)
        protected void Line3(int z, Color c, int ax, int ay, int bx, int by, int cx, int cy) { LineCmd(z, c, ax, ay, bx, by); LineCmd(z, c, bx, by, cx, cy); }

        // the game's line colours are 0x00BBGGRR
        protected static Color Bgr(int v) { return new Color32((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), 255); }
    }
}
