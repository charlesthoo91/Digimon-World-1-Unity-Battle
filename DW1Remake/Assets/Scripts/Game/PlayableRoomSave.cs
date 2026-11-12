using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The save machine (script built-in 0x30: openSaveMachine / tickSaveMachine) and the menu after the partner falls asleep
    /// (partnerTickSleep state 5, main menu 0x28), from main/main_menu.c, US layout. The game draws these with drawString into its text page,
    /// renderText and renderMenuBox at fixed places; positions below are PS1 screen pixels (0-319, 0-239) as in the code.
    /// Our save file stands in for the memory card, so the "no memory card" screens (0x44, sleep without a card) never show.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        // one screen: its texts, its boxes, and the yellow cursor frame (MENU_HIGHLIGHTS: x, y, w, h, row height, rows, scroll limit)
        sealed class SysScreen
        {
            public readonly List<KeyValuePair<Vector2, string>> Texts = new List<KeyValuePair<Vector2, string>>();
            public readonly List<RectInt> Boxes = new List<RectInt>();
            public int CurX, CurY, CurW, CurH, RowH, Rows, Max, Pos, Scroll;
            public bool HasCursor;
            public bool Sides;      // ours (OPTIONS): left / right end Ask with 3 / 4
            public System.Action<SysScreen> Refresh;      // redraws the texts when the list scrolls (drawRegisteredDigimonSlots)
            public void Text(string s, int x, int y) { Texts.Add(new KeyValuePair<Vector2, string>(new Vector2(x, y), s)); }
            public readonly Dictionary<int, int> Width = new Dictionary<int, int>();   // renderText shows only this many pixels of the text page
            public void Text(string s, int x, int y, int w) { Width[Texts.Count] = w; Text(s, x, y); }
            public readonly HashSet<int> Dim = new HashSet<int>();     // renderText colour 1 (0x40 of 0x80): rows that cannot be picked
            public void DimText(string s, int x, int y) { Dim.Add(Texts.Count); Text(s, x, y); }
            public void Box(int x, int y, int w, int h) { Boxes.Add(new RectInt(x, y, w, h)); }
            public void Cursor(int x, int y, int w, int h, int rowH, int rows, int max = 0) { HasCursor = true; CurX = x; CurY = y; CurW = w; CurH = h; RowH = rowH; Rows = rows; Max = max; }
        }

        SysScreen _sys;
        Texture2D _sysFill;

        string Exe(uint addr) { return DigimonStatsCatalog.ExeText(_disc, addr); }

        // SLOT_ACTION_TITLES (exe 0x80131878, 8 pointers)
        string SlotTitle(int i)
        {
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            return Exe(System.BitConverter.ToUInt32(exe, (int)(0x80131878 - load + 0x800) + 4 * i));
        }

        // tickMenuInput: up/down move the cursor (sound 2), past the last row the list scrolls; Cross = 1 (sound 3), Circle = 2 (sound 4)
        IEnumerator Ask(SysScreen s, System.Action<int> answer)
        {
            _sys = s;
            yield return null;
            while (true)
            {
                int step = MenuStep();
                int pos0 = s.Pos, scroll0 = s.Scroll;
                if (s.HasCursor && step < 0)
                {
                    if (s.Pos > 0) { s.Pos--; Snd(2); }
                    else if (s.Scroll > 0) { s.Scroll--; Snd(2); if (s.Refresh != null) s.Refresh(s); }
                }
                else if (s.HasCursor && step > 0)
                {
                    if (s.Pos < s.Rows - 1) { s.Pos++; Snd(2); }
                    else if (s.Scroll < s.Max) { s.Scroll++; Snd(2); if (s.Refresh != null) s.Refresh(s); }
                }
                // ours: a page that redraws itself (OPTIONS' help box) is told about every cursor move, not only scrolling
                if (s.Refresh != null && s.Pos != pos0 && s.Scroll == scroll0) s.Refresh(s);
                if (s.Sides && Pad.Pressed(PadButton.Left)) { answer(3); break; }
                if (s.Sides && Pad.Pressed(PadButton.Right)) { answer(4); break; }
                if (GameMenus.CancelKey()) { Snd(4); answer(2); break; }
                if (ActionPressed()) { Snd(3); answer(1); break; }
                yield return null;
            }
            _sys = null;
        }

        void Snd(int n) { if (_audio != null) _audio.Play(0, n); }

        // ------------------------------------------------------------------ the save machine

        bool _saveMachineOn;

        // openSaveMachine: a partner under level 3 (DIGIMON_DATA) cannot be registered (menu 0x46, then 0x45); otherwise menu 0x3C
        IEnumerator SaveMachine()
        {
            _saveMachineOn = true;
            _mainState = 1;
            var info = DigimonStatsCatalog.Get(_disc, PartnerType);
            int menu = info != null && info.Level < 3 ? 0x46 : 0x3C;
            // openSaveMachine: no memory card (MEMORY_CARD_ID / MEMORY_CARD_SLOT -1) = menu 0x44 (renderCantRegister, view 17): four lines,
            // gone on Cross / Circle or after 0x79 frames; nothing is registered or saved
            if (_noCard)
            {
                var s = new SysScreen();
                for (int i = 0; i < 4; i++) s.Text(Exe(NoCardLines[i]), 0x22, 0x46 + 12 * i);
                s.Box(0x18, 0x40, 0x110, 0x3A);
                _sys = s;
                float until = Time.time + 0x79 / 20f;
                yield return null;
                while (Time.time < until && !ActionPressed() && !GameMenus.CancelKey()) yield return null;
                Snd(3);
                _sys = null;
                menu = -1;
            }
            int slot = 0, scroll = 0, pos = 0;
            while (menu != -1)
            {
                int answer = 0; SysScreen s;
                switch (menu)
                {
                    case 0x46:      // renderCantRegisterBaby: "<name> <species> is in / training, Memory Card cannot / be registered for Competition"
                        s = new SysScreen();
                        s.Text(_state.PartnerName + Exe(0x801346B4) + _partnerName + Exe(0x801346B8), 0x2E, 0x45);
                        s.Text(Exe(0x80131580), 0x2E, 0x45 + 0xC);
                        s.Text(Exe(0x801315A0), 0x2E, 0x45 + 0x18);
                        s.Box(0x24, 0x40, 0xF8, 0x2E);
                        yield return Ask(s, a => answer = a);
                        menu = 0x45;
                        break;
                    case 0x3C:      // renderRegisterDigimon: the partner's numbers and "Do you want to register?"
                    {
                        s = RegisterScreen();
                        yield return Ask(s, a => answer = a);
                        menu = answer == 1 && s.Pos == 0 ? 0x3D : 0x45;
                        break;
                    }
                    case 0x3D:      // renderSelectRegisterSlot: 10 rows of 39 slots
                        s = SlotScreen(scroll, pos);
                        yield return Ask(s, a => answer = a);
                        scroll = s.Scroll; pos = s.Pos;
                        if (answer == 1) { slot = s.Scroll + s.Pos; menu = 0x3E; }
                        else menu = 0x3C;
                        break;
                    case 0x3E:      // renderConfirmRegister
                        s = ConfirmRegisterScreen();
                        yield return Ask(s, a => answer = a);
                        if (answer == 1 && s.Pos == 0) { RegisterBattleData(slot); SaveToFile(); menu = -1; }
                        else if (answer == 1) menu = 0x3D;
                        else menu = 0x3C;
                        break;
                    case 0x45:      // renderDoYouWantToSave; Circle does nothing here
                        s = YesNoScreen(Exe(0x80131568), 0x46, 0x3C, 0x57, 0x63, 67, 87);
                        s.Box(0x3C, 0x32, 0xBC, 0x20); s.Box(0x3C, 0x52, 0x38, 0x22);
                        yield return Ask(s, a => answer = a);
                        if (answer == 1) { if (s.Pos == 0) SaveToFile(); menu = -1; }
                        break;
                }
            }
            _saveMachineOn = false;
            AdvanceScript();        // ACTIVE_INSTRUCTION = 0: the script goes on
        }

        static readonly uint[] NoCardLines = { 0x80131500, 0x8013151C, 0x80131538, 0x8013154C };     // view 17 (US)

        SysScreen YesNoScreen(string question, int x, int qy, int yesY, int noY, int curX, int curY)
        {
            var s = new SysScreen();
            s.Text(question, x, qy);
            s.Text(Exe(0x801346AC), x, yesY);
            s.Text(Exe(0x801346B0), x, noY);
            s.Cursor(curX, curY, 33, 12, 12, 2);
            return s;
        }

        // drawMainMenuStrings view 14 (US): text page rows 12 apart from v 0x0C, drawn at (0x32, 0x37); the values at x 0x64
        SysScreen RegisterScreen()
        {
            var s = new SysScreen();
            s.Text(SlotTitle(6), 0x32, 0x21);
            int y0 = 0x37;
            string[] labels = { Exe(0x80134684), Exe(0x8013468C), Exe(0x80134694), Exe(0x80134698), Exe(0x8013469C), Exe(0x801346A4), Exe(0x8013143C), Exe(0x80131448), Exe(0x80131454) };
            string[] values = new string[9];
            values[0] = _state.PartnerName; values[1] = _partnerName;
            values[2] = FormatInteger(_state.Stats[4], 4); values[3] = FormatInteger(_state.Stats[5], 4);
            values[4] = FormatInteger(_state.Stats[0], 3); values[5] = FormatInteger(_state.Stats[1], 3);
            for (int i = 0; i < 3; i++) values[6 + i] = i < _state.TechSet.Count && _state.TechSet[i] >= 0 ? Battle.MakeTech(_state.TechSet[i], false).Name : "";
            for (int i = 0; i < 9; i++) { s.Text(labels[i], 0x32, y0 + 12 * i); s.Text(values[i], 0x32 + 0x64, y0 + 12 * i); }
            s.Text(Exe(0x80131460), 0x46, 0xAD);
            s.Text(Exe(0x801346AC), 0x46, 0xC3);
            s.Text(Exe(0x801346B0), 0x46, 0xCF);
            s.Box(0x28, 0x1C, 0xF6, 0x16); s.Box(0x28, 0x32, 0xE0, 0x76); s.Box(0x3C, 0xA8, 0xC8, 0x16); s.Box(0x3C, 0xBE, 0x38, 0x22);
            s.Cursor(67, 195, 33, 12, 12, 2);
            return s;
        }

        // drawRegisteredDigimonSlots: name at x 0x1E, species at 0x6C, an empty slot shows "." (colour 9). The US slot labels
        // (SAVEGAME_ID_LABEL + 2) are empty, so no slot numbers are shown.
        SysScreen SlotScreen(int scroll, int pos)
        {
            var s = new SysScreen { Scroll = scroll, Pos = pos };
            s.Cursor(70, 55, 204, 12, 12, 10, 29);
            s.Refresh = FillSlots;
            FillSlots(s);
            return s;
        }

        void FillSlots(SysScreen s)
        {
            s.Texts.Clear(); s.Boxes.Clear();
            s.Text(SlotTitle(6), 0x32, 0x21);
            for (int i = 0; i < 10; i++)
            {
                var r = _state.Registry[s.Scroll + i];
                int y = 0x37 + 12 * i;
                if (r.Type != 0)
                {
                    var e = DigimonStatsCatalog.Get(_disc, r.Type);
                    s.Text(r.Name, 0x46 + 0x1E, y);
                    s.Text(e != null ? e.Name : "", 0x46 + 0x6C, y);
                }
                else s.Text(Exe(0x801346C0), 0x46 + 0x1E, y);
            }
            s.Text(Exe(0x8013147C), 0x46, 0xB9);
            s.Box(0x28, 0x1C, 0xF6, 0x16); s.Box(0x3C, 0x32, 0xE0, 0x82); s.Box(0x3C, 0xB4, 0xD4, 0x16);
        }

        // view 16 (US): "Do you want to register" / the partner's name; Yes / No; three lines of notice
        SysScreen ConfirmRegisterScreen()
        {
            var s = new SysScreen();
            s.Text(SlotTitle(6), 0x32, 0x21);
            s.Text(Exe(0x80131498), 0x46, 0x37);
            s.Text(_state.PartnerName, 0x46, 0x37 + 12);
            s.Text(Exe(0x801346AC), 0x46, 0x59);
            s.Text(Exe(0x801346B0), 0x46, 0x65);
            s.Text(Exe(0x801314B0), 0x46, 0x7D); s.Text(Exe(0x801314C8), 0x46, 0x7D + 12); s.Text(Exe(0x801314E4), 0x46, 0x7D + 24);
            s.Box(0x28, 0x1C, 0xF6, 0x16); s.Box(0x3C, 0x32, 0xD4, 0x22); s.Box(0x3C, 0x54, 0x38, 0x22); s.Box(0x3C, 0x78, 0xD4, 0x2E);
            s.Cursor(67, 89, 33, 12, 12, 2);
            return s;
        }

        // formatInteger: the number right-aligned in 'digits' places
        static string FormatInteger(int v, int digits) { return v.ToString().PadLeft(digits); }

        // registerBattleData
        void RegisterBattleData(int slot)
        {
            var r = _state.Registry[slot];
            r.Hp = _state.Stats[4]; r.Mp = _state.Stats[5]; r.Off = _state.Stats[0]; r.Def = _state.Stats[1]; r.Speed = _state.Stats[2]; r.Brain = _state.Stats[3];
            r.Discipline = _state.Discipline; r.Type = PartnerType; r.Name = _state.PartnerName;
            for (int i = 0; i < 3; i++) r.Moves[i] = i < _state.TechSet.Count ? _state.TechSet[i] : -1;
        }

        // ------------------------------------------------------------------ the sleep menu

        // menu 0x28 (renderSleepMenu, US): "Go to sleep" / "Save and go to sleep"; Circle does nothing
        IEnumerator SleepMenu()
        {
            while (true)
            {
                var s = new SysScreen();
                s.Text(Exe(0x8013136C), 0x76, 0x6A, 0x54);
                s.Text(Exe(0x80131378), 0x76, 0x7A, 0x54);
                s.Box(0x6C, 0x65, 0x68, 0x26);
                s.Cursor(118, 106, 84, 12, 16, 2);
                int answer = 0;
                yield return Ask(s, a => answer = a);
                if (answer != 1) continue;
                if (s.Pos == 1 && !_noCard) SaveToFile();      // menu 0x28 -> 0x29: no memory card, no save
                yield break;
            }
        }

        // ------------------------------------------------------------------ drawing

        static float SysX(float x) { return 707.5f + (x - 160f) * 4.42f; }
        static float SysY(float y) { return 530f + (y - 120f) * 4.42f; }

        void DrawSysScreen()
        {
            if (_sys == null) return;
            if (_sysFill == null)
            {
                // renderMenuBox: a frame, then a POLY_G4 inset 3 pixels, blue 0x70 / 0x50 (top) to 0x30 / 0x10 (bottom)
                _sysFill = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _sysFill.SetPixels32(new[] { new Color32(0, 0, 0x30, 255), new Color32(0, 0, 0x10, 255), new Color32(0, 0, 0x70, 255), new Color32(0, 0, 0x50, 255) });
                _sysFill.Apply();
            }
            UiKit.Begin();
            foreach (var b in _sys.Boxes)
            {
                var r = new Rect(SysX(b.x), SysY(b.y), b.width * 4.42f, b.height * 4.42f);
                UiKit.Box(r, new Color(0, 0, 0, 0));
                GUI.DrawTexture(new Rect(r.x + 3 * 4.42f, r.y + 3 * 4.42f, r.width - 6 * 4.42f, r.height - 6 * 4.42f), _sysFill, ScaleMode.StretchToFill);
            }
            var font = Battle.StatFont;
            for (int i = 0; i < _sys.Texts.Count; i++)
            {
                var t = _sys.Texts[i];
                if (string.IsNullOrEmpty(t.Value)) continue;
                int clipW;
                if (font != null && _sys.Width.TryGetValue(i, out clipW))
                {
                    // a slice of the text page: letters past the width are not shown (US "Save and go to sleep" shows "Save and go")
                    string cut = t.Value;
                    while (cut.Length > 0 && font.Measure(cut) > clipW) cut = cut.Substring(0, cut.Length - 1);
                    t = new KeyValuePair<Vector2, string>(t.Key, cut);
                }
                Color ink = font != null ? font.Ink : Color.white;
                if (_sys.Dim.Contains(i)) ink = new Color(ink.r * 0.5f, ink.g * 0.5f, ink.b * 0.5f, ink.a);
                if (HasCjk(t.Value) && (font == null || !font.HasChinese) && CjkFont != null) DrawCjk(t.Value, SysX(t.Key.x), SysY(t.Key.y), ink);      // ours: Traditional Chinese (PlayableRoomOptions)
                else if (font != null) font.Draw(t.Value, SysX(t.Key.x), SysY(t.Key.y), 4.42f, ink);
                else UiKit.Text(new Rect(SysX(t.Key.x), SysY(t.Key.y) - 8, 1000, 62), t.Value, 48, Color.white, TextAnchor.UpperLeft);
            }
            if (_sys.HasCursor)
            {
                // renderMenuSelector: a yellow (0xA0, 0xA0, 0) outline round the row
                float x = SysX(_sys.CurX - 1), y = SysY(_sys.CurY + _sys.RowH * _sys.Pos - 2), w = (_sys.CurW + 1) * 4.42f, h = (_sys.CurH + 2) * 4.42f, k = 4.42f;
                var c = new Color(0xA0 / 255f, 0xA0 / 255f, 0f, 1f);
                UiKit.Fill(new Rect(x, y, w, k), c); UiKit.Fill(new Rect(x, y + h - k, w, k), c);
                UiKit.Fill(new Rect(x, y, k, h), c); UiKit.Fill(new Rect(x + w - k, y, k, h), c);
            }
            UiKit.End();
        }
    }
}
