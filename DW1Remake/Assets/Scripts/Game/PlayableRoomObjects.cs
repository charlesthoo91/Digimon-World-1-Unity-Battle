using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The map's 3D objects (main/map_object.c): doors and machines (loadDoors, renderDoors, tickRotateDoor; script 0x70), the boulder
    /// (spawnBoulder, moveBoulder; 0x76-0x78) and the chest model (initializeChest, renderChest). Also the script moves of map pieces
    /// (tickMoveObjectTo 0x71, spawnSpriteAtLocation 0x7D, spawnSpriteAtEntity 0x7E, overworld.c).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        sealed class StaticModel { public Mesh Solid, Blend; public Material[] SolidMats, BlendMats; public int[] Packet, Corner; }   // per vertex: its TMD packet, its TMD vertex
        sealed class Door { public int Model = -1, Direction; public int[] Pos = new int[3], Rot = new int[3]; public Transform T, Copy; }
        sealed class PieceMove { public int Piece, Steps, X, Y, Dx, Dy; public bool Started; }

        MapOverlay _mapVram;                                                  // the map's images in the emulated video memory (door pictures)
        GameObject _objRoot;
        readonly List<Door> _doors = new List<Door>();                        // MAP_3D_OBJ: 6 slots, empty ones keep their place
        readonly List<Object> _objAssets = new List<Object>();                // meshes, textures and materials made for this map
        readonly Dictionary<long, Texture2D> _pageTex = new Dictionary<long, Texture2D>();
        int _rotateTimer;                                                     // MAP_OBJ_ROTATE_TIMER
        int _doorMove = -1, _doorTarget;                                      // SCRIPT_MOVEMENT[11]: the door being turned
        readonly PieceMove[] _pieceMoves = new PieceMove[10];                 // SCRIPT_MOVEMENT[12..21]
        Door _boulder;                                                        // MAP_3D_OBJ[0] while the boulder is out
        StaticModel _chestModel; bool _chestTried;
        float _objAcc;

        void ClearMapObjects()
        {
            if (_objRoot != null) Destroy(_objRoot);
            _objRoot = null;
            foreach (var o in _objAssets) if (o != null) Destroy(o);
            _objAssets.Clear(); _pageTex.Clear(); _doors.Clear();
            _doorMove = -1; _rotateTimer = 0; _boulder = null; _mapVram = null;
            for (int i = 0; i < _pieceMoves.Length; i++) _pieceMoves[i] = null;
            ClearEvents();
        }

        Transform ObjRoot()
        {
            if (_objRoot == null) { _objRoot = new GameObject("MapObjects"); _objRoot.transform.SetParent(transform, false); }
            return _objRoot.transform;
        }

        bool ObjectsBusy()
        {
            if (_doorMove >= 0) return true;
            foreach (var m in _pieceMoves) if (m != null) return true;
            return false;
        }

        // ------------------------------------------------------------------ static TMD models

        static MmdModel ReadTmd(byte[] tmd)
        {
            var buf = new byte[tmd.Length + 4];
            buf[0] = 4; System.Array.Copy(tmd, 0, buf, 4, tmd.Length);
            return MmdModel.Parse(buf);
        }

        // one TMD object: one sub-mesh per (texture page, colour set, semi-transparent) its packets name. tex(face, layer) gives the picture
        // (layer 0 all colours, 1 the solid ones, 2 the STP ones), uv(face, corner) the corner's place in it.
        StaticModel BuildStatic(MmdModel.Part part, System.Func<MmdModel.Face, int, Texture2D> tex, System.Func<MmdModel.Face, int, Vector2> uv, List<Object> owner)
        {
            var keys = new List<long>(); var firsts = new List<MmdModel.Face>();
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<List<int>>();
            var packet = new List<int>(); var corner = new List<int>();
            foreach (var f in part.Faces)
            {
                long key = ((long)f.Tpage << 17) | ((long)f.Clut << 1) | (f.Abr >= 0 ? 1L : 0L);
                int k = keys.IndexOf(key);
                if (k < 0) { k = keys.Count; keys.Add(key); firsts.Add(f); tris.Add(new List<int>()); }
                int b = verts.Count;
                for (int i = 0; i < 3; i++)
                {
                    int v = f.V[i] * 3;
                    verts.Add(new Vector3(part.Verts[v], -part.Verts[v + 1], part.Verts[v + 2]) * MmdModel.Scale);
                    uvs.Add(uv(f, i));
                    packet.Add(f.Prim); corner.Add(f.V[i]);
                }
                // the PS1's corner order is Unity's front (as ArenaStage); a one-sided packet is not drawn from behind (OMOC08's toy boxes have
                // the "?" faces outside and plain white ones inside, the same quads turned the other way), a two-sided one gets both windings
                tris[k].Add(b); tris[k].Add(b + 1); tris[k].Add(b + 2);
                if (f.TwoSided) { tris[k].Add(b); tris[k].Add(b + 2); tris[k].Add(b + 1); }
            }
            var m = new StaticModel { Solid = new Mesh { name = "TMD", subMeshCount = keys.Count }, SolidMats = new Material[keys.Count], Packet = packet.ToArray(), Corner = corner.ToArray() };
            m.Solid.SetVertices(verts); m.Solid.SetUVs(0, uvs);
            for (int k = 0; k < keys.Count; k++) m.Solid.SetTriangles(tris[k], k);
            m.Solid.RecalculateNormals(); m.Solid.RecalculateBounds();
            owner.Add(m.Solid);
            var semi = new List<int>();
            for (int k = 0; k < keys.Count; k++)
            {
                bool s = firsts[k].Abr >= 0;
                m.SolidMats[k] = MmdModel.MakeMaterial(tex(firsts[k], s ? 1 : 0));
                if (m.SolidMats[k] != null) m.SolidMats[k].SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
                owner.Add(m.SolidMats[k]);
                if (s) semi.Add(k);
            }
            // the PS1 blends only the STP colours of a semi-transparent packet: those go on a second mesh with the packet's blend mode
            if (semi.Count > 0)
            {
                m.Blend = new Mesh { name = "TMD blend", subMeshCount = semi.Count };
                m.Blend.SetVertices(verts); m.Blend.SetUVs(0, uvs);
                m.BlendMats = new Material[semi.Count];
                for (int i = 0; i < semi.Count; i++)
                {
                    m.Blend.SetTriangles(tris[semi[i]], i);
                    m.BlendMats[i] = MmdModel.MakeBlendMaterial(tex(firsts[semi[i]], 2), firsts[semi[i]].Abr);
                    if (m.BlendMats[i] != null) m.BlendMats[i].SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
                    owner.Add(m.BlendMats[i]);
                }
                m.Blend.RecalculateNormals(); m.Blend.RecalculateBounds();
                owner.Add(m.Blend);
            }
            return m;
        }

        Transform MakeStaticObject(StaticModel m, string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = OtFlatCopy(m.Solid, m, go.transform);
            go.AddComponent<MeshRenderer>().sharedMaterials = m.SolidMats;
            if (m.Blend != null)
            {
                var b = new GameObject(name + " blend");
                b.transform.SetParent(go.transform, false);
                b.AddComponent<MeshFilter>().sharedMesh = OtFlatCopy(m.Blend, m, b.transform);
                b.AddComponent<MeshRenderer>().sharedMaterials = m.BlendMats;
            }
            return go.transform;
        }

        // ---- GsSortObject4J (drawObject): each packet goes into the ordering table as a whole, at the average depth of its corners (AVSZ3 / AVSZ4),
        // so a map piece (renderMapOverlays: one depth per piece) is wholly in front of a door face or wholly behind it. Our depth test is per
        // pixel, which let the painted pillars of OMOC05 cut steps into its slanted door faces. Before the main camera draws, every corner of a
        // packet slides along its line of sight to the packet's average view depth: the picture on the screen is the same, the depth is flat.
        sealed class OtFlat { public Transform T; public Mesh Mesh; public Vector3[] Src, Work; public int[][] Groups, Corners; }
        readonly List<OtFlat> _otFlat = new List<OtFlat>();
        bool _otHooked;

        Mesh OtFlatCopy(Mesh shared, StaticModel m, Transform t)
        {
            if (m.Packet == null) return shared;
            var mesh = Object.Instantiate(shared);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            _objAssets.Add(mesh);
            var groups = new Dictionary<int, List<int>>(); var corners = new Dictionary<int, List<int>>();
            for (int i = 0; i < m.Packet.Length; i++)
            {
                List<int> g, c;
                if (!groups.TryGetValue(m.Packet[i], out g)) { groups[m.Packet[i]] = g = new List<int>(); corners[m.Packet[i]] = new List<int>(); }
                g.Add(i);
                c = corners[m.Packet[i]];
                bool seen = false;
                foreach (int k in c) if (m.Corner[k] == m.Corner[i]) { seen = true; break; }
                if (!seen) c.Add(i);
            }
            var e = new OtFlat { T = t, Mesh = mesh, Src = shared.vertices, Groups = new int[groups.Count][], Corners = new int[groups.Count][] };
            e.Work = new Vector3[e.Src.Length];
            int n = 0;
            foreach (var kv in groups) { e.Groups[n] = kv.Value.ToArray(); e.Corners[n] = corners[kv.Key].ToArray(); n++; }
            _otFlat.Add(e);
            if (!_otHooked) { _otHooked = true; UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OtFlatten; }
            return mesh;
        }

        void OtFlatten(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || cam != _cam) return;
            Matrix4x4 view = cam.worldToCameraMatrix;
            for (int k = _otFlat.Count - 1; k >= 0; k--)
            {
                var e = _otFlat[k];
                if (e.T == null || e.Mesh == null) { _otFlat.RemoveAt(k); continue; }
                if (!e.T.gameObject.activeInHierarchy) continue;
                Matrix4x4 toView = view * e.T.localToWorldMatrix, back = toView.inverse;
                for (int g = 0; g < e.Groups.Length; g++)
                {
                    float avg = 0f;
                    foreach (int i in e.Corners[g]) avg += toView.MultiplyPoint3x4(e.Src[i]).z;
                    avg /= e.Corners[g].Length;
                    foreach (int i in e.Groups[g])
                    {
                        Vector3 v = toView.MultiplyPoint3x4(e.Src[i]);
                        // only in front of the camera (view z is negative there); a corner behind it keeps its place
                        if (v.z < -0.001f && avg < -0.001f) v *= avg / v.z;
                        e.Work[i] = back.MultiplyPoint3x4(v);
                    }
                }
                e.Mesh.vertices = e.Work;
            }
        }

        // projectPosition: RotMatrix (X, Y, Z) then the translation, in game units
        static void PlaceStatic(Transform t, int[] pos, int[] rot) { MmdModel.SetLocal(t, rot, pos); }

        // a 256 x 256 page of the map's video memory as a door packet reads it
        Texture2D PageTexture(MmdModel.Face f, int layer)
        {
            long key = ((long)f.Tpage << 20) | ((long)f.Clut << 2) | (long)layer;
            Texture2D t;
            if (_pageTex.TryGetValue(key, out t)) return t;
            t = new Texture2D(256, 256, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(_mapVram.PagePixels(f.Tpage, f.Clut, _map.GetPalette(_curPalette), layer));
            t.Apply();
            _pageTex[key] = t; _objAssets.Add(t);
            return t;
        }

        // ------------------------------------------------------------------ doors and machines

        // loadDoors (map.c calls it when MAP_ENTRIES[map].doorsId is not 0): DOOR_MAPDATA[doorsId - 1] (exe 0x80122E60, 54 bytes: s8 model[6],
        // s16 x[6], y[6], z[6], rotation[6]); models \DOOR\DOORnn.TMD, their pictures already in the map's images
        void LoadDoors(byte[] mapBytes)
        {
            if (_mapId < 0 || _mapId >= _num8.Count) return;
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            int doorsId = exe[(int)(0x801292D4 - load + 0x800) + 16 * _mapId + 13];
            if (doorsId == 0) return;
            if (_mapVram == null) _mapVram = MapOverlay.Parse(mapBytes, _num8[_mapId], _num4[_mapId]);
            int e = (int)(0x80122E60 - load + 0x800) + 54 * (doorsId - 1);
            System.Func<int, int> s16 = o => System.BitConverter.ToInt16(exe, o);
            var models = new Dictionary<int, StaticModel>();
            for (int slot = 0; slot < 6; slot++)
            {
                var d = new Door { Model = (sbyte)exe[e + slot] };
                d.Pos[0] = s16(e + 6 + 2 * slot); d.Pos[1] = s16(e + 18 + 2 * slot); d.Pos[2] = s16(e + 30 + 2 * slot);
                d.Rot[1] = d.Direction = s16(e + 42 + 2 * slot);
                _doors.Add(d);
                if (d.Model < 0) continue;
                StaticModel m;
                if (!models.TryGetValue(d.Model, out m))
                {
                    try
                    {
                        var model = ReadTmd(_disc.ReadFile("/DOOR/DOOR" + d.Model.ToString("D2") + ".TMD"));
                        m = model.Parts.Count > 0 ? BuildStatic(model.Parts[0], PageTexture, (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f), _objAssets) : null;
                    }
                    catch (System.Exception ex) { Debug.LogWarning("[objects] door model " + d.Model + ": " + ex.Message); m = null; }
                    models[d.Model] = m;
                }
                if (m == null) continue;
                d.T = MakeStaticObject(m, "Door " + d.Model, ObjRoot());
                if (d.Model == 10) d.Copy = MakeStaticObject(m, "Door " + d.Model + " (other half)", ObjRoot());
            }
            PlaceDoors();
        }

        // initializeTrainingPoop (map.c, only on map 0xA5, GOMI02): \ETCNA\TRY.TMD, the big poop, at (1922, 0, 503), no rotation, scale 0x1000; its picture is in the map's images
        void LoadTrainingPoop(byte[] mapBytes)
        {
            if (_mapId != 0xA5 || _mapId >= _num8.Count) return;
            try
            {
                if (_mapVram == null) _mapVram = MapOverlay.Parse(mapBytes, _num8[_mapId], _num4[_mapId]);
                var model = ReadTmd(_disc.ReadFile("/ETCNA/TRY.TMD"));
                var m = model.Parts.Count > 0 ? BuildStatic(model.Parts[0], PageTexture, (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f), _objAssets) : null;
                if (m == null) return;
                var d = new Door { Model = -2 };
                d.Pos[0] = 1922; d.Pos[1] = 0; d.Pos[2] = 503;
                d.T = MakeStaticObject(m, "Training poop", ObjRoot());
                _doors.Add(d);
                PlaceDoors();
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] training poop: " + ex.Message); }
        }

        // loadWarpCrystals (map.c calls it for maps 101-103, ICSA05-07): \ETCNA\WARP.TMD, its pictures in the map's images; object 0 at
        // (x, -188, z), object 1 hangs on object 0's coordinates at (0, -188, 0); no rotation, scale 0x1000 (WARP_CRYSTAL_SCALE), never moved
        // (renderWarpCrystals has no tick). The places are addWarpCrystal's in loadWarpCrystals; map 104's four are never reached (100 < map < 104).
        void LoadWarpCrystals(byte[] mapBytes)
        {
            if (_mapId <= 100 || _mapId >= 104 || _mapId >= _num8.Count) return;
            int[][] at = _mapId == 101 ? new[] { new[] { 2237, -1667 }, new[] { -2237, -1667 }, new[] { 2358, 419 }, new[] { -2358, 419 } }
                       : _mapId == 102 ? new[] { new[] { 0, 0 } }
                       : new[] { new[] { 2376, -127 }, new[] { 1566, -3358 } };
            try
            {
                if (_mapVram == null) _mapVram = MapOverlay.Parse(mapBytes, _num8[_mapId], _num4[_mapId]);
                var model = ReadTmd(_disc.ReadFile("/ETCNA/WARP.TMD"));
                if (model.Parts.Count < 2) return;
                System.Func<MmdModel.Face, int, Vector2> uv = (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f);
                var lower = BuildStatic(model.Parts[0], PageTexture, uv, _objAssets);
                var upper = BuildStatic(model.Parts[1], PageTexture, uv, _objAssets);
                foreach (var p in at)
                {
                    var t0 = MakeStaticObject(lower, "Warp crystal", ObjRoot());
                    PlaceStatic(t0, new[] { p[0], -188, p[1] }, new int[3]);
                    var t1 = MakeStaticObject(upper, "Warp crystal top", t0);
                    PlaceStatic(t1, new[] { 0, -188, 0 }, new int[3]);
                }
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] warp crystal: " + ex.Message); }
        }

        // renderDoors: model 10 is drawn twice, the second one mirrored and 680 units along Z
        void PlaceDoors()
        {
            foreach (var d in _doors)
            {
                if (d.T == null) continue;
                PlaceStatic(d.T, d.Pos, d.Rot);
                if (d.Copy != null)
                {
                    int[] p = { d.Pos[0], d.Pos[1], d.Pos[2] - (d.Rot[1] < 0 ? 1 : -1) * 680 }, r = { d.Rot[0], -d.Rot[1], d.Rot[2] };
                    PlaceStatic(d.Copy, p, r);
                }
            }
        }

        // renderDoors: a door is drawn only when isBoxOffScreen(translation, 700, 800) says some corner of its box (x and z +-700, y 0 and -800) is on
        // the screen (isOffScreen: within 160 x 120 of the picture's middle); model 10's other half goes with it. YAKA26's DOOR16 stands 225
        // units in front of the camera with every corner off the screen: the PS1 never draws it; ours drew it over half the dining room
        // (user's screenshot 2026-11-11). Once a frame, after the camera is placed.
        // with the mod 16:9 the picture is 240 x 16 / 9 PS1 pixels wide: doors in the new side parts are drawn too
        static float halfW { get { return ModSettings.Widescreen ? 120f * 16f / 9f : 160f; } }

        void CullDoors()
        {
            if (_doors.Count == 0 || _map == null || _h == 0f) return;
            float cx = _scrollPx.x - _map.PixelWidth * 0.5f, cy = _scrollPx.y - _map.PixelHeight * 0.5f;
            foreach (var d in _doors)
            {
                if (d.T == null || d.Model < 0) continue;
                bool on = !BoxOffScreen(d.Pos[0], d.Pos[1], d.Pos[2], 700, 800, cx, cy);
                if (d.T.gameObject.activeSelf != on) d.T.gameObject.SetActive(on);
                if (d.Copy != null && d.Copy.gameObject.activeSelf != on) d.Copy.gameObject.SetActive(on);
            }
        }

        static readonly int[,] DirectionFactors = { { -1, 1 }, { 1, 1 }, { -1, -1 }, { 1, -1 } };     // DIRECTION_FACTORS

        // isBoxOffScreen: the 8 corners through GsWSMATRIX (RTPS); 1 when none is on the screen
        bool BoxOffScreen(int x, int y, int z, int width, int height, float viewX, float viewY)
        {
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                {
                    Vector2 s = RtpsPoint(new Vector3(x + width * DirectionFactors[j, 0], y - i * height, z + width * DirectionFactors[j, 1]));
                    float sx = s.x - viewX, sy = s.y - viewY;
                    if (!(sx < -halfW || sx > halfW || sy < -120f || sy > 120f)) return false;     // isOffScreen(screen, 320, 240) == 0 (wider with the mod 16:9: halfW)
                }
            return true;
        }

        // RTPS as the GTE does it: x and y times H / SZ, where SZ below 0 counts as 0 and the quotient stops at 0x1FFFF (about 2), so a point near
        // or behind the camera lands at about twice its view x / y; the result is held within -1024..1023
        Vector2 RtpsPoint(Vector3 g)
        {
            Vector3 q = g - _vpG;
            float vx = Vector3.Dot(q, _rG), vy = Vector3.Dot(q, _dG), vz = Vector3.Dot(q, _fG);
            float k = vz <= 0f ? 0x1FFFF / 65536f : Mathf.Min(_h / vz, 0x1FFFF / 65536f);
            return new Vector2(Mathf.Clamp(vx * k, -1024f, 1023f), Mathf.Clamp(vy * k, -1024f, 1023f));
        }

        // tickRotateDoor: one game frame; true when done
        bool TickRotateDoor(int slot, int target)
        {
            if (slot < 0 || slot >= _doors.Count) return true;
            var d = _doors[slot];
            switch (d.Model)
            {
                case 0:
                    if (++_rotateTimer > 32) { _rotateTimer = 0; return true; }
                    d.Rot[1] -= target * 33;
                    break;
                case 9: case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x10: case 0x11: case 0x12: case 0x13:
                    if (++_rotateTimer > 32) { _rotateTimer = 0; d.Rot[1] = d.Direction; return true; }
                    d.Rot[1] += target * 33;
                    break;
                case 0x0E: case 0x0F:
                    if (++_rotateTimer > 32) { _rotateTimer = 0; return true; }
                    d.Rot[1] += target * 33;
                    break;
                case 5: case 0x1F: case 0x20:
                    if (++_rotateTimer > 9) { _rotateTimer = 0; return true; }
                    d.Pos[1] -= target * 200;
                    break;
                case 0x15: case 0x16: case 0x18: case 0x1B: case 0x1D:
                    if (++_rotateTimer > 9) { _rotateTimer = 0; return true; }
                    if (_mapId == 0x81) d.Pos[2] += 30; else d.Pos[0] += 30;
                    break;
                case 0x14: case 0x17: case 0x19: case 0x1C: case 0x1E:
                    if (++_rotateTimer > 9) { _rotateTimer = 0; return true; }
                    if (_mapId == 0x81) d.Pos[2] -= 30; else d.Pos[0] -= 30;
                    break;
                case 0x1A:
                    ++_rotateTimer;
                    d.Rot[0] -= target * 110;
                    if (d.Rot[0] < -0x400) d.Rot[0] = -0x400;
                    if (d.Rot[0] > 0) d.Rot[0] = 0;
                    if (_rotateTimer > 19) { _rotateTimer = 0; return true; }
                    break;
            }
            return false;       // any other model never finishes (as in the game)
        }

        // ------------------------------------------------------------------ the boulder

        // spawnBoulder: \ETCNA\IWA.TMD with \ETCNA\IWA1.TIM (VRAM 640,0, its own page), at (-300, -500, 2000), no rotation
        void SpawnBoulder()
        {
            if (_boulder != null && _boulder.T != null) Destroy(_boulder.T.gameObject);
            _boulder = null;
            try
            {
                var model = ReadTmd(_disc.ReadFile("/ETCNA/IWA.TMD"));
                TimImage tim;
                if (model.Parts.Count == 0 || !TimImage.TryParse(_disc.ReadFile("/ETCNA/IWA1.TIM"), 0, out tim)) return;
                Texture2D solid = tim.ToTextureLayer(0, 1), stp = tim.ToTextureLayer(0, 2), all = tim.ToTexture(0);
                _objAssets.Add(solid); _objAssets.Add(stp); _objAssets.Add(all);
                var m = BuildStatic(model.Parts[0], (f, layer) => layer == 0 ? all : layer == 1 ? solid : stp,
                                    (f, i) => new Vector2((f.U[i] + 0.5f) / tim.Width, 1f - (f.T[i] + 0.5f) / tim.Height), _objAssets);
                _boulder = new Door { Model = -2 };
                _boulder.Pos[0] = -300; _boulder.Pos[1] = -500; _boulder.Pos[2] = 2000;
                _boulder.T = MakeStaticObject(m, "Boulder", ObjRoot());
                PlaceStatic(_boulder.T, _boulder.Pos, _boulder.Rot);
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] boulder: " + ex.Message); }
        }

        // moveBoulder: the game adds the first number to Y (height) and the second to Z, and turns it 10 about X
        void MoveBoulder(int dy, int dz)
        {
            if (_boulder == null || _boulder.T == null) return;
            _boulder.Pos[1] += dy; _boulder.Pos[2] += dz; _boulder.Rot[0] -= 10;
            PlaceStatic(_boulder.T, _boulder.Pos, _boulder.Rot);
        }

        void RemoveBoulder()
        {
            if (_boulder != null && _boulder.T != null) Destroy(_boulder.T.gameObject);
            _boulder = null;
        }

        // ------------------------------------------------------------------ the chest model

        // initializeChest / renderChest: \ETCNA\TAKA.TMD object 0 is the box, object 1 the tray (its coordinates hang on the box's);
        // the picture is \ETCNA\TAKA.TIM (VRAM 960,384, in the page at 960,256: v counts from 128)
        StaticModel[] ChestModel()
        {
            if (_chestTried) return _chestModel != null ? new[] { _chestModel, _chestTray } : null;
            _chestTried = true;
            try
            {
                var model = ReadTmd(_disc.ReadFile("/ETCNA/TAKA.TMD"));
                TimImage tim;
                if (model.Parts.Count < 2 || !TimImage.TryParse(_disc.ReadFile("/ETCNA/TAKA.TIM"), 0, out tim)) return null;
                var keep = new List<Object>();      // kept for the whole game
                Texture2D all = tim.ToTexture(0), solid = tim.ToTextureLayer(0, 1), stp = tim.ToTextureLayer(0, 2);
                System.Func<MmdModel.Face, int, Texture2D> tex = (f, layer) => layer == 0 ? all : layer == 1 ? solid : stp;
                System.Func<MmdModel.Face, int, Vector2> uv = (f, i) => new Vector2((f.U[i] + 0.5f) / tim.Width, 1f - (f.T[i] - 128 + 0.5f) / tim.Height);
                _chestModel = BuildStatic(model.Parts[0], tex, uv, keep);
                _chestTray = BuildStatic(model.Parts[1], tex, uv, keep);
                return new[] { _chestModel, _chestTray };
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] chest: " + ex.Message); return null; }
        }
        StaticModel _chestTray;

        // ------------------------------------------------------------------ map pieces moved by scripts

        // where a point of the world is on the painted map (getDrawPosition): the inverse of PlaceOverlay
        Vector2 MapPixelOf(Vector3 world)
        {
            Transform c = _cam.transform;
            Vector3 l = world - c.position;
            float dz = Mathf.Max(0.01f, Vector3.Dot(l, c.forward));
            return new Vector2(Vector3.Dot(l, c.right) / dz * _h + _map.PixelWidth * 0.5f, -Vector3.Dot(l, c.up) / dz * _h + _map.PixelHeight * 0.5f);
        }

        // the same distance BuildOverlays gives a piece
        float PieceDepth(MapOverlay.Obj ob)
        {
            Vector3 world = new Vector3(ob.WX, ob.WY, ob.WZ);
            float z = Vector3.Dot(world - _vpG, _fG);
            int order = Mathf.Clamp((int)(z / 16f), 35, 4095);
            if (ob.WY == 10000) order = 4094;
            return Mathf.Min(order * 16f * Unit, 295f);
        }

        void ReplacePiece(int idx, float dm = -1f)
        {
            if (_overlay == null || _ovPieces == null || idx < 0 || idx >= _ovPieces.Length || _ovPieces[idx] == null) return;
            var inst = _overlay.Instances[idx];
            int f0 = inst.Frames[0], cur = f0;
            foreach (var a in _ovAnims) if (a.I == inst) { cur = inst.Frames[a.Cur]; break; }
            if (f0 < 0 || f0 >= _overlay.Objects.Length) return;
            if (cur < 0 || cur >= _overlay.Objects.Length) cur = f0;
            PlaceOverlay(_ovPieces[idx].transform, inst, _overlay.Objects[cur], dm >= 0f ? dm : PieceDepth(_overlay.Objects[f0]));
        }

        // spawnSpriteAtLocation: count pieces from 'first' are centred on the point (each by the size of its first shown frame) -> loadMapObjectPosition
        void PiecesAt(Vector3 world, int first, int count)
        {
            if (_overlay == null) return;
            Vector2 at = MapPixelOf(world);
            for (int i = 0; i < count; i++)
            {
                int idx = first + i;
                if (idx < 0 || idx >= _overlay.Instances.Length) continue;
                var inst = _overlay.Instances[idx];
                int j = 0;
                while (j < 7 && inst.Frames[j] == -2) j++;
                int o = inst.Frames[j];
                if (o < 0 || o >= _overlay.Objects.Length) continue;
                inst.X = (int)at.x - _overlay.Objects[o].W / 2;
                inst.Y = (int)at.y - _overlay.Objects[o].H / 2;
                ReplacePiece(idx);
            }
        }

        // tickMoveObjectTo: the step is (target - where it is) / steps, worked out on the first frame; it stops on the target
        bool TickMovePiece(PieceMove m)
        {
            if (_overlay == null || m.Piece < 0 || m.Piece >= _overlay.Instances.Length) return true;
            var inst = _overlay.Instances[m.Piece];
            if (!m.Started)
            {
                int steps = m.Steps != 0 ? m.Steps : 1;
                m.Dx = (short)((m.X - inst.X) / steps); m.Dy = (short)((m.Y - inst.Y) / steps);
                m.Started = true;
            }
            inst.X += m.Dx; inst.Y += m.Dy;
            if (m.Dx > 0) { if (inst.X >= m.X) inst.X = m.X; }
            else if (m.Dx < 0) { if (inst.X <= m.X) inst.X = m.X; }
            else inst.X = m.X;
            if (m.Dy > 0) { if (inst.Y >= m.Y) inst.Y = m.Y; }
            else if (m.Dy < 0) { if (inst.Y <= m.Y) inst.Y = m.Y; }
            else inst.Y = m.Y;
            ReplacePiece(m.Piece);
            return inst.X == m.X && inst.Y == m.Y;
        }

        // ------------------------------------------------------------------ script commands and the frame tick

        bool ExecObjectCmd(ScriptAction a)
        {
            switch (a.Cmd)
            {
                case 0x70: _doorMove = a.Actor; _doorTarget = a.Frames; return true;
                case 0x71:
                    if (a.Actor >= 0 && a.Actor < _pieceMoves.Length)
                        _pieceMoves[a.Actor] = new PieceMove { Piece = a.Target, Steps = a.Frames, X = (int)a.X, Y = (int)a.Z };
                    return true;
                case 0x76: SpawnBoulder(); return true;
                case 0x77: MoveBoulder((int)a.X, (int)a.Z); return true;
                case 0x78: RemoveBoulder(); return true;
                case 0x7D: PiecesAt(ToUnity(new Vector3(a.X, a.Y, a.Z)), a.Target, a.Frames); return true;
                case 0x7E:
                {
                    Transform t; DigimonAnimator an;
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        var node = an != null ? an.Node(a.Frames) : null;
                        PiecesAt(node != null ? node.position : t.position, a.Target, 1);
                    }
                    return true;
                }
            }
            return false;
        }

        // SCRIPT_MOVEMENT slots 11 (door) and 12-21 (pieces), once a game frame
        void TickMapObjects()
        {
            _objAcc += Time.deltaTime * 20f;
            while (_objAcc >= 1f)
            {
                _objAcc -= 1f;
                if (_doorMove >= 0 && TickRotateDoor(_doorMove, _doorTarget)) _doorMove = -1;
                for (int i = 0; i < _pieceMoves.Length; i++)
                    if (_pieceMoves[i] != null && TickMovePiece(_pieceMoves[i])) _pieceMoves[i] = null;
                TickEvents();
            }
            if (_objAcc > 3f) _objAcc = 0f;
            PlaceDoors();
        }
    }
}
