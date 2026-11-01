using System;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Formats;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DW1Remake.Game
{
    /// <summary>
    /// The Triangle menu of the game: Item, Digimon, Player, Praise, Scold, Sleep, plus the shop.
    /// Layouts follow the PS1 game's screens; the numbers come from ScriptState and the disc (item names, prices, texts).
    /// Keys: Tab = Triangle (open / back), Space/Enter/Z = Cross (select), Arrows = move, X/Esc = back.
    /// </summary>
    public sealed class GameMenus
    {
        enum Mode { None, Main, Bag, Buy, Sell, Digimon, Player, Msg, Name }

        readonly ScriptState _s;
        readonly List<ItemEntry> _items;
        Mode _mode, _back = Mode.None;
        int _sel, _tab;
        string _msg = "";

        // filled in by the room every frame
        public string PlayerName { get { return _s.PlayerName; } set { _s.PlayerName = value; } }
        public string PartnerName { get { return _s.PartnerName; } set { _s.PartnerName = value; } }
        public string Species = "Agumon";
        public int Day = 1;
        public ItemIcons Icons;
        public BattleIcons BIcons;
        public Portraits Faces;
        public DigimonStats PartnerInfo;   // the partner's techniques (the Tech page greys out the ones it cannot use)
        public MenuArt Art;
        public DW1Remake.Disc.DiscImage Disc;
        public Texture BoyPicture; public System.Action NeedBoyPicture;
        int _tr, _tc;                      // cursor on the Tech page: row, column
        public int LastUsedItem = -1;   // set when a bag item was used (the battle shows it being thrown to the partner)
        public System.Action OnNameDone;
        public System.Action<int> OnCommand, OnFeed;     // 15 praise, 4 scold, 3 sleep; an item handed to the partner
        public System.Func<bool> InBattle, CanSleep;
        int _nameStage;
        string _input = "";

        /// <summary>The name screens at the start of a new game: first the boy, then the partner. Calls OnNameDone when finished.</summary>
        public void OpenNameEntry() { _mode = Mode.Name; _nameStage = 0; _input = PlayerName; }

        public GameMenus(ScriptState state, List<ItemEntry> items) { _s = state; _items = items; }

        public bool Active { get { return _mode != Mode.None; } }

        public void OpenMain() { _mode = Mode.Main; _back = Mode.None; _sel = 0; }
        public void OpenBag() { _mode = Mode.Bag; _back = Mode.None; _sel = 0; _msg = ""; }
        Func<int, bool> _stock;   // which items this shop sells (null = the development test shop: everything priced)
        /// <summary>stock(i) says whether item i is for sale. The real shops have no list: the shopkeeper's script switches on flag 0x180 + item number for each item it sells.</summary>
        public void OpenShop(Func<int, bool> stock = null) { _stock = stock; _mode = Mode.Buy; _back = Mode.None; _sel = 0; _msg = ""; }

        /// <summary>Test helper (F6): a few Bits and some items so the menus have something in them.</summary>
        public void DebugGive()
        {
            _s.Money += 1000;
            _s.AddItem(38, 3);   // Meat
            _s.AddItem(0, 2);    // small recovery
            _s.AddItem(4, 1);    // MP floppy
        }

        List<int> Entries()
        {
            var l = new List<int>();
            for (int i = 0; i < _items.Count; i++)
            {
                if (_mode == Mode.Buy) { if (_stock != null ? _stock(i) : (_items[i].Price > 0 && _items[i].Category <= 2)) l.Add(i); }
                else if (_s.Bag[i] > 0) l.Add(i);
            }
            return l;
        }

        void GoBack() { _mode = _back == Mode.Main ? Mode.Main : Mode.None; _back = Mode.None; _msg = ""; }

        /// <summary>One frame of input. step = up/down, side = left/right, action = Cross, cancel = Triangle/back.</summary>
        public void Tick(int step, int side, bool action, bool cancel)
        {
            switch (_mode)
            {
                case Mode.Main:
                    if (cancel) { _mode = Mode.None; return; }
                    if (side != 0) _sel = (_sel / 3) * 3 + ((_sel % 3) + side + 3) % 3;
                    if (step != 0) _sel = (_sel + 3) % 6;
                    if (action) PickMain();
                    return;
                case Mode.Digimon:
                case Mode.Player:
                {
                    int n = _mode == Mode.Digimon ? 2 : 4;
                    if (cancel && _treeType >= 0) { _treeType = -1; return; }
                    if (cancel) { _mode = Mode.Main; return; }
                    if (_mode == Mode.Digimon && _tab == 1) { TickTech(step, side, action); return; }
                    if (_mode == Mode.Player && _tab == 1 && TickChart(step, side, action)) return;
                    if (side != 0) _tab = (_tab + side + n) % n;
                    return;
                }
                case Mode.Msg:
                    if (action || cancel) { _mode = Mode.Main; }
                    return;
                case Mode.Name:
                    return;   // typing is handled in OnGUI
            }
            // bag and shop
            if (cancel) { GoBack(); return; }
            if ((_mode == Mode.Buy || _mode == Mode.Sell) && side != 0) { _mode = _mode == Mode.Buy ? Mode.Sell : Mode.Buy; _sel = 0; _msg = ""; }
            var list = Entries();
            if (list.Count == 0) { _sel = 0; return; }
            if (_mode == Mode.Bag)      // two columns
            {
                if (step != 0) _sel = (_sel + step * 2 + list.Count * 2) % list.Count;
                if (side != 0) _sel = (_sel + side + list.Count) % list.Count;
            }
            else if (step != 0) _sel = (_sel + step + list.Count) % list.Count;
            _sel = Mathf.Clamp(_sel, 0, list.Count - 1);
            if (!action) return;
            int id = list[_sel];
            var it = _items[id];
            if (_mode == Mode.Bag)
            {
                if (InBattle()) _msg = Use(it);
                else { _mode = Mode.None; OnFeed(id); }      // startFeedingItem: the boy hands it to the partner
                return;
            }
            else if (_mode == Mode.Buy)
            {
                if (_s.Money < it.Price) _msg = "Not enough Bits.";
                else if (!_s.AddItem(id, 1)) _msg = "Your bag is full.";
                else { _s.Money -= it.Price; _msg = "Bought " + it.Name + "."; }
            }
            else
            {
                _s.RemoveItem(id, 1);
                _s.Money += it.Price / 2;    // sell price = half (assumed, not checked against the game yet)
                _msg = "Sold " + it.Name + ".";
            }
        }

        void PickMain()
        {
            switch (_sel)
            {
                case 0: _mode = Mode.Bag; _back = Mode.Main; _sel = 0; _msg = ""; break;
                case 1: _mode = Mode.Digimon; _tab = 0; break;
                case 2: _mode = Mode.Player; _tab = 0; break;
                case 3: _mode = Mode.None; OnCommand(15); break;
                case 4: _mode = Mode.None; OnCommand(4); break;
                case 5: if (CanSleep()) { _mode = Mode.None; OnCommand(3); } break;     // the option is off unless it is sleepy
            }
        }

        string Use(ItemEntry it)
        {
            // only the recovery items are wired up so far (ids 0-7: HP and MP recovery)
            int[] hp = { 500, 1500, 5000, 9999 };
            int[] mp = { 500, 1500, 5000 };
            int id = it.Id;
            if (id >= 0 && id <= 3) _s.AddStat(6, hp[id]);
            else if (id >= 4 && id <= 6) _s.AddStat(7, mp[id - 4]);
            else if (id == 7) { _s.AddStat(6, 1500); _s.AddStat(7, 1500); }
            else return "Can't use " + it.Name + " yet.";
            _s.RemoveItem(id, 1);
            LastUsedItem = id;
            return "Used " + it.Name + ".  HP " + _s.Stats[6] + "/" + _s.Stats[4] + "  MP " + _s.Stats[7] + "/" + _s.Stats[5];
        }

        // ================================================================ drawing
        public void OnGUI()
        {
            if (_mode == Mode.None) return;
            UiKit.Begin();
            switch (_mode)
            {
                case Mode.Main: DrawMain(); break;
                case Mode.Bag: DrawBag(); break;
                case Mode.Buy: case Mode.Sell: DrawList(); break;
                case Mode.Digimon: DrawDigimon(); break;
                case Mode.Player: DrawPlayer(); break;
                case Mode.Msg: DrawMsg(); break;
                case Mode.Name: DrawName(); break;
            }
            UiKit.End();
        }

        void DrawMsg()
        {
            DrawMain();
            var r = new Rect(135, 730, 1152, 275);
            UiKit.Box(r, UiKit.PanelFill);
            UiKit.Text(new Rect(r.x + 30, r.y + 40, r.width - 60, r.height - 60), _msg, 44, Color.white, TextAnchor.UpperLeft, true);
        }

        void DrawMain()
        {
            var box = new Rect(420, 155, 575, 515);
            UiKit.Box(box, UiKit.PanelFill);
            UiKit.Fill(new Rect(box.x + 7, box.y + 100, box.width - 14, 5), UiKit.Frame);
            UiKit.Text(new Rect(box.x + 30, box.y + 14, box.width - 60, 80), "Year 1     Day " + Day, 58, UiKit.Yellow, TextAnchor.MiddleLeft);
            string[] names = { "ITEM", "DIGIMON", "PLAYER", "PRAISE", "SCOLD", "SLEEP" };
            for (int i = 0; i < 6; i++)
            {
                float cx = box.x + 130 + (i % 3) * 160f, cy = box.y + 190 + (i / 3) * 185f;
                var tile = new Rect(cx - 62, cy - 62, 124, 124);
                if (i == _sel)
                {
                    UiKit.Fill(new Rect(tile.x - 6, tile.y - 6, tile.width + 12, tile.height + 12), new Color(1f, 0.75f, 0.1f));
                    UiKit.Fill(new Rect(tile.x - 1, tile.y - 1, tile.width + 2, tile.height + 2), new Color(0.12f, 0.18f, 0.22f));
                }
                if (Art != null)
                {
                    var size = Art.Size(i) * 4.4f;
                    GUI.DrawTextureWithTexCoords(new Rect(cx - size.x / 2, cy - size.y / 2, size.x, size.y), Art.Sheet(i), Art.Uv(i, i == _sel ? (int)(Time.time * 3f) % 2 : 0));
                }
                UiKit.Text(new Rect(cx - 80, cy + 62, 160, 50), names[i], 34, Color.white, TextAnchor.MiddleCenter);
            }
        }

        void DrawName()
        {
            UiKit.Fill(new Rect(0, 0, UiKit.W, UiKit.H), new Color(0, 0, 0, 0.75f));
            var box = new Rect(250, 330, 915, 330);
            UiKit.Box(box, UiKit.PanelFill);
            UiKit.Text(new Rect(box.x + 40, box.y + 30, box.width - 80, 70), _nameStage == 0 ? "What is your name?" : "What is the name of your partner?", 54, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Fill(new Rect(box.x + 40, box.y + 125, box.width - 80, 90), new Color(0.08f, 0.12f, 0.16f));
            var st = new GUIStyle(GUI.skin.textField) { fontSize = 52, alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            st.normal.textColor = Color.white; st.focused.textColor = Color.white;
            st.normal.background = null; st.focused.background = null; st.hover.background = null;
            GUI.SetNextControlName("nameField");
            _input = GUI.TextField(new Rect(box.x + 55, box.y + 130, box.width - 110, 80), _input, 10, st);
            GUI.FocusControl("nameField");
            UiKit.Text(new Rect(box.x + 40, box.y + 240, box.width - 80, 60), "Type a name (up to 10 letters), then press Enter.", 34, UiKit.Grey, TextAnchor.MiddleLeft);
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                string n = _input.Trim();
                if (n.Length == 0) n = _nameStage == 0 ? "A" : "B";
                if (_nameStage == 0) { PlayerName = n; _nameStage = 1; _input = PartnerName; }
                else { PartnerName = n; _mode = Mode.None; if (OnNameDone != null) OnNameDone(); }
                e.Use();
            }
        }

        static void ClearBox(Rect r, Color fill)      // see-through inside, the frame all round
        {
            UiKit.Fill(r, fill);
            UiKit.Fill(new Rect(r.x, r.y, r.width, 5), UiKit.Frame); UiKit.Fill(new Rect(r.x, r.yMax - 5, r.width, 5), UiKit.Frame);
            UiKit.Fill(new Rect(r.x, r.y, 5, r.height), UiKit.Frame); UiKit.Fill(new Rect(r.xMax - 5, r.y, 5, r.height), UiKit.Frame);
        }

        // the item page of the PS1 game: a see-through window on top with two columns, the description in a window at the bottom
        void DrawBag()
        {
            var list = Entries();
            var clear = new Color(0.05f, 0.1f, 0.08f, 0.6f);
            var box = new Rect(70, 38, 1270, 490);
            ClearBox(box, clear);
            int firstRow = Mathf.Max(0, _sel / 2 - 4);
            for (int i = firstRow * 2; i < list.Count && i < firstRow * 2 + 10; i++)
            {
                var it = _items[list[i]];
                float x = box.x + 25 + (i % 2) * 620, y = box.y + 28 + (i / 2 - firstRow) * 88;
                if (i == _sel) { UiKit.Fill(new Rect(x - 3, y - 3, 586, 84), Color.white * 0.75f); UiKit.Fill(new Rect(x, y, 580, 78), new Color(0.3f, 0.32f, 0.3f, 0.95f)); }
                if (Icons != null) GUI.DrawTextureWithTexCoords(new Rect(x + 6, y + 3, 72, 72), Icons.Sheet, Icons.Uv(it.Id));
                Color c = it.TextColour == 2 ? new Color(0.25f, 1f, 0.45f) : it.TextColour == 1 ? new Color(1f, 0.25f, 0.5f) : it.TextColour == 0 ? new Color(0.25f, 0.9f, 0.9f) : Color.white;
                UiKit.Text(new Rect(x + 90, y, 400, 78), it.Name, 50, c, TextAnchor.MiddleLeft);
                UiKit.Text(new Rect(x + 490, y, 80, 78), _s.Bag[it.Id].ToString(), 50, Color.white, TextAnchor.MiddleRight);
            }
            var help = new Rect(70, 855, 1270, 115);
            ClearBox(help, clear);
            string h = _msg.Length > 0 ? _msg : list.Count > 0 ? _items[list[Mathf.Clamp(_sel, 0, list.Count - 1)]].Description : "";
            UiKit.Text(new Rect(help.x + 100, help.y + 10, help.width - 120, 95), h, 50, Color.white, TextAnchor.MiddleLeft, true);
        }

        void DrawList()
        {
            var list = Entries();
            var box = new Rect(150, 120, 1115, 820);
            UiKit.Box(box, UiKit.PanelFill);
            string title = _mode == Mode.Buy ? "SHOP: BUY    (left/right = sell)" : "SHOP: SELL    (left/right = buy)";
            UiKit.Text(new Rect(box.x + 30, box.y + 14, 800, 70), title, 52, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Text(new Rect(box.x + 700, box.y + 14, 380, 70), "Bits " + _s.Money, 46, Color.white, TextAnchor.MiddleRight);
            UiKit.Text(new Rect(box.x + 30, box.y + 78, 800, 50), "Slots " + _s.BagKinds() + " / " + _s.ItemSlots, 34, UiKit.Cyan, TextAnchor.MiddleLeft);
            int first = Mathf.Max(0, Mathf.Min(_sel - 4, list.Count - 10));
            for (int i = first; i < list.Count && i < first + 10; i++)
            {
                var it = _items[list[i]];
                float y = box.y + 135 + (i - first) * 52;
                if (i == _sel) UiKit.Fill(new Rect(box.x + 20, y, box.width - 40, 50), new Color(0.25f, 0.45f, 0.6f));
                if (Icons != null) GUI.DrawTextureWithTexCoords(new Rect(box.x + 32, y + 1, 48, 48), Icons.Sheet, Icons.Uv(it.Id));
                UiKit.Text(new Rect(box.x + 92, y, 600, 50), it.Name, 38, Color.white, TextAnchor.MiddleLeft);
                string right = _mode == Mode.Buy ? it.Price + " Bits" : "x " + _s.Bag[it.Id];
                UiKit.Text(new Rect(box.x + 700, y, 360, 50), right, 38, Color.white, TextAnchor.MiddleRight);
            }
            if (list.Count == 0) UiKit.Text(new Rect(box.x + 40, box.y + 150, 800, 60), "(nothing here)", 40, UiKit.Grey, TextAnchor.MiddleLeft);
            var help = new Rect(box.x + 20, box.y + 670, box.width - 40, 130);
            UiKit.Fill(help, new Color(0.1f, 0.15f, 0.2f));
            string h = list.Count > 0 ? _items[list[Mathf.Clamp(_sel, 0, list.Count - 1)]].Description : "";
            UiKit.Text(new Rect(help.x + 14, help.y + 6, help.width - 28, 60), h, 36, Color.white, TextAnchor.UpperLeft, true);
            if (_msg.Length > 0) UiKit.Text(new Rect(help.x + 14, help.y + 62, help.width - 28, 60), _msg, 34, UiKit.Yellow, TextAnchor.UpperLeft, true);
        }

        void DrawDigimon()
        {
            UiKit.Tab(new Rect(75, 40, 310, 72), "Status", _tab == 0);
            UiKit.Tab(new Rect(415, 40, 240, 72), "Tech", _tab == 1);
            UiKit.Box(new Rect(47, 110, 1318, 895), UiKit.PanelFill);
            if (_tab == 0) DrawStatus(); else DrawTech();
        }

        // the game's two-part bars: on the good side the good colour fills over a full bar of the low colour, on the bad side the low colour shrinks over nothing
        static void CareBar(Rect r, bool good, float t)
        {
            Color high = new Color(0.9f, 0.5f, 0.75f), low = new Color(0.35f, 0.4f, 0.8f);
            UiKit.Bar(r, good ? 1f : t, low);
            if (good) UiKit.Fill(new Rect(r.x + 3, r.y + 3, (r.width - 6) * Mathf.Clamp01(t), r.height - 6), high);
        }

        void DrawStatus()
        {
            // profile
            UiKit.Fill(new Rect(52, 440, 1308, 4), UiKit.Frame);
            UiKit.Tag(new Rect(90, 148, 235, 50), "PROFILE");
            UiKit.Text(new Rect(100, 212, 120, 70), "NAME", 40, UiKit.Cyan);
            UiKit.Value(new Rect(222, 210, 435, 68), PartnerName);
            UiKit.Text(new Rect(100, 288, 120, 70), "DIGI", 40, UiKit.Cyan);
            UiKit.Value(new Rect(222, 285, 1085, 68), Species);
            UiKit.Text(new Rect(115, 368, 110, 70), "AGE", 40, UiKit.Cyan);
            UiKit.Value(new Rect(222, 362, 115, 68), _s.Age.ToString(), 44, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(410, 368, 140, 70), "WEIGHT", 40, UiKit.Cyan);
            UiKit.Value(new Rect(540, 362, 100, 68), _s.Weight.ToString(), 44, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(745, 220, 200, 40), "SPECIAL", 28, new Color(0.3f, 0.95f, 0.3f));
            UiKit.Text(new Rect(955, 220, 160, 40), "TYPE", 28, UiKit.Yellow);
            UiKit.Text(new Rect(1085, 220, 160, 40), "ACTIVE", 28, new Color(1f, 0.3f, 0.9f));
            UiKit.Text(new Rect(1085, 288, 200, 60), "day", 44, Color.white, TextAnchor.MiddleLeft);   // (day / night activity: not wired up yet)
            // condition
            UiKit.Fill(new Rect(592, 448, 5, 552), UiKit.Frame);
            UiKit.Tag(new Rect(90, 472, 245, 50), "CONDITION");
            UiKit.Text(new Rect(150, 660, 200, 70), "Life", 50, UiKit.Yellow);
            for (int i = 0; i < 3; i++) UiKit.Fill(new Rect(335 + i * 62, 668, 50, 48), i < _s.Life ? new Color(0.95f, 0.3f, 0.45f) : UiKit.Grey);
            UiKit.Text(new Rect(95, 745, 260, 60), "HAPPINESS", 34, UiKit.Yellow, TextAnchor.MiddleLeft);
            CareBar(new Rect(335, 750, 225, 48), _s.Happiness >= 0, _s.Happiness >= 0 ? _s.Happiness / 100f : (_s.Happiness + 100) / 100f);
            UiKit.Text(new Rect(95, 820, 260, 60), "Disc.", 44, UiKit.Yellow, TextAnchor.MiddleLeft);
            CareBar(new Rect(335, 825, 225, 48), _s.Discipline >= 50, _s.Discipline >= 50 ? (_s.Discipline - 50) / 50f : _s.Discipline / 50f);
            UiKit.Text(new Rect(95, 895, 260, 60), "Vir.", 44, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Bar(new Rect(335, 900, 225, 48), _s.Para.Virus * 3 / 50f, new Color(0.5f, 0.8f, 0.9f));
            // parameter
            UiKit.Tag(new Rect(630, 472, 255, 50), "PARAMETER");
            string[] nm = { "HP", "MP", "Off", "Def", "SPEED", "BRAINS" };
            int[] cur = { _s.Stats[6], _s.Stats[7], _s.Stats[0], _s.Stats[1], _s.Stats[2], _s.Stats[3] };
            int[] max = { _s.Stats[4], _s.Stats[5], 0, 0, 0, 0 };
            for (int i = 0; i < 6; i++)
            {
                float y = 540 + i * 72;
                UiKit.Text(new Rect(685, y, 200, 70), nm[i], i < 4 ? 56 : 42, Color.white, TextAnchor.MiddleLeft);
                UiKit.Text(new Rect(860, y - 8, 160, 60), cur[i].ToString(), 50, new Color(0.9f, 0.93f, 0.95f), TextAnchor.MiddleCenter);
                float t = i < 2 ? (max[i] > 0 ? cur[i] / (float)max[i] : 0f) : cur[i] / 999f;
                float wBar = i < 2 ? 460f : 240f;
                UiKit.Bar(new Rect(860, y + 38, wBar, 26), t, UiKit.Cyan);
                if (i < 2) UiKit.Text(new Rect(1125, y - 8, 160, 60), max[i].ToString(), 50, new Color(0.9f, 0.93f, 0.95f), TextAnchor.MiddleCenter);
            }
        }

        // ---- the Tech page (layout of the PS1 game): Techset on top, 7 rows of 8 techniques below
        static readonly int[] RowStarts = { 0, 40, 8, 32, 16, 24, 49 };     // technique numbers; 48 and 57 are repeats of 44 and 55
        static readonly int[] RowCounts = { 8, 8, 8, 8, 8, 8, 8 };
        static readonly string[] RowNames = { "FIRE", "BATTLE", "AIR", "EARTH", "ICE", "MECH", "FILTH" };
        static readonly int[] RowIcon = { BattleIcons.Fire, BattleIcons.Retreat, BattleIcons.Thunder, BattleIcons.Plant, BattleIcons.Water, BattleIcons.Gear, BattleIcons.Poop };

        int TechAt(int r, int c) { return c < RowCounts[r] ? RowStarts[r] + c : -1; }
        bool Usable(int id) { return PartnerInfo != null && PartnerInfo.Learnable.Contains(id); }
        static string Range(Battle.Tech t) { return t.Self ? "Self" : t.Wide ? "W" : t.Melee ? "S" : "L"; }

        void TickTech(int step, int side, bool action)
        {
            if (side < 0 && _tc == 0) { _tab = 0; return; }              // left of the first column: back to the Status tab
            if (side != 0) _tc = Mathf.Clamp(_tc + side, 0, 7);
            if (step != 0) _tr = (_tr + step + 7) % 7;
            if (!action) return;
            int id = TechAt(_tr, _tc);
            if (id < 0 || !Usable(id) || !_s.Techs[id]) return;          // only mastered techniques can be put in the Techset
            if (_s.TechSet.Contains(id)) _s.TechSet.Remove(id);
            else if (_s.TechSet.Count < 3) _s.TechSet.Add(id);
        }

        void DrawTech()
        {
            UiKit.Text(new Rect(95, 118, 400, 60), "Techset", 54, UiKit.Yellow);
            string[] cols = { "POWER", "MP", "RANG", "SPEC" };
            float[] cx = { 830, 1010, 1130, 1250 };
            for (int i = 0; i < 4; i++) UiKit.Text(new Rect(cx[i] - 10, 168, 150, 40), cols[i], 28, UiKit.Grey);
            for (int r = 0; r < 3; r++)
            {
                float y = 215 + r * 75;
                if (r < _s.TechSet.Count)
                {
                    int id = _s.TechSet[r]; var t = Battle.MakeTech(id, false);
                    UiKit.Text(new Rect(190, y, 650, 70), t.Name, 54, Color.white);
                    UiKit.Text(new Rect(cx[0] - 10, y, 150, 70), t.Power.ToString(), 54, Color.white);
                    UiKit.Text(new Rect(cx[1] - 10, y, 150, 70), t.Mp.ToString(), 54, Color.white);
                    UiKit.Text(new Rect(cx[2] - 10, y, 80, 70), Range(t), 54, Color.white);
                    UiKit.Text(new Rect(cx[3] - 10, y, 120, 70), t.Spec, 40, Color.white);
                }
                else UiKit.Text(new Rect(cx[0] + 90, y + 30, 500, 40), ".          .          .", 30, UiKit.Grey);
            }
            UiKit.Fill(new Rect(52, 435, 1308, 4), UiKit.Frame);
            UiKit.Text(new Rect(140, 450, 400, 60), "Techs", 54, UiKit.Yellow);
            for (int c = 0; c < 8; c++) UiKit.Text(new Rect(588 + c * 76, 450, 60, 40), (c + 1).ToString(), 28, Color.white, TextAnchor.MiddleCenter);
            var gold = new Color(1f, 0.85f, 0.1f);
            for (int r = 0; r < 7; r++)
            {
                float y = 495 + r * 70;
                UiKit.Text(new Rect(380, y, 160, 60), RowNames[r], 34, Color.white, TextAnchor.MiddleRight);
                UiKit.Text(new Rect(1180, y, 160, 60), RowNames[r], 34, Color.white, TextAnchor.MiddleLeft);
                for (int c = 0; c < 8; c++)
                {
                    var cell = new Rect(560 + c * 76, y, 64, 62);
                    int id = TechAt(r, c);
                    bool usable = id >= 0 && Usable(id), mastered = usable && _s.Techs[id], current = id >= 0 && _s.TechSet.Contains(id);
                    UiKit.Fill(cell, id < 0 ? Color.black : new Color(0.05f, 0.08f, 0.1f));
                    UiKit.Fill(new Rect(cell.x + 3, cell.y + 3, cell.width - 6, cell.height - 6), current ? new Color(0.8f, 0.3f, 0.4f) : id < 0 ? Color.black : usable && !mastered ? new Color(0.3f, 0.42f, 0.48f) : new Color(0.25f, 0.27f, 0.28f));
                    if (id >= 0 && BIcons != null)
                    {
                        // lit when learned; grey when this Digimon can still learn it; dark when it cannot
                        var o = GUI.color; GUI.color = mastered ? Color.white : usable ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.22f, 0.22f, 0.22f);
                        GUI.DrawTextureWithTexCoords(new Rect(cell.x + 8, cell.y + 7, 48, 48), BIcons.Sheet, BIcons.Uv(RowIcon[r], 1, 0));
                        GUI.color = o;
                    }
                    if (r == _tr && c == _tc)
                    {
                        UiKit.Fill(new Rect(cell.x - 5, cell.y - 5, cell.width + 10, 5), gold); UiKit.Fill(new Rect(cell.x - 5, cell.yMax, cell.width + 10, 5), gold);
                        UiKit.Fill(new Rect(cell.x - 5, cell.y, 5, cell.height), gold); UiKit.Fill(new Rect(cell.xMax, cell.y, 5, cell.height), gold);
                    }
                }
            }
            int cur = TechAt(_tr, _tc);
            if (cur >= 0 && Usable(cur))
            {
                var t = Battle.MakeTech(cur, false);
                float px = _tc < 4 ? 860 : 110;
                var box = new Rect(px, 540, 430, 440);
                UiKit.Box(box, new Color(0.18f, 0.16f, 0.5f, 0.97f));
                UiKit.Tag(new Rect(box.x + 10, box.y - 25, 200, 36), "TECH. DATA");
                UiKit.Text(new Rect(box.x + 25, box.y + 20, box.width - 40, 70), t.Name, 48, Color.white);
                string[] lab = { "POWER", "MP", "RANG", "SPEC" };
                string[] val = { t.Power.ToString(), t.Mp.ToString(), Range(t), t.Spec.Length > 0 ? t.Spec : "-" };
                for (int i = 0; i < 4; i++)
                {
                    UiKit.Text(new Rect(box.x + 25, box.y + 110 + i * 80, 160, 60), lab[i], 30, UiKit.Grey);
                    UiKit.Text(new Rect(box.x + 220, box.y + 100 + i * 80, 200, 70), val[i], 56, Color.white);
                }
            }
        }

        void DrawPlayer()
        {
            string[] labels = { "Player", "Chart", "Med.", "Card" };
            float[] tx = { 75, 415, 745, 970 }, tw = { 310, 295, 195, 195 };
            for (int i = 0; i < 4; i++) UiKit.Tab(new Rect(tx[i], 40, tw[i], 72), labels[i], _tab == i);
            UiKit.Box(new Rect(47, 110, 1318, 895), UiKit.PanelFill);
            switch (_tab)
            {
                case 0: DrawPlayerInfo(); break;
                case 1: DrawChart(); break;
                case 2: DrawMedals(); break;
                default: DrawCards(); break;
            }
        }

        void DrawPlayerInfo()
        {
            UiKit.Fill(new Rect(52, 225, 1308, 4), UiKit.Frame);
            UiKit.Fill(new Rect(505, 228, 5, 770), UiKit.Frame);
            UiKit.Fill(new Rect(510, 740, 850, 4), UiKit.Frame);
            UiKit.Text(new Rect(150, 140, 180, 80), "Name", 56, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Value(new Rect(327, 145, 335, 68), PlayerName);
            if (BoyPicture == null && NeedBoyPicture != null) NeedBoyPicture();
            if (BoyPicture != null) GUI.DrawTexture(new Rect(60, 235, 440, 690), BoyPicture, ScaleMode.ScaleToFit, true);
            UiKit.Text(new Rect(545, 270, 300, 70), "Level", 56, UiKit.Yellow);
            UiKit.Value(new Rect(900, 265, 120, 68), "0", 44, TextAnchor.MiddleCenter);
            UiKit.Value(new Rect(875, 350, 440, 68), "Amateur");
            UiKit.Text(new Rect(545, 435, 300, 70), "Raised", 56, UiKit.Yellow);
            UiKit.Value(new Rect(900, 440, 120, 68), "1", 44, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(545, 525, 300, 70), "Bits", 56, UiKit.Yellow);
            UiKit.Value(new Rect(880, 530, 335, 68), _s.Money.ToString(), 44, TextAnchor.MiddleRight);
            UiKit.Text(new Rect(545, 620, 300, 70), "Time", 56, UiKit.Yellow);
            int mins = Mathf.FloorToInt(_s.PlayMinutes);
            UiKit.Value(new Rect(880, 625, 335, 68), (mins / 60) + " : " + (mins % 60), 44, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(545, 785, 300, 70), "Items", 56, UiKit.Yellow);
            for (int i = 0; i < 4; i++) UiKit.Value(new Rect(900 + i * 88, 785, 86, 90), "");
            UiKit.Text(new Rect(545, 885, 300, 70), "Medals", 56, UiKit.Yellow);
            UiKit.Value(new Rect(900, 885, 120, 68), "0", 44, TextAnchor.MiddleCenter);
        }

        // ---- the Digimon chart. Cells by stage in the order of the PS1 page (screenshot); the Champion order is also the order of the game's evolution items.
        static readonly int[][] ChartTypes =
        {
            new[] { 1, 29, 15, 43 }, new[] { 2, 30, 16, 44 }, new[] { 3, 17, 31, 18, 45, 32, 46, 4, 57 },
            new[] { 5, 47, 34, 7, 51, 24, 23, 53 }, new[] { 9, 38, 48, 50, 25, 35, 52 }, new[] { 21, 8, 20, 33, 58, 49, 11 }, new[] { 36, 6, 37, 19, 10, 22, 39 },
            new[] { 12, 26, 41, 60, 27, 61, 28, 42 }, new[] { 40, 54, 59, 55, 13, 14, 56 }
        };
        // the picture in ETCDAT/SBOY.TIM and its colour set, by Digimon type (DW1-SydPatches DigimonData.cpp; the pictures agree with the PS1 chart screenshot)
        static readonly int[] FaceOfType = { 0, 74, 75, 66, 67, 26, 27, 28, 29, 30, 31, 61, 6, 7, 8, 76, 77, 68, 69, 32, 33, 34, 35, 36, 37, 62, 9, 10, 11, 78, 79, 70, 71, 38, 39, 40, 41, 42, 43, 63, 12, 13, 14, 80, 81, 72, 73, 44, 45, 46, 47, 48, 49, 64, 15, 16, 17, 51, 50, 1, 2, 3 };
        static readonly int[] ColourOfType = { 0, 0, 0, 0, 1, 1, 3, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 2, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 3, 0, 0, 0, 1, 2, 2, 3, 3, 3, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 4, 3 };
        static readonly float[] ChartX = { 155, 310, 465, 625, 725, 825, 925, 1085, 1185 };
        int _cc = 2, _cr, _treeType = -1;

        static float ChartY(int col, int row) { return col < 2 ? 155 + row * 172 : 155 + row * 86; }

        void Face(int type, Rect cell)
        {
            UiKit.Square(cell);
            if (Faces != null && type < FaceOfType.Length)
                GUI.DrawTextureWithTexCoords(new Rect(cell.x + 2, cell.y + 2, cell.width - 4, cell.height - 4), Faces.SheetFor(ColourOfType[type]), Faces.Uv(FaceOfType[type], (int)(Time.time * 2f) % 2));
        }

        // returns false when the input should go to the tabs (left of the first column, right of the last)
        bool TickChart(int step, int side, bool action)
        {
            if (_treeType >= 0) return true;
            if (side < 0 && _cc == 0) return false;
            if (side > 0 && _cc == ChartTypes.Length - 1) return false;
            if (side != 0) { _cc += side; }
            if (step != 0) _cr += step;
            _cr = Mathf.Clamp(_cr, 0, ChartTypes[_cc].Length - 1);
            if (action && _s.Raised[ChartTypes[_cc][_cr]]) _treeType = ChartTypes[_cc][_cr];
            return true;
        }

        static void Line(Vector2 a, Vector2 b, Color c)
        {
            float len = (b - a).magnitude, deg = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            var old = GUI.matrix;
            GUIUtility.RotateAroundPivot(deg, a);
            UiKit.Fill(new Rect(a.x, a.y - 5, len, 10), c);
            GUI.matrix = old;
        }

        void DrawChart()
        {
            if (_treeType >= 0) { DrawTree(); return; }
            Color[] c = { new Color(0.62f, 0.35f, 0.45f), new Color(0.40f, 0.17f, 0.34f), new Color(0.05f, 0.30f, 0.5f), new Color(0f, 0.5f, 0.25f), new Color(0.65f, 0.35f, 0.12f) };
            float[] x = { 122, 278, 434, 592, 1050 }, w = { 148, 148, 148, 450, 240 };
            for (int i = 0; i < 5; i++) UiKit.Fill(new Rect(x[i], 142, w[i], 720), c[i]);
            for (int col = 0; col < ChartTypes.Length; col++)
                for (int r = 0; r < ChartTypes[col].Length; r++)
                {
                    var cell = new Rect(ChartX[col], ChartY(col, r), 80, 82);
                    if (_s.Raised[ChartTypes[col][r]]) Face(ChartTypes[col][r], cell); else UiKit.Square(cell);
                }
            var cur = new Rect(ChartX[_cc], ChartY(_cc, _cr), 80, 82);
            var gold = new Color(1f, 0.95f, 0.3f);
            UiKit.Fill(new Rect(cur.x - 6, cur.y - 6, 92, 6), gold); UiKit.Fill(new Rect(cur.x - 6, cur.yMax, 92, 6), gold);
            UiKit.Fill(new Rect(cur.x - 6, cur.y, 6, 82), gold); UiKit.Fill(new Rect(cur.xMax, cur.y, 6, 82), gold);
            UiKit.Text(new Rect(600, 890, 330, 70), "X  Select", 50, Color.white);
            UiKit.Text(new Rect(950, 890, 330, 70), "^  Cancel", 50, Color.white);
        }

        // the evolution line of a Digimon: only the ones you have raised show
        void DrawTree()
        {
            var info = Disc != null ? DigimonStatsCatalog.Get(Disc, _treeType) : null;
            if (info == null) return;
            UiKit.Text(new Rect(575, 120, 260, 80), "Tree", 58, UiKit.Yellow, TextAnchor.MiddleCenter);
            var inner = new Color(0.2f, 0.3f, 0.36f);
            UiKit.Fill(new Rect(152, 160, 330, 590), inner); UiKit.Fill(new Rect(607, 350, 205, 205), inner); UiKit.Fill(new Rect(935, 155, 330, 585), inner);
            var me = new Vector2(708, 452);
            var fromList = info.EvolvesFrom.FindAll(v => _s.Raised[v]);
            for (int i = 0; i < fromList.Count; i++)
            {
                float y = 452 + (i - (fromList.Count - 1) / 2f) * 100f;
                Line(new Vector2(280, y), new Vector2(670, 452), new Color(0.95f, 0.5f, 0.05f));
                Face(fromList[i], new Rect(198, y - 41, 80, 82));
            }
            Color[] line = { new Color(1f, 0.75f, 0f), new Color(0f, 0.6f, 0f), new Color(0f, 0.6f, 0.7f), new Color(0f, 0.2f, 0.75f), new Color(0.7f, 0f, 0.65f), new Color(0.7f, 0.05f, 0.1f) };
            Vector2[] spot = { new Vector2(1020, 175), new Vector2(1095, 285), new Vector2(1170, 395), new Vector2(1170, 505), new Vector2(1095, 615), new Vector2(1020, 720) };
            for (int i = 0; i < info.EvolvesTo.Count && i < 6; i++)
            {
                if (!_s.Raised[info.EvolvesTo[i]]) continue;
                Vector2 bend = new Vector2(spot[i].x - 130f, 452 + (i - 2.5f) * 12f);
                Line(me + new Vector2(40, 0), bend, line[i]);
                Line(bend, spot[i] - new Vector2(40, 0), line[i]);
                Face(info.EvolvesTo[i], new Rect(spot[i].x - 40, spot[i].y - 41, 80, 82));
            }
            Face(_treeType, new Rect(me.x - 40, me.y - 41, 80, 82));
            UiKit.Text(new Rect(200, 755, 460, 70), info.LevelName, 52, Color.white, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(150, 840, 560, 70), info.Name, 52, Color.white, TextAnchor.MiddleCenter);
        }

        void DrawMedals()
        {
            UiKit.Text(new Rect(47, 125, 1318, 80), "Medals Chart", 56, UiKit.Yellow, TextAnchor.MiddleCenter);
            UiKit.Fill(new Rect(52, 195, 1308, 4), UiKit.Frame);
            UiKit.Fill(new Rect(1015, 198, 5, 400), UiKit.Frame);
            UiKit.Fill(new Rect(52, 595, 1308, 4), UiKit.Frame);
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 5; c++)
                {
                    var rc = new Rect(160 + c * 168, 215 + r * 113, 80, 105);
                    UiKit.Fill(new Rect(rc.x + 5, rc.y, 70, 30), new Color(0.7f, 0.1f, 0.15f));
                    UiKit.Fill(new Rect(rc.x, rc.y + 35, 80, 65), new Color(0.07f, 0.07f, 0.09f));
                }
            UiKit.Value(new Rect(275, 630, 780, 70), "");
            UiKit.Fill(new Rect(52, 905, 1308, 4), UiKit.Frame);
            UiKit.Text(new Rect(555, 925, 400, 70), "X  Select", 50, Color.white);
            UiKit.Text(new Rect(900, 925, 400, 70), "^  Cancel", 50, Color.white);
        }

        void DrawCards()
        {
            UiKit.Text(new Rect(47, 125, 1318, 80), "Card List", 56, UiKit.Yellow, TextAnchor.MiddleCenter);
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 11; c++)
                {
                    var cell = new Rect(130 + c * 106, 200 + r * 113, 85, 88);
                    UiKit.Square(cell);
                    int n = r * 11 + c;
                    if (Faces != null && n < Portraits.Count && _s.Cards[n])
                        GUI.DrawTextureWithTexCoords(new Rect(cell.x + 6, cell.y + 8, 73, 73), Faces.Sheet, Faces.Uv(n, ((int)(Time.time * 2f) + n) % 2));
                }
            UiKit.Text(new Rect(555, 880, 400, 70), "X  Select", 50, Color.white);
            UiKit.Text(new Rect(900, 880, 400, 70), "^  Cancel", 50, Color.white);
        }

        // ================================================================ keys
        /// <summary>Triangle (opens the menu / goes back).</summary>
        public static bool TriangleKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && (k.tabKey.wasPressedThisFrame || k.cKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.C);
#else
            return false;
#endif
        }
        /// <summary>R1 held (E): the boy walks instead of running.</summary>
        public static bool WalkHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.eKey.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(KeyCode.E);
#else
            return false;
#endif
        }

        /// <summary>Select (Backspace).</summary>
        public static bool SelectKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.backspaceKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Backspace);
#else
            return false;
#endif
        }

        public static bool BagKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.iKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.I);
#else
            return false;
#endif
        }
        public static bool ShopKey()
        {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            return false;   // test key: development only
#elif ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.f5Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F5);
#else
            return false;
#endif
        }
        public static bool GiveKey()
        {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            return false;   // test key: development only
#elif ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.f6Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F6);
#else
            return false;
#endif
        }
        public static bool CancelKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && (k.tabKey.wasPressedThisFrame || k.cKey.wasPressedThisFrame || k.escapeKey.wasPressedThisFrame || k.xKey.wasPressedThisFrame || k.iKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.X) || Input.GetKeyDown(KeyCode.I);
#else
            return false;
#endif
        }
        public static int SideStep()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; if (k == null) return 0;
            if (k.leftArrowKey.wasPressedThisFrame || k.aKey.wasPressedThisFrame) return -1;
            if (k.rightArrowKey.wasPressedThisFrame || k.dKey.wasPressedThisFrame) return 1;
            return 0;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) return -1;
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) return 1;
            return 0;
#else
            return 0;
#endif
        }
    }
}
