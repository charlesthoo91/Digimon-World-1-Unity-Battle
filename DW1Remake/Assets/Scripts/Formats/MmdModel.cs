using System;
using System.Collections.Generic;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// Digimon World 1 model file (.MMD): a PlayStation TMD model split into body parts,
    /// followed by an animation table. This class reads the parts and the first pose and
    /// builds Unity objects from them in memory. Nothing is written to disk.
    /// </summary>
    public sealed class MmdModel
    {
        public const float Scale = 0.01f;   // game units -> Unity metres
        public static float Brightness = 1f; // multiplies the Digimon's colours
        public static bool UseLit;          // true: react to scene lights (used by the playable room)

        public sealed class Face
        {
            public int[] V = new int[3];     // vertex indices
            public byte[] U = new byte[3];   // texture coordinates (pixels)
            public byte[] T = new byte[3];
            public int Row;                  // which colour palette this face uses
            public int Abr = -1;             // semi-transparent primitive (mode bit 1): the blend mode from its texture page, else -1
            public int Prim;                 // the TMD primitive this face comes from (a quad gives two faces)
            public bool Quad, Second;        // part of a quad; the quad's second triangle (corners 1, 3, 2)
            public int[] PrimN;              // the primitive's first three normal indices (lit packets), else null
            public int[] PrimV;              // the primitive's corner vertices in TMD order (3 or 4)
            public int Clut, Tpage;          // the packet's raw CLUT and texture page words (where in video memory its picture is)
        }

        public sealed class Part
        {
            public short[] Verts;            // x,y,z per vertex
            public short[] Normals;          // x,y,z per normal (4096 = 1)
            public List<Face> Faces = new List<Face>();
        }

        public readonly List<Part> Parts = new List<Part>();
        byte[] _d;

        static int U32(byte[] d, int o) { return (int)BitConverter.ToUInt32(d, o); }
        static int U16(byte[] d, int o) { return BitConverter.ToUInt16(d, o); }
        static int S16(byte[] d, int o) { return BitConverter.ToInt16(d, o); }

        public static MmdModel Parse(byte[] d)
        {
            var m = new MmdModel { _d = d };
            int tmd = U32(d, 0);
            int nobj = U32(d, tmd + 8);
            int ob = tmd + 12;
            for (int i = 0; i < nobj; i++)
            {
                int o = ob + 28 * i;
                int vt = U32(d, o), nv = U32(d, o + 4), nt = U32(d, o + 8), nn = U32(d, o + 12), pt = U32(d, o + 16), npr = U32(d, o + 20);
                var part = new Part { Verts = new short[nv * 3], Normals = new short[nn * 3] };
                for (int k = 0; k < nn && ob + nt + 8 * k + 6 <= d.Length; k++)
                    for (int c = 0; c < 3; c++) part.Normals[k * 3 + c] = (short)S16(d, ob + nt + 8 * k + 2 * c);
                for (int k = 0; k < nv; k++)
                {
                    part.Verts[k * 3] = (short)S16(d, ob + vt + 8 * k);
                    part.Verts[k * 3 + 1] = (short)S16(d, ob + vt + 8 * k + 2);
                    part.Verts[k * 3 + 2] = (short)S16(d, ob + vt + 8 * k + 4);
                }

                int p = ob + pt;
                for (int n = 0; n < npr; n++)
                {
                    int ilen = d[p + 1], mode = d[p + 3];
                    int b = p + 4;
                    p += 4 + 4 * ilen;
                    bool quad = (mode & 8) != 0;
                    if ((mode & 4) == 0) continue;               // not textured

                    // standard TMD packets: uv words, then lit (flag bit 0 clear) = normal/vertex pairs (gouraud) or n0 v0 v1 v2 v3 (flat);
                    // unlit (flag bit 0 set, used by technique effects) = colour words, then v0 v1 v2 v3
                    int count = quad ? 4 : 3;
                    bool gouraud = (mode & 0x10) != 0, unlit = (d[b - 2] & 1) != 0;
                    int first = unlit ? 2 * (count + (gouraud ? count : 1)) : 2 * count + 1, step = !unlit && gouraud ? 2 : 1;
                    if (first + step * (count - 1) >= 2 * ilen) continue;
                    int[] vi = new int[4];
                    byte[] u = new byte[4], v = new byte[4];
                    bool ok = true;
                    for (int k = 0; k < count; k++)
                    {
                        vi[k] = U16(d, b + 2 * (first + step * k));
                        if (vi[k] >= nv) ok = false;
                        u[k] = d[b + 4 * k];
                        v[k] = d[b + 4 * k + 1];
                    }
                    if (!ok) continue;
                    int row = (U16(d, b + 2) - 0x7A00) >> 6;
                    if (row < 0 || row > 15) row = 0;

                    int[] pn = null;
                    if (!unlit && gouraud) pn = new[] { U16(d, b + 2 * (2 * count)), U16(d, b + 2 * (2 * count + 2)), U16(d, b + 2 * (2 * count + 4)) };
                    var pv = new int[count]; System.Array.Copy(vi, pv, count);
                    int[][] tris = quad ? new[] { new[] { 0, 1, 2 }, new[] { 1, 3, 2 } } : new[] { new[] { 0, 1, 2 } };
                    for (int ti = 0; ti < tris.Length; ti++)
                    {
                        var t = tris[ti];
                        var f = new Face { Row = row, Abr = (mode & 2) != 0 ? (U16(d, b + 6) >> 5) & 3 : -1, Prim = n, Quad = quad, Second = ti == 1, PrimN = pn, PrimV = pv, Clut = U16(d, b + 2), Tpage = U16(d, b + 6) };
                        for (int k = 0; k < 3; k++) { f.V[k] = vi[t[k]]; f.U[k] = u[t[k]]; f.T[k] = v[t[k]]; }
                        part.Faces.Add(f);
                    }
                }
                m.Parts.Add(part);
            }
            return m;
        }

        /// <summary>First frame of the first animation: rotation and location for every bone.</summary>
        public bool TryGetStartPose(int bones, out int[][] rot, out int[][] loc)
        {
            rot = new int[bones][]; loc = new int[bones][];
            for (int i = 0; i < bones; i++) { rot[i] = new int[3]; loc[i] = new int[3]; }
            int ao = U32(_d, 4);
            int first = 0;
            for (int i = 0; i < 16 && ao + 4 * i + 4 <= _d.Length; i++)
            {
                int o = U32(_d, ao + 4 * i);
                if (o != 0) { first = o; break; }
            }
            if (first == 0) return false;
            int p = ao + first;
            bool scaled = (U16(_d, p) & 0x8000) != 0;
            p += 2;
            for (int n = 1; n < bones; n++)
            {
                if (p + 18 > _d.Length) return false;
                if (scaled) p += 6;
                for (int k = 0; k < 3; k++) { rot[n][k] = S16(_d, p + 2 * k); loc[n][k] = S16(_d, p + 6 + 2 * k); }
                p += 12;
            }
            return true;
        }

        // ------------------------------------------------------------------ Unity objects

        static double[,] RotXyz(double a, double b, double c)
        {
            double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(c), sc = Math.Sin(c);
            double[,] X = { { 1, 0, 0 }, { 0, ca, -sa }, { 0, sa, ca } };
            double[,] Y = { { cb, 0, sb }, { 0, 1, 0 }, { -sb, 0, cb } };
            double[,] Z = { { cc, -sc, 0 }, { sc, cc, 0 }, { 0, 0, 1 } };
            return Mul(Mul(X, Y), Z);
        }

        static double[,] Mul(double[,] A, double[,] B)
        {
            var R = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++) R[i, j] += A[i, k] * B[k, j];
            return R;
        }

        public static void SetLocal(Transform t, int[] rot, int[] loc)
        {
            double k = 2 * Math.PI / 4096;
            double[,] R = RotXyz(rot[0] * k, rot[1] * k, rot[2] * k);
            double[] f = { 1, -1, 1 };   // game space has Y pointing down; Unity's points up
            var m = Matrix4x4.identity;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] = (float)(f[i] * f[j] * R[i, j]);
            t.localRotation = m.rotation;
            t.localPosition = new Vector3(loc[0], -loc[1], loc[2]) * Scale;
        }

        static Texture2D BuildAtlas(TimImage tim, List<int> rows)
        {
            int h = 128 * rows.Count;
            var px = new Color32[256 * h];
            for (int k = 0; k < rows.Count; k++)
                Array.Copy(tim.GetPixels(rows[k]), 0, px, k * 128 * 256, 256 * 128);
            var tex = new Texture2D(256, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// The PS1 semi-transparency of a primitive (abr in its texture page), for the colours with the STP bit:
        /// 0 = half back + half front, 1 = back + front, 2 = back - front, 3 = back + front / 4. Drawn with the overlay shader, no blur.
        /// </summary>
        public static Material MakeBlendMaterial(Texture2D tex, int abr)
        {
            var sh = Shader.Find("DW1Remake/Overlay");
            if (sh == null) return null;
            var m = new Material(sh);
            m.SetTexture("_BaseMap", tex);
            m.SetVector("_Texel", new Vector4(1f / tex.width, 1f / tex.height, 0, 0));
            m.SetFloat("_BlurMax", 0f); m.SetFloat("_Sharpen", 0f);
            SetPsxBlend(m, abr);
            return m;
        }

        public static void SetPsxBlend(Material m, int abr)
        {
            var one = (float)UnityEngine.Rendering.BlendMode.One;
            switch (abr & 3)
            {
                case 0: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); m.SetFloat("_Opacity", 0.5f); m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add); break;
                case 1: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add); break;
                case 2: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.ReverseSubtract); break;
                default: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 0.25f); m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add); break;
            }
            m.SetFloat("_ZWriteOn", 0f);
            m.renderQueue = 3000;
        }

        public static Material MakeMaterial(Texture2D tex)
        {
            Shader sh = Shader.Find(UseLit ? "Universal Render Pipeline/Lit" : "Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Transparent Cutout");
            var mat = new Material(sh);
            mat.SetTexture("_BaseMap", tex);
            mat.SetTexture("_MainTex", tex);
            mat.SetFloat("_Cull", 0);          // draw both sides
            mat.SetFloat("_AlphaClip", 1);     // palette colour 0 is see-through
            mat.SetFloat("_Cutoff", 0.5f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetColor("_BaseColor", new Color(Brightness, Brightness, Brightness, 1f));
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_SpecularHighlights", 0f);
            mat.SetFloat("_EnvironmentReflections", 0f);
            mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            mat.renderQueue = 2450;
            return mat;
        }

        /// <summary>Builds the Digimon as a bone hierarchy in its first pose.</summary>
        public GameObject Build(DigimonEntry e, byte[] allTim)
        {
            var root = new GameObject(e.Name);
            int bones = e.BoneCount;

            int[][] rot, loc;
            TryGetStartPose(bones, out rot, out loc);

            // textures: one atlas holding every palette this model uses
            var rows = new List<int>();
            foreach (var part in Parts) foreach (var f in part.Faces) if (!rows.Contains(f.Row)) rows.Add(f.Row);
            rows.Sort();
            Material mat = null;
            TimImage tim;
            if (TimImage.TryParse(allTim, e.Type * 0x4800, out tim) && tim.Width == 256 && tim.Height == 128 && tim.BitsPerPixel == 4)
                mat = MakeMaterial(BuildAtlas(tim, rows));
            else
                Debug.LogWarning(e.Name + ": texture not found in ALLTIM.TIM, using a plain material.");
            if (mat == null) mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));

            var nodes = new Transform[bones];
            for (int i = 0; i < bones; i++)
            {
                var go = new GameObject("bone_" + i);
                nodes[i] = go.transform;
                nodes[i].SetParent(e.NodeParent[i] < 0 ? root.transform : nodes[e.NodeParent[i]], false);
                if (i > 0) SetLocal(nodes[i], rot[i], loc[i]);

                int obj = e.NodeObject[i];
                if (obj < 0 || obj >= Parts.Count) continue;
                go.name = "bone_" + i + "_part_" + obj;
                go.AddComponent<MeshFilter>().sharedMesh = BuildMesh(Parts[obj], rows, e.Name + "_" + obj);
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }

            var animator = root.AddComponent<DigimonAnimator>();
            animator.Init(nodes, AnimDecoder.Decode(_d, bones));
            return root;
        }

        static Mesh BuildMesh(Part part, List<int> rows, string name)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float H = 128f * rows.Count;
            foreach (var f in part.Faces)
            {
                int row = rows.IndexOf(f.Row);
                int baseIndex = verts.Count;
                for (int k = 0; k < 3; k++)
                {
                    int v = f.V[k] * 3;
                    verts.Add(new Vector3(part.Verts[v], -part.Verts[v + 1], part.Verts[v + 2]) * Scale);
                    uvs.Add(new Vector2((f.U[k] + 0.5f) / 256f, (row * 128 + 127 - f.T[k] + 0.5f) / H));
                }
                tris.Add(baseIndex); tris.Add(baseIndex + 2); tris.Add(baseIndex + 1);   // mirrored axis -> flip winding
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
