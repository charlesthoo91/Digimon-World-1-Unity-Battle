using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The list boxes of two script routines (shop/script_menu.c, US build), on the game's UI box:
    /// Birdramon (createBirdraTransportMenu, renderBirdraTransportMenu, calculateBirdramonMenuStrings): box 1 at (-71, -100) 222 x 129 out of
    /// Birdramon, headers 6 and 2, six rows of "map name, cost", the scroll bar (0xD2, 0x18, 6 x 0x5A) and the cursor 0xCA wide;
    /// Mojyamon (createMojyaTradeMenu, renderMojyaTradeMenu, calculateItemListStrings): box 1 at (-88, -80) 223 x 83, the label "You have" /
    /// "Will trade" (exe 0x801307CC), an engraved line, three rows "item he wants, item he gives" with a dash on a trade already made, the
    /// cursor 0xD4 wide. Positions are PS1 pixels from the middle of the screen.
    /// </summary>
    public sealed class ScriptListView : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 1 };
        public enum Kind { Birdramon, Mojyamon, Jukebox }
        public Kind Mode;

        // one row: its text (drawString codes) and whether it can be picked (bit 0x80 of ITEM_MENU_LEFT->buf)
        public readonly List<string> Rows = new List<string>();
        public readonly List<bool> Ok = new List<bool>();
        public int Top, Cursor;
        int Visible { get { return Mode == Kind.Mojyamon ? 3 : 6; } }

        string _label = "";
        float _acc;
        const int LabelRow = 108;

        public static ScriptListView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new ScriptListView();
            v.Init(disc, font);
            v.LoadHeaders();
            v._label = v.Str(0x801307CC);
            return v;
        }

        public bool Showing { get { return Box.State != 0; } }
        public int Row { get { return Top + Cursor; } }

        /// <summary>calculateBirdramonMenuStrings: colour 1 (3 if the Bits do not pay), the place name padded to 12 (padWithSpaces), 0x0F, 0x1B, the cost in 4.</summary>
        public static string BirdraRow(string place, int cost, bool ok)
        {
            return Ink(ok ? 1 : 3) + place + Pad(place, 12) + Gap + Tab1B + ItemMenuView.Digits(cost, 4);
        }

        /// <summary>calculateItemListStrings (US): colour 7, a dash (0x817C) when this trade was made or a space, colour 1 (3 if the bag has none),
        /// the item he wants padded to 8, 0x0F, colour 1, the item he gives.</summary>
        /// <summary>calculateMusicMenuStrings: colour 6 for the song variable 249 names, else 1; its number + 1 in two places (intToStringSJIS flag 0),
        /// 0x0F, the name (BGM_TRACK_NAMES), padded to 12 (padWithSpaces). The US build writes this into a 64-byte line (the EU one 80): the longest
        /// names ("The Ancient Region of Dino ... Night Theme", 56 letters) ran past it into the next memory, which froze the game when the list
        /// scrolled to them (the US jukebox freeze). Our rows are strings, so nothing runs over; the page cuts a long name at the box edge.</summary>
        public static string JukeboxRow(int track, string name, bool chosen)
        {
            return Ink(chosen ? 6 : 1) + ItemMenuView.Digits(track + 1, 2) + Gap + name + Pad(name, 12);
        }

        public static string MojyaRow(string want, string give, bool ok, bool traded)
        {
            return Ink(7) + (traded ? ((char)0x817C).ToString() : " ") + Ink(ok ? 1 : 3) + want + Pad(want, 8) + Gap + Ink(1) + give;
        }

        // padWithSpaces (US): width minus half the name's length in bytes, in full-width spaces
        static string Pad(string s, int width) { int n = width - s.Length / 2; return n > 0 ? new string(' ', n) : ""; }

        public void Open(Kind mode, RectInt origin)
        {
            Mode = mode; Top = 0; Cursor = 0;
            if (mode == Kind.Birdramon) Open(Box, new RectInt(-71, -100, 222, 129), origin, 0xF1);
            // createJukeboxMenu: (-0x47, -0x62, 0xDE, 0x81). Ours (user's choice 2026-11-10): 0x110 wide, the widest the 256-wide text page fills
            else if (mode == Kind.Jukebox) { Open(Box, new RectInt(-0x88, -0x62, JukeW, 0x81), origin, 0xF1); _tick = 0; _tickRow = -1; }
            else Open(Box, new RectInt(-88, -80, 223, 83), origin, 0xE1);
        }

        // itemMenuCursorUp / Down / MoveToTop / MoveToBottom
        public void Up() { if (Top + Cursor == 0) { Snd(0xB); return; } if (Cursor == 0) Top--; else Cursor--; Snd(2); }
        public void Down()
        {
            Cursor++;
            if (Top + Cursor < Rows.Count) { if (Cursor == Visible) { Top++; Cursor--; } Snd(2); }
            else { Cursor--; Snd(0xB); }
        }
        public void ToTop() { Top = 0; Cursor = 0; Snd(2); }
        public void ToBottom()
        {
            int diff = Rows.Count - Visible;
            if (diff <= 0) { Top = 0; Cursor = Mathf.Max(0, Rows.Count - 1); } else { Top = diff; Cursor = Visible - 1; }
            Snd(2);
        }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); TickJukebox(); }
            }
            var key = new System.Text.StringBuilder().Append(Mode).Append(Top).Append(':').Append(_skip).Append(':').Append(Row).Append(':');
            foreach (var r in Rows) key.Append(r).Append('|');
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                Color32 white = Font.Colour(1);
                for (int i = 0; i < Visible && Top + i < Rows.Count; i++) DrawCoded(px, Shown(Top + i), 0, i * 12, white);
                if (Mode == Kind.Mojyamon) DrawCoded(px, _label, 0, LabelRow, white);
            });
            Begin();
            DrawBox(Box, () => { if (Mode == Kind.Birdramon) DrawBirdra(); else if (Mode == Kind.Jukebox) DrawJukebox(); else DrawMojya(); });
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // renderBirdraTransportMenu
        void DrawBirdra()
        {
            int z = 6 - Box.Id, bx = Box.Rect.x, by = Box.Rect.y;
            Header(z, 6, bx + 8, by + 5);
            Header(z, 2, bx + 0xA2, by + 5);
            ScrollBarAt(z, bx + 0xD2, by + 0x18, 6, 0x5A, Rows.Count, Top, Visible);
            SelectionCursor(z, bx + 5, by + Cursor * 0x12 + 0x11, 0xCA, 0x12);
            int rows = Mathf.Min(Visible, Rows.Count - Top);
            for (int i = 0; i < rows; i++) String(z, bx + 8, by + 0x13 + i * 0x12, 0xC8, 0, i * 12);
        }

        // renderJukeboxMenu: headers 4 and 5, the scroll bar of allocateItemMenuBox(0x7E, 6, 0xD2, 0x18, 6, 0x5A), the cursor, the rows.
        // Ours, in the wider box: the bar, cursor and rows keep their distances from the box's right edge (0x0C, 0x14, 0x16)
        const int JukeW = 0x110, JukeRowW = JukeW - 0x16;
        void DrawJukebox()
        {
            int z = 6 - Box.Id, bx = Box.Rect.x, by = Box.Rect.y;
            Header(z, 4, bx + 8, by + 5);
            Header(z, 5, bx + 0x26, by + 5);
            ScrollBarAt(z, bx + JukeW - 0x0C, by + 0x18, 6, 0x5A, Rows.Count, Top, Visible);
            SelectionCursor(z, bx + 5, by + Cursor * 0x12 + 0x11, JukeW - 0x14, 0x12);
            int rows = Mathf.Min(Visible, Rows.Count - Top);
            for (int i = 0; i < rows; i++) String(z, bx + 8, by + 0x13 + i * 0x12, JukeRowW, 0, i * 12);
        }

        // ours: a name still too long for the row slides left on the cursor's row, a letter every 3 frames, with a stop at each end
        int _tick, _tickRow = -1, _skip;
        const int JukePrefix = 4;       // colour code, two digits, 0x0F
        void TickJukebox()
        {
            if (Mode != Kind.Jukebox || Font == null || Row >= Rows.Count) return;
            if (_tickRow != Row) { _tickRow = Row; _tick = 0; _skip = 0; return; }
            string r = Rows[Row];
            if (r.Length <= JukePrefix || Font.Measure(r) <= JukeRowW) { _skip = 0; return; }
            _tick++;
            bool fits = Font.Measure(r.Substring(0, JukePrefix) + r.Substring(JukePrefix + _skip)) <= JukeRowW;
            if (!fits && _tick >= 20 && _tick % 3 == 0) _skip++;
            if (fits && _tick >= 20) { if (_tick > 20 + 3 * _skip + 30) { _skip = 0; _tick = 0; } }
        }

        string Shown(int row)
        {
            string r = Rows[row];
            if (Mode != Kind.Jukebox || row != Row || _skip <= 0 || r.Length <= JukePrefix + _skip) return r;
            return r.Substring(0, JukePrefix) + r.Substring(JukePrefix + _skip);
        }

        /// <summary>createJukeboxMenu: the cursor on the given row, as far down as the six rows allow.</summary>
        public void Place(int row)
        {
            int scroll = row - Visible;
            if (scroll < 0) { Top = 0; Cursor = row; } else { Top = scroll + 1; Cursor = Visible - 1; }
        }

        // renderMojyaTradeMenu (US): the engraved line, the label's two pieces (0x42 at (0x14, 5), 0x4E at (0x7A, 5)), the cursor, the rows
        void DrawMojya()
        {
            int z = 6 - Box.Id, bx = Box.Rect.x, by = Box.Rect.y;
            HLine(z, bx + 4, by + 0x15, 0xD7);
            String(z, bx + 0x14, by + 5, 0x42, 0, LabelRow);
            String(z, bx + 0x7A, by + 5, 0x4E, 0x42, LabelRow);
            SelectionCursor(z, bx + 5, by + Cursor * 0x12 + 0x19, 0xD4, 0x12);
            int rows = Mathf.Min(Visible, Rows.Count - Top);
            for (int i = 0; i < rows; i++) String(z, bx + 8, by + 0x1B + i * 0x12, 0xD0, 0, i * 12);
        }
    }
}
