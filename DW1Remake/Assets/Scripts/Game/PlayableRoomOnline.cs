using System.Collections;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod "Online battle", ModSettings.OnlineBattle; not in the original game). In Greymon's arena (ROOM13 / ROOM19, map Digimon
    /// record 0, script 5) talking to Greymon first asks: online battle, tournament (the original script) or leave. Online battle opens the
    /// link screen: one PC hosts and gives a code, the friend pastes it and gives a reply code, the host pastes that, and the two PCs link
    /// directly (NetLink). Once linked they swap their partners' profiles; when both press Battle! the match runs (PlayableRoomOnlineMatch).
    /// The link screen uses the game's boxes and the pad keys; the codes go through the PC's clipboard (Copy / Paste).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        sealed class FriendProfile { public int Type, Hp, Mp, Off, Def, Spd, Brain; public int[] Techs = { 0xFF, 0xFF, 0xFF, 0xFF }; public string Name = ""; }   // Techs: three techniques and the finishing move (0xFF none)

        NetLink _net;
        bool _onlineOn, _profileSent;
        int _onlineRole = -1;                   // -1 not chosen, 1 host, 0 join
        string _friendCode = "";
        FriendProfile _friend;

        const byte MsgProfile = 1;

        const int GreymonScript = 5, GreymonCounterTile = 81;     // ROOM13 / ROOM19: map Digimon record 0 (type 178) and the action tiles in front of his counter

        bool InGreymonArena { get { if (_mapId < 0 || _mapId >= _mapNames.Count) return false; string m = _mapNames[_mapId]; return m == "ROOM13" || m == "ROOM19"; } }

        // Cross on Greymon himself with the mod on: our question first
        bool OnlineArenaTalk(Npc n)
        {
            if (!ModSettings.OnlineBattle || n == null || n.Data.Script != GreymonScript || !InGreymonArena) return false;
            StartCoroutine(OnlineArena(() => StartScript(GreymonScript, n)));
            return true;
        }

        // Cross at his counter (action tile 81: the way the boy really talks to him, he stands behind it): our question first
        bool OnlineArenaTile(int cell)
        {
            if (!ModSettings.OnlineBattle || cell != GreymonCounterTile || !InGreymonArena || _runner != null || _routineOn) return false;
            StartCoroutine(OnlineArena(() => TryDoor(cell)));
            return true;
        }

        // original = what the game does without the mod (Greymon's script / the counter tile's section)
        IEnumerator OnlineArena(System.Action original)
        {
            _routineOn = true;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            int pick = 0;
            yield return ModAsk("Battle with a friend online,\nor enter a tournament?", new[] { "Online", "Tournament", "Leave" }, GreymonScript, a => pick = a);
            if (pick == 1) { _routineOn = false; original(); yield break; }
            if (pick != 0) { _routineOn = false; yield break; }
            // ours: the box holds three answers, so "Online" asks which (the online tournament: PlayableRoomOnlineTournament)
            yield return ModAsk("A battle with one friend,\nor a tournament of up to 8 players?", new[] { "Online battle", "Online tournament", "Back" }, GreymonScript, a => pick = a);
            if (pick == 1) { yield return OnlineTournament(); _routineOn = false; yield break; }
            if (pick != 0) { _routineOn = false; yield break; }

            _onPos = 0; _onNote = null;
            _onlineOn = true; _onlineRole = -1; _friendCode = ""; _friend = null; _profileSent = false; _meReady = _friendReady = false;
            // a linked game must keep running when its window is not in front (a player build stops otherwise, and the link times out)
            bool wasBackground = Application.runInBackground;
            Application.runInBackground = true;
            while (_onlineOn)
            {
                if (_net != null)
                {
                    _net.Tick();
                    if (_net.Now == NetLink.State.Linked && !_profileSent) { _net.SendSure(MyProfile()); _profileSent = true; }
                    byte[] m;
                    while ((m = _net.Receive()) != null) ReadOnline(m);
                    // both pressed Battle!: the host says start, and both PCs go to the arena
                    if (_onlineRole == 1 && _meReady && _friendReady && _friend != null && !_onlineMatch) { _net.SendSure(new[] { MsgStart, (byte)(_onlineOrders ? 1 : 0), (byte)_onlineTime }); StartCoroutine(OnlineMatch(true)); }
                    if (_onlineMatch) OnlineFrame();
                }
                // the link screen while no match runs (the match has the arena's own screens)
                if (!_onlineMatch) OnlineScreenTick();
                else if (_sys != null && _sys == _onSys) { _sys = null; _onSys = null; }
                yield return null;
            }
            if (_net != null) { _net.Dispose(); _net = null; }
            Application.runInBackground = wasBackground;
            _routineOn = false;
        }

        // a question in the dialogue box with our own words (HeadAsk takes the disc's texts); Triangle / Circle = the last answer
        // as the game's own questions do (1B speaker, text; 1B FD, 10 choices): the speaker's line first, Cross, then the boy's answers in a
        // fresh box (the box has three rows: a two-line question and three answers do not fit together)
        IEnumerator ModAsk(string question, string[] options, int speaker, System.Action<int> answer)
        {
            question = DW1Remake.Formats.Tr.Text(question, "ours");      // ours: in the current language
            var tr = new string[options.Length];
            for (int i = 0; i < options.Length; i++) tr[i] = DW1Remake.Formats.Tr.Text(options[i], "ours");
            options = tr;
            _line = question; _choiceOpts = null; _speaker = speaker;
            yield return null;
            while (!ActionPressed()) yield return null;
            Snd(3);
            _line = null; _choiceOpts = new List<string>(options); _speaker = 0xFD; _choiceSel = 0;
            yield return null;
            while (true)
            {
                int ms = MenuStep();
                if (ms != 0) { _choiceSel = (_choiceSel + ms + options.Length) % options.Length; Snd(2); }
                if (ActionPressed()) { Snd(3); break; }
                if (GameMenus.CancelKey()) { Snd(4); _choiceSel = options.Length - 1; break; }
                yield return null;
            }
            int pick = _choiceSel;
            _line = null; _choiceOpts = null;
            answer(pick);
            yield return null;
        }

        // ---------------------------------------------------------------- what the two PCs tell each other

        // profile: message 1, then type, max HP, max MP, Off, Def, Speed, Brains (u16 each), the three techniques and the finishing move (a byte
        // each, 0xFF none) and the partner's name (UTF-8)
        byte[] MyProfile()
        {
            var name = Encoding.UTF8.GetBytes(_state.PartnerName ?? "");
            var b = new byte[1 + 14 + 4 + name.Length];
            b[0] = MsgProfile;
            int[] v = { PartnerType, _state.Stats[4], _state.Stats[5], _state.Stats[0], _state.Stats[1], _state.Stats[2], _state.Stats[3] };
            for (int i = 0; i < v.Length; i++) { b[1 + 2 * i] = (byte)(v[i] >> 8); b[2 + 2 * i] = (byte)v[i]; }
            int[] t = MyTechs();
            for (int i = 0; i < 4; i++) b[15 + i] = (byte)t[i];
            name.CopyTo(b, 19);
            return b;
        }

        // the partner's fight techniques as Battle.SetupTechs picks them (known ones in the Techset, up to three) and its finishing move
        int[] MyTechs()
        {
            var r = new[] { 0xFF, 0xFF, 0xFF, 0xFF };
            var info = DigimonStatsCatalog.Get(_disc, PartnerType);
            if (info == null) return r;
            int n = 0;
            for (int i = 0; i < info.Learnable.Count && n < 3; i++)
                if (_state.Techs[info.Learnable[i]] && (!_state.TechSet.Exists(x => x >= 0) || _state.TechSet.Contains(info.Learnable[i]))) r[n++] = info.Learnable[i];
            if (n == 0 && info.Learnable.Count > 0) r[0] = info.Learnable[0];
            if (info.SignatureId >= 0) r[3] = info.SignatureId;
            return r;
        }

        void ReadOnline(byte[] m)
        {
            if (m.Length < 1) return;
            if (m[0] == MsgProfile && m.Length >= 19)
            {
                _friend = ParseProfile(m);
                Debug.Log("[online] friend's partner: " + _friend.Name + " (type " + _friend.Type + ")");
            }
            else ReadMatch(m);
        }

        // a profile message (MyProfile) back into its numbers; null when it is too short
        static FriendProfile ParseProfile(byte[] m)
        {
            if (m == null || m.Length < 19 || m[0] != MsgProfile) return null;
            System.Func<int, int> u16 = i => (m[1 + 2 * i] << 8) | m[2 + 2 * i];
            var p = new FriendProfile { Type = u16(0), Hp = u16(1), Mp = u16(2), Off = u16(3), Def = u16(4), Spd = u16(5), Brain = u16(6), Name = Encoding.UTF8.GetString(m, 19, m.Length - 19) };
            for (int i = 0; i < 4; i++) p.Techs[i] = m[15 + i];
            return p;
        }

        // ---------------------------------------------------------------- the link screen, in the game's own boxes, font and cursor (SysScreen,
        // DrawSysScreen, as the save and title menus): up / down and Cross; Triangle / Circle closes. Copy and paste use the PC's clipboard

        int _onPos;
        string _onNote; float _onNoteUntil;
        SysScreen _onSys;
        const int OnX = 0x22, OnBoxX = 0x18, OnBoxW = 0x110;

        // a sentence broken at spaces to fit the box (the font's own widths)
        static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>(); var font = Battle.StatFont;
            foreach (var para in (text ?? "").Split('\n'))
            {
                // ours: Chinese has no spaces between words; it breaks between any two characters (a Chinese character is about 12 pixels)
                if (HasCjk(para) && font != null && font.HasChinese) { lines.AddRange(DW1Remake.Formats.Tr.Wrap(para, width, font.MeasureRaw)); continue; }
                if (HasCjk(para))
                {
                    string line = ""; int lw = 0;
                    foreach (char ch in para)
                    {
                        int cw = ch >= 0x2E80 ? 12 : 6;
                        if (lw + cw > width && line.Length > 0) { lines.Add(line); line = ""; lw = 0; }
                        line += ch; lw += cw;
                    }
                    lines.Add(line);
                    continue;
                }
                string cur = "";
                foreach (var w in para.Split(' '))
                {
                    string next = cur.Length == 0 ? w : cur + " " + w;
                    if (font != null && cur.Length > 0 && font.Measure(next) > width) { lines.Add(cur); cur = w; }
                    else cur = next;
                }
                lines.Add(cur);
            }
            return lines;
        }

        // the PC's clipboard; in the browser its own (asked for once by the browser), whose answer comes later (OnWebPaste)
        System.Action<string> _pasteTo;
        static void CopyText(string t)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebFiles.CopyText(t);
#else
            GUIUtility.systemCopyBuffer = t;
#endif
        }

        void PasteText(System.Action<string> to)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _pasteTo = to; WebFiles.PasteText(gameObject.name, "OnWebPaste");
#else
            to(GUIUtility.systemCopyBuffer ?? "");
#endif
        }

        /// <summary>The browser's clipboard text (DW1Web.jslib DW1_PasteText); "" = empty or not allowed.</summary>
        public void OnWebPaste(string text)
        {
            var to = _pasteTo; _pasteTo = null;
            if (text.Length == 0) { _onNote = "Nothing to paste. Copy the code again."; _onNoteUntil = Time.time + 4f; return; }
            if (to != null) to(text);
        }

        void OnlineScreenTick()
        {
            var lines = new List<string>(); var opts = new List<string>(); var acts = new List<System.Action>();
            int wrap = OnBoxW - 0x14;
            System.Action close = () => { _onlineOn = false; };
            if (_net == null)
            {
                lines.AddRange(Wrap(DW1Remake.Formats.Tr.Text("One of you hosts and the other joins. You swap two codes by chat or e-mail.", "ours"), wrap));
                opts.Add("Host. I make the first code."); acts.Add(() => { _onlineRole = 1; _net = new NetLink(true); _onPos = 0; });
                opts.Add("Join. My friend has a code."); acts.Add(() => { _onlineRole = 0; _net = new NetLink(false); _onPos = 0; });
            }
            else if (_net.Now == NetLink.State.Linked)
            {
                lines.Add(DW1Remake.Formats.Tr.Format("Linked.   Ping {0} ms", _net.PingMs));
                if (_onlineResult != null) lines.Add(DW1Remake.Formats.Tr.Format("Last match   {0}", DW1Remake.Formats.Tr.Text(_onlineResult, "ours")));
                if (_friend == null) lines.Add("Waiting for your friend's Digimon...");
                else
                {
                    var info = DigimonStatsCatalog.Get(_disc, _friend.Type);
                    lines.Add(DW1Remake.Formats.Tr.Format("Friend   {0}   {1}", _friend.Name, info != null ? info.Name : ""));
                    lines.Add("HP " + _friend.Hp + "   MP " + _friend.Mp);
                    lines.Add(DW1Remake.Formats.Tr.Format("Off {0}  Def {1}  Spd {2}  Brn {3}", _friend.Off, _friend.Def, _friend.Spd, _friend.Brain));
                    if (_onlineRole == 1)
                    {
                        opts.Add(DW1Remake.Formats.Tr.Format("Orders   {0}", DW1Remake.Formats.Tr.Text(_onlineOrders ? "ON" : "OFF", "ours"))); acts.Add(() => { _onlineOrders = !_onlineOrders; });
                        // ours: the match's time, 99 seconds (the arena's), 300, or none
                        opts.Add(DW1Remake.Formats.Tr.Format("Time   {0}", OnlineTimeName(_onlineTime))); acts.Add(() => { _onlineTime = (_onlineTime + 1) % 3; });
                    }
                    else { lines.Add(_onlineOrders ? "Orders ON" : "The host chooses the orders."); lines.Add("The host chooses the time limit."); }
                    if (!_meReady) { opts.Add("Battle!"); acts.Add(() => { _meReady = true; _net.SendSure(new[] { MsgReady }); }); }
                    else lines.Add(_friendReady ? "Starting..." : "Waiting for your friend's Battle!...");
                }
            }
            else if (_net.Now == NetLink.State.Failed || _net.Now == NetLink.State.Closed)
            {
                lines.AddRange(Wrap(DW1Remake.Formats.Tr.Text(_net.Status, "online status"), wrap));
                opts.Add("Try again");
                acts.Add(() => { _net.Dispose(); _net = null; _onlineRole = -1; _friendCode = ""; _friend = null; _profileSent = false; _meReady = _friendReady = false; _onPos = 0; });
            }
            else
            {
                lines.AddRange(Wrap(DW1Remake.Formats.Tr.Text(_net.Status, "online status"), wrap));
                if (_net.MyCode != null)
                {
                    lines.Add(_onlineRole == 1 ? "Your code" : "Your reply code");
                    lines.AddRange(Wrap(NetLink.Spaced(_net.MyCode), wrap));      // the groups of five go on as many lines as the box needs
                    opts.Add("Copy my code"); acts.Add(() => { CopyText(_net.MyCode); _onNote = "Your code is copied."; _onNoteUntil = Time.time + 2.5f; });
                }
                if (_net.WantsFriendCode)
                {
                    opts.Add(_onlineRole == 1 ? "Paste reply code" : "Paste friend's code");
                    acts.Add(() => PasteText(t => { if (_net == null) return; _friendCode = t ?? ""; _net.UseFriendCode(_friendCode); }));
                }
            }
            if (_onNote != null && Time.time < _onNoteUntil) lines.Add(_onNote);
            opts.Add("Close"); acts.Add(close);
            if (lines.Count > 9) lines.RemoveRange(9, lines.Count - 9);

            // the title box, the text box, the choices box under it
            var s = new SysScreen();
            s.Text("ONLINE BATTLE", OnX, 0x15); s.Box(OnBoxX, 0x10, OnBoxW, 0x16);
            int y = 0x31;
            foreach (var l in lines) { s.Text(l, OnX, y); y += 12; }
            s.Box(OnBoxX, 0x2C, OnBoxW, 12 * lines.Count + 10);
            int oy = 0x2C + 12 * lines.Count + 10 + 6 + 5;
            for (int i = 0; i < opts.Count; i++) s.Text(opts[i], OnX, oy + 12 * i);
            s.Box(OnBoxX, oy - 5, OnBoxW, 12 * opts.Count + 10);
            if (_onPos >= opts.Count) _onPos = opts.Count - 1;
            s.Cursor(OnX - 3, oy, OnBoxW - 0x10, 0xC, 12, opts.Count);
            s.Pos = _onPos;
            _sys = _onSys = s;

            int ms = MenuStep();
            if (ms != 0) { _onPos = (_onPos + ms + opts.Count) % opts.Count; Snd(2); }
            else if (ActionPressed()) { Snd(3); acts[_onPos](); }
            else if (GameMenus.CancelKey()) { Snd(4); close(); }
            if (!_onlineOn && _sys == _onSys) { _sys = null; _onSys = null; }
        }
    }
}
