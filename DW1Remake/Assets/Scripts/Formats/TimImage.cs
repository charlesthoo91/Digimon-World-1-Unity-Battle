using System;
using System.Collections.Generic;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// PlayStation TIM texture. Parsed from memory, converted to Unity pixels on demand.
    /// Nothing is written to disk.
    /// </summary>
    public sealed class TimImage
    {
        public int BitsPerPixel;   // 4, 8, 16 or 24
        public int Width, Height;  // in pixels
        public bool HasPalette;
        public int PaletteCount;   // number of colour palettes (CLUTs)
        public int EndOffset;      // where this TIM ends inside the source buffer

        byte[] _src;
        int _pixelOffset;          // start of pixel data
        int _imgWordsPerRow;       // image block width in 16-bit words
        int _clutOffset, _clutWidth;

        static uint U32(byte[] d, int o) { return BitConverter.ToUInt32(d, o); }
        static int U16(byte[] d, int o) { return BitConverter.ToUInt16(d, o); }

        public static bool TryParse(byte[] d, int offset, out TimImage img)
        {
            img = null;
            if (offset < 0 || offset + 20 > d.Length) return false;
            if (U32(d, offset) != 0x10) return false;
            uint flags = U32(d, offset + 4);
            if ((flags & ~0xFu) != 0) return false;

            int mode = (int)(flags & 3);
            bool hasClut = (flags & 8) != 0;
            var t = new TimImage();
            t._src = d;
            t.BitsPerPixel = mode == 0 ? 4 : mode == 1 ? 8 : mode == 2 ? 16 : 24;
            t.HasPalette = hasClut;
            int p = offset + 8;

            if (hasClut)
            {
                if (p + 12 > d.Length) return false;
                int bnum = (int)U32(d, p);
                int cw = U16(d, p + 8), ch = U16(d, p + 10);
                if (bnum < 12 || cw == 0 || ch == 0 || p + bnum > d.Length) return false;
                if (p + 12 + cw * ch * 2 > d.Length) return false;
                t._clutOffset = p + 12;
                t._clutWidth = cw;
                t.PaletteCount = Math.Max(1, (cw * ch) / (t.BitsPerPixel == 4 ? 16 : 256));
                // Palettes are rows of 16 (4bpp) or 256 (8bpp) colours
                t._clutWidth = t.BitsPerPixel == 4 ? 16 : 256;
                p += bnum;
            }
            else t.PaletteCount = 1;

            if (p + 12 > d.Length) return false;
            int ib = (int)U32(d, p);
            int w = U16(d, p + 8), h = U16(d, p + 10);
            if (ib < 12 || w == 0 || h == 0 || h > 1024 || p + ib > d.Length) return false;
            if (p + 12 + w * h * 2 > d.Length) return false;

            t._imgWordsPerRow = w;
            t._pixelOffset = p + 12;
            t.Width = t.BitsPerPixel == 4 ? w * 4 : t.BitsPerPixel == 8 ? w * 2 : t.BitsPerPixel == 16 ? w : (w * 2) / 3;
            t.Height = h;
            t.EndOffset = p + ib;
            img = t;
            return true;
        }

        /// <summary>Finds every TIM stored back to back in a buffer.</summary>
        public static List<TimImage> ParseAll(byte[] d)
        {
            var list = new List<TimImage>();
            int off = 0;
            while (off < d.Length)
            {
                TimImage t;
                if (TryParse(d, off, out t)) { list.Add(t); off = t.EndOffset; continue; }
                int a4 = (off + 3) & ~3;
                if (a4 != off && TryParse(d, a4, out t)) { list.Add(t); off = t.EndOffset; continue; }
                int a2k = (off + 2047) & ~2047;
                if (a2k != off && TryParse(d, a2k, out t)) { list.Add(t); off = t.EndOffset; continue; }
                break;
            }
            return list;
        }

        Color32 FromBgr555(int v)
        {
            if (v == 0) return new Color32(0, 0, 0, 0); // PS1: pure 0 = transparent
            // a semi-transparent primitive blends only the colours with the top (STP) bit set: layer 1 keeps the solid ones, layer 2 the blended ones
            if (_layer == 1 && (v & 0x8000) != 0 || _layer == 2 && (v & 0x8000) == 0) return new Color32(0, 0, 0, 0);
            byte r = (byte)((v & 31) * 255 / 31);
            byte g = (byte)(((v >> 5) & 31) * 255 / 31);
            byte b = (byte)(((v >> 10) & 31) * 255 / 31);
            return new Color32(r, g, b, 255);
        }

        int _shift;     // the PS1 can start a colour set part way along a row
        int _layer;     // 0 all colours, 1 only those without the STP bit, 2 only those with it

        /// <summary>One layer of the picture for a semi-transparent primitive: 1 = the solid colours, 2 = the colours the PS1 blends (STP bit set).</summary>
        public Texture2D ToTextureLayer(int palette, int layer)
        {
            _layer = layer;
            var t = ToTexture(palette);
            _layer = 0;
            return t;
        }

        /// <summary>True when some colour of this set has the STP bit, so a semi-transparent primitive has something to blend.</summary>
        public bool HasStp(int palette)
        {
            if (!HasPalette) return false;
            int n = BitsPerPixel == 4 ? 16 : 256;
            for (int i = 0; i < n; i++)
            {
                int o = _clutOffset + (palette * _clutWidth + i) * 2;
                if (o + 2 <= _src.Length && (U16(_src, o) & 0x8000) != 0) return true;
            }
            return false;
        }

        /// <summary>The texture with its colours taken this many entries further along the colour table.</summary>
        public Texture2D ToTexture(int palette, int shift)
        {
            _shift = shift;
            var t = ToTexture(palette);
            _shift = 0;
            return t;
        }

        // colours a program loaded into this picture's colour table at run time (LoadImage of a CLUT row: the effects' BTL_loadClutColors)
        readonly Dictionary<int, int> _clutOverride = new Dictionary<int, int>();
        public void SetPaletteColor(int palette, int index, int bgr555) { _clutOverride[palette * 256 + index] = bgr555; }
        /// <summary>Back to the file's own colours; true when there were changes (pictures made from it are then out of date).</summary>
        public bool ClearPaletteOverrides() { bool had = _clutOverride.Count > 0; _clutOverride.Clear(); return had; }

        Color32 PaletteColor(int palette, int index)
        {
            index += _shift;
            if (!HasPalette) { byte g = (byte)(index * 255 / (BitsPerPixel == 4 ? 15 : 255)); return new Color32(g, g, g, 255); }
            int over;
            if (_clutOverride.Count > 0 && _clutOverride.TryGetValue(palette * 256 + index, out over)) return FromBgr555(over);
            int o = _clutOffset + (palette * _clutWidth + index) * 2;
            if (o + 2 > _src.Length) return new Color32(255, 0, 255, 255);
            return FromBgr555(U16(_src, o));
        }

        /// <summary>Pixels bottom-to-top, as Unity wants them.</summary>
        public Color32[] GetPixels(int palette)
        {
            palette = Mathf.Clamp(palette, 0, PaletteCount - 1);
            var px = new Color32[Width * Height];
            int rowBytes = _imgWordsPerRow * 2;
            for (int y = 0; y < Height; y++)
            {
                int row = _pixelOffset + y * rowBytes;
                int dst = (Height - 1 - y) * Width;
                for (int x = 0; x < Width; x++)
                {
                    Color32 c;
                    switch (BitsPerPixel)
                    {
                        case 4: { int b = _src[row + (x >> 1)]; c = PaletteColor(palette, (x & 1) == 0 ? b & 15 : b >> 4); break; }
                        case 8: c = PaletteColor(palette, _src[row + x]); break;
                        case 16: c = FromBgr555(U16(_src, row + x * 2)); break;
                        default: { int o = row + x * 3; c = new Color32(_src[o], _src[o + 1], _src[o + 2], 255); break; }
                    }
                    px[dst + x] = c;
                }
            }
            return px;
        }

        public Texture2D ToTexture(int palette)
        {
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(GetPixels(palette));
            tex.Apply();
            return tex;
        }
    }
}
