using System;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// The foreground pieces of a map (tree tops, rock edges, bridges...). The map file holds a few small
    /// texture pages (TIM images, laid out like the PlayStation's video memory), a list of sprite pieces cut
    /// from them, and a list of instances that say where each piece goes on the painted map.
    /// </summary>
    public sealed class MapOverlay
    {
        public struct Obj { public int TexX, TexY, W, H, WX, WY, WZ, Clut, Trans; }

        public sealed class Inst
        {
            public short[] Frames = new short[8];   // piece numbers of the animation, -1 = end, -2 = hold the previous frame for its time
            public int[] Times = new int[8];        // how long each frame stays (ticks)
            public int X, Y;                        // top-left corner on the painted map (pixels)
            public int Flag;                        // 1 = hidden, 0x80 = free floating (snow)
        }

        public Obj[] Objects = new Obj[0];
        public Inst[] Instances = new Inst[0];

        const int VW = 1024, VH = 512;
        readonly ushort[] _vram = new ushort[VW * VH];

        static int I32(byte[] d, int o) { return BitConverter.ToInt32(d, o); }
        static int U16(byte[] d, int o) { return BitConverter.ToUInt16(d, o); }
        static int S16(byte[] d, int o) { return BitConverter.ToInt16(d, o); }

        public static MapOverlay Parse(byte[] map, int num8, int num4)
        {
            var m = new MapOverlay();
            if (num8 + num4 == 0) return m;
            int s = I32(map, 0);
            int n = s / 4;
            int objIdx = 1 + num8 + num4;
            if (objIdx > n - 3) return m;      // objects, entities and walking grid must follow the images

            for (int i = 0; i < num8 + num4; i++) m.LoadTim(map, I32(map, (1 + i) * 4), i - num8);

            int o = I32(map, objIdx * 4);
            int count = S16(map, o);
            if (count < 0 || count > 1000) return m;
            m.Objects = new Obj[count];
            for (int i = 0; i < count; i++)
            {
                int p = o + 2 + 18 * i;
                m.Objects[i] = new Obj
                {
                    TexX = U16(map, p), TexY = U16(map, p + 2), W = U16(map, p + 4), H = U16(map, p + 6),
                    WX = S16(map, p + 8), WY = S16(map, p + 10), WZ = S16(map, p + 12),
                    Clut = U16(map, p + 14) & 0xFF, Trans = U16(map, p + 16) & 0xFF
                };
            }
            int q = o + 2 + 18 * count;
            int ic = U16(map, q);
            if (ic > 1000) return m;
            m.Instances = new Inst[ic];
            for (int i = 0; i < ic; i++)
            {
                int p = q + 2 + 38 * i;
                var inst = new Inst();
                for (int k = 0; k < 8; k++) { inst.Frames[k] = (short)S16(map, p + 2 * k); inst.Times[k] = U16(map, p + 16 + 2 * k); }
                inst.X = S16(map, p + 32); inst.Y = S16(map, p + 34); inst.Flag = U16(map, p + 36);
                m.Instances[i] = inst;
            }
            return m;
        }

        // Puts one texture page into the emulated video memory. idx4 = number of this page among the 4-bit pages (negative for 8-bit pages).
        void LoadTim(byte[] d, int off, int idx4)
        {
            if (off < 0 || off + 20 > d.Length || BitConverter.ToUInt32(d, off) != 0x10) return;
            uint flag = BitConverter.ToUInt32(d, off + 4);
            bool is4 = (flag & 3) == 0;
            int p = off + 8;
            int cx = 0, cy = 0, cw = 0, ch = 0, cdata = 0;
            if ((flag & 8) != 0)
            {
                int csz = I32(d, p);
                cx = U16(d, p + 4); cy = U16(d, p + 6); cw = U16(d, p + 8); ch = U16(d, p + 10); cdata = p + 12;
                p += csz;
            }
            int x = U16(d, p + 4), y = U16(d, p + 6), w = U16(d, p + 8), h = U16(d, p + 10);
            int data = p + 12;
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    int vx = x + c, vy = y + r, so = data + 2 * (r * w + c);
                    if (vx < VW && vy < VH && so + 2 <= d.Length) _vram[vy * VW + vx] = (ushort)U16(d, so);
                }
            if ((flag & 8) == 0) return;
            if (!is4)
            {
                if (cy == 480) return;      // the shared palette (the map's day/evening/night colours)
                for (int r = 0; r < ch; r++)
                    for (int c = 0; c < cw; c++)
                    {
                        int so = cdata + 2 * (r * cw + c);
                        if (cx + c < VW && cy + r < VH && so + 2 <= d.Length) _vram[(cy + r) * VW + cx + c] = (ushort)U16(d, so);
                    }
            }
            else if (idx4 == 0)
            {
                for (int r = 0; r < ch; r++)
                    for (int c = 0; c < 16; c++)
                    {
                        int so = cdata + 2 * (r * cw + c);
                        if (16 * r + c < VW && so + 2 <= d.Length) _vram[486 * VW + 16 * r + c] = (ushort)U16(d, so);
                    }
            }
        }

        /// <summary>True if this piece uses the map's own palette (so it changes with the time of day).</summary>
        public bool UsesMapPalette(int obj) { return Objects[obj].Clut == 0xFF; }

        /// <summary>Pixels of one piece, bottom row first (Unity order). bg = the map palette in use (256 colours, BGR555).</summary>
        public Color32[] BuildObject(int index, ushort[] bg)
        {
            var o = Objects[index];
            var px = new Color32[o.W * o.H];
            for (int y = 0; y < o.H; y++)
                for (int x = 0; x < o.W; x++)
                {
                    int u = o.TexX + x, v = o.TexY + y;
                    int c;
                    if (o.Clut < 16)
                    {
                        int word = 384 + (u / 256) * 64 + (u % 256) / 4;
                        int nib = (_vram[Math.Min(v, VH - 1) * VW + Math.Min(word, VW - 1)] >> (4 * (u % 4))) & 15;
                        c = _vram[486 * VW + o.Clut * 16 + nib];
                    }
                    else
                    {
                        int word = 384 + (u / 256) * 128 + (u % 256) / 2;
                        int w16 = _vram[Math.Min(v, VH - 1) * VW + Math.Min(word, VW - 1)];
                        int idx = ((u & 1) == 0) ? (w16 & 255) : (w16 >> 8);
                        c = o.Clut == 0xFF ? bg[idx] : _vram[Math.Min(468 + o.Clut, VH - 1) * VW + idx];
                    }
                    Color32 col;
                    if (c == 0) col = new Color32(0, 0, 0, 0);          // fully transparent
                    else col = new Color32((byte)((c & 31) * 255 / 31), (byte)(((c >> 5) & 31) * 255 / 31), (byte)(((c >> 10) & 31) * 255 / 31), 255);
                    px[(o.H - 1 - y) * o.W + x] = col;
                }
            return px;
        }
    }
}
