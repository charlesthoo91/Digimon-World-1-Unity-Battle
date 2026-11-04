using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The sparkles round a Digimon that a recovery item heals (addHealingParticleEffect, tickHealingParticles, renderHealingParticles in main/particle.c).
    /// 44 frames: in each of the first 20 a sparkle is born on a ring of radius x 12 / 10 round the Digimon (the ring turns 640 a frame), starting 70 units over
    /// its head and coming down to the ground over the 20 frames; each lives 18 frames through PARTICLE_COLOR1 (and a half-size copy in PARTICLE_COLOR2), its
    /// size jittering 15-84 every frame. With variant 1 (Restore and the bigger recovery items) 4 more a frame for 39 frames spiral down from the head
    /// (PARTICLE_COLOR3, size 24, 6 frames each). All drawn with renderFXParticle. Colours read from the exe (0x80124BC4, 0x80124B8C, 0x80124B78).
    /// </summary>
    public sealed class HealingParticles
    {
        const int Count = 20;       // NUM_HEALING_PARTICLES
        struct P { public int Counter, Size; public Vector3 Pos; }      // game units (y down)

        sealed class Effect
        {
            public int Frame = -1, Radius; public bool Second; public Vector3 Off;
            public readonly P[] A = new P[Count], B = new P[Count];
        }

        readonly List<Effect> _fx = new List<Effect>();
        readonly Color32[] _c1 = new Color32[18], _c2 = new Color32[18], _c3 = new Color32[6];
        readonly System.Random _rng = new System.Random();
        float _acc;

        public HealingParticles(DiscImage disc)
        {
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint load = BitConverter.ToUInt32(exe, 0x18);
            Func<uint, int> at = a => (int)(a - load + 0x800);
            for (int i = 0; i < 18; i++) { int o = at(0x80124BC4) + 3 * i; _c1[i] = new Color32(exe[o], exe[o + 1], exe[o + 2], 255); }
            for (int i = 0; i < 18; i++) { int o = at(0x80124B8C) + 3 * i; _c2[i] = new Color32(exe[o], exe[o + 1], exe[o + 2], 255); }
            for (int i = 0; i < 6; i++) { int o = at(0x80124B78) + 3 * i; _c3[i] = new Color32(exe[o], exe[o + 1], exe[o + 2], 255); }
        }

        /// <summary>addHealingParticleEffect: at the Digimon's place (game units), its DIGIMON_DATA height and radius; second = the variant with the spiral.</summary>
        public void Add(Vector3 gamePos, int height, int radius, bool second)
        {
            var e = new Effect { Frame = 0, Radius = radius * 12 / 10, Second = second, Off = new Vector3(gamePos.x, -height - 70, gamePos.z) };
            for (int i = 0; i < Count; i++) { e.A[i].Counter = -1; e.B[i].Counter = -1; }
            _fx.Add(e);
        }

        /// <summary>Steps the effects at 20 a second and draws them with the effect player's renderFXParticle.</summary>
        public void Update(float dt, EffectPlayer fx, Camera cam)
        {
            if (_fx.Count == 0) return;
            _acc += dt;
            while (_acc >= 0.05f)
            {
                _acc -= 0.05f;
                for (int k = _fx.Count - 1; k >= 0; k--) if (!Tick(_fx[k])) _fx.RemoveAt(k);
            }
            if (fx == null || cam == null) return;
            foreach (var e in _fx)
            {
                foreach (var p in e.A)
                    if (p.Counter >= 0) { fx.DrawFxParticle(ToUnity(p.Pos), p.Size, _c1[p.Counter], cam); fx.DrawFxParticle(ToUnity(p.Pos), p.Size >> 1, _c2[p.Counter], cam); }
                if (!e.Second) continue;
                foreach (var p in e.B)
                    if (p.Counter >= 0) fx.DrawFxParticle(ToUnity(p.Pos), 24, _c3[Mathf.Min(p.Counter, 5)], cam);
            }
        }

        // tickHealingParticles
        bool Tick(Effect e)
        {
            e.Frame++;
            if (e.Frame >= 44) return false;
            int f = e.Frame;
            if (f < 20)
            {
                // RotMatrixZYX(0, f x 640, 0) on (radius, 0, 0)
                float a = ((f * 640) & 0xFFF) * Mathf.PI * 2f / 4096f;
                e.A[f].Counter = 17;
                e.A[f].Pos = new Vector3(e.Off.x + Mathf.Cos(a) * e.Radius, e.Off.y - PsLerp(0, (int)e.Off.y, 0, 20, f), e.Off.z - Mathf.Sin(a) * e.Radius);
            }
            for (int i = 0; i < Count; i++) { e.A[i].Counter--; e.A[i].Size = _rng.Next(70) + 15; }
            if (f < 39)
            {
                for (int i = 0; i < 4; i++)
                {
                    int angle = PsLerp(0, 512, 0, 13, f);
                    // 512 / (i x 13 / 4): for i = 0 the R3000 gives -1 for a division by 0
                    int div = i * 13 / 4;
                    angle += div == 0 ? -1 : 512 / div;
                    int j = f % 5 + i * 5;
                    e.B[j].Counter = 5;
                    float x = e.Off.x + (_rng.Next(60) - 30) - (((e.Radius + 150) * PsSin512(angle)) >> 12);
                    float z = e.Off.z + (_rng.Next(60) - 30) + (((e.Radius + 150) * PsSin512(angle + 0x80)) >> 12);
                    float y = PsLerp((_rng.Next(60) - 30) + (int)e.Off.y, 0, 0, 39, f);
                    e.B[j].Pos = new Vector3(x, y, z);
                }
            }
            for (int i = 0; i < Count; i++) e.B[i].Counter--;
            return true;
        }

        static Vector3 ToUnity(Vector3 g) { return new Vector3(g.x, -g.y, g.z) * 0.01f; }

        static int PsLerp(int a, int b, int t0, int t1, int t)
        {
            if (t1 == t0) return 0;
            int range = b - a, r = range * (t - t0) / (t1 - t0);
            if (range >= 0) { r %= range + 1; if (r < 0) r += range; } else { r %= range - 1; if (r > 0) r += range; }
            return r + a;
        }

        static int PsSin512(int a) { return Mathf.RoundToInt(Mathf.Sin((a & 0x1FF) * Mathf.PI * 2f / 512f) * 4096f); }
    }
}
