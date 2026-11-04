using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The game's item list box (main/script_common.c, ui.c, US build) for ITEM_MENU_TYPE 0 (shop: buy), 1 (shop: sell) and 5 (script built-in 0,
    /// openDiscardItem): a UI box (renderUIBoxStatic / renderUIBoxBorder, growing out of its owner in 4 frames: renderUIBoxAnim), the headers
    /// (ITEM_MENU_HEADER_UVS on SYSTEM_W), one row of 0x12 per entry with the item picture and calculateItemMenuStrings' text, the scroll bar
    /// (renderItemMenuScrollBar), the cursor (renderSelectionCursor); the "Is it OK?" box (createSingleCardShopMenu, renderSingleCardShop, text
    /// DG0 0x4D8), the amount box (createItemMenuAmountBox, renderItemMenuAmountBox), the description box (createItemMenuDescriptionBox) and the
    /// shop's Bits box (createShopBitsBox, renderShopBitsBox). Positions are PS1 pixels from the middle of the screen; the tables are read from the exe.
    /// </summary>
    public sealed class ItemMenuView : UiBoxPage
    {
        public readonly UiBox List = new UiBox { Id = 1 }, Bits = new UiBox { Id = 2 }, Confirm = new UiBox { Id = 3 }, Amount = new UiBox { Id = 3 }, Desc = new UiBox { Id = 3 };
        // ITEM_MENU_LEFT / RIGHT ->buf: type (0xFF empty), then for type 0 whether it can be bought (0 / 1), for 1 and 5 the amount | 0x80 if it can be picked
        public readonly List<int> Types = new List<int>(), Amounts = new List<int>();
        public int Top, Cursor, Pick;                                                     // topRow, cursor, SHOP_AMOUNT of the Yes/No box (0 Yes, 1 No)
        public int Type = 5;                                                              // ITEM_MENU_TYPE
        public int ShopItem, ShopPrice, ShopAmount, MaxAmount;                            // SHOP_ITEM_TYPE, SHOP_ITEM_PRICE, SHOP_AMOUNT, MAX_SHOP_AMOUNT
        public string BitsText = "";                                                      // the Bits box's line (shopkeeper text 6)
        public const int Visible = 6;

        ItemIcons _icons;
        List<ItemEntry> _items;
        string _ask = "", _answers = "";
        float _acc;

        // ITEM_MENU_POS, ITEM_MENU_DESCRIPTION_RECTS, SELECTION_CURSOR_WIDTHS (8 types), ITEM_MENU_HEADER_UVS (5), AMOUNT_BOX_LAYOUT (US: 4 used)
        const uint MenuPos = 0x8013027C, DescRects = 0x801302BC, CursorWidths = 0x801302FC, HeaderUvs = 0x8012FE24, AmountLayout = 0x8012FDE4;
        readonly RectInt[] _pos = new RectInt[8], _desc = new RectInt[8];
        readonly int[] _cursorW = new int[8];
        readonly int[,] _hdr = new int[5, 4], _amountAt = new int[5, 3];

        // text page rows: the list 0-5, the Yes/No box 96 / 108, the amount box 120, the description 132, the Bits box 144
        const int AmountRow = 120, DescRow = 132, BitsRow = 144;

        public static ItemMenuView Load(DiscImage disc, GameFont font, ItemIcons icons, List<ItemEntry> items, string confirmText)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new ItemMenuView { _icons = icons, _items = items };
            v.Init(disc, font);
            for (int t = 0; t < 8; t++)
            {
                int p = v.At(MenuPos) + t * 8, d = v.At(DescRects) + t * 8;
                v._pos[t] = new RectInt(v.S16(p), v.S16(p + 2), v.S16(p + 4), v.S16(p + 6));
                v._desc[t] = new RectInt(v.S16(d), v.S16(d + 2), v.S16(d + 4), v.S16(d + 6));
                v._cursorW[t] = v.S16(v.At(CursorWidths) + t * 2);
            }
            for (int i = 0; i < 5; i++) for (int k = 0; k < 4; k++) v._hdr[i, k] = v.S16(v.At(HeaderUvs) + i * 8 + k * 2);
            for (int i = 0; i < 5; i++) for (int k = 0; k < 3; k++) v._amountAt[i, k] = v.S16(v.At(AmountLayout) + i * 6 + k * 2);
            var lines = (confirmText ?? "").Split('\n');
            v._ask = lines[0]; v._answers = lines.Length > 1 ? lines[1] : "";
            return v;
        }

        public bool Showing { get { return List.State != 0 || Confirm.State != 0 || Amount.State != 0 || Desc.State != 0 || Bits.State != 0; } }

        /// <summary>ITEM_MENU_POS of the current type: where the list box stands.</summary>
        public RectInt ListRect { get { return _pos[Type & 7]; } }

        /// <summary>shopFillSellItemList: one row per bag slot in the bag's order; returns whether any item can be picked (Item.droppable).</summary>
        public bool Fill(ScriptState s)
        {
            Types.Clear(); Amounts.Clear(); Top = 0; Cursor = 0;
            bool any = false;
            s.SyncSlots();
            int size = Mathf.Min(s.Slots.Length, s.ItemSlots > 0 ? s.ItemSlots : 10);
            for (int i = 0; i < size; i++)
            {
                int t = s.Slots[i];
                if (t == 0xFF || t >= _items.Count || s.Bag[t] == 0) { Types.Add(0xFF); Amounts.Add(0); continue; }
                int amount = s.Bag[t];
                if (_items[t].Droppable) { amount |= 0x80; any = true; }
                Types.Add(t); Amounts.Add(amount);
            }
            return any;
        }

        /// <summary>shopFillBuyItemList: every item whose flag 0x180 + item is on (stock); 1 = it can be bought now (the Bits pay for it, and the bag
        /// has a free slot and less than 99 of it, or no free slot but some of it below 99). Returns whether anything can be bought.</summary>
        public bool FillBuy(ScriptState s, Func<int, bool> stock)
        {
            Types.Clear(); Amounts.Clear(); Top = 0; Cursor = 0;
            s.SyncSlots();
            int size = Mathf.Min(s.Slots.Length, s.ItemSlots > 0 ? s.ItemSlots : 10);
            bool space = false, any = false;
            for (int i = 0; i < size; i++) if (s.Slots[i] == 0xFF) { space = true; break; }
            for (int id = 0; id < 128 && id < _items.Count; id++)
            {
                if (!stock(id)) continue;
                Types.Add(id);
                int ok;
                if (_items[id].Price > s.Money) ok = 0;
                else if (space) ok = s.Bag[id] == 99 ? 0 : 1;
                else ok = s.Bag[id] > 0 && s.Bag[id] != 99 ? 1 : 0;
                Amounts.Add(ok);
                if (ok != 0) any = true;
            }
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

        // itemMenuCursorMoveToTop / itemMenuCursorMoveToBottom (R1 + up / down)
        public void ToTop() { Top = 0; Cursor = 0; Snd(2); }
        public void ToBottom()
        {
            int diff = Types.Count - Visible;
            if (diff <= 0) { Top = 0; Cursor = Types.Count - 1; } else { Top = diff; Cursor = Visible - 1; }
            Snd(2);
        }

        public int Row { get { return Top + Cursor; } }

        // ITEM_MENU_DESCRIPTION_RECTS of the type, moved onto the cursor's row of the list box: where the sub-boxes grow out of
        RectInt RowOrigin()
        {
            var d = _desc[Type & 7];
            return new RectInt(d.x + List.Rect.x, d.y + List.Rect.y + Cursor * 18, d.width, d.height);
        }

        /// <summary>createSingleCardShopMenu for type 5: only a row whose item can be picked; the box grows out of the description place of that row.</summary>
        public bool Ask()
        {
            int r = Row;
            if (r >= Types.Count || Types[r] == 0xFF || (Amounts[r] & 0x80) == 0) { Snd(0xB); return false; }
            Open(Confirm, new RectInt(-0x38, -0x15, 0x70, 0x2A), RowOrigin(), 0xC1);
            Pick = 0;
            Snd(3);
            return true;
        }

        /// <summary>createItemMenuAmountBox, types 0 and 1: the price (half the item's value when selling) and the most that can be bought or sold.
        /// money = the Bits now, held = how many the bag has of it (getItemCount).</summary>
        public bool OpenAmount(int money, int held)
        {
            int r = Row;
            if (r >= Types.Count || Types[r] == 0xFF) { Snd(0xB); return false; }
            ShopItem = Types[r];
            int amount = Amounts[r];
            ShopPrice = _items[ShopItem].Price;
            if (Type == 1)
            {
                if ((amount & 0x80) == 0) { Snd(0xB); return false; }
                ShopPrice >>= 1;
                MaxAmount = amount & 0x7F;
            }
            else
            {
                if (amount == 0) { Snd(0xB); return false; }
                MaxAmount = 0x63;
                int n = 0x63 - held;
                if (n < MaxAmount) MaxAmount = n;
                if (MaxAmount * ShopPrice > money) MaxAmount = ShopPrice > 0 ? money / ShopPrice : MaxAmount;
            }
            ShopAmount = 1;
            Open(Amount, new RectInt(-0x41, -0x2A, 0x82, 0x53), RowOrigin(), 0xC1);
            Snd(3);
            return true;
        }

        /// <summary>closeTextbox(3, origin): the amount box shrinks into the one who gets the item or the Bits.</summary>
        public void CloseAmountInto(RectInt? origin)
        {
            if (origin.HasValue) Amount.Origin = origin.Value;
            Close(Amount);
        }

        /// <summary>createItemMenuDescriptionBox: the item's description, growing out of the row.</summary>
        public bool OpenDesc()
        {
            int r = Row;
            if (r >= Types.Count || Types[r] == 0xFF) return false;
            ShopItem = Types[r];
            Open(Desc, new RectInt(-0x84, -0x0B, 0x108, 0x16), RowOrigin(), 0xC1);
            return true;
        }

        /// <summary>createShopBitsBox: out of the boy at (-0x98, -0x62) 0x52 x 0x21; already open = only its text is written again.</summary>
        public void OpenBits(RectInt? boy, string text)
        {
            BitsText = text ?? "";
            if (Bits.State == 1) return;
            var rect = new RectInt(-0x98, -0x62, 0x52, 0x21);
            Open(Bits, rect, boy ?? new RectInt(-5, -5, 10, 10), 0xE1);
        }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(List); Step(Bits); Step(Confirm); Step(Amount); Step(Desc); }
            }
            BuildText();
            Begin();
            DrawBox(List, () => DrawList());
            DrawBox(Bits, () => DrawBits());
            DrawBox(Confirm, () => DrawConfirm());
            DrawBox(Amount, () => DrawAmount());
            DrawBox(Desc, () => DrawDesc());
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // ---- the text: drawString (US, script_textbox.c) with the codes the menus use
        public const char Gap = '\u000F', Tab16 = '\u0016', Tab18 = '\u0018', Tab19 = '\u0019', Tab1A = '\u001A', Tab1C = '\u001C';
        const char Mark = (char)0xE000;      // a colour code (01 n), as GameFont keeps it
        static string Ink(int n) { return ((char)(Mark + n)).ToString(); }

        /// <summary>intToStringSJIS: 'digits' places; a leading 0 is a space (flag 0) or left out (flag 1); the last place is always written.</summary>
        public static string Digits(int value, int digits, bool skip = false)
        {
            var sb = new System.Text.StringBuilder();
            bool started = false;
            for (int d = digits; d > 0; d--)
            {
                int div = 1; for (int k = 1; k < d; k++) div *= 10;
                int c = (value / div) % 10;
                if (d != 1 && c == 0 && !started) { if (!skip) sb.Append(' '); continue; }
                if (c != 0) started = true;
                sb.Append((char)('0' + c));
            }
            return sb.ToString();
        }

        // drawString into the page: 0x0F clears 6 pixels and moves on 6; 0x16 / 0x18 / 0x19 / 0x1A / 0x1C clear an area and jump to x 0x69 / 0x60 /
        // 0x9C / 0xB4 / 0x64; a space (0x8140) clears 12 pixels and moves on by the last glyph's advance (the US code never sets it for a space;
        // at the start of a string ours uses 0); any other glyph is drawn whole (drawGlyph) and moves on by its own width
        void DrawCoded(Color32[] px, string s, int x, int y, Color32 ink)
        {
            if (Font == null || s == null) return;
            int pos = 0, adv = 0;
            foreach (char ch in s)
            {
                if (ch >= Mark && ch <= Mark + 15) { ink = Font.Colour(ch - Mark); continue; }
                switch (ch)
                {
                    case Gap: Clear(px, x + pos, y, 6); pos += 6; continue;
                    case Tab16: Clear(px, x + pos - 6, y, 0x69); pos = 0x69; continue;
                    case Tab18: Clear(px, x + pos, y, 0x60); pos = 0x60; continue;
                    case Tab19: Clear(px, x + pos, y, 0x9C); pos = 0x9C; continue;
                    case Tab1A: Clear(px, x + pos, y, 0xB4); pos = 0xB4; continue;
                    case Tab1C: Clear(px, x + pos, y, 0x64); pos = 0x64; continue;
                    case '\n': return;
                    case ' ': case '　': Clear(px, x + pos, y, 12); pos += adv; continue;
                }
                string one = ch.ToString();
                Font.Render(one, px, 256, 256, x + pos, y, ink);
                adv = Font.Measure(one);
                pos += adv;
            }
        }

        static void Clear(Color32[] px, int x, int y, int w)
        {
            for (int r = 0; r < 12; r++)
                for (int c = 0; c < w; c++)
                {
                    int u = x + c, v = y + r;
                    if (u < 0 || u >= 256 || v < 0 || v >= 256) continue;
                    px[(255 - v) * 256 + u] = new Color32(0, 0, 0, 0);
                }
        }

        // calculateItemMenuStrings (US): colour 1, or 3 if it cannot be bought / picked; the name, 0x0F; type 0: 0x16, the price in 4 places, 0x0F;
        // type 1: 0x16, half the price (or four dashes 0x817C if it cannot be sold), 0x0F, the amount in 2 places; type 5: 0x1C, the amount
        string RowText(int t, int a)
        {
            bool on = Type == 0 ? a != 0 : (a & 0x80) != 0;
            var sb = new System.Text.StringBuilder(Ink(on ? 1 : 3)).Append(_items[t].Name).Append(Gap);
            if (Type == 5) return sb.Append(Tab1C).Append(Digits(a & 0x7F, 2)).ToString();
            sb.Append(Tab16);
            if (Type == 1 && !on) sb.Append(new string((char)0x817C, 4)).Append(Gap);
            else sb.Append(Digits(Type == 1 ? _items[t].Price >> 1 : _items[t].Price, 4)).Append(Gap);
            if (Type == 1) sb.Append(Digits(a & 0x7F, 2));
            return sb.ToString();
        }

        // updateItemMenuAmountBoxString (US): the name, 0x18, the price in 5 places, 0x19, the amount in 2, 0x1A, the total in 6 (at most 999999)
        string AmountText()
        {
            if (ShopItem < 0 || ShopItem >= _items.Count) return "";
            int total = Mathf.Min(ShopPrice * ShopAmount, 999999);
            return _items[ShopItem].Name + Tab18 + Digits(ShopPrice, 5) + Tab19 + Digits(ShopAmount, 2) + Tab1A + Digits(total, 6);
        }

        void BuildText()
        {
            var key = new System.Text.StringBuilder().Append(Type).Append(':').Append(Top).Append(':');
            for (int i = 0; i < Types.Count; i++) key.Append(Types[i]).Append(',').Append(Amounts[i]).Append(';');
            string amountText = Amount.State != 0 ? AmountText() : "";
            string desc = Desc.State != 0 && ShopItem >= 0 && ShopItem < _items.Count ? _items[ShopItem].Description : "";
            key.Append('|').Append(amountText).Append('|').Append(desc).Append('|').Append(BitsText);
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                Color32 white = Font.Colour(1);
                for (int i = 0; i < Visible && Top + i < Types.Count; i++)
                {
                    int t = Types[Top + i];
                    if (t == 0xFF || t >= _items.Count) continue;
                    DrawCoded(px, RowText(t, Amounts[Top + i]), 0, i * 12, white);
                }
                Font.Render(_ask, px, 256, 256, 0, 96, white);
                Font.Render(_answers, px, 256, 256, 0, 108, white);
                DrawCoded(px, amountText, 0, AmountRow, white);
                DrawCoded(px, desc, 0, DescRow, white);
                DrawCoded(px, BitsText, 0, BitsRow, white);
            });
        }

        // renderItemMenuSprite: ITEM_MENU_HEADER_UVS entry on SYSTEM_W (colour set (96, 492))
        void Header(int z, int idx, int x, int y) { SysIcon(z, x, y, _hdr[idx, 2], _hdr[idx, 3], _hdr[idx, 0] - 0x200, _hdr[idx, 1] - 0x100, 4); }

        // renderItemMenu: the headers (0 and 1, plus 3 when selling; 0 and 3 for type 5), the scroll bar, the cursor, then the pictures and the text
        void DrawList()
        {
            int z = 6 - List.Id, bx = List.Rect.x, by = List.Rect.y;
            Header(z, 0, bx + 8, by + 5);
            if (Type == 5) Header(z, 3, bx + 0x80, by + 5);
            else
            {
                Header(z, 1, bx + 0x80, by + 5);
                if (Type != 0) Header(z, 3, bx + 0xB6, by + 5);
            }
            ScrollBar(z, bx, by);
            SelectionCursor(z, bx + 5, by + Cursor * 0x12 + 0x11, _cursorW[Type & 7], 0x12);
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

        // renderItemMenuScrollBar; the bar's place is the item box's rect (allocateItemMenuBox: x 0xB2 buying, 0xD2 selling, 0x9A type 5; y 0x18, 6 x 0x5A)
        void ScrollBar(int z, int bx, int by)
        {
            int x = (Type == 0 ? 0xB2 : Type == 1 ? 0xD2 : 0x9A) + bx, y = 0x18 + by, w = 6, h = 0x5A;
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

        // renderItemMenuAmountBox (US): two engraved lines (renderHorizontalLine), the inset round the amount (renderInsetWithoutBox), the item's
        // picture, then four pieces of the text line at AMOUNT_BOX_LAYOUT's places: name (8 cells), price (5), amount (2), total (6)
        void DrawAmount()
        {
            int z = 6 - Amount.Id, x = Amount.Rect.x, y = Amount.Rect.y;
            HLine(z, x + 4, y + 0x17, 0x7A);
            HLine(z, x + 0x0C, y + 0x3C, 0x6A);
            Inset(z, x + 0x55, y + 0x2A, 0x1A, 0x0E);
            if (_icons != null && ShopItem >= 0 && ShopItem < 128)
            {
                var uv = _icons.Uv(ShopItem); var sheet = _icons.Sheet;
                int sx = x + 8, sy = y + 5;
                Add(z, () => GUI.DrawTextureWithTexCoords(new Rect(X(sx), Y(sy), 16 * 4.42f, 16 * 4.42f), sheet, uv));
            }
            int col = 0;
            for (int i = 0; i < 4; i++)
            {
                int chars = _amountAt[i, 2];
                String(z, x + _amountAt[i, 0], y + _amountAt[i, 1], chars * 12, col * 12, AmountRow);
                col += chars;
            }
        }

        // renderItemMenuDescriptionBox (US): one line at (6, 5), 0xFC wide
        void DrawDesc()
        {
            int z = 6 - Desc.Id;
            String(z, Desc.Rect.x + 6, Desc.Rect.y + 5, 0xFC, 0, DescRow);
        }

        // renderShopBitsBox (US): the line's first 0x30 pixels at (0x16, 4), the next 0x3C under it at (0x16, 0x11)
        void DrawBits()
        {
            int z = 6 - Bits.Id, x = Bits.Rect.x + 6, y = Bits.Rect.y + 4;
            String(z, x + 0x10, y, 0x30, 0, BitsRow);
            String(z, x + 0x10, y + 0x0D, 0x3C, 0x30, BitsRow);
        }

        // renderHorizontalLine: black, 0xA08769, black, one under the other
        void HLine(int z, int x, int y, int w)
        {
            LineCmd(z, Bgr(0x020202), x, y, x + w - 1, y);
            LineCmd(z, Bgr(0xA08769), x, y + 1, x + w - 1, y + 1);
            LineCmd(z, Bgr(0x020202), x, y + 2, x + w - 1, y + 2);
        }

        // renderInsetWithoutBox: light bottom and right edges, dark top and left
        void Inset(int z, int x, int y, int w, int h)
        {
            Line3(z, Bgr(0xA08769), x + 1, y + h - 1, x + w - 1, y + h - 1, x + w - 1, y);
            Line3(z, Bgr(0x020202), x + w - 1, y, x, y, x, y + h - 1);
        }
    }
}
