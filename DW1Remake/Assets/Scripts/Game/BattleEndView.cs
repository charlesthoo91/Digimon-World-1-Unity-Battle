using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The boxes after a won fight (main/battle_ui.c, btl/battle_hud.c, US build): handleBattleEndBox's three animated UI boxes, box 0 the
    /// stats (-88, -78) 176 x 96 out of the partner, box 1 the Bits (-88, 18) 176 x 31 (66 with text) out of the boy, box 2 the final balance
    /// (-88, -13) 176 x 31 out of where box 1 started; and the battle end text typed into the area (-78, 54) 156 x 24 under the Bits
    /// (BTL_initializeBattleEndText, BTL_append..., BTL_drawBattleEndText, BTL_tickBattleEndText, BTL_isEndBoxTextFinished). The numbers and
    /// bars inside the boxes are drawn by Battle (DrawStatsBox, renderBitBox, renderFinalBalance).
    /// </summary>
    public sealed class BattleEndView : UiBoxPage
    {
        public readonly UiBox Stats = new UiBox { Id = 0 }, Bits = new UiBox { Id = 1 }, Final = new UiBox { Id = 2 };
        float _acc;

        public static BattleEndView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new BattleEndView();
            v.Init(disc, font);
            return v;
        }

        public bool Showing { get { return Stats.State != 0 || Bits.State != 0 || Final.State != 0; } }

        /// <summary>createAnimatedUIBox(id, colour, features, ...): sound 0, it grows out of the start square in 4 frames.</summary>
        public void OpenBox(UiBox b, RectInt rect, RectInt from, int colour, int features)
        {
            Open(b, rect, from, features << 4);
            b.Color = colour; b.Features = features;
        }

        public void CloseAll() { Close(Stats); Close(Bits); Close(Final); }

        // ---- the battle end text (US): '#C' n colour, '#R' a new row, '#W' a new row that waits 60 frames; the character '0' stops the pen
        string _text = "";
        int _cursor, _penX, _penY, _v, _rowsShown, _lineCount, _rowsDrawn, _wait, _waitFrames;
        bool _typing;
        int _colour = 1;
        readonly List<KeyValuePair<Vector3Int, char>> _glyphs = new List<KeyValuePair<Vector3Int, char>>();   // (x, y, colour) and the glyph
        const int VisibleRows = 2;

        /// <summary>BTL_initializeBattleEndText(0x60, 2, (-78, 54, 156, 24)).</summary>
        public void InitText()
        {
            _typing = false; _penX = 0; _penY = _v = 0x60; _text = ""; _cursor = 0; _rowsShown = 0; _lineCount = 0; _rowsDrawn = 0;
            _waitFrames = 0x3C; _wait = _waitFrames; _glyphs.Clear(); _colour = 1;
        }

        public bool HasText { get { return _text.Length > 0; } }

        /// <summary>strcat into BTL_END_BOX_TEXTBUFFER; 'lines' is what the append adds to BATTLE_END_BOX_LINE_COUNT.</summary>
        public void Append(string s, int lines) { _text += s; _lineCount += lines; }

        /// <summary>BTL_drawBattleEndText (US): one glyph a call (codes are read on the way); a row break returns at once.</summary>
        public void DrawStep(bool flag)
        {
            while (flag)
            {
                if (_cursor >= _text.Length) return;
                char c = _text[_cursor];
                if (c == '#')
                {
                    _cursor++;
                    char k = _cursor < _text.Length ? _text[_cursor] : '\0';
                    if (k == 'C') { _cursor++; if (_cursor < _text.Length) { _colour = _text[_cursor] - '0'; _cursor++; } continue; }
                    if (k == 'W') _wait = _waitFrames;
                    if (k == 'W' || k == 'R') { _penX = 0; _cursor++; _penY += 12; _rowsShown++; _rowsDrawn++; return; }
                    continue;
                }
                // the US code stops its pen on the character '0' (it meant the end of the text): "reduced by 10%" / "20%" stopped at the 1 / 2.
                // Ours (fix of glitch Q, user's request 2026-11-09): a '0' is drawn like any other glyph; the end of the text stops the pen
                _glyphs.Add(new KeyValuePair<Vector3Int, char>(new Vector3Int(_penX, _penY, _colour), c));
                _penX += Font != null ? Font.MeasureRaw(c.ToString(), GameFont.ChineseText(_text)) : 8;      // ours: the whole text says whether it is Chinese
                _cursor++;
                return;
            }
        }

        /// <summary>BTL_tickBattleEndText, one game frame; 'pressed' = Cross or Triangle pressed alone this frame.</summary>
        public void TickText(bool pressed)
        {
            if (_wait != 0) _wait--;
            if (_text.Length == 0) { if (pressed) _wait = 0; return; }
            if (_rowsShown == VisibleRows) { if (_wait == 0) { _v += 12; _rowsShown--; } }
            else { DrawStep(_typing); if (_wait != 0) _typing = false; }
            if (pressed) { if (_wait != 0) _wait = 0; else _typing = true; }
        }

        /// <summary>BTL_isEndBoxTextFinished. Ours: once the pen reaches the end of the text the text also counts as done when its wait is over,
        /// so a wrong row count (the US "MP Consumption Bonus" line counted 4 rows and had 1) cannot freeze the game (glitch Q).</summary>
        public bool TextFinished
        {
            get
            {
                if (_wait != 0) return false;
                if (_lineCount == _rowsDrawn) return true;
                return _cursor >= _text.Length;
            }
        }

        /// <summary>Ours (fix of glitch Q, user's request 2026-11-09): the US "MP Consumption Bonus" text has no row break, so it ran off the
        /// 156-wide text area. This splits plain words into rows that fit (glyph widths as DrawStep advances the pen), joined by '#R'.</summary>
        public string WrapRows(string[] words, out int rows)
        {
            const int Width = 156;
            var sb = new System.Text.StringBuilder(); int x = 0; rows = 1;
            foreach (var w in words)
            {
                if (string.IsNullOrEmpty(w)) continue;
                int ww = Measure(w), space = x > 0 ? Measure(" ") : 0;
                if (x > 0 && x + space + ww > Width) { sb.Append("#R"); rows++; x = 0; space = 0; }
                if (space > 0) sb.Append(' ');
                sb.Append(w); x += space + ww;
            }
            return sb.ToString();
        }

        int Measure(string s) { int n = 0; bool zh = GameFont.ChineseText(s); foreach (char c in s) n += Font != null ? Font.MeasureRaw(c.ToString(), zh) : 8; return n; }

        public void Draw(Action stats, Action bits, Action final)
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Stats); Step(Bits); Step(Final); }
            }
            var key = new System.Text.StringBuilder().Append(_glyphs.Count).Append(':').Append(_v);
            BuildPage(key.ToString(), px =>
            {
                if (Font == null) return;
                bool zh = GameFont.ChineseText(_text);
                foreach (var g in _glyphs) Font.RenderRaw(g.Value.ToString(), px, 256, 256, g.Key.x, g.Key.y, Font.Colour(g.Key.z), zh);
            });
            Begin();
            DrawBox(Stats, () => Add(6 - Stats.Id, stats));
            DrawBox(Bits, () =>
            {
                Add(6 - Bits.Id, bits);
                if (_text.Length > 0)
                {
                    // renderBitBox: two lines at y 50 / 51, then BTL_renderBattleEndText: the page from V, 156 x 24 at (-78, 54)
                    int z = 6 - Bits.Id;
                    LineCmd(z, Bgr(0x8E8E8E), -86, 50, 84, 50);
                    LineCmd(z, Bgr(0x121212), -85, 51, 85, 51);
                    String(z, -78, 54, 156, 0, _v, false);
                    String(z, -78, 66, 156, 0, _v + 12, false);
                }
            });
            DrawBox(Final, () => Add(6 - Final.Id, final));
            UiKit.Begin();
            Flush();
            UiKit.End();
        }
    }
}
