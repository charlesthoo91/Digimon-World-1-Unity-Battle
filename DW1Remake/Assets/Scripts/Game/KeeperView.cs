using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The Item Keeper's window (shop/script_menu.c, US build): createItemKeepWindow, renderKeeperBox, tickItemKeeperWindow, itemKeeperFillItemList,
    /// keeperMoveOne / Ten / Stack, keeperScrollLeftToItem / RightToItem. Two UI boxes, 148 x 127: box 1 at (-152, -98) out of the keeper with the
    /// stored items, box 2 at (0, -98) out of the boy with the bag's slots. Each has the label ("Keeping" / "In hand", exe 0x801307C0 / 0x801307B4),
    /// two engraved headers (MENU and COUNT), five rows of name and amount (calculateItemMenuStrings, as item menu type 5: no pictures), the
    /// scroll bar and the cursor; the box in use has a blue half-see-through bar over its label. Start: the item's description (box 3).
    /// </summary>
    public sealed class KeeperView : UiBoxPage
    {
        public readonly UiBox Left = new UiBox { Id = 1 }, Right = new UiBox { Id = 2 }, Desc = new UiBox { Id = 3 };

        // ITEM_MENU_LEFT / RIGHT: type (0xFF an empty bag slot) and amount | 0x80 when it can move to the other side
        public sealed class Rows { public readonly List<int> Types = new List<int>(), Amounts = new List<int>(); public int Top, Cursor; }
        public readonly Rows L = new Rows(), R = new Rows();
        public int Side;                     // MAIN_D_80134F90: 0 the stored list, 1 the bag
        public const int Visible = 5;        // allocateItemMenuBox(..., 5, 0x88, 0x28, 6, 0x4A)

        List<ItemEntry> _items;
        string _labelLeft = "", _labelRight = "";
        int _descItem = -1;
        float _acc;
        const int DescRow = 132, LabelRowLeft = 120, LabelRowRight = 144;

        public static KeeperView Load(DiscImage disc, GameFont font, List<ItemEntry> items)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new KeeperView { _items = items };
            v.Init(disc, font);
            v.LoadHeaders();
            v._labelRight = v.Str(0x801307B4);
            v._labelLeft = v.Str(0x801307C0);
            return v;
        }

        public bool Showing { get { return Left.State != 0 || Right.State != 0 || Desc.State != 0; } }
        Rows Active { get { return Side == 0 ? L : R; } }
        public int ActiveCount { get { return Active.Types.Count; } }

        /// <summary>itemKeeperFillItemList: left = every item stored (in item order), movable when the bag has a free slot or holds it below 99;
        /// right = the bag's slots in order (empty ones too), movable when fewer than 99 are stored.</summary>
        public void Fill(ScriptState s)
        {
            s.SyncSlots();
            int size = Mathf.Min(s.Slots.Length, s.ItemSlots > 0 ? s.ItemSlots : 10);
            bool free = false;
            for (int i = 0; i < size; i++) if (s.Slots[i] == 0xFF) { free = true; break; }
            L.Types.Clear(); L.Amounts.Clear();
            for (int j = 0; j < 0x80; j++)
            {
                int c = s.Stored[j];
                if (c == 0) continue;
                L.Types.Add(j);
                bool ok = free;
                if (!ok) for (int i = 0; i < size; i++) if (s.Slots[i] == j && s.Bag[j] != 0x63) { ok = true; break; }
                L.Amounts.Add(ok ? c | 0x80 : c);
            }
            R.Types.Clear(); R.Amounts.Clear();
            for (int i = 0; i < size; i++)
            {
                int t = s.Slots[i];
                if (t == 0xFF || s.Bag[t] == 0) { R.Types.Add(0xFF); R.Amounts.Add(0); continue; }
                R.Types.Add(t);
                R.Amounts.Add(s.Stored[t] != 0x63 ? s.Bag[t] | 0x80 : s.Bag[t]);
            }
        }

        /// <summary>createItemKeepWindow: box 1 out of the keeper, box 2 out of the boy, both lists at the top.</summary>
        public void OpenWindow(RectInt keeper, RectInt boy)
        {
            L.Top = L.Cursor = R.Top = R.Cursor = 0;
            Open(Left, new RectInt(-152, -98, 148, 127), keeper, 0xE1);
            Open(Right, new RectInt(0, -98, 148, 127), boy, 0xE1);
        }

        public void CloseWindow() { Close(Left); Close(Right); Close(Desc); }

        // itemMenuCursorUp / Down / MoveToTop / MoveToBottom on the list in use
        public void Up()
        {
            var b = Active;
            if (b.Top + b.Cursor == 0) { Snd(0xB); return; }
            if (b.Cursor == 0) b.Top--; else b.Cursor--;
            Snd(2);
        }

        public void Down()
        {
            var b = Active;
            b.Cursor++;
            if (b.Top + b.Cursor < b.Types.Count) { if (b.Cursor == Visible) { b.Top++; b.Cursor--; } Snd(2); }
            else { b.Cursor--; Snd(0xB); }
        }

        public void ToTop() { var b = Active; b.Top = 0; b.Cursor = 0; Snd(2); }

        public void ToBottom()
        {
            var b = Active;
            int diff = b.Types.Count - Visible;
            if (diff <= 0) { b.Top = 0; b.Cursor = b.Types.Count - 1; } else { b.Top = diff; b.Cursor = Visible - 1; }
            Snd(2);
        }

        /// <summary>keeperMoveOne (n = 1), keeperMoveTen (10), keeperMoveStack (0 = as many as fit under 99). False when nothing moved.</summary>
        public bool Move(ScriptState s, int n)
        {
            var b = Active;
            int row = b.Top + b.Cursor;
            if (row >= b.Types.Count) return false;
            int item = b.Types[row], amount = b.Amounts[row];
            if (item == 0xFF || (amount & 0x80) == 0) return false;
            amount &= 0x7F;
            if (Side == 0)
            {
                int held = s.Bag[item];
                if (n == 1) { if (held >= 0x63) return false; }
                else if (n == 10) { if (amount < 10 || held >= 0x5A) return false; }
                else n = Mathf.Min(amount, 0x63 - held);
                s.Stored[item] = (byte)(s.Stored[item] - n);
                s.AddItem(item, n);
            }
            else
            {
                int kept = s.Stored[item];
                if (n == 1) { if (s.Bag[item] == 0) return false; }
                else if (n == 10) { if (amount < 10 || kept >= 0x5A) return false; }
                else n = Mathf.Min(amount, 0x63 - kept);
                s.RemoveItem(item, n);
                s.Stored[item] = (byte)(s.Stored[item] + n);
            }
            Fill(s);
            // the list it came from first, then the other (keeperMoveOne's order)
            if (Side == 0) { FollowLeft(item); FollowRight(item); } else { FollowRight(item); FollowLeft(item); }
            return true;
        }

        // keeperScrollLeftToItem: onto the moved item; if it is gone, the row it was on (the last one at most), one row up when the list ends early
        void FollowLeft(int item)
        {
            var b = L;
            int count = b.Types.Count, top = b.Top, cursor = b.Cursor;
            int i = b.Types.IndexOf(item);
            if (i < 0)
            {
                if (count == 0) { b.Top = 0; b.Cursor = 0; return; }
                i = top + cursor;
                if (i >= count) i = count - 1;
                if (top + Visible >= count && top != 0) { b.Top--; return; }
            }
            if (i >= top && i < top + Visible) { b.Cursor = i - top; return; }
            int d = i - Visible;
            if (d < 0) { b.Top = 0; b.Cursor = i; } else { b.Top = d + 1; b.Cursor = Visible - 1; }
        }

        // keeperScrollRightToItem: onto the moved item if the bag still has it; otherwise the cursor stays
        void FollowRight(int item)
        {
            var b = R;
            int i = b.Types.IndexOf(item);
            if (i < 0) return;
            if (i >= b.Top && i < b.Top + Visible) { b.Cursor = i - b.Top; return; }
            int d = i - Visible;
            if (d < 0) { b.Top = 0; b.Cursor = i; } else { b.Top = d + 1; b.Cursor = Visible - 1; }
        }

        /// <summary>createItemMenuDescriptionBox(box, (5, 0x20, 0x80, 0x12), box in use): out of the cursor's row. False on an empty slot.</summary>
        public bool OpenDesc()
        {
            var b = Active; var box = Side == 0 ? Left : Right;
            int row = b.Top + b.Cursor;
            if (row >= b.Types.Count || b.Types[row] == 0xFF) return false;
            _descItem = b.Types[row];
            Open(Desc, new RectInt(-0x84, -0x0B, 0x108, 0x16), new RectInt(5 + box.Rect.x, 0x20 + box.Rect.y + b.Cursor * 18, 0x80, 0x12), 0xC1);
            return true;
        }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Left); Step(Right); Step(Desc); }
            }
            BuildText();
            Begin();
            DrawBox(Left, () => DrawList(Left, L, 0));
            DrawBox(Right, () => DrawList(Right, R, 1));
            DrawBox(Desc, () => String(6 - Desc.Id, Desc.Rect.x + 6, Desc.Rect.y + 5, 0xFC, 0, DescRow));
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // the text page: the left rows 0-4, the right rows 5-9 (calculateItemMenuStrings type 5: colour 1, or 3 when it cannot move; the name,
        // 0x0F, 0x1C, the amount in 2 places), the labels and the description
        void BuildText()
        {
            var key = new System.Text.StringBuilder();
            foreach (var b in new[] { L, R })
            {
                key.Append(b.Top).Append(':');
                for (int i = 0; i < b.Types.Count; i++) key.Append(b.Types[i]).Append(',').Append(b.Amounts[i]).Append(';');
                key.Append('|');
            }
            string desc = Desc.State != 0 && _descItem >= 0 && _descItem < _items.Count ? _items[_descItem].Description : "";
            key.Append(desc);
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                Color32 white = Font.Colour(1);
                for (int side = 0; side < 2; side++)
                {
                    var b = side == 0 ? L : R;
                    for (int i = 0; i < Visible && b.Top + i < b.Types.Count; i++)
                    {
                        int t = b.Types[b.Top + i];
                        if (t == 0xFF || t >= _items.Count) continue;
                        int a = b.Amounts[b.Top + i];
                        DrawCoded(px, Ink((a & 0x80) != 0 ? 1 : 3) + _items[t].Name + Gap + Tab1C + ItemMenuView.Digits(a & 0x7F, 2), 0, (side * Visible + i) * 12, white);
                    }
                }
                DrawCoded(px, _labelLeft, 0, LabelRowLeft, white);
                DrawCoded(px, _labelRight, 0, LabelRowRight, white);
                DrawCoded(px, desc, 0, DescRow, white);
            });
        }

        // renderKeeperBox (US): the two engraved header boxes with MENU and COUNT, the scroll bar (0x88, 0x28, 6 x 0x4A), the cursor, the label
        // at (0x2D, 7) 0x3C wide ("Keeping") or 0x38 ("In hand"), the blue bar over the label of the box in use, then the rows from (8, 0x24)
        void DrawList(UiBox box, Rows b, int side)
        {
            int z = 6 - box.Id, bx = box.Rect.x, by = box.Rect.y;
            TableBox(z, bx + 4, by + 0x15, 0x67, 0x0B);
            Header(z, 0, bx + 8, by + 0x17);
            TableBox(z, bx + 0x6B, by + 0x15, 0x25, 0x0B);
            Header(z, 3, bx + 0x6E, by + 0x17);
            ScrollBarAt(z, bx + 0x88, by + 0x28, 6, 0x4A, b.Types.Count, b.Top, Visible);
            SelectionCursor(z, bx + 5, by + b.Cursor * 0x12 + 0x21, 0x80, 0x12);
            String(z, bx + 0x2D, by + 7, 0x3C, 0, side != 0 ? LabelRowRight : LabelRowLeft);      // fix (user, 2026-11-05): the game's 0x38 for "In hand" cuts its last letter
            if (Side == side) Box(z, bx + 4, by + 3, 0x8C, 0x12, 0x80, 0x80, 0xFF, 1);
            int rows = Mathf.Min(Visible, b.Types.Count - b.Top);
            for (int i = 0; i < rows; i++)
                if (b.Types[b.Top + i] != 0xFF) String(z, bx + 8, by + 0x24 + i * 0x12, 0x80, 0, (side * Visible + i) * 12);
        }
    }
}
