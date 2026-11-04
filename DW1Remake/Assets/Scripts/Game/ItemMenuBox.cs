using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The game's item list box (main/script_common.c, ui.c, US build), as script built-in 0 (openDiscardItem, ITEM_MENU_TYPE 5) shows it:
    /// a UI box (renderUIBoxStatic / renderUIBoxBorder, growing out of the boy in 4 frames: renderUIBoxAnim), the MENU and COUNT headers
    /// (ITEM_MENU_HEADER_UVS on SYSTEM_W), one row of 0x12 per bag slot with the item picture and calculateItemMenuStrings' text,
    /// the scroll bar (renderItemMenuScrollBar), the cursor (renderSelectionCursor), and the "Is it OK?" box (createSingleCardShopMenu,
    /// renderSingleCardShop, text DG0 0x4D8). Positions are PS1 pixels from the middle of the screen.
    /// </summary>
    public sealed class ItemMenuView : UiBoxPage
    {
        public readonly UiBox List = new UiBox { Id = 1 }, Confirm = new UiBox { Id = 3 };
        public readonly List<int> Types = new List<int>(), Amounts = new List<int>();   // ITEM_MENU_RIGHT->buf: type (0xFF empty), amount | 0x80 if it can be picked
        public int Top, Cursor, Pick;                                                     // topRow, cursor, SHOP_AMOUNT (0 Yes, 1 No)
        public const int Visible = 6;

        ItemIcons _icons;
        List<ItemEntry> _items;
        string _ask = "", _answers = "";
        float _acc;

        // ITEM_MENU_POS[5], ITEM_MENU_DESCRIPTION_RECTS[5], SELECTION_CURSOR_WIDTHS[5]; ITEM_MENU_RIGHT: rect (0x9A, 0x18, 6, 0x5A)
        static readonly RectInt ListRect = new RectInt(-0x24, -0x62, 0xA6, 0x81), DescRect = new RectInt(5, 17, 146, 18), BarRect = new RectInt(0x9A, 0x18, 6, 0x5A);
        const int CursorWidth = 0x92;

        public static ItemMenuView Load(DiscImage disc, GameFont font, ItemIcons icons, List<ItemEntry> items, string confirmText)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new ItemMenuView { _icons = icons, _items = items };
            v.Init(disc, font);
            var lines = (confirmText ?? "").Split('\n');
            v._ask = lines[0]; v._answers = lines.Length > 1 ? lines[1] : "";
            return v;
        }

        public bool Showing { get { return List.State != 0 || Confirm.State != 0; } }

        /// <summary>shopFillSellItemList: one row per bag slot; returns whether any item can be picked (Item.droppable).</summary>
        public bool Fill(ScriptState s)
        {
            Types.Clear(); Amounts.Clear(); Top = 0; Cursor = 0;
            bool any = false;
            // our bag keeps counts, not slot order: the items by number, then the empty slots
            for (int i = 0; i < s.Bag.Length && i < _items.Count; i++)
            {
                if (s.Bag[i] == 0) continue;
                int amount = s.Bag[i];
                if (_items[i].Droppable) { amount |= 0x80; any = true; }
                Types.Add(i); Amounts.Add(amount);
            }
            while (Types.Count < s.ItemSlots) { Types.Add(0xFF); Amounts.Add(0); }
            return any;
        }

        // itemMenuCursorUp / itemMenuCursorDown
        public void Up()
        {
            if (Top + Cursor == 0) { Snd(0xB); return; }
            if (Cursor == 0) Top--; else Cursor--;
            Snd(2);
        }

        public void Down()
        {
            Cursor++;
            if (Top + Cursor < Types.Count)
            {
                if (Cursor == Visible) { Top++; Cursor--; }
                Snd(2);
            }
            else { Cursor--; Snd(0xB); }
        }

        public int Row { get { return Top + Cursor; } }

        /// <summary>createSingleCardShopMenu for type 5: only a row whose item can be picked; the box grows out of the description place of that row.</summary>
        public bool Ask()
        {
            int r = Row;
            if (r >= Types.Count || Types[r] == 0xFF || (Amounts[r] & 0x80) == 0) { Snd(0xB); return false; }
            var origin = new RectInt(DescRect.x + List.Rect.x, DescRect.y + List.Rect.y + Cursor * 18, DescRect.width, DescRect.height);
            Open(Confirm, new RectInt(-0x38, -0x15, 0x70, 0x2A), origin, 0xC1);
            Pick = 0;
            Snd(3);
            return true;
        }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(List); Step(Confirm); }
            }
            BuildText();
            Begin();
            DrawBox(List, () => DrawList());
            DrawBox(Confirm, () => DrawConfirm());
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // the text page: the rows (calculateItemMenuStrings type 5: colour 1, or 3 if it cannot be picked; the name, 0x0F = 6 pixels on,
        // 0x1C = to x 0x64, the amount in 2 places), then the two lines of the "Is it OK?" box from row 8
        void BuildText()
        {
            var key = new System.Text.StringBuilder().Append(Top).Append(':');
            for (int i = 0; i < Types.Count; i++) key.Append(Types[i]).Append(',').Append(Amounts[i]).Append(';');
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                int digit = Font.Measure("0");
                for (int i = 0; i < Visible && Top + i < Types.Count; i++)
                {
                    int t = Types[Top + i];
                    if (t == 0xFF || t >= _items.Count) continue;
                    int a = Amounts[Top + i];
                    Color32 ink = Font.Colour((a & 0x80) != 0 ? 1 : 3);
                    Font.Render(_items[t].Name, px, 256, 256, 0, i * 12, ink);
                    string n = (a & 0x7F).ToString();
                    Font.Render(n, px, 256, 256, 0x64 + (2 - n.Length) * digit, i * 12, ink);
                }
                Color32 white = Font.Colour(1);
                Font.Render(_ask, px, 256, 256, 0, 96, white);
                Font.Render(_answers, px, 256, 256, 0, 108, white);
            });
        }

        // renderItemMenu, type 5: MENU and COUNT, the scroll bar, the cursor, then the pictures and the text
        void DrawList()
        {
            int z = 6 - List.Id, bx = List.Rect.x, by = List.Rect.y;
            SysIcon(z, bx + 8, by + 5, 0x14, 7, 0x00, 0xA2, 4);
            SysIcon(z, bx + 0x80, by + 5, 0x16, 7, 0x64, 0xB0, 4);
            ScrollBar(z, bx, by);
            SelectionCursor(z, bx + 5, by + Cursor * 0x12 + 0x11, CursorWidth, 0x12);
            int rows = Mathf.Min(Visible, Types.Count - Top);
            for (int i = 0; i < rows; i++)
            {
                int t = Types[Top + i];
                if (t == 0xFF || _icons == null) continue;
                int x = bx + 8, y = by + 0x12 + i * 0x12;
                var uv = _icons.Uv(t); var sheet = _icons.Sheet;
                Add(z, () => GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), 16 * 4.42f, 16 * 4.42f), sheet, uv));
            }
            for (int i = 0; i < rows; i++)
                if (Types[Top + i] != 0xFF) String(z, bx + 0x1A, by + 0x13 + i * 0x12, 0xFF, 0, i * 12);
        }

        // renderItemMenuScrollBar
        void ScrollBar(int z, int bx, int by)
        {
            int x = BarRect.x + bx, y = BarRect.y + by, w = BarRect.width, h = BarRect.height;
            int rows = Mathf.Max(Types.Count, Visible);
            int track = h * 100 / rows, thumbY = y + 1 + track * Top / 100;
            track = track * Visible / 100;
            Color shadow = Bgr(0x020202), light = Bgr(0xA08769);
            Line3(z, shadow, x + w + 1, y, x, y, x, y + h);
            Line3(z, light, x, y + h + 1, x + w + 1, y + h + 1, x + w + 1, y + 1);
            Line3(z, light, x + w, thumbY, x + 1, thumbY, x + 1, thumbY + track - 2);
            Line3(z, shadow, x + 1, thumbY + track - 1, x + w, thumbY + track - 1, x + w, thumbY + 1);
            Box(z, x + 2, thumbY + 1, w - 2, track - 2, 0x5B, 0x70, 0x80);
            Box(z, x + 1, y + 1, w, h, 0x35, 0x4B, 0x5C);
        }

        // renderSingleCardShop: CONFIRM_BOX_LAYOUT (US): "Is it OK?" at (4, 2) 0x60 wide; "Yes" (0, 12) at (0x0E, 0x12); "No" (0x24, 12) at (0x44, 0x12)
        void DrawConfirm()
        {
            int z = 6 - Confirm.Id, x = Confirm.Rect.x + 4, y = Confirm.Rect.y + 3;
            String(z, x + 4, y + 2, 0x60, 0, 96);
            String(z, x + 0x0E, y + 0x12, 0x24, 0, 108);
            String(z, x + 0x44, y + 0x12, 0x24, 0x24, 108);
            SelectionCursor(z, x + 8 + Pick * 47, y + 0x12, 0x28, 0x0E);
        }

    }
}
