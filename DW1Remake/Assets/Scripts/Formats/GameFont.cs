using System;
using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// The game's own text font (font.c, US build), read from the exe at run time: 79 glyphs of 12 x 11 pixels, one bit a pixel (0 = ink),
    /// and each glyph's advance width after its 11 rows (GLYPH_DATA 0x80130880, 24 bytes each). CHAR_TO_GLYPH_TABLE (0x801307E0) gives each glyph's
    /// Shift-JIS code; ASCII becomes Shift-JIS through CHAR_MAPPING_NORMAL / CHAR_MAPPING_SPECIAL (sjis.c), '.' and '\'' by drawString itself.
    /// The ink is colour 1 of FONT_CLUT (0x80130FE8), the colour setTextColor leaves by default.
    /// </summary>
    public sealed class GameFont
    {
        const uint CharTable = 0x801307E0, Glyphs = 0x80130880, Clut = 0x80130FE8, MapNormal = 0x8012F39C, MapSpecial = 0x8012F3A8;
        const int Count = 79, W = 12, H = 11;

        readonly ushort[] _codes = new ushort[Count];
        readonly int[] _width = new int[Count];
        readonly ushort[] _normal = new ushort[6], _special = new ushort[66];
        readonly bool[] _ink = new bool[Count * W * H];        // glyph g, row y, column x: ink
        public Texture2D Sheet { get; private set; }      // the glyphs side by side, 12 x 11 each, ink white (tinted when drawn)
        public Color Ink { get; private set; }
        readonly Color[] _colours = new Color[16];

        /// <summary>Text colour code n (string control 01 n, setTextColor): the pixel value drawGlyph writes, so entry n of FONT_CLUT.</summary>
        public Color Colour(int n) { return _colours[n & 15]; }

        public static GameFont Load(DiscImage disc)
        {
            if (!disc.Exists("/SLUS_010.32")) return null;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint load = BitConverter.ToUInt32(exe, 0x18);
            Func<uint, int> at = a => (int)(a - load + 0x800);
            var f = new GameFont();
            for (int i = 0; i < Count; i++) f._codes[i] = BitConverter.ToUInt16(exe, at(CharTable) + 2 * i);
            for (int i = 0; i < 6; i++) f._normal[i] = BitConverter.ToUInt16(exe, at(MapNormal) + 2 * i);
            for (int i = 0; i < 66; i++) f._special[i] = BitConverter.ToUInt16(exe, at(MapSpecial) + 2 * i);
            for (int i = 0; i < 16; i++)
            {
                int k = BitConverter.ToUInt16(exe, at(Clut) + 2 * i);
                f._colours[i] = new Color32((byte)((k & 31) * 255 / 31), (byte)(((k >> 5) & 31) * 255 / 31), (byte)(((k >> 10) & 31) * 255 / 31), 255);
            }
            f.Ink = f._colours[1];
            var px = new Color32[Count * W * H];
            for (int g = 0; g < Count; g++)
            {
                int o = at(Glyphs) + g * 24;
                for (int y = 0; y < H; y++)
                {
                    int row = BitConverter.ToUInt16(exe, o + 2 * y);
                    for (int x = 0; x < W; x++)
                        if ((row & (0x8000 >> x)) == 0) { px[(H - 1 - y) * Count * W + g * W + x] = new Color32(255, 255, 255, 255); f._ink[(g * H + y) * W + x] = true; }
                }
                f._width[g] = BitConverter.ToUInt16(exe, o + 22);
            }
            f.Sheet = new Texture2D(Count * W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            f.Sheet.SetPixels32(px); f.Sheet.Apply();
            return f;
        }

        // convertAsciiToJis (sjis.c); drawString turns '.' into 0x8142 and '\'' into 0x8175 first
        ushort ToJis(char ch)
        {
            if (ch >= 0x8000) return ch;      // already a Shift-JIS code (the button symbols)
            if (ch == '.') return 0x8142;
            if (ch == '\'') return 0x8175;
            int a = ch;
            if (a >= 0x30 && a <= 0x39) return (ushort)(a + _normal[0] - _normal[1]);
            if (a >= 0x41 && a <= 0x5A) return (ushort)(a + _normal[2] - _normal[3]);
            if (a >= 0x61 && a <= 0x7A) return (ushort)(a + _normal[4] - _normal[5]);
            int k;
            if (a >= 0x20 && a <= 0x2F) k = a - 0x20;
            else if (a >= 0x3A && a <= 0x40) k = a - 0x20 - 0xA;
            else if (a >= 0x5B && a <= 0x60) k = a - 0x20 - 0x24;
            else if (a >= 0x7B && a <= 0x7E) k = a - 0x20 - 0x3E;
            else return 0;
            return k >= 0 && k < 33 ? _special[k * 2] : (ushort)0;
        }

        // drawGlyph: a code not in the table falls on the last glyph (the game writes the code there as the end marker)
        int GlyphOf(char ch)
        {
            ushort code = ToJis(ch);
            for (int i = 0; i < Count - 1; i++) if (_codes[i] == code) return i;
            return Count - 1;
        }

        /// <summary>Width in PS1 pixels (sum of the glyph advances).</summary>
        public int Measure(string s)
        {
            int w = 0;
            foreach (char ch in s) if (!IsMark(ch)) w += _width[GlyphOf(ch)];
            return w;
        }

        /// <summary>drawString into a text page (pixels bottom row first, as a Unity texture): white ink with its top left at (x, y).
        /// drawGlyph (US) loads the whole 12 x 11 cell, blank pixels too, at x rounded down to a multiple of 4 (one VRAM word holds 4 pixels),
        /// so a later string overwrites what is under its cells.</summary>
        public void Render(string s, Color32[] page, int pageW, int pageH, int x, int y)
        {
            Render(s, page, pageW, pageH, x, y, new Color32(255, 255, 255, 255));
        }

        // a colour code kept in script text (ScriptRunner.ColourMark + n: string control 01 n)
        const char Mark = (char)0xE000;
        static bool IsMark(char ch) { return ch >= Mark && ch <= Mark + 15; }

        /// <summary>The same with the glyphs in one colour (setTextColor: FONT_CLUT entry n, see Colour); a colour code in the text (01 n) switches it, as drawString does.</summary>
        public void Render(string s, Color32[] page, int pageW, int pageH, int x, int y, Color32 ink)
        {
            foreach (char ch in s)
            {
                if (IsMark(ch)) { ink = _colours[ch - Mark]; continue; }
                int g = GlyphOf(ch), cx = x / 4 * 4;
                for (int r = 0; r < H; r++)
                    for (int c = 0; c < W; c++)
                    {
                        int px = cx + c, py = y + r;
                        if (px < 0 || px >= pageW || py < 0 || py >= pageH) continue;
                        page[(pageH - 1 - py) * pageW + px] = _ink[(g * H + r) * W + c] ? ink : new Color32(0, 0, 0, 0);
                    }
                x += _width[g];
            }
        }

        /// <summary>Draws a line with its top left at (x, y) in canvas units; px = canvas units per PS1 pixel.</summary>
        public void Draw(string s, float x, float y, float px, Color tint)
        {
            var o = GUI.color; GUI.color = tint;
            foreach (char ch in s)
            {
                if (IsMark(ch)) continue;
                int g = GlyphOf(ch);
                GUI.DrawTextureWithTexCoords(new Rect(x, y, W * px, H * px), Sheet, new Rect(g * W / (float)(Count * W), 0f, W / (float)(Count * W), 1f));
                x += _width[g] * px;
            }
            GUI.color = o;
        }
    }
}
