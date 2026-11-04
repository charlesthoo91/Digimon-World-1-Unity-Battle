using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>Items lying on a map (script 0x74, up to 11) and treasure chests (0x75, up to 8), from main/item.c, tamer.c, map_object.c. The chest is ETCNA/TAKA.TMD (PlayableRoomObjects); plain boxes only if it cannot be read.</summary>
    public sealed partial class PlayableRoom
    {
        sealed class Ground { public int Item; public Vector2Int Tile; public Vector3 Pos; }
        sealed class Chest { public int Item, Trigger; public Vector2Int Tile, Reach; public bool Taken; public Transform Box, Tray; public Vector3 TrayHome, TrayDir; }
        readonly List<Ground> _ground = new List<Ground>();
        readonly List<Chest> _chests = new List<Chest>();
        readonly List<RectInt> _blocks = new List<RectInt>();     // tiles closed by chests and script command 0x7C
        string _boxText; int _boxItem = -1; bool _refused;

        void ClearWorldItems()
        {
            _ground.Clear(); _blocks.Clear();
            foreach (var c in _chests) if (c.Box != null) Destroy(c.Box.gameObject);
            _chests.Clear();
        }

        // spawnDroppedItems: an enemy's item lies exactly where it fell (height 0), on that tile, until picked up
        void SpawnDropped(int item, Vector3 at)
        {
            if (_ground.Count >= 11) return;
            _ground.Add(new Ground { Item = item, Tile = TileOf(at), Pos = new Vector3(at.x, 0f, at.z) });
        }

        void SpawnItem(int item, int tileX, int tileY)
        {
            if (_ground.Count >= 11) return;
            var t = new Vector2Int(tileX, tileY);
            // spawnItem: at the boy's height
            _ground.Add(new Ground { Item = item, Tile = t, Pos = new Vector3(((tileX - 50) * 100 + 50) * Unit, _player != null ? _player.position.y : 0f, ((50 - tileY) * 100 - 50) * Unit) });
        }

        void SpawnChest(int item, int x, int z, int rotation, int trigger)
        {
            if (_chests.Count >= 8) return;
            Vector2Int tile = TileOf(new Vector3(x * Unit, 0f, z * Unit));
            Vector3 at = new Vector3((tile.x * 100 - 4950) * Unit, 0f, (4950 - tile.y * 100) * Unit);
            _blocks.Add(new RectInt(tile.x - 2, tile.y - 2, 5, 5));
            Vector2Int reach = tile;
            if (rotation == 0) reach.y += 4; else if (rotation == 0x800) reach.y -= 4;
            if (rotation == 0x400) reach.x -= 4; else if (rotation == 0xC00) reach.x += 4;

            float a = rotation * 2f * Mathf.PI / 4096f;
            Vector3 front = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));     // the tray slides 160 units along -Z of the chest
            var parts = ChestModel();
            if (parts != null)
            {
                // spawnChest: the box at the middle of its tile, height 0, rotation (0, rotation, 0); the tray at (0, 0, 0) on the box, (0, 0, -160) once taken
                var boxT = MakeStaticObject(parts[0], "Chest", transform);
                PlaceStatic(boxT, new[] { tile.x * 100 - 4950, 0, 4950 - tile.y * 100 }, new[] { 0, rotation, 0 });
                var trayT = MakeStaticObject(parts[1], "Chest tray", boxT);
                var ch = new Chest { Item = item, Trigger = trigger, Tile = tile, Reach = reach, Box = boxT, Tray = trayT, TrayHome = boxT.position, TrayDir = -front };
                ch.Taken = _state.GetFlag(trigger);
                trayT.position = ch.TrayHome + (ch.Taken ? ch.TrayDir * 1.6f : Vector3.zero);
                _chests.Add(ch);
                return;
            }
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(box.GetComponent<Collider>());
            box.transform.SetParent(transform, false);
            box.transform.position = at + Vector3.up * 0.6f; box.transform.localScale = new Vector3(2.4f, 1.2f, 2.4f);
            box.transform.rotation = Quaternion.Euler(0f, rotation * 360f / 4096f, 0f);
            box.GetComponent<MeshRenderer>().material.color = new Color(0.55f, 0.36f, 0.12f);
            var tray = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(tray.GetComponent<Collider>());
            tray.transform.SetParent(transform, false);
            tray.transform.localScale = new Vector3(2.2f, 0.5f, 2.2f); tray.transform.rotation = box.transform.rotation;
            tray.GetComponent<MeshRenderer>().material.color = new Color(0.8f, 0.62f, 0.2f);
            tray.transform.parent = box.transform;
            var c = new Chest { Item = item, Trigger = trigger, Tile = tile, Reach = reach, Box = box.transform, Tray = tray.transform, TrayHome = at + Vector3.up * 1.0f, TrayDir = -front };
            c.Taken = _state.GetFlag(trigger);
            tray.transform.position = c.TrayHome + (c.Taken ? c.TrayDir * 1.6f : Vector3.zero);
            _chests.Add(c);
        }

        bool TileBlocked(Vector3 u)
        {
            if (_blocks.Count == 0) return false;
            var t = TileOf(u);
            foreach (var r in _blocks) if (r.Contains(t)) return true;
            return false;
        }

        // checkItemPickup: within one tile of the boy
        void CheckPickup()
        {
            var me = TileOf(_player.position);
            bool near = false;
            foreach (var g in _ground)
                if (Mathf.Abs(g.Tile.x - me.x) <= 1 && Mathf.Abs(g.Tile.y - me.y) <= 1)
                {
                    near = true;
                    if (_refused) return;       // HAS_PICKED_UP_ITEM: a full bag asks once, until the boy has walked away
                    Run(Pickup(g), true);
                    return;
                }
            if (!near) _refused = false;
        }

        PickupBox _pickup;

        PickupBox PickupView()
        {
            if (_pickup == null && _disc != null && Battle.StatFont != null) { _pickup = PickupBox.Load(_disc, Battle.StatFont); if (_pickup != null) _pickup.Sound = Snd; }
            return _pickup;
        }

        // createAnimatedUIBox(1, 0, 2, (-130, 42, 262, 59), the boy's 10 x 10 square)
        RectInt BoySquare()
        {
            Vector2 p = Ps1Point(_player.position + Vector3.up * 0.8f);
            return new RectInt((int)p.x - 5, (int)p.y - 5, 10, 10);
        }

        void OpenPickupBox(PickupBox v)
        {
            if (v == null) return;
            v.Open(v.Box, new RectInt(-130, 42, 262, 59), BoySquare(), 0x20 | 0x80);
        }

        // TAKE_ITEM_FRAME_COUNTER: Cross counts once more than 'after' game frames have passed since the counter started
        IEnumerator WaitCross(int after)
        {
            for (float t = 0f; t <= after / 20f; t += Time.deltaTime) yield return null;
            while (!ActionPressed()) yield return null;
        }

        // tamerTickPickupItem: animation 12, the box grows out of the boy; Cross (sound 3): the item goes in the bag (sound 7) and the box
        // shrinks back, or "I can't hold anymore." (state 1) and Cross again
        IEnumerator Pickup(Ground g)
        {
            if (_anim != null && _anim.HasSlot(12)) _anim.SetSlot(12, true);
            var v = PickupView();
            _boxItem = g.Item;
            if (v != null) { v.SetText(_state.ItemNameOf(g.Item), 0); OpenPickupBox(v); } else _boxText = _state.ItemNameOf(g.Item);
            yield return WaitCross(4);
            Snd(3);
            if (_state.AddItem(g.Item, 1))
            {
                Snd(7);
                _ground.Remove(g);
            }
            else
            {
                string full = DigimonStatsCatalog.ExeText(_disc, 0x80122D68);    // "I can't hold anymore."
                if (v != null) v.SetText(full, 1); else _boxText = full;
                yield return null;
                yield return WaitCross(4);
                Snd(3);
                _refused = true;
            }
            // removeAnimatedUIBox(1, the boy's square)
            if (v != null) { v.Box.Origin = BoySquare(); v.Close(v.Box); }
            _boxText = null; _boxItem = -1;
            if (_anim != null) _anim.SetSlot(IdleSlot);
        }

        bool TryChest()
        {
            var me = TileOf(_player.position);
            foreach (var c in _chests)
                if (Mathf.Abs(c.Reach.x - me.x) <= 1 && Mathf.Abs(c.Reach.y - me.y) <= 1) { Run(OpenChest(c), true); return true; }
            return false;
        }

        // tamerTickTakeChest: the tray slides out 20 units a frame, then the box grows out of the boy. A taken chest: "Hey! It's empty!" (state 2).
        // Cross: a full bag says "I can't hold anymore." (state 1) while the tray slides back; otherwise the chest's flag is set. Cross again: the box
        // shrinks back and the item goes in the bag. (The game reads the held button for these two presses; ours wants two presses.)
        IEnumerator OpenChest(Chest c)
        {
            var v = PickupView();
            if (c.Taken)
            {
                string empty = DigimonStatsCatalog.ExeText(_disc, 0x80122D80);    // "Hey! It's empty!"
                if (v != null) { v.SetText(empty, 2); OpenPickupBox(v); } else _boxText = empty;
                yield return WaitCross(5);
                if (v != null) { v.Box.Origin = BoySquare(); v.Close(v.Box); }
                _boxText = null;
                yield break;
            }
            for (float t = 0f; t < 0.4f; t += Time.deltaTime) { c.Tray.position = c.TrayHome + c.TrayDir * (t / 0.4f * 1.6f); yield return null; }
            c.Tray.position = c.TrayHome + c.TrayDir * 1.6f;
            _boxItem = c.Item;
            if (v != null) { v.SetText(_state.ItemNameOf(c.Item), 0); OpenPickupBox(v); } else _boxText = _state.ItemNameOf(c.Item);
            yield return WaitCross(5);
            bool fits = _state.CanAddItem(c.Item);
            if (!fits)
            {
                string full = DigimonStatsCatalog.ExeText(_disc, 0x80122D68);
                if (v != null) v.SetText(full, 1); else _boxText = full;
                for (float t = 0.4f; t > 0f; t -= Time.deltaTime) { c.Tray.position = c.TrayHome + c.TrayDir * (t / 0.4f * 1.6f); yield return null; }
                c.Tray.position = c.TrayHome;
            }
            else _state.SetFlag(c.Trigger, true);
            yield return null;
            yield return WaitCross(5);
            if (v != null) { v.Box.Origin = BoySquare(); v.Close(v.Box); }
            if (fits && _state.AddItem(c.Item, 1)) { Snd(7); c.Taken = true; }
            _boxText = null; _boxItem = -1;
        }

        Material _itemMat; Mesh _itemQuad; MaterialPropertyBlock _itemMpb;

        // renderOverworldItem: the item picture centred on its ground point, VIEWPORT_DISTANCE x 128 / depth pixels wide, i.e. 128 game units,
        // sorted by its depth in the ordering table like everything else, so the boy or a Digimon in front of it covers it (drawn in the 3D view)
        void DrawGroundItems()
        {
            // part of the 3D picture: menus lie over it, they do not hide it
            if (_cam == null || _menus == null || _menus.Icons == null || (_ground.Count == 0 && _heldItem < 0)) return;
            if (_itemMat == null)
            {
                var sh = Shader.Find("DW1Remake/Effect");
                if (sh == null) return;
                _itemMat = new Material(sh);
                _itemMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _itemMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _itemMat.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add);
                _itemMat.renderQueue = 3001;      // after the drop shadows
                _itemQuad = new Mesh();
                _itemQuad.vertices = new[] { new Vector3(-.5f, .5f, 0), new Vector3(.5f, .5f, 0), new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0) };
                _itemQuad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
                _itemQuad.triangles = new[] { 0, 1, 2, 2, 1, 3 };
                _itemQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
                _itemMpb = new MaterialPropertyBlock();
            }
            var icons = _menus.Icons;
            float size = 128 * Unit;
            foreach (var g in _ground)
            {
                Rect r = icons.Uv(g.Item);
                // the shader counts v from the top
                _itemMpb.SetTexture("_MainTex", icons.Sheet);
                _itemMpb.SetVector("_Rect", new Vector4(r.x, 1f - r.y - r.height, r.width, r.height));
                _itemMpb.SetColor("_Color", Color.white);
                Graphics.DrawMesh(_itemQuad, Matrix4x4.TRS(g.Pos, _cam.transform.rotation, new Vector3(size, size, 1f)), _itemMat, 0, null, 0, _itemMpb);
            }
            // renderFeedingItem: the item the boy holds out, at his bone 9 (the hand), drawn as an item on the ground is
            if (_heldItem >= 0 && _anim != null)
            {
                Transform hand = _anim.Node(9);
                Vector3 at = hand != null ? hand.position : _player.position + Vector3.up;
                Rect r = icons.Uv(_heldItem);
                _itemMpb.SetTexture("_MainTex", icons.Sheet);
                _itemMpb.SetVector("_Rect", new Vector4(r.x, 1f - r.y - r.height, r.width, r.height));
                _itemMpb.SetColor("_Color", Color.white);
                Graphics.DrawMesh(_itemQuad, Matrix4x4.TRS(at, _cam.transform.rotation, new Vector3(size, size, 1f)), _itemMat, 0, null, 0, _itemMpb);
            }
        }

        void DrawWorldItems()
        {
            if (_pickup != null) _pickup.Draw();
            if (_boxText == null) return;
            UiKit.Begin();
            var box = new Rect(330, 760, 760, 150);
            UiKit.Box(box, UiKit.DialogFill);
            UiKit.Text(new Rect(box.x + 40, box.y + 30, box.width - 80, 90), _boxText, 48, Color.white, TextAnchor.MiddleCenter);
            UiKit.End();
        }
    }
}
