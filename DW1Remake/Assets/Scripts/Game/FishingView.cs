using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Fishing's screen pieces (fish/fish.c, FISH_REL.BIN): the bait menu (FISH_openBaitMenu / FISH_renderBaitMenu: UI box 3, two columns of the
    /// bag's food, 9 rows, a scroll bar when there are more), the text box (FISH_showTextBox / FISH_renderTextBox: static UI box 2 with up to
    /// four lines) and the tension gauge (FISH_drawTensionGauge: FISH_SPRITES 31 and up to 9 bars of sprite 32 in the gauge's colour; ordering
    /// places 10 and 9, below 0x20, so they stay on the screen while the picture scrolls). The room keeps the state; this only draws it.
    /// Positions are PS1 pixels from the middle of the screen.
    /// </summary>
    public sealed class FishingView : UiBoxPage
    {
        public readonly UiBox Bait = new UiBox { Id = 3 }, Text = new UiBox { Id = 2 };
        public ItemIcons ItemPics;
        public BattleArt Art;                       // the digit sheet of renderItemAmount and the "x" of the bait rows
        public List<ItemEntry> Items;
        public ScriptState State;

        // FishingBaitMenu
        public readonly int[] BaitSlots = new int[30];     // bag slots of the food, -1 = none
        public int Column, CursorRow, RowOffset, VisibleRows = 9, TotalRows;
        public bool ScrollBar;                              // features 6 (more rows than fit)

        // FishingView.lines
        public readonly string[] TextLines = new string[4];

        // FishingTension
        public bool Gauge;
        public int GaugeBars;
        public Color32 GaugeColour = new Color32(255, 255, 255, 255);
        public int GaugeX = -95, GaugeY = -64;              // MAIN_D_80134974
        public Texture2D Sheet;                             // FISHING.TIM's page, colour set of the gauge sprites
        public int[] FrameSpr, BarSpr;                      // FishSprite u0, v0, u1, v1, ox, oy
        Texture2D _bar; Color32 _barColour; bool _barMade;

        FishingView() { }

        public static FishingView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new FishingView();
            v.Init(disc, font);
            return v;
        }

        /// <summary>renderUIBoxAnimated for both boxes, once a game frame.</summary>
        public void StepBoxes() { Step(Bait); Step(Text); }

        public void Draw()
        {
            if (Bait.State == 0 && Text.State == 0 && !Gauge) return;
            BuildText();
            Begin();
            DrawBox(Bait, BaitContent);
            DrawBox(Text, TextContent);
            if (Gauge) DrawGauge();
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // the text area: FISH_showTextBox draws line i at (0, i x 12); FISH_drawBaitMenuRow draws row r's two names at (i x 0x60, (r % rows) x 12)
        void BuildText()
        {
            var key = new System.Text.StringBuilder();
            bool bait = Bait.State != 0, text = Text.State != 0;
            if (text) for (int i = 0; i < 4; i++) key.Append(TextLines[i] ?? "\u0001").Append('|');
            if (bait)
            {
                key.Append(RowOffset).Append(':');
                for (int i = 0; i < 30; i++) key.Append(BaitSlots[i] < 0 ? -1 : State.Slots[BaitSlots[i]]).Append(',');
            }
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                if (text) for (int i = 0; i < 4; i++) if (TextLines[i] != null) Font.Render(TextLines[i], px, 256, 256, 0, i * 12);
                if (!bait) return;
                for (int r = RowOffset; r < RowOffset + VisibleRows && r < 15; r++)
                    for (int i = 0; i < 2; i++)
                    {
                        int slot = BaitSlots[r * 2 + i];
                        if (slot < 0) continue;
                        int id = State.Slots[slot];
                        if (id == 0xFF || Items == null || id >= Items.Count) continue;
                        Font.Render(Items[id].Name, px, 256, 256, i * 0x60, (r % VisibleRows) * 12);
                    }
            });
        }

        // FISH_renderBaitMenu: per cell the item's picture, its name (colour 0), the "x" and the amount; then the cursor round the chosen cell
        void BaitContent()
        {
            int z = 6 - Bait.Id, x = Bait.Rect.x, y = Bait.Rect.y;
            for (int row = 0; row < VisibleRows; row++)
                for (int col = 0; col < 2; col++)
                {
                    int r = row + RowOffset, cell = col + r * 2;
                    if (cell >= 30) continue;
                    int slot = BaitSlots[cell];
                    if (slot < 0) continue;
                    int id = State.Slots[slot];
                    if (id == 0xFF) continue;
                    if (ItemPics != null)
                    {
                        var uv = ItemPics.Uv(id); var sheet = ItemPics.Sheet; int ix = x + (col != 0 ? 0x98 : 8), iy = y + 0x0E + row * 0x12;
                        Add(z, () => GUI.DrawTextureWithTexCoords(new Rect(X(ix), Y(iy), 16 * 4.42f, 16 * 4.42f), sheet, uv));
                    }
                    String(z, x + (col != 0 ? 0xAB : 0x1B), y + 0x10 + row * 0x12, 0x60, col * 0x60, (r % VisibleRows) * 12);
                    // the "x": page (896, 256), colour set (256, 484), u 0x80 v 0x9C, 8 x 8
                    int mx = x + (col != 0 ? 0x10D : 0x7D), my = y + 0x14 + row * 0x12;
                    if (Art != null) { var art = Art; Add(z, () => art.Hud(4, 0x80, 0x9C - 128, 8, 8, mx, my)); }
                    Amount(z, x + (col != 0 ? 0x116 : 0x86), y + 0x12 + row * 0x12, 2, id < State.Bag.Length ? State.Bag[id] : 0);
                }
            SelectionCursor(z, x + 6 + Column * 144, y + 0x0D + (CursorRow - RowOffset) * 18, 0x8E, 0x12);
            if (ScrollBar) BoxScrollBar(z, Bait.Rect);
        }

        // renderUIBox with features bit 2: the scroll bar at the box's right edge (ui.c)
        void BoxScrollBar(int z, RectInt r)
        {
            if (TotalRows <= VisibleRows) return;
            Color dark = Bgr(0x020202), light = Bgr(0xA08769);
            Line3(z, dark, r.x + r.width - 13, r.y + r.height - 10, r.x + r.width - 13, r.y + 13, r.x + r.width - 6, r.y + 13);
            Line3(z, light, r.x + r.width - 6, r.y + 14, r.x + r.width - 6, r.y + r.height - 10, r.x + r.width - 12, r.y + r.height - 10);
            int h25 = r.height - 25;
            int rowHeight = h25 * VisibleRows / TotalRows;
            int barTop = r.y + 14 + (h25 - rowHeight) * RowOffset / (TotalRows - VisibleRows);
            Line3(z, dark, r.x + r.width - 7, barTop, r.x + r.width - 7, barTop + rowHeight, r.x + r.width - 13, barTop + rowHeight);
            Line3(z, light, r.x + r.width - 12, barTop + rowHeight, r.x + r.width - 12, barTop, r.x + r.width - 6, barTop);
            Box(z, r.x + r.width - 11, barTop + 1, 4, rowHeight - 1, 0x5B, 0x70, 0x80);
            Box(z, r.x + r.width - 12, r.y + 14, 6, r.height - 24, 0x35, 0x4B, 0x5C);
        }

        // renderItemAmount (utils2.c): n places, right-aligned, 6 apart; each digit 6 x 10 at u 0x89 + 6 x digit, v 0x9C, colour 0
        void Amount(int z, int x, int y, int n, int value)
        {
            if (Art == null) { Number(z, 0, x, y, n, value); return; }
            string s = Mathf.Clamp(value, 0, 99).ToString();
            if (s.Length > n) s = s.Substring(s.Length - n);
            var art = Art;
            for (int k = 0; k < s.Length; k++)
            {
                int d = s[k] - '0', dx = x + (n - s.Length + k) * 6;
                Add(z, () => art.Hud(4, d * 6 + 0x89, 0x9C - 128, 6, 10, dx, y));
            }
        }

        // FISH_renderTextBox: each line with renderString(0, x + 5, y + i x 13 + 3, 0xFF, 12, 0, i x 12)
        void TextContent()
        {
            int z = 6 - Text.Id, x = Text.Rect.x, y = Text.Rect.y;
            for (int i = 0; i < 4; i++) if (TextLines[i] != null) String(z, x + 5, y + i * 13 + 3, 0xFF, 0, i * 12);
        }

        // FISH_drawTensionGauge: the frame (sprite 31) at ordering place 10, then n = remap(level, -limit, limit, 0, 9) bars (sprite 32) 3 apart
        // at place 9, tinted by the gauge's colour (the PS1 multiplies the texel by colour / 128)
        void DrawGauge()
        {
            if (Sheet == null || FrameSpr == null || BarSpr == null) return;
            Sprite(10, Sheet, GaugeX, GaugeY, FrameSpr);
            var bar = BarTexture();
            if (bar == null) return;
            int w = Mathf.Abs(BarSpr[2] - BarSpr[0]), h = Mathf.Abs(BarSpr[3] - BarSpr[1]);
            for (int i = 0; i < GaugeBars; i++)
            {
                int bx = GaugeX + i * 3 - BarSpr[4], by = GaugeY - BarSpr[5];
                Add(9, () => GUI.DrawTexture(new Rect(X(bx), Y(by), w * 4.42f, h * 4.42f), bar));
            }
        }

        // FISH_drawSprite2D with scale 1, no turn, colour 0x808080
        void Sprite(int z, Texture2D tex, int x, int y, int[] s)
        {
            int w = Mathf.Abs(s[2] - s[0]), h = Mathf.Abs(s[3] - s[1]);
            Picture(z, tex, x - s[4], y - s[5], w, h, Mathf.Min(s[0], s[2]), Mathf.Min(s[1], s[3]));
        }

        Texture2D BarTexture()
        {
            int w = Mathf.Abs(BarSpr[2] - BarSpr[0]), h = Mathf.Abs(BarSpr[3] - BarSpr[1]);
            if (w <= 0 || h <= 0) return null;
            if (_bar == null) _bar = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            if (_barMade && _barColour.r == GaugeColour.r && _barColour.g == GaugeColour.g && _barColour.b == GaugeColour.b) return _bar;
            _barColour = GaugeColour; _barMade = true;
            var px = new Color32[w * h];
            int u0 = Mathf.Min(BarSpr[0], BarSpr[2]), v0 = Mathf.Min(BarSpr[1], BarSpr[3]);
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                {
                    Color32 c = Sheet.GetPixel(u0 + xx, 255 - (v0 + yy));
                    px[(h - 1 - yy) * w + xx] = new Color32((byte)Mathf.Min(255, c.r * GaugeColour.r / 128), (byte)Mathf.Min(255, c.g * GaugeColour.g / 128),
                                                           (byte)Mathf.Min(255, c.b * GaugeColour.b / 128), c.a);
                }
            _bar.SetPixels32(px); _bar.Apply();
            return _bar;
        }
    }
}
