using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Script built-ins that talk through script 0's text tables (showMapHeadTextbox / showMapheadSelection, shop/script_menu.c):
    /// 0x0A Birdramon transport (tickBirdraTransport). The texts come from DG.SCN entry 0; the tables from the exe.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        bool _routineOn;

        // the list box of a routine: rows, which ones can be picked, the cursor, and the Bits box beside it
        sealed class RoutineList
        {
            public readonly List<string> Names = new List<string>();
            public readonly List<string> Values = new List<string>();
            public readonly List<bool> Usable = new List<bool>();
            public int Cursor;
            public bool ShowBits;
            public string Head;       // a label line over the rows
        }
        RoutineList _list;

        // colours: keep the text's colour codes (for the dialogue box, which draws them with the game font)
        string HeadText(int section, int idx, bool colours = false) { return ScriptRunner.MapHeadText(_scripts, _state, section, idx, _state.Vars[0xFE], colours) ?? ""; }

        List<ItemEntry> _itemCatalog;
        ItemMenuView _itemBox;

        // openDiscardItem (built-in 0, ITEM_MENU_TYPE 5) with tickItemMenu and tickSingleCardShop. Picked: variable 254 = the item, flag 3 off.
        // Circle: only while flag 3 is on (the script sets it first): 254 = 0xFF, flag 3 off. Nothing that can be handed over: 254 = 0xFF, flag 3 on.
        // Not ported: R1 + up/down (to the first / last row) and Start (the item's description box).
        IEnumerator DiscardItem()
        {
            _routineOn = true;
            if (_itemBox == null && _itemCatalog != null)
            {
                _itemBox = ItemMenuView.Load(_disc, Battle.StatFont, _menus != null ? _menus.Icons : null, _itemCatalog, HeadText(0x4D8, 0));
                if (_itemBox != null) _itemBox.Sound = Snd;
            }
            var v = _itemBox;
            if (v == null || !v.Fill(_state))
            {
                _state.SetFlag(3, true); _state.Vars[0xFE] = 0xFF;
                _routineOn = false; AdvanceScript(); yield break;
            }
            // setupBoxOrigin(0xFD): the box grows out of a 10 x 10 square on the boy (ours: his middle; getEntityScreenPos is not decompiled)
            Vector2 boy = Ps1Point(_player.position + Vector3.up * 0.8f);
            v.Open(v.List, new RectInt(-0x24, -0x62, 0xA6, 0x81), new RectInt((int)boy.x - 5, (int)boy.y - 5, 10, 10), 0xF1);
            bool done = false;
            while (!done)
            {
                yield return null;
                if (v.Confirm.State == 1)
                {
                    if (GameMenus.CancelKey()) { v.Close(v.Confirm); Snd(4); continue; }
                    int side = GameMenus.SideStep();
                    if (side < 0) { v.Pick = 0; Snd(2); continue; }
                    if (side > 0) { v.Pick = 1; Snd(2); continue; }
                    if (ActionPressed())
                    {
                        v.Close(v.Confirm);
                        if (v.Pick == 0) { _state.Vars[0xFE] = (byte)v.Types[v.Row]; _state.SetFlag(3, false); Snd(3); done = true; }
                        else Snd(4);
                    }
                    continue;
                }
                if (v.Confirm.State != 0 || v.List.State != 1) continue;
                if (ActionPressed()) { v.Ask(); continue; }
                if (GameMenus.CancelKey())
                {
                    if (_state.GetFlag(3)) { _state.Vars[0xFE] = 0xFF; _state.SetFlag(3, false); done = true; }
                    Snd(4);
                    continue;
                }
                int step = MenuStep();
                if (step < 0) v.Up(); else if (step > 0) v.Down();
            }
            v.Close(v.List);
            _routineOn = false;
            AdvanceScript();        // the script goes on while the box shrinks away
        }

        // a world point in PS1 screen pixels from the middle of the picture
        Vector2 Ps1Point(Vector3 world)
        {
            if (_cam == null) return Vector2.zero;
            Vector3 sp = _cam.WorldToScreenPoint(world); Rect pic = UiKit.Picture;
            return new Vector2((sp.x - pic.x) / pic.width * 320f - 160f, (Screen.height - sp.y - pic.y) / pic.height * 240f - 120f);
        }

        // showMapHeadTextbox with SCRIPT_TEXTBOX_MODE 1: the owner (variable 254) says it; Cross goes on
        IEnumerator HeadSay(int section, int idx, int speaker = -1)
        {
            _line = HeadText(section, idx, true); _speaker = speaker < 0 ? _state.Vars[0xFE] : speaker; _choiceOpts = null;
            yield return null;
            while (!(DialogueReady && ActionPressed())) yield return null;
            _line = null;
            yield return null;
        }

        // showMapheadSelection with SCRIPT_TEXTBOX_MODE 2: the text's last 'count' lines are the choices; the state goes to next + choice
        IEnumerator HeadAsk(int section, int idx, int count, System.Action<int> answer)
        {
            var lines = new List<string>(HeadText(section, idx, true).Split('\n'));
            int q = Mathf.Max(0, lines.Count - count);
            _line = string.Join("\n", lines.GetRange(0, q).ToArray());
            _choiceOpts = lines.GetRange(q, lines.Count - q); _choiceSel = 0; _speaker = _state.Vars[0xFE];
            yield return null;
            while (true)
            {
                int ms = MenuStep();
                if (ms != 0) _choiceSel = (_choiceSel + ms + _choiceOpts.Count) % _choiceOpts.Count;
                if (ActionPressed()) break;
                yield return null;
            }
            int pick = _choiceSel;
            _line = null; _choiceOpts = null;
            answer(pick);
            yield return null;
        }

        // ------------------------------------------------------------------ 0x0A Birdramon transport

        // BIRDRA_TRANSPORT_TARGETS (exe 0x8013024C, 6 x 8 bytes): map, exit, flag that opens it, cost in Bits
        const uint BirdraTargets = 0x8013024C, MapNamePtr = 0x801291BC, MapEntries = 0x801292D4;

        // tickBirdraTransport (states 0-6) with birdraFillTargetList, createBirdraTransportMenu and tickBirdraTransportMenu
        IEnumerator BirdraTransport()
        {
            _routineOn = true;
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            System.Func<uint, int> at = a => (int)(a - load + 0x800);
            // birdraFillTargetList: the destinations whose flag is on; those the Bits pay for get bit 0x80 (can be picked)
            var rows = new List<int>();
            var list = new RoutineList { ShowBits = true };
            for (int i = 0; i < 6; i++)
            {
                int o = at(BirdraTargets) + 8 * i;
                int map = exe[o], flag = System.BitConverter.ToUInt16(exe, o + 2), cost = System.BitConverter.ToInt32(exe, o + 4);
                if (!_state.GetFlag(flag)) continue;
                int name = exe[at(MapEntries) + 16 * map + 15];         // MAP_ENTRIES[map].loadingName -> MAP_NAME_PTR
                uint ptr = System.BitConverter.ToUInt32(exe, at(MapNamePtr) + 4 * name);
                rows.Add(i);
                list.Names.Add(Exe(ptr).Trim());
                list.Values.Add(cost.ToString());
                list.Usable.Add(cost <= _state.Money);
            }
            yield return HeadSay(0x4D6, 4);                 // state 0: "Where should I take you?"
            int state = 3, chosen = -1;
            while (state != 2)
            {
                switch (state)
                {
                    case 3:     // the list (createShopBitsBox(1), createBirdraTransportMenu); Cross on one the Bits pay for, Circle leaves
                        _list = list;
                        yield return null;
                        while (true)
                        {
                            int ms = MenuStep();
                            if (ms < 0 && list.Cursor > 0) { list.Cursor--; Snd(2); }
                            else if (ms > 0 && list.Cursor < list.Names.Count - 1) { list.Cursor++; Snd(2); }
                            if (GameMenus.CancelKey()) { Snd(4); state = 6; break; }
                            if (ActionPressed() && list.Names.Count > 0)
                            {
                                if (list.Usable[list.Cursor]) { Snd(3); chosen = rows[list.Cursor]; state = 7; break; }
                                Snd(0xB);
                            }
                            yield return null;
                        }
                        if (state == 7) { yield return HeadSay(0x4D6, 6); state = 4; }     // "Are you sure? Once you go, I can't take you back."
                        break;
                    case 4:     // "I know" / "Cancel"
                    {
                        int pick = 0;
                        yield return HeadAsk(0x4D6, 7, 2, a => pick = a);
                        state = 5 + pick;
                        break;
                    }
                    case 5:     // variables 247/248 = map/exit, Bits paid, and script 0 section 0x4E3 takes over (the flight)
                    {
                        int o = at(BirdraTargets) + 8 * chosen;
                        _list = null;
                        _state.Vars[0xF7] = exe[o]; _state.Vars[0xF8] = exe[o + 1];
                        _state.Money -= System.BitConverter.ToInt32(exe, o + 4);
                        _routineOn = false;
                        StartDgSection(0x4E3);
                        yield break;
                    }
                    case 6:     // "I think that going yourself may be the best way."
                        _list = null;
                        yield return HeadSay(0x4D6, 5);
                        state = 2;
                        break;
                }
            }
            _list = null; _routineOn = false;
            AdvanceScript();
        }

        // ------------------------------------------------------------------ 0x0B Item Keeper

        // the two lists of createItemKeepWindow: left = stored items (itemKeeperFillItemList: every item with a count), right = the bag's
        // slots (empty ones too). 'Ok' = bit 0x80 of the count: the item can move out of that list.
        sealed class KeeperState
        {
            public readonly List<int> LeftItem = new List<int>(), RightItem = new List<int>();
            public readonly List<bool> LeftOk = new List<bool>(), RightOk = new List<bool>();
            public int Side, LeftCur, RightCur, LeftTop, RightTop;
            public string LeftLabel, RightLabel;
        }
        KeeperState _keeper;
        const int KeeperRows = 6;     // registerTextbox(1, 9, 6, ...): six rows show

        void KeeperFill(KeeperState k)
        {
            var bag = _state.Bag; var stored = _state.Stored;
            bool free = _state.ItemSlots == 0 || _state.BagKinds() < _state.ItemSlots;
            k.LeftItem.Clear(); k.LeftOk.Clear(); k.RightItem.Clear(); k.RightOk.Clear();
            for (int j = 0; j < 0x80; j++)
            {
                if (stored[j] == 0) continue;
                k.LeftItem.Add(j);
                k.LeftOk.Add(free || (bag[j] != 0 && bag[j] != 0x63));
            }
            for (int j = 0; j < bag.Length; j++)
                if (bag[j] != 0) { k.RightItem.Add(j); k.RightOk.Add(stored[j] != 0x63); }
            int slots = Mathf.Max(_state.ItemSlots, k.RightItem.Count);
            while (k.RightItem.Count < slots) { k.RightItem.Add(0xFF); k.RightOk.Add(false); }
        }

        // keeperScrollLeftToItem / keeperScrollRightToItem: the cursor follows the moved item; a list that lost it keeps its place
        static void KeeperFollow(List<int> items, int item, ref int cur, ref int top)
        {
            int i = items.IndexOf(item);
            if (items.Count == 0) { cur = top = 0; return; }
            if (i < 0) i = Mathf.Min(top + cur, items.Count - 1);
            if (i >= top && i < top + KeeperRows) { cur = i - top; return; }
            if (i - KeeperRows < 0) { top = 0; cur = i; } else { top = i - KeeperRows + 1; cur = KeeperRows - 1; }
        }

        // keeperMoveOne / keeperMoveTen / keeperMoveStack: how = 1, 10 or 0 (all that fits under 99)
        void KeeperMove(KeeperState k, int how)
        {
            var bag = _state.Bag; var stored = _state.Stored;
            bool left = k.Side == 0;
            var items = left ? k.LeftItem : k.RightItem; var ok = left ? k.LeftOk : k.RightOk;
            int row = left ? k.LeftTop + k.LeftCur : k.RightTop + k.RightCur;
            if (row >= items.Count) return;
            int item = items[row];
            if (item == 0xFF || !ok[row]) return;
            int have = left ? stored[item] : bag[item], there = left ? bag[item] : stored[item], n;
            if (how == 1) { if (left ? there >= 0x63 : have == 0) return; n = 1; }
            else if (how == 10) { if (have < 10 || there >= 0x5A) return; n = 10; }
            else n = Mathf.Min(have, 0x63 - there);
            if (left) { if (!_state.AddItem(item, n)) return; stored[item] = (byte)(stored[item] - n); }
            else { _state.RemoveItem(item, n); stored[item] = (byte)(stored[item] + n); }
            KeeperFill(k);
            KeeperFollow(k.LeftItem, item, ref k.LeftCur, ref k.LeftTop);
            KeeperFollow(k.RightItem, item, ref k.RightCur, ref k.RightTop);
        }

        // tickItemKeeper (states 0-6) with tickItemKeeperWindow
        IEnumerator ItemKeeper()
        {
            _routineOn = true;
            yield return HeadSay(0x4D5, 0);                 // "Do you have any business at the Item Keeper?"
            int pick = 0;
            yield return HeadAsk(0x4D5, 1, 2, a => pick = a);     // Yes / No
            if (pick == 0)
            {
                var k = new KeeperState { LeftLabel = HeadText(0x4D5, 4), RightLabel = HeadText(0x4D5, 5) };
                KeeperFill(k);
                _keeper = k;
                _line = HeadText(0x4D5, 3); _speaker = _state.Vars[0xFE];      // the help line stays up while the lists are open
                yield return null;
                while (true)
                {
                    var items = k.Side == 0 ? k.LeftItem : k.RightItem;
                    if (GameMenus.TriangleOnly()) { Snd(4); break; }
                    int side = GameMenus.SideStep();
                    if (side < 0) { k.Side = 0; Snd(2); }
                    else if (side > 0) { k.Side = 1; Snd(2); }
                    else if (items.Count != 0)
                    {
                        if (ActionPressed()) { KeeperMove(k, 1); Snd(3); }
                        else if (GameMenus.CircleKey()) { KeeperMove(k, 10); Snd(3); }
                        else if (GameMenus.SquareKey()) { KeeperMove(k, 0); Snd(3); }
                        else
                        {
                            int ms = MenuStep();
                            if (ms != 0)
                            {
                                if (k.Side == 0) KeeperStep(ref k.LeftCur, ref k.LeftTop, k.LeftItem.Count, ms);
                                else KeeperStep(ref k.RightCur, ref k.RightTop, k.RightItem.Count, ms);
                            }
                        }
                    }
                    yield return null;
                }
                _keeper = null; _line = null;
                yield return null;
            }
            yield return HeadSay(0x4D5, 2);                 // "Come to this shop when you need it."
            _routineOn = false;
            AdvanceScript();
        }

        // itemMenuCursorUp / itemMenuCursorDown: the cursor moves in the six rows, then the list scrolls
        void KeeperStep(ref int cur, ref int top, int count, int dir)
        {
            int at = top + cur + dir;
            if (at < 0 || at >= count) return;
            Snd(2);
            if (dir < 0) { if (cur > 0) cur--; else top--; }
            else { if (cur < KeeperRows - 1) cur++; else top++; }
        }

        // renderKeeperBox: two boxes (-152,-98, 148x127) and (0,-98, 148x127) with the label, six rows of name and count; the cursor
        // sits in the active one. Still ours: box art, font, colours (counts that cannot move are grey), no scroll bar.
        void DrawKeeper()
        {
            var k = _keeper;
            if (k == null) return;
            const float s = 4.42f;
            for (int b = 0; b < 2; b++)
            {
                var r = new Rect(707.5f + (b == 0 ? -152 : 0) * s, 530 - 98 * s, 148 * s, 127 * s);
                UiKit.Box(r, UiKit.DialogFill);
                UiKit.Text(new Rect(r.x + 30, r.y + 10, r.width - 60, 60), b == 0 ? k.LeftLabel : k.RightLabel, 44, UiKit.Yellow);
                var items = b == 0 ? k.LeftItem : k.RightItem; var ok = b == 0 ? k.LeftOk : k.RightOk;
                int top = b == 0 ? k.LeftTop : k.RightTop, cur = b == 0 ? k.LeftCur : k.RightCur;
                for (int i = 0; i < KeeperRows && top + i < items.Count; i++)
                {
                    int item = items[top + i];
                    float y = r.y + 0x13 * s + i * 0x12 * s;
                    bool on = k.Side == b && cur == i;
                    if (item == 0xFF) { if (on) UiKit.Text(new Rect(r.x + 20, y, 60, 0x12 * s), ">", 44, UiKit.Yellow); continue; }
                    Color c = !ok[top + i] ? Color.gray : on ? UiKit.Yellow : Color.white;
                    int count = b == 0 ? _state.Stored[item] : _state.Bag[item];
                    string name = _state.ItemNameOf != null ? _state.ItemNameOf(item) : item.ToString();
                    UiKit.Text(new Rect(r.x + 20, y, r.width - 140, 0x12 * s), (on ? "> " : "   ") + name, 40, c);
                    UiKit.Text(new Rect(r.xMax - 130, y, 110, 0x12 * s), count.ToString(), 40, c, TextAnchor.MiddleRight);
                }
            }
        }

        // ------------------------------------------------------------------ 0x02 Mojyamon trade

        // MOJYAMON_ITEMS_GIVE / MOJYAMON_ITEMS_GET (exe 0x80130438 / 0x80130444): three trades per Mojyamon (variable 249 = which one)
        const uint MojyaGive = 0x80130438, MojyaGet = 0x80130444;

        // openMojyamonShop (states 0-6) with mojyaTradeFillItemList, tickMojyaTradeMenu and setMojyaItemTradedTrigger
        IEnumerator MojyamonTrade()
        {
            _routineOn = true;
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            int give = (int)(MojyaGive - load + 0x800), get = (int)(MojyaGet - load + 0x800);
            int type = _state.Vars[0xF9] * 3;                 // ITEM_MENU_TYPE
            var list = new RoutineList();
            System.Func<int, string> name = i => _state.ItemNameOf != null ? _state.ItemNameOf(i) : i.ToString();
            for (int i = 0; i < 3; i++)
            {
                int want = exe[give + type + i];
                list.Names.Add(name(want));
                list.Values.Add(name(exe[get + type + i]));
                list.Usable.Add(_state.Bag[want] != 0);
            }
            _state.Vars[0xF9] = 0xFF;
            yield return HeadSay(0x4D6, 8);                 // "I'm a collector. If you got a thing I want, I'll trade."
            int state = 3, row = 0;
            while (state != 2)
            {
                switch (state)
                {
                    case 3:     // the list, with "What will you give me?" up behind it
                        _list = list; list.Head = HeadText(0x4D6, 0xF);    // "I want  You have" (updateMojyaTradeStrings)
                        _line = HeadText(0x4D6, 9); _speaker = _state.Vars[0xFE];
                        yield return null;
                        while (true)
                        {
                            int ms = MenuStep();
                            if (ms < 0 && list.Cursor > 0) { list.Cursor--; Snd(2); }
                            else if (ms > 0 && list.Cursor < 2) { list.Cursor++; Snd(2); }
                            if (GameMenus.CancelKey()) { Snd(4); state = 6; break; }
                            if (ActionPressed())
                            {
                                if (list.Usable[list.Cursor]) { Snd(3); row = list.Cursor; _state.Vars[0xF9] = exe[give + type + row]; state = 7; break; }
                                Snd(0xB);
                            }
                            yield return null;
                        }
                        _line = null;
                        if (state == 7) { yield return HeadSay(0x4D6, 0xB); state = 4; }     // "You'll really give me <item>?"
                        break;
                    case 4:
                    {
                        int pick = 0;
                        yield return HeadAsk(0x4D6, 0xC, 2, a => pick = a);     // "Yeah" / "Well.... no"
                        state = 5 + pick;
                        break;
                    }
                    case 5:
                        _list = null;
                        if (_state.AddItem(exe[get + type + row], 1))
                        {
                            _state.RemoveItem(_state.Vars[0xF9], 1);
                            _state.SetFlag(type + row + 5, true);           // setMojyaItemTradedTrigger: flags 5-13, all of them = flag 14
                            bool all = true;
                            for (int f = 5; f < 0xE; f++) if (!_state.GetFlag(f)) all = false;
                            if (all) _state.SetFlag(0xE, true);
                            yield return HeadSay(0x4D6, 0xD);       // "Thanks."
                        }
                        else yield return HeadSay(0x4D6, 0xE);      // "Too many Items."
                        state = 2;
                        break;
                    case 6:
                        _list = null;
                        yield return HeadSay(0x4D6, 10);            // "I'll trade all the time, so bring whatever you got."
                        state = 2;
                        break;
                }
            }
            _list = null; _routineOn = false;
            AdvanceScript();
        }

        // renderBirdraTransportMenu: a box of rows 0x12 high, the cursor on one, names then cost; rows the Bits do not pay for are grey.
        // Still ours: the box art and font (renderItemMenuItemList, item-menu sprites) and the Bits box.
        void DrawRoutineList()
        {
            if (_itemBox != null) _itemBox.Draw();
            if (_keeper != null) { UiKit.Begin(); DrawKeeper(); UiKit.End(); }
            var l = _list;
            if (l == null) return;
            UiKit.Begin();
            var r = new Rect(707.5f - 71 * 4.42f, 530 - 100 * 4.42f, 222 * 4.42f, Mathf.Max(3, l.Names.Count + 1) * 0x12 * 4.42f);
            if (l.Head != null) r.height += 0x12 * 4.42f;
            UiKit.Box(r, UiKit.DialogFill);
            float y0 = r.y;
            if (l.Head != null) { UiKit.Text(new Rect(r.x + 40, r.y + 10, r.width - 80, 0x12 * 4.42f), l.Head.Replace("\n", "     "), 44, UiKit.Yellow); y0 += 0x12 * 4.42f; }
            for (int i = 0; i < l.Names.Count; i++)
            {
                float y = y0 + (0x13 + i * 0x12 - 0x11 + 4) * 4.42f;
                Color c = !l.Usable[i] ? Color.gray : i == l.Cursor ? UiKit.Yellow : Color.white;
                UiKit.Text(new Rect(r.x + 40, y, r.width * 0.65f, 0x12 * 4.42f), (i == l.Cursor ? "> " : "   ") + l.Names[i], 46, c);
                UiKit.Text(new Rect(r.x + r.width * 0.65f, y, r.width * 0.35f - 40, 0x12 * 4.42f), l.Values[i], 46, c, TextAnchor.MiddleRight);
            }
            if (l.ShowBits)
            {
                var b = new Rect(r.xMax - 360, r.y - 90, 360, 80);
                UiKit.Box(b, UiKit.DialogFill);
                UiKit.Text(new Rect(b.x + 20, b.y, b.width - 40, b.height), _state.Money + " Bits", 46, Color.white, TextAnchor.MiddleRight);
            }
            UiKit.End();
        }
    }
}
