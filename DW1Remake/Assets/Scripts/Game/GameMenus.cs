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
        public System.Action<int> Sfx;                   // playSound(0, n): 2 cursor, 3 confirm, 4 back
        public TriangleMenuView Tri;                     // the game's own Triangle menu (null: the old drawing)
        public InventoryView Inv;                        // the game's own bag (null: the old drawing)
        bool _invOpened;
        public System.Func<RectInt> BoyOrigin;           // the boy's 10 x 10 square on screen (getEntityScreenPos), where the boxes grow from
        public int Year = 1, DayOfYear = 1;              // YEAR + 1, DAY + 1
        int _nameStage;
        string _input = "";

        /// <summary>The name screens at the start of a new game: first the boy, then the partner. Calls OnNameDone when finished.</summary>
        public void OpenNameEntry() { _mode = Mode.Name; _nameStage = 0; _input = PlayerName; }

        public GameMenus(ScriptState state, List<ItemEntry> items) { _s = state; _items = items; }

        public bool Active { get { return _mode != Mode.None; } }

        public void OpenMain() { _mode = Mode.Main; _back = Mode.None; _sel = 0; if (Tri != null) { Tri.CloseNow(Tri.Box); Tri.Selection = 1; } }
        public void OpenBag() { _mode = Mode.Bag; _back = Mode.None; _sel = 0; _msg = ""; _pick = null; }

        // script built-in 0 (openDiscardItem, item menu type 5): the bag opens to choose one item; Cross gives its number, back gives -1
        Action<int> _pick;
        public void OpenPick(Action<int> pick) { _mode = Mode.Bag; _back = Mode.None; _sel = 0; _msg = ""; _pick = pick; }
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

        void GoBack()
        {
            _mode = _back == Mode.Main ? Mode.Main : Mode.None; _back = Mode.None; _msg = "";
            if (_pick != null) { var p = _pick; _pick = null; p(-1); }
        }

        /// <summary>One frame of input. step = up/down, side = left/right, action = Cross, cancel = Triangle/back.</summary>
        public void Tick(int step, int side, bool action, bool cancel)
        {
            if (_mode == Mode.Main && Tri != null) { TickTri(step, side, action, cancel); return; }
            if (_mode == Mode.Bag && Inv != null && _pick == null) { TickInv(step, side, action, cancel); return; }
            if (_mode == Mode.Player && PlayerMenu != null) { TickPlayer(step, side, action, cancel); return; }
            if (_mode == Mode.Digimon && DigimonMenu != null && PartnerInfo != null) { TickDigimon(step, side, action, cancel); return; }
            if (Sfx != null && _mode != Mode.Name) { if (cancel) Sfx(4); else if (action) Sfx(3); else if (step != 0 || side != 0) Sfx(2); }
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
                    if (_mode == Mode.Digimon && _tab == 1 && _techIn) { TickTech(step, side, action, cancel); return; }
                    if (cancel) { _mode = Mode.Main; return; }
                    // tickDigimonMenu: Left / Right change the tab, Cross on the Tech tab puts the cursor in the grid (MENU_STATE 6)
                    if (_mode == Mode.Digimon && _tab == 1 && action) { _techIn = true; Snd(3); return; }
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
                if (_pick != null) { var p = _pick; _pick = null; _mode = Mode.None; p(id); return; }
                if (InBattle())
                {
                    // inventory action 5 in a fight: the page closes and startThrowingItem takes the item out of the bag; it works when it lands
                    if (!CanApply(id)) { _msg = "Can't use " + it.Name + " yet."; return; }
                    if (ThrowBusy != null && ThrowBusy()) return;
                    _s.RemoveItem(id, 1);
                    LastUsedItem = id;
                    _mode = Mode.None;
                }
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

        // tickTriangleMenu / tickGameMenu with the game's menu: the box grows out of the boy each time the menu comes back (state 0); Cross works once it is
        // open (TRIANGLE_MENU_STATE -1), sound 3 or 4 for a disabled option; Circle / Triangle closes it (sound 4) once it is open or gone
        void TickTri(int step, int side, bool action, bool cancel)
        {
            var o = BoyOrigin != null ? BoyOrigin() : new RectInt(-5, -5, 10, 10);
            if (!Tri.Showing) Tri.Open(o, CanSleep == null || CanSleep(), Tri.Selection);
            Tri.Year = Year; Tri.Day = DayOfYear;
            Tri.Move(step, side);
            if (!Tri.Ready) return;
            if (action)
            {
                int sel = Tri.Selection;
                if (Sfx != null) Sfx(Tri.Disabled[sel] ? 4 : 3);
                if (Tri.Disabled[sel]) return;
                Tri.Shut(o);
                // handleGameMenuSelection: 1 Item, 2 Digimon, 3 Player, 4 Praise (partner state 0xF), 5 Scold (4), 6 Sleep (3)
                switch (sel)
                {
                    case 1: _mode = Mode.Bag; _back = Mode.Main; _sel = 0; _msg = ""; break;
                    case 2: _mode = Mode.Digimon; _tab = 0; _techIn = false; _digimonOpened = false; break;
                    case 3: _mode = Mode.Player; _tab = 0; _playerOpened = false; break;
                    case 4: _mode = Mode.None; OnCommand(15); break;
                    case 5: _mode = Mode.None; OnCommand(4); break;
                    case 6: _mode = Mode.None; OnCommand(3); break;
                }
                return;
            }
            if (cancel) { if (Sfx != null) Sfx(4); Tri.Shut(o); _mode = Mode.None; }
        }

        // tickInventoryUI: Cross / Circle go to box 3 (sort or drop) if it is open, else to box 2 (Use, Move, Sort, Drop), else to the list
        void TickInv(int step, int side, bool action, bool cancel)
        {
            var o = BoyOrigin != null ? BoyOrigin() : new RectInt(-5, -5, 10, 10);
            if (!_invOpened) { Inv.InBattle = InBattle != null && InBattle(); Inv.Open(o); _invOpened = true; }
            var v = Inv;
            if (v.Third.State == 1)
            {
                if (v.ThirdIsSort)
                {
                    v.MoveOption(step, ref v.Sort, 3);
                    // selectSorting, then actions 11-13 close box 3 and box 2
                    if (action) { v.SortBy(v.Sort); v.Close(v.Third); v.Close(v.Options); }
                    else if (cancel) v.Close(v.Third);
                }
                else
                {
                    v.MoveDrop(side);
                    if (action && v.DropPick == 0)
                    {
                        // action 7: both boxes close, then every one of the item goes
                        int id = v.SelectedItem;
                        v.Close(v.Third); v.Close(v.Options);
                        if (id != 0xFF) { _s.RemoveItem(id, _s.Bag[id]); _s.SyncSlots(); }
                    }
                    else if (action || cancel) v.Close(v.Third);
                }
                return;
            }
            if (v.Options.State == 1)
            {
                v.MoveOption(step, ref v.Option, 4);
                if (cancel) { v.Close(v.Options); return; }
                if (!action) return;
                switch (v.Option)
                {
                    case 0:
                        if (!v.OptionUsable(0)) return;
                        UseItem(v.SelectedItem, o);
                        return;
                    case 1: v.MoveSource = v.Pointer; v.Close(v.Options); return;     // UI box 0 state 4
                    case 2: v.OpenThird(true); return;
                    case 3: if (v.OptionUsable(3)) v.OpenThird(false); return;
                }
                return;
            }
            if (v.Options.State != 0 || v.Third.State != 0 || !v.Ready) return;
            v.MoveCursor(step, side);
            if (action)
            {
                // selectRegularInventory: in the move state Cross swaps (sound 4), otherwise it opens box 2
                if (v.Moving) { if (Sfx != null) Sfx(4); v.SwapMoved(); }
                else v.OpenOptions();
                return;
            }
            if (cancel)
            {
                if (v.Moving) { if (Sfx != null) Sfx(3); v.MoveSource = -1; return; }
                // action 2: the boxes close and the Triangle menu comes back (in a fight the page just closes)
                v.Shut(o); _invOpened = false;
                _mode = _back == Mode.Main && !v.InBattle ? Mode.Main : Mode.None; _back = Mode.None;
            }
        }

        // Triangle menu states 5 and 6 with tickPlayerMenu: box 1 grows out of the boy once the Triangle menu's box has gone; Triangle / Circle on a page
        // closes it, and the Triangle menu comes back once it has shrunk away
        bool _playerOpened;
        PlayerMenuView _playerMenu; bool _playerMenuTried;
        PlayerMenuView PlayerMenu
        {
            get
            {
                if (!_playerMenuTried && Disc != null && Battle.StatFont != null)
                {
                    _playerMenuTried = true;
                    try { _playerMenu = PlayerMenuView.Load(Disc, Battle.StatFont); } catch (Exception e) { Debug.LogWarning("[menu] player menu: " + e.Message); }
                    if (_playerMenu != null) _playerMenu.Sound = n => { if (Sfx != null) Sfx(n); };
                }
                return _playerMenu;
            }
        }

        void TickPlayer(int step, int side, bool action, bool cancel)
        {
            var o = BoyOrigin != null ? BoyOrigin() : new RectInt(-5, -5, 10, 10);
            var v = PlayerMenu;
            if (!_playerOpened)
            {
                if (Tri != null && Tri.Showing) return;
                v.Open(o); _playerOpened = true;
                return;
            }
            if (!v.Showing) { _playerOpened = false; _mode = Tri != null ? Mode.Main : Mode.None; return; }
            if (!v.Ready) return;
            if (v.Tick(_s, step, side, action, cancel)) v.Shut(o);
        }

        /// <summary>The Player and Digimon menus hide the boy and his partner while they are open (tickPlayerMenu / tickDigimonMenu clear their isOnScreen).</summary>
        public bool HidesActors
        {
            get
            {
                if (_mode == Mode.Player) return _playerMenu != null && _playerOpened && _playerMenu.Showing && !_playerMenu.Closing;
                if (_mode == Mode.Digimon) return _digimonMenu != null && _digimonOpened && _digimonMenu.Showing && !_digimonMenu.Closing;
                return false;
            }
        }

        // Triangle menu states 3 and 4 with tickDigimonMenu, the same way as the Player menu
        bool _digimonOpened;
        DigimonMenuView _digimonMenu; bool _digimonMenuTried;
        DigimonMenuView DigimonMenu
        {
            get
            {
                if (!_digimonMenuTried && Disc != null && Battle.StatFont != null)
                {
                    _digimonMenuTried = true;
                    try { _digimonMenu = DigimonMenuView.Load(Disc, Battle.StatFont); } catch (Exception e) { Debug.LogWarning("[menu] digimon menu: " + e.Message); }
                    if (_digimonMenu != null) _digimonMenu.Sound = n => { if (Sfx != null) Sfx(n); };
                }
                return _digimonMenu;
            }
        }

        void TickDigimon(int step, int side, bool action, bool cancel)
        {
            var o = BoyOrigin != null ? BoyOrigin() : new RectInt(-5, -5, 10, 10);
            var v = DigimonMenu;
            if (!_digimonOpened)
            {
                if (Tri != null && Tri.Showing) return;
                v.Open(o); _digimonOpened = true;
                return;
            }
            if (!v.Showing) { _digimonOpened = false; _mode = Tri != null ? Mode.Main : Mode.None; return; }
            if (!v.Ready) return;
            // Square (X) is also one of our cancel keys: here it has its own job (the help page)
            bool square = SquareKey();
            if (square) cancel = false;
            ThreeSlots();
            if (v.Tick(step, side, action, cancel, square, CanLeaveGrid, (c, r) => CrossTech(r, c))) v.Shut(o);
        }

        // tickDigimonMenu state 6, Circle: allowed with at least one technique set and one of them with power
        bool CanLeaveGrid()
        {
            bool any = false, power = false;
            foreach (int id in _s.TechSet) if (id >= 0) { any = true; if (Battle.MakeTech(id, false).Power != 0) power = true; }
            return any && power;
        }

        // Cross in the grid: getEquippedSlot takes a set technique out, else equipMove
        void CrossTech(int row, int col)
        {
            int id = TechAt(row, col);
            ThreeSlots();
            int slot = EquippedSlot(id);
            if (slot >= 0) { _s.TechSet[slot] = -1; Snd(3); }
            else EquipMove(id);
        }

        // action 5: the boxes close; then startFeedingItem (the boy hands it over) or, in a fight, startThrowingItem
        void UseItem(int id, RectInt boy)
        {
            if (id == 0xFF) return;
            Inv.Shut(boy); _invOpened = false;
            if (Inv.InBattle)
            {
                if (!CanApply(id) || (ThrowBusy != null && ThrowBusy())) { _mode = Mode.None; return; }
                _s.RemoveItem(id, 1); _s.SyncSlots();
                LastUsedItem = id;
                _mode = Mode.None;
                return;
            }
            _mode = Mode.None;
            OnFeed(id);
        }

        void PickMain()
        {
            switch (_sel)
            {
                case 0: _mode = Mode.Bag; _back = Mode.Main; _sel = 0; _msg = ""; break;
                case 1: _mode = Mode.Digimon; _tab = 0; _techIn = false; _digimonOpened = false; break;
                case 2: _mode = Mode.Player; _tab = 0; _playerOpened = false; break;
                case 3: _mode = Mode.None; OnCommand(15); break;
                case 4: _mode = Mode.None; OnCommand(4); break;
                case 5: if (CanSleep()) { _mode = Mode.None; OnCommand(3); } break;     // the option is off unless it is sleepy
            }
        }

        public System.Func<bool> ThrowBusy;

        static bool CanApply(int id) { return id >= 0 && id <= 7; }

        /// <summary>The item's effect in a fight, when the thrown item reaches the partner. Only the recovery items are wired up so far (ids 0-7; amounts from their names).</summary>
        public void ApplyItem(int id)
        {
            int[] hp = { 500, 1500, 5000, 9999 };
            int[] mp = { 500, 1500, 5000 };
            if (id >= 0 && id <= 3) _s.AddStat(6, hp[id]);
            else if (id >= 4 && id <= 6) _s.AddStat(7, mp[id - 4]);
            else if (id == 7) { _s.AddStat(6, 1500); _s.AddStat(7, 1500); }
        }

        // ================================================================ drawing
        public Func<GameObject> BuildPartnerModel;      // a new copy of the partner's model (the Status page shows it)
        readonly DigiviceModel _digivice = new DigiviceModel();
        TechView _techView; bool _techTried;

        public void OnGUI()
        {
            _digivice.Hide();       // shown again below while the Status page is open
            _boyDigivice.Hide();
            if (_playerMenu != null && _mode != Mode.Player) _playerMenu.HideModels();
            if (Tri != null && Tri.Showing) Tri.Draw();      // also while it shrinks away after the menu closed
            if (Inv != null && Inv.Showing) Inv.Draw();
            if (_mode == Mode.None) return;
            UiKit.Begin();
            switch (_mode)
            {
                case Mode.Main: if (Tri == null) DrawMain(); break;
                case Mode.Bag: if (Inv == null || _pick != null) DrawBag(); break;
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
                // addGameMenu / renderRectPolyFT4: Sleep is disabled unless the partner is sleepy (condition bit 1); a disabled option is drawn at colour 0x32 of 0x80
                bool off = i == 5 && CanSleep != null && !CanSleep();
                Color tint = off ? new Color(0x32 / 128f, 0x32 / 128f, 0x32 / 128f, 1f) : Color.white;
                if (Art != null)
                {
                    var size = Art.Size(i) * 4.4f;
                    var o = GUI.color; GUI.color = tint;
                    GUI.DrawTextureWithTexCoords(new Rect(cx - size.x / 2, cy - size.y / 2, size.x, size.y), Art.Sheet(i), Art.Uv(i, i == _sel ? (int)(Time.time * 3f) % 2 : 0));
                    GUI.color = o;
                }
                UiKit.Text(new Rect(cx - 80, cy + 62, 160, 50), names[i], 34, tint, TextAnchor.MiddleCenter);
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
            var dm = PartnerInfo != null ? DigimonMenu : null;
            if (dm != null)
            {
                dm.Draw((tab, state) =>
                {
                    if (tab == 0) { if (state >= 1) DrawStatus(); }
                    else DrawTechPage(state, dm.Col, dm.Row);
                });
                return;
            }
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

        StatusView _statusView; bool _statusTried;

        void DrawStatus()
        {
            if (!_statusTried && Disc != null && Battle.StatFont != null) { _statusTried = true; try { _statusView = StatusView.Load(Disc, Battle.StatFont); } catch (Exception e) { Debug.LogWarning("[menu] status page: " + e.Message); } }
            if (_statusView != null && PartnerInfo != null)
            {
                // renderDigiviceEntity: its own ordering table sorted into the page's (GsSortOt), so the page's words lie over the model
                _digivice.Draw(Disc, PartnerInfo.Type, BuildPartnerModel);
                _statusView.Draw(_s, PartnerInfo.Type, PartnerName, Species, (int)(Time.unscaledTime * 20f));      // renderDigimonStatsView
                return;
            }
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
        bool Usable(int id) { return PartnerInfo != null && (PartnerInfo.Learnable.Contains(id) || id == 0x2C && PartnerInfo.Learnable.Contains(0x30) || id == 0x37 && PartnerInfo.Learnable.Contains(0x39)); }
        static string Range(Battle.Tech t) { return t.Self ? "Self" : t.Wide ? "W" : t.Melee ? "S" : "L"; }

        bool _techIn;      // the cursor is in the technique grid (MENU_STATE 6)
        public Action<int> Sound;     // playSound(0, n)
        void Snd(int n) { if (Sound != null) Sound(n); }

        // the Techset keeps three slots (EQUIPPED_MOVES); an emptied slot stays empty (-1) and the others do not move up
        void ThreeSlots() { while (_s.TechSet.Count < 3) _s.TechSet.Add(-1); }

        // tickDigimonMenu, MENU_STATE 6: the box moves 18 / 15 pixels and stops at the edges (8 columns, 7 rows)
        void TickTech(int step, int side, bool action, bool cancel)
        {
            if (cancel)
            {
                // the grid can be left only with at least one technique set and one of them with power
                bool any = false, power = false;
                foreach (int id in _s.TechSet) if (id >= 0) { any = true; if (Battle.MakeTech(id, false).Power != 0) power = true; }
                if (any && power) _techIn = false;
                Snd(4);
                return;
            }
            if (action)
            {
                int id = TechAt(_tr, _tc);
                ThreeSlots();
                int slot = EquippedSlot(id);
                if (slot >= 0) { _s.TechSet[slot] = -1; Snd(3); }      // getEquippedSlot: Cross on a set technique takes it out
                else EquipMove(id);
            }
            int r = Mathf.Clamp(_tr + step, 0, 6), c = Mathf.Clamp(_tc + side, 0, 7);
            if (r != _tr || c != _tc) Snd(2);
            _tr = r; _tc = c;
        }

        int EquippedSlot(int id)
        {
            for (int i = 0; i < _s.TechSet.Count; i++)
                if (_s.TechSet[i] >= 0 && (_s.TechSet[i] == id || id == 0x2C && _s.TechSet[i] == 0x30 || id == 0x37 && _s.TechSet[i] == 0x39)) return i;
            return -1;
        }

        // equipMove: it must be learned (hasMove), in this Digimon's 16 techniques (44 counts as 48 and 55 as 57 for those that have the
        // other one), and a slot must be free
        void EquipMove(int id)
        {
            if (id < 0 || !_s.Techs[id] || PartnerInfo == null) { Snd(4); return; }
            int use = -1;
            foreach (int m in PartnerInfo.Learnable)
            {
                if (id == 0x2C && m == 0x30) { use = 0x30; break; }
                if (id == 0x37 && m == 0x39) { use = 0x39; break; }
                if (m == id) { use = id; break; }
            }
            if (use < 0) { Snd(4); return; }
            int slot = _s.TechSet.IndexOf(-1);
            if (slot < 0) { Snd(4); return; }
            _s.TechSet[slot] = use;
            Snd(3);
        }

        void DrawTechPage(int state, int col, int row)
        {
            if (!_techTried && Disc != null && Battle.StatFont != null) { _techTried = true; try { _techView = TechView.Load(Disc, Battle.StatFont); } catch (Exception e) { Debug.LogWarning("[menu] tech page: " + e.Message); } }
            if (_techView == null || PartnerInfo == null) return;
            ThreeSlots();
            int[] equipped = { _s.TechSet[0], _s.TechSet[1], _s.TechSet[2], PartnerInfo.SignatureId };
            _techView.Draw(equipped, id => id >= 0 && id < _s.Techs.Length && _s.Techs[id], PartnerInfo.RawMoves, state, col, row, (int)(Time.unscaledTime * 20f));   // renderDigimonMovesView
        }

        void DrawTech()
        {
            if (!_techTried && Disc != null && Battle.StatFont != null) { _techTried = true; try { _techView = TechView.Load(Disc, Battle.StatFont); } catch (Exception e) { Debug.LogWarning("[menu] tech page: " + e.Message); } }
            if (_techView != null && PartnerInfo != null)
            {
                ThreeSlots();
                int[] equipped = { _s.TechSet[0], _s.TechSet[1], _s.TechSet[2], PartnerInfo.SignatureId };
                _techView.Draw(equipped, id => id >= 0 && id < _s.Techs.Length && _s.Techs[id], PartnerInfo.RawMoves, _techIn ? 6 : 1, _tc, _tr, (int)(Time.unscaledTime * 20f));   // renderDigimonMovesView
                return;
            }
            UiKit.Text(new Rect(95, 118, 400, 60), "Techset", 54, UiKit.Yellow);
            string[] cols = { "POWER", "MP", "RANG", "SPEC" };
            float[] cx = { 830, 1010, 1130, 1250 };
            for (int i = 0; i < 4; i++) UiKit.Text(new Rect(cx[i] - 10, 168, 150, 40), cols[i], 28, UiKit.Grey);
            for (int r = 0; r < 3; r++)
            {
                float y = 215 + r * 75;
                if (r < _s.TechSet.Count && _s.TechSet[r] >= 0)
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
                    bool usable = id >= 0 && Usable(id), mastered = usable && _s.Techs[id], current = id >= 0 && EquippedSlot(id) >= 0;
                    UiKit.Fill(cell, id < 0 ? Color.black : new Color(0.05f, 0.08f, 0.1f));
                    UiKit.Fill(new Rect(cell.x + 3, cell.y + 3, cell.width - 6, cell.height - 6), current ? new Color(0.8f, 0.3f, 0.4f) : id < 0 ? Color.black : usable && !mastered ? new Color(0.3f, 0.42f, 0.48f) : new Color(0.25f, 0.27f, 0.28f));
                    if (id >= 0 && BIcons != null)
                    {
                        // lit when learned; grey when this Digimon can still learn it; dark when it cannot
                        var o = GUI.color; GUI.color = mastered ? Color.white : usable ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.22f, 0.22f, 0.22f);
                        GUI.DrawTextureWithTexCoords(new Rect(cell.x + 8, cell.y + 7, 48, 48), BIcons.Sheet, BIcons.Uv(RowIcon[r], 1, 0));
                        GUI.color = o;
                    }
                    if (_techIn && r == _tr && c == _tc)
                    {
                        UiKit.Fill(new Rect(cell.x - 5, cell.y - 5, cell.width + 10, 5), gold); UiKit.Fill(new Rect(cell.x - 5, cell.yMax, cell.width + 10, 5), gold);
                        UiKit.Fill(new Rect(cell.x - 5, cell.y, 5, cell.height), gold); UiKit.Fill(new Rect(cell.xMax, cell.y, 5, cell.height), gold);
                    }
                }
            }
            int cur = TechAt(_tr, _tc);
            if (cur >= 0)
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
            var v = PlayerMenu;
            if (v == null) return;
            v.Draw(_s, DrawPlayerInfo);
        }

        PlayerInfoView _playerInfo; bool _playerInfoTried;
        readonly DigiviceModel _boyDigivice = new DigiviceModel();
        public Func<GameObject> BuildBoyModel;          // a copy of the boy's model (the Player page shows it in the Digivice)

        void DrawPlayerInfo()
        {
            if (!_playerInfoTried && Disc != null && Battle.StatFont != null) { _playerInfoTried = true; try { _playerInfo = PlayerInfoView.Load(Disc, Battle.StatFont); } catch (Exception e) { Debug.LogWarning("[menu] player page: " + e.Message); } }
            if (_playerInfo != null)
            {
                int raised = 0; foreach (bool r in _s.Raised) if (r) raised++;
                // renderDigiviceEntity(the boy): drawn before the page, so the words lie over it
                if (BuildBoyModel != null) _boyDigivice.Draw(Disc, 0, BuildBoyModel);
                // renderPlayerInfoView: medals counted over hasMedal(0 .. 0x11)
                int medals = 0; for (int j = 0; j < 0x12; j++) if (_s.GetFlag(0x16C + j)) medals++;
                _playerInfo.Draw(_s, PlayerName, raised, medals);
                return;
            }
            DrawPlayerInfoOld();
        }

        void DrawPlayerInfoOld()
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
        // Triangle alone (Tab, C, Esc), for screens where Square has its own job (the Item Keeper)
        public static bool TriangleOnly()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && (k.tabKey.wasPressedThisFrame || k.cKey.wasPressedThisFrame || k.escapeKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.Escape);
#else
            return false;
#endif
        }
        // Start = Enter
        public static bool StartKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
#else
            return false;
#endif
        }
        // Square = X, Circle = Z
        public static bool SquareKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.xKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.X);
#else
            return false;
#endif
        }
        public static bool CircleKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.zKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Z);
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
