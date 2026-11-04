using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The new game's questions and the naming box (shop/script_menu.c): newGameStateMachine (Jijimon's questions, texts 0x10-0x20 of
    /// script 0's section 0x4D6), initializeNamingBuffer, tickNamingBox, renderNamingBox, renderSelectionBox, renderNameDisplayBox, US layout.
    /// Built-in 0x20 opens the naming box alone (flags 2: rename the boy, 3: rename the partner). The letter pages, labels, cursor
    /// pieces and box positions come from the exe; the cursor is drawn from the clock sheet (VRAM 896,448, colour row 508).
    /// </summary>
    public sealed class CharacterSetup
    {
        const int Section = 0x4D6;
        const uint CharPages = 0x80130774, CtrlBoxes = 0x8013078C, RolloverChars = 0x801307A0, RolloverCtrl = 0x80134608,
                   TitleStr = 0x801345F8, ButtonsStr = 0x80134600, PromptTamer = 0x801345F4,
                   U02 = 0x8013460C, U13 = 0x80134614, V01 = 0x8013461C, V23 = 0x80134624, CtrlX = 0x8013462C, Ys = 0x80134634,
                   CtrlW = 0x8013463C, Hs = 0x80134644, CharX = 0x8013464C, CharW = 0x80134654;
        const int NamingDelete = 0, NamingOk = 1, NamingLabels = 3, GridX = 0x4E;    // US

        readonly MapScripts _scripts;
        readonly ScriptState _st;
        readonly GameMenus _m;
        readonly byte[] _exe;
        readonly uint _load;

        public bool Active { get; private set; }
        public System.Action OnDone;
        public bool DrawBackground = true;   // false when a 3D stage with its own black background is showing
        public BattleArt Art;                // the clock sheet (cursor pieces)

        int _state, _next, _mode;            // SCRIPT_STATE_2, SCRIPT_NEXT_STATE_2, SCRIPT_TEXTBOX_MODE (1 text, 2 selection)
        string _speaker = "", _body = "";
        List<string> _opts;
        int _sel;
        bool _talking;

        int _flags;                          // NAMING_BOX_FLAG: 1 = the partner's name, 2 = opened by a script
        readonly ushort[] _buf = new ushort[7];   // NAMING_BUFFER, Shift-JIS codes, 0 = end
        int _letter, _selector;             // NAMING_CURRENT_LETTER, NAMING_SELECTOR (0x8000 | n = a button)
        bool _naming;

        public CharacterSetup(MapScripts scripts, ScriptState state, GameMenus menus, DiscImage disc)
        {
            _scripts = scripts; _st = state; _m = menus;
            if (disc != null && disc.Exists("/SLUS_010.32")) { _exe = disc.ReadFile("/SLUS_010.32"); _load = System.BitConverter.ToUInt32(_exe, 0x18); }
        }

        int At(uint a) { return (int)(a - _load + 0x800); }
        int S16(uint a, int i) { return System.BitConverter.ToInt16(_exe, At(a) + 2 * i); }
        int U8(uint a, int i) { return _exe[At(a) + i]; }
        int SB8(uint a, int i) { return (sbyte)_exe[At(a) + i]; }
        string Str(uint a) { int p = At(a), j = p; while (j < _exe.Length && _exe[j] != 0) j++; return System.Text.Encoding.ASCII.GetString(_exe, p, j - p); }

        // NAMING_CHAR_PAGES[page]: 9 rows of 5 Shift-JIS letters
        ushort Letter(int page, int row, int col)
        {
            uint table = System.BitConverter.ToUInt32(_exe, At(CharPages) + 4 * page);
            uint str = System.BitConverter.ToUInt32(_exe, At(table) + 4 * row);
            int o = At(str) + col * 2;
            return (ushort)(_exe[o] << 8 | _exe[o + 1]);
        }

        void Sfx(int n) { if (_m != null && _m.Sfx != null) _m.Sfx(n); }

        // ------------------------------------------------------------------ start

        /// <summary>newGameScene: initializeNamingBuffer(0), variable 254 = 0, then newGameStateMachine.</summary>
        public void Start()
        {
            if (_exe == null || _scripts == null) { Finish(); return; }
            Active = true;
            InitNaming(0);
            _st.Vars[0xFE] = 0;
        }

        /// <summary>Built-in 0x20 (initializeNamingBuffer(variable 254)): with flag 2 the naming box opens on the current name.</summary>
        public void StartNaming(int flags)
        {
            if (_exe == null) { Finish(); return; }
            Active = true;
            InitNaming(flags);
        }

        void InitNaming(int flags)
        {
            if ((flags & 2) == 0) { _flags = 0; _state = 0; _buf[0] = 0; }
            else
            {
                _talking = false;
                _flags = flags; _state = 0x14;
                SetBuffer((_flags & 1) == 0 ? _st.PlayerName : _st.PartnerName);
            }
        }

        void Finish() { Active = false; _naming = false; if (OnDone != null) OnDone(); }

        // ------------------------------------------------------------------ newGameStateMachine

        // showNewGameDialogue / showNewGameSelection: showMapHeadTextbox(text, 0xFE, 0, 0x4D6); the selection's last two lines are the choices
        void Dialogue(int text, int next)
        {
            string t = ScriptRunner.MapHeadText(_scripts, _st, Section, text, 0xFF) ?? "";
            int nl = t.IndexOf('\n');
            _speaker = nl >= 0 ? t.Substring(0, nl).Trim() : ""; _body = nl >= 0 ? t.Substring(nl + 1).Trim() : t.Trim();
            _opts = null; _talking = true;
            _state = 1; _next = next; _mode = 1;
        }

        void Selection(int text, int next)
        {
            var lines = new List<string>((ScriptRunner.MapHeadText(_scripts, _st, Section, text, 0xFF) ?? "").Split('\n'));
            int q = Mathf.Max(0, lines.Count - 2);
            _speaker = string.Join(" ", lines.GetRange(0, q).ToArray()).Trim(); _body = "";
            _opts = lines.GetRange(q, lines.Count - q); _sel = 0; _talking = true;
            _state = 1; _next = next; _mode = 2;
        }

        void Step()
        {
            switch (_state)
            {
                case 0:
                    _st.SetFlag(49, true);
                    _st.PlayerName = Str(ButtonsStr + 7);      // DIGIMON_NAME(0) = &MAIN_D_80134600[7]
                    Dialogue(0x10, 2); break;
                case 2: Dialogue(0x11, 3); break;
                case 3: Selection(0x12, 4); break;
                case 4: Dialogue(0x13, 6); break;
                case 5: Dialogue(0x16, 10); break;
                case 6: Dialogue(0x14, 7); break;
                case 7: Selection(0x15, 8); break;
                case 8: _st.Vars[0xFE] = 0; _state = 0x11; break;
                case 9: _st.Vars[0xFE] = 1; _state = 0x11; break;
                case 10: Dialogue(0x17, 0x0B); break;
                case 0x0B: Selection(0x18, 8); break;
                case 0x11: Dialogue(0x19, 0x12); break;
                case 0x12: Dialogue(0x1A, 0x13); break;
                case 0x13: _talking = false; _state = 0x14; break;
                case 0x14:
                    _selector = 0;                                // setupNameSelectorBox: SHOP_AMOUNT = NAMING_START_PAGE (0)
                    _letter = Length() ; if (_letter == 6) _letter--;      // setupNameDisplayBox
                    _naming = true; _state = 1; _mode = 0;
                    break;
                case 0x15:
                    _naming = false;
                    if ((_flags & 1) == 0) _st.PlayerName = BufferText(); else _st.PartnerName = BufferText();
                    _state = 0x16;
                    break;
                case 0x16:
                    if ((_flags & 2) != 0) { Finish(); return; }
                    if ((_flags & 1) == 0) Dialogue(0x1B, 0x17); else Dialogue(0x1E, 0x1B);
                    break;
                case 0x17: Selection(0x1C, 0x18); break;
                case 0x18: Dialogue(0x1D, 0x1A); break;
                case 0x19: _state = 0x13; break;
                case 0x1A: _flags |= 1; _buf[0] = 0; _state = 0x13; break;
                case 0x1B: Selection(0x1C, 0x1C); break;
                case 0x1C: Dialogue(0x1F, 0x1E); break;
                case 0x1D: _state = 0x13; break;
                case 0x1E: Dialogue(0x20, 0x1F); break;
                case 0x1F: _talking = false; _st.SetFlag(49, false); Finish(); return;
            }
        }

        // ------------------------------------------------------------------ the name buffer

        int Length() { int n = 0; while (n < 6 && _buf[n] != 0) n++; return n; }

        string BufferText()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 6 && _buf[i] != 0; i++) sb.Append(MapScripts.Pair(_buf[i]));
            return sb.ToString();
        }

        // a stored name back into the letters of the pages it was typed from (a letter not on them is left out)
        void SetBuffer(string s)
        {
            int n = 0;
            foreach (char ch in s ?? "")
            {
                if (n >= 6) break;
                ushort code = 0;
                for (int page = 0; page < 2 && code == 0; page++)
                    for (int r = 0; r < 9 && code == 0; r++)
                        for (int c = 0; c < 5; c++) { ushort l = Letter(page, r, c); if (l != 0x8140 && MapScripts.Pair(l) == ch.ToString()) { code = l; break; } }
                if (code != 0) _buf[n++] = code;
            }
            _buf[n] = 0;
        }

        // namingDeleteLast (US: the letter before the cursor goes)
        void DeleteLast()
        {
            if (_buf[0] == 0) return;
            if (_letter == 0) _buf[0] = 0;
            else { _letter--; _buf[_letter] = 0; }
            Sfx(3);
        }

        // terminateNamingBuffer: trailing spaces (0x8140) are cut off
        void Terminate()
        {
            int pos = 0;
            for (; pos < 6; pos++) if (pos + 1 >= 6 || _buf[pos + 1] == 0) break;
            while (pos != 0 && _buf[pos] == 0x8140) pos--;
            if (pos + 1 < _buf.Length) _buf[pos + 1] = 0;
        }

        // ------------------------------------------------------------------ input

        public void Tick(int step, int side, bool action, bool cancel)
        {
            if (!Active) return;
            if (_state == 1)
            {
                if (_naming) { TickNaming(step, side, action, cancel); return; }
                // tickScriptDialogueBox: mode 1 goes on with Cross; mode 2 to the next state + the choice
                if (_mode == 2 && _opts != null && _opts.Count > 0 && step != 0) { _sel = (_sel + step + _opts.Count) % _opts.Count; Sfx(2); }
                if (action) { Sfx(3); _state = _mode == 2 ? _next + _sel : _next; }
                return;
            }
            for (int guard = 0; guard < 8 && Active && _state != 1; guard++) Step();
        }

        // tickNamingBox (US), one button a frame in this order
        void TickNaming(int step, int side, bool action, bool cancel)
        {
            int row = _selector / 5, col = _selector % 5, special = _selector & 0x7FFF;
            bool button = (_selector & 0x8000) != 0;
            if (GameMenus.SquareKey()) { _buf[0] = 0; _letter = 0; Sfx(3); return; }
            if (action)
            {
                if (!button)
                {
                    int page = row < 9 ? 0 : 1;
                    if (row >= 9) row -= 9;
                    ushort l = Letter(page, row, col);
                    if (l == 0x8140 && _letter == 0) { Sfx(0x0B); return; }     // no space first
                    _buf[_letter] = l; _buf[_letter + 1] = 0;
                    if (_letter != 5) _letter++;
                    if (_buf[5] != 0 && _letter == 5) _selector = 0x8000 | NamingOk;
                    Sfx(3);
                    return;
                }
                if (special == NamingDelete) DeleteLast();
                else if (special == NamingOk)
                {
                    if (_buf[0] == 0) { Sfx(0x0B); return; }
                    Terminate(); Sfx(3);
                    _state = 0x15;
                }
                return;
            }
            if (GameMenus.CircleKey()) { _selector = 0x8000 | 0; Sfx(2); return; }        // NAMING_FIRST_BUTTON
            if (cancel) { DeleteLast(); return; }
            if (GameMenus.StartKey()) { _selector = 0x8000 | NamingOk; Sfx(2); return; }
            if (side < 0) Left(col, row, special);
            else if (side > 0) Right(col, row, special);
            else if (step < 0) Up(col, row);
            else if (step > 0) Down(col, row);
        }

        // namingSelectionLeft / Right / Up / Down: rows 0-8 the left grid, 9-17 the right; NAMING_ROLLOVER_CHARS / _CTRL join the buttons
        void Left(int col, int row, int special)
        {
            if ((_selector & 0x8000) == 0)
            {
                if (row < 9) _selector = col == 0 ? System.BitConverter.ToUInt16(_exe, At(RolloverChars) + 2 * row) : col - 1 + row * 5;
                else { if (col == 0) { row -= 9; col = 4; } else col--; _selector = col + row * 5; }
            }
            else _selector = System.BitConverter.ToUInt16(_exe, At(RolloverCtrl) + 2 * special) + 0x31;
            Sfx(2);
        }

        void Right(int col, int row, int special)
        {
            if ((_selector & 0x8000) == 0)
            {
                if (row < 9) { if (col == 4) { row += 9; col = 0; } else col++; _selector = col + row * 5; }
                else if (col == 4) _selector = System.BitConverter.ToUInt16(_exe, At(RolloverChars) + 2 * (row - 9));
                else _selector = col + 1 + row * 5;
            }
            else _selector = System.BitConverter.ToUInt16(_exe, At(RolloverCtrl) + 2 * special);
            Sfx(2);
        }

        void Up(int col, int row)
        {
            if ((_selector & 0x8000) == 0)
            {
                if (row < 9) row = row == 0 ? 8 : row - 1;
                else row = row == 9 ? 17 : row - 1;
                _selector = col + row * 5;
            }
            else if (_selector == (0x8000 | 0)) _selector = 0x8000 | NamingOk;
            else _selector--;
            Sfx(2);
        }

        void Down(int col, int row)
        {
            if ((_selector & 0x8000) == 0)
            {
                if (row < 9) row = row == 8 ? 0 : row + 1;
                else row = row == 17 ? 9 : row + 1;
                _selector = col + row * 5;
            }
            else if (_selector == (0x8000 | NamingOk)) _selector = 0x8000 | 0;
            else _selector++;
            Sfx(2);
        }

        // ------------------------------------------------------------------ drawing

        static float CX(float x) { return 707.5f + x * 4.42f; }       // PS1 pixels from the middle -> UiKit canvas
        static float CY(float y) { return 530f + y * 4.42f; }

        public void OnGUI()
        {
            if (!Active) return;
            if (DrawBackground) UiKit.Fill(new Rect(0, 0, Screen.width, Screen.height), Color.black);
            UiKit.Begin();
            if (_naming) DrawNaming();
            else if (_talking) DrawChat();
            UiKit.End();
        }

        // setupNewGameDialogueBox: the box (-130, -78, 262, 59); the box style and text drawing are ours (as the room's dialogue box)
        void DrawChat()
        {
            var r = new Rect(CX(-130), CY(-78), 262 * 4.42f, 59 * 4.42f);
            UiKit.Box(r, UiKit.DialogFill, 4f);
            var font = Battle.StatFont;
            float x = r.x + 10 * 4.42f, y = r.y + 6 * 4.42f;
            if (_speaker.Length > 0) { Text(_speaker, x, y, font != null ? font.Colour(7) : UiKit.Yellow); y += 13 * 4.42f; }
            if (_opts != null)
            {
                for (int i = 0; i < _opts.Count; i++)
                {
                    if (i == _sel) UiKit.Fill(new Rect(x - 2 * 4.42f, y - 4.42f, 60 * 4.42f, 13 * 4.42f), new Color(1f, 1f, 1f, 0.18f));
                    Text(_opts[i].Trim(), x, y, font != null ? font.Ink : Color.white); y += 13 * 4.42f;
                }
                return;
            }
            foreach (var line in _body.Split('\n')) { Text(line.Trim(), x, y, font != null ? font.Ink : Color.white); y += 13 * 4.42f; }
        }

        static void Text(string s, float x, float y, Color c)
        {
            var font = Battle.StatFont;
            if (font != null) font.Draw(s, x, y, 4.42f, c);
            else UiKit.Text(new Rect(x, y - 8, 1000, 62), s, 48, c, TextAnchor.UpperLeft);
        }

        // one letter in the fixed-width mode (DRAW_STRING2_IS_FIXED_WIDTH = 1): 12 pixels a cell
        static void Glyph(ushort code, float psx, float psy, Color c)
        {
            if (code == 0 || code == 0x8140) return;
            var font = Battle.StatFont;
            if (font != null) font.Draw(((char)code).ToString(), CX(psx), CY(psy), 4.42f, c);
        }

        void DrawNaming()
        {
            var font = Battle.StatFont;
            Color ink = font != null ? font.Ink : Color.white;
            // setupNameSelectorBox (-145, -91, 290, 138) and setupNameDisplayBox (-145, 60, 149, 42)
            const int bx = -145, by = -91, dx = -145, dy = 60;
            UiKit.Box(new Rect(CX(bx), CY(by), 290 * 4.42f, 138 * 4.42f), UiKit.DialogFill, 4f);
            UiKit.Box(new Rect(CX(dx), CY(dy), 149 * 4.42f, 42 * 4.42f), UiKit.DialogFill, 4f);
            // renderNamingBox: the labels (NAMING_CTRL_BOXES x, y, width) cut from the line "<colour 7>Name<colour 1>BackOK"
            string title = Str(TitleStr), buttons = Str(ButtonsStr);
            string line = title + buttons;
            int texX = 0;
            for (int j = 0; j < NamingLabels; j++)
            {
                int lx = S16(CtrlBoxes, j * 3), ly = S16(CtrlBoxes, j * 3 + 1), lw = S16(CtrlBoxes, j * 3 + 2);
                for (int k = 0; k < lw / 12; k++)
                {
                    int ci = texX / 12 + k;
                    if (ci >= line.Length) break;
                    Color c = ci < title.Length ? (font != null ? font.Colour(7) : UiKit.Yellow) : ink;
                    if (font != null) font.Draw(line[ci].ToString(), CX(bx + lx + k * 12), CY(by + ly), 4.42f, c);
                }
                texX += lw;
            }
            // the letters: left grid at x + 0x4E, right one 0x70 further, 18 apart, rows 14 apart from y + 6
            for (int page = 0; page < 2; page++)
                for (int r = 0; r < 9; r++)
                    for (int k = 0; k < 5; k++)
                        Glyph(Letter(page, r, k), bx + GridX + page * 0x70 + k * 18, by + 6 + r * 14, ink);
            DrawSelector(bx, by);
            // renderNameDisplayBox (US): the name, in fixed-width cells, at x + 7, y + 6 (the US prompt is empty), and the underscore
            for (int i = 0; i < 6 && _buf[i] != 0; i++) Glyph(_buf[i], dx + 7 + Str(PromptTamer).Length * 12 + i * 12, dy + 6, ink);
            // renderNamingUnderscore: three lines (0x020202, 0x10C0C0, 0x020202) under the current letter
            float ux = dx + _letter * 12 + 4, uy = dy + 0x10;
            UiKit.Fill(new Rect(CX(ux), CY(uy), 12 * 4.42f, 4.42f), new Color32(2, 2, 2, 255));
            UiKit.Fill(new Rect(CX(ux), CY(uy + 1), 12 * 4.42f, 4.42f), new Color32(0xC0, 0xC0, 0x10, 255));
            UiKit.Fill(new Rect(CX(ux), CY(uy + 2), 12 * 4.42f, 4.42f), new Color32(2, 2, 2, 255));
        }

        // renderSelectionBox: 8 pieces of the clock sheet (VRAM 896,448; CLUT 256,508) round the letter or the button
        void DrawSelector(int bx, int by)
        {
            var art = Art;
            if (art == null) return;
            int baseX, baseY;
            bool button = (_selector & 0x8000) != 0;
            if (!button)
            {
                int row = _selector / 5, col = _selector % 5;
                if (row < 9) { baseX = bx + col * 18 + 0x4A; baseY = by + row * 14 + 2; }
                else { baseX = bx + col * 18 + 0xBA; baseY = by + (row - 9) * 14 + 2; }
            }
            else
            {
                int idx = (_selector & 0x7FFF) * 3 + 3;
                baseX = bx + S16(CtrlBoxes, idx) - 4; baseY = by + S16(CtrlBoxes, idx + 1) - 4;
            }
            for (int i = 0; i < 8; i++)
            {
                int u0 = U8(U02, i), u1 = U8(U13, i), v0 = U8(V01, i), v1 = U8(V23, i);
                int x = baseX + (button ? SB8(CharX, i) : SB8(CtrlX, i)), y = baseY + SB8(Ys, i);
                int w = button ? SB8(CharW, i) : SB8(CtrlW, i), h = SB8(Hs, i);
                art.ClockQuad(508 - 496, u0, v0, u1, v1, x, y, w, h);
            }
        }
    }
}
