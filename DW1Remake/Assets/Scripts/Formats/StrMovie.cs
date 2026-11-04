using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// A PlayStation .STR movie read straight from the disc's raw sectors: video sectors (STR header 0x0160 / 0x8001, frame chunks) hold
    /// MDEC bitstream version 2 or 3 frames; XA sectors (sub-header sub-mode bit 2) hold ADPCM audio. The game plays them at double CD
    /// speed (CdlSetmode 0x80: 150 sectors a second), so a frame is due when its last sector has been read. Decoding follows the MDEC
    /// chip and Sony's DecDCTvlc: MPEG-1 run/level codes, the default PlayStation quantisation table, 8 x 8 IDCT, YCbCr to RGB.
    /// </summary>
    public sealed class StrMovie
    {
        public const int SectorsPerSecond = 150;

        public int Width, Height;
        public readonly List<int> FrameNumber = new List<int>();          // the STR header's frame number
        public readonly List<int> FrameDue = new List<int>();             // index (from the file's start) of the frame's last sector
        readonly List<int[]> _chunks = new List<int[]>();                  // LBAs of each frame's chunks, in order
        public float[] Audio;                                              // interleaved samples, -1..1
        public int Channels = 2, Rate = 37800, FirstAudioSector = -1;

        readonly DiscImage _disc;
        readonly byte[] _sector = new byte[2352];

        StrMovie(DiscImage disc) { _disc = disc; }

        /// <summary>Scans the file once (frame list and all its audio). Frames after endFrame are left out (the game stops there).</summary>
        public static StrMovie Open(DiscImage disc, string path, int endFrame)
        {
            var f = disc.Find(path);
            if (f == null || !disc.IsRaw) return null;
            var m = new StrMovie(disc);
            int maxSectors = (f.Size + 2047) / 2048;
            var audio = new List<float>(1 << 20);
            var hist = new int[4];
            var pending = new Dictionary<int, int[]>();
            var b = m._sector;
            for (int i = 0; i < maxSectors; i++)
            {
                disc.ReadRawSector(f.Lba + i, b);
                int submode = b[18], coding = b[19];
                if ((submode & 0x04) != 0)
                {
                    if (m.FirstAudioSector < 0)
                    {
                        m.FirstAudioSector = i;
                        m.Channels = (coding & 1) != 0 ? 2 : 1;
                        m.Rate = ((coding >> 2) & 3) == 0 ? 37800 : 18900;
                    }
                    DecodeXa(b, 24, (coding & 1) != 0, ((coding >> 4) & 3) == 0, hist, audio);
                    continue;
                }
                int magic = b[24] | b[25] << 8;
                if (magic != 0x0160) { if (m.FrameNumber.Count > 0) break; continue; }
                int chunk = b[28] | b[29] << 8, chunks = b[30] | b[31] << 8, frame = BitConverter.ToInt32(b, 32);
                if (endFrame > 0 && frame > endFrame) break;
                if (m.Width == 0) { m.Width = b[40] | b[41] << 8; m.Height = b[42] | b[43] << 8; }
                int[] list;
                if (!pending.TryGetValue(frame, out list)) { list = new int[chunks]; for (int k = 0; k < chunks; k++) list[k] = -1; pending[frame] = list; }
                if (chunk < list.Length) list[chunk] = f.Lba + i;
                if (chunk == chunks - 1)
                {
                    m.FrameNumber.Add(frame); m.FrameDue.Add(i); m._chunks.Add(list);
                    pending.Remove(frame);
                }
            }
            m.Audio = audio.ToArray();
            return m;
        }

        // ------------------------------------------------------------------ XA ADPCM (CD-ROM XA, 18 sound groups of 128 bytes a sector)

        static readonly int[] K0 = { 0, 60, 115, 98 }, K1 = { 0, 0, -52, -55 };

        static void DecodeXa(byte[] s, int at, bool stereo, bool fourBit, int[] hist, List<float> outp)
        {
            if (!fourBit) return;            // 8-bit XA is not used by these movies
            var l = new float[28 * 4]; var r = new float[28 * 4];
            for (int g = 0; g < 18; g++)
            {
                int gp = at + g * 128;
                int nl = 0, nr = 0;
                for (int u = 0; u < 8; u++)
                {
                    int prm = s[gp + 4 + u], shift = prm & 15, filt = (prm >> 4) & 3;
                    int ch = stereo ? (u & 1) : 0;
                    for (int j = 0; j < 28; j++)
                    {
                        int by = s[gp + 16 + j * 4 + u / 2];
                        int nib = (u & 1) != 0 ? by >> 4 : by & 15;
                        int t = (short)(nib << 12) >> shift;
                        int v = t + ((hist[ch * 2] * K0[filt] + hist[ch * 2 + 1] * K1[filt] + 32) >> 6);
                        if (v > 32767) v = 32767; if (v < -32768) v = -32768;
                        hist[ch * 2 + 1] = hist[ch * 2]; hist[ch * 2] = v;
                        if (!stereo) outp.Add(v / 32768f);
                        else if (ch == 0) l[nl++] = v / 32768f; else r[nr++] = v / 32768f;
                    }
                    if (stereo && (u & 1) != 0)
                    {
                        for (int k = 0; k < 28; k++) { outp.Add(l[k]); outp.Add(r[k]); }
                        nl = 0; nr = 0;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ MDEC video

        // MPEG-1 dct_coeff_next (run / level), code bits; "10" = end of block, "000001" = escape (6-bit run, 10-bit level)
        static readonly string[] AcCodes =
        {
            "11 0 1", "011 1 1", "0100 0 2", "0101 2 1", "00101 0 3", "00111 3 1", "00110 4 1", "000110 1 2", "000111 5 1", "000101 6 1",
            "000100 7 1", "0000110 0 4", "0000100 2 2", "0000111 8 1", "0000101 9 1", "00100110 0 5", "00100001 0 6", "00100101 1 3",
            "00100100 3 2", "00100111 10 1", "00100011 11 1", "00100010 12 1", "00100000 13 1", "0000001010 0 7", "0000001100 1 4",
            "0000001011 2 3", "0000001111 4 2", "0000001001 5 2", "0000001110 14 1", "0000001101 15 1", "0000001000 16 1",
            "000000011101 0 8", "000000011000 0 9", "000000010011 0 10", "000000010000 0 11", "000000011011 1 5", "000000010100 2 4",
            "000000011100 3 3", "000000010010 4 3", "000000011110 6 2", "000000010101 7 2", "000000010001 8 2", "000000011111 17 1",
            "000000011010 18 1", "000000011001 19 1", "000000010111 20 1", "000000010110 21 1", "0000000011010 0 12", "0000000011001 0 13",
            "0000000011000 0 14", "0000000010111 0 15", "0000000010110 1 6", "0000000010101 1 7", "0000000010100 2 5", "0000000010011 3 4",
            "0000000010010 5 3", "0000000010001 9 2", "0000000010000 10 2", "0000000011111 22 1", "0000000011110 23 1", "0000000011101 24 1",
            "0000000011100 25 1", "0000000011011 26 1", "00000000011111 0 16", "00000000011110 0 17", "00000000011101 0 18",
            "00000000011100 0 19", "00000000011011 0 20", "00000000011010 0 21", "00000000011001 0 22", "00000000011000 0 23",
            "00000000010111 0 24", "00000000010110 0 25", "00000000010101 0 26", "00000000010100 0 27", "00000000010011 0 28",
            "00000000010010 0 29", "00000000010001 0 30", "00000000010000 0 31", "000000000011000 0 32", "000000000010111 0 33",
            "000000000010110 0 34", "000000000010101 0 35", "000000000010100 0 36", "000000000010011 0 37", "000000000010010 0 38",
            "000000000010001 0 39", "000000000010000 0 40", "000000000011111 1 8", "000000000011110 1 9", "000000000011101 1 10",
            "000000000011100 1 11", "000000000011011 1 12", "000000000011010 1 13", "000000000011001 1 14", "0000000000010011 1 15",
            "0000000000010010 1 16", "0000000000010001 1 17", "0000000000010000 1 18", "0000000000010100 6 3", "0000000000011010 11 2",
            "0000000000011001 12 2", "0000000000011000 13 2", "0000000000010111 14 2", "0000000000010110 15 2", "0000000000010101 16 2",
            "0000000000011111 27 1", "0000000000011110 28 1", "0000000000011101 29 1", "0000000000011100 30 1", "0000000000011011 31 1"
        };
        // MPEG-1 dct_dc_size_luminance / _chrominance (version 3 DC)
        static readonly string[] LumaDc = { "100", "00", "01", "101", "110", "1110", "11110", "111110", "1111110" };
        static readonly string[] ChromaDc = { "00", "01", "10", "110", "1110", "11110", "111110", "1111110", "11111110" };

        // the PlayStation's default quantisation table (DecDCTReset), natural order; the zig-zag order
        static readonly int[] Q =
        {
            2, 16, 19, 22, 26, 27, 29, 34, 16, 16, 22, 24, 27, 29, 34, 37, 19, 22, 26, 27, 29, 34, 34, 38, 22, 22, 26, 27, 29, 34, 37, 40,
            22, 26, 27, 29, 32, 35, 40, 48, 26, 27, 29, 32, 35, 40, 48, 58, 26, 27, 29, 34, 38, 46, 56, 69, 27, 29, 35, 38, 46, 56, 69, 83
        };
        static readonly int[] Zz =
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
        };

        // 16-bit look-ahead table: (length << 16) | (run << 8) | level; run 0xFE = end of block, 0xFF = escape; 0 = no code
        static int[] _ac, _lumaDc, _chromaDc;
        static float[] _cos;

        static void BuildTables()
        {
            if (_ac != null) return;
            var t = new int[65536];
            Action<string, int> put = (code, val) =>
            {
                int len = code.Length, c = Convert.ToInt32(code, 2), fill = 16 - len;
                for (int k = 0; k < (1 << fill); k++) t[(c << fill) | k] = (len << 16) | val;
            };
            put("10", 0xFE << 8);
            put("000001", 0xFF << 8);
            foreach (var s in AcCodes) { var p = s.Split(' '); put(p[0], (int.Parse(p[1]) << 8) | int.Parse(p[2])); }
            _ac = t;
            System.Func<string[], int[]> dc = a => { var r = new int[a.Length]; for (int k = 0; k < a.Length; k++) r[k] = (a[k].Length << 16) | Convert.ToInt32(a[k], 2); return r; };
            _lumaDc = dc(LumaDc); _chromaDc = dc(ChromaDc);
            _cos = new float[64];
            for (int x = 0; x < 8; x++) for (int u = 0; u < 8; u++) _cos[x * 8 + u] = (u == 0 ? Mathf.Sqrt(0.5f) : 1f) * Mathf.Cos((2 * x + 1) * u * Mathf.PI / 16f) / 2f;
        }

        sealed class Bits
        {
            readonly byte[] _b; int _p; readonly int _end;
            public Bits(byte[] b, int start, int end) { _b = b; _p = start * 8; _end = end; }
            int Bit(int at)
            {
                int w = at >> 4, o = w * 2;
                if (o + 1 >= _end) return 0;
                int v = _b[o] | _b[o + 1] << 8;
                return (v >> (15 - (at & 15))) & 1;
            }
            public int Peek16() { int v = 0; for (int i = 0; i < 16; i++) v = v << 1 | Bit(_p + i); return v; }
            public void Skip(int n) { _p += n; }
            public int Read(int n) { int v = 0; for (int i = 0; i < n; i++) v = v << 1 | Bit(_p++); return v; }
            // table[k] = (length << 16) | code of size k
            public int ReadVlc(int[] table)
            {
                int code = 0;
                for (int len = 1; len <= 16; len++)
                {
                    code = code << 1 | Bit(_p + len - 1);
                    for (int k = 0; k < table.Length; k++)
                        if (table[k] == ((len << 16) | code)) { _p += len; return k; }
                }
                throw new FormatException("bad DC code");
            }
        }

        readonly float[] _blk = new float[6 * 64], _tmp = new float[64];
        readonly int[] _coef = new int[64];
        byte[] _frame = new byte[0];

        /// <summary>Decodes frame i into px (Width x Height, bottom row first as a Unity texture). False when the frame is broken.</summary>
        public bool Decode(int i, Color32[] px)
        {
            BuildTables();
            var lbas = _chunks[i];
            if (_frame.Length < lbas.Length * 2016) _frame = new byte[lbas.Length * 2016];
            for (int k = 0; k < lbas.Length; k++)
            {
                if (lbas[k] < 0) return false;
                _disc.ReadRawSector(lbas[k], _sector);
                Buffer.BlockCopy(_sector, 24 + 32, _frame, k * 2016, 2016);
            }
            int qs = _frame[4] | _frame[5] << 8, ver = _frame[6] | _frame[7] << 8;
            var bs = new Bits(_frame, 8, lbas.Length * 2016);
            int[] pred = new int[3];
            int mbw = (Width + 15) / 16, mbh = (Height + 15) / 16;
            try
            {
                for (int mx = 0; mx < mbw; mx++)
                    for (int my = 0; my < mbh; my++)
                    {
                        // Cr, Cb, Y1 (top left), Y2 (top right), Y3, Y4
                        for (int bi = 0; bi < 6; bi++)
                        {
                            Array.Clear(_coef, 0, 64);
                            int kind = bi == 0 ? 0 : bi == 1 ? 1 : 2;
                            if (ver == 3)
                            {
                                int size = bs.ReadVlc(kind == 2 ? _lumaDc : _chromaDc), diff = 0;
                                if (size != 0) { int v = bs.Read(size); diff = (v >> (size - 1)) != 0 ? v : v - (1 << size) + 1; }
                                pred[kind] += diff * 4;
                                _coef[0] = pred[kind] * Q[0];
                            }
                            else
                            {
                                int dc = bs.Read(10); if ((dc & 0x200) != 0) dc -= 0x400;
                                _coef[0] = dc * Q[0];
                            }
                            int at = 0;
                            while (true)
                            {
                                int e = _ac[bs.Peek16()];
                                if (e == 0) throw new FormatException("bad AC code");
                                bs.Skip(e >> 16);
                                int run = (e >> 8) & 0xFF, level = e & 0xFF;
                                if (run == 0xFE) break;
                                if (run == 0xFF) { run = bs.Read(6); level = bs.Read(10); if ((level & 0x200) != 0) level -= 0x400; }
                                else if (bs.Read(1) != 0) level = -level;
                                at += run + 1;
                                if (at > 63) throw new FormatException("block overrun");
                                int z = Zz[at];
                                int mag = Math.Abs(level) * Q[z] * qs / 8;
                                _coef[z] = level < 0 ? -mag : mag;
                            }
                            Idct(_coef, _blk, bi * 64);
                        }
                        for (int yy = 0; yy < 16; yy++)
                        {
                            int py = my * 16 + yy;
                            if (py >= Height) break;
                            for (int xx = 0; xx < 16; xx++)
                            {
                                int pxx = mx * 16 + xx;
                                if (pxx >= Width) break;
                                int yb = 2 + (yy >> 3) * 2 + (xx >> 3);
                                float Y = _blk[yb * 64 + (yy & 7) * 8 + (xx & 7)] + 128f;
                                int ci = (yy >> 1) * 8 + (xx >> 1);
                                float cr = _blk[ci], cb = _blk[64 + ci];
                                float r = Y + 1.402f * cr, g = Y - 0.3437f * cb - 0.7143f * cr, bl = Y + 1.772f * cb;
                                px[(Height - 1 - py) * Width + pxx] = new Color32(Clamp(r), Clamp(g), Clamp(bl), 255);
                            }
                        }
                    }
            }
            catch (FormatException) { return false; }
            return true;
        }

        static byte Clamp(float v) { int i = Mathf.RoundToInt(v); return (byte)(i < 0 ? 0 : i > 255 ? 255 : i); }

        // 8 x 8 inverse DCT, rows then columns
        void Idct(int[] c, float[] outp, int at)
        {
            for (int y = 0; y < 8; y++)
                for (int u = 0; u < 8; u++)
                {
                    float s = 0f;
                    for (int v = 0; v < 8; v++) { int cv = c[v * 8 + u]; if (cv != 0) s += _cos[y * 8 + v] * cv; }
                    _tmp[y * 8 + u] = s;
                }
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    float s = 0f;
                    for (int u = 0; u < 8; u++) s += _cos[x * 8 + u] * _tmp[y * 8 + u];
                    outp[at + y * 8 + x] = s;
                }
        }
    }
}
