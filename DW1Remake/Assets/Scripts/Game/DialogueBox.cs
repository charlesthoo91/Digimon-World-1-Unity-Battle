using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The script dialogue box (main/script_textbox.c, US build): UI box 0 at (-0x82, 0x2A) 0x106 x 0x3B (setupDialogueBox), growing out of a 10 x 10
    /// square on the speaker (setupBoxOrigin) or appearing at once when nobody speaks; it closes at once (no 0x40 flag: removeStaticUIBox).
    /// showTextbox writes the speaker's name in its colour (6 the boy, 0x0A the partner, 5 from 0xC8 on, else 7) as the first row, then the text;
    /// renderScriptDialogueBox shows 4 rows of 12 at (5, 4) inside the box, 13 apart, the selection cursor (renderDialogueSelectionCursor)
    /// and, while it waits for Cross, the "next" mark (renderUIBox: SYSTEM_W page (320,128) u 0x4C + 16 x (timer / 4 % 3), v 0xB7, CLUT (96,500)).
    /// </summary>
    public sealed class DialogueBox : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 0 };
        public int Owner = -1;                      // CURRENT_DIALOGUE_OWNER the box was set up for (-1: none open)

        // setupDialogueBox: rect (-0x82, 0x2A, 0x106, 0x3B), flags 0x21 (| 0x80 when it grows out of the speaker)
        static readonly RectInt BoxRect = new RectInt(-0x82, 0x2A, 0x106, 0x3B);
        const int Flags = 0x21;

        string _name; int _nameColour;
        readonly List<string> _rows = new List<string>();
        int _choiceRow = -1, _choices, _cursorWidth;
        public int Current;                         // DIALOGUE_SELECTION.current
        public bool Waiting;                        // MAIN_D_80134F94: a text waits for Cross (the "next" mark shows)
        int _timer;                                 // TEXTBOX_OPEN_TIMER
        float _acc;

        public static DialogueBox Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32")) return null;
            var b = new DialogueBox();
            b.Init(disc, font);
            return b;
        }

        public bool Showing { get { return Box.State != 0; } }

        /// <summary>setDialogueOwner -> setupDialogueBox: a new box for a new speaker. origin = the speaker's 10 x 10 square on screen, null when there is none.</summary>
        public void Setup(int owner, RectInt? origin)
        {
            CloseNow(Box);
            Owner = owner;
            if (origin.HasValue) Open(Box, BoxRect, origin.Value, Flags | 0x80);
            else OpenStatic(Box, BoxRect, Flags);
            Clear();
        }

        /// <summary>closeAllTextboxes / closeBox at the end of a script: gone at once.</summary>
        public void Shut() { CloseNow(Box); Owner = -1; Clear(); }

        public void Clear() { _rows.Clear(); _name = null; _choiceRow = -1; _choices = 0; Waiting = false; }

        /// <summary>showTextbox: the speaker's name (unless 0xFF) then the text's rows; choices = how many of the last rows are a selection.</summary>
        public void Show(int speaker, string name, string text, int choices = 0)
        {
            Clear();
            if (speaker != 0xFF && !string.IsNullOrEmpty(name))
            {
                _name = name;
                _nameColour = speaker == 0xFD ? 6 : speaker == 0xFC ? 0x0A : speaker >= 0xC8 ? 5 : 7;
            }
            if (!string.IsNullOrEmpty(text)) _rows.AddRange(text.Split('\n'));
            _choices = choices;
            if (choices > 0)
            {
                _choiceRow = _rows.Count - choices;
                // scriptShowSelection: cursorWidth = the longest row in characters x GLYPH_WIDTH (12) + 2, at most 0xF0
                int cols = 0;
                for (int i = _choiceRow; i < _rows.Count; i++) cols = Mathf.Max(cols, ScriptRunner.Plain(_rows[i]).Length);
                _cursorWidth = Mathf.Min(0xF0, cols * 12 + 2);
                Current = 0;
            }
            Waiting = choices == 0;
            _timer = 0;
        }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); _timer++; }
            }
            BuildText();
            Begin();
            DrawBox(Box, () => DrawText());
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // the text page: row 0 the name (01 colour, name, 01 01), then the rows in colour 1 unless a code changes it
        void BuildText()
        {
            var key = new System.Text.StringBuilder().Append(_name).Append('|').Append(_nameColour);
            foreach (var r in _rows) key.Append('|').Append(r);
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                int row = 0;
                if (_name != null) Font.Render(_name, px, 256, 256, 0, (row++) * 12, Font.Colour(_nameColour));
                foreach (var r in _rows) Font.Render(r, px, 256, 256, 0, (row++) * 12, Font.Colour(1));
            });
        }

        // renderScriptDialogueBox: 4 rows of the page, 0xFC wide, from (box + 5, box + 4), 13 apart; then the cursor or the "next" mark
        void DrawText()
        {
            int z = 6 - Box.Id, x = Box.Rect.x + 5, y = Box.Rect.y + 4;
            for (int i = 0; i < 4; i++) String(z, x, y + i * 13, 0xFC, 0, i * 12);
            if (_choiceRow >= 0)
            {
                // renderDialogueSelectionCursor (US): (x - 1, y + cursorOffsetY + current x 13 - 2), cursorOffsetY 13 when a name is shown
                int first = (_name != null ? 1 : 0) + _choiceRow;
                SelectionCursor(z, x - 1, y + first * 13 + Current * 13 - 2, _cursorWidth, 0x0D);
            }
            else if (Waiting)
            {
                // renderUIBox(0) at (box + 0xEF, box + 0x2B)
                int u = _timer / 4 % 3 * 16 + 0x4C;
                SysIcon(z, Box.Rect.x + 0xEF, Box.Rect.y + 0x2B, 16, 12, u, 0xB7, 12);
            }
        }

        /// <summary>tickSelectionDialogue: up / down wrap round (sound 2).</summary>
        public void Move(int step)
        {
            if (_choices <= 0 || step == 0) return;
            Current = (Current + step + _choices) % _choices;
            Snd(2);
        }
    }
}
