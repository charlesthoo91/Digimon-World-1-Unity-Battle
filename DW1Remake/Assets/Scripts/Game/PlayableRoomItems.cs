using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>Items lying on a map (script 0x74, up to 11) and treasure chests (0x75, up to 8), from main/item.c, tamer.c, map_object.c. The chest box is ours: ETCNA/TAKA.TMD is not read yet.</summary>
    public sealed partial class PlayableRoom
    {
        sealed class Ground { public int Item; public Vector2Int Tile; public Vector3 Pos; }
        sealed class Chest { public int Item, Trigger; public Vector2Int Tile, Reach; public bool Taken; public Transform Box, Tray; public Vector3 TrayHome, TrayDir; }
        readonly List<Ground> _ground = new List<Ground>();
        readonly List<Chest> _chests = new List<Chest>();
        readonly List<RectInt> _blocks = new List<RectInt>();     // tiles closed by chests and script command 0x7C
        string _boxText; int _boxItem = -1;

        void ClearWorldItems()
        {
            _ground.Clear(); _blocks.Clear();
            foreach (var c in _chests) if (c.Box != null) Destroy(c.Box.gameObject);
            _chests.Clear();
        }

        void SpawnItem(int item, int tileX, int tileY)
        {
            if (_ground.Count >= 11) return;
            var t = new Vector2Int(tileX, tileY);
            _ground.Add(new Ground { Item = item, Tile = t, Pos = new Vector3(((tileX - 50) * 100 + 50) * Unit, 0f, ((50 - tileY) * 100 - 50) * Unit) });
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
            foreach (var g in _ground)
                if (Mathf.Abs(g.Tile.x - me.x) <= 1 && Mathf.Abs(g.Tile.y - me.y) <= 1) { Run(Pickup(g), true); return; }
        }

        IEnumerator WaitAction()
        {
            yield return new WaitForSeconds(0.25f);
            while (!ActionPressed()) yield return null;
            if (_audio != null) _audio.Play(0, 3);
        }

        // tamerTickPickupItem
        IEnumerator Pickup(Ground g)
        {
            if (_anim != null && _anim.HasSlot(12)) _anim.SetSlot(12, true);
            _boxItem = g.Item; _boxText = _state.ItemNameOf(g.Item);
            yield return WaitAction();
            if (_state.AddItem(g.Item, 1))
            {
                if (_audio != null) _audio.Play(0, 7);
                _ground.Remove(g);
            }
            else
            {
                _boxText = DigimonStatsCatalog.ExeText(_disc, 0x80122D68);    // "I can't hold anymore."
                yield return WaitAction();
                _ground.Remove(g);    // the game keeps it only until the boy has left its tile; it is not picked up again at once
                _ground.Add(g);
                yield return new WaitForSeconds(0.2f);
            }
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

        // tamerTickTakeChest: the tray slides out 20 units a frame
        IEnumerator OpenChest(Chest c)
        {
            if (c.Taken)
            {
                _boxText = DigimonStatsCatalog.ExeText(_disc, 0x80122D80);    // "Hey! It's empty!"
                yield return WaitAction();
                _boxText = null;
                yield break;
            }
            for (float t = 0f; t < 0.4f; t += Time.deltaTime) { c.Tray.position = c.TrayHome + c.TrayDir * (t / 0.4f * 1.6f); yield return null; }
            c.Tray.position = c.TrayHome + c.TrayDir * 1.6f;
            _boxItem = c.Item; _boxText = _state.ItemNameOf(c.Item);
            yield return WaitAction();
            if (_state.AddItem(c.Item, 1))
            {
                if (_audio != null) _audio.Play(0, 7);
                _state.SetFlag(c.Trigger, true);
                c.Taken = true;
            }
            else
            {
                _boxText = DigimonStatsCatalog.ExeText(_disc, 0x80122D68);
                for (float t = 0.4f; t > 0f; t -= Time.deltaTime) { c.Tray.position = c.TrayHome + c.TrayDir * (t / 0.4f * 1.6f); yield return null; }
                c.Tray.position = c.TrayHome;
                yield return WaitAction();
            }
            _boxText = null; _boxItem = -1;
        }

        void DrawWorldItems()
        {
            if (_cam == null || _menus == null || _menus.Icons == null || _menus.Active) return;
            var icons = _menus.Icons;
            foreach (var g in _ground)
            {
                Vector3 s = _cam.WorldToScreenPoint(g.Pos + Vector3.up * 0.4f);
                if (s.z <= 0f) continue;
                Vector3 up = _cam.WorldToScreenPoint(g.Pos + Vector3.up * 1.2f);
                float size = Mathf.Abs(up.y - s.y);
                GUI.DrawTextureWithTexCoords(new Rect(s.x - size / 2f, Screen.height - s.y - size / 2f, size, size), icons.Sheet, icons.Uv(g.Item));
            }
            if (_boxText == null) return;
            UiKit.Begin();
            var box = new Rect(330, 760, 760, 150);
            UiKit.Box(box, UiKit.PanelFill);
            UiKit.Text(new Rect(box.x + 40, box.y + 30, box.width - 80, 90), _boxText, 48, Color.white, TextAnchor.MiddleCenter);
            UiKit.End();
        }
    }
}
