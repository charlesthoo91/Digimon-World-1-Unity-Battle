using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The Player page of the Player menu (renderPlayerInfoView, drawPlayerInfoStrings in main/game_menu.c, US build). Tables: TAMER_VIEW_PLAYER_LINES
    /// 0x80124424 (11 lines), TAMER_WINDOW_NAME_STRING 0x801244EC (11 labels: colour 3, the last four colour 0), TAMER_WINDOW_BOXES 0x80124494 (11 inset
    /// boxes), PLAYER_VIEW_LABELS 0x80124800, TAMER_LEVEL_TITLES 0x80124918, FMT_PLAYTIME 0x80134300. The four key items (flags 0x2D-0x30) are SYSTEM_W
    /// pictures (page v 0xA0, colour set 7); the boy's model stands in the Digivice (renderDigiviceEntity).
    /// </summary>
    public sealed class PlayerInfoView : PsPage
    {
        struct Txt { public int X, Y, W, U, V; }
        readonly Txt[] _names = new Txt[11];
        readonly RectInt[] _boxes = new RectInt[11];
        readonly string[] _titles = new string[11];
        string[] _labels = new string[5];
        string _fmt;

        public static PlayerInfoView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new PlayerInfoView();
            v.Init(disc, font);
            for (int i = 0; i < 11; i++) { int o = v.At(0x801244EC) + 8 * i; v._names[i] = new Txt { X = v.S16(o), Y = v.S16(o + 2), W = v.S16(o + 4), U = v.Exe[o + 6], V = v.Exe[o + 7] }; }
            for (int i = 0; i < 11; i++) { int o = v.At(0x80124494) + 8 * i; v._boxes[i] = new RectInt(v.S16(o), v.S16(o + 2), v.S16(o + 4), v.S16(o + 6)); }
            for (int i = 0; i < 11; i++) v._titles[i] = v.Str(BitConverter.ToUInt32(v.Exe, v.At(0x80124918) + 4 * i));
            for (int i = 0; i < 5; i++) v._labels[i] = v.Str(BitConverter.ToUInt32(v.Exe, v.At(0x80124800) + 4 * i));
            v._fmt = v.Str(0x80134300);
            return v;
        }

        /// <summary>PLAYER_VIEW_LABELS[0]: the four tab words in one string (Player, Chart, Med., Card), drawn at the top of the page.</summary>
        public string TabWords { get { return _labels[0]; } }

        // renderInsetBox: dark lines left and top, light ones bottom and right, then the fill
        void Inset(int x, int y, int w, int h)
        {
            x -= 0xA0; y -= 0x78;
            Color dark = new Color32(2, 2, 2, 255), light = new Color32(0x69, 0x87, 0xA0, 255);
            LineCmd(5, dark, x, y + h, x, y); LineCmd(5, dark, x, y, x + w, y);
            LineCmd(5, light, x, y + h, x + w, y + h); LineCmd(5, light, x + w, y + h, x + w, y);
            Box(5, x, y, w, h, 0x35, 0x4B, 0x5C);
        }

        // renderString(colour, x, y, w, 12, u, v, 5, 1): the page slice in TEXT_COLORS[colour] over a black copy 1 pixel down and right
        void Text(int colour, int x, int y, int w, int u, int v)
        {
            PageText(5, colour, x, y, w, 12, u, v);
            var tex = Page;
            if (tex == null || w <= 0) return;
            Rect uv = new Rect(u / 256f, 1f - (v + 12) / 256f, w / 256f, 12 / 256f);
            Add(5, () => { var o = GUI.color; GUI.color = Color.black; GUI.DrawTextureWithTexCoords(new Rect(X(x + 1), Y(y + 1), w * 4.42f, 12 * 4.42f), tex, uv); GUI.color = o; });
        }

        public void Draw(ScriptState s, string playerName, int raisedCount, int medals)
        {
            int mins = Mathf.FloorToInt(s.PlayMinutes);
            string time = (_fmt ?? "%d : %d").Replace("%d : %d", (mins / 60) + " : " + (mins % 60));
            // drawPlayerInfoStrings: the labels at rows 12 / 24 / 36, the play time at 0xE4, the name at 0x30, the 11 level titles from 0x40
            BuildPage(playerName + "|" + time, px =>
            {
                for (int i = 2; i < 5; i++) Write(px, _labels[i], 0, (i - 1) * 12);
                Write(px, time, 0, 0xE4);
                Write(px, playerName, 0, 0x30);
                for (int i = 0; i < 11; i++) Write(px, _titles[i], 0, i * 12 + 0x40);
            });
            int level = Mathf.Clamp(s.TamerLevel, 0, 10);
            Begin();
            Lines(5, 0x80124424, 11);
            for (int i = 0; i < 11; i++) { var t = _names[i]; Text(3 - (i / 7) * 3, t.X, t.Y + 1, t.W, t.U, t.V); }
            Text(0, -0x54, -0x50, 0x48, 0, 0x30);
            Number(5, 0, 0x35, -0x36, 2, level);
            // US: the title, then a piece of the "Bits ... Time" row right after it (u 0xB4, as the game has it)
            Text(0, 0x28, -0x24, 0x64, 0, level * 12 + 0x40);
            int n = (_titles[level] ?? "").Length / 2;
            Text(0, n * 12 + 0x28, -0x25, 0x30, 0xB4, 0x18);
            Number(5, 0, 0x35, -0x11, 2, raisedCount);
            Number(5, 0, 0x29, 2, 6, s.Money);
            Text(0, 0x32, 0x16, 0x4C, 0, 0xE4);
            // the key items: flags 0x2D / 0x2E side by side, 0x2F, 0x30
            for (int i = 0; i < 2; i++) if (s.GetFlag(i + 0x2D)) SysIcon(5, i * 0x15 + 0x34, 0x37, 16, 16, i * 0x10 + 0xB0, 0xA0, 7);
            if (s.GetFlag(0x2F)) SysIcon(5, 0x5E, 0x37, 16, 16, 0xE0, 0xA0, 7);
            if (s.GetFlag(0x30)) SysIcon(5, 0x73, 0x37, 16, 16, 0xD0, 0xA0, 7);
            Number(5, 0, 0x35, 0x4D, 2, medals);
            foreach (var r in _boxes) Inset(r.x, r.y, r.width, r.height);
            Flush();
        }
    }

    /// <summary>
    /// The Digimon menu round the Status and Tech pages (main/game_menu.c, US build): Triangle menu state 3 opens UI box 1
    /// (createMenuBox(1, -0x96, -0x59, 300, 0xBE), out of the boy), the two tabs (renderDigimonMenu, DIGIMON_MENU_VIEWS: words from
    /// STATUS_VIEW_LABELS[0] at the top of the text page), and tickDigimonMenu's states: 0 the page's words being written (Status 4 frames,
    /// Tech 6), 1 the page; on Tech, Cross -> 2 (box 1 shrinks 0x27 a frame) -> 3 (grows) -> 6 the grid; Circle in the grid -> 4 -> 5 -> 1;
    /// Square in the grid -> 7 (8 frames of help words) -> 8 the help page; Circle there -> 9 (the Tech words again, 6 frames, the cursor back
    /// to the top left) -> 6. The pages themselves are StatusView and TechView.
    /// </summary>
    public sealed class DigimonMenuView : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 1 };
        public int Tab;                 // DIGIMON_MENU_STATE: 0 Status, 1 Tech
        public int State;               // MENU_STATE
        public int Col, Row;            // MOVE_SELECT_BOX_X / Y as a column (0x73 + 0x12 n) and row (0x6F + 0xF n)
        int _sub;
        float _acc;
        string _tabWords;

        public static DigimonMenuView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new DigimonMenuView();
            v.Init(disc, font);
            v._tabWords = v.Labels[0];
            return v;
        }

        public bool Showing { get { return Box.State != 0; } }
        public bool Ready { get { return Box.State == 1; } }
        public bool Closing { get { return Box.State == 3; } }

        public void Open(RectInt boy)
        {
            CloseNow(Box);
            Open(Box, new RectInt(-0x96, -0x59, 300, 0xBE), boy, 0);
            Tab = 0; State = 0; _sub = 0;
        }

        public void Shut(RectInt boy) { Box.Origin = boy; Close(Box); }

        /// <summary>tickDigimonMenu, once per press. canLeaveGrid: at least one technique set and one with power. cross(col, row) in the grid
        /// equips or takes out (getEquippedSlot / equipMove, with their own sounds). Returns true when the menu should close (TRIANGLE_MENU_STATE 4).</summary>
        public bool Tick(int step, int side, bool cross, bool cancel, bool square, Func<bool> canLeaveGrid, Action<int, int> crossInGrid)
        {
            if (Tab != 1 || State == 1)
            {
                if (side > 0 && State != 0) { Tab++; if (Tab >= 2) Tab = 1; else { State = 0; _sub = 0; Snd(2); } }
                if (side < 0 && State != 0) { Tab--; if (Tab < 0) Tab = 0; else { State = 0; _sub = 0; Snd(2); } }
                if (cancel) { Snd(4); if (State == 1) return true; }
                if (Tab == 1 && cross) { if (State == 1) State = 2; Snd(3); }
            }
            else if (State == 6)
            {
                if (cancel) { if (canLeaveGrid()) State = 4; Snd(4); }
                else if (square) { State = 7; _sub = 0; Snd(3); }
                else if (cross) crossInGrid(Col, Row);
                int c = Mathf.Clamp(Col + side, 0, 7), r = Mathf.Clamp(Row + step, 0, 6);
                if (c != Col || r != Row) Snd(2);
                Col = c; Row = r;
            }
            else if (State == 8 && cancel) { State = 9; _sub = 0; Snd(4); }
            return false;
        }

        // once a game frame: the string functions and the box's height (renderDigimonStatsView / renderDigimonMovesView)
        void StepState()
        {
            switch (State)
            {
                case 0:
                    // drawDigimonStatsStrings: 4 frames; drawDigimonMovesText: 6 (its last one puts the cursor at the top left)
                    if (++_sub >= (Tab == 0 ? 4 : 6)) { State = 1; _sub = 0; if (Tab == 1) { Col = 0; Row = 0; } }
                    break;
                case 2: case 4:
                    Box.Rect.height -= 0x27;
                    if (Box.Rect.height < 0x4A) State++;
                    break;
                case 3: case 5:
                    Box.Rect.height += 0x27;
                    if (Box.Rect.height > 0xBD) State = State == 3 ? 6 : 1;
                    break;
                case 7:
                    if (++_sub >= 8) { State = 8; _sub = 0; }
                    break;
                case 9:
                    if (++_sub >= 6) { State = 6; _sub = 0; Col = 0; Row = 0; }
                    break;
            }
        }

        /// <summary>One frame of drawing (inside UiKit.Begin): box 1 and the tabs, then page(tab, state) draws the page over it.</summary>
        public void Draw(Action<int, int> page)
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); if (Box.State == 1) StepState(); }
            }
            BuildPage(_tabWords, px => Write(px, _tabWords, 0, 0));
            Begin();
            DrawBox(Box, () =>
            {
                // US: the tab words, colour 0 for the open tab and 1 for the other, over the tab pictures
                Words(5, Tab == 0 ? 0 : 1, -0x8A, -0x65, 0x3C, 0, 0);
                Words(5, Tab == 1 ? 0 : 1, -0x3F, -0x65, 0x30, 0x3C, 0);
                MenuTab(-0x91, 0x4C, Tab == 0 ? 0 : 1);
                MenuTab(-0x46, 0x40, Tab == 1 ? 0 : 1);
            });
            Flush();
            if (Box.State == 1 && page != null) page(Tab, State);
        }
    }

    /// <summary>
    /// The Player menu round its pages (main/game_menu.c, US build): UI box 1 (createMenuBox(1, -0x96, -0x59, 300, 0xBE), growing out of the boy),
    /// the four tabs (renderPlayerMenu, renderMenuTab), the input (tickPlayerMenu) and the Chart, Medals and Card pages (renderEvoChartView /
    /// renderEvoChartDetail / drawEvoChartStrings, renderMedalView / drawMedalViewStrings, renderCardsView / drawCardViewStrings / renderCardImage /
    /// renderCardCount). The Player page itself is PlayerInfoView. Tables read from the exe: EVO_CHART_BOXES 0x80124544, EVO_CHART_VIEW_COLORS
    /// 0x80123E1C, EVO_CHART_DETAIL_COLORS 0x80123E6C, CHART_FROM/TO_LINES/SPRITES 0x80124944-0x80124B20, the line colours 0x80124A84, the detail insets
    /// 0x80124B48, EVOLUTION_PATHS 0x8012B66C, CARD_SPRITE_CLUT 0x80123E28, the medal page lines 0x8012472C, PLAYER_VIEW_LABELS 0x80124800,
    /// CARD_CHART_LABELS 0x80124814, MEDAL_NAMES 0x80124828, MEDAL_DESCRIPTIONS 0x80124864. Pictures: the faces and frames are ETCDAT/ETCTIM.BIN's
    /// sixth picture (texture page 0x18, VRAM 512,256), the card pictures CARD/CARD.ALL (one TIM per 14 sectors).
    /// </summary>
    public sealed class PlayerMenuView : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 1 }, Detail = new UiBox { Id = 2 }, Count = new UiBox { Id = 3 };
        public int Tab;                 // PLAYER_MENU_STATE: 0 Player, 1 Chart, 2 Medals, 3 Card
        public int State;               // MENU_STATE: 0 writing the page's words, 1 the page, 2 a cursor in it, 3 a detail box (Chart, Card) or new words (Medals)
        int _sub;                       // MENU_SUB_STATE: how far the words are written
        int _col, _row, _digimon = -1;  // CHART_SELECTED_COLUMN / ROW / DIGIMON
        int _medal, _medalShown;        // MEDAL_SELECTOR_INDEX, SELECTED_MEDAL
        int _card;                      // SELECTED_CARD
        int _frames;                    // PLAYTIME_FRAMES for the blinking face

        struct ChartBox { public int X, Y, U, V, Clut; }
        readonly ChartBox[] _chart = new ChartBox[62];
        readonly int[] _viewClut = new int[5], _detailClut = new int[5];
        readonly int[,] _fromLines = new int[9, 8], _toLines = new int[11, 8];
        readonly Vector2Int[] _fromSpr = new Vector2Int[9], _toSpr = new Vector2Int[11];
        readonly Color[] _toColour = new Color[12];
        readonly RectInt[] _detailInsets = new RectInt[6];
        readonly int[] _cardClut = new int[66];
        readonly sbyte[] _paths = new sbyte[180 * 11];
        readonly string[] _playerLabels = new string[5], _cardLabels = new string[5], _medalNames = new string[15], _medalText = new string[45];
        TimImage _faces;
        readonly Texture2D[] _faceTex = new Texture2D[16];
        Texture2D _cardPicture; int _cardPictureId = -1;
        readonly MedalModel _medalModel = new MedalModel();
        int _medalPicture = -1;         // which medal's picture activateMedalTexture last copied into the model's block (-1: the block as loaded)
        bool _showMedal;
        float _acc;

        public static PlayerMenuView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32") || !disc.Exists("/ETCDAT/ETCTIM.BIN")) return null;
            var v = new PlayerMenuView();
            v.Init(disc, font);
            // EvoChartEntry {posX, posY, u, v, clut, pad}; the game's loops run to 0x3E, one past the 61 it holds (the 62nd is the next table's bytes)
            for (int i = 0; i < 62; i++) { int o = v.At(0x80124544) + 8 * i; v._chart[i] = new ChartBox { X = v.S16(o), Y = v.S16(o + 2), U = v.Exe[o + 4], V = v.Exe[o + 5], Clut = v.Exe[o + 6] }; }
            for (int i = 0; i < 5; i++) { v._viewClut[i] = (ushort)v.S16(v.At(0x80123E1C) + 2 * i); v._detailClut[i] = (ushort)v.S16(v.At(0x80123E6C) + 2 * i); }
            // Line4Points {x1..x4, y1..y4}: from-lines even (4) then odd (5); to-lines even (6) then odd (5)
            Action<uint, int[,], int, int> lines = (a, dst, first, n) => { for (int i = 0; i < n; i++) for (int k = 0; k < 8; k++) dst[first + i, k] = v.S16(v.At(a) + 16 * i + 2 * k); };
            lines(0x80124944, v._fromLines, 0, 4); lines(0x80124984, v._fromLines, 4, 5);
            lines(0x801249D4, v._toLines, 0, 6); lines(0x80124A34, v._toLines, 6, 5);
            // ChartSprite {posX, posY, pad, pad}
            Action<uint, Vector2Int[], int, int> sprites = (a, dst, first, n) => { for (int i = 0; i < n; i++) dst[first + i] = new Vector2Int(v.S16(v.At(a) + 8 * i), v.S16(v.At(a) + 8 * i + 2)); };
            sprites(0x80124AA8, v._fromSpr, 0, 4); sprites(0x80124AC8, v._fromSpr, 4, 5);
            sprites(0x80124AF0, v._toSpr, 0, 6); sprites(0x80124B20, v._toSpr, 6, 5);
            for (int i = 0; i < 12; i++) { int o = v.At(0x80124A84) + 3 * i; v._toColour[i] = new Color32(v.Exe[o], v.Exe[o + 1], v.Exe[o + 2], 255); }
            for (int i = 0; i < 6; i++) { int o = v.At(0x80124B48) + 8 * i; v._detailInsets[i] = new RectInt(v.S16(o), v.S16(o + 2), v.S16(o + 4), v.S16(o + 6)); }
            for (int i = 0; i < 66; i++) v._cardClut[i] = v.Exe[v.At(0x80123E28) + i];
            for (int i = 0; i < v._paths.Length && v.At(0x8012B66C) + i < v.Exe.Length; i++) v._paths[i] = (sbyte)v.Exe[v.At(0x8012B66C) + i];
            Func<uint, int, string> ptr = (a, i) => v.Str(BitConverter.ToUInt32(v.Exe, v.At(a) + 4 * i));
            for (int i = 0; i < 5; i++) { v._playerLabels[i] = ptr(0x80124800, i); v._cardLabels[i] = ptr(0x80124814, i); }
            for (int i = 0; i < 15; i++) v._medalNames[i] = ptr(0x80124828, i);
            for (int i = 0; i < 45; i++) v._medalText[i] = ptr(0x80124864, i);
            var all = TimImage.ParseAll(disc.ReadFile("/ETCDAT/ETCTIM.BIN"));
            if (all.Count > 5) v._faces = all[5];
            return v;
        }

        public bool Showing { get { return Box.State != 0; } }
        public bool Ready { get { return Box.State == 1; } }
        public bool Closing { get { return Box.State == 3; } }

        /// <summary>The Digimon under the Chart page's cursor (CHART_SELECTED_DIGIMON, cursor or detail box open), else -1. For the evolution guide (ours).</summary>
        /// <summary>The recruit tab (ours) is open and its page may be drawn.</summary>
        public bool RecruitPage { get { return Tab == 4 && State >= 1 && Box.State == 1; } }
        public int ChartDigimon { get { return Tab == 1 && State >= 2 && _digimon >= 1 && _digimon <= 62 ? _digimon : -1; } }

        /// <summary>Triangle menu state 5: box 1 grows out of the boy; the tab words go on the text page; the Player page first.</summary>
        public void Open(RectInt boy)
        {
            CloseNow(Box); CloseNow(Detail); CloseNow(Count);
            Open(Box, new RectInt(-0x96, -0x59, 300, 0xBE), boy, 0);
            Tab = 0; State = 0; _sub = 0; _digimon = -1;
        }

        /// <summary>Triangle menu state 6: box 1 shrinks back into the boy.</summary>
        public void Shut(RectInt boy) { Box.Origin = boy; Close(Box); CloseNow(Detail); CloseNow(Count); }

        static int ColumnX(int c) { return c < 3 ? c * 0x25 + 0x1C : c < 7 ? (c - 3) * 0x18 + 0x8B : (c - 7) * 0x18 + 0xF8; }

        /// <summary>tickPlayerMenu, once per press. Returns true when Triangle / Circle asks for the menu to close.</summary>
        public bool Tick(ScriptState s, int step, int side, bool cross, bool cancel)
        {
            if (State < 2)
            {
                // ours, the OPTIONS recruit tab: a fifth tab after Card
                int tabs = ModSettings.RecruitTab ? 5 : 4;
                if (side > 0) { Tab++; if (Tab >= tabs) Tab = tabs - 1; else { State = 0; _sub = 0; Snd(2); } }
                if (side < 0) { Tab--; if (Tab < 0) Tab = 0; else { State = 0; _sub = 0; Snd(2); } }
                if (cancel) { Snd(4); return true; }
                if (cross && State == 1 && Tab != 0 && Tab != 4) { State = 2; Snd(3); _medal = _medalShown = 0; _card = 0; }
                return false;
            }
            if (cancel && State == 2) { Snd(4); State = 1; _digimon = -1; }
            if (Tab == 1)
            {
                if (State == 2)
                {
                    int pc = _col, pr = _row, maxRow;
                    if (side < 0) _col--;
                    if (side > 0) _col++;
                    _col = Mathf.Clamp(_col, 0, 8);
                    if (_col < 2)
                    {
                        if (step < 0) _row -= 2;
                        if (step > 0) _row += 2;
                        _row = _row / 2 * 2;
                        maxRow = 6;
                    }
                    else
                    {
                        if (step < 0) _row--;
                        if (step > 0) _row++;
                        maxRow = _col == 2 ? 8 : (_col >= 4 && _col < 7) || _col == 8 ? 6 : 7;
                    }
                    if (_row < 0) _row = 0;
                    if (_row > maxRow) _row = maxRow;
                    if (pc != _col || pr != _row) Snd(2);
                    int sx = ColumnX(_col), i;
                    for (i = 0; i < 0x3E; i++) if (sx + 2 == _chart[i].X && _row * 0x13 + 0x2B == _chart[i].Y) break;
                    _digimon = i + 1;
                    if (cross)
                    {
                        if (Raised(s, _digimon))
                        {
                            if (Detail.State == 0)
                            {
                                Open(Detail, new RectInt(-0x96, -0x59, 0x12C, 0xBE), new RectInt(sx - 0x99, _row * 0x13 - 0x45, 10, 10), 0);
                                Snd(3);
                                State = 3;
                            }
                        }
                        else Snd(4);
                    }
                }
                else if (State == 3 && cancel)
                {
                    Detail.Origin = new RectInt(ColumnX(_col) - 0x99, _row * 0x13 - 0x45, 10, 10);
                    Close(Detail);
                    Snd(3);
                    State = 2;
                }
            }
            // US: the medal cursor also moves while the new words are being written (the other builds wait for MENU_STATE 2)
            if (Tab == 2)
            {
                if (side < 0 && _medal % 5 != 0) _medal--;
                if (side > 0 && _medal % 5 != 4) _medal++;
                if (step < 0 && _medal >= 5) _medal -= 5;
                if (step > 0 && _medal < 10) _medal += 5;
                if (_medalShown != _medal)
                {
                    // activateMedalTexture, then drawMedalViewStrings from its sub state 6: the name and text are written again
                    if (s.GetFlag(0x16C + _medal)) { _medalPicture = _medal; State = 3; _sub = 6; }
                    Snd(2);
                }
                _medalShown = _medal;
            }
            if (Tab == 3)
            {
                int prev = _card;
                if (State == 2)
                {
                    if (cross && s.CardCount[_card] != 0)
                    {
                        Snd(3);
                        State = 3;
                        var from = CardOrigin();
                        Open(Detail, new RectInt(-0x4B, -0x53, 0x96, 0xB4), from, 0);
                        Open(Count, new RectInt(0x4A, 0x45, 0x36, 0x18), from, 0);
                    }
                    if (State == 2)
                    {
                        if (side < 0 && _card % 11 != 0) _card--;
                        if (side > 0 && _card % 11 != 10) _card++;
                        if (step < 0 && _card >= 11) _card -= 11;
                        if (step > 0 && _card < 0x37) _card += 11;
                        if (prev != _card) Snd(2);
                    }
                }
                if (cancel && State == 3)
                {
                    Snd(4);
                    State = 2;
                    Detail.Origin = CardOrigin(); Count.Origin = CardOrigin();
                    Close(Detail); Close(Count);
                }
            }
            return false;
        }

        RectInt CardOrigin() { return new RectInt(_card % 11 * 0x18 - 0x79, _card / 11 * 0x18 - 0x36, 1, 1); }

        // hasDigimonRaised: trigger 0x200 + id (kept as ScriptState.Raised)
        static bool Raised(ScriptState s, int id) { return id >= 0 && id < s.Raised.Length && s.Raised[id]; }

        // the string functions write one piece of the page a frame; MENU_STATE goes to 1 (2 for new medal words) on the frame they report done
        void StepStrings()
        {
            if (Tab == 2 && State == 3)
            {
                // drawMedalViewStrings from sub state 6: clear, name, three lines; done on the fifth frame
                _sub = _sub == 6 ? 2 : _sub + 1;
                if (_sub == 6) State = 2;
                return;
            }
            if (State != 0) return;
            _sub++;
            // drawPlayerInfoStrings and drawMedalViewStrings take 6 frames, drawCardViewStrings 2, drawEvoChartStrings 1
            int need = Tab == 1 || Tab == 4 ? 1 : Tab == 3 ? 2 : 6;
            if (_sub >= need) { State = 1; _sub = 0; }
        }

        Texture2D Faces(int row) { return Pal(_faces, _faceTex, row); }
        static int ClutRow(int clut) { return (clut >> 6) - 488; }

        // renderRectPolyFT4 on texture page 0x18 (ETCTIM picture 6)
        void Face(int z, int x, int y, int w, int h, int u, int v, int clut) { Picture(z, Faces(ClutRow(clut)), x, y, w, h, u, v); }

        // renderBorderBox: c1 on the left and top, c2 on the bottom and right, then the fill
        void BorderBox(int z, int x, int y, int w, int h, int c1, int c2, int r, int g, int b)
        {
            x -= 0xA0; y -= 0x78;
            Line3(z, Bgr(c1), x, y + h, x, y, x + w, y);
            Line3(z, Bgr(c2), x, y + h, x + w, y + h, x + w, y);
            Box(z, x, y, w, h, r, g, b);
        }

        // renderInsetBox
        void Inset(int z, int x, int y, int w, int h) { BorderBox(z, x, y, w, h, 0x020202, 0xA08769, 0x35, 0x4B, 0x5C); }

        /// <summary>One frame of drawing (inside UiKit.Begin). playerPage draws the Player page over the box (PlayerInfoView) when it is ready.</summary>
        public void Draw(ScriptState s, Action playerPage)
        {
            if (!Showing && Detail.State == 0) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); Step(Detail); Step(Count); if (Box.State == 1) StepStrings(); _frames++; if (_showMedal) _medalModel.Turn(); }
            }
            BuildText(s);
            Begin();
            _showMedal = false;
            DrawBox(Box, () => Content(s));
            DrawBox(Detail, () => { if (Tab == 1) ChartDetail(s); else CardPicture(); });
            DrawBox(Count, () => CardCount(s));
            Flush();
            // renderDigiviceMedals: the medal turns over the page
            if (_showMedal) _medalModel.Draw(Disc, _faces, _medalPicture); else _medalModel.Hide();
            if (Box.State == 1 && Tab == 0 && State >= 1 && playerPage != null) playerPage();
        }

        /// <summary>Hides the medal model (the menu is not drawn this frame).</summary>
        public void HideModels() { _medalModel.Hide(); }

        // the text page: PLAYER_VIEW_LABELS[0] (the tab words) at the top, then the page's own strings
        void BuildText(ScriptState s)
        {
            string name = Tab == 1 && _digimon > 0 ? DigimonName(_digimon) : "";
            BuildPage(Tab + "|" + _medal + "|" + name + "|" + ModSettings.EvolutionGuide + "|" + ModSettings.RecruitTab, px =>
            {
                Write(px, _playerLabels[0], 0, 0);
                if (ModSettings.RecruitTab) Write(px, "Rec.", 0xC4, 0);         // ours: the recruit tab's word, after the four
                if (Tab == 1)
                {
                    // drawEvoChartStrings
                    Write(px, _cardLabels[1], 0, 0xC);
                    Write(px, _cardLabels[2], 0, 0x18);
                    Write(px, _cardLabels[3], 0, 0x24);
                    Write(px, _cardLabels[4], 0, 0x3C);
                    Write(px, name, 0, 0x30);
                    // ours, the OPTIONS evolution guide: the square button (glyph 0x81A0) and INFO on a free row of the text page
                    if (ModSettings.EvolutionGuide) Write(px, ((char)0x81A0) + "INFO", 0, 0x48);
                }
                else if (Tab == 2)
                {
                    // drawMedalViewStrings
                    Write(px, _playerLabels[1], 0, 0xC);
                    Write(px, _medalNames[_medal], 0, 0x18);
                    for (int j = 0; j < 3; j++) Write(px, _medalText[_medal * 3 + j], 0, j * 12 + 0x24);
                }
                else if (Tab == 3) Write(px, _cardLabels[0], 0, 0xC);      // drawCardViewStrings
            });
        }

        string DigimonName(int id)
        {
            var d = Disc != null ? DigimonStatsCatalog.Get(Disc, id) : null;
            return d != null ? d.Name ?? "" : "";
        }

        // renderPlayerMenu
        void Content(ScriptState s)
        {
            switch (Tab)
            {
                case 1: ChartView(s); break;
                case 2: MedalView(s); break;
                case 3: CardsView(s); break;
            }
            // US: the tab words, colour 0 for the open tab and 1 for the others, over the tab pictures
            int[] tx = { -0x89, -0x3E, 0xD, 0x40 }, tw = { 0x3C, 0x3C, 0x24, 0x24 }, tu = { 0, 0x3C, 0x78, 0x9C };
            int[] tabX = { -0x91, -0x46, 5, 0x38 }, tabW = { 0x4C, 0x4C, 0x34, 0x34 };
            // ours, with the recruit tab: Player and Chart 8 pixels narrower and every tab moved so the five have room (word 8 right of the tab's edge)
            if (ModSettings.RecruitTab) { tabX = new[] { -0x92, -0x4C, -6, 0x30 }; tabW = new[] { 0x44, 0x44, 0x34, 0x34 }; for (int i = 0; i < 4; i++) tx[i] = tabX[i] + 8; }
            for (int i = 0; i < 4; i++) Words(5, Tab == i ? 0 : 1, tx[i], -0x65, tw[i], tu[i], 0);
            if (ModSettings.RecruitTab) { Words(5, Tab == 4 ? 0 : 1, 0x6E, -0x65, 0x24, 0xC4, 0); MenuTab(0x66, 0x30, Tab == 4 ? 0 : 1); }
            for (int i = 0; i < 4; i++) MenuTab(tabX[i], tabW[i], Tab == i ? 0 : 1);
        }

        // ---- renderEvoChartView
        void ChartView(ScriptState s)
        {
            if (State == 0) return;
            if (State >= 2)
            {
                int x = ColumnX(_col);
                Face(5, x - 0xA2, _row * 0x13 - 0x4E, 0x18, 0x14, 0, 0xE8, 0x7DC7);
            }
            for (int i = 0; i < 0x3E; i++)
            {
                if (!Raised(s, i + 1)) continue;
                int shift = i + 1 == _digimon && State > 1 && _frames % 10 < 5 ? 0x10 : 0;
                var b = _chart[i];
                if (b.Clut < 5) Face(5, b.X - 0xA0, b.Y - 0x78, 0x10, 0x10, (shift + b.U) & 0xFF, b.V, _viewClut[b.Clut]);
            }
            for (int i = 0; i < 0x3E; i++) BorderBox(5, _chart[i].X - 1, _chart[i].Y - 1, 0x12, 0x12, 0xBEBEBE, 0x3C3C3C, 0x87, 0x87, 0x87);
            BorderBox(5, 0x15, 0x27, 0x22, 0x9F, 0xAAA0C8, 0x5A3C8C, 0xA5, 0x5A, 0x73);
            BorderBox(5, 0x3A, 0x27, 0x22, 0x9F, 0xA078A5, 0x46144B, 0x6E, 0x2D, 0x5A);
            BorderBox(5, 0x5F, 0x27, 0x22, 0xB0, 0xC88250, 0x5F370A, 0x0F, 0x50, 0x82);
            BorderBox(5, 0x84, 0x27, 0x6A, 0x9F, 0x6EAF5A, 0x285A00, 0x00, 0x87, 0x41);
            BorderBox(5, 0xF1, 0x27, 0x38, 0x9F, 0x6996D2, 0x1E4178, 0xAF, 0x64, 0x2D);
            Words(5, 0xF, -0x1A, 0x54, 0xC, 0, 0xC);
            Words(5, 0, -0xE, 0x54, 0x3C, 0xC, 0xC);
            Words(5, 7, 0x3A, 0x54, 0xC, 0x48, 0xC);
            Words(5, 0, 0x46, 0x54, 0x3C, 0x54, 0xC);
            // ours: the square button in pink (TEXT_COLORS 16) and INFO, bottom left, under the first columns
            if (ModSettings.EvolutionGuide) { Words(5, 16, -0x88, 0x54, 0xC, 0, 0x48); Words(5, 0, -0x7C, 0x54, 0x30, 0xC, 0x48); }
        }

        int From(int id, int j) { int o = (id - 1) * 11 + j; return o >= 0 && o < _paths.Length ? _paths[o] : -1; }
        int To(int id, int j) { int o = (id - 1) * 11 + 5 + j; return o >= 0 && o < _paths.Length ? _paths[o] : -1; }

        // the three lines of one path: dark one pixel above and below, light in the middle (drawLine3P + drawLine2P)
        void PathLine(int[,] t, int row, Color mid, Color edge)
        {
            for (int d = -1; d <= 1; d++)
            {
                Color c = d == 0 ? mid : edge;
                Line3(4, c, t[row, 0], t[row, 4] + d, t[row, 1], t[row, 5] + d, t[row, 2], t[row, 6] + d);
                LineCmd(4, c, t[row, 2], t[row, 6] + d, t[row, 3], t[row, 7] + d);
            }
        }

        // ---- renderEvoChartDetail (UI box 2)
        void ChartDetail(ScriptState s)
        {
            int id = _digimon;
            if (id < 1) return;
            int n1 = 0, n2 = 0;
            for (int i = 0; i < 5; i++) if (From(id, i) != -1) n1++;
            for (int i = 0; i < 6; i++) if (To(id, i) != -1) n2++;
            int fromFirst = n1 % 2 == 0 ? 0 : 4, fromCount = n1 % 2 == 0 ? 4 : 5;
            int toFirst = n2 % 2 == 0 ? 0 : 6, toCount = n2 % 2 == 0 ? 6 : 5;
            // the line colours are 0x00BBGGRR: 0x65DB around 0x0794E3
            Color fromEdge = Bgr(0x65DB), fromMid = Bgr(0x794E3);
            for (int j = 0; j < fromCount; j++) if (From(id, j) > 0) PathLine(_fromLines, fromFirst + j, fromMid, fromEdge);
            for (int j = 0; j < toCount; j++) if (To(id, j) > 0) PathLine(_toLines, toFirst + j, _toColour[j * 2], _toColour[j * 2 + 1]);
            var me = _chart[Mathf.Clamp(id - 1, 0, 61)];
            if (me.Clut < 5) Face(4, -8, -0x14, 0x10, 0x10, me.U, me.V, _detailClut[me.Clut]);
            BorderBox(4, 0x97, 99, 0x12, 0x12, 0xBEBEBE, 0x3C3C3C, 0x87, 0x87, 0x87);
            for (int j = 0; j < fromCount; j++)
            {
                int f = From(id, j); var p = _fromSpr[fromFirst + j];
                if (f <= 0) continue;
                var b = _chart[Mathf.Clamp(f - 1, 0, 61)];
                if (Raised(s, f) && b.Clut < 5) Face(4, p.x, p.y, 0x10, 0x10, b.U, b.V, _detailClut[b.Clut]);
                BorderBox(4, p.x + 0x9F, p.y + 0x77, 0x12, 0x12, 0xBEBEBE, 0x3C3C3C, 0x87, 0x87, 0x87);
            }
            for (int j = 0; j < toCount; j++)
            {
                int t = To(id, j); var p = _toSpr[toFirst + j];
                if (t <= 0) continue;
                var b = _chart[Mathf.Clamp(t - 1, 0, 61)];
                if (Raised(s, t) && b.Clut < 5) Face(4, p.x, p.y, 0x10, 0x10, b.U, b.V, _detailClut[b.Clut]);
                BorderBox(4, p.x + 0x9F, p.y + 0x77, 0x12, 0x12, 0xBEBEBE, 0x3C3C3C, 0x87, 0x87, 0x87);
            }
            // US: "Tree", the level word (DIGIMON_DATA level) and the name centred by its length; the game passes no shadow argument to these
            Words(4, 3, -0x14, -0x4F, 0x24, 0, 0x18, false);
            var info = Disc != null ? DigimonStatsCatalog.Get(Disc, id) : null;
            switch (info != null ? info.Level : 0)
            {
                case 1: Words(4, 0, -0x5B, 0x3A, 0x2E, 0x24, 0x18, false); break;
                case 2: Words(4, 0, -0x63, 0x3A, 0x3E, 0x52, 0x18, false); break;
                case 3: Words(4, 0, -0x5B, 0x3A, 0x2F, 0, 0x24, false); break;
                case 4: Words(4, 0, -0x64, 0x3A, 0x41, 0x2F, 0x24, false); break;
                case 5: Words(4, 0, -0x64, 0x3A, 0x41, 0, 0x3C, false); break;
            }
            int len = DigimonName(id).Length / 2;
            Words(4, 0, -0x5C - (len - 4) * 6, 0x4D, 0x78, 0, 0x30, false);
            foreach (var r in _detailInsets) Inset(4, r.x, r.y, r.width, r.height);
        }

        // ---- renderMedalView (hasMedal: trigger 0x16C + medal)
        void MedalView(ScriptState s)
        {
            if (State == 0) return;
            if (State >= 2) Face(1, _medal % 5 * 38 - 0x7E, _medal / 5 * 24 - 0x3D, 0x18, 0x18, 0, 0xD0, 0x7DC7);
            Lines(5, 0x8012472C, 0xE);
            Words(5, 3, -0x36, -0x51, 0x6C, 0, 0xC);
            for (int i = 0; i < 15; i++)
            {
                if (s.GetFlag(0x16C + i))
                {
                    if (_medal == i && State != 3)
                    {
                        Words(5, 0, -0x5D, 0x17, 0xAC, 0, 0x18);
                        for (int j = 0; j < 3; j++) Words(5, 0, -0x7E, j * 13 + 0x27, 0xFC, 0, j * 12 + 0x24);
                    }
                    if (_medal == i) _showMedal = true;      // renderDigiviceMedals
                }
                else Face(5, i % 5 * 38 - 0x7C, i / 5 * 24 - 0x3D, 0xF, 0x18, 0xF0, 0xC8, 0x7DC7);
                Face(5, i % 5 * 38 - 0x7A, i / 5 * 24 - 0x3D, 0xF, 0x18, 0xF0, 0xB0, 0x7DC7);
            }
            Inset(5, 0x3E, 0x8D, 0xB0, 0xE);
            Words(5, 0xF, -0x23, 0x55, 0xC, 0x6C, 0xC);
            Words(5, 0, -0x17, 0x55, 0x18, 0x84, 0xC);
            Words(5, 0, 1, 0x55, 0x24, 0x9C, 0xC);
            Words(5, 7, 0x2B, 0x55, 0xC, 0x78, 0xC);
            Words(5, 0, 0x37, 0x55, 0x30, 0xC0, 0xC);
        }

        // ---- renderCardsView (getCardAmount: ScriptState.CardCount)
        void CardsView(ScriptState s)
        {
            if (State == 0) return;
            if (State >= 2) Face(5, _card % 11 * 24 - 0x84, _card / 11 * 24 - 0x40, 0x18, 0x14, 0, 0xE8, 0x7DC7);
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 11; j++)
                {
                    int card = j + i * 11;
                    if (s.CardCount[card] > 0) Face(5, j * 24 - 0x81, i * 24 - 0x3E, 0x10, 0x10, card % 8 * 32, card / 8 * 16, _cardClut[card] * 64 + 0x7A07);
                    BorderBox(5, j * 24 + 0x1E, i * 24 + 0x39, 0x12, 0x12, 0xBEBEBE, 0x3C3C3C, 0x69, 0x69, 0x69);
                }
            Words(5, 3, -0x24, -0x50, 0x48, 0, 0xC);
            Words(5, 0xF, -0x23, 0x53, 0xC, 0x48, 0xC);
            Words(5, 0, -0x17, 0x53, 0x3C, 0x54, 0xC);
            Words(5, 7, 0x2B, 0x53, 0xC, 0x90, 0xC);
            Words(5, 0, 0x37, 0x53, 0x3C, 0x9C, 0xC);
        }

        // renderCardImage (UI box 2): loadCardImage put entry SELECTED_CARD of CARD/CARD.ALL (14 sectors each) at VRAM (576, 256)
        void CardPicture()
        {
            if (_cardPictureId != _card)
            {
                _cardPictureId = _card;
                if (_cardPicture != null) UnityEngine.Object.Destroy(_cardPicture);
                _cardPicture = null;
                TimImage tim;
                if (Disc.Exists("/CARD/CARD.ALL") && TimImage.TryParse(Disc.ReadFile("/CARD/CARD.ALL"), _card * 14 * 2048, out tim))
                {
                    _cardPicture = tim.ToTexture(0);
                    _cardPicture.filterMode = FilterMode.Point;
                }
            }
            Picture(4, _cardPicture, -0x4B, -0x54, 0x96, 0xB4, 0, 0);
        }

        // renderCardCount (UI box 3): the number of this card, and the word after it (u 0xD8 of the card page's line); no shadow argument is passed
        void CardCount(ScriptState s)
        {
            Number(3, 0, 0x59, 0x4B, 1, s.CardCount[_card]);
            Words(0, 0, 0x6B, 0x4C, 0xC, 0xD8, 0xC, false);
        }
    }

    /// <summary>
    /// The turning medal of the Medals page (renderDigiviceMedals, initializeMedalModel, activateMedalTexture): \ETCNA\MEDAL.TMD (path at exe 0x8012AAF0),
    /// seen from MEDAL_VIEW (exe 0x80123880: from (-1050, 220, -10000) to (-1050, 220, 0)) with GsSetProjection(0x400), turning by 0x64 a frame
    /// (MEDAL_ROTATION.vy). Its faces use a 24 x 32 block of ETCTIM's sixth picture (VRAM 512, 432); activateMedalTexture copies medal n's block there
    /// from (518 + n % 9 * 6, 432 + n / 9 * 32) (MEDAL_RECT 6 x 32), so here each corner's picture place is moved by the same amount.
    /// Ours: the TMD's light-sourced shading is not done (drawn at full brightness).
    /// </summary>
    public sealed class MedalModel
    {
        const int Layer = 29;
        static readonly Vector3 Away = new Vector3(0f, -6000f, 0f);     // far below every map, seen only by this camera
        GameObject _root, _model; Camera _cam; RenderTexture _rt; Mesh _mesh; Material _mat;
        Vector3 _eye, _at; bool _tried; int _built = -2, _vy;
        MmdModel.Part _part;

        public static void ExcludeFrom(Camera main) { if (main != null) main.cullingMask &= ~(1 << Layer); }

        public void Turn() { _vy = (_vy + 0x64) & 0xFFF; }

        public void Draw(DiscImage disc, TimImage faces, int picture)
        {
            if (disc == null || faces == null) return;
            if (!_tried)
            {
                _tried = true;
                try
                {
                    byte[] exe = disc.ReadFile("/SLUS_010.32");
                    uint load = BitConverter.ToUInt32(exe, 0x18);
                    int o = (int)(0x80123880 - load + 0x800);
                    _eye = new Vector3(BitConverter.ToInt32(exe, o), -BitConverter.ToInt32(exe, o + 4), BitConverter.ToInt32(exe, o + 8)) * 0.01f;
                    _at = new Vector3(BitConverter.ToInt32(exe, o + 12), -BitConverter.ToInt32(exe, o + 16), BitConverter.ToInt32(exe, o + 20)) * 0.01f;
                    var sb = new System.Text.StringBuilder();
                    for (int p = (int)(0x8012AAF0 - load + 0x800); exe[p] != 0; p++) sb.Append(exe[p] == '\\' ? '/' : (char)exe[p]);
                    byte[] tmd = disc.ReadFile(sb.ToString());
                    var buf = new byte[tmd.Length + 4]; buf[0] = 4; Array.Copy(tmd, 0, buf, 4, tmd.Length);
                    var model = MmdModel.Parse(buf);
                    if (model.Parts.Count > 0) _part = model.Parts[0];
                }
                catch (Exception e) { Debug.LogWarning("[menu] medal model: " + e.Message); }
            }
            if (_part == null || _part.Faces.Count == 0) return;
            if (_root == null)
            {
                _root = new GameObject("Medal view");
                var camGo = new GameObject("Medal camera");
                camGo.transform.SetParent(_root.transform, false);
                _cam = camGo.AddComponent<Camera>();
                _cam.cullingMask = 1 << Layer;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = new Color(0, 0, 0, 0);
                _cam.fieldOfView = 2f * Mathf.Atan(120f / 1024f) * Mathf.Rad2Deg;      // GsSetProjection(0x400) on a 240-line screen
                _cam.aspect = 4f / 3f;
                _cam.nearClipPlane = 1f; _cam.farClipPlane = 500f;
                _rt = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32);
                _cam.targetTexture = _rt;
                _model = new GameObject("Medal");
                _model.layer = Layer;
                _model.transform.SetParent(_root.transform, false);
                _model.transform.position = Away;
                _mesh = new Mesh { name = "MEDAL.TMD" };
                _model.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _mat = new Material(MmdModel.UnlitShader);
                _mat.SetFloat("_Cull", 0);
                _mat.SetFloat("_AlphaClip", 1); _mat.SetFloat("_Cutoff", 0.5f); _mat.EnableKeyword("_ALPHATEST_ON");
                _model.AddComponent<MeshRenderer>().sharedMaterial = _mat;
                // the packets' colour set: CLUT y - 488 is the picture's row (0x7C07 = (112, 496), row 8)
                int row = Mathf.Clamp((_part.Faces[0].Clut >> 6) - 488, 0, faces.PaletteCount - 1);
                var tex = faces.ToTexture(row);
                tex.filterMode = FilterMode.Point;
                _mat.SetTexture("_BaseMap", tex); _mat.SetTexture("_MainTex", tex);
            }
            _root.SetActive(true);
            if (_built != picture) { _built = picture; Build(faces, picture); }
            _model.transform.rotation = Quaternion.Euler(0f, _vy * 360f / 4096f, 0f);
            _cam.transform.position = Away + _eye;
            _cam.transform.rotation = Quaternion.LookRotation((_at - _eye).normalized, Vector3.up);
            if (Event.current != null && Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, UiKit.W, UiKit.H), _rt, ScaleMode.StretchToFill, true);
        }

        // one triangle per face; picture places on page 0x18 (the sixth ETCTIM picture starts at its top left), moved to medal n's block
        void Build(TimImage faces, int picture)
        {
            int du = picture < 0 ? 0 : 24 + picture % 9 * 24, dv = picture < 0 ? 0 : picture / 9 * 32;
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            foreach (var f in _part.Faces)
            {
                int b = verts.Count;
                for (int i = 0; i < 3; i++)
                {
                    int v = f.V[i] * 3;
                    verts.Add(new Vector3(_part.Verts[v], -_part.Verts[v + 1], _part.Verts[v + 2]) * MmdModel.Scale);
                    uvs.Add(new Vector2((f.U[i] + du + 0.5f) / faces.Width, 1f - (f.T[i] + dv + 0.5f) / faces.Height));
                }
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            }
            _mesh.Clear();
            _mesh.SetVertices(verts); _mesh.SetUVs(0, uvs); _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateBounds();
        }

        public void Hide() { if (_root != null && _root.activeSelf) _root.SetActive(false); }
    }
}
