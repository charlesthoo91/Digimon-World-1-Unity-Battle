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
        sealed class StaticModel { public Mesh Solid, Blend; public Material[] SolidMats, BlendMats; }
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
                }
                tris[k].Add(b); tris[k].Add(b + 2); tris[k].Add(b + 1);
            }
            var m = new StaticModel { Solid = new Mesh { name = "TMD", subMeshCount = keys.Count }, SolidMats = new Material[keys.Count] };
            m.Solid.SetVertices(verts); m.Solid.SetUVs(0, uvs);
            for (int k = 0; k < keys.Count; k++) m.Solid.SetTriangles(tris[k], k);
            m.Solid.RecalculateNormals(); m.Solid.RecalculateBounds();
            owner.Add(m.Solid);
            var semi = new List<int>();
            for (int k = 0; k < keys.Count; k++)
            {
                bool s = firsts[k].Abr >= 0;
                m.SolidMats[k] = MmdModel.MakeMaterial(tex(firsts[k], s ? 1 : 0));
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
            go.AddComponent<MeshFilter>().sharedMesh = m.Solid;
            go.AddComponent<MeshRenderer>().sharedMaterials = m.SolidMats;
            if (m.Blend != null)
            {
                var b = new GameObject(name + " blend");
                b.transform.SetParent(go.transform, false);
                b.AddComponent<MeshFilter>().sharedMesh = m.Blend;
                b.AddComponent<MeshRenderer>().sharedMaterials = m.BlendMats;
            }
            return go.transform;
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
