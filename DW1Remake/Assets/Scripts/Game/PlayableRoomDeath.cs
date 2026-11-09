using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The death with no lives left and the rebirth from the egg (dooa/dooa.c, doo2/doo2.c). 20 frames a second; the frame count goes on from
    /// MURD's 35. DOOA_tickDissolve: 35 the death map loads under the black (only the partner is drawn, at its old place), the camera swings round
    /// to it (35-85, DOOA_updateCutsceneCamera); 86 it stands at (0, 0, 0) facing the boy; 105-140 its faces come off in 36 waves
    /// (DOOA_SHARD_WAVE_SCHEDULE, DOOA_spawnBoneShards) and fall to the ground while the rest turns to wireframe; 191-208 it squashes and
    /// stretches (DOOA_DISSOLVE_SCALE_CURVE) and jumps up; 209 it is gone and a column of flashes sinks (209-309); 309 the shards start
    /// flickering in random colours; 329-379 the camera goes back; 389-439 the first map Digimon (Jijimon) fades in from black.
    /// DOOA_tickRebirth (script routine 4): the shards rise and spin (10), then fall into the middle (100-132) while the egg (ETCDAT/TAMA.TMD,
    /// six parts for each of the four eggs) fills in from wireframe; 132-152 flashes; 152 the baby replaces the partner and the map fades in
    /// (152-202); 202 the baby shows (animation 0x1C) and the egg parts break off one by one (DOOA_SPARKLE_BONE_IDS, DOO2_buildShardSet);
    /// 262 animation 0x0B, then the scene ends.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        static readonly int[] ShardWave = { 15, 15, 15, 15, 15, 14, 14, 14, 14, 13, 13, 13, 12, 12, 12, 11, 11, 11, 10, 10, 10, 9, 9, 8, 8, 7, 7, 6, 6, 5, 5, 4, 3, 2, 1, 0 };
        static readonly int[] DissolveCurve = { 100, 94, 89, 85, 82, 80, 79, 79, 100, 120, 139, 157, 174, 190, 205, 219, 232, 244, 255, 265, 274, 282, 289, 295, 300, 350, 395, 435, 470, 500, 525, 545 };
        static readonly int[] SparkleBones = { -1, -1, -1, -1, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, 4, 5, -1, 0 };

        // ---- DOOA shards: one per model face, kept as a centre and its corners round it (game units, the centre from the partner's spot)
        sealed class DooaShard
        {
            public Vector3 Center; public Vector3[] Corner; public Vector2[] Uv;
            public int RotY, Spin, SpinMax, TargetRadius, Radius = 0x1000, DropDepth, FallSpeed, AxisDistance = 1, Delay;
        }
        readonly List<DooaShard> _dooa = new List<DooaShard>();
        int _shardState, _shardPrev; bool _shardFlicker, _shardsOn; Color _shardCol = new Color(0x50 / 128f, 0x50 / 128f, 0x50 / 128f, 1f);
        float _storedY; Mesh _shardMesh;

        // the egg (DOO2): its parts, which are gone, the wireframe share (MAIN_D_80135324) and the parts breaking (DOO2 shard sets)
        sealed class EggPart { public MmdModel.Part Part; public bool Gone; }
        sealed class EggShards { public MmdModel.Part Part; public int T; public float[] OffX, OffY, OffZ; public int[] Frame; }
        readonly List<EggPart> _egg = new List<EggPart>();
        readonly List<EggShards> _eggShards = new List<EggShards>();
        Texture2D[] _eggTex; Material[] _eggMat; int _eggWire; bool _eggOn; Mesh _eggMesh, _eggLines;

        // DOOA_spawnBoneShards: every bone from 2 on; a face comes off when WIREFRAME_RNG_TABLE[face & 15] is this wave's number (and 3 in 4 times)
        void SpawnShardWave(int wave)
        {
            for (int b = 2; b < _evoParts.Count; b++)
            {
                var ep = _evoParts[b];
                if (ep.Part == null || ep.Src == null || ep.Node == null) continue;
                var m = ep.Node.localToWorldMatrix; var srcV = ep.Src.vertices; var srcUv = ep.Src.uv;
                var faces = ep.Part.Faces;
                Vector3 loc = ToGameV(_partner.position);
                DooaShard cur = null; int curPrim = -1;
                var corners = new List<Vector3>(); var uvs = new List<Vector2>();
                for (int j = 0; j < faces.Count && 3 * j + 2 < srcV.Length; j++)
                {
                    var f = faces[j];
                    if (f.Prim != curPrim)
                    {
                        Close(cur, corners, uvs);
                        curPrim = f.Prim; cur = null; corners.Clear(); uvs.Clear();
                        if ((Random.Range(0, 4) & 3) == 0 || WireOrder[f.Prim & 15] != wave) continue;
                        cur = new DooaShard();
                    }
                    if (cur == null) continue;
                    for (int k = 0; k < 3; k++) { corners.Add(ToGameV(m.MultiplyPoint3x4(srcV[3 * j + k]))); uvs.Add(srcUv[3 * j + k]); }
                }
                Close(cur, corners, uvs);
            }
        }

        void Close(DooaShard s, List<Vector3> corners, List<Vector2> uvs)
        {
            if (s == null || corners.Count == 0 || _dooa.Count > 3000) return;
            Vector3 c = Vector3.zero; foreach (var p in corners) c += p; c /= corners.Count;
            s.Corner = new Vector3[corners.Count]; s.Uv = uvs.ToArray();
            for (int i = 0; i < corners.Count; i++) s.Corner[i] = corners[i] - c;
            Vector3 loc = ToGameV(_partner.position);
            s.Center = new Vector3(c.x - loc.x, c.y - _storedY, c.z - loc.z);
            s.AxisDistance = Mathf.Max(1, (int)Mathf.Sqrt(s.Center.x * s.Center.x + s.Center.z * s.Center.z));
            _dooa.Add(s);
        }

        // DOOA_updateShards
        void TickShards()
        {
            if (_shardFlicker) _shardCol = new Color((Random.Range(0, 100) + 60) / 128f, (Random.Range(0, 100) + 60) / 128f, (Random.Range(0, 100) + 60) / 128f, 1f);
            foreach (var s in _dooa)
            {
                switch (_shardState)
                {
                    case 0:
                        s.FallSpeed = Mathf.Min(0x400, s.FallSpeed + 1);
                        s.Center.y = Mathf.Min(0f, s.Center.y + s.FallSpeed);
                        break;
                    case 1:
                        if (_shardState != _shardPrev)
                        {
                            s.Spin = 0; s.SpinMax = Random.Range(0, 0x155) + 0xE3; s.Radius = 0x1000;
                            int lo = 0x64000 / s.AxisDistance, hi = 0x190000 / s.AxisDistance;
                            s.TargetRadius = lo + (hi > lo ? Random.Range(0, hi - lo) : 0);
                            s.FallSpeed = 0; s.DropDepth = Random.Range(0, 0x226);
                        }
                        s.Spin = Mathf.Min(s.SpinMax, s.Spin + (s.SpinMax / 80 == 0 ? 1 : s.SpinMax / 100));
                        s.RotY += s.Spin;
                        if (s.Radius < s.TargetRadius) s.Radius += s.TargetRadius / 80 == 0 ? 1 : s.TargetRadius / 100;
                        s.FallSpeed++;
                        s.Center.y = Mathf.Max(-s.DropDepth, s.Center.y - (s.FallSpeed >> 3));
                        break;
                    case 2: s.RotY += s.Spin; break;
                    case 3:
                        if (_shardState != _shardPrev) { s.Delay = Random.Range(0, 20); s.TargetRadius = s.Radius; }
                        if (s.Delay > 0) s.Delay--;
                        else if (s.Delay == 0)
                        {
                            s.Radius -= s.TargetRadius / 10 + 1;
                            if (s.Radius < 0) { s.Radius = 0; s.Delay = -1; }
                            s.Center.y = PsLerp(-s.DropDepth, -100, s.TargetRadius, 0, s.Radius);      // DOOA_getStoredDigimonY() - 100, from the stored height
                        }
                        s.RotY = (s.RotY + s.Spin) % 4096;
                        break;
                }
            }
            _shardPrev = _shardState;
        }

        // DOOA_renderShards: centre x radius / 4096, turned about Y (RotMatrixZYX) together with the corners, at the partner's spot. Flickering: flat
        // random colours; else the face's own picture at colour (0x50, 0x50, 0x50).
        void DrawDooaShards()
        {
            if (!_shardsOn || _dooa.Count == 0) return;
            var mat = _shardFlicker ? null : PartnerMaterial();
            if (_shardFlicker && _lineMat == null) { var sh = Shader.Find("Universal Render Pipeline/Unlit"); if (sh != null) _lineMat = new Material(sh); }
            Vector3 loc = ToGameV(_partner != null ? _partner.position : Vector3.zero);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            foreach (var s in _dooa)
            {
                if (s.Delay < 0) continue;
                float a = s.RotY * Mathf.PI * 2f / 4096f, c = Mathf.Cos(a), sn = Mathf.Sin(a);
                Vector3 off = new Vector3(s.Center.x * s.Radius / 4096f, s.Center.y, s.Center.z * s.Radius / 4096f);
                int b = v.Count;
                for (int i = 0; i < s.Corner.Length; i++)
                {
                    Vector3 p = off + s.Corner[i];
                    Vector3 r = new Vector3(c * p.x + sn * p.z, p.y, -sn * p.x + c * p.z);
                    v.Add(ToUnity(new Vector3(r.x + loc.x, r.y + _storedY, r.z + loc.z))); uv.Add(s.Uv[i]);
                }
                for (int i = 0; i + 2 < s.Corner.Length; i += 3) { tri.Add(b + i); tri.Add(b + i + 2); tri.Add(b + i + 1); }
            }
            if (_shardMesh == null) _shardMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _shardMesh.Clear(); _shardMesh.SetVertices(v); _shardMesh.SetUVs(0, uv); _shardMesh.SetTriangles(tri, 0); _shardMesh.RecalculateNormals(); _shardMesh.RecalculateBounds();
            _evoMp.Clear();
            if (_shardFlicker) { if (_lineMat == null) return; _evoMp.SetColor("_BaseColor", _shardCol); Graphics.DrawMesh(_shardMesh, Matrix4x4.identity, _lineMat, 0, null, 0, _evoMp); }
            else if (mat != null) { _evoMp.SetColor("_BaseColor", _shardCol); Graphics.DrawMesh(_shardMesh, Matrix4x4.identity, mat, 0, null, 0, _evoMp); }
        }

        // ---- the egg
        void LoadEgg(int slot)
        {
            _egg.Clear(); _eggShards.Clear();
            try
            {
                byte[] tmd = _disc.ReadFile("/ETCDAT/TAMA.TMD");
                var model = MmdModel.Parse(PrefixTmd(tmd));
                TimImage tim;
                if (!TimImage.TryParse(_disc.ReadFile("/ETCDAT/TAMA.TIM"), 0, out tim)) return;
                if (_eggTex == null)
                {
                    _eggTex = new Texture2D[4]; _eggMat = new Material[4];
                    for (int r = 0; r < 4 && r < tim.PaletteCount; r++) { _eggTex[r] = tim.ToTexture(r); _eggMat[r] = MmdModel.MakeMaterial(_eggTex[r]); }
                }
                for (int i = 0; i < 6; i++) { int o = slot * 6 + i; _egg.Add(new EggPart { Part = o < model.Parts.Count ? model.Parts[o] : null }); }
            }
            catch (System.Exception e) { Debug.LogWarning("[death] egg: " + e.Message); }
        }

        static byte[] PrefixTmd(byte[] tmd) { var b = new byte[tmd.Length + 4]; b[0] = 4; System.Array.Copy(tmd, 0, b, 4, tmd.Length); return b; }

        static Vector3 EggVert(MmdModel.Part p, int i) { return new Vector3(p.Verts[i * 3], -p.Verts[i * 3 + 1], p.Verts[i * 3 + 2]) * MmdModel.Scale; }

        // DOO2_renderWireframeModel: faces with WIREFRAME_RNG_TABLE[face & 15] below the share are drawn, the others as lines of one grey (55-254)
        void DrawEgg()
        {
            if (!_eggOn || _eggMat == null || _cam == null) return;
            var lv = new List<Vector3>(); var li = new List<int>();
            for (int row = 0; row < 4; row++)
            {
                var fv = new List<Vector3>(); var fu = new List<Vector2>(); var ft = new List<int>();
                foreach (var e in _egg)
                {
                    if (e.Gone || e.Part == null) continue;
                    foreach (var f in e.Part.Faces)
                    {
                        Vector3 a = EggVert(e.Part, f.V[0]), b = EggVert(e.Part, f.V[1]), c = EggVert(e.Part, f.V[2]);
                        if (WireOrder[f.Prim & 15] < _eggWire)
                        {
                            if (f.Row != row) continue;
                            int k = fv.Count;
                            fv.Add(a); fv.Add(b); fv.Add(c);
                            for (int q = 0; q < 3; q++) fu.Add(new Vector2((f.U[q] + 0.5f) / _eggTex[0].width, 1f - (f.T[q] + 0.5f) / _eggTex[0].height));
                            ft.Add(k); ft.Add(k + 2); ft.Add(k + 1);
                        }
                        else if (row == 0 && !f.Second)
                        {
                            int k = lv.Count;
                            lv.Add(a); lv.Add(b); lv.Add(c);
                            li.Add(k); li.Add(k + 1); li.Add(k + 1); li.Add(k + 2); li.Add(k + 2); li.Add(k);
                        }
                    }
                }
                if (ft.Count > 0 && _eggMat[row] != null)
                {
                    var mesh = new Mesh(); mesh.SetVertices(fv); mesh.SetUVs(0, fu); mesh.SetTriangles(ft, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
                    Graphics.DrawMesh(mesh, Matrix4x4.identity, _eggMat[row], 0);
                    Destroy(mesh, 0.2f);
                }
            }
            if (li.Count > 0)
            {
                if (_eggLines == null) _eggLines = new Mesh();
                _eggLines.Clear(); _eggLines.SetVertices(lv); _eggLines.SetIndices(li, MeshTopology.Lines, 0); _eggLines.RecalculateBounds();
                float g = (0x37 + Random.Range(0, 0xFF - 0x37)) / 255f;
                DrawLines(_eggLines, new Color(g, g, g, 1f));
            }
            DrawEggShards();
        }

        // DOO2_renderTriShard / QuadShard (speed 80): each face falls (offset y += frame + centre y / 240) until it reaches the ground, and moves out by
        // centre / 240 while in the air; shade ((41 - t) x 74 / 40 + 54) / 128, half see-through; a set lasts 41 frames
        void TickEggShards()
        {
            for (int i = _eggShards.Count - 1; i >= 0; i--)
            {
                var s = _eggShards[i];
                if (++s.T >= 41) { _eggShards.RemoveAt(i); continue; }
                var faces = s.Part.Faces;
                for (int j = 0; j < faces.Count; j++)
                {
                    var f = faces[j];
                    Vector3 a = new Vector3(s.Part.Verts[f.V[0] * 3], s.Part.Verts[f.V[0] * 3 + 1], s.Part.Verts[f.V[0] * 3 + 2]);
                    Vector3 b = new Vector3(s.Part.Verts[f.V[1] * 3], s.Part.Verts[f.V[1] * 3 + 1], s.Part.Verts[f.V[1] * 3 + 2]);
                    Vector3 c = new Vector3(s.Part.Verts[f.V[2] * 3], s.Part.Verts[f.V[2] * 3 + 1], s.Part.Verts[f.V[2] * 3 + 2]);
                    Vector3 mid = (a + b + c) / 3f;
                    float low = (a.y + b.y + c.z) / 3f;      // the game adds the third corner's z here
                    s.Frame[j]++;
                    s.OffY[j] += s.Frame[j] + mid.y / 240f;
                    if (s.OffY[j] + low > 0f) s.OffY[j] = -low;
                    else { s.OffX[j] += mid.x / 240f; s.OffZ[j] += mid.z / 240f; }
                }
            }
        }

        void DrawEggShards()
        {
            if (_halfMat == null) { var sh = Shader.Find("DW1Remake/Half"); if (sh == null) return; _halfMat = new Material(sh); }
            foreach (var s in _eggShards)
            {
                var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
                var faces = s.Part.Faces;
                for (int j = 0; j < faces.Count; j++)
                {
                    var f = faces[j]; int k = v.Count;
                    for (int q = 0; q < 3; q++)
                    {
                        int vi = f.V[q];
                        v.Add(ToUnity(new Vector3(s.Part.Verts[vi * 3] + s.OffX[j], s.Part.Verts[vi * 3 + 1] + s.OffY[j], s.Part.Verts[vi * 3 + 2] + s.OffZ[j])));
                        uv.Add(new Vector2((f.U[q] + 0.5f) / _eggTex[0].width, 1f - (f.T[q] + 0.5f) / _eggTex[0].height));
                    }
                    t.Add(k); t.Add(k + 2); t.Add(k + 1);
                }
                var mesh = new Mesh(); mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(t, 0); mesh.RecalculateBounds();
                float c = ((41 - s.T) * 74 / 40 + 54) / 128f;
                int row = faces.Count > 0 ? Mathf.Clamp(faces[0].Row, 0, 3) : 0;
                _evoMp.Clear(); if (_eggTex[row] != null) _evoMp.SetTexture("_MainTex", _eggTex[row]); _evoMp.SetColor("_Color", new Color(c, c, c, 1f));
                Graphics.DrawMesh(mesh, Matrix4x4.identity, _halfMat, 0, null, 0, _evoMp);
                Destroy(mesh, 0.2f);
            }
        }

        // ---- DOOA_updateCutsceneCamera: the first half turns the view to the partner (and the drawing offset to the middle), the second half
        // moves the eye round to it at (heading + 0x638), (height x 5 + 1200) away, and the projection to 1000
        struct DView { public Vector3 Eye, Look; public float Fov; public Vector2 Shift; }

        DView CurrentView()
        {
            return new DView { Eye = _cam.transform.position, Look = _cam.transform.position + _cam.transform.forward * Vector3.Distance(_cam.transform.position, _partner.position), Fov = _cam.fieldOfView, Shift = _cam.lensShift };
        }

        void CutsceneCamera(DView start, int height, int t0, int t1, int frame)
        {
            if (frame < t0 || frame > t1) return;
            int mid = (t0 + t1) / 2;
            Vector3 look, eye;
            EvoCameraVectors((GameHeading(_partner) + 0x638) & 0xFFF, height * 5 + 1200, height, out look, out eye);
            _evoCam = true;
            float vp0 = 120f / Mathf.Tan(start.Fov * 0.5f * Mathf.Deg2Rad);
            if (frame <= mid)
            {
                float k = mid == t0 ? 1f : (frame - t0) / (float)(mid - t0);
                _evoLook = Vector3.Lerp(start.Look, look, k); _evoShift = Vector2.Lerp(start.Shift, Vector2.zero, k);
                _evoEye = start.Eye; _evoFov = start.Fov;
            }
            else
            {
                float k = t1 == mid ? 1f : (frame - mid) / (float)(t1 - mid);
                _evoEye = Vector3.Lerp(start.Eye, eye, k);
                _evoShift = new Vector2(0f, Mathf.Lerp(0f, PsLerp(0, 20, 0, 200, height) / 240f, k));
                _evoFov = 2f * Mathf.Atan(120f / Mathf.Lerp(vp0, 1000f, k)) * Mathf.Rad2Deg;
                _evoLook = look;
            }
        }

        void SetTint(Transform t, float k)
        {
            if (t == null) return;
            var mp = new MaterialPropertyBlock();
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                r.GetPropertyBlock(mp);
                mp.SetColor("_BaseColor", new Color(k * MmdModel.Brightness, k * MmdModel.Brightness, k * MmdModel.Brightness, 1f));
                r.SetPropertyBlock(mp);
            }
        }

        // DOOA_tickDissolve, from frame 35 (lives = 0)
        IEnumerator DeathScene(int section, bool late)
        {
            var info = DigimonStatsCatalog.Get(_disc, PartnerType);
            int height = info != null ? info.Height : 200;
            var fx = _battle != null ? _battle.Effects : null;
            DView oldView = CurrentView();
            Vector3 oldPos = _partner.position; Quaternion oldRot = _partner.rotation;
            _storedY = ToGameV(_partner.position).y;
            // phase 0 (frame 35): the death map under the black, only the partner drawn, where it was
            _runner = null; _line = null; _syncBit = true;
            HideWorld(false);
            DoChangeMap(new ScriptAction { Map = late ? 0xCD : 0xDA, Exit = 1, Arrive = 255 }, true);
            while (_changing) yield return null;
            if (_audio != null) { _audio.StopMusic(); _audio.SetMapSounds(0x13); }          // loadMapSounds2(0x13)
            DView newView = CurrentView();
            HideWorld(true);
            _partner.gameObject.SetActive(true);
            _partner.position = oldPos; _partner.rotation = oldRot;
            // DOOA_tickDissolve phase 0: with lives left (old age) animation 1; none left 0x2C with animFlag bit 0 cleared, so it holds its first pose
            bool livesLeft = _state.Life != 0;
            if (_partnerAnim != null)
            {
                if (livesLeft) { if (_partnerAnim.HasSlot(1)) _partnerAnim.SetSlot(1); }
                else if (_partnerAnim.HasSlot(0x2C)) { _partnerAnim.SetSlot(0x2C); _partnerAnim.Playing = false; }
            }
            _evoCam = true; _evoEye = oldView.Eye; _evoLook = oldView.Look; _evoFov = oldView.Fov; _evoShift = oldView.Shift;
            _dooa.Clear(); _shardState = 0; _shardPrev = 0; _shardFlicker = false; _shardsOn = true; _shardCol = new Color(0x50 / 128f, 0x50 / 128f, 0x50 / 128f, 1f);
            CollectEvoParts(); _wireTotal = 16;
            Vector3 scale0 = _partner.localScale;
            float targetY = 0f;
            Npc first = _npcs.Count > 0 ? _npcs[0] : null;
            int frame = 0x23; float acc = 0f;
            while (frame < 0x1B8)
            {
                acc += Time.deltaTime;
                while (acc >= 0.05f && frame < 0x1B8)
                {
                    acc -= 0.05f; frame++;
                    if (frame < 0x56) CutsceneCamera(oldView, height, 0x23, 0x55, frame);
                    if (frame == 0x56)
                    {
                        // phase 2: at (0, 0, 0), facing the boy, the death map's own view as the start
                        _partner.position = new Vector3(0f, 0f, 0f);
                        Vector3 d = _player.position - _partner.position; d.y = 0f;
                        if (d.sqrMagnitude > 1e-4f) _partner.rotation = FaceRot(d);
                        _storedY = 0f;
                        oldView = newView;
                        CutsceneCamera(oldView, height, 0, 0x64, 0x63);
                    }
                    // phase 2's first frame: with lives left animation 0x0C; at the top of each tick, once 0x0C has played to its end, animation 1 again
                    // and the partner's height and place are stored (DOOA_storeDigimonY)
                    if (frame == 0x56 && livesLeft && _partnerAnim != null && _partnerAnim.HasSlot(0x0C)) _partnerAnim.SetSlot(0x0C, true);
                    else if (livesLeft && _partnerAnim != null && _partnerAnim.Slot == 0x0C && !_partnerAnim.Busy && _partner.gameObject.activeSelf)
                    {
                        if (_partnerAnim.HasSlot(1)) _partnerAnim.SetSlot(1);
                        _storedY = ToGameV(_partner.position).y;
                    }
                    if (frame == 0x69 && _audio != null) _audio.Play(8, 0);
                    if (frame == 0x8C && _audio != null) _audio.StopSound();
                    // phase 4: the sequence ends the frame after 0x1B7 with stopSound
                    if (frame == 0x1B8 && _audio != null) _audio.StopSound();
                    if (frame >= 0x69 && frame < 0x8D)
                    {
                        int w = frame - 0x69;
                        _wireTotal = ShardWave[w];
                        if (w == 0 || ShardWave[w - 1] != ShardWave[w]) SpawnShardWave(ShardWave[w]);
                    }
                    if (frame == 0xBF)
                    {
                        targetY = -(height + 100);
                        if (_audio != null) { _audio.StopSound(); _audio.Play(8, 1); }
                    }
                    if (frame >= 0xBF && frame < 0xD1)
                    {
                        // the squash and stretch; past 300 on the curve it is up in the air
                        int idx = Mathf.Clamp(PsLerp(0, 0x1F, 0xBF, 0xD0, frame), 0, 31);
                        int work = (DissolveCurve[idx] - 100) * 200 / 100 + 100;
                        float sy = work / 100f, sx = (10000f / work) / 100f;
                        sx = Mathf.Lerp(sx, sx * 0.5f, (frame - 0xBF) / (float)(0xD0 - 0xBF));
                        if (DissolveCurve[idx] >= 0x12D)
                        {
                            int w2 = 0x12C - (DissolveCurve[idx] - 0x12C);
                            sy = ((w2 - 100) * 200 / 100 + 100) / 100f;
                            var p = _partner.position; p.y = -targetY * Unit; _partner.position = p;
                        }
                        _partner.localScale = new Vector3(scale0.x * sx, scale0.y * sy, scale0.z * sx);
                    }
                    if (frame == 0xD1)
                    {
                        _wireTotal = 16;
                        _partner.localScale = scale0;
                        var p = _partner.position; p.y = 0f; _partner.position = p;
                        _partner.gameObject.SetActive(false);
                        if (_audio != null) _audio.Play(8, 2);
                    }
                    if (frame >= 0xD1 && frame < 0x135 && fx != null)
                    {
                        // a flash each frame sinking from above where it jumped to the ground (mode 0, 8 frames, scale 0 - 0x14)
                        float y = PsLerp((int)targetY, 0, 0xD1, 0x135, frame);
                        var col = new Vector3(Random.Range(0, 100) + 60, Random.Range(0, 100) + 60, Random.Range(0, 100) + 60);
                        // setEFEFlashOffset(x = _sin(lerp(0, 0x600)) x 10 / 4096): pushed sideways on the screen by x x viewport / depth, i.e. x game
                        // units across the picture at its own depth, so the column of flashes sways three times as it sinks
                        int sway = PsSin512(PsLerp(0, 0x600, 0xD1, 0x135, frame)) * 10 / 4096;
                        Vector3 at = ToUnity(new Vector3(0f, y, 0f)) + (_cam != null ? _cam.transform.right : Vector3.right) * sway * Unit;
                        fx.AddFlash(0, at, 8, 0, 0x14, col, new Vector3(0x14, 0x14, 0x14));
                    }
                    if (frame == 0x135) { _shardFlicker = true; if (_audio != null) _audio.Play(8, 3); }
                    if (frame >= 0x135 && frame < 0x17B) CutsceneCamera(oldView, height, 0x149, 0x17B, 0x2C4 - frame);
                    if (frame == 0x17B) LoadEgg(Random.Range(0, 4));
                    if (frame >= 0x185 && frame < 0x1B8 && first != null && first.T != null)
                    {
                        first.T.gameObject.SetActive(true);
                        SetTint(first.T, 1f - PsLerp(0xFF, 0, 0x185, 0x1B7, frame) / 255f);
                    }
                    TickShards();
                }
                if (fx != null) fx.Tick(Time.deltaTime, _cam);
                if (_partner.gameObject.activeSelf) DrawWireParts();
                DrawDooaShards();
                yield return null;
            }
            // phase 4: the camera back to the map; the stage stays dark until the rebirth
            if (first != null && first.T != null) SetTint(first.T, 1f);
            _evoCam = false;
            StartCoroutine(ShardsAfterDissolve());
            // the end of the sequence: partnerTickDying sets SOME_SCRIPT_SYNC_BIT (our Finish), and the death map's section, already started on
            // arrival by SECTION_ON_EXIT (0x7B in DG.SCN 0 section 0x4DE) and waiting at 4A D5, goes on; it is not started again
        }

        // DOOA_initShardEffect adds the shards as their own object (0x608: DOOA_updateShards / DOOA_renderShards); the dissolve's end does not
        // remove it, so they lie flickering on the floor while the death map's section talks, until the rebirth takes them (DOOA_removeShardEffect)
        IEnumerator ShardsAfterDissolve()
        {
            int map = _mapId; float acc = 0f;
            while (_shardsOn && !_eggOn && _dooa.Count > 0 && _mapId == map)
            {
                acc += Time.deltaTime;
                while (acc >= 0.05f) { acc -= 0.05f; TickShards(); }
                DrawDooaShards();
                yield return null;
            }
        }

        // DOOA_getSequenceState / DOOA_tickRebirth (script routine 4)
        int _rebirthStreaks;
        readonly List<Mesh> _rebirthMeshes = new List<Mesh>();

        // DOOA_renderRebirth: fadeLevel spark streaks (DOO2_renderSparkStreak) out of the partner's place 100 units up: (0, 0, customRandom(400, 500))
        // turned by RotMatrixZYX(rand % customRandom(-170, 1024), rand, 0); the third corner is that end shaken by up to 50 on each axis
        void DrawRebirthStreaks()
        {
            if (_rebirthStreaks <= 0 || _partner == null) return;
            Vector3 o = ToGameV(_partner.position) + new Vector3(0f, -100f, 0f);
            for (int i = 0; i < _rebirthStreaks; i++)
            {
                int range = Random.Range(-170, 1025);
                int rx = range != 0 ? Random.Range(0, 0x8000) % range : 0;       // C's %: the sign of the left side (rand is never negative)
                int ry = Random.Range(0, 0x8000);
                Vector3 a = o + EffectPlayer.RotateZYX(rx, ry, 0, new Vector3(0f, 0f, Random.Range(400, 501)));
                Vector3 b = a + new Vector3(Random.Range(-50, 51), Random.Range(-50, 51), Random.Range(-50, 51));
                while (_rebirthMeshes.Count <= i) _rebirthMeshes.Add(new Mesh());
                if (!DrawStreakTri(_rebirthMeshes[i], b, o, a)) return;
            }
        }

        IEnumerator Rebirth()
        {
            int slot = _state.Life > 0 || _state.Para.IsNaturalDeath ? _eggSlot : Random.Range(0, 4);
            var fx = _battle != null ? _battle.Effects : null;
            LoadEgg(slot);
            _eggOn = true; _eggWire = 0;
            bool hadShards = _dooa.Count > 0;
            _shardsOn = hadShards;
            int frame = 0, sparkle = 0; float acc = 0f;
            bool babyOut = false, ended = false;
            while (!ended)
            {
                acc += Time.deltaTime;
                while (acc >= 0.05f && !ended)
                {
                    acc -= 0.05f; frame++;
                    // DOOA_tickRebirth: fadeLevel is 0 each frame; in phase 0xCE (from the frame after 132) up to 146 it is lerp(5, 0, 132, 150)
                    _rebirthStreaks = frame > 132 && frame < 147 ? PsLerp(5, 0, 132, 150, frame) : 0;
                    if (frame == 10) { _shardState = 1; if (_audio != null) { _audio.StopSound(); _audio.Play(8, 5); } }
                    if (frame == 100) _shardState = 3;
                    if (frame >= 100 && frame <= 132) _eggWire = frame >= 130 ? 0x10 : PsLerp(1, 0x10, 100, 132, frame);
                    if (frame == 132)
                    {
                        _shardsOn = false; _dooa.Clear();
                        if (_audio != null) _audio.Play(8, 6);
                    }
                    if (frame >= 132 && frame <= 152 && fx != null)
                    {
                        int scale = PsLerp(20, 10, 132, 152, frame) * 25 * 4096 / 1000, lv = PsLerp(120, 10, 132, 152, frame), r = Mathf.Max(1, lv * 50 / 120);
                        fx.AddFlash(0, ToUnity(new Vector3(0, -100, 0)), 4, 0, scale, new Vector3(lv + Random.Range(0, r), lv + Random.Range(0, r), lv + Random.Range(0, r)), new Vector3(lv / 6, lv / 6, lv / 6));
                    }
                    if (frame == 152)
                    {
                        // reincarnatePartner, the boy and the map back, the scene fades in from black
                        _care.Reincarnate(Babies[slot]);
                        SwapPartner(Babies[slot]);
                        _partner.position = Vector3.zero;
                        HideWorld(false);
                        _partner.gameObject.SetActive(false);
                        _fade = 1f;
                    }
                    if (frame > 152 && frame < 202) _fade = PsLerp(255, 0, 152, 202, frame) / 255f;
                    if (frame == 202)
                    {
                        _fade = 0f;
                        _partner.gameObject.SetActive(true);
                        Vector3 d = _player.position - _partner.position; d.y = 0f;
                        if (d.sqrMagnitude > 1e-4f) _partner.rotation = FaceRot(d);
                        if (_partnerAnim != null && _partnerAnim.HasSlot(0x1C)) _partnerAnim.SetSlot(0x1C);
                    }
                    if (frame > 202)
                    {
                        if (sparkle < 47)
                        {
                            int b = SparkleBones[sparkle];
                            if (b >= 0 && b < _egg.Count && !_egg[b].Gone && _egg[b].Part != null)
                            {
                                int n = _egg[b].Part.Faces.Count;
                                if (_eggShards.Count < 6) _eggShards.Add(new EggShards { Part = _egg[b].Part, OffX = new float[n], OffY = new float[n], OffZ = new float[n], Frame = new int[n] });
                                _egg[b].Gone = true;
                                if (_audio != null) _audio.Play(8, 7);
                            }
                            sparkle++;
                        }
                        else if (frame >= 262 && !babyOut)
                        {
                            babyOut = true;
                            if (_partnerAnim != null && _partnerAnim.HasSlot(0x0B)) _partnerAnim.SetSlot(0x0B, true);
                        }
                        else if (babyOut && (_partnerAnim == null || !_partnerAnim.Busy)) ended = true;
                    }
                    TickShards();
                    TickEggShards();
                }
                if (fx != null) fx.Tick(Time.deltaTime, _cam);
                DrawDooaShards();
                DrawEgg();
                DrawRebirthStreaks();
                yield return null;
            }
            _rebirthStreaks = 0;
            _eggOn = false; _egg.Clear(); _eggShards.Clear(); _dooa.Clear(); _shardsOn = false;
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            if (_audio != null && _mapId >= 0 && _mapId < _mapFlags.Count) { _audio.SetMapSounds(_mapFlags[_mapId] & 0x1F); _audio.RestoreBgm(); }
        }
    }
}
