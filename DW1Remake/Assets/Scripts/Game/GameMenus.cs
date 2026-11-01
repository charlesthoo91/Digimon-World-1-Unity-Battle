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
        enum Mode { None, Main, Bag, Buy, Sell, Digimon, Player, Msg }

        readonly ScriptState _s;
        readonly List<ItemEntry> _items;
        Mode _mode, _back = Mode.None;
        int _sel, _tab;
        string _msg = "";

        // filled in by the room every frame
        public string PlayerName = "A", PartnerName = "B", Species = "Agumon";
        public int Day = 1;
        public System.Action OnSleep;

        public GameMenus(ScriptState state, List<ItemEntry> items) { _s = state; _items = items; }

        public bool Active { get { return _mode != Mode.None; } }

        public void OpenMain() { _mode = Mode.Main; _back = Mode.None; _sel = 0; }
        public void OpenBag() { _mode = Mode.Bag; _back = Mode.None; _sel = 0; _msg = ""; }
        public void OpenShop() { _mode = Mode.Buy; _back = Mode.None; _sel = 0; _msg = ""; }

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
                if (_mode == Mode.Buy) { if (_s.ItemTable[i] > 0) l.Add(i); }
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
                    if (cancel) { _mode = Mode.Main; return; }
                    if (side != 0) _tab = (_tab + side + n) % n;
                    return;
                }
                case Mode.Msg:
                    if (action || cancel) { _mode = Mode.Main; }
                    return;
            }
            // bag and shop
            if (cancel) { GoBack(); return; }
            if ((_mode == Mode.Buy || _mode == Mode.Sell) && side != 0) { _mode = _mode == Mode.Buy ? Mode.Sell : Mode.Buy; _sel = 0; _msg = ""; }
            var list = Entries();
            if (list.Count == 0) { _sel = 0; return; }
            if (step != 0) _sel = (_sel + step + list.Count) % list.Count;
            _sel = Mathf.Clamp(_sel, 0, list.Count - 1);
            if (!action) return;
            int id = list[_sel];
            var it = _items[id];
            if (_mode == Mode.Bag) _msg = Use(it);
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
                case 3:   // praise (effects are placeholders until the real rules are found)
                    _s.Happiness = Mathf.Min(100, _s.Happiness + 8);
                    _msg = "You praised " + PartnerName + ". Happiness went up."; _mode = Mode.Msg; break;
                case 4:   // scold
                    _s.Discipline = Mathf.Min(100, _s.Discipline + 8);
                    _s.Happiness = Mathf.Max(0, _s.Happiness - 4);
                    _msg = "You scolded " + PartnerName + ". Discipline went up."; _mode = Mode.Msg; break;
                case 5:   // sleep
                    _s.Stats[6] = _s.Stats[4]; _s.Stats[7] = _s.Stats[5];
                    if (OnSleep != null) OnSleep();
                    _msg = PartnerName + " had a good sleep. HP and MP are full."; _mode = Mode.Msg; break;
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
                case Mode.Bag: case Mode.Buy: case Mode.Sell: DrawList(); break;
                case Mode.Digimon: DrawDigimon(); break;
                case Mode.Player: DrawPlayer(); break;
                case Mode.Msg: DrawMsg(); break;
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
            Color[] cols = { new Color(0.85f, 0.2f, 0.15f), new Color(0.9f, 0.9f, 0.85f), new Color(0.9f, 0.65f, 0.4f), new Color(0.25f, 0.25f, 0.25f), new Color(0.25f, 0.25f, 0.25f), new Color(0.2f, 0.45f, 0.3f) };
            for (int i = 0; i < 6; i++)
            {
                float cx = box.x + 130 + (i % 3) * 160f, cy = box.y + 190 + (i / 3) * 185f;
                var tile = new Rect(cx - 62, cy - 62, 124, 124);
                if (i == _sel)
                {
                    UiKit.Fill(new Rect(tile.x - 6, tile.y - 6, tile.width + 12, tile.height + 12), new Color(1f, 0.75f, 0.1f));
                    UiKit.Fill(new Rect(tile.x - 1, tile.y - 1, tile.width + 2, tile.height + 2), new Color(0.12f, 0.18f, 0.22f));
                }
                UiKit.Fill(new Rect(cx - 40, cy - 40, 80, 80), cols[i]);   // simple placeholder icon in our own art
                UiKit.Text(new Rect(cx - 80, cy + 62, 160, 50), names[i], 34, Color.white, TextAnchor.MiddleCenter);
            }
        }

        void DrawList()
        {
            var list = Entries();
            var box = new Rect(150, 120, 1115, 820);
            UiKit.Box(box, UiKit.PanelFill);
            string title = _mode == Mode.Bag ? "ITEM" : _mode == Mode.Buy ? "SHOP: BUY    (left/right = sell)" : "SHOP: SELL    (left/right = buy)";
            UiKit.Text(new Rect(box.x + 30, box.y + 14, 800, 70), title, 52, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Text(new Rect(box.x + 700, box.y + 14, 380, 70), "Bits " + _s.Money, 46, Color.white, TextAnchor.MiddleRight);
            UiKit.Text(new Rect(box.x + 30, box.y + 78, 800, 50), "Slots " + _s.BagKinds() + " / " + _s.ItemSlots, 34, UiKit.Cyan, TextAnchor.MiddleLeft);
            int first = Mathf.Max(0, Mathf.Min(_sel - 4, list.Count - 10));
            for (int i = first; i < list.Count && i < first + 10; i++)
            {
                var it = _items[list[i]];
                float y = box.y + 135 + (i - first) * 52;
                if (i == _sel) UiKit.Fill(new Rect(box.x + 20, y, box.width - 40, 50), new Color(0.25f, 0.45f, 0.6f));
                UiKit.Text(new Rect(box.x + 40, y, 600, 50), it.Name, 38, Color.white, TextAnchor.MiddleLeft);
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
            UiKit.Bar(new Rect(335, 750, 225, 48), _s.Happiness / 100f, new Color(0.9f, 0.5f, 0.75f));
            UiKit.Text(new Rect(95, 820, 260, 60), "Disc.", 44, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Bar(new Rect(335, 825, 225, 48), _s.Discipline / 100f, new Color(0.9f, 0.3f, 0.5f));
            UiKit.Text(new Rect(95, 895, 260, 60), "Vir.", 44, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Bar(new Rect(335, 900, 225, 48), 0f, new Color(0.5f, 0.8f, 0.9f));
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

        void DrawTech()
        {
            UiKit.Fill(new Rect(52, 435, 1308, 4), UiKit.Frame);
            UiKit.Fill(new Rect(52, 610, 1308, 4), UiKit.Frame);
            UiKit.Text(new Rect(80, 125, 400, 60), "Techset", 54, UiKit.Yellow);
            string[] cols = { "POWER", "MP", "RANG", "SPEC" };
            float[] cx = { 840, 1040, 1180, 1280 };
            for (int i = 0; i < 4; i++) UiKit.Text(new Rect(cx[i] - 35, 170, 150, 40), cols[i], 28, UiKit.Grey);
            for (int r = 0; r < 3; r++) UiKit.Text(new Rect(150, 215 + r * 75, 600, 60), "-", 50, UiKit.Grey);
            UiKit.Text(new Rect(80, 455, 400, 60), "Final", 54, UiKit.Yellow);
            UiKit.Text(new Rect(150, 530, 600, 60), "-", 50, UiKit.Grey);
            UiKit.Text(new Rect(130, 640, 900, 60), "X   Change Techniques", 50, Color.white);
            UiKit.Text(new Rect(130, 710, 900, 60), "^   Cancel (Tab)", 50, Color.white);
            UiKit.Text(new Rect(130, 800, 1150, 100), "(Technique data has not been decoded from the disc yet.)", 36, UiKit.Grey);
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
            // a plain stand-in figure (own art, not the game's)
            UiKit.Fill(new Rect(190, 300, 160, 150), new Color(0.85f, 0.65f, 0.5f));
            UiKit.Fill(new Rect(175, 270, 190, 70), new Color(0.35f, 0.35f, 0.38f));
            UiKit.Fill(new Rect(145, 450, 250, 250), new Color(0.9f, 0.75f, 0.15f));
            UiKit.Fill(new Rect(165, 700, 210, 220), new Color(0.3f, 0.3f, 0.5f));
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

        void DrawChart()
        {
            // the Digimon chart: one square per Digimon you can raise, grouped by stage (columns). Only the partner's square is filled in so far.
            Color[] c = { new Color(0.62f, 0.35f, 0.45f), new Color(0.40f, 0.17f, 0.34f), new Color(0.05f, 0.30f, 0.5f), new Color(0f, 0.5f, 0.25f), new Color(0.65f, 0.35f, 0.12f) };
            float[] x = { 95, 258, 420, 585, 1065 }, w = { 152, 155, 155, 470, 250 };
            for (int i = 0; i < 5; i++) UiKit.Fill(new Rect(x[i], 148, w[i], 755), c[i]);
            for (int r = 0; r < 4; r++) { UiKit.Square(new Rect(130, 163 + r * 180, 83, 88)); UiKit.Square(new Rect(293, 163 + r * 180, 83, 88)); }
            for (int r = 0; r < 9; r++) UiKit.Square(new Rect(455, 163 + r * 90, 83, 88));
            UiKit.Fill(new Rect(462, 168, 70, 75), new Color(0.95f, 0.8f, 0.1f));   // the partner's rookie square (marker)
            for (int col = 0; col < 4; col++) for (int r = 0; r < 8; r++) if (!(r == 8)) UiKit.Square(new Rect(620 + col * 106, 163 + r * 90, 83, 88));
            for (int col = 0; col < 2; col++) for (int r = 0; r < 9; r++) UiKit.Square(new Rect(1100 + col * 106, 163 + r * 90, 83, 88));
            UiKit.Text(new Rect(590, 915, 400, 70), "X  Select", 50, Color.white);
            UiKit.Text(new Rect(940, 915, 400, 70), "^  Cancel", 50, Color.white);
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
            for (int r = 0; r < 6; r++) for (int c = 0; c < 11; c++) UiKit.Square(new Rect(130 + c * 106, 200 + r * 113, 85, 88));
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
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.f5Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F5);
#else
            return false;
#endif
        }
        public static bool GiveKey()
        {
#if ENABLE_INPUT_SYSTEM
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
