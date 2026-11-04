using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The evolution scene, a port of evl/evl.c (EVL_initEvoSequence, EVL_tickEvoSequence and its helpers) and main/evl.c. 20 frames a second.
    /// 0-32 everything fades to black; 32 the map and everyone but the partner go; the camera swings round to the partner (EVL_updateEvoCamera).
    /// From 106 the body parts come off one by one in a random order: each part's faces fly away (shard sets), the part is left as a flickering
    /// wireframe, a streak shoots from the middle and a flash goes off at it. 112-204 the partner's colours are pushed towards white at random.
    /// 204-264 it grows to the new Digimon's size, 214-283 growing flashes, 284 the new model arrives as a wireframe and its faces fill in (294-334),
    /// 354 everything comes back and it cheers (animation 11).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        // the camera while the scene runs (GS_VIEWPOINT, DRAWING_OFFSET, VIEWPORT_DISTANCE)
        bool _evoCam; Vector3 _evoEye, _evoLook; float _evoFov; Vector2 _evoShift;

        void EvoCameraLate()
        {
            if (!_evoCam || _cam == null) return;
            _cam.transform.position = _evoEye;
            Vector3 d = _evoLook - _evoEye;
            if (d.sqrMagnitude > 1e-6f) _cam.transform.rotation = Quaternion.LookRotation(d, Vector3.up);
            _cam.fieldOfView = _evoFov;
            _cam.lensShift = _evoShift;
        }

        static readonly int[] WireOrder = { 0x06, 0x03, 0x0b, 0x08, 0x0a, 0x0d, 0x01, 0x04, 0x0e, 0x07, 0x0c, 0x0f, 0x05, 0x02, 0x09, 0x00 };     // WIREFRAME_RNG_TABLE
        static readonly int[] ShardFrames = { 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 2, -1, -1, -1, -1, -1, -1, -1, 3, -1, -1, -1, 4, -1, 5, -1, 6, 0 };   // EVL_D_80063EFC
        static readonly int[] RingFrames = { 1, 2, 3, 4, 5, 4, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };     // EVL_D_80063F28

        sealed class EvoPart { public Transform Node; public MeshRenderer R; public MmdModel.Part Part; public Mesh Src, Lines, Faces; public int Share = 16; }
        sealed class Shards { public int T; public Vector3[] Pos; public Vector2[] Uv; public Vector3[] Drift; public Mesh Mesh; public Texture Tex; }
        sealed class Streak { public int Bone, T; public Mesh Mesh; }
        sealed class Ring { public Vector3 At; public int T; public Color32 Col; public Mesh Mesh; }

        readonly List<EvoPart> _evoParts = new List<EvoPart>();
        readonly List<Shards> _shards = new List<Shards>();
        readonly List<Streak> _streaks = new List<Streak>();
        readonly List<Ring> _rings = new List<Ring>();
        int _wireTotal = 16;
        Material _lineMat, _halfMat; Texture2D _defEff; MaterialPropertyBlock _evoMpBlock; MaterialPropertyBlock _evoMp { get { return _evoMpBlock ?? (_evoMpBlock = new MaterialPropertyBlock()); } }      // not a field initializer: Unity forbids that in a MonoBehaviour

        static int PsLerp(int a, int b, int t0, int t1, int t)
        {
            if (t1 == t0) return 0;
            int range = b - a, r = range * (t - t0) / (t1 - t0);
            if (range >= 0) { r %= range + 1; if (r < 0) r += range; } else { r %= range - 1; if (r > 0) r += range; }
            return r + a;
        }

        static Vector3 ToGameV(Vector3 u) { return new Vector3(u.x / Unit, -u.y / Unit, u.z / Unit); }

        int GameHeading(Transform t)
        {
            Vector3 g = t.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
            return ((int)(Mathf.Atan2(g.x, g.z) / (Mathf.PI * 2f) * 4096f)) & 0xFFF;
        }

        // EVL_calculateCameraVectors: looking at bone 0 raised by half the height, from "distance" away at angle rotY, 400 + 2 x height higher
        void EvoCameraVectors(int rotY, int distance, int height, out Vector3 look, out Vector3 eye)
        {
            var n0 = _partnerAnim != null ? _partnerAnim.Node(0) : null;
            Vector3 r = ToGameV(n0 != null ? n0.position : _partner.position);
            r.y -= height / 2;
            float a = rotY * Mathf.PI * 2f / 4096f;
            Vector3 p = new Vector3(distance * Mathf.Sin(a), 0f, distance * Mathf.Cos(a)) + r;
            p.y = r.y - 0x190 - height * 2;
            look = ToUnity(r); eye = ToUnity(p);
        }

        void CollectEvoParts() { CollectEvoParts(_partnerAnim, PartnerType); }

        // the parts of any Digimon's model (renderWireframed draws entity 1, the partner, and entity 2 with their own totals)
        void CollectEvoParts(DigimonAnimator anim, int type)
        {
            _evoParts.Clear();
            if (anim == null) return;
            DigimonEntry entry = null;
            foreach (var e in DigimonCatalog.Load(_disc)) if (e.Type == type) { entry = e; break; }
            if (entry == null) return;
            var model = MmdModel.Parse(_disc.ReadFile(entry.MmdPath));
            for (int i = 0; i < entry.BoneCount; i++)
            {
                var node = anim.Node(i);
                int obj = entry.NodeObject[i];
                var ep = new EvoPart { Node = node };
                if (node != null && obj >= 0 && obj < model.Parts.Count)
                {
                    ep.Part = model.Parts[obj];
                    ep.R = node.GetComponent<MeshRenderer>();
                    var mf = node.GetComponent<MeshFilter>();
                    ep.Src = mf != null ? mf.sharedMesh : null;
                    ep.Lines = new Mesh(); ep.Faces = new Mesh();
                }
                _evoParts.Add(ep);
            }
        }

        Material PartnerMaterial()
        {
            foreach (var ep in _evoParts) if (ep.R != null) return ep.R.sharedMaterial;
            return null;
        }

        // renderWireframed: faces whose WIREFRAME_RNG_TABLE[face & 15] is below "share" are drawn as usual, the rest as grey lines (55-254 at random, one shade
        // per part each frame); faces turned away are skipped (RotNclip). A quad's line runs v0, v1, v3, v2 (three strokes).
        void DrawWireParts()
        {
            if (_cam == null) return;
            var mat = PartnerMaterial();
            for (int b = 0; b < _evoParts.Count; b++)
            {
                var ep = _evoParts[b];
                if (ep.Part == null || ep.Src == null || ep.Node == null) continue;
                int share = Mathf.Min(_wireTotal, ep.Share);
                if (ep.R != null) ep.R.enabled = share >= 16;
                if (share >= 16) continue;
                var srcV = ep.Src.vertices; var srcUv = ep.Src.uv;
                var m = ep.Node.localToWorldMatrix;
                var lv = new List<Vector3>(); var li = new List<int>();
                var fv = new List<Vector3>(); var fu = new List<Vector2>(); var ft = new List<int>();
                var faces = ep.Part.Faces;
                for (int j = 0; j < faces.Count && 3 * j + 2 < srcV.Length; j++)
                {
                    var f = faces[j];
                    Vector3 a = m.MultiplyPoint3x4(srcV[3 * j]), bb = m.MultiplyPoint3x4(srcV[3 * j + 1]), c = m.MultiplyPoint3x4(srcV[3 * j + 2]);
                    Vector3 sa = _cam.WorldToScreenPoint(a), sb = _cam.WorldToScreenPoint(bb), sc = _cam.WorldToScreenPoint(c);
                    if (sa.z <= 0f || sb.z <= 0f || sc.z <= 0f) continue;
                    float area = (sb.x - sa.x) * (sc.y - sa.y) - (sb.y - sa.y) * (sc.x - sa.x);
                    if (area <= 0f) continue;                 // turned away
                    if (WireOrder[f.Prim & 15] < share)
                    {
                        int k = fv.Count;
                        fv.Add(srcV[3 * j]); fv.Add(srcV[3 * j + 1]); fv.Add(srcV[3 * j + 2]);
                        fu.Add(srcUv[3 * j]); fu.Add(srcUv[3 * j + 1]); fu.Add(srcUv[3 * j + 2]);
                        ft.Add(k); ft.Add(k + 2); ft.Add(k + 1);
                    }
                    else if (!f.Second)
                    {
                        int k = lv.Count;
                        if (f.Quad && f.PrimV != null && f.PrimV.Length == 4)
                        {
                            Vector3 V(int vi) { return m.MultiplyPoint3x4(new Vector3(ep.Part.Verts[vi * 3], -ep.Part.Verts[vi * 3 + 1], ep.Part.Verts[vi * 3 + 2]) * MmdModel.Scale); }
                            lv.Add(V(f.PrimV[0])); lv.Add(V(f.PrimV[1])); lv.Add(V(f.PrimV[3])); lv.Add(V(f.PrimV[2]));
                            li.Add(k); li.Add(k + 1); li.Add(k + 1); li.Add(k + 2); li.Add(k + 2); li.Add(k + 3);
                        }
                        else
                        {
                            lv.Add(a); lv.Add(bb); lv.Add(c);
                            li.Add(k); li.Add(k + 1); li.Add(k + 1); li.Add(k + 2); li.Add(k + 2); li.Add(k);
                        }
                    }
                }
                if (ft.Count > 0 && mat != null)
                {
                    ep.Faces.Clear(); ep.Faces.SetVertices(fv); ep.Faces.SetUVs(0, fu); ep.Faces.SetTriangles(ft, 0); ep.Faces.RecalculateNormals(); ep.Faces.RecalculateBounds();
                    Graphics.DrawMesh(ep.Faces, m, mat, ep.Node.gameObject.layer);
                }
                if (li.Count > 0)
                {
                    ep.Lines.Clear(); ep.Lines.SetVertices(lv); ep.Lines.SetIndices(li, MeshTopology.Lines, 0); ep.Lines.RecalculateBounds();
                    float g = (0x37 + Random.Range(0, 0xFF - 0x37)) / 255f;
                    DrawLines(ep.Lines, new Color(g, g, g, 1f));
                }
            }
        }

        void DrawLines(Mesh mesh, Color c)
        {
            if (_lineMat == null) { var sh = Shader.Find("Universal Render Pipeline/Unlit"); if (sh == null) return; _lineMat = new Material(sh); }
            _evoMp.Clear(); _evoMp.SetColor("_BaseColor", c);
            Graphics.DrawMesh(mesh, Matrix4x4.identity, _lineMat, 0, null, 0, _evoMp);
        }

        // EVL_buildShardSet: the part's vertices in the world at this moment, and for each face the mean of its first three normals turned by the bone
        // (4096 = one unit): the face drifts that far x timer / 60 (EVL_renderTriShard/QuadShard). 31 frames, shade (31 - t) x 74 / 30 + 54, half see-through.
        void BuildShards(int bone)
        {
            if (bone < 0 || bone >= _evoParts.Count) return;
            var ep = _evoParts[bone];
            if (ep.Part == null || ep.Src == null || ep.Node == null || _shards.Count >= 30) return;
            var m = ep.Node.localToWorldMatrix; var srcV = ep.Src.vertices; var srcUv = ep.Src.uv;
            var faces = ep.Part.Faces; int n = Mathf.Min(faces.Count, srcV.Length / 3);
            var s = new Shards { Pos = new Vector3[n * 3], Uv = new Vector2[n * 3], Drift = new Vector3[n], Mesh = new Mesh() };
            var mat = PartnerMaterial(); s.Tex = mat != null ? mat.GetTexture("_BaseMap") : null;
            var nr = ep.Part.Normals;
            for (int j = 0; j < n; j++)
            {
                for (int k = 0; k < 3; k++) { s.Pos[3 * j + k] = m.MultiplyPoint3x4(srcV[3 * j + k]); s.Uv[3 * j + k] = srcUv[3 * j + k]; }
                var pn = faces[j].PrimN;
                if (pn == null || nr == null) continue;
                Vector3 sum = Vector3.zero;
                foreach (int ni in pn) if (ni * 3 + 2 < nr.Length) sum += new Vector3(nr[ni * 3], -nr[ni * 3 + 1], nr[ni * 3 + 2]);
                s.Drift[j] = m.MultiplyVector(sum / 3f) * Unit;       // game units of the turned normals, in metres
            }
            _shards.Add(s);
        }

        void DrawShards()
        {
            if (_halfMat == null) { var sh = Shader.Find("DW1Remake/Half"); if (sh == null) return; _halfMat = new Material(sh); }
            foreach (var s in _shards)
            {
                var v = new Vector3[s.Pos.Length]; var t = new int[s.Pos.Length];
                for (int j = 0; j < s.Drift.Length; j++)
                    for (int k = 0; k < 3; k++) { v[3 * j + k] = s.Pos[3 * j + k] + s.Drift[j] * (s.T / 60f); t[3 * j + k] = 3 * j + (k == 0 ? 0 : 3 - k); }
                s.Mesh.Clear(); s.Mesh.vertices = v; s.Mesh.uv = s.Uv; s.Mesh.triangles = t; s.Mesh.RecalculateBounds();
                float c = ((31 - s.T) * 74 / 30 + 54) / 128f;
                _evoMp.Clear(); if (s.Tex != null) _evoMp.SetTexture("_MainTex", s.Tex); _evoMp.SetColor("_Color", new Color(c, c, c, 1f));
                Graphics.DrawMesh(s.Mesh, Matrix4x4.identity, _halfMat, 0, null, 0, _evoMp);
            }
        }

        // EVL_renderSparkStreak: from bone 1 out past the peeled bone; length = r^2 / distance (r 400-500 at random each frame), the third corner shaken
        // by up to 80; picture at VRAM page (768,256) u 0x30-0x5F v 0xA0-0xA7 = ETCDAT/DEFEFF.TIM rows 32-39, colour set 1; grey 10-137 at random, added.
        void DrawStreaks()
        {
            var fx = _battle != null ? _battle.Effects : null;
            if (_defEff == null)
            {
                TimImage def;
                if (_disc.Exists("/ETCDAT/DEFEFF.TIM") && TimImage.TryParse(_disc.ReadFile("/ETCDAT/DEFEFF.TIM"), 0, out def)) _defEff = def.ToTexture(1);
                if (_defEff == null) return;
            }
            var addMat = fx != null ? fx.AddMaterial : null;
            if (addMat == null || _partnerAnim == null) return;
            foreach (var s in _streaks)
            {
                var nb = _partnerAnim.Node(s.Bone); var n1 = _partnerAnim.Node(1);
                if (nb == null || n1 == null) continue;
                Vector3 a = ToGameV(n1.position), d = ToGameV(nb.position) - a;
                float lenSq = d.sqrMagnitude; if (lenSq < 1f) continue;
                float r = Random.Range(0x190, 0x1F5); float sc = r * r;
                Vector3 b = a + d * sc / lenSq;
                Vector3 c = b + new Vector3(Random.Range(-0x50, 0x51), Random.Range(-0x50, 0x51), Random.Range(-0x50, 0x51));
                if (s.Mesh == null) s.Mesh = new Mesh();
                s.Mesh.Clear();
                s.Mesh.vertices = new[] { ToUnity(c), ToUnity(a), ToUnity(b) };
                float w = _defEff.width, h = _defEff.height;
                s.Mesh.uv = new[] { new Vector2(0x5F / w, 1f - 32f / h), new Vector2(0x5F / w, 1f - 39f / h), new Vector2(0x30 / w, 1f - 32f / h) };
                s.Mesh.triangles = new[] { 0, 1, 2 };
                s.Mesh.RecalculateBounds();
                float g = (Random.Range(0, 128) + 10) / 128f;
                _evoMp.Clear(); _evoMp.SetTexture("_MainTex", _defEff); _evoMp.SetColor("_Color", new Color(g, g, g, 1f));
                _evoMp.SetVector("_Rect", new Vector4(0f, 0f, 1f, 1f));
                Graphics.DrawMesh(s.Mesh, Matrix4x4.identity, addMat, 0, null, 0, _evoMp);
            }
        }

        // EVL_renderParticle: a square of lines flat at the partner's first place, half side lerp(8, 2500) over 86 frames, colour fading to 0
        void DrawRings()
        {
            foreach (var g in _rings)
            {
                int s = PsLerp(8, 0x9C4, 0, 0x56, g.T);
                Vector3 p = g.At;
                var v = new[] { ToUnity(p + new Vector3(s, 0, s)), ToUnity(p + new Vector3(s, 0, -s)), ToUnity(p + new Vector3(-s, 0, -s)), ToUnity(p + new Vector3(-s, 0, s)) };
                if (g.Mesh == null) g.Mesh = new Mesh();
                g.Mesh.Clear(); g.Mesh.vertices = v; g.Mesh.SetIndices(new[] { 0, 1, 1, 2, 2, 3, 3, 0 }, MeshTopology.Lines, 0); g.Mesh.RecalculateBounds();
                DrawLines(g.Mesh, new Color(PsLerp(g.Col.r, 0, 0, 0x56, g.T) / 255f, PsLerp(g.Col.g, 0, 0, 0x56, g.T) / 255f, PsLerp(g.Col.b, 0, 0, 0x56, g.T) / 255f, 1f));
            }
        }

        // EVL_brightenDigimonClut: every non-black colour moves towards white by a random 0-99% (one factor per channel each frame) x (t - start) / (end - start)
        Color32[] _evoOrig; Texture2D _evoTex;
        void Brighten(int t, int start, int end)
        {
            if (_evoTex == null || _evoOrig == null) return;
            if (t > end) t = end;
            int fr = Random.Range(0, 100), fg = Random.Range(0, 100), fb = Random.Range(0, 100);
            var px = new Color32[_evoOrig.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var o = _evoOrig[i];
                int r = o.r >> 3, g = o.g >> 3, b = o.b >> 3;
                if (o.a != 0 && (r != 0 || g != 0 || b != 0))
                {
                    r += fr * ((31 - r) * (t - start) / (end - start)) / 100;
                    g += fg * ((31 - g) * (t - start) / (end - start)) / 100;
                    b += fb * ((31 - b) * (t - start) / (end - start)) / 100;
                }
                px[i] = new Color32((byte)(r << 3), (byte)(g << 3), (byte)(b << 3), o.a);
            }
            _evoTex.SetPixels32(px); _evoTex.Apply();
        }

        void SetPartnerTint(Color c)
        {
            var mat = PartnerMaterial(); if (mat != null) mat.SetColor("_BaseColor", c);
        }

        IEnumerator EvolveScene(int target)
        {
            // main/evl.c: comes to the boy, idle; after 55 frames animation 1 and the evolving bubble; 55 more, music stops, the scene starts
            if (!Close(3.5f)) yield return ComeToBoy();
            int newType = DigimonStatsCatalog.GainOf(_disc, target).Target;
            var oldInfo = _menus.PartnerInfo; var newInfo = DigimonStatsCatalog.Get(_disc, newType);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            if (_audio != null) _audio.SetMapSounds(18);          // loadMapSounds2(18): the evolution's sounds in bank 8
            yield return new WaitForSeconds(55f / 20f);
            if (_partnerAnim != null && _partnerAnim.HasSlot(1)) _partnerAnim.SetSlot(1);
            _bubbleType = 7; _bubbleStart = Time.time;
            yield return new WaitForSeconds(55f / 20f);
            _bubbleType = -1;
            if (_audio != null) _audio.StopMusic();

            // EVL_initEvoSequence: the bone order is random (bone 0 never comes off)
            CollectEvoParts();
            int bones = _evoParts.Count;
            var order = new List<int>();
            { var keys = new int[bones]; for (int i = 1; i < bones; i++) keys[i] = Random.Range(0, 0x1000); for (int i = 1; i < bones; i++) { int best = 1; for (int j = 2; j < bones; j++) if (keys[best] > keys[j]) best = j; order.Add(best); keys[best] = 0x1000; } }
            int peeled = 0;
            Vector3 ringAt = ToGameV(_partner.position);
            var mat0 = PartnerMaterial();
            _evoTex = mat0 != null ? mat0.GetTexture("_BaseMap") as Texture2D : null;
            _evoOrig = _evoTex != null ? _evoTex.GetPixels32() : null;
            Vector3 camPos = _cam.transform.position; Quaternion camRot = _cam.transform.rotation; float camFov = _cam.fieldOfView; Vector2 camShift = _cam.lensShift;
            float vp0 = 120f / Mathf.Tan(camFov * 0.5f * Mathf.Deg2Rad);
            Vector3 savedLook = camPos + _cam.transform.forward * Vector3.Distance(camPos, _partner.position), savedEye = camPos;
            Vector3 scale0 = _partner.localScale;
            int camAngle = 0;
            var fx = _battle != null ? _battle.Effects : null;
            _wireTotal = 16;
            int frame = 0; float acc = 0f;
            int heightOld = oldInfo != null ? oldInfo.Height : 200, radiusOld = oldInfo != null ? oldInfo.Radius : 150;
            int heightNew = newInfo != null ? newInfo.Height : heightOld, radiusNew = newInfo != null ? newInfo.Radius : radiusOld;

            while (frame <= 0x162)
            {
                acc += Time.deltaTime;
                while (acc >= 0.05f && frame <= 0x162)
                {
                    acc -= 0.05f;
                    if (_partnerAnim != null && _partnerAnim.Slot == 12 && !_partnerAnim.Busy && _partnerAnim.HasSlot(1)) _partnerAnim.SetSlot(1);
                    if (frame < 0x20) _fade = frame / 32f;                              // state 0
                    if (frame == 0x20)
                    {
                        _fade = 0f; HideWorld(true); SetPartnerTint(Color.black);
                        _evoCam = true; _evoEye = savedEye; _evoLook = savedLook; _evoFov = camFov; _evoShift = camShift;
                    }
                    // EVL_updateEvoCamera (state 1, up to 106) and the turning camera of state 2 (from 165)
                    if (frame >= 0x20 && frame < 0x6A)
                    {
                        int size = Mathf.Max(heightOld, radiusOld) * 5 + 0x4B0;
                        Vector3 look, eye;
                        if (frame < 0x61)
                        {
                            camAngle = 0x638;
                            EvoCameraVectors((camAngle + GameHeading(_partner)) & 0xFFF, size, heightOld, out look, out eye);
                            if (frame <= 0x40) { float k = (frame - 0x20) / 32f; _evoLook = Vector3.Lerp(savedLook, look, k); _evoShift = Vector2.Lerp(camShift, Vector2.zero, k); }
                            else
                            {
                                float k = (frame - 0x40) / 32f; _evoEye = Vector3.Lerp(savedEye, eye, k);
                                int t = PsLerp(0, 0x14, 0, 0xC8, heightOld);
                                _evoShift = new Vector2(0f, Mathf.Lerp(0f, t / 240f, k));
                                _evoFov = 2f * Mathf.Atan(120f / Mathf.Lerp(vp0, 1000f, k)) * Mathf.Rad2Deg;
                            }
                        }
                        else
                        {
                            camAngle += 0x16;
                            EvoCameraVectors((camAngle + GameHeading(_partner)) & 0xFFF, size, heightOld, out look, out eye);
                            _evoLook = look; _evoEye = eye;
                        }
                    }
                    if (frame == 0x6A)
                    {
                        if (_audio != null) { _audio.Play(8, 1); _audio.Play(8, 0); }
                        if (_partnerAnim != null && _partnerAnim.HasSlot(12)) _partnerAnim.SetSlot(12, true);
                    }
                    if (frame >= 0x6A)
                    {
                        if (frame > 0x6A && RingFrames[frame % 18] != 0 && _rings.Count < 100)
                            _rings.Add(new Ring { At = ringAt, Col = new Color32(0x0A, (byte)PsLerp(0x32, 0xE6, 1, 5, RingFrames[frame % 18]), 0x0A, 255) });
                        if (frame >= 0x70 && frame < 0xCD) { if (frame == 0x70) SetPartnerTint(Color.white); Brighten(frame, 0x70, 0x9A); }
                        bool peel = frame == 0x6A || (frame >= 0x70 && frame < 0x9B && ShardFrames[frame - 0x70] >= 0) || (frame >= 0x9B && frame < 0x11C);
                        while (peel && peeled < order.Count)
                        {
                            int bone = order[peeled++];
                            if (_evoParts[bone].Part == null) continue;            // a bone without a part: the next one
                            if (_audio != null) _audio.Play(8, Random.Range(0, 3) + 2);
                            BuildShards(bone);
                            _evoParts[bone].Share = 0;
                            _streaks.Add(new Streak { Bone = bone, T = 0x11C - frame });
                            var nb = _partnerAnim != null ? _partnerAnim.Node(bone) : null;
                            if (fx != null && nb != null) fx.AddFlash(1, nb.position, 0xA, 0, 0x333, new Vector3(0x96, 0x96, 0x96), new Vector3(0x14, 0x14, 0x14));
                            break;
                        }
                        // growing to the new size, and the camera that turns round from 165 on
                        int rad = radiusOld, hei = heightOld;
                        if (frame >= 0xCC && frame < 0x108)
                        {
                            float rr = radiusNew / (float)radiusOld, hr = heightNew / (float)heightOld, k = (frame - 0xCC) / (float)(0x108 - 0xCC);
                            _partner.localScale = new Vector3(scale0.x * Mathf.Lerp(1f, rr, k), scale0.y * Mathf.Lerp(1f, hr, k), scale0.z * Mathf.Lerp(1f, rr, k));
                            rad = PsLerp(radiusOld, radiusNew, 0xCC, 0x108, frame); hei = PsLerp(heightOld, heightNew, 0xCC, 0x108, frame);
                        }
                        else if (frame >= 0x108) { rad = radiusNew; hei = heightNew; }
                        if (frame >= 0xA5)
                        {
                            camAngle += frame >= 0xC3 ? 0x16 : PsLerp(0, 0x16, 0xA4, 0xC2, frame);
                            Vector3 look, eye;
                            EvoCameraVectors((camAngle + GameHeading(_partner)) & 0xFFF, Mathf.Max(rad, hei) * 5 + 0x4B0, hei, out look, out eye);
                            _evoLook = look; _evoEye = eye;
                        }
                        if (frame == 0xD6 && _audio != null) _audio.Play(8, 5);
                        if (frame >= 0xD7 && frame < 0x11C && (frame & 1) == 0 && fx != null && _partnerAnim != null && _partnerAnim.Node(1) != null)
                        {
                            int c = PsLerp(0x32, 0x78, 0xD6, 0x11C, frame);
                            // every (lerp(5, 1)) frames the flash goes in front of everything (depth 0x21), else behind the models (0xFA0): it covers the change
                            int depth = (frame & PsLerp(5, 1, 0xD6, 0x11C, frame)) == 0 ? 0x21 : 0xFA0;
                            fx.AddFlash(0, _partnerAnim.Node(1).position, 0xA, 0, PsLerp(0x333, 0x4800, 0xD6, 0x11C, frame),
                                        new Vector3(c + Random.Range(0, 100), c + Random.Range(0, 100), c + Random.Range(0, 100)), new Vector3(0x14, 0x14, 0x14), depth);
                        }
                        if (frame == 0x11C)
                        {
                            // EVL_applyEvolution: the new Digimon, shown as a wireframe that fills in
                            foreach (var ep in _evoParts) if (ep.R != null) ep.R.enabled = true;
                            _care.ApplyEvolution(target);
                            SwapPartner(newType);
                            CollectEvoParts();
                            foreach (var ep in _evoParts) ep.Share = 16;
                            _wireTotal = 0;
                            _streaks.Clear();
                            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
                        }
                        if (frame >= 0x126 && frame < 0x14F) _wireTotal = PsLerp(0, 0x10, 0x126, 0x14E, frame);
                    }
                    for (int i = _shards.Count - 1; i >= 0; i--) if (++_shards[i].T > 0x1F) _shards.RemoveAt(i);
                    for (int i = _streaks.Count - 1; i >= 0; i--) if (--_streaks[i].T < 0) _streaks.RemoveAt(i);
                    for (int i = _rings.Count - 1; i >= 0; i--) if (++_rings[i].T > 0x56) _rings.RemoveAt(i);
                    frame++;
                }
                if (fx != null) fx.Tick(Time.deltaTime, _cam);
                DrawWireParts(); DrawShards(); DrawStreaks(); DrawRings();
                yield return null;
            }

            // state 3: everything back, the new Digimon cheers (animation 11) until the animation ends
            _wireTotal = 16;
            foreach (var ep in _evoParts) if (ep.R != null) ep.R.enabled = true;
            _shards.Clear(); _streaks.Clear(); _rings.Clear();
            _evoCam = false;
            _cam.transform.position = camPos; _cam.transform.rotation = camRot; _cam.fieldOfView = camFov; _cam.lensShift = camShift;
            HideWorld(false);
            if (_audio != null) { if (_mapId >= 0 && _mapId < _mapFlags.Count) _audio.SetMapSounds(_mapFlags[_mapId] & 0x1F); _audio.RestoreBgm(); }
            yield return Play(_partnerAnim, 11);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }
    }
}
