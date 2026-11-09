using System;
using System.Collections.Generic;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// A plain TMD file (the arena's models: STDDAT/T_TOGI, B_TOGI, WIN_LOSE, DRAW, CHAMP, T_DAI, B_DAI) with every polygon packet kind:
    /// lit (normals) or unlit (colours), flat or Gouraud, with or without a picture, three or four corners. Pictures are named by
    /// video memory place (texture page and colour set words), so the mesh is built against a PsxVram.
    /// </summary>
    public sealed class PsxTmd
    {
        public sealed class Prim
        {
            public int Count;                 // 3 or 4 corners
            public bool Textured, Lit, Semi, Raw;    // Raw = TGE (mode bit 0): the picture as it is, colour ignored
            public bool TwoSided;                    // FCE (flag bit 1); without it the PS1 does not draw the packet seen from behind
            public int Tpage, Clut;
            public readonly int[] V = new int[4], N = new int[4];
            public readonly byte[] U = new byte[4], T = new byte[4];
            public readonly Color32[] C = new Color32[4];
        }

        public sealed class Obj
        {
            public short[] Verts = new short[0], Normals = new short[0];
            public readonly List<Prim> Prims = new List<Prim>();
        }

        public readonly List<Obj> Objs = new List<Obj>();

        static int U16(byte[] d, int o) { return BitConverter.ToUInt16(d, o); }

        public static PsxTmd Parse(byte[] d)
        {
            var m = new PsxTmd();
            if (d == null || d.Length < 12) return m;
            int nobj = BitConverter.ToInt32(d, 8), ob = 12;
            for (int i = 0; i < nobj && ob + 28 * i + 28 <= d.Length; i++)
            {
                int o = ob + 28 * i;
                int vt = BitConverter.ToInt32(d, o), nv = BitConverter.ToInt32(d, o + 4), nt = BitConverter.ToInt32(d, o + 8), nn = BitConverter.ToInt32(d, o + 12);
                int pt = BitConverter.ToInt32(d, o + 16), npr = BitConverter.ToInt32(d, o + 20);
                var obj = new Obj { Verts = new short[nv * 3], Normals = new short[nn * 3] };
                for (int k = 0; k < nv; k++) for (int c = 0; c < 3; c++) obj.Verts[k * 3 + c] = BitConverter.ToInt16(d, ob + vt + 8 * k + 2 * c);
                for (int k = 0; k < nn; k++) for (int c = 0; c < 3; c++) obj.Normals[k * 3 + c] = BitConverter.ToInt16(d, ob + nt + 8 * k + 2 * c);
                int p = ob + pt;
                for (int n = 0; n < npr && p + 4 <= d.Length; n++)
                {
                    int ilen = d[p + 1], flag = d[p + 2], mode = d[p + 3];
                    int b = p + 4;
                    p += 4 + 4 * ilen;
                    if ((mode & 0xE0) != 0x20) continue;       // lines and sprites: none in these files
                    var pr = new Prim
                    {
                        Count = (mode & 8) != 0 ? 4 : 3, Textured = (mode & 4) != 0, Lit = (flag & 1) == 0,
                        Semi = (mode & 2) != 0, Raw = (mode & 1) != 0 && (mode & 4) != 0, TwoSided = (flag & 2) != 0
                    };
                    bool gouraud = (mode & 0x10) != 0, grad = (flag & 4) != 0;
                    int q = b;
                    if (pr.Textured)
                    {
                        for (int k = 0; k < pr.Count; k++) { pr.U[k] = d[q + 4 * k]; pr.T[k] = d[q + 4 * k + 1]; }
                        pr.Clut = U16(d, q + 2); pr.Tpage = U16(d, q + 6);
                        q += 4 * pr.Count;
                    }
                    else pr.Tpage = 0;
                    if (pr.Lit)
                    {
                        // lit: a packet with no picture has its colour(s) first; then normal / vertex pairs (Gouraud) or n0 v0 v1 v2 (v3)
                        if (!pr.Textured)
                        {
                            int nc = grad ? pr.Count : 1;
                            for (int k = 0; k < pr.Count; k++) { int c = q + 4 * (k < nc ? k : 0); pr.C[k] = new Color32(d[c], d[c + 1], d[c + 2], 255); }
                            q += 4 * nc;
                        }
                        else for (int k = 0; k < pr.Count; k++) pr.C[k] = new Color32(128, 128, 128, 255);
                        if (gouraud) for (int k = 0; k < pr.Count; k++) { pr.N[k] = U16(d, q + 4 * k); pr.V[k] = U16(d, q + 4 * k + 2); }
                        else
                        {
                            int n0 = U16(d, q);
                            for (int k = 0; k < pr.Count; k++) { pr.N[k] = n0; pr.V[k] = U16(d, q + 2 + 2 * k); }
                        }
                    }
                    else
                    {
                        // unlit: colour(s) (one each with Gouraud or gradation), then v0 v1 v2 (v3)
                        int nc = gouraud || grad ? pr.Count : 1;
                        for (int k = 0; k < pr.Count; k++) { int c = q + 4 * (k < nc ? k : 0); pr.C[k] = new Color32(d[c], d[c + 1], d[c + 2], 255); }
                        q += 4 * nc;
                        for (int k = 0; k < pr.Count; k++) pr.V[k] = U16(d, q + 2 * k);
                    }
                    bool ok = true;
                    for (int k = 0; k < pr.Count; k++) if (pr.V[k] >= nv || (pr.Lit && pr.N[k] >= Math.Max(1, nn))) ok = false;
                    if (ok) obj.Prims.Add(pr);
                }
                m.Objs.Add(obj);
            }
            return m;
        }

        /// <summary>STD_offsetTMDObjectUVs(tmd, vofs, nofs, obj): adds (nofs &lt;&lt; 8) + vofs to each picture word of the object's packets, as 32-bit
        /// words (a negative u borrows from v); it stops at the first packet with no picture.</summary>
        public static void OffsetObjectUvs(byte[] d, int vofs, int nofs, int objIdx)
        {
            int nobj = BitConverter.ToInt32(d, 8);
            if (objIdx >= nobj) return;
            int ob = 12, o = ob + 28 * objIdx;
            int pt = BitConverter.ToInt32(d, o + 16), npr = BitConverter.ToInt32(d, o + 20);
            int delta = (nofs << 8) + vofs, p = ob + pt;
            for (int i = 0; i < npr; i++)
            {
                int code = d[p + 3];
                if ((code & 4) == 0) return;
                int len = d[p + 1] + 1;
                int words = (code & 8) != 0 ? 4 : 3;
                for (int k = 1; k <= words; k++)
                {
                    int w = BitConverter.ToInt32(d, p + 4 * k) + delta;
                    Array.Copy(BitConverter.GetBytes(w), 0, d, p + 4 * k, 4);
                }
                p += 4 * len;
            }
        }

        // ------------------------------------------------------------------ lighting

        /// <summary>
        /// The flat lights a GsSortObject4J lit packet sees (GsSetFlatLight / GsSetAmbient): each light's colour times how much the normal faces
        /// against its direction, plus the ambient (4096 = 1); the packet's colour times that, clamped at 255. Normals go through the object's
        /// matrix (GsSetLightMatrix(lw), scale included).
        /// </summary>
        public sealed class Lights
        {
            public Vector3[] Dir = new Vector3[0];      // game space, normalised
            public Color[] Col = new Color[0];          // 255 = 1
            public float Ambient;

            public Color32 Shade(Vector3 n, Color32 c)
            {
                float r = Ambient, g = Ambient, b = Ambient;
                for (int i = 0; i < Dir.Length; i++)
                {
                    float k = Mathf.Max(0f, -Vector3.Dot(Dir[i], n));
                    r += k * Col[i].r; g += k * Col[i].g; b += k * Col[i].b;
                }
                r = Mathf.Min(r, 8f); g = Mathf.Min(g, 8f); b = Mathf.Min(b, 8f);
                return new Color32((byte)Mathf.Min(255f, c.r * r), (byte)Mathf.Min(255f, c.g * g), (byte)Mathf.Min(255f, c.b * b), 255);
            }
        }

        /// <summary>One object as a Unity mesh: a sub-mesh per (picture, colour set, solid / STP layer); lit packets get their colour from Relight.</summary>
        public sealed class Built
        {
            public Mesh Mesh; public Material[] Mats; public Obj Obj;
            readonly List<int> _cornerPrim = new List<int>(), _cornerK = new List<int>();
            Color32[] _colours;

            public static Built Make(Obj obj, PsxVram vram, Func<Prim, int, Material> material, List<UnityEngine.Object> owner)
            {
                var b = new Built { Obj = obj };
                var keys = new List<long>(); var tris = new List<List<int>>(); var first = new List<Prim>(); var layers = new List<int>();
                var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color32>();
                for (int pi = 0; pi < obj.Prims.Count; pi++)
                {
                    var p = obj.Prims[pi];
                    // a semi-transparent packet: its solid colours drawn normally, its STP colours with the packet's blend (two passes)
                    int passes = p.Textured && p.Semi ? 2 : 1;
                    int start = verts.Count;
                    for (int k = 0; k < p.Count; k++)
                    {
                        int v = p.V[k] * 3;
                        verts.Add(new Vector3(obj.Verts[v], -obj.Verts[v + 1], obj.Verts[v + 2]) * 0.01f);
                        uvs.Add(new Vector2((p.U[k] + 0.5f) / 256f, 1f - (p.T[k] + 0.5f) / 256f));
                        cols.Add(p.C[k]);
                        b._cornerPrim.Add(pi); b._cornerK.Add(k);
                    }
                    for (int pass = 0; pass < passes; pass++)
                    {
                        int layer = !p.Textured ? -1 : !p.Semi ? 0 : pass == 0 ? 1 : 2;
                        int abr = (p.Tpage >> 5) & 3;
                        long key = p.Textured ? (((long)(p.Tpage & 0x19F) << 24) | ((long)p.Clut << 4) | ((long)(layer & 3) << 2) | (long)(layer == 2 ? abr : 0)) : -1L - (p.Semi ? abr + 1 : 0);
                        int ki = keys.IndexOf(key);
                        if (ki < 0) { ki = keys.Count; keys.Add(key); tris.Add(new List<int>()); first.Add(p); layers.Add(layer); }
                        // corners 0 1 2 (and 1 3 2) in the packet's order are its front in Unity too (the Y flip and Unity's clockwise front
                        // cancel out; checked against the lit packets' normals); a two-sided packet also gets its back
                        tris[ki].Add(start); tris[ki].Add(start + 1); tris[ki].Add(start + 2);
                        if (p.Count == 4) { tris[ki].Add(start + 1); tris[ki].Add(start + 3); tris[ki].Add(start + 2); }
                        if (p.TwoSided)
                        {
                            tris[ki].Add(start); tris[ki].Add(start + 2); tris[ki].Add(start + 1);
                            if (p.Count == 4) { tris[ki].Add(start + 1); tris[ki].Add(start + 2); tris[ki].Add(start + 3); }
                        }
                    }
                }
                b.Mesh = new Mesh { name = "TMD", subMeshCount = keys.Count };
                if (verts.Count > 65000) b.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                b.Mesh.SetVertices(verts); b.Mesh.SetUVs(0, uvs); b.Mesh.SetColors(cols);
                for (int k = 0; k < keys.Count; k++) b.Mesh.SetTriangles(tris[k], k);
                b.Mesh.RecalculateBounds();
                b._colours = cols.ToArray();
                b.Mats = new Material[keys.Count];
                for (int k = 0; k < keys.Count; k++) b.Mats[k] = material(first[k], layers[k]);
                if (owner != null) { owner.Add(b.Mesh); foreach (var m in b.Mats) if (m != null) owner.Add(m); }
                return b;
            }

            /// <summary>Lit packets: their colour through the lights with normals turned by m (game space, 3 x 3, scale kept); unlit ones keep theirs.</summary>
            public void Relight(Lights lights, Matrix4x4 m)
            {
                bool any = false;
                for (int i = 0; i < _colours.Length; i++)
                {
                    var p = Obj.Prims[_cornerPrim[i]];
                    if (!p.Lit) continue;
                    int k = _cornerK[i], ni = p.N[k] * 3;
                    if (ni + 2 >= Obj.Normals.Length) continue;
                    var n = new Vector3(Obj.Normals[ni], Obj.Normals[ni + 1], Obj.Normals[ni + 2]) / 4096f;
                    _colours[i] = lights.Shade(m.MultiplyVector(n), p.C[k]);
                    any = true;
                }
                if (any) Mesh.colors32 = _colours;
            }
        }
    }
}
