using System.Collections.Generic;
using System.Text;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The game's character setup at the start of a new game: Jijimon asks a few questions (Yes/No, day or night) and you
    /// type your name and your Digimon's name on a letter grid. The game does this in program code, so the flow is rebuilt
    /// here, but every line of text is read from the player's disc (script bank at the start of DG.SCN).
    /// </summary>
    public sealed class CharacterSetup
    {
        // text positions inside that script (US disc)
        const int TIntro = 22242, TDigivice = 22342, OYesNo1 = 22416, TSee1 = 22442, TDayNight = 22482, ODayNight = 22618, TSee2 = 22650,
                  TFriends = 22690, OYesNo2 = 22758, TAlright = 22784, TName = 22828, TNameOk = 22890, OYesNo3 = 22940,
                  TDName = 22966, TDNameOk = 23048, TGood = 23098, TEnd = 23146;

        enum Mode { Say, Ask, Edit }

        readonly byte[] _d;
        readonly GameMenus _m;
        int _pc;
        Mode _mode;
        string _speaker = "", _body = "", _header = "";
        List<string> _opts = new List<string>();
        int _sel, _row, _col;
        string _buf = "";
        bool _partner;

        public bool Active { get; private set; }
        public int DayNight;                 // 0 = day, 1 = night (Jijimon's question)
        public System.Action OnDone;
        public bool DrawBackground = true;   // false when a 3D stage with its own black background is showing

        public CharacterSetup(byte[] scriptBank, GameMenus menus) { _d = scriptBank; _m = menus; }

        public void Start()
        {
            if (_d == null) { Finish(); return; }
            Active = true; _pc = 0; Enter();
        }

        void Finish() { Active = false; if (OnDone != null) OnDone(); }

        // ------------------------------------------------------------------ text
        static string Decode(byte[] d, int p)
        {
            if (d == null || p < 0 || p + 2 > d.Length || d[p] != 0x1A) return "";
            var sb = new StringBuilder();
            int j = p + 2;
            while (j < d.Length)
            {
                int c = d[j];
                if ((c >= 0x81 && c <= 0x9F) || (c >= 0xE0 && c <= 0xFC)) { if (j + 1 >= d.Length) break; sb.Append(MapScripts.Pair(c << 8 | d[j + 1])); j += 2; }
                else if (c == 0x0D) { sb.Append('\n'); j += 2; }
                else if (c == 0x01) j += 2;
                else if (c == 0x00) break;
                else if (c < 0x20) { sb.Append("[name]"); j += 2; }
                else j++;
            }
            return sb.ToString();
        }

        void Say(int off, string name = null)
        {
            string t = Decode(_d, off);
            if (name != null) t = t.Replace("[name]", name);
            int nl = t.IndexOf('\n');
            string first = nl >= 0 ? t.Substring(0, nl).Trim() : "";
            _speaker = first; _body = nl >= 0 ? t.Substring(nl + 1).Trim() : t.Trim();
            _mode = Mode.Say;
        }

        // a question box: the first line is a header (a name, or "???" when nobody is named), the other lines are the answers
        void Ask(int off, string headerName = null)
        {
            string t = Decode(_d, off);
            var lines = t.Split('\n');
            string head = lines.Length > 0 ? lines[0].Trim() : "";
            head = head.Replace("[name]", headerName ?? "");
            _header = head.Length > 0 ? head : "???";
            _opts = new List<string>();
            for (int i = 1; i < lines.Length; i++) { var l = lines[i].Trim(); if (l.Length > 0) _opts.Add(l); }
            _sel = 0; _mode = Mode.Ask;
        }

        void Edit(bool partner)
        {
            _partner = partner; _buf = ""; _row = 0; _col = 0; _mode = Mode.Edit;
        }

        // the questions, in the order the game asks them
        void Enter()
        {
            switch (_pc)
            {
                case 0: Say(TIntro); break;
                case 1: Say(TDigivice); break;
                case 2: Ask(OYesNo1); break;
                case 3: Say(TSee1); break;
                case 4: Say(TDayNight); break;
                case 5: Ask(ODayNight); break;
                case 6: Say(TSee2); break;
                case 7: Say(TFriends); break;
                case 8: Ask(OYesNo2); break;
                case 9: Say(TAlright); break;
                case 10: Say(TName); break;
                case 11: Edit(false); break;
                case 12: Say(TNameOk, _m.PlayerName); break;
                case 13: Ask(OYesNo3, _m.PlayerName); break;
                case 14: Say(TDName); break;
                case 15: Edit(true); break;
                case 16: Say(TDNameOk, _m.PartnerName); break;
                case 17: Ask(OYesNo3, _m.PartnerName); break;
                case 18: Say(TGood); break;
                case 19: Say(TEnd); break;
                default: Finish(); break;
            }
        }

        void Next() { _pc++; Enter(); }

        // ------------------------------------------------------------------ name grid
        static string Cell(int r, int c)
        {
            const string up = "ABCDEFGHIJKLMNOPQRSTUVWXY", lo = "abcdefghijklmnopqrstuvwxy";
            if (r <= 4) return c < 5 ? up[r * 5 + c].ToString() : lo[r * 5 + (c - 5)].ToString();
            if (r == 5) return c == 0 ? "Z" : c == 5 ? "z" : "";
            if (r == 6) return c < 5 ? "Back" : (c - 5).ToString();
            return c < 5 ? "OK" : (5 + c - 5).ToString();
        }

        // ------------------------------------------------------------------ input
        public void Tick(int step, int side, bool action, bool cancel)
        {
            if (!Active) return;
            switch (_mode)
            {
                case Mode.Say:
                    if (action) Next();
                    break;
                case Mode.Ask:
                    if (_opts.Count > 0 && step != 0) _sel = (_sel + step + _opts.Count) % _opts.Count;
                    if (action) Answer();
                    break;
                case Mode.Edit:
                    if (step != 0) _row = (_row + step + 8) % 8;
                    if (side != 0) _col = (_col + side + 10) % 10;
                    if (cancel && _buf.Length > 0) _buf = _buf.Substring(0, _buf.Length - 1);
                    if (action) Press();
                    break;
            }
        }

        void Answer()
        {
            if (_pc == 5) DayNight = _sel;
            if ((_pc == 13 || _pc == 17) && _sel == 1) { _pc = _pc == 13 ? 10 : 14; Enter(); return; }   // "No": ask again
            Next();
        }

        void Press()
        {
            string c = Cell(_row, _col);
            if (c.Length == 0) return;
            if (c == "Back") { if (_buf.Length > 0) _buf = _buf.Substring(0, _buf.Length - 1); return; }
            if (c == "OK")
            {
                if (_buf.Length == 0) return;
                if (_partner) _m.PartnerName = _buf; else _m.PlayerName = _buf;
                Next();
                return;
            }
            if (_buf.Length < 8) _buf += c;
        }

        // ------------------------------------------------------------------ drawing
        public void OnGUI()
        {
            if (!Active) return;
            if (DrawBackground) UiKit.Fill(new Rect(0, 0, Screen.width, Screen.height), Color.black);
            UiKit.Begin();
            if (_mode == Mode.Edit) DrawEdit();
            else DrawChat();
            UiKit.End();
        }

        void DrawChat()
        {
            // like the original: one wide box near the top of the screen, Jijimon standing below it on the right
            var r = new Rect(140, 165, 1145, 270);
            UiKit.Box(r, new Color(0.16f, 0.22f, 0.26f, 0.98f));
            if (_mode == Mode.Ask)
            {
                UiKit.Text(new Rect(r.x + 30, r.y + 8, r.width - 60, 56), _header, 42, new Color(0.55f, 0.65f, 1f));
                for (int i = 0; i < _opts.Count; i++)
                {
                    float y = r.y + 68 + i * 62;
                    if (i == _sel) UiKit.Fill(new Rect(r.x + 12, y, 285, 60), new Color(0.35f, 0.38f, 0.40f));
                    UiKit.Text(new Rect(r.x + 24, y, 400, 60), _opts[i], 46, Color.white, TextAnchor.MiddleLeft);
                }
                return;
            }
            float ty = r.y + 22;
            if (_speaker.Length > 0) { UiKit.Text(new Rect(r.x + 30, r.y + 8, 800, 60), _speaker, 48, UiKit.Yellow); ty = r.y + 68; }
            UiKit.Text(new Rect(r.x + 30, ty, r.width - 60, r.height - (ty - r.y) - 10), _body, 46, Color.white, TextAnchor.UpperLeft, true);
            if ((int)(Time.unscaledTime * 2f) % 2 == 0) UiKit.Text(new Rect(r.xMax - 100, r.yMax - 75, 60, 50), "X", 40, new Color(0.5f, 0.6f, 1f), TextAnchor.MiddleCenter);
        }

        void DrawEdit()
        {
            var panel = new Rect(150, 40, 1115, 650);
            UiKit.Box(panel, UiKit.PanelFill);
            UiKit.Text(new Rect(panel.x + 40, panel.y + 24, 300, 70), "Name", 50, UiKit.Yellow);
            const float x0 = 470, y0 = 70, cw = 62, ch = 70;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 10; c++)
                {
                    string t = Cell(r, c);
                    if (t.Length == 0) continue;
                    bool special = t == "Back" || t == "OK";
                    if (special && c != 0) continue;           // Back and OK are drawn once, at the left
                    float x = special ? panel.x + 70 : x0 + c * cw + (c >= 5 ? 90 : 0);
                    float y = y0 + r * ch + 10;
                    bool on = r == _row && (c == _col || (special && _col < 5 && _row == r));
                    if (on) UiKit.Fill(new Rect(x - 8, y - 4, special ? 150 : cw - 8, ch - 12), new Color(1f, 0.75f, 0.1f));
                    if (on) UiKit.Fill(new Rect(x - 4, y, special ? 142 : cw - 16, ch - 20), new Color(0.12f, 0.18f, 0.22f));
                    UiKit.Text(new Rect(x - 4, y - 4, special ? 150 : cw - 8, ch - 12), t, special ? 44 : 46, (t == "OK" && _buf.Length == 0) ? UiKit.Grey : Color.white, special ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter);
                }
            var nb = new Rect(150, 720, 480, 130);
            UiKit.Box(nb, UiKit.PanelFill);
            UiKit.Text(new Rect(nb.x + 28, nb.y + 14, 420, 50), _partner ? "Digimon's name" : "Your name", 40, UiKit.Yellow);
            string shown = _buf + ((int)(Time.unscaledTime * 2f) % 2 == 0 ? "_" : " ");
            UiKit.Text(new Rect(nb.x + 28, nb.y + 62, 420, 60), shown, 50, Color.white);
            UiKit.Text(new Rect(660, 790, 700, 60), "Arrows move, Space types, Tab erases", 30, UiKit.Grey);
        }
    }
}
