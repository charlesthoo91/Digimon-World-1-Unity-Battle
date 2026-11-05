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
        public enum Kind { Birdramon, Mojyamon }
        public Kind Mode;

        // one row: its text (drawString codes) and whether it can be picked (bit 0x80 of ITEM_MENU_LEFT->buf)
        public readonly List<string> Rows = new List<string>();
        public readonly List<bool> Ok = new List<bool>();
        public int Top, Cursor;
        int Visible { get { return Mode == Kind.Birdramon ? 6 : 3; } }

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
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); }
            }
            var key = new System.Text.StringBuilder().Append(Mode).Append(Top).Append(':');
            foreach (var r in Rows) key.Append(r).Append('|');
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                Color32 white = Font.Colour(1);
                for (int i = 0; i < Visible && Top + i < Rows.Count; i++) DrawCoded(px, Rows[Top + i], 0, i * 12, white);
                if (Mode == Kind.Mojyamon) DrawCoded(px, _label, 0, LabelRow, white);
            });
            Begin();
            DrawBox(Box, () => { if (Mode == Kind.Birdramon) DrawBirdra(); else DrawMojya(); });
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
