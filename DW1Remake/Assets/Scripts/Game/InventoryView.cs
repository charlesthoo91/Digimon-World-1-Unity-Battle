using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The bag (main/inventory.c, US build). UI box 0, the list: (-0x98, -0x68) 0x130 x 0x6E (0xB6 for 20 or 30 slots), growing out of the boy; two items a
    /// row, 0x12 apart, each its picture, its name in text colour 5 + ITEM_PARA.itemColor (8 when 0xFF) and its amount; 9 rows show at a time. UI box 1, the
    /// description: (-0x98, 0x4D) 0x130 x 0x1C, text colour 9. Cross on an item opens box 2, Use / Move / Sort / Drop (ITEM_MENU_LABELS; grey (0x0A) when it
    /// cannot be done), growing out of the item's row; Sort opens box 3 with Battle / Raise / Basic (ITEM_SORT_ORDER, ITEM_PARA.sortingValue), Drop box 3
    /// "Are you sure?" Yes / No (No first). Move marks the item and the next Cross swaps the two slots. Use closes everything and the boy hands the item over
    /// (startFeedingItem) or, in a fight, throws it (startThrowingItem).
    /// </summary>
    public sealed class InventoryView : UiBoxPage
    {
        public readonly UiBox Top = new UiBox { Id = 0 }, Bottom = new UiBox { Id = 1 }, Options = new UiBox { Id = 2 }, Third = new UiBox { Id = 3 };
        public int Pointer, Offset;           // INVENTORY_POINTER, INVENTORY_TYPE_OFFSET
        public int Option, Sort, DropPick;    // ITEM_MENU_SELECTED, SORT_SELECTED, DROP_ITEM_SELECTED_OPTION
        public int MoveSource = -1;           // ITEM_MOVE_SOURCE while UI box 0 is in its move state (4)
        public bool ThirdIsSort;              // box 3 holds the sort choice (else the drop question)
        public bool InBattle;                 // GAME_STATE 1
        public BattleArt Art;                 // the digit sheet of setEntityTextDigit (renderItemAmount)

        ScriptState _s;
        List<ItemEntry> _items;
        ItemIcons _icons;
        string[] _labels = new string[4], _sorts = new string[3];
        string _yes, _no, _sure;
        readonly byte[] _order = new byte[18];
        float _acc;

        public static InventoryView Load(DiscImage disc, GameFont font, ScriptState state, List<ItemEntry> items, ItemIcons icons)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new InventoryView { _s = state, _items = items, _icons = icons };
            v.Init(disc, font);
            for (int i = 0; i < 4; i++) v._labels[i] = v.Str((uint)(0x8012A9D8 + 8 * i));      // ITEM_MENU_LABELS: 4 strings of 8 bytes
            v._sorts[0] = v.Str(0x801343BC); v._sorts[1] = v.Str(0x801343C4); v._sorts[2] = v.Str(0x801343CC);
            v._yes = v.Str(0x801343D4); v._no = v.Str(0x801343D8); v._sure = v.Str(0x8012A9B4);
            for (int i = 0; i < 18; i++) v._order[i] = v.Exe[v.At(0x8012A9C4) + i];
            return v;
        }

        // ITEM_PARA (0x801269DC, 32 bytes): +26 sortingValue, +28 itemColor, +29 droppable
        int SortingValue(int id) { return S16(At(0x801269DC) + 32 * id + 26); }
        int ItemColor(int id) { return Exe[At(0x801269DC) + 32 * id + 28]; }
        bool Droppable(int id) { return Exe[At(0x801269DC) + 32 * id + 29] == 1; }

        public bool Showing { get { return Top.State != 0 || Bottom.State != 0 || Options.State != 0 || Third.State != 0; } }
        int Size { get { return Mathf.Clamp(_s.ItemSlots > 0 ? _s.ItemSlots : 10, 10, 30); } }
        int Visible { get { return Size == 10 ? 5 : 9; } }
        public int SelectedItem { get { return Pointer < Size ? _s.Slots[Pointer] : 0xFF; } }

        /// <summary>createInventoryView: the list box grows out of the boy; the description box follows once it is open.</summary>
        public void Open(RectInt boy)
        {
            _s.SyncSlots();
            Pointer = 0; Offset = 0; MoveSource = -1;
            CloseNow(Top); CloseNow(Bottom); CloseNow(Options); CloseNow(Third);
            Open(Top, new RectInt(-0x98, -0x68, 0x130, Size == 10 ? 0x6E : 0xB6), boy, 0x20);
            Top.Color = 0;
        }

        /// <summary>closeInventoryBoxes2: the description box shrinks into the item's picture, the list into the boy.</summary>
        public void Shut(RectInt boy)
        {
            if (Bottom.State != 0) { Bottom.Origin = new RectInt(Top.Rect.x + ((Pointer & 1) != 0 ? 0x9A : 0x0A), Top.Rect.y + 8 + (Pointer / 2 - Offset) * 0x12, 0x10, 0x10); Close(Bottom); }
            if (Top.State != 0) { Top.Origin = boy; Close(Top); }
            if (Options.State != 0) CloseNow(Options);
            if (Third.State != 0) CloseNow(Third);
        }

        public bool Ready { get { return Top.State == 1 && Bottom.State == 1; } }
        public bool Moving { get { return MoveSource >= 0; } }

        // tickInventoryTop: Up / Down two slots, Left / Right the other column (sound 2); the list scrolls a row at a time
        public void MoveCursor(int step, int side)
        {
            if (step < 0 && Pointer >= 2) { Snd(2); Pointer -= 2; }
            if (step > 0 && Pointer < Size - 2) { Snd(2); Pointer += 2; }
            if (side < 0 && (Pointer & 1) != 0) { Snd(2); Pointer--; }
            if (side > 0 && (Pointer & 1) == 0) { Snd(2); Pointer++; }
            if (Pointer < Offset * 2) Offset--;
            if (Pointer > (Offset + Visible - 1) * 2 + 1) Offset++;
        }

        // tickInventoryOptionSelector
        public void MoveOption(int step, ref int cursor, int max)
        {
            if (step < 0 && cursor != 0) { Snd(2); cursor--; }
            if (step > 0 && cursor < max - 1) { Snd(2); cursor++; }
        }

        // tickDropItemConfirmBox: Left = Yes, Right = No
        public void MoveDrop(int side)
        {
            if (side < 0 && DropPick != 0) { Snd(2); DropPick--; }
            if (side > 0 && DropPick == 0) { Snd(2); DropPick++; }
        }

        /// <summary>getItemOptionColor: 9 can be done, 0x0A cannot.</summary>
        public bool OptionUsable(int mode)
        {
            int t = SelectedItem;
            if (mode == 1 || mode == 2) return true;
            if (t == 0xFF) return false;
            int c = ItemColor(t);
            if (mode == 0) return InBattle ? (c == 1 || c == 2) : (c == 0 || c == 2);
            return !InBattle && Droppable(t);
        }

        /// <summary>createItemOption: box 2 at the item's column, 0x3A x 0x54, growing out of its row.</summary>
        public void OpenOptions()
        {
            Option = 0;
            int x = Top.Rect.x + ((Pointer & 1) != 0 ? 0x9A : 0x0A);
            int row = Pointer / 2 - Offset;
            int y = row < 5 ? Top.Rect.y + 0x18 + row * 0x12 : Top.Rect.y + 0x20 + (row - 6) * 0x12;
            Open(Options, new RectInt(x, y, 0x3A, 0x54), new RectInt(x - 3, Top.Rect.y + 7 + row * 0x12, 0x8A, 0x12), 0x00);
            Options.Color = 1;
        }

        /// <summary>createSortOption / createDropItemConfirmBox: box 3 grows out of the chosen option.</summary>
        public void OpenThird(bool sort)
        {
            ThirdIsSort = sort;
            var from = new RectInt(Options.Rect.x + 9, Options.Rect.y + 6 + Option * 0x12, 0x28, 0x10);
            if (sort) { Sort = 0; Open(Third, new RectInt(Options.Rect.x + Options.Rect.width, Options.Rect.y, 0x48, 0x42), from, 0x00); }
            else { DropPick = 1; Open(Third, new RectInt(-0x40, -0x26, 0x80, 0x35), from, 0x00); }
            Third.Color = 1;
        }

        /// <summary>moveItem: swaps the marked slot and the one under the cursor.</summary>
        public void SwapMoved()
        {
            if (MoveSource < 0) return;
            byte a = _s.Slots[Pointer]; _s.Slots[Pointer] = _s.Slots[MoveSource]; _s.Slots[MoveSource] = a;
            MoveSource = -1;
        }

        /// <summary>sortItems(mode 11 + n): the items by ITEM_PARA.sortingValue in the order ITEM_SORT_ORDER[n] gives, each group by item number.</summary>
        public void SortBy(int n)
        {
            int size = Size;
            var groups = new List<int>[6];
            for (int g = 0; g < 6; g++)
            {
                groups[g] = new List<int>();
                for (int j = 0; j < size; j++) if (_s.Slots[j] != 0xFF && SortingValue(_s.Slots[j]) == g) groups[g].Add(_s.Slots[j]);
                groups[g].Sort();
            }
            var want = new List<int>();
            for (int j = 0; j < 6; j++) { int g = _order[n * 6 + j]; if (g < 6) want.AddRange(groups[g]); }
            for (int i = 0; i < size; i++) _s.Slots[i] = i < want.Count ? (byte)want[i] : (byte)0xFF;
        }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f)
                {
                    _acc -= 0.05f;
                    Step(Top); Step(Bottom); Step(Options); Step(Third);
                    // createInventoryView: the description box grows out of the list's first picture once the list is open
                    if (Top.State == 1 && Bottom.State == 0 && Bottom.Frame == 0)
                    {
                        Open(Bottom, new RectInt(-0x98, 0x4D, 0x130, 0x1C), new RectInt(Top.Rect.x + 8, Top.Rect.y + 0x0E, 0x10, 0x10), 0x20);
                        Bottom.Color = 0;
                    }
                }
            }
            BuildText();
            Begin();
            DrawBox(Top, DrawTop);
            DrawBox(Bottom, DrawBottom);
            DrawBox(Options, DrawOptions);
            DrawBox(Third, DrawThird);
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // the text page (drawInventoryText): slot n's name at ((n & 1) x 0x60, (n / 2) x 12); the labels at x 0xC0 rows 0-3; the sort words at 0xC0 rows
        // 0x30 / 0x3C / 0x48; Yes at (0xC5, 0x54), No at (0xC0, 0x60); the description at (0, 0xB4); "Are you sure?" at (0, 0xC0)
        void BuildText()
        {
            int t = SelectedItem;
            var key = new System.Text.StringBuilder();
            for (int i = 0; i < Size; i++) key.Append(_s.Slots[i]).Append(',');
            key.Append('|').Append(t);
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                for (int i = 0; i < Size; i++)
                {
                    int id = _s.Slots[i];
                    if (id == 0xFF || id >= _items.Count) continue;
                    Font.Render(_items[id].Name, px, 256, 256, (i & 1) * 0x60, (i / 2) * 12);
                }
                for (int i = 0; i < 4; i++) Font.Render(_labels[i] ?? "", px, 256, 256, 0xC0, i * 12);
                for (int i = 0; i < 3; i++) Font.Render(_sorts[i] ?? "", px, 256, 256, 0xC0, 0x30 + i * 12);
                Font.Render(_yes ?? "", px, 256, 256, 0xC5, 0x54);
                Font.Render(_no ?? "", px, 256, 256, 0xC0, 0x60);
                if (t != 0xFF && t < _items.Count) Font.Render(_items[t].Description ?? "", px, 256, 256, 0, 0xB4);
                Font.Render(_sure ?? "", px, 256, 256, 0, 0xC0);
            });
        }

        // renderString(colour, x, y, w, 12, u, v, layer, 1): the page slice in TEXT_COLORS[colour] over a black copy 1 pixel down and right
        void Text(int z, int colour, int x, int y, int w, int u, int v)
        {
            // the same as PsPage.Words (slice, then its black copy), so a Chinese slice replaces the English in both (ours, translation)
            Words(z, colour, x, y, w, u, v);
        }

        // renderInventoryTop
        void DrawTop()
        {
            int z = 6 - Top.Id, x = Top.Rect.x, y = Top.Rect.y;
            int count = Size == 10 ? 10 : 18, slot = Offset * 2;
            for (int i = 0; i < count && slot < Size; i++, slot++)
            {
                int id = _s.Slots[slot];
                if (id == 0xFF || id >= _items.Count) continue;
                int row = i / 2, left = (i & 1) != 0 ? x + 0x9A : x + 0x0A;
                if (_icons != null)
                {
                    var uv = _icons.Uv(id); var sheet = _icons.Sheet; int ix = left, iy = y + 8 + row * 0x12;
                    Add(z, () => GUI.DrawTextureWithTexCoords(new Rect(X(ix), Y(iy), 16 * 4.42f, 16 * 4.42f), sheet, uv));
                }
                int c = ItemColor(id);
                Text(z, c == 0xFF ? 8 : c + 5, (i & 1) != 0 ? x + 0xAD : x + 0x1D, y + 0x0A + row * 0x12, 0x60, (slot & 1) * 0x60, (slot / 2) * 12);
                Amount(z, (i & 1) != 0 ? x + 0x113 : x + 0x83, y + 0x0C + row * 0x12, 2, _s.Bag[id]);
            }
            if (MoveSource >= 0)
            {
                int row = MoveSource / 2 - Offset;
                if (row >= 0 && row < Visible)
                {
                    int mx = Top.Rect.x + ((MoveSource & 1) != 0 ? 0x97 : 7), my = Top.Rect.y + 7 + row * 0x12;
                    Color c = Bgr(0x5D4AF1);
                    Line3(z, c, mx, my + 0x12, mx, my, mx + 0x8A, my);
                    Line3(z, c, mx + 0x8A, my, mx + 0x8A, my + 0x12, mx, my + 0x12);
                }
            }
            SelectionCursor(z, Top.Rect.x + ((Pointer & 1) != 0 ? 0x97 : 7), Top.Rect.y + 7 + (Pointer / 2 - Offset) * 0x12, 0x8A, 0x12);
        }

        // renderItemAmount (utils2.c): n places, right-aligned, 6 apart; each digit 6 x 10 at u 0x89 + 6 x digit, v 0x9C of the setEntityTextDigit page
        // (the battle digit sheet, colour set 4), colour 0
        void Amount(int z, int x, int y, int n, int value)
        {
            if (Art == null) { Number(z, 0, x, y, n, value); return; }
            string s = Mathf.Clamp(value, 0, 99).ToString();
            if (s.Length > n) s = s.Substring(s.Length - n);
            var art = Art;
            for (int k = 0; k < s.Length; k++)
            {
                int d = s[k] - '0', dx = x + (n - s.Length + k) * 6;
                Add(z, () => art.Hud(4, d * 6 + 0x89, 0x9C - 128, 6, 10, dx, y));
            }
        }

        // renderInventoryBottom: the description, text colour 9
        void DrawBottom()
        {
            if (SelectedItem == 0xFF) return;
            Text(6 - Bottom.Id, 9, Bottom.Rect.x + 0x1A, Bottom.Rect.y + 8, 0xFC, 0, 0xB4);
        }

        // renderItemOption
        void DrawOptions()
        {
            int z = 6 - Options.Id, x = Options.Rect.x + 9;
            for (int i = 0; i < 4; i++) Text(z, OptionUsable(i) ? 9 : 0x0A, x + 3, Options.Rect.y + 8 + i * 0x12, 0x24, 0xC0, i * 12);
            SelectionCursor(z, x, Options.Rect.y + 6 + Option * 0x12, 0x28, 0x10);
        }

        // renderSortOption / renderDropItemConfirmBox
        void DrawThird()
        {
            int z = 6 - Third.Id;
            if (ThirdIsSort)
            {
                int x = Third.Rect.x + 9;
                for (int i = 0; i < 3; i++) Text(z, 9, x + 3, Third.Rect.y + 8 + i * 0x12, 0x30, 0xC0, 0x30 + i * 12);
                SelectionCursor(z, x, Third.Rect.y + 6 + Sort * 0x12, 0x36, 0x10);
                return;
            }
            int y = Third.Rect.y + 0x1E;
            Text(z, 9, -0x34, -0x1E, 0x6C, 0, 0xC0);
            for (int i = 0; i < 2; i++) Text(z, 9, Third.Rect.x + 0x12 + i * 0x34 + 5, y + 2, 0x24, 0xC0, 0x54 + i * 12);
            SelectionCursor(z, Third.Rect.x + 0x12 + DropPick * 0x35, y, 0x2A, 0x10);
        }
    }
}
