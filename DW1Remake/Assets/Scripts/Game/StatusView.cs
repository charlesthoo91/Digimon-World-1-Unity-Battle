using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// What the game's menu pages are drawn with (game_menu.c): a 256-wide text page that drawString writes into and renderString shows slices of,
    /// renderRectPolyFT4 pictures from ETCDAT/SYSTEM_W.TIM (VRAM 320,128: texture page 5 with v from 128, colour sets from row 488), renderNumber,
    /// renderBox, separator lines (UI_LINES_BORDER_COLOR 0x80123F48) and TEXT_COLORS (0x8012B918). Positions are PS1 pixels from the middle of
    /// the screen. Primitives go into ordering-table slots: a lower slot is drawn over a higher one, and in one slot the one added last is drawn
    /// first, so commands are collected and drawn in that order.
    /// </summary>
    public abstract class PsPage
    {
        protected readonly Color[] LineColor = new Color[4];
        protected readonly Color[] TextColor = new Color[32];
        protected readonly string[] Labels = new string[8];          // STATUS_VIEW_LABELS 0x801247B8
        readonly Texture2D[] _sys = new Texture2D[16];
        protected TimImage SysTim;
        protected GameFont Font;
        protected DiscImage Disc;
        protected byte[] Exe; protected uint Load;

        Texture2D _page; string _pageKey;
        readonly List<KeyValuePair<long, Action>> _cmds = new List<KeyValuePair<long, Action>>();
        int _seq;

        protected void Init(DiscImage disc, GameFont font)
        {
            Disc = disc; Font = font;
            Exe = disc.ReadFile("/SLUS_010.32");
            Load = BitConverter.ToUInt32(Exe, 0x18);
            for (int i = 0; i < 4; i++) { int o = At(0x80123F48) + 3 * i; LineColor[i] = new Color32(Exe[o], Exe[o + 1], Exe[o + 2], 255); }
            for (int i = 0; i < 32; i++) { int o = At(0x8012B918) + 3 * i; TextColor[i] = new Color(Exe[o] / 128f, Exe[o + 1] / 128f, Exe[o + 2] / 128f, 1f); }
            for (int i = 0; i < 8; i++) Labels[i] = Str(BitConverter.ToUInt32(Exe, At(0x801247B8) + 4 * i));
            TimImage.TryParse(disc.ReadFile("/ETCDAT/SYSTEM_W.TIM"), 0, out SysTim);
        }

        protected int At(uint a) { return (int)(a - Load + 0x800); }
        protected int S16(int o) { return BitConverter.ToInt16(Exe, o); }

        // a string of the exe; a Shift-JIS pair (the button symbols) becomes one character holding its code
        protected string Str(uint a)
        {
            var sb = new System.Text.StringBuilder();
            for (int p = At(a); Exe[p] != 0; p++)
            {
                if (Exe[p] >= 0x80 && Exe[p + 1] != 0) { sb.Append((char)(Exe[p] << 8 | Exe[p + 1])); p++; }
                else sb.Append((char)Exe[p]);
            }
            return sb.ToString();
        }

        protected static Texture2D Pal(TimImage tim, Texture2D[] cache, int row)
        {
            if (tim == null || row < 0 || row >= cache.Length || row >= tim.PaletteCount) return null;
            return cache[row] ?? (cache[row] = tim.ToTexture(row));
        }

        // ---- the text page
        protected void BuildPage(string key, Action<Color32[]> draw)
        {
            if (_page != null && key == _pageKey) return;
            _pageKey = key;
            var px = new Color32[256 * 256];
            draw(px);
            if (_page == null) _page = new Texture2D(256, 256, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _page.SetPixels32(px); _page.Apply();
        }
        protected void Write(Color32[] px, string s, int x, int y) { if (Font != null && s != null) Font.Render(s, px, 256, 256, x, y); }

        // ---- the ordering table
        protected void Begin() { _cmds.Clear(); _seq = 0; }
        protected Texture2D Page { get { return _page; } }
        protected void Add(int z, Action a) { _cmds.Add(new KeyValuePair<long, Action>(((long)z << 20) | (long)(_seq++), a)); }
        protected void Flush()
        {
            // higher slots first; inside a slot the last added first
            _cmds.Sort((a, b) => b.Key.CompareTo(a.Key));
            foreach (var c in _cmds) c.Value();
        }

        protected static float X(float x) { return 707.5f + x * 4.42f; }
        protected static float Y(float y) { return 530f + y * 4.42f; }

        protected void Picture(int z, Texture2D tex, int x, int y, int w, int h, int u, int v)
        {
            if (tex == null || w <= 0 || h <= 0) return;
            Add(z, () => GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, h * 4.42f), tex,
                new Rect(u / (float)tex.width, 1f - (v + h) / (float)tex.height, w / (float)tex.width, h / (float)tex.height)));
        }

        // the same with the primitive's colour (0x80 = as the picture is, 0x32 = the dimmed one of renderRectPolyFT4 flag 1)
        protected void Picture(int z, Texture2D tex, int x, int y, int w, int h, int u, int v, Color tint)
        {
            if (tex == null || w <= 0 || h <= 0) return;
            Add(z, () =>
            {
                var o = GUI.color; GUI.color = tint;
                GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, h * 4.42f), tex,
                    new Rect(u / (float)tex.width, 1f - (v + h) / (float)tex.height, w / (float)tex.width, h / (float)tex.height));
                GUI.color = o;
            });
        }

        // SYSTEM_W with one of its colour sets (CLUT y 488 + row)
        protected Texture2D SysPal(int row) { return Pal(SysTim, _sys, row); }

        // renderRectPolyFT4 on texture page 5: SYSTEM_W, v counted from 128
        protected void SysIcon(int z, int x, int y, int w, int h, int u, int v, int row) { Picture(z, Pal(SysTim, _sys, row), x, y, w, h, u, v - 0x80); }

        // renderString: a piece of the text page; a textured primitive multiplies by colour / 128 and clamps
        protected void PageText(int z, int color, int x, int y, int w, int h, int u, int v)
        {
            if (_page == null || w <= 0) return;
            Color ink = Font != null ? Font.Ink : Color.white, t = TextColor[color & 31];
            Color c = new Color(Mathf.Min(1f, ink.r * t.r), Mathf.Min(1f, ink.g * t.g), Mathf.Min(1f, ink.b * t.b), 1f);
            var tex = _page;
            Add(z, () =>
            {
                var o = GUI.color; GUI.color = c;
                GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, h * 4.42f), tex, new Rect(u / 256f, 1f - (v + h) / 256f, w / 256f, h / 256f));
                GUI.color = o;
            });
        }

        // renderString(colour, x, y, w, 12, u, v, layer, shadow): the page slice in TEXT_COLORS[colour], with a black copy 1 pixel down and right
        protected void Words(int z, int colour, int x, int y, int w, int u, int v, bool shadow = true)
        {
            PageText(z, colour, x, y, w, 12, u, v);
            var tex = Page;
            if (!shadow || tex == null || w <= 0) return;
            Rect uv = new Rect(u / 256f, 1f - (v + 12) / 256f, w / 256f, 12 / 256f);
            Add(z, () => { var o = GUI.color; GUI.color = Color.black; GUI.DrawTextureWithTexCoords(new Rect(X(x + 1), Y(y + 1), w * 4.42f, 12 * 4.42f), tex, uv); GUI.color = o; });
        }

        // renderNumber: right-aligned, 8 pixels a digit (the US DIGIT_WIDTH), no leading zeros; the blue comes from the green
        protected void Number(int z, int color, int x, int y, int digits, int value)
        {
            string s = Mathf.Max(0, value).ToString();
            if (s.Length > digits) s = s.Substring(s.Length - digits);
            Color ink = Font != null ? Font.Ink : Color.white, t = TextColor[color & 31];
            Color c = new Color(Mathf.Min(1f, ink.r * t.r), Mathf.Min(1f, ink.g * t.g), Mathf.Min(1f, ink.b * t.g), 1f);
            var f = Font;
            for (int i = 0; i < s.Length; i++)
            {
                string d = s[i].ToString(); float dx = x + (digits - s.Length + i) * 8;
                Add(z, () => { if (f != null) f.Draw(d, X(dx), Y(y), 4.42f, c); });
            }
        }

        // renderBox: flags bit 0 = semi-transparent; with bit 1 the blend is "back - front" (subtract), else half and half
        protected void Box(int z, int x, int y, int w, int h, int r, int g, int b, int flags = 0)
        {
            if (w <= 0 || h <= 0) return;
            Color c;
            if ((flags & 1) == 0) c = new Color32((byte)r, (byte)g, (byte)b, 255);
            else if ((flags & 2) == 0) c = new Color(r / 255f, g / 255f, b / 255f, 0.5f);
            else c = new Color(0f, 0f, 0f, (r + g + b) / (3f * 255f));       // subtract: GUI cannot subtract, so the same amount of black over it (ours)
            Add(z, () => UiKit.Fill(new Rect(X(x), Y(y), w * 4.42f, h * 4.42f), c));
        }

        protected void LineCmd(int z, Color c, int x0, int y0, int x1, int y1)
        {
            Add(z, () =>
            {
                if (x0 == x1) UiKit.Fill(new Rect(X(x0), Y(Mathf.Min(y0, y1)), 4.42f, (Mathf.Abs(y1 - y0) + 1) * 4.42f), c);
                else if (y0 == y1) UiKit.Fill(new Rect(X(Mathf.Min(x0, x1)), Y(y0), (Mathf.Abs(x1 - x0) + 1) * 4.42f, 4.42f), c);
                else
                {
                    int n = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
                    for (int i = 0; i <= n; i++) UiKit.Fill(new Rect(X(Mathf.Round(Mathf.Lerp(x0, x1, i / (float)n))), Y(Mathf.Round(Mathf.Lerp(y0, y1, i / (float)n))), 4.42f, 4.42f), c);
                }
            });
        }

        // renderSeparatorLines: Line {x1, y1, x2, y2, colour, pad} (10 bytes)
        protected void Lines(int z, uint table, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int o = At(table) + 10 * i;
                LineCmd(z, LineColor[Exe[o + 8] & 3], S16(o), S16(o + 2), S16(o + 4), S16(o + 6));
            }
        }

        // IconRect {x, y, w, h, u, v} (8 bytes)
        protected void Icons(int z, uint table, int count, int row, int fixedY = int.MinValue)
        {
            for (int i = 0; i < count; i++)
            {
                int o = At(table) + 8 * i;
                SysIcon(z, S16(o), fixedY != int.MinValue ? fixedY : S16(o + 2), Exe[o + 4], Exe[o + 5], Exe[o + 6], Exe[o + 7] + 0x80, row);
            }
        }
    }

    /// <summary>
    /// The Status page (renderDigimonStatsView, drawDigimonStatsStrings, renderDigimonStatusConditions, US build). Tables: STATS_VIEW_LINES
    /// 0x80123F54, STATS_VIEW_TEXT 0x80124334, STATS_VIEW_ELEMENTS 0x8012437C with STATS_VIEW_ELEMENT_CLUT 0x80123DB8, STATS_VIEW_INSETS
    /// 0x801242CC, condition masks 0x80123DD0. The bars come from ETCTIM.BIN image 5 (VRAM 512,256).
    /// </summary>
    public sealed class StatusView : PsPage
    {
        struct Text { public int X, Y, W, U, V; }
        struct Icon { public int X, Y, W, H, U, V, Row; }

        readonly Text[] _texts = new Text[9];
        readonly Icon[] _icons = new Icon[21];
        readonly RectInt[] _insets = new RectInt[13];
        readonly int[] _condMask = new int[6];
        readonly string[] _statNames = new string[6];
        readonly Texture2D[] _bar = new Texture2D[16];
        TimImage _barTim;

        public static StatusView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new StatusView();
            v.Init(disc, font);
            for (int i = 0; i < 9; i++) { int o = v.At(0x80124334) + 8 * i; v._texts[i] = new Text { X = v.S16(o), Y = v.S16(o + 2), W = v.S16(o + 4), U = v.Exe[o + 6], V = v.Exe[o + 7] }; }
            for (int i = 0; i < 21; i++)
            {
                int o = v.At(0x8012437C) + 8 * i;
                v._icons[i] = new Icon { X = v.S16(o), Y = v.S16(o + 2), W = v.Exe[o + 4], H = v.Exe[o + 5], U = v.Exe[o + 6], V = v.Exe[o + 7], Row = v.Exe[v.At(0x80123DB8) + i] };
            }
            for (int i = 0; i < 13; i++) { int o = v.At(0x801242CC) + 8 * i; v._insets[i] = new RectInt(v.S16(o), v.S16(o + 2), v.S16(o + 4), v.S16(o + 6)); }
            for (int i = 0; i < 6; i++) v._condMask[i] = BitConverter.ToInt32(v.Exe, v.At(0x80123DD0) + 4 * i);
            for (int i = 0; i < 6; i++) v._statNames[i] = v.Str(0x80124C0C + (uint)(12 * i));
            var all = TimImage.ParseAll(disc.ReadFile("/ETCDAT/ETCTIM.BIN"));
            if (all.Count > 5) v._barTim = all[5];
            return v;
        }

        Texture2D Bar(int row) { return Pal(_barTim, _bar, row); }

        // renderInsetBox: dark lines left and top, light ones bottom and right, then the fill (all at the same depth)
        void Inset(int x, int y, int w, int h)
        {
            x -= 0xA0; y -= 0x78;
            Color dark = new Color32(2, 2, 2, 255), light = new Color32(0x69, 0x87, 0xA0, 255);
            LineCmd(5, dark, x, y + h, x, y); LineCmd(5, dark, x, y, x + w, y);
            LineCmd(5, light, x, y + h, x + w, y + h); LineCmd(5, light, x + w, y + h, x + w, y);
            Box(5, x, y, w, h, 0x35, 0x4B, 0x5C);
        }

        public void Draw(ScriptState s, int partnerType, string name, string species, int frames)
        {
            BuildPage(name + "|" + species, px =>
            {
                // drawDigimonStatsStrings
                Write(px, _statNames[2], 0, 0x24);
                Write(px, _statNames[3], 0x28, 0x24);
                Write(px, _statNames[3], 0x24, 0x24);
                Write(px, species, 0x9C, 0x24);
                Write(px, species, 0, 0x48);
                Write(px, name, 0, 0x3C);
                Write(px, Labels[1], 0, 0x30);
                Write(px, Labels[2], 0, 0x18);
            });
            var info = DigimonStatsCatalog.Get(Disc, partnerType);
            var raise = DigimonStatsCatalog.RaiseOf(Disc, partnerType);
            Begin();
            Lines(5, 0x80123F54, 6);
            foreach (var t in _texts) PageText(5, 3, t.X, t.Y + 1, t.W, 12, t.U, t.V);
            foreach (var i in _icons) SysIcon(5, i.X, i.Y, i.W, i.H, i.U, i.V + 0x80, i.Row);
            PageText(5, 0, -0x6D, -0x42, 0x48, 12, 0, 0x3C);
            PageText(5, 0, -0x6C, -0x32, Mathf.Min(species.Length * 10, 0x78), 12, 0, 0x48);
            Number(5, 0, -0x6D, -0x22, 2, s.Age);
            Number(5, 0, -0x23, -0x22, 2, s.Weight);
            Number(5, 0, 0x23, 0x01, 4, s.Stats[6]); Number(5, 0, 0x5F, 0x01, 4, s.Stats[4]);
            Number(5, 0, 0x23, 0x10, 4, s.Stats[7]); Number(5, 0, 0x5F, 0x10, 4, s.Stats[5]);
            Number(5, 0, 0x23, 0x1F, 4, s.Stats[0]); Number(5, 0, 0x23, 0x2E, 4, s.Stats[1]);
            Number(5, 0, 0x23, 0x3D, 4, s.Stats[2]); Number(5, 0, 0x23, 0x4C, 4, s.Stats[3]);
            // renderDigimonStatsBar: width = full * value / limit, 2 high
            StatBar(s.Stats[4], 9999, 100, 0x24, 0x0C); StatBar(s.Stats[5], 9999, 100, 0x24, 0x1B);
            StatBar(s.Stats[0], 999, 50, 0x24, 0x2A); StatBar(s.Stats[1], 999, 50, 0x24, 0x39);
            StatBar(s.Stats[2], 999, 50, 0x24, 0x48); StatBar(s.Stats[3], 999, 50, 0x24, 0x57);
            if (info != null)
            {
                int k = 0;
                for (int i = 0; i < 3; i++)
                {
                    int sp = info.Specials[i];
                    if (sp == 0xFF) continue;
                    int row = sp == 2 || sp == 3 || sp == 4 ? 1 : sp == 5 ? 2 : 0;
                    SysIcon(5, k + 9, -0x33, 12, 12, (frames % 10 < 5 ? 0 : 0x0C) + sp * 0x18 + 0x24, 0x80, row);
                    k += 12;
                }
                if (info.Attribute != 0) SysIcon(5, 0x3B, -0x33, 12, 12, (info.Attribute - 1) * 12, 0x80, 0);
            }
            if (raise != null)
            {
                if (raise.SleepCycle == 5) PageText(5, 0, 0x55, -0x33, 0x30, 12, 0xCC, 0x30);
                if (raise.SleepCycle < 5) PageText(5, 0, 0x55, -0x33, 0x30, 12, raise.SleepCycle * 0x30, 0x18);
            }
            for (int i = 0; i < 3; i++)
            {
                if (i < s.Life) SysIcon(5, i * 14 - 0x54, 0x22, 12, 11, 0xE8, 0x8C, 6);
                SysIcon(5, i * 14 - 0x54, 0x22, 12, 11, 0xF4, 0x8C, 6);
            }
            Conditions(s.Para.Condition, frames);
            int happy = s.Happiness, disc = s.Discipline;
            SysIcon(5, -0x8A, 0x31, 11, 11, happy >= 0 ? 0 : 0x0B, 0xF4, 10);
            if (happy >= 0) { Picture(5, Bar(14), -0x54, 0x35, (byte)(happy / 2), 6, 0x18, 0xF0); Picture(5, Bar(13), -0x54, 0x35, 0x32, 6, 0x4A, 0xF0); }
            else Picture(5, Bar(13), -0x54, 0x35, (byte)((happy + 100) / 2), 6, 0x4A, 0xF0);
            SysIcon(5, -0x8A, 0x40, 11, 11, disc >= 0x32 ? 0x16 : 0x21, 0xF4, 10);
            if (disc >= 0x32) { Picture(5, Bar(14), -0x54, 0x44, (byte)(disc - 0x32), 6, 0x18, 0xF0); Picture(5, Bar(13), -0x54, 0x44, 0x32, 6, 0x4A, 0xF0); }
            else Picture(5, Bar(13), -0x54, 0x44, (byte)disc, 6, 0x4A, 0xF0);
            Box(5, -0x54, 0x52, s.Para.Virus * 3, 6, 0xC8, 0xC8, 0x3C);
            foreach (var r in _insets) Inset(r.x, r.y, r.width, r.height);
            Flush();
        }

        void StatBar(int value, int limit, int full, int x, int y) { Box(5, x, y, (byte)(full * value / limit), 2, 0x32, 0xC8, 0xC8); }

        // renderDigimonStatusConditions: the icons bob on a 16-frame cycle
        void Conditions(int condition, int frames)
        {
            int f = frames % 16;
            int bob = f < 3 ? 0x82 : f < 5 ? 0x80 : f < 7 ? 0x7D : f < 9 ? 0x79 : f < 11 ? 0x7D : f < 13 ? 0x80 : 0x82;
            for (int i = 0; i < 6; i++)
            {
                if ((condition & _condMask[i]) == 0) continue;
                int row = i == 0 || i == 4 ? 2 : i == 5 ? 1 : 0;
                SysIcon(5, i * 15 - 0x8A, bob - 0x78, 12, 12, i * 12 + 0x30, 0x8C, row);
            }
            if ((condition & 8) != 0) SysIcon(5, -0x30, bob - 0x78, 12, 12, 0x60, 0xC8, 0);
        }
    }

    /// <summary>
    /// The Tech page (renderDigimonMovesView US, drawDigimonMovesText, renderDigimonMovesSelected, renderDigimonMoveBox). MENU_STATE 1 = the
    /// Techset with the finisher ("Final") and the help line; 6 = the grid of 7 elements x 8 techniques with the cursor box and the panel
    /// of the technique under it; 7 / 8 the help page (Square). The box shrinking / growing between them (MENU_STATE 2-5) is DigimonMenuView's.
    /// Tables: MOVES_VIEW_TECHSET_TEXT 0x801241CC, MOVES_TECHSET_LINES 0x80123F90, MOVES_VIEW_HELP_LINES 0x80123FCC, MOVES_VIEW_HELP_CORNERS
    /// 0x80124044, DIGIMON_MOVE_INFO_LINES_LEFT / RIGHT 0x80124064 / 0x80124118, TECH_VIEW_LABELS 0x801247D8, MOVE_NAMES 0x80126054.
    /// </summary>
    public sealed class TechView : PsPage
    {
        string _rangeLabels;
        readonly string[] _techLabels = new string[9];

        public static TechView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new TechView();
            v.Init(disc, font);
            v._rangeLabels = v.Str(BitConverter.ToUInt32(v.Exe, v.At(0x801247D8)));
            for (int i = 0; i < 9; i++) v._techLabels[i] = v.Str(BitConverter.ToUInt32(v.Exe, v.At(0x801247D8) + 4 * i));
            return v;
        }

        // the colour set of an element's icon (renderDigimonMoveBox): 2, 3, 4 use row 1, 5 row 2, the others row 0
        static int ElementRow(int e) { return e == 2 || e == 3 || e == 4 ? 1 : e == 5 ? 2 : 0; }

        // equipMove's grid: display row -> element row (0, 5, 1, 4, 2, 3, 6); technique = column + row * 8, + 1 on the last row
        public static int MoveAt(int col, int row)
        {
            int[] map = { 0, 5, 1, 4, 2, 3, 6 };
            int r = map[Mathf.Clamp(row, 0, 6)], id = col + r * 8;
            return r == 6 ? id + 1 : id;
        }

        static string Name(int id) { var t = DigimonStatsCatalog.TechOf(id); return t != null ? t.Name : ""; }

        /// <param name="equipped">EQUIPPED_MOVES: the three set techniques (-1 = empty) and the finisher</param>
        /// <param name="learned">hasMove</param>
        /// <param name="moves">DIGIMON_DATA moves: the partner's 16 technique numbers (255 = none)</param>
        public void Draw(int[] equipped, Func<int, bool> learned, int[] moves, int mode, int col, int row, int frames)
        {
            // mode = MENU_STATE: 1 the Techset, 2-5 the box shrinking and growing (only the top part), 6 the grid, 7 / 8 the help page (Square), 9 back
            bool grid = mode == 6, help = mode == 7 || mode == 8;
            int cursorMove = MoveAt(col, row);
            string key = string.Join(",", equipped) + "|" + (grid && learned(cursorMove) ? cursorMove : -1) + "|" + help;
            BuildPage(key, px =>
            {
                if (help)
                {
                    // drawMoveViewHelpStrings: TECH_VIEW_LABELS 1-8 (STATUS_VIEW_LABELS 9-16) from row 0x18, over the cleared area (0, 24, 256, 200)
                    Write(px, Labels[0], 0, 0);
                    for (int i = 0; i < 8; i++) Write(px, _techLabels[i + 1], 0, i * 12 + 0x18);
                    return;
                }
                // drawDigimonMovesText (and the tab labels at the top of the page)
                Write(px, Labels[0], 0, 0);
                for (int i = 0; i < 3; i++) if (equipped[i] >= 0) Write(px, Name(equipped[i]), 0, 0x18 + 12 * i);
                if (equipped[3] >= 0) Write(px, Name(equipped[3]), 0, 0x3C);
                Write(px, Labels[3], 0, 0x48);
                Write(px, Labels[6], 0x70, 0x6C);
                Write(px, Labels[6], 0x84, 0x6C);
                Write(px, Labels[4], 0, 0x6C);
                Write(px, Labels[5], 0, 0x54);
                Write(px, Labels[7], 0, 0x60);
                Write(px, _rangeLabels, 0, 0x78);
                if (grid && learned(cursorMove)) Write(px, Name(cursorMove), 0, 0x84);
            });
            Begin();
            if (mode == 8) { DrawHelp(); Flush(); return; }
            if (mode <= 0 || mode >= 7) { Flush(); return; }
            // the Techset, on both pages
            Icons(5, 0x801241CC, 13, 4);
            Words(5, 3, -0x8E, -0x57, 0x3C, 0, 0x48);
            for (int j = 0; j < 3; j++)
            {
                if (equipped[j] < 0) continue;
                var mv = DigimonStatsCatalog.TechOf(equipped[j]);
                if (mv == null) continue;
                Words(5, 0, -0x7C, j * 15 - 0x42, 0x78, 0, j * 12 + 0x18);
                Number(5, 0, 0x10, j * 15 - 0x43, 3, mv.Power);
                Number(5, 0, 0x39, j * 15 - 0x43, 3, mv.Mp * 3);
                if (mv.Range != 0) Words(5, 0, 0x65, j * 15 - 0x42, 12, (mv.Range - 1) * 0x24, 0x78);
                if (mv.Status != 0) SysIcon(5, 0x7D, j * 15 - 0x43, 12, 12, (mv.Status - 1) * 12, 0x8C, 0);
            }
            Lines(3, 0x80123F90, 3);
            if (mode == 1) DrawTechset(equipped);
            else if (grid) DrawGrid(equipped, learned, moves, col, row, frames);
            Flush();
        }

        // MENU_STATE 1
        void DrawTechset(int[] equipped)
        {
            Words(5, 3, -0x8E, -0x0F, 0x24, 0x3C, 0x48);
            Icons(5, 0x801241CC, 3, 4, -9);
            SysIcon(5, 0x75, 4, 4, 4, 0x78, 0x8C, 4);
            Lines(5, 0x80123F90 + 30, 3);
            if (equipped[3] >= 0)
            {
                var mv = DigimonStatsCatalog.TechOf(equipped[3]);
                Words(5, 3, -0x8E, -0x0F, 0x24, 0x3C, 0x48);
                Words(5, 0, -0x7C, 1, 0x84, 0, 0x3C);
                if (mv != null)
                {
                    Number(5, 0, 0x10, 0, 3, mv.Power);
                    Words(5, 0, 0x65, 1, 12, (mv.Range - 1) * 12 + 0x48, 0x6C);
                    if (mv.Status != 0) SysIcon(5, 0x7D, 0, 12, 12, (mv.Status - 1) * 12, 0x8C, 0);
                }
            }
            Words(5, 0xF, -0x82, 0x19, 12, 0, 0x54);
            Words(5, 0, -0x76, 0x19, 0xA8, 0x0C, 0x54);
            Words(5, 0, -0x76, 0x27, 0x84, 0x0C, 0x60);
            Words(5, 7, -0x82, 0x27, 12, 0, 0x60);
            Words(5, 0, 0x0E, 0x27, 0x18, 0x90, 0x60);
        }

        // MENU_STATE 6
        void DrawGrid(int[] equipped, Func<int, bool> learned, int[] moves, int col, int row, int frames)
        {
            int boxX = 0x73 + col * 18, boxY = 0x6F + row * 15;
            Words(5, 3, -0x8E, -0x0F, 0x3C, 0x60, 0x48);
            Lines(5, 0x80123FCC, 12);
            Icons(5, 0x80124044, 4, 4);
            Words(5, 0, -0x87, 0x20, 0x30, 0x3C, 0);
            Words(5, 0, -0x81, 0x2E, 0x24, 0, 0x6C);
            Words(5, 0x0C, -0x74, 0x4C, 0x18, 0x30, 0x6C);
            SysIcon(5, -0x74, 0x3C, 10, 10, 0x80, 0x8C, 4);
            for (int j = 0; j < 7; j++)
            {
                SysIcon(5, -0x43, j * 15 - 8, 0x14, 10, j * 0x14, 0x98, 4);
                SysIcon(5, 100, j * 15 - 8, 0x14, 10, j * 0x14, 0x98, 4);
            }
            for (int j = 0; j < 8; j++) SysIcon(5, j * 0x12 - 0x26, -0x0F, 4, 5, j * 4 + 0x94, 0x8C, 4);
            SysIcon(5, boxX - 0xA0, boxY - 0x78, 0x12, 0x10, 0xC0, 0x8C, 4);
            for (int r = 0; r < 7; r++)
                for (int j = 0; j < 8; j++) Box(5, j * 0x12 - 0x2A, r * 15 - 7, 12, 12, 0x4E, 0x60, 0x6E, 0x80);
            Selected(boxX > 0xA8 ? 0 : 1, learned(MoveAt(col, row)) ? MoveAt(col, row) : -1);
            MoveBox(equipped, learned, moves, frames);
        }

        static Color Bgr(int v) { return new Color32((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), 255); }

        // renderRectPolyFT4 flag 0x80 / renderBox flag 0x80: two dark lines round a 12 x 12 square
        void Outline(int z, int x, int y)
        {
            Color c = Bgr(0x020202);
            LineCmd(z, c, x - 1, y - 1, x + 0xD, y - 1); LineCmd(z, c, x + 0xD, y - 1, x + 0xD, y + 0xC);
            LineCmd(z, c, x - 1, y - 1, x - 1, y + 0xC); LineCmd(z, c, x - 1, y + 0xC, x + 0xD, y + 0xC);
        }

        // MENU_STATE 8 (US): "Technique Select Help", the meaning of each colour of the grid
        void DrawHelp()
        {
            LineCmd(5, Bgr(0x020202), -0x92, -4, 0x92, -4);
            LineCmd(5, Bgr(0xFAD990), -0x93, -3, 0x93, -3);
            LineCmd(5, Bgr(0x020202), -0x92, -2, 0x92, -2);
            Words(5, 3, -0x8E, -0x52, 0xA0, 0, 0x30);
            Words(5, 0xE, -0x72, -0x32, 12, 0xCC, 0x30);
            Words(5, 0xE, 6, -0x21, 12, 0xCC, 0x30);
            Words(5, 0xE, -0x2A, -0x13, 12, 0xCC, 0x30);
            // MOVES_VIEW_STRING_SPRITES {posX, posY, uvWidth, uvX, uvY}
            for (int i = 0; i < 12; i++) { int o = At(0x80124234) + 8 * i; Words(5, 0, S16(o), S16(o + 2) + 1, S16(o + 4), Exe[o + 6], Exe[o + 7]); }
            Words(5, 0, 0x42, 0x33, 0x3C, 0x78, 0x48);
            Words(5, 0, 0x42, 0x43, 0x3C, 0x84, 0x54);
            SysIcon(5, -0x81, -0x41, 12, 12, 0xB4, 0x8C, 4);
            SysIcon(5, -0x81, -0x22, 12, 12, 0xB4, 0x8C, 4);
            SysIcon(5, -0x7B, 0x50, 0x12, 0x10, 0xC0, 0x8C, 4);
            // MOVES_VIEW_SPRITES (IconRect), colour sets (96, 488) / (96, 489) for the 6th; the US build skips the 3rd
            for (int i = 0; i < 7; i++)
            {
                if (i == 2) continue;
                int o = At(0x80124294) + 8 * i, x = S16(o), y = S16(o + 2);
                SysIcon(5, x, y, Exe[o + 4], Exe[o + 5], Exe[o + 6], Exe[o + 7] + 0x80, i == 5 ? 1 : 0);
                Outline(2, x, y);
            }
            Box(4, 0x52, -0x22, 12, 12, 200, 0, 0x28, 1); Outline(4, 0x52, -0x22);
            Box(4, -0x78, 2, 12, 12, 200, 0, 0x28, 1); Outline(4, -0x78, 2);
            Box(4, -0x78, 0x22, 12, 12, 0x4E, 0x60, 0x6E); Outline(4, -0x78, 0x22);
            Box(4, -0x78, 0x32, 12, 12, 0x68, 0x68, 0x68, 3); Outline(4, -0x78, 0x32);
            Box(4, -0x78, 0x42, 12, 12, 0x68, 0x68, 0x68, 3); Outline(4, -0x78, 0x42);
        }

        // renderDigimonMovesSelected: the panel on the side away from the cursor
        void Selected(int panel, int moveId)
        {
            Lines(4, panel == 0 ? 0x80124064u : 0x80124118u, 18);
            SysIcon(4, panel * 0x9D - 0x91, 1, 4, 4, 0x78, 0x90, 4);
            SysIcon(4, panel * 0x9D - 0x10, 1, 4, 4, 0x7C, 0x90, 4);
            SysIcon(4, panel * 0xCB - 0x91, 0x5C, 4, 4, 0x78, 0x94, 4);
            SysIcon(4, panel * 0xCB - 0x3E, 0x5C, 4, 4, 0x7C, 0x94, 4);
            if (panel == 0) SysIcon(4, -0x10, 0x15, 4, 4, 0x7C, 0x94, 4); else SysIcon(4, 0x0C, 0x15, 4, 4, 0x78, 0x94, 4);
            Box(4, panel * 0x9D - 0x8F, 3, 0x80, 0x13, 0x32, 0x32, 0x80);
            Box(4, panel * 0xCB - 0x8F, 0x18, 0x57, 0x45, 0x32, 0x32, 0x80);
            SysIcon(3, panel * 0x9D - 0x8C, -2, 0x25, 7, 0x11, 0xB0, 4);
            SysIcon(3, panel * 0xCB - 0x8C, 0x1D, 0x17, 7, 0x5C, 0xA2, 4);
            SysIcon(3, panel * 0xCB - 0x8C, 0x2E, 0x0B, 7, 0x74, 0xA2, 4);
            SysIcon(3, panel * 0xCB - 0x8C, 0x3F, 0x13, 7, 0x4C, 0xA9, 4);
            SysIcon(3, panel * 0xCB - 0x8C, 0x50, 0x10, 7, 0, 0xB0, 4);
            for (int i = 0; i < 4; i++) SysIcon(3, panel * 0xCB - 0x71, i * 0x11 + 0x1B, 10, 10, 0x8A, 0x8C, 4);
            if (moveId < 0) return;
            var mv = DigimonStatsCatalog.TechOf(moveId);
            if (mv == null) return;
            Words(3, 0, panel * 0x9D - 0x8A, 9, 0x78, 0, 0x84);
            Number(3, 0, panel * 0xCB - 0x64, 0x1B, 3, mv.Power);
            Number(3, 0, panel * 0xCB - 0x64, 0x2C, 3, mv.Mp * 3);
            Words(3, 0, panel * 0xCB - 0x64, 0x3E, 0x24, (mv.Range - 1) * 0x24, 0x78);
            int[] su = { 0, 0x84, 0xA0, 0xBC, 0xDB }, sw = { 0, 0x1A, 0x1C, 0x20, 0x1E };
            if (mv.Status >= 1 && mv.Status <= 4) Words(3, 0, panel * 0xCB - 0x64, 0x4F, sw[mv.Status], su[mv.Status], 0x6C);
        }

        // renderDigimonMoveBox: red under the set techniques, grey over those this Digimon cannot use, the element icon on learned ones (blinking)
        void MoveBox(int[] equipped, Func<int, bool> learned, int[] moves, int frames)
        {
            for (int i = 0; i < 3; i++)
            {
                int e = equipped[i];
                if (e < 0 || e >= 0x3A) continue;
                int x = e % 8, y = e < 8 ? 0 : e < 0x10 ? 2 : e < 0x18 ? 4 : e < 0x20 ? 5 : e < 0x28 ? 3 : e < 0x30 ? 1 : 6;
                if (y == 6) x = (e - 1) % 8;
                if (e == 0x30) { x = 4; y = 1; }
                if (e == 0x39) { x = 6; y = 6; }
                Box(4, x * 0x12 - 0x2A, y * 15 - 7, 12, 12, 200, 0, 0x28, 1);
            }
            var sorted = new int[16];
            Array.Copy(moves, sorted, 16);
            Array.Sort(sorted);
            int k = 0;
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < 8; col++)
                {
                    int moveId = row * 8 + col;
                    if (moveId >= 0x30) moveId++;
                    if (moveId == 0x2C && Array.IndexOf(sorted, 0x2C) < 0 && Array.IndexOf(sorted, 0x30) < 0) Box(4, 0x1E, 8, 12, 12, 0x68, 0x68, 0x68, 3);
                    if (moveId == 0x37 && Array.IndexOf(sorted, 0x37) < 0 && Array.IndexOf(sorted, 0x39) < 0) Box(4, 0x42, 0x53, 12, 12, 0x68, 0x68, 0x68, 3);
                    if (k < 16 && moveId == sorted[k]) k++;
                    else if (moveId != 0x2C && moveId != 0x37 && moveId != 0x30 && moveId != 0x39)
                    {
                        int y = moveId < 8 ? 0 : moveId < 0x10 ? 2 : moveId < 0x18 ? 4 : moveId < 0x20 ? 5 : moveId < 0x28 ? 3 : moveId < 0x30 ? 1 : 6;
                        Box(4, col * 0x12 - 0x2A, y * 15 - 7, 12, 12, 0x68, 0x68, 0x68, 3);
                    }
                    if (learned(moveId))
                    {
                        var mv = DigimonStatsCatalog.TechOf(moveId);
                        int sp = mv != null ? mv.Element : 0;
                        int blink = frames % 0x14 < 10 ? 0 : 0x0C;
                        SysIcon(4, col * 0x12 - 0x2A, sp * 15 - 7, 12, 12, blink + sp * 0x18 + 0x24, 0x80, ElementRow(sp));
                    }
                }
        }
    }

    /// <summary>
    /// renderDigiviceEntity: the partner on the Status page, seen from DIGIVICE_ENTITY_VIEW (exe 0x80123860: from (1300, 0, -3280) towards
    /// (0, 0, 0)) with projection 0x200, standing at its RAISE_DATA view position with no rotation, in the first frame of animation 0
    /// (the game restarts animation 0 every frame). Drawn here by a second camera into a picture laid over the page.
    /// The game lights it with one flat light; this model is unlit.
    /// </summary>
    public sealed class DigiviceModel
    {
        const int Layer = 31;
        static readonly Vector3 Away = new Vector3(0f, -5000f, 0f);     // far below every map, seen only by this camera
        GameObject _root, _model; Camera _cam; RenderTexture _rt; int _type = -1;
        Vector3 _eye, _at; bool _viewRead;

        public static void ExcludeFrom(Camera main) { if (main != null) main.cullingMask &= ~(1 << Layer); }

        public void Draw(DiscImage disc, int type, Func<GameObject> build)
        {
            if (build == null) return;
            if (!_viewRead)
            {
                _viewRead = true;
                byte[] exe = disc.ReadFile("/SLUS_010.32");
                uint load = BitConverter.ToUInt32(exe, 0x18);
                int o = (int)(0x80123860 - load + 0x800);
                _eye = new Vector3(BitConverter.ToInt32(exe, o), -BitConverter.ToInt32(exe, o + 4), BitConverter.ToInt32(exe, o + 8)) * 0.01f;
                _at = new Vector3(BitConverter.ToInt32(exe, o + 12), -BitConverter.ToInt32(exe, o + 16), BitConverter.ToInt32(exe, o + 20)) * 0.01f;
            }
            if (_root == null)
            {
                _root = new GameObject("Digivice view");
                var camGo = new GameObject("Digivice camera");
                camGo.transform.SetParent(_root.transform, false);
                _cam = camGo.AddComponent<Camera>();
                _cam.cullingMask = 1 << Layer;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = new Color(0, 0, 0, 0);
                _cam.fieldOfView = 2f * Mathf.Atan(120f / 512f) * Mathf.Rad2Deg;       // GsSetProjection(0x200) on a 240-line screen
                _cam.aspect = 4f / 3f;
                _cam.nearClipPlane = 0.1f; _cam.farClipPlane = 500f;
                _rt = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32);
                _cam.targetTexture = _rt;
            }
            _root.SetActive(true);
            if (_type != type)
            {
                if (_model != null) UnityEngine.Object.Destroy(_model);
                _model = null; _type = type;
                try { _model = build(); } catch (Exception e) { Debug.LogWarning("[menu] digivice model: " + e.Message); }
                if (_model != null)
                {
                    _model.transform.SetParent(_root.transform, false);
                    foreach (var t in _model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
                    var raise = DigimonStatsCatalog.RaiseOf(disc, type);
                    Vector3 p = raise != null ? new Vector3(raise.ViewX, -raise.ViewY, raise.ViewZ) * 0.01f : Vector3.zero;
                    _model.transform.position = Away + p;
                    _model.transform.rotation = Quaternion.identity;
                    var an = _model.GetComponent<DigimonAnimator>();
                    if (an != null) an.SetSlot(0);
                }
            }
            _cam.transform.position = Away + _eye;
            _cam.transform.rotation = Quaternion.LookRotation((_at - _eye).normalized, Vector3.up);
            if (Event.current != null && Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, UiKit.W, UiKit.H), _rt, ScaleMode.StretchToFill, true);
        }

        public void Hide() { if (_root != null && _root.activeSelf) _root.SetActive(false); }
    }
}
