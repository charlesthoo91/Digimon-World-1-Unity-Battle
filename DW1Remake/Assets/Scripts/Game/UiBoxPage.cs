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
        protected void String(int z, int x, int y, int w, int u, int v)
        {
            var tex = Page;
            if (tex == null || w <= 0) return;
            Color t = TextColor[0];
            Rect uv = new Rect(u / 256f, 1f - (v + 12) / 256f, w / 256f, 12 / 256f);
            Add(z, () => { var o = GUI.color; GUI.color = t; GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, 12 * 4.42f), tex, uv); GUI.color = o; });
            Add(z, () => { var o = GUI.color; GUI.color = Color.black; GUI.DrawTextureWithTexCoords(new Rect(X(x + 1), Y(y + 1), w * 4.42f, 12 * 4.42f), tex, uv); GUI.color = o; });
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

        // drawLine3P: two lines, (a)-(b) and (b)-(c)
        protected void Line3(int z, Color c, int ax, int ay, int bx, int by, int cx, int cy) { LineCmd(z, c, ax, ay, bx, by); LineCmd(z, c, bx, by, cx, cy); }

        // the game's line colours are 0x00BBGGRR
        protected static Color Bgr(int v) { return new Color32((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), 255); }
    }
}
