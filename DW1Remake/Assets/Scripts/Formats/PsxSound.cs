using System;
using System.Collections.Generic;

namespace DW1Remake.Formats
{
    public sealed class VabTone { public int Mode, Vol, Pan, Center, Shift, Min, Max, BendMin, BendMax, Adsr1, Adsr2, Vag; }

    /// <summary>
    /// A PlayStation sound bank (the "pBAV" part of a .VHB file): programs, tones and samples, read at run time from the player's disc.
    /// A .VHB file starts with u32 offsets of the VAB head and the sample body; FAALL.VHB has also the offset of its song table.
    /// </summary>
    public sealed class VabBank
    {
        public int MasterVol;
        public int[] ProgVol = new int[128], ProgPan = new int[128], ToneStart = new int[128], ToneCount = new int[128];
        public VabTone[] Tones;
        public short[][] Samples;     // by vag number; 0 is empty
        public int[] Loop;            // sample where a looping sound repeats from, -1 = plays once

        public static VabBank Parse(byte[] d, int file)
        {
            int vh = file + BitConverter.ToInt32(d, file), vb = file + BitConverter.ToInt32(d, file + 4);
            if (BitConverter.ToUInt32(d, vh) != 0x56414270) throw new InvalidOperationException("Not a sound bank.");
            int programs = BitConverter.ToUInt16(d, vh + 18), vags = BitConverter.ToUInt16(d, vh + 22);
            var b = new VabBank { MasterVol = d[vh + 24] };
            int p = vh + 32, ordinal = 0;
            for (int i = 0; i < 128; i++, p += 16)
            {
                b.ToneCount[i] = d[p]; b.ProgVol[i] = d[p + 1]; b.ProgPan[i] = d[p + 4];
                b.ToneStart[i] = ordinal * 16;
                if (b.ToneCount[i] > 0) ordinal++;
            }
            b.Tones = new VabTone[programs * 16];
            for (int i = 0; i < b.Tones.Length; i++, p += 32)
                b.Tones[i] = new VabTone
                {
                    Mode = d[p + 1], Vol = d[p + 2], Pan = d[p + 3], Center = d[p + 4], Shift = d[p + 5], Min = d[p + 6], Max = d[p + 7],
                    BendMin = d[p + 12], BendMax = d[p + 13], Adsr1 = BitConverter.ToUInt16(d, p + 16), Adsr2 = BitConverter.ToUInt16(d, p + 18), Vag = BitConverter.ToInt16(d, p + 22)
                };
            b.Samples = new short[vags + 1][]; b.Loop = new int[vags + 1];
            b.Samples[0] = new short[0]; b.Loop[0] = -1;
            int at = vb;
            for (int v = 1; v <= vags; v++)
            {
                int size = BitConverter.ToUInt16(d, p + v * 2) << 3;
                int end = Math.Min(at + size, d.Length);
                b.Samples[v] = Decode(d, at, end, out b.Loop[v]);
                at += size;
            }
            return b;
        }

        static readonly int[] F0 = { 0, 60, 115, 98, 122 }, F1 = { 0, 0, -52, -55, -60 };

        // PlayStation ADPCM: 16-byte blocks of 28 samples
        static short[] Decode(byte[] d, int from, int to, out int loop)
        {
            var o = new List<short>((to - from) / 16 * 28);
            int s1 = 0, s2 = 0; loop = -1; int start = 0;
            for (int p = from; p + 16 <= to; p += 16)
            {
                int shift = d[p] & 15, filter = Math.Min(d[p] >> 4, 4), flags = d[p + 1];
                if ((flags & 4) != 0) start = o.Count;
                for (int i = 0; i < 28; i++)
                {
                    int nibble = (d[p + 2 + i / 2] >> (i % 2 * 4)) & 15;
                    int v = (short)(nibble << 12) >> Math.Min(shift, 12);
                    v += (s1 * F0[filter] + s2 * F1[filter] + 32) >> 6;
                    v = Math.Max(-32768, Math.Min(32767, v));
                    s2 = s1; s1 = v;
                    o.Add((short)v);
                }
                if ((flags & 1) != 0) { if ((flags & 2) != 0) loop = start; break; }
            }
            return o.ToArray();
        }
    }

    /// <summary>The sound chip's volume envelope, stepped 44100 times a second.</summary>
    public struct SpuEnvelope
    {
        int _phase, _level, _wait;   // phase 0 attack, 1 decay, 2 sustain, 3 release, 4 silent
        int _a1, _a2;
        public SpuEnvelope(int adsr1, int adsr2) { _a1 = adsr1; _a2 = adsr2; _phase = 0; _level = 0; _wait = 0; }
        public void Release() { if (_phase < 3) { _phase = 3; _wait = 0; } }
        public bool Silent { get { return _phase == 4; } }
        public float Level { get { return _level / 32767f; } }

        public void Step()
        {
            if (_phase == 4) return;
            int rate; bool exp, down;
            if (_phase == 0) { rate = (_a1 >> 8) & 0x7F; exp = (_a1 & 0x8000) != 0; down = false; }
            else if (_phase == 1) { rate = ((_a1 >> 4) & 15) << 2; exp = true; down = true; }
            else if (_phase == 2) { rate = (_a2 >> 6) & 0x7F; exp = (_a2 & 0x8000) != 0; down = (_a2 & 0x4000) != 0; }
            else { rate = (_a2 & 31) << 2; exp = (_a2 & 0x20) != 0; down = true; }
            if (--_wait > 0) return;
            int shift = rate >> 2, cycles = 1 << Math.Max(0, shift - 11);
            int step = (down ? -8 + (rate & 3) : 7 - (rate & 3)) << Math.Max(0, 11 - shift);
            if (exp && !down && _level > 0x6000) cycles *= 4;
            if (exp && down) step = step * _level / 32768;
            _wait = cycles;
            _level = Math.Max(0, Math.Min(32767, _level + step));
            if (_phase == 0 && _level >= 32767) _phase = 1;
            else if (_phase == 1 && _level <= (((_a1 & 15) + 1) << 11)) _phase = 2;
            else if (_phase == 3 && _level <= 0) _phase = 4;
        }
    }

    /// <summary>One song ("pQES"): MIDI-like events, 480 ticks a beat. Controller 99 marks the loop: 20 start, 30 end.</summary>
    public sealed class SeqSong
    {
        public struct Ev { public int Tick, Status, A, B; }
        public List<Ev> Events = new List<Ev>();
        public int Ppqn, Tempo;

        public static SeqSong Parse(byte[] d, int o)
        {
            var s = new SeqSong { Ppqn = d[o + 8] << 8 | d[o + 9], Tempo = d[o + 10] << 16 | d[o + 11] << 8 | d[o + 12] };
            int p = o + 15, status = 0, tick = 0;
            while (p < d.Length)
            {
                int dt = 0, c;
                do { c = d[p++]; dt = dt << 7 | c & 0x7F; } while ((c & 0x80) != 0);
                tick += dt;
                if ((d[p] & 0x80) != 0) status = d[p++];
                if (status == 0xFF)
                {
                    int meta = d[p++];
                    if (meta == 0x2F) { s.Events.Add(new Ev { Tick = tick, Status = 0xFF, A = 0x2F }); break; }
                    if (meta == 0x51) { s.Events.Add(new Ev { Tick = tick, Status = 0xFF, A = 0x51, B = d[p] << 16 | d[p + 1] << 8 | d[p + 2] }); p += 3; }
                    else break;
                    continue;
                }
                int hi = status & 0xF0, a = d[p++], b = hi == 0xC0 || hi == 0xD0 ? 0 : d[p++];
                s.Events.Add(new Ev { Tick = tick, Status = status, A = a, B = b });
            }
            return s;
        }
    }

    /// <summary>Plays a song on a bank into a stereo buffer, as much at a time as asked, so a long song can be made over several frames. Reverb and vibrato are left out.</summary>
    public sealed class SeqRenderer
    {
        public const int Rate = 22050;
        const int MaxSamples = Rate * 600;
        sealed class Voice { public VabTone T; public short[] S; public int Loop, Channel, Note; public double Pos, Step; public float L, R; public SpuEnvelope Env; public bool Held; }
        readonly VabBank _b; readonly SeqSong _s;
        readonly List<Voice> _voices = new List<Voice>();
        readonly int[] _prog = new int[16], _vol = new int[16], _pan = new int[16], _exp = new int[16], _bend = new int[16];
        readonly List<float> _out = new List<float>(Rate * 120);
        int _ev; double _pending; int _tempo;
        public int LoopStart = -1;     // sample frame where the loop starts
        public bool Done;
        public float[] Pcm { get { return _out.ToArray(); } }
        public int Frames { get { return _out.Count / 2; } }

        public SeqRenderer(VabBank bank, SeqSong song)
        {
            _b = bank; _s = song; _tempo = song.Tempo;
            for (int i = 0; i < 16; i++) { _vol[i] = 127; _pan[i] = 64; _exp[i] = 127; _bend[i] = 8192; }
        }

        double TickFrames { get { return _tempo / (double)_s.Ppqn / 1000000.0 * Rate; } }

        /// <summary>Makes about this many sample frames more; true when the song has reached its end or loop end.</summary>
        public bool Advance(int frames)
        {
            while (frames > 0 && !Done)
            {
                if (_pending >= 1.0) { int n = (int)Math.Min(_pending, frames); Mix(n); _pending -= n; frames -= n; continue; }
                if (_ev >= _s.Events.Count || Frames > MaxSamples) { Done = true; break; }
                var e = _s.Events[_ev++];
                Apply(e);
                if (_ev < _s.Events.Count) _pending += (_s.Events[_ev].Tick - e.Tick) * TickFrames;
            }
            return Done;
        }

        void Apply(SeqSong.Ev e)
        {
            int hi = e.Status & 0xF0, ch = e.Status & 15;
            if (e.Status == 0xFF) { if (e.A == 0x51) _tempo = e.B; else Done = true; return; }
            if (hi == 0x90 && e.B > 0) NoteOn(ch, e.A, e.B);
            else if (hi == 0x80 || hi == 0x90) { foreach (var v in _voices) if (v.Channel == ch && v.Note == e.A && v.Held) { v.Held = false; v.Env.Release(); } }
            else if (hi == 0xC0) _prog[ch] = e.A;
            else if (hi == 0xE0) _bend[ch] = e.A | e.B << 7;
            else if (hi == 0xB0)
            {
                if (e.A == 7) _vol[ch] = e.B;
                else if (e.A == 10) _pan[ch] = e.B;
                else if (e.A == 11) _exp[ch] = e.B;
                else if (e.A == 99 && e.B == 20) LoopStart = Frames;
                else if (e.A == 99 && e.B == 30) Done = true;
            }
        }

        void NoteOn(int ch, int note, int vel)
        {
            int prog = _prog[ch], count = _b.ToneCount[prog], start = _b.ToneStart[prog];
            for (int i = 0; i < count && start + i < _b.Tones.Length; i++)
            {
                var t = _b.Tones[start + i];
                if (note < t.Min || note > t.Max || t.Vag <= 0 || t.Vag >= _b.Samples.Length) continue;
                float vol = vel / 127f * _vol[ch] / 127f * _exp[ch] / 127f * t.Vol / 127f * _b.ProgVol[prog] / 127f;
                float pan = Math.Max(0f, Math.Min(1f, (t.Pan + _pan[ch] - 64) / 127f));
                var v = new Voice { T = t, S = _b.Samples[t.Vag], Loop = _b.Loop[t.Vag], Channel = ch, Note = note, Held = true, Env = new SpuEnvelope(t.Adsr1, t.Adsr2) };
                v.L = vol * Math.Min(1f, (1f - pan) * 2f); v.R = vol * Math.Min(1f, pan * 2f);
                v.Step = StepOf(t, note, (_bend[ch] - 8192) / 8192.0);
                _voices.Add(v);
            }
        }

        // the sample plays at 44100 at its centre note
        static double StepOf(VabTone t, int note, double bend)
        {
            double semis = note - t.Center + t.Shift / 128.0 + bend * (bend >= 0 ? t.BendMax : t.BendMin);
            return Math.Pow(2.0, semis / 12.0) * 44100.0 / Rate;
        }

        void Mix(int frames)
        {
            int envSteps = 44100 / Rate;
            for (int f = 0; f < frames; f++)
            {
                float l = 0f, r = 0f;
                for (int i = _voices.Count - 1; i >= 0; i--)
                {
                    var v = _voices[i];
                    for (int k = 0; k < envSteps; k++) v.Env.Step();
                    int a = (int)v.Pos;
                    if (a >= v.S.Length - 1)
                    {
                        if (v.Loop < 0 || v.S.Length < 2) { _voices.RemoveAt(i); continue; }
                        v.Pos = v.Loop + (v.Pos - a); a = (int)v.Pos;
                    }
                    if (v.Env.Silent) { _voices.RemoveAt(i); continue; }
                    float frac = (float)(v.Pos - a), s = (v.S[a] + (v.S[a + 1] - v.S[a]) * frac) / 32768f * v.Env.Level;
                    l += s * v.L; r += s * v.R;
                    v.Pos += v.Step;
                }
                _out.Add(l); _out.Add(r);
            }
        }

        /// <summary>A sound effect: every tone of the program whose top note is this note (startSound), mono, cut at 4 seconds if it loops.</summary>
        public static float[] Effect(VabBank b, int prog, int note)
        {
            int start = prog * 16;
            var mix = new float[Rate * 4]; int used = 0;
            for (int i = 0; i < 16 && start + i < b.Tones.Length; i++)
            {
                var t = b.Tones[start + i];
                if (t.Max != note || t.Vag <= 0 || t.Vag >= b.Samples.Length) continue;
                var s = b.Samples[t.Vag]; int loop = b.Loop[t.Vag];
                var env = new SpuEnvelope(t.Adsr1, t.Adsr2);
                double pos = 0, step = StepOf(t, note, 0);
                float vol = t.Vol / 127f;
                for (int f = 0; f < mix.Length; f++)
                {
                    int a = (int)pos;
                    if (a >= s.Length - 1) { if (loop < 0 || s.Length < 2) break; pos = loop; a = loop; }
                    env.Step(); env.Step();
                    mix[f] += (s[a] + (s[a + 1] - s[a]) * (float)(pos - a)) / 32768f * env.Level * vol;
                    pos += step;
                    if (f + 1 > used) used = f + 1;
                }
            }
            var o = new float[used];
            Array.Copy(mix, o, used);
            return o;
        }
    }
}
