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
    /// The link screen is used with the mouse and keyboard (copy / paste of the codes).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        sealed class FriendProfile { public int Type, Hp, Mp, Off, Def, Spd, Brain; public int[] Techs = { 0xFF, 0xFF, 0xFF, 0xFF }; public string Name = ""; }   // Techs: three techniques and the finishing move (0xFF none)

        NetLink _net;
        bool _onlineOn, _profileSent;
        int _onlineRole = -1;                   // -1 not chosen, 1 host, 0 join
        string _friendCode = "";
        FriendProfile _friend;
        GUIStyle _onBox, _onLabel, _onButton, _onField;

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
            yield return ModAsk("Battle with a friend online,\nor enter a tournament?", new[] { "Online battle", "Tournament", "Leave" }, GreymonScript, a => pick = a);
            if (pick == 1) { _routineOn = false; original(); yield break; }
            if (pick != 0) { _routineOn = false; yield break; }

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
                    if (_onlineRole == 1 && _meReady && _friendReady && _friend != null && !_onlineMatch) { _net.SendSure(new[] { MsgStart, (byte)(_onlineOrders ? 1 : 0) }); StartCoroutine(OnlineMatch(true)); }
                    if (_onlineMatch) OnlineFrame();
                }
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
                System.Func<int, int> u16 = i => (m[1 + 2 * i] << 8) | m[2 + 2 * i];
                _friend = new FriendProfile { Type = u16(0), Hp = u16(1), Mp = u16(2), Off = u16(3), Def = u16(4), Spd = u16(5), Brain = u16(6), Name = Encoding.UTF8.GetString(m, 19, m.Length - 19) };
                for (int i = 0; i < 4; i++) _friend.Techs[i] = m[15 + i];
                Debug.Log("[online] friend's partner: " + _friend.Name + " (type " + _friend.Type + ")");
            }
            else ReadMatch(m);
        }

        // ---------------------------------------------------------------- the link screen (mouse and keyboard)

        void OnlineStyles()
        {
            if (_onBox != null) return;
            _onBox = new GUIStyle(GUI.skin.box) { fontSize = 22, alignment = TextAnchor.UpperCenter };
            _onLabel = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
            _onButton = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            _onField = new GUIStyle(GUI.skin.textField) { fontSize = 20 };
        }

        void DrawOnline()
        {
            if (!_onlineOn || _onlineMatch) return;
            OnlineStyles();
            float w = Mathf.Min(900f, Screen.width - 40f), h = 520f;
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.Box(r, "Online battle", _onBox);
            GUILayout.BeginArea(new Rect(r.x + 20, r.y + 50, r.width - 40, r.height - 70));
            if (_onlineRole < 0)
            {
                GUILayout.Label("One of you hosts, the other joins. You will swap two codes (by chat, e-mail...).", _onLabel);
                GUILayout.Space(10);
                if (GUILayout.Button("Host: I make the first code", _onButton, GUILayout.Height(44))) { _onlineRole = 1; _net = new NetLink(true); }
                if (GUILayout.Button("Join: my friend sent me a code", _onButton, GUILayout.Height(44))) { _onlineRole = 0; _net = new NetLink(false); }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", _onButton, GUILayout.Height(40))) _onlineOn = false;
            }
            else if (_net != null)
            {
                GUILayout.Label(_net.Status, _onLabel);
                GUILayout.Space(8);
                bool host = _onlineRole == 1;
                if (_net.MyCode != null && _net.Now != NetLink.State.Linked)
                {
                    GUILayout.Label(host ? "1. Your code - send it to your friend:" : "2. Your reply code - send it back to your friend:", _onLabel);
                    GUILayout.BeginHorizontal();
                    GUILayout.TextField(_net.MyCode, _onField, GUILayout.Height(36));
                    if (GUILayout.Button("Copy", _onButton, GUILayout.Width(110), GUILayout.Height(36))) GUIUtility.systemCopyBuffer = _net.MyCode;
                    GUILayout.EndHorizontal();
                }
                if (_net.Now == NetLink.State.Ready)
                {
                    GUILayout.Space(8);
                    GUILayout.Label(host ? "2. Paste your friend's reply code:" : "1. Paste your friend's code:", _onLabel);
                    GUILayout.BeginHorizontal();
                    _friendCode = GUILayout.TextField(_friendCode, _onField, GUILayout.Height(36));
                    if (GUILayout.Button("Paste", _onButton, GUILayout.Width(110), GUILayout.Height(36))) _friendCode = GUIUtility.systemCopyBuffer ?? "";
                    GUILayout.EndHorizontal();
                    if (GUILayout.Button("Link", _onButton, GUILayout.Height(40))) _net.UseFriendCode(_friendCode);
                }
                if (_net.Now == NetLink.State.Linked)
                {
                    GUILayout.Label("Ping: " + _net.PingMs + " ms", _onLabel);
                    if (_onlineResult != null) GUILayout.Label("Last match: " + _onlineResult, _onLabel);
                    if (_friend == null) GUILayout.Label("Waiting for your friend's Digimon...", _onLabel);
                    else
                    {
                        var info = DigimonStatsCatalog.Get(_disc, _friend.Type);
                        GUILayout.Label("Your friend's partner: " + _friend.Name + " (" + (info != null ? info.Name : "type " + _friend.Type) + ")", _onLabel);
                        GUILayout.Label("HP " + _friend.Hp + "   MP " + _friend.Mp + "   Off " + _friend.Off + "   Def " + _friend.Def + "   Speed " + _friend.Spd + "   Brains " + _friend.Brain, _onLabel);
                        GUILayout.Space(6);
                        if (_onlineRole == 1)
                        {
                            if (GUILayout.Button("Orders: " + (_onlineOrders ? "ON - each player gives orders to their own Digimon" : "OFF - both Digimon fight by their own judgement"), _onButton, GUILayout.Height(40)))
                                _onlineOrders = !_onlineOrders;
                        }
                        else GUILayout.Label("The host chooses whether players give orders. A tournament match; the host's PC runs the fight.", _onLabel);
                        if (!_meReady) { if (GUILayout.Button("Battle!", _onButton, GUILayout.Height(44))) { _meReady = true; _net.SendSure(new[] { MsgReady }); } }
                        else GUILayout.Label(_friendReady ? "Starting..." : "Waiting for your friend to press Battle!...", _onLabel);
                    }
                }
                if (_net.Now == NetLink.State.Failed || _net.Now == NetLink.State.Closed)
                {
                    if (GUILayout.Button("Try again", _onButton, GUILayout.Height(40))) { _net.Dispose(); _net = null; _onlineRole = -1; _friendCode = ""; _friend = null; _profileSent = false; _meReady = _friendReady = false; }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", _onButton, GUILayout.Height(40))) _onlineOn = false;
            }
            GUILayout.EndArea();
        }
    }
}
