using System;
using System.Collections.Generic;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// The PlayStation's 1024 x 512 video memory, filled from TIM files the way LoadImage puts them there (pictures and colour sets at the
    /// places their headers name). Primitives read it through a texture page (x, y, colour depth) and a colour set (x, y), as the GPU does.
    /// Used by the arena (STD_REL.BIN), whose sprites and models name their pictures by video memory place only.
    /// </summary>
    public sealed class PsxVram
    {
        public const int W = 1024, H = 512;
        public readonly ushort[] Px = new ushort[W * H];
        int _version;
        readonly Dictionary<long, KeyValuePair<int, Texture2D>> _cache = new Dictionary<long, KeyValuePair<int, Texture2D>>();

        /// <summary>Every TIM in d from 'offset' on, back to back (STD_loadTIMToVRAM, STD_loadArenaTIMToVRAM, loadTIMFile); at most 'count'.</summary>
        public void LoadTims(byte[] d, int offset = 0, int count = int.MaxValue)
        {
            int p = offset;
            for (int i = 0; i < count && p + 8 <= d.Length; i++)
            {
                int end = LoadTim(d, p);
                if (end <= p) break;
                p = end;
            }
        }

        /// <summary>One TIM at 'off'; returns where it ends (or 'off' when it is not a TIM).</summary>
        public int LoadTim(byte[] d, int off)
        {
            if (off + 8 > d.Length || BitConverter.ToUInt32(d, off) != 0x10) return off;
            uint flag = BitConverter.ToUInt32(d, off + 4);
            int p = off + 8;
            if ((flag & 8) != 0)
            {
                int csz = BitConverter.ToInt32(d, p);
                Put(d, p + 12, BitConverter.ToUInt16(d, p + 4), BitConverter.ToUInt16(d, p + 6), BitConverter.ToUInt16(d, p + 8), BitConverter.ToUInt16(d, p + 10));
                p += csz;
            }
            if (p + 12 > d.Length) return off;
            int isz = BitConverter.ToInt32(d, p);
            Put(d, p + 12, BitConverter.ToUInt16(d, p + 4), BitConverter.ToUInt16(d, p + 6), BitConverter.ToUInt16(d, p + 8), BitConverter.ToUInt16(d, p + 10));
            _version++;
            return p + isz;
        }

        void Put(byte[] d, int src, int x, int y, int w, int h)
        {
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    int so = src + 2 * (r * w + c), vx = x + c, vy = y + r;
                    if (so + 2 > d.Length || vx >= W || vy >= H) continue;
                    Px[vy * W + vx] = BitConverter.ToUInt16(d, so);
                }
        }

        /// <summary>LoadImage of words already written into Px: the pages made from them are made again.</summary>
        public void Changed() { _version++; }

        /// <summary>MoveImage: copies a w x h block of 16-bit words.</summary>
        public void Move(int sx, int sy, int w, int h, int dx, int dy)
        {
            var tmp = new ushort[w * h];
            for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) tmp[r * w + c] = Px[((sy + r) & (H - 1)) * W + ((sx + c) & (W - 1))];
            for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) Px[((dy + r) & (H - 1)) * W + ((dx + c) & (W - 1))] = tmp[r * w + c];
            _version++;
        }

        /// <summary>The colour a primitive reads at (u, v) of a texture page: mode 0 4-bit, 1 8-bit, 2 16-bit; page at (px, py); colour set at (cx, cy).</summary>
        public ushort Texel(int mode, int px, int py, int cx, int cy, int u, int v)
        {
            int vy = (py + (v & 255)) & (H - 1);
            u &= 255;
            if (mode == 0)
            {
                int nib = (Px[vy * W + ((px + u / 4) & (W - 1))] >> (4 * (u & 3))) & 15;
                return Px[(cy & (H - 1)) * W + ((cx + nib) & (W - 1))];
            }
            if (mode == 1)
            {
                int w16 = Px[vy * W + ((px + u / 2) & (W - 1))];
                int idx = (u & 1) == 0 ? (w16 & 255) : (w16 >> 8);
                return Px[(cy & (H - 1)) * W + ((cx + idx) & (W - 1))];
            }
            return Px[vy * W + ((px + u) & (W - 1))];
        }

        /// <summary>
        /// A whole 256 x 256 page as a texture (Unity order: bottom row first, so uv y = 1 - (v + h) / 256). layer 0 = every colour, 1 = the ones
        /// without the STP bit, 2 = the STP ones. Colour 0x0000 is always see-through. Kept until the video memory changes.
        /// </summary>
        public Texture2D Page(int mode, int px, int py, int cx, int cy, int layer = 0)
        {
            px &= ~63; py &= ~255; cx &= ~15;      // a page starts at a multiple of 64 x 256; a colour set's x is kept in steps of 16 (getClut)
            long key = ((long)mode << 40) | ((long)(px >> 6) << 34) | ((long)(py >> 8) << 33) | ((long)(cx >> 4) << 26) | ((long)cy << 16) | (long)layer;
            KeyValuePair<int, Texture2D> e;
            if (_cache.TryGetValue(key, out e) && e.Key == _version) return e.Value;
            var tex = e.Value;
            if (tex == null) tex = new Texture2D(256, 256, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pix = new Color32[256 * 256];
            for (int v = 0; v < 256; v++)
                for (int u = 0; u < 256; u++)
                    pix[(255 - v) * 256 + u] = Colour(Texel(mode, px, py, cx, cy, u, v), layer);
            tex.SetPixels32(pix); tex.Apply();
            _cache[key] = new KeyValuePair<int, Texture2D>(_version, tex);
            return tex;
        }

        /// <summary>A texture page word (getTPage / GetTPage: bits 0-3 x / 64, bit 4 y / 256, bits 5-6 abr, bits 7-8 mode) and a CLUT word (x / 16, y).</summary>
        public Texture2D Page(int tpage, int clut, int layer = 0)
        {
            return Page((tpage >> 7) & 3, (tpage & 15) * 64, ((tpage >> 4) & 1) * 256, (clut & 63) * 16, (clut >> 6) & 511, layer);
        }

        public static Color32 Colour(ushort c, int layer)
        {
            if (c == 0 || layer == 1 && (c & 0x8000) != 0 || layer == 2 && (c & 0x8000) == 0) return new Color32(0, 0, 0, 0);
            return new Color32((byte)((c & 31) * 255 / 31), (byte)(((c >> 5) & 31) * 255 / 31), (byte)(((c >> 10) & 31) * 255 / 31), 255);
        }

        public bool HasStp(int mode, int px, int py, int cx, int cy, int u, int v, int w, int h)
        {
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { ushort c = Texel(mode, px, py, cx, cy, u + x, v + y); if (c != 0 && (c & 0x8000) != 0) return true; }
            return false;
        }

        public void Destroy()
        {
            foreach (var e in _cache.Values) if (e.Value != null) UnityEngine.Object.Destroy(e.Value);
            _cache.Clear();
        }
    }
}
