using System;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// A Digimon World 1 map: a grid of 128x128 pre-rendered picture tiles (.TFS) plus the
    /// camera and lighting setup stored in the .MAP file. Built in memory only.
    /// </summary>
    /// <summary>One exit of a map: where you arrive when coming IN through it, and where it leads OUT to.</summary>
    public struct Warp
    {
        public int SpawnX, SpawnY, SpawnZ;   // arrival point (game units)
        public int Rotation;                 // arrival facing, 4096 = full turn
        public int TargetMap, TargetExit;    // where walking onto this exit leads
    }

    /// <summary>A Digimon placed in a map: a friendly NPC (follow mode 1) or a wild one that wanders along waypoints.</summary>
    public sealed class MapDigimon
    {
        public int Type, FollowMode, RotY, Script;
        public int Hp, Mp, Off, Def, Spd, Brain, Bits;      // the fight numbers of this Digimon on this map (record +22 ...)
        public int[] MoveSlots = new int[4], MoveChance = new int[4];   // its techniques as animation slots (255 = none) and how often each is picked
        public Vector3 Pos;                                   // game units
        public System.Collections.Generic.List<Vector3> Waypoints = new System.Collections.Generic.List<Vector3>();
    }

    public sealed class MapBackground
    {
        public System.Collections.Generic.List<MapDigimon> Npcs = new System.Collections.Generic.List<MapDigimon>();
        public int TilesX, TilesY;
        public int[] Tiles;                 // tile id per grid cell, -1 = empty
        public Vector3 Viewpoint, ReferencePoint;
        public int ViewDistance;
        public int PaletteCount;            // 0 = day, 1 = evening, 2 = night (usually)
        public Vector3[] LightDir = new Vector3[3];     // direction of each of the map's 3 lights (game space)
        public Color[] LightColor = new Color[3];
        public string LightInfo = "";
        public Warp[] Warps = new Warp[10];
        public byte[] Collision;            // 100 x 100 walking grid: 0 = free, 0x80 = blocked, other = trigger (exit, door...)

        ushort[][] _palettes;
        byte[][] _pixels;                   // 128*128 palette indices per grid cell (null = empty)

        static int I32(byte[] d, int o) { return BitConverter.ToInt32(d, o); }
        static int S16(byte[] d, int o) { return BitConverter.ToInt16(d, o); }
        static int U16(byte[] d, int o) { return BitConverter.ToUInt16(d, o); }

        /// <summary>Walking-grid value at a game-space position (game units; 100 units per tile). Out of range = blocked.</summary>
        public byte CollisionAt(float gameX, float gameZ)
        {
            int tx = (int)(gameX / 100f) + 50; if (gameX < 0) tx--;
            int tz = 50 - (int)(gameZ / 100f); if (gameZ > 0) tz--;
            if (tx < 0 || tx >= 100 || tz < 0 || tz >= 100) return 0x80;
            return Collision[tx + tz * 100];
        }

        /// <summary>The walking-grid tile under a game position.</summary>
        public static void TileAt(float gameX, float gameZ, out int tx, out int tz)
        {
            tx = Mathf.FloorToInt(gameX / 100f) + 50;
            tz = 49 - Mathf.FloorToInt(gameZ / 100f);
        }

        public bool TileBlocked(int tx, int tz)
        {
            if (tx < 0 || tx >= 100 || tz < 0 || tz >= 100) return true;
            return (Collision[tx + tz * 100] & 0x80) != 0;
        }

        public int PixelWidth { get { return TilesX * 128; } }
        public int PixelHeight { get { return TilesY * 128; } }

        public static MapBackground Parse(byte[] map, byte[] tfs)
        {
            var m = new MapBackground();
            int s = I32(map, 0);
            m.Viewpoint = new Vector3(I32(map, s), I32(map, s + 4), I32(map, s + 8)) * 2f;
            m.ReferencePoint = new Vector3(I32(map, s + 12), I32(map, s + 16), I32(map, s + 20)) * 2f;
            m.ViewDistance = I32(map, s + 108);
            for (int i = 0; i < 3; i++)
            {
                int lo = s + 24 + 24 * i;
                m.LightDir[i] = new Vector3(I32(map, lo), I32(map, lo + 4), I32(map, lo + 8));
                int r = I32(map, lo + 12), g = I32(map, lo + 16), b = I32(map, lo + 20);
                m.LightColor[i] = new Color(Mathf.Clamp01(r / 255f), Mathf.Clamp01(g / 255f), Mathf.Clamp01(b / 255f), 1f);
                m.LightInfo += "L" + i + " dir(" + m.LightDir[i].x + "," + m.LightDir[i].y + "," + m.LightDir[i].z + ") rgb(" + r + "," + g + "," + b + ")  ";
            }
            m.TilesX = I32(map, s + 144);
            m.TilesY = I32(map, s + 148);
            if (m.TilesX < 1 || m.TilesY < 1 || m.TilesX > 64 || m.TilesY > 64) throw new InvalidOperationException("Not a standard map file.");
            // The file starts with a list of section offsets; the last one is the walking grid.
            int sections = s / 4;
            m.Collision = new byte[10000];
            int colOff = sections >= 2 ? I32(map, (sections - 1) * 4) : 0;
            if (colOff > 0 && colOff + 10000 <= map.Length) Buffer.BlockCopy(map, colOff, m.Collision, 0, 10000);
            else for (int i = 0; i < 10000; i++) m.Collision[i] = 0x80;

            // second to last section: the exits (10 of them)
            if (sections >= 3)
            {
                int eo = I32(map, (sections - 2) * 4);
                if (eo > 0 && eo + 120 <= map.Length)
                    for (int e = 0; e < 10; e++)
                        m.Warps[e] = new Warp
                        {
                            SpawnX = S16(map, eo + 2 * e), SpawnY = S16(map, eo + 20 + 2 * e), SpawnZ = S16(map, eo + 40 + 2 * e),
                            Rotation = S16(map, eo + 60 + 2 * e), TargetMap = U16(map, eo + 80 + 2 * e), TargetExit = U16(map, eo + 100 + 2 * e)
                        };
            }

            // the Digimon of the map follow the 120 bytes of exits
            if (sections >= 3)
            {
                int eo2 = I32(map, (sections - 2) * 4);
                int q = eo2 + 120;
                if (eo2 > 0 && q + 2 <= map.Length)
                {
                    int count = S16(map, q); q += 2;
                    for (int i = 0; i < count && i < 16 && q + 84 <= map.Length; i++)
                    {
                        int wc = S16(map, q + 66);
                        var d = new MapDigimon
                        {
                            Type = S16(map, q), FollowMode = S16(map, q + 2),
                            Pos = new Vector3(S16(map, q + 4), S16(map, q + 6), S16(map, q + 8)),
                            RotY = S16(map, q + 12), Script = S16(map, q + 20),
                            Hp = S16(map, q + 22), Mp = S16(map, q + 24), Off = S16(map, q + 30), Def = S16(map, q + 32), Spd = S16(map, q + 34), Brain = S16(map, q + 36), Bits = S16(map, q + 38)
                        };
                        for (int k = 0; k < 4; k++) { d.MoveSlots[k] = S16(map, q + 44 + k * 2); d.MoveChance[k] = S16(map, q + 52 + k * 2); }
                        for (int w = 0; w < wc && w < 32 && q + 84 + 6 * w + 6 <= map.Length; w++)
                            d.Waypoints.Add(new Vector3(S16(map, q + 84 + 6 * w), S16(map, q + 86 + 6 * w), S16(map, q + 88 + 6 * w)));
                        m.Npcs.Add(d);
                        q += 84 + 6 * Math.Max(0, wc);
                    }
                }
            }

            m.Tiles = new int[m.TilesX * m.TilesY];
            for (int i = 0; i < m.Tiles.Length; i++) m.Tiles[i] = I32(map, s + 152 + 4 * i);

            int p = 8;
            m.PaletteCount = I32(tfs, 4);
            m._palettes = new ushort[m.PaletteCount][];
            for (int i = 0; i < m.PaletteCount; i++)
            {
                m._palettes[i] = new ushort[256];
                for (int k = 0; k < 256; k++) m._palettes[i][k] = BitConverter.ToUInt16(tfs, p + 2 * k);
                p += 512;
            }
            m._pixels = new byte[m.Tiles.Length][];
            for (int i = 0; i < m.Tiles.Length; i++)
            {
                if (m.Tiles[i] == -1) continue;
                p += 4;
                m._pixels[i] = new byte[128 * 128];
                Buffer.BlockCopy(tfs, p, m._pixels[i], 0, 128 * 128);
                p += 128 * 128;
            }
            return m;
        }

        /// <summary>One of the map's colour palettes (256 colours, BGR555).</summary>
        public ushort[] GetPalette(int palette)
        {
            palette = Mathf.Clamp(palette, 0, Math.Max(0, PaletteCount - 1));
            return (ushort[])_palettes[palette].Clone();
        }

        public Texture2D BuildTexture(int palette)
        {
            palette = Mathf.Clamp(palette, 0, Math.Max(0, PaletteCount - 1));
            int W = PixelWidth, H = PixelHeight;
            var colors = new Color32[W * H];
            var pal = new Color32[256];
            for (int k = 0; k < 256; k++)
            {
                int v = _palettes[palette][k];
                pal[k] = new Color32((byte)((v & 31) * 255 / 31), (byte)(((v >> 5) & 31) * 255 / 31), (byte)(((v >> 10) & 31) * 255 / 31), 255);
            }
            var black = new Color32(0, 0, 0, 255);
            for (int i = 0; i < colors.Length; i++) colors[i] = black;

            for (int ty = 0; ty < TilesY; ty++)
                for (int tx = 0; tx < TilesX; tx++)
                {
                    byte[] px = _pixels[ty * TilesX + tx];
                    if (px == null) continue;
                    for (int y = 0; y < 128; y++)
                    {
                        int dst = (H - 1 - (ty * 128 + y)) * W + tx * 128;   // Unity textures start at the bottom
                        for (int x = 0; x < 128; x++) colors[dst + x] = pal[px[y * 128 + x]];
                    }
                }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(colors);
            tex.Apply();
            return tex;
        }
    }
}
