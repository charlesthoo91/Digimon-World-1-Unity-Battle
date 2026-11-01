using System;
using System.Collections.Generic;

namespace DW1Remake.Formats
{
    public enum InsKind { Loop, End, Snd, Tex, Key }

    public sealed class KeyGroup
    {
        public int Node, Sc;
        public int[] Vals = new int[9];
        public bool[] Has = new bool[9];
    }

    public sealed class Ins
    {
        public InsKind Kind;
        public int Frame;   // frame this instruction fires on
        public int Count;   // loop count
        public int Jump;    // loop end: frame to jump back to
        public List<KeyGroup> Groups;
    }

    /// <summary>One animation: the starting pose of every bone plus the instruction stream.</summary>
    public sealed class Anim
    {
        public int Frames;
        public int[][] StartRot, StartLoc;   // index = bone (bone 0 unused)
        public List<Ins> Ins = new List<Ins>();
    }

    public static class AnimDecoder
    {
        static int U32(byte[] d, int o) { return (int)BitConverter.ToUInt32(d, o); }
        static int U16(byte[] d, int o) { return BitConverter.ToUInt16(d, o); }
        static int S16(byte[] d, int o) { return BitConverter.ToInt16(d, o); }

        /// <summary>Decodes every animation slot of an .MMD file. Empty slots are null.</summary>
        public static Anim[] Decode(byte[] m, int bones)
        {
            int ao = U32(m, 4);
            var offs = new List<int>();
            int lim = 1 << 30;
            for (int i = 0; 4 * i < lim && ao + 4 * i + 4 <= m.Length; i++)
            {
                int v = U32(m, ao + 4 * i);
                offs.Add(v);
                if (v != 0) lim = Math.Min(lim, v);
            }
            var starts = new SortedSet<int>();
            foreach (int o in offs) if (o != 0) starts.Add(o);
            var startList = new List<int>(starts);

            var result = new Anim[offs.Count];
            for (int s = 0; s < offs.Count; s++)
            {
                int o = offs[s];
                if (o == 0) continue;
                int idx = startList.IndexOf(o);
                int end = ao + (idx + 1 < startList.Count ? startList[idx + 1] : m.Length - ao);
                if (end > m.Length) end = m.Length;
                result[s] = DecodeOne(m, ao + o, end, bones);
            }
            return result;
        }

        static Anim DecodeOne(byte[] m, int p, int end, int bones)
        {
            if (p + 2 > end) return null;
            int fc = U16(m, p);
            bool scaled = (fc & 0x8000) != 0;
            p += 2;
            var a = new Anim { Frames = fc & 0x7FFF, StartRot = new int[bones][], StartLoc = new int[bones][] };
            a.StartRot[0] = new int[3]; a.StartLoc[0] = new int[3];
            for (int n = 1; n < bones; n++)
            {
                if (p + (scaled ? 18 : 12) > end) return null;
                if (scaled) p += 6;
                a.StartRot[n] = new[] { S16(m, p), S16(m, p + 2), S16(m, p + 4) };
                a.StartLoc[n] = new[] { S16(m, p + 6), S16(m, p + 8), S16(m, p + 10) };
                p += 12;
            }
            while (p + 2 <= end)
            {
                int w = U16(m, p);
                int op = w & 0xF000;
                if (op == 0x1000) { a.Ins.Add(new Ins { Kind = InsKind.Loop, Count = w & 0xFF }); p += 2; }
                else if (op == 0x2000) { a.Ins.Add(new Ins { Kind = InsKind.End, Frame = w & 0xFFF, Jump = p + 4 <= end ? S16(m, p + 2) : 0 }); p += 4; }
                else if (op == 0x4000) { a.Ins.Add(new Ins { Kind = InsKind.Snd, Frame = w & 0xFFF }); p += 4; }
                else if (op == 0x3000) { a.Ins.Add(new Ins { Kind = InsKind.Tex, Frame = w & 0xFFF }); p += 8; }
                else if (op == 0x0000)
                {
                    var key = new Ins { Kind = InsKind.Key, Frame = w & 0xFFF, Groups = new List<KeyGroup>() };
                    p += 2;
                    while (p + 4 <= end && (U16(m, p) & 0x8000) != 0)
                    {
                        int g = U16(m, p);
                        var grp = new KeyGroup { Node = g & 0x3F, Sc = S16(m, p + 2) };
                        int mask = (g >> 6) & 0x1FF;
                        p += 4;
                        for (int i = 0; i < 9; i++)
                            if ((mask & (0x100 >> i)) != 0 && p + 2 <= end) { grp.Has[i] = true; grp.Vals[i] = S16(m, p); p += 2; }
                        key.Groups.Add(grp);
                    }
                    a.Ins.Add(key);
                }
                else break;
            }
            return a;
        }
    }

    /// <summary>
    /// Plays one animation the way the game does (a port of the game's tick routine).
    /// Call Tick() once per game tick, then read Rot / Loc for every bone.
    /// </summary>
    public sealed class AnimSim
    {
        sealed class Momentum
        {
            public int[] Delta = new int[9], Sub = new int[9], Sc1 = new int[9], SubSc = new int[9], Sv = new int[9];
        }

        readonly Anim _a;
        readonly int _bones;
        readonly Momentum[] _mom;
        int _ptr, _frame = 1, _loopCount, _loopStart, _loopEnd = 1;
        bool _running = true;

        public int[][] Rot, Loc;

        public AnimSim(Anim a, int bones)
        {
            _a = a; _bones = bones;
            Rot = new int[bones][]; Loc = new int[bones][];
            _mom = new Momentum[bones];
            for (int i = 0; i < bones; i++)
            {
                Rot[i] = i == 0 ? new int[3] : (int[])a.StartRot[i].Clone();
                Loc[i] = i == 0 ? new int[3] : (int[])a.StartLoc[i].Clone();
                _mom[i] = new Momentum();
            }
        }

        int FrameOf(int p)
        {
            if (p >= _a.Ins.Count) return 0xFFF;
            var x = _a.Ins[p];
            return x.Kind != InsKind.Loop ? x.Frame & 0xFFF : (0x1000 | x.Count) & 0xFFF;
        }

        void ApplyMomentum()
        {
            for (int i = 0; i < _bones; i++)
            {
                var m = _mom[i];
                for (int j = 0; j < 9; j++)
                {
                    int d = m.Delta[j], sd = m.Sub[j], sc = m.Sc1[j], ss = m.SubSc[j], sv = m.Sv[j];
                    if (d == 0 && sd == 0) continue;
                    int nv = d, ns = ss;
                    if (sd != 0)
                    {
                        ns -= sd;
                        if (ns < 1) { ns += sc; nv += sv; }
                    }
                    m.SubSc[j] = ns;
                    if (j < 3) continue;                        // scale: not used yet
                    if (j < 6) Rot[i][j - 3] += nv;
                    else if (i != 0) Loc[i][j - 6] += nv;       // bone 0 movement is the entity's, ignored here
                }
            }
        }

        void ApplyKey(List<KeyGroup> groups)
        {
            foreach (var g in groups)
            {
                if (g.Node >= _bones || g.Sc == 0) continue;
                var m = _mom[g.Node];
                for (int i = 0; i < 9; i++)
                {
                    if (!g.Has[i]) continue;
                    int v = g.Vals[i];
                    m.Delta[i] = v / g.Sc;
                    int sub = v % g.Sc;
                    if (sub != 0)
                    {
                        m.Sv[i] = sub < 1 ? -1 : 1;
                        m.Sc1[i] = g.Sc; m.SubSc[i] = g.Sc;
                    }
                    m.Sub[i] = Math.Abs(sub);
                }
            }
        }

        /// <summary>Advances one game tick. Returns false once the animation has finished.</summary>
        public bool Tick()
        {
            if (!_running) return false;
            ApplyMomentum();
            var ins = _a.Ins;
            if (_ptr < ins.Count && ins[_ptr].Kind == InsKind.Loop)
            {
                _loopCount = ins[_ptr].Count;
                _ptr++; _loopStart = _ptr;
                _loopEnd = FrameOf(_ptr);
            }
            int guard = 0;
            while (_frame == _loopEnd)
            {
                if (++guard > 10000 || _ptr >= ins.Count) { _running = false; return false; }
                var x = ins[_ptr];
                switch (x.Kind)
                {
                    case InsKind.Snd:
                    case InsKind.Tex: _ptr++; break;
                    case InsKind.End:
                        if (_loopCount != 0xFF) _loopCount = (_loopCount - 1) & 0xFF;
                        if (_loopCount == 0) _ptr++;
                        else { _frame = x.Jump; _ptr = _loopStart; }
                        break;
                    case InsKind.Loop: _loopCount = x.Count; _ptr++; _loopStart = _ptr; break;
                    case InsKind.Key: _ptr++; ApplyKey(x.Groups); break;
                }
                _loopEnd = FrameOf(_ptr);
            }
            if (_frame == _a.Frames) _running = false; else _frame++;
            return true;
        }
    }
}
