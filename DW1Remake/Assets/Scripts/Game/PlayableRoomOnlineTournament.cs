using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod "Online battle"; not in the original game; docs/ONLINE-TOURNAMENT-PLAN.md): an online tournament of up to 8 players at
    /// Greymon's arena, empty places filled by computer Digimon, with the arena tournament's rules (STD_tournamentMain / STD_setupParticipants):
    /// 8 places at random, three knock-out rounds, the cup's entry lists and opponents, computer vs computer decided by STD_WIN_CHANCES.
    ///
    /// Anyone hosts (the host fights too). The tournament service (TournamentClient, a Cloudflare Worker) lists the open tournaments and passes
    /// the WebRTC link codes between the host's game and each player's, so nobody copies codes. Every player then has a direct link to the host
    /// (NetLink). The host's game keeps the bracket; each match is played by its own players: against a computer Digimon on that player's PC
    /// (OnlineMatch with cpuType), player against player as the online battle's match (OnlineMatch: one PC runs the fight, the other copies it):
    /// over the host's own link when the host is in it, otherwise over a new direct link between the two players, whose codes the host passes
    /// on. A round's matches are played at the same time; the next round starts when all are decided.
    ///
    /// Rules that are ours (the user's decisions 2026-11-13): a player vs player match that ends level on time goes to sudden death
    /// (Battle.SuddenDeath); nothing is written into anyone's save (OnlineMatch puts everything back). Also ours: a player whose link to the host
    /// is lost loses their match; a player vs player match whose own link fails (or ends level with no time limit) is played again, and after
    /// two such repeats it is decided by STD_WIN_CHANCES as a computer match is.
    ///
    /// Messages between the games: MsgTour (40) + one JSON OtMsg; the match's own messages (1-14) as the online battle's.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const byte MsgTour = 40;
        const int OtBoard = 1, OtStart = 2, OtMatch = 3, OtRelay = 4, OtResult = 5, OtProgress = 6, OtEnd = 7;
        // watching a match live: 8 a player asks the host for match m, 9 the host asks the PC running it for a copy, 10 that PC sends both sides'
        // profiles, 11 one of the match's own messages (1-14, base64), passed on by the host to everyone watching
        const int OtWatch = 8, OtMirror = 9, OtFeedStart = 10, OtFeed = 11;

        [System.Serializable] sealed class OtMsg
        {
            public int k, round, m = -1, place = -1, opp = -1, oppCpu = -1, role, via, to = -1, from = -1, winner = -1, cup, time, me = -1;
            public bool orders;
            public string code = "", prof = "", cupName = "", text = "";
            public string[] names = new string[0], species = new string[0];
            public int[] types = new int[0], human = new int[0], wins = new int[0];
        }

        sealed class OtEntrant
        {
            public string Name = "", Species = "", Prof = "";
            public int Type, Pid = -1, Place = -1;
            public FriendProfile P;
            public NetLink Link;                // the host's link to this player (null for the host itself)
            public bool AnswerSent, Gone;
        }

        enum OtPhase { Choose, HostCup, Opening, Lobby, Browse, Joining, Waiting, Running, Done, EnterPass, FindCode }

        // ours: the screen's choices box shows at most OtRows rows; a longer list (the open tournaments: up to MaxOpen = 30 at the service)
        // scrolls, its last choices (Refresh, Find by code, Back, Close) staying in place under it. _otScroll = the first list row shown
        const int OtRows = 9;
        int _otScroll;

        // ours: Find by code: the tournament's 6 letters, picked one place at a time (the service's CodeLetters, online/tournament-worker
        // src/shared.ts: no I, L, O, 0 or 1), or pasted
        const string OtCodeLetters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        readonly int[] _otFind = new int[6];
        bool _otFinding;

        // ours: an optional 4-digit password (for friends only); "" = none. The host's game checks it when someone joins
        string _otPass = "", _otJoinPass = "";
        readonly int[] _otDigits = new int[4];
        TournamentClient.Info _otPassFor;

        OtPhase _otPhase;
        bool _otOn, _otHost;
        int _otPos;
        string _otNote; float _otNoteUntil;
        SysScreen _otSys;
        TournamentClient.Socket _otWs;
        string _otCode = "", _otKey = "", _otCupName = "", _otHostName = "", _otStatus = "";
        int _otCup, _otTime;
        bool _otOrders, _otBusy;
        float _otFullAt = -1f, _otJoinStarted;

        // the host's game
        readonly List<OtEntrant> _otE = new List<OtEntrant>();               // [0] = the host itself
        readonly List<OtEntrant> _otPending = new List<OtEntrant>();         // asked to join, link not made yet
        readonly int[] _otMatchHost = new int[7], _otTries = new int[7];

        // every game (the host too, for its own matches)
        NetLink _otToHost, _otDirect;
        int _otMe = -1;
        string[] _otNames = new string[8], _otSpecies = new string[8];
        int[] _otTypes = new int[8], _otHuman = new int[8], _otW = { -1, -1, -1, -1, -1, -1, -1 };
        readonly List<string> _otBoard = new List<string>();
        List<TournamentClient.Info> _otList = new List<TournamentClient.Info>();
        string _otListErr = "";
        OtMsg _otAssign;
        bool _otMatchBusy;
        readonly List<KeyValuePair<NetLink, byte[]>> _otStash = new List<KeyValuePair<NetLink, byte[]>>();
        string _otChampion = "";

        static string T(string en) { return Tr.Text(en, "ours"); }

        /// <summary>The online battle's or the online tournament's screen is up (not a match): the world stops as under the Triangle menu.</summary>
        bool OnlineScreenOpen { get { return (_onlineOn && !_onlineMatch) || (_otOn && !_tourOn); } }

        // ---------------------------------------------------------------- the loop (Greymon's menu: "Online tournament")

        IEnumerator OnlineTournament()
        {
            _otOn = true; _otHost = false; _otPhase = OtPhase.Choose; _otPos = 0; _otNote = null;
            _otE.Clear(); _otPending.Clear(); _otBoard.Clear(); _otStash.Clear();
            _otMe = -1; _otAssign = null; _otMatchBusy = false; _otChampion = ""; _otStatus = ""; _otFullAt = -1f;
            for (int i = 0; i < 7; i++) { _otW[i] = -1; _otMatchHost[i] = -1; _otTries[i] = 0; }
            _otTime = 0; _otOrders = true; _otCup = 0;
            _otWatching = false; _otWatchM = -1; _otMirrorWanted = false; _otFeedStartPending = false; _otBracketQueued = 0; _otShownRounds = 0;
            _otWatchers.Clear(); _matchMirror = null;
            bool wasBackground = Application.runInBackground;
            Application.runInBackground = true;      // a linked game must keep running when its window is not in front
            while (_otOn)
            {
                OtNetTick();
                // a bracket or match starting puts the leave question away (the answer No): it stayed on screen over them and took no keys
                if (_otAsking && (_tourOn || _otMatchBusy)) OtLeaveEnd(false);
                if (!_tourOn && _otAsking) OtLeaveTick();
                else if (!_tourOn) OtScreenTick();
                else if (_sys != null && _sys == _otSys) { _sys = null; _otSys = null; }
                yield return null;
            }
            while (_otMatchBusy) yield return null;
            OtCleanup();
            Application.runInBackground = wasBackground;
        }

        void OtCleanup()
        {
            if (_otWs != null) { _otWs.Dispose(); _otWs = null; }
            foreach (var e in _otE) if (e.Link != null) e.Link.Dispose();
            foreach (var e in _otPending) if (e.Link != null) e.Link.Dispose();
            _otE.Clear(); _otPending.Clear();
            if (_otToHost != null) { _otToHost.Dispose(); _otToHost = null; }
            if (_otDirect != null) { _otDirect.Dispose(); _otDirect = null; }
            _net = null;
            if (_sys != null && _sys == _otSys) { _sys = null; _otSys = null; }
        }

        void OtSay(string en) { _otNote = T(en); _otNoteUntil = Time.time + 4f; }

        static byte[] OtPack(OtMsg m)
        {
            var j = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(m));
            var b = new byte[1 + j.Length]; b[0] = MsgTour; j.CopyTo(b, 1);
            return b;
        }

        static OtMsg OtUnpack(byte[] b)
        {
            try { return JsonUtility.FromJson<OtMsg>(System.Text.Encoding.UTF8.GetString(b, 1, b.Length - 1)); } catch { return null; }
        }

        // every frame: the service's socket, the links, the match's messages
        void OtNetTick()
        {
            if (_otWs != null) { TournamentClient.RoomMsg rm; while (_otWs != null && (rm = _otWs.Next()) != null) { if (_otHost) OtHostRoom(rm); else OtPlayerRoom(rm); } }
            if (_otHost)
            {
                for (int i = _otPending.Count - 1; i >= 0; i--) OtPendingTick(_otPending[i]);
                foreach (var e in _otE.ToArray())
                {
                    if (e.Link == null) continue;
                    e.Link.Tick();
                    OtRead(e.Link, m => OtHostRecv(e, m));
                    if (!e.Gone && (e.Link.Now == NetLink.State.Failed || e.Link.Now == NetLink.State.Closed)) OtHostLost(e);
                }
                if (_otPhase == OtPhase.Lobby && _otFullAt >= 0f && Time.realtimeSinceStartup - _otFullAt > 30f) OtHostStart();
            }
            else if (_otToHost != null)
            {
                _otToHost.Tick();
                OtRead(_otToHost, OtPlayerRecv);
                if (_otToHost.Now == NetLink.State.Linked && _otWs != null && _otPhase == OtPhase.Joining) { _otWs.Dispose(); _otWs = null; _otPhase = OtPhase.Waiting; _otPos = 0; }
                if ((_otToHost.Now == NetLink.State.Failed || _otToHost.Now == NetLink.State.Closed) && _otPhase != OtPhase.Done && _otPhase != OtPhase.Choose)
                {
                    _otStatus = _otPhase == OtPhase.Joining ? _otToHost.Status : "The link to the host was lost. The tournament is over for you.";
                    if (_otPhase != OtPhase.Joining) _otPhase = OtPhase.Done;
                }
            }
            if (_otDirect != null) { _otDirect.Tick(); OtRead(_otDirect, null); OtDirectStep(); }
            // the match's own messages that came before the match had started
            if (_onlineMatch && _net != null && _otStash.Count > 0)
            {
                foreach (var s in _otStash.ToArray()) if (s.Key == _net) ReadOnline(s.Value);
                _otStash.RemoveAll(s => s.Key == _net);
            }
            if (_onlineMatch && _otFeedStartPending && _otAssign != null && (_otAssign.oppCpu < 0 || _tourFoe != null)) OtSendFeedStart();
            if (_onlineMatch && (_net != null || _matchMirror != null || _otWatching)) OnlineFrame();
            // a match against a computer Digimon has no link: its surrender question (L2 + R2) runs here alone
            else if (_onlineMatch) SurrenderTick();
        }

        // a link's messages: the tournament's to 'tour', the match's to the match (or kept until it starts)
        void OtRead(NetLink link, System.Action<OtMsg> tour)
        {
            byte[] m;
            while ((m = link.Receive()) != null)
            {
                if (m.Length < 1) continue;
                if (m[0] == MsgTour) { var t = OtUnpack(m); if (t != null && tour != null) tour(t); continue; }
                if (_net == link && _onlineMatch) ReadOnline(m);
                else _otStash.Add(new KeyValuePair<NetLink, byte[]>(link, m));
            }
        }

        // ---------------------------------------------------------------- the cups (the arena script's table, as Greymon's schedule)

        // tournamentCheckEligible / tournamentCheckFair: a partner may enter when it is in list 3, or in list 2 (too strong; it may still enter)
        bool OtCupAllows(byte[] s, int cup, int type)
        {
            return CupList(s, CupEntry(s, cup, 3)).Contains(type) || CupList(s, CupEntry(s, cup, 2)).Contains(type);
        }

        // the cups of TOURNAMENT_SCHEDULE (30 days) that are open on the host's game (isTournamentEnabled) and that the host's partner may enter
        List<int> OtCups()
        {
            var r = new List<int>();
            byte[] s = ArenaScript();
            int sched = ExeAt(0x80130198);
            for (int d = 0; d < 30; d++)
                for (int i = 0; i < 6; i++)
                {
                    int cup = ExeBytes[sched + d * 6 + i];
                    if (cup == 0xFF) break;
                    if (!r.Contains(cup) && CupEnabled(s, cup) && OtCupAllows(s, cup, PartnerType)) r.Add(cup);
                }
            r.Sort();
            return r;
        }

        // the computer entrants (scriptStartTournament): list 4's types that are recruited on the host's game (any in cup 0x16), an empty list =
        // every recruited Digimon; fewer than needed are repeated at random; then STD_tournamentMain's three type swaps
        int[] OtCpuTypes(int n)
        {
            var pool = new List<int>();
            byte[] s = ArenaScript();
            // free for all: the empty-list rule, every Digimon recruited on the host's game
            for (int i = 0; i < 0x70; i++) if (_state.GetFlag(i + 200)) pool.Add(i);
            var r = new int[n];
            for (int i = 0; i < n; i++)
            {
                int t;
                if (pool.Count == 0) t = PartnerType;
                else if (pool.Count >= n - i) { int k = Random.Range(0, pool.Count); t = pool[k]; pool.RemoveAt(k); }
                else t = pool[Random.Range(0, pool.Count)];
                if (t == 0x40) t = 0x36; if (t == 0x3F) t = 0x30; if (t == 0x41) t = 0x2A;
                r[i] = t;
            }
            return r;
        }

        string SpeciesEn(int type) { var i = DigimonStatsCatalog.Get(_disc, type); return i != null ? i.NameEn : type.ToString(); }

        OtEntrant OtSelf()
        {
            return new OtEntrant { Name = string.IsNullOrEmpty(_state.PlayerName) ? "Player" : _state.PlayerName, Type = PartnerType, Species = SpeciesEn(PartnerType),
                                   Prof = System.Convert.ToBase64String(MyProfile()), P = ParseProfile(MyProfile()) };
        }

        // ---------------------------------------------------------------- the host: opening, sign-up

        // the computer's strength = STD_OPPONENT_BASE_STATS of cups 0-3 (Grade D, C, B, A), shown as its letter
        static string OtGrade(int tier) { return tier == 0 ? "D" : tier == 1 ? "C" : tier == 2 ? "B" : "A"; }

        IEnumerator OtOpen()
        {
            _otBusy = true; _otHost = true; _otCupName = T("Free for all") + "  " + OtGrade(_otCup);
            _otPhase = OtPhase.Opening;
            string code = null, key = null, err = null;
            var me = OtSelf();
            yield return TournamentClient.Open(me.Name, _otCupName, OnlineTimeName(_otTime), _otPass.Length > 0, (c, k, e) => { code = c; key = k; err = e; });
            _otBusy = false;
            if (!string.IsNullOrEmpty(err)) { _otStatus = err; _otHost = false; _otPhase = OtPhase.Choose; _otPos = 0; OtSay(err); yield break; }
            _otCode = code; _otKey = key; _otHostName = me.Name;
            _otWs = new TournamentClient.Socket(TournamentClient.HostUrl(code, key));
            _otE.Clear(); _otE.Add(me);
            _otPhase = OtPhase.Lobby; _otPos = 0;
            OtBoardChanged();
        }

        void OtHostRoom(TournamentClient.RoomMsg rm)
        {
            if (rm.t == "join")
            {
                System.Action<string> refuse = why => _otWs.Send(new TournamentClient.RoomMsg { t = "refuse", pid = rm.pid, reason = why });
                if (_otPhase != OtPhase.Lobby) { refuse("started"); return; }
                if (_otE.Count + _otPending.Count >= 8) { refuse("full"); return; }
                FriendProfile p = null;
                try { p = ParseProfile(System.Convert.FromBase64String(rm.data)); } catch { }
                if (p == null) { refuse("That is not a game of this version."); return; }
                if (_otPass.Length > 0 && rm.pass != _otPass) { refuse("Wrong password."); return; }
                var e = new OtEntrant { Pid = rm.pid, Name = rm.name, Type = p.Type, Species = SpeciesEn(p.Type), Prof = rm.data, P = p, Link = new NetLink(false) };
                if (!e.Link.UseFriendCode(rm.offer)) { refuse(e.Link.Status); e.Link.Dispose(); return; }
                _otPending.Add(e);
                Debug.Log("[tournament] " + e.Name + " asks to join (" + e.Species + ")");
            }
            else if (rm.t == "leave")
            {
                // a player's socket closes once its link to us is made; only a join we never answered is dropped
                var e = _otPending.Find(x => x.Pid == rm.pid);
                if (e != null && !e.AnswerSent) { e.Link.Dispose(); _otPending.Remove(e); }
            }
        }

        void OtPendingTick(OtEntrant e)
        {
            e.Link.Tick();
            if (e.Link.MyCode != null && !e.AnswerSent && _otWs != null) { _otWs.Send(new TournamentClient.RoomMsg { t = "answer", pid = e.Pid, sdp = e.Link.MyCode }); e.AnswerSent = true; }
            if (e.Link.Now == NetLink.State.Linked)
            {
                _otPending.Remove(e);
                if (_otPhase != OtPhase.Lobby || _otE.Count >= 8) { e.Link.Dispose(); return; }
                _otE.Add(e);
                Snd(3);
                OtBoardChanged();
            }
            else if (e.Link.Now == NetLink.State.Failed || e.Link.Now == NetLink.State.Closed) { e.Link.Dispose(); _otPending.Remove(e); }
        }

        // the board changed: the players, the service (list and Discord) and the 30 s start when full
        void OtBoardChanged()
        {
            var names = new List<string>(); var species = new List<string>();
            var players = new List<TournamentClient.Player>();
            foreach (var e in _otE) { names.Add(e.Name); species.Add(e.Species); players.Add(new TournamentClient.Player { name = e.Name, digimon = e.Species }); }
            if (_otWs != null) _otWs.Send(new TournamentClient.RoomMsg { t = "board", players = players.ToArray() });
            var msg = new OtMsg { k = OtBoard, names = names.ToArray(), species = species.ToArray(), cupName = _otCupName, time = _otTime, orders = _otOrders, text = _otHostName };
            foreach (var e in _otE) if (e.Link != null) e.Link.SendSure(OtPack(msg));
            OtPlayerRecv(msg);
            _otFullAt = _otE.Count >= 8 ? (_otFullAt >= 0f ? _otFullAt : Time.realtimeSinceStartup) : -1f;
        }

        void OtHostLost(OtEntrant e)
        {
            e.Gone = true;
            Debug.Log("[tournament] " + e.Name + " left");
            if (_otPhase == OtPhase.Lobby) { e.Link.Dispose(); _otE.Remove(e); OtBoardChanged(); return; }
            if (_otPhase != OtPhase.Running) return;
            // a match of the current round with this player: the other side wins
            for (int m = 0; m < 7; m++)
            {
                if (_otW[m] >= 0) continue;
                int a, b; if (!OtPair(m, out a, out b)) continue;
                if (a == e.Place) OtHostResolve(m, b); else if (b == e.Place) OtHostResolve(m, a);
            }
        }

        // ---------------------------------------------------------------- the host: the bracket

        // match m's two places: 0-3 the first round (places 2m, 2m+1), 4-5 the second (winners of 0/1, 2/3), 6 the final; false = not known yet
        bool OtPair(int m, out int a, out int b)
        {
            if (m < 4) { a = 2 * m; b = 2 * m + 1; return true; }
            if (m < 6) { a = _otW[(m - 4) * 2]; b = _otW[(m - 4) * 2 + 1]; return a >= 0 && b >= 0; }
            a = _otW[4]; b = _otW[5]; return a >= 0 && b >= 0;
        }

        static int OtRoundOf(int m) { return m < 4 ? 0 : m < 6 ? 1 : 2; }

        OtEntrant OtAt(int place) { return place >= 0 && place < 8 ? _otE.Find(x => x.Place == place) : null; }

        void OtHostStart()
        {
            if (_otPhase != OtPhase.Lobby) return;
            if (_otWs != null) _otWs.Send(new TournamentClient.RoomMsg { t = "start" });
            // STD_setupParticipants: every player takes a random place; the computer entrants fill the rest
            var places = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7 };
            for (int i = 7; i > 0; i--) { int j = Random.Range(0, i + 1); int t = places[i]; places[i] = places[j]; places[j] = t; }
            for (int i = 0; i < _otE.Count; i++) _otE[i].Place = places[i];
            int[] cpu = OtCpuTypes(8 - _otE.Count);
            for (int p = 0, c = 0; p < 8; p++)
            {
                var e = OtAt(p);
                if (e != null) { _otNames[p] = e.Name; _otSpecies[p] = e.Species; _otTypes[p] = e.Type; _otHuman[p] = 1; }
                else { _otTypes[p] = cpu[c++]; _otSpecies[p] = SpeciesEn(_otTypes[p]); _otNames[p] = "COM"; _otHuman[p] = 0; }
            }
            for (int i = 0; i < 7; i++) { _otW[i] = -1; _otMatchHost[i] = -1; _otTries[i] = 0; }
            Debug.Log("[tournament] start: " + string.Join(", ", System.Array.ConvertAll(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, p => _otNames[p] + " " + _otSpecies[p])));
            foreach (var e in _otE)
            {
                var msg = new OtMsg { k = OtStart, me = e.Place, names = (string[])_otNames.Clone(), species = (string[])_otSpecies.Clone(), types = (int[])_otTypes.Clone(),
                                      human = (int[])_otHuman.Clone(), cup = _otCup, cupName = _otCupName, time = _otTime, orders = _otOrders };
                OtSendTo(e, msg);
            }
            for (int m = 0; m < 4; m++) OtHostLaunch(m);
        }

        void OtSendTo(OtEntrant e, OtMsg msg)
        {
            if (e.Link == null) OtPlayerRecv(msg);       // the host itself
            else if (!e.Gone) e.Link.SendSure(OtPack(msg));
        }

        bool OtIn(int place) { var e = OtAt(place); return e != null && !e.Gone; }

        void OtHostLaunch(int m)
        {
            int a, b;
            if (!OtPair(m, out a, out b) || _otW[m] >= 0) return;
            bool ha = _otHuman[a] == 1, hb = _otHuman[b] == 1;
            bool ia = OtIn(a), ib = OtIn(b);
            if (ha && !ia || hb && !ib)
            {
                // a player who left loses (both left: the first place goes on)
                OtHostResolve(m, ha && !ia ? (hb && !ib ? a : b) : a);
                return;
            }
            if (!ha && !hb) { OtHostResolve(m, Random.Range(0, 100) < WinChance(_otTypes[a], _otTypes[b]) ? a : b); return; }
            int round = OtRoundOf(m);
            if (ha != hb)
            {
                int p = ha ? a : b, cpu = ha ? b : a;
                _otMatchHost[m] = p;
                OtSendTo(OtAt(p), new OtMsg { k = OtMatch, m = m, round = round, place = p, opp = cpu, oppCpu = _otTypes[cpu], role = 0 });
                return;
            }
            // player against player: the host's game runs it when the host is in it (over its own link), else the first place over a direct link
            var ea = OtAt(a); var eb = OtAt(b);
            bool viaHub = ea.Link == null || eb.Link == null;
            int mh = viaHub ? (ea.Link == null ? a : b) : a, guest = mh == a ? b : a;
            _otMatchHost[m] = mh;
            OtSendTo(OtAt(mh), new OtMsg { k = OtMatch, m = m, round = round, place = mh, opp = guest, prof = OtAt(guest).Prof, role = 1, via = viaHub ? 0 : 1 });
            OtSendTo(OtAt(guest), new OtMsg { k = OtMatch, m = m, round = round, place = guest, opp = mh, prof = OtAt(mh).Prof, role = 2, via = viaHub ? 0 : 1 });
        }

        void OtHostRecv(OtEntrant from, OtMsg msg)
        {
            if (msg.k == OtRelay)
            {
                var to = OtAt(msg.to);
                if (to != null) OtSendTo(to, new OtMsg { k = OtRelay, m = msg.m, from = from.Place, code = msg.code });
            }
            else if (msg.k == OtResult) OtHostResult(from.Place, msg);
            else if (msg.k == OtWatch) OtHostWatch(from, msg.m);
            else if (msg.k == OtFeedStart || msg.k == OtFeed)
            {
                List<OtEntrant> w;
                if (msg.m >= 0 && msg.m < 7 && _otW[msg.m] < 0 && _otWatchers.TryGetValue(msg.m, out w))
                    foreach (var e in w.ToArray()) { if (e.Gone) w.Remove(e); else OtSendTo(e, msg); }
            }
        }

        void OtHostResult(int fromPlace, OtMsg msg)
        {
            int m = msg.m;
            if (m < 0 || m > 6 || _otW[m] >= 0 || fromPlace != _otMatchHost[m]) return;      // only the side that ran the match reports it
            if (msg.winner >= 0) { OtHostResolve(m, msg.winner); return; }
            // the match's own link failed, or it ended level with no time limit: played again; after two repeats, STD_WIN_CHANCES
            int a, b; OtPair(m, out a, out b);
            if (++_otTries[m] > 2) { OtHostResolve(m, Random.Range(0, 100) < WinChance(_otTypes[a], _otTypes[b]) ? a : b); return; }
            OtHostLaunch(m);
        }

        readonly Dictionary<int, List<OtEntrant>> _otWatchers = new Dictionary<int, List<OtEntrant>>();

        // a player wants to watch match m: on the list, and the PC running it is asked for a copy (again for each new watcher: the profiles come first)
        void OtHostWatch(OtEntrant who, int m)
        {
            if (m < 0 || m > 6 || _otW[m] >= 0 || _otMatchHost[m] < 0) return;
            List<OtEntrant> w;
            if (!_otWatchers.TryGetValue(m, out w)) _otWatchers[m] = w = new List<OtEntrant>();
            if (!w.Contains(who)) w.Add(who);
            var runner = OtAt(_otMatchHost[m]);
            if (runner != null) OtSendTo(runner, new OtMsg { k = OtMirror, m = m });
        }

        void OtHostResolve(int m, int winner)
        {
            if (_otW[m] >= 0) return;
            _otW[m] = winner;
            _otWatchers.Remove(m);
            Debug.Log("[tournament] match " + m + ": " + _otNames[winner] + " (" + _otSpecies[winner] + ") wins");
            var prog = new OtMsg { k = OtProgress, wins = (int[])_otW.Clone() };
            foreach (var e in _otE) OtSendTo(e, prog);
            int round = OtRoundOf(m);
            for (int k = 0; k < 7; k++) if (OtRoundOf(k) == round && _otW[k] < 0) return;
            if (round < 2) { for (int k = 0; k < 7; k++) if (OtRoundOf(k) == round + 1) OtHostLaunch(k); return; }
            // the final is decided
            string champ = _otNames[_otW[6]] == "COM" ? "COM " + _otSpecies[_otW[6]] : _otNames[_otW[6]];
            if (_otWs != null) _otWs.Send(new TournamentClient.RoomMsg { t = "end", champion = champ });
            var end = new OtMsg { k = OtEnd, text = champ, wins = (int[])_otW.Clone() };
            foreach (var e in _otE) OtSendTo(e, end);
        }

        // ---------------------------------------------------------------- a player (the host too, for its own place)

        void OtPlayerRoom(TournamentClient.RoomMsg rm)
        {
            switch (rm.t)
            {
                case "hello":
                    if (_otToHost == null) { _otToHost = new NetLink(true); _otStatus = "Linking with the host..."; }
                    break;
                case "answer":
                    if (_otToHost != null && !_otToHost.UseFriendCode(rm.sdp)) _otStatus = _otToHost.Status;
                    break;
                case "refused":
                    _otStatus = rm.reason == "full" ? "That tournament is full." : rm.reason == "started" ? "That tournament has already started."
                              : rm.reason == "host-away" ? "The host is not there right now." : rm.reason;
                    if (_otToHost != null) { _otToHost.Dispose(); _otToHost = null; }
                    if (_otWs != null) { _otWs.Dispose(); _otWs = null; }
                    break;
                case "closed":
                    _otStatus = "The host closed the tournament.";
                    if (_otToHost != null) { _otToHost.Dispose(); _otToHost = null; }
                    if (_otWs != null) { _otWs.Dispose(); _otWs = null; }
                    break;
            }
        }

        // sends our offer once the link has its code (the room passes it to the host)
        void OtJoinStep()
        {
            if (_otPhase != OtPhase.Joining || _otWs == null || _otToHost == null || _otToHost.MyCode == null || _otJoinSent) return;
            var me = OtSelf();
            _otWs.Send(new TournamentClient.RoomMsg { t = "join", name = me.Name, offer = _otToHost.MyCode, data = me.Prof, pass = _otJoinPass });
            _otJoinSent = true; _otJoinStarted = Time.realtimeSinceStartup;
        }
        bool _otJoinSent;

        void OtPlayerRecv(OtMsg msg)
        {
            switch (msg.k)
            {
                case OtBoard:
                    _otBoard.Clear();
                    for (int i = 0; i < msg.names.Length; i++) _otBoard.Add(msg.names[i] + "   " + (i < msg.species.Length ? SpeciesShown(msg.species[i]) : ""));
                    _otCupName = msg.cupName; _otTime = msg.time; _otOrders = msg.orders; if (!_otHost) _otHostName = msg.text;
                    break;
                case OtStart:
                    _otMe = msg.me; _otNames = msg.names; _otSpecies = msg.species; _otTypes = msg.types; _otHuman = msg.human;
                    _otCup = msg.cup; _otCupName = msg.cupName; _otTime = msg.time; _otOrders = msg.orders;
                    for (int i = 0; i < 7; i++) _otW[i] = -1;
                    _otPhase = OtPhase.Running; _otPos = 0;
                    // the cup's title and the bracket first, before anyone's first match (the matches wait for it)
                    _otShownRounds = 0;
                    if (_otMatchBusy) _otBracketQueued = 1; else StartCoroutine(OtShowBracket(1));
                    break;
                case OtMatch:
                    _otAssign = msg;
                    if (_otMatchBusy) { _otQueued = msg; break; }       // the last match is still closing: this one starts right after it
                    StartCoroutine(OtRunMatch(msg));
                    break;
                case OtRelay:
                    OtRelayIn(msg);
                    break;
                case OtProgress:
                    if (msg.wins != null && msg.wins.Length == 7) for (int i = 0; i < 7; i++) _otW[i] = msg.wins[i];
                    OtRoundShown();
                    break;
                case OtMirror:
                    // the host wants a copy of our match for someone watching: our match's messages go to it from now on, the profiles first
                    if (_otAssign != null && _otAssign.m == msg.m && _otMatchBusy && !_otWatching) { _otMirrorWanted = true; _otFeedStartPending = true; }
                    break;
                case OtFeedStart:
                    if (_otWatchM == msg.m && !_otWatching && !_otMatchBusy) StartCoroutine(OtWatchRun(msg));
                    break;
                case OtFeed:
                    if (_otWatching && msg.m == _otWatchM)
                    {
                        _otFeedAt = Time.realtimeSinceStartup;
                        byte[] d = null;
                        try { d = System.Convert.FromBase64String(msg.code); } catch { }
                        if (d != null && d.Length > 0 && _onlineMatch) ReadOnline(d);
                    }
                    break;
                case OtEnd:
                    if (msg.wins != null && msg.wins.Length == 7) for (int i = 0; i < 7; i++) _otW[i] = msg.wins[i];
                    _otChampion = msg.text; _otPhase = OtPhase.Done; _otPos = 0;
                    break;
            }
        }

        // a species name in this game's language (the messages carry the disc's English name)
        readonly Dictionary<string, int> _otTypeOf = new Dictionary<string, int>();
        string SpeciesShown(string en)
        {
            if (string.IsNullOrEmpty(en)) return "";
            int type;
            if (!_otTypeOf.TryGetValue(en, out type))
            {
                type = -1;
                for (int t = 0; t < 0x80; t++) { var i = DigimonStatsCatalog.Get(_disc, t); if (i != null && i.NameEn == en) { type = t; break; } }
                _otTypeOf[en] = type;
            }
            var info = type >= 0 ? DigimonStatsCatalog.Get(_disc, type) : null;
            return info != null ? info.Name : en;
        }

        void OtSendHost(OtMsg msg)
        {
            if (_otHost) { var me = _otE.Count > 0 ? _otE[0] : null; if (me != null) OtHostRecv(me, msg); }
            else if (_otToHost != null) _otToHost.SendSure(OtPack(msg));
        }

        // ---------------------------------------------------------------- one match of ours

        bool _otRelaySent;
        OtMsg _otQueued;

        void OtMatchDone()
        {
            _otMatchBusy = false;
            _matchMirror = null; _otMirrorWanted = false; _otFeedStartPending = false;
            if (_otBracketQueued > 0) { int b = _otBracketQueued; _otBracketQueued = 0; StartCoroutine(OtShowBracket(b)); return; }
            if (_otQueued != null) { var q = _otQueued; _otQueued = null; StartCoroutine(OtRunMatch(q)); }
        }

        // ---------------------------------------------------------------- watching a match live (ours)

        bool _otWatching, _otMirrorWanted, _otFeedStartPending;
        int _otWatchM = -1;
        float _otFeedAt;

        // a profile message (MyProfile's layout) for a Digimon of a match: the computer's numbers come from its record
        static byte[] OtProfileOf(int type, int hp, int mp, int off, int def, int spd, int brain, string name)
        {
            var n = System.Text.Encoding.UTF8.GetBytes(name ?? "");
            var b = new byte[19 + n.Length];
            b[0] = MsgProfile;
            int[] v = { type, hp, mp, off, def, spd, brain };
            for (int i = 0; i < v.Length; i++) { b[1 + 2 * i] = (byte)(v[i] >> 8); b[2 + 2 * i] = (byte)v[i]; }
            for (int i = 0; i < 4; i++) b[15 + i] = 0xFF;
            n.CopyTo(b, 19);
            return b;
        }

        // the PC running a watched match: both sides' Digimon (side 0 = ours, side 1 = the other) for the watchers, then every match message
        void OtSendFeedStart()
        {
            _otFeedStartPending = false;
            var a = _otAssign;
            string p1;
            if (a.oppCpu >= 0)
            {
                var d = _tourFoe.Data;
                p1 = System.Convert.ToBase64String(OtProfileOf(d.Type, d.Hp, d.Mp, d.Off, d.Def, d.Spd, d.Brain, _tourFoe.Name));
            }
            else p1 = a.prof;
            OtSendHost(new OtMsg { k = OtFeedStart, m = a.m, prof = OtSelf().Prof, code = p1 });
            int m = a.m;
            _matchMirror = data => { if (_otMirrorWanted) OtSendHost(new OtMsg { k = OtFeed, m = m, code = System.Convert.ToBase64String(data) }); };
        }

        // watching: this PC copies the match as the online battle's friend does; its partner stands in as side 1 for the time of the match
        IEnumerator OtWatchRun(OtMsg msg)
        {
            FriendProfile p0 = null, p1 = null;
            try { p0 = ParseProfile(System.Convert.FromBase64String(msg.prof)); p1 = ParseProfile(System.Convert.FromBase64String(msg.code)); } catch { }
            if (p0 == null || p1 == null) yield break;
            _otMatchBusy = true; _otWatching = true; _otFeedAt = Time.realtimeSinceStartup;
            _friend = p0; _net = null;
            _onlineOrders = false; _onlineTime = 0; _onlineSudden = true;
            yield return OnlineMatch(false, -1, 0, 0, p1);
            _otWatching = false; _otWatchM = -1;
            OtMatchDone();
        }

        // ---------------------------------------------------------------- the bracket after a round (the arena's own, ArenaScreens)

        int _otBracketQueued, _otShownRounds;

        // a round is decided for everyone: its part of the bracket is played out on every screen (the next match waits for it)
        void OtRoundShown()
        {
            int done = 0;
            for (int r = 0; r < 2; r++)
            {
                bool all = true;
                for (int m = 0; m < 7; m++) if (OtRoundOf(m) == r && _otW[m] < 0) all = false;
                if (all) done = r + 1;
            }
            if (done <= _otShownRounds) return;
            _otShownRounds = done;
            int stop = done == 1 ? 5 : 7;
            if (_otMatchBusy) _otBracketQueued = stop; else StartCoroutine(OtShowBracket(stop));
        }

        // STD_tournamentMain's bracket scenes in the arena world: the first time with the cup's title (STD_addBracketIntro); the rounds already
        // shown are run through at once without sounds, then this round is played out (every match as the original shows the others',
        // PlayerSlot -1) up to STD_BRACKET_MATCH 'stop'
        IEnumerator OtShowBracket(int stop)
        {
            _otMatchBusy = true; _tourOn = true;
            _line = null; _choiceOpts = null;
            Vector3 camPos = _cam.transform.position; Quaternion camRot = _cam.transform.rotation;
            float camFov = _cam.fieldOfView, camFocal = _cam.focalLength; Vector2 camSensor = _cam.sensorSize, camShift = _cam.lensShift;
            bool camPhys = _cam.usePhysicalProperties; var camGate = _cam.gateFit;
            yield return TourFade(1f, 5);
            if (_audio != null) { _audio.SetMapSounds(0x10); _audio.BattleSounds(); }
            int arena = _mapId == 0x6A ? 1 : 0;
            _tourHidden.Clear();
            foreach (var n in _npcs) if (n.T != null && n.T.gameObject.activeSelf) { n.T.gameObject.SetActive(false); _tourHidden.Add(n); }
            bool boyShown = _player.gameObject.activeSelf;
            _player.gameObject.SetActive(false);
            Vector3 home = _partner.position; Quaternion homeRot = _partner.rotation;
            ArenaOn(arena, _otCup);
            _p1 = Fighter(_partner, _partnerAnim, PartnerType, true);
            _arenaScr.InitBracket();
            _arenaScr.PlayerSlot = -1;
            // STD_tournamentMain's three type swaps (0x40 -> 0x36, 0x3F -> 0x30, 0x41 -> 0x2A): the bracket's sprite sheet has no picture for those
            // three, so a player's partner of one of them is drawn as the game's own stand-in (only on the bracket; the fight is the real one)
            for (int i = 0; i < 8; i++) { int t = _otTypes[i]; _arenaScr.Types[i] = (byte)(t == 0x40 ? 0x36 : t == 0x3F ? 0x30 : t == 0x41 ? 0x2A : t); }
            for (int i = 0; i < 4; i++) _arenaScr.Round1[i] = (byte)Mathf.Max(0, _otW[i]);
            for (int i = 0; i < 2; i++) _arenaScr.Round2[i] = (byte)Mathf.Max(0, _otW[4 + i]);
            _camState = 10; _camTimer = 0;
            SetPlace(_p1, 0, 0, 0, 0x400);
            _partner.gameObject.SetActive(false);
            if (stop == 1)
            {
                // the cup's title over the bracket before the first round (STD_addBracketIntro), as the arena's own tournament opens
                _arenaScr.AddIntro();
                if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); _audio.PlayMusic(0x1D, 0); }
                yield return TourFade(0f, 5);
                yield return ArenaFrames(0x78);
                _arenaScr.RemoveIntro();
            }
            else
            {
                if (stop == 7)
                {
                    _arenaScr.Silent = true; _arenaScr.StopAt = 5;
                    for (int guard = 0; guard < 20000 && _arenaScr.Match < 5; guard++) _arenaScr.TickBracket();
                    _arenaScr.Silent = false;
                }
                yield return TourFade(0f, 5);
            }
            _arenaScr.MatchResult = 0;
            _arenaScr.StopAt = stop;
            _arenaScr.Fast = true;
            _arenaScr.AddBracket();
            if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); _audio.PlayMusic(0x1D, 1); _audio.SetMapSounds(0x10); }
            float t0 = Time.realtimeSinceStartup;
            while (!_arenaScr.Finished && Time.realtimeSinceStartup - t0 < 90f) yield return ArenaFrames(1);
            yield return ArenaFrames(20);
            _arenaScr.RemoveBracket();
            _arenaScr.StopAt = 0; _arenaScr.Fast = false;
            if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); }
            yield return TourFade(1f, 5);
            ArenaOff();
            _cam.transform.SetPositionAndRotation(camPos, camRot);
            _cam.usePhysicalProperties = camPhys; _cam.gateFit = camGate; _cam.sensorSize = camSensor; _cam.focalLength = camFocal; _cam.lensShift = camShift;
            if (!camPhys) _cam.fieldOfView = camFov;
            _partner.gameObject.SetActive(true);
            if (_tourView != null) _tourView.ShowTimer = false;
            _p1 = null;
            _partner.position = home; _partner.rotation = homeRot;
            if (_partnerAnim != null) { _partnerAnim.TakeBodyMove(); _partnerAnim.SetSlot(IdleSlot); }
            _player.gameObject.SetActive(boyShown);
            foreach (var n in _tourHidden) if (n.T != null) n.T.gameObject.SetActive(true);
            _tourHidden.Clear();
            if (_audio != null && _mapId >= 0 && _mapId < _mapFlags.Count) { _audio.SetMapSounds(_mapFlags[_mapId] & 0x1F); _audio.RestoreBgm(); }
            yield return TourFade(0f, 5);
            _tourOn = false;
            OtMatchDone();
        }

        // the champion (STD_tournamentMain after a won final): STD_loadChampionModels, the podium and STD_addChampionScene, on the champion's PC
        IEnumerator OtChampionScene()
        {
            _arena.LoadChampion(_disc);
            yield return TourFade(1f, 10);
            yield return ArenaFrames(1);
            _partner.gameObject.SetActive(true);
            _arena.ShowPodium(true);
            InitializeChampionScene();
            yield return ArenaFrames(2);
            yield return TourFade(0f, 15);
            yield return ArenaFrames(1);
            _arena.AddChampion();
            float t0 = Time.realtimeSinceStartup;
            while (!_arena.ChampDone && Time.realtimeSinceStartup - t0 < 60f) yield return ArenaFrames(1);
            _arena.ShowPodium(false); _arena.ShowScene(0);
            if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); }
        }

        IEnumerator OtRunMatch(OtMsg a)
        {
            _otMatchBusy = true; _otRelaySent = false;
            _onlineOrders = true; _onlineTime = _otTime; _onlineSudden = true;       // orders always on (the user's decision 2026-11-13)
            if (a.oppCpu >= 0)
            {
                // against a computer Digimon of the cup, on this PC alone
                _net = null;
                yield return OnlineMatch(true, a.oppCpu, a.round, _otCup, null, a.round == 2);
                // a draw counts as lost, as the arena's tournament does
                OtSendHost(new OtMsg { k = OtResult, m = a.m, winner = _onlineLastResult == 1 ? a.place : a.opp });
                OtMatchDone();
                yield break;
            }
            _friend = ParseProfile(System.Convert.FromBase64String(a.prof));
            bool runs = a.role == 1;
            NetLink link = null;
            if (a.via == 0)
            {
                // the host's own link: on the host's game the other player's, on a player's game the one to the host
                link = _otHost ? (OtAt(a.opp) != null ? OtAt(a.opp).Link : null) : _otToHost;
            }
            else
            {
                // a direct link between the two players: the one who runs the match makes the first code, the host passes the codes on
                // (the other side's link may already be there: the runner's code can come before this match starts)
                if (runs) { if (_otDirect != null) _otDirect.Dispose(); _otDirect = new NetLink(true); }
                float t0 = Time.realtimeSinceStartup;
                while ((_otDirect == null || _otDirect.Now != NetLink.State.Linked) && Time.realtimeSinceStartup - t0 < 90f)
                {
                    if (_otDirect != null && (_otDirect.Now == NetLink.State.Failed || _otDirect.Now == NetLink.State.Closed)) break;
                    yield return null;
                }
                link = _otDirect != null && _otDirect.Now == NetLink.State.Linked ? _otDirect : null;
            }
            if (link == null || link.Now != NetLink.State.Linked || _friend == null)
            {
                if (_otDirect != null) { _otDirect.Dispose(); _otDirect = null; }
                if (runs) OtSendHost(new OtMsg { k = OtResult, m = a.m, winner = -1 });
                OtMatchDone();
                yield break;
            }
            _net = link; _onlineRole = runs ? 1 : 0;
            yield return OnlineMatch(runs, -1, 0, 0, null, a.round == 2);
            _net = null;
            _otStash.RemoveAll(s => s.Key == link);
            if (_otDirect != null) { _otDirect.Dispose(); _otDirect = null; }
            if (runs)
            {
                int w = _onlineLinkLost || _onlineLastResult == 0 ? -1 : _onlineLastResult == 1 ? a.place : a.opp;
                OtSendHost(new OtMsg { k = OtResult, m = a.m, winner = w });
            }
            OtMatchDone();
        }

        // the direct link's codes: the runner sends its code once made; the other side answers the code that comes
        void OtDirectStep()
        {
            var a = _otAssign;
            if (a == null || _otDirect == null || a.via != 1 || _otRelaySent || _otDirect.MyCode == null) return;
            OtSendHost(new OtMsg { k = OtRelay, m = a.m, to = a.opp, code = _otDirect.MyCode });
            _otRelaySent = true;
        }

        void OtRelayIn(OtMsg msg)
        {
            var a = _otAssign;
            if (a == null || a.via != 1 || msg.m != a.m || msg.from != a.opp) return;
            if (a.role == 2)
            {
                if (_otDirect != null) _otDirect.Dispose();
                _otDirect = new NetLink(false);
                _otDirect.UseFriendCode(msg.code);
            }
            else if (_otDirect != null) _otDirect.UseFriendCode(msg.code);
        }

        // ---------------------------------------------------------------- the screen (the game's boxes, font and cursor, as the link screen)

        string OtPlaceName(int p)
        {
            if (p < 0 || p >= 8 || _otNames == null || p >= _otNames.Length) return "?";
            return _otHuman[p] == 1 ? _otNames[p] : SpeciesShown(_otSpecies[p]);
        }

        void OtScreenTick()
        {
            var lines = new List<string>(); var opts = new List<string>(); var acts = new List<System.Action>();
            // ours: the tournament's boxes are wider than the link screen's (the results board's lines are long)
            const int TbX = 0x08, TbW = 0x130, TbTx = 0x12;
            int wrap = TbW - 0x14;

            System.Action close = () =>
            {
                bool inIt = _otPhase == OtPhase.Lobby || _otPhase == OtPhase.Joining || _otPhase == OtPhase.Waiting || _otPhase == OtPhase.Running;
                if (inIt) OtAskLeave(); else _otOn = false;
            };
            switch (_otPhase)
            {
                case OtPhase.Choose:
                    lines.AddRange(Wrap(T("A tournament for up to 8 players. Empty places become computer Digimon. Anyone can host; the host fights too."), wrap));
                    opts.Add(T("Host")); acts.Add(() => { _otPhase = OtPhase.HostCup; _otPos = 0; });
                    opts.Add(T("Join")); acts.Add(() => { _otPhase = OtPhase.Browse; _otPos = 0; StartCoroutine(OtRefresh()); });
                    break;
                case OtPhase.HostCup:
                {
                    // free for all (the user's decision 2026-11-13): any partner may enter; the host sets the computer's strength. The time is
                    // always the arena's 99 seconds, then sudden death (the user's decision 2026-11-13)
                    _otTime = 0;
                    lines.AddRange(Wrap(T("Free for all: any Digimon may enter. Computer Digimon come from those recruited in your game."), wrap));
                    opts.Add(T("Open the tournament")); acts.Add(() => StartCoroutine(OtOpen()));
                    opts.Add(Tr.Format("Computer strength   {0}", OtGrade(_otCup))); acts.Add(() => { _otCup = (_otCup + 1) % 4; });
                    // ours: a password for friends only: a new random 4 digits each time it is switched on
                    opts.Add(Tr.Format("Password   {0}", _otPass.Length > 0 ? _otPass : T("OFF")));
                    acts.Add(() => { _otPass = _otPass.Length > 0 ? "" : Random.Range(0, 10000).ToString("0000"); });
                    opts.Add(T("Back")); acts.Add(() => { _otPhase = OtPhase.Choose; _otPos = 0; });
                    break;
                }
                case OtPhase.Opening:
                    lines.Add(T("Opening the tournament..."));
                    break;
                case OtPhase.Lobby:
                    lines.Add(Tr.Format("Code {0}   {1}", _otCode, _otCupName));
                    if (_otPass.Length > 0) lines.Add(Tr.Format("Password   {0}", _otPass));
                    lines.Add(T("Orders ON"));
                    for (int i = 0; i < _otBoard.Count; i++) lines.Add((i + 1) + "  " + _otBoard[i]);
                    if (_otPending.Count > 0) lines.Add(Tr.Format("{0} joining...", _otPending.Count));
                    if (_otFullAt >= 0f) lines.Add(Tr.Format("Full. Starting in {0} s", Mathf.Max(0, 30 - (int)(Time.realtimeSinceStartup - _otFullAt))));
                    opts.Add(T("Start")); acts.Add(OtAskStart);
                    // ours: the tournament's code to the PC's clipboard, to send to friends (they use Join > Find by code)
                    opts.Add(T("Copy my code")); acts.Add(() => { CopyText(_otCode); OtSay("Your code is copied."); });
                    break;
                case OtPhase.Browse:
                {
                    // ours: more tournaments than fit scroll (OtRows less the 4 fixed rows under them); the line says which are shown
                    int shown = OtRows - 4, n = _otList.Count;
                    if (n > shown)
                    {
                        int first = OtScrollFor(n, shown);
                        lines.Add(T("Open tournaments") + "   " + Tr.Format("{0}-{1} of {2}", first + 1, first + shown, n));
                    }
                    else lines.Add(T("Open tournaments"));
                    if (_otBusy) lines.Add(T("Looking..."));
                    else if (_otListErr.Length > 0) lines.AddRange(Wrap(T(_otListErr), wrap));
                    else if (_otList.Count == 0) lines.AddRange(Wrap(T("No open tournaments. Anyone can host one."), wrap));
                    else if (n > shown) lines.AddRange(Wrap(T("Up and down scroll the list. Left and right turn a page."), wrap));
                    foreach (var t in _otList)
                    {
                        var info = t;
                        // (every tournament is free for all: the cup's name is left out so the line fits; a long name is cut short)
                        opts.Add(OtListRow(info, wrap));
                        acts.Add(() => OtPick(info));
                    }
                    opts.Add(T("Refresh")); acts.Add(() => StartCoroutine(OtRefresh()));
                    opts.Add(T("Find by code")); acts.Add(() => { _otPhase = OtPhase.FindCode; _otPos = 0; });
                    opts.Add(T("Back")); acts.Add(() => { _otPhase = OtPhase.Choose; _otPos = 0; });
                    break;
                }
                case OtPhase.FindCode:
                {
                    // ours: the 6 letters, one row each (up / down picks a place, left / right or Cross changes it), as the password's digits
                    string code = OtFindCode();
                    lines.Add(T("Find a tournament by its code"));
                    lines.Add(Tr.Format("Code   {0}", code));
                    if (_otFinding) lines.Add(T("Looking..."));
                    else if (_otNote == null || Time.time >= _otNoteUntil) lines.AddRange(Wrap(T("Ask the host for the code. Paste works too."), wrap));
                    for (int d = 0; d < 6; d++) { int dd = d; opts.Add(Tr.Format("Letter {0}   {1}", d + 1, code[d])); acts.Add(() => { _otFind[dd] = (_otFind[dd] + 1) % OtCodeLetters.Length; }); }
                    opts.Add(T("Paste code")); acts.Add(() => PasteText(OtPasteCode));
                    opts.Add(T("Find")); acts.Add(() => { if (!_otFinding) StartCoroutine(OtFind()); });
                    opts.Add(T("Back")); acts.Add(() => { _otPhase = OtPhase.Browse; _otPos = 0; });
                    break;
                }
                case OtPhase.EnterPass:
                    // the 4 digits: up / down picks a digit, left / right (or Cross) changes it
                    lines.Add(Tr.Format("{0}'s {1}", _otPassFor != null ? _otPassFor.host : "", _otPassFor != null ? _otPassFor.cup : ""));
                    lines.AddRange(Wrap(T("This tournament has a password. Ask the host for it."), wrap));
                    for (int d = 0; d < 4; d++) { int dd = d; opts.Add(Tr.Format("Digit {0}   {1}", d + 1, _otDigits[d])); acts.Add(() => { _otDigits[dd] = (_otDigits[dd] + 1) % 10; }); }
                    opts.Add(T("Join")); acts.Add(() => { _otJoinPass = "" + _otDigits[0] + _otDigits[1] + _otDigits[2] + _otDigits[3]; if (_otPassFor != null) OtJoin(_otPassFor); });
                    opts.Add(T("Back")); acts.Add(() => { _otPhase = OtPhase.Browse; _otPos = 0; });
                    break;
                case OtPhase.Joining:
                    OtJoinStep();
                    if (_otJoinSent && _otWs != null && Time.realtimeSinceStartup - _otJoinStarted > 60f) { _otStatus = "The host did not answer."; _otWs.Dispose(); _otWs = null; }
                    lines.AddRange(Wrap(T(_otStatus), wrap));
                    opts.Add(T("Back")); acts.Add(() => { OtCleanup(); _otPhase = OtPhase.Browse; _otPos = 0; StartCoroutine(OtRefresh()); });
                    break;
                case OtPhase.Waiting:
                    lines.Add(Tr.Format("{0}'s {1}", _otHostName, _otCupName));
                    lines.Add(T("Orders ON"));
                    for (int i = 0; i < _otBoard.Count; i++) lines.Add((i + 1) + "  " + _otBoard[i]);
                    lines.Add(T("Waiting for the host to start."));
                    break;
                case OtPhase.Running:
                case OtPhase.Done:
                {
                    lines.Add(_otCupName);
                    for (int m = 0; m < 7; m++)
                    {
                        int a, b;
                        if (!OtKnownPair(m, out a, out b)) continue;
                        string l = OtPlaceName(a) + " - " + OtPlaceName(b);
                        // the result as words (the font has no arrow: ">" is drawn as a triangle)
                        if (_otW[m] >= 0) l = OtPlaceName(a) + " " + T(_otW[m] == a ? "WIN" : "LOSE") + " - " + OtPlaceName(b) + " " + T(_otW[m] == b ? "WIN" : "LOSE");
                        lines.AddRange(Wrap(l, wrap));
                    }
                    if (_otPhase == OtPhase.Done) lines.AddRange(Wrap(_otChampion.Length > 0 ? Tr.Format("Champion: {0}", _otChampion) : T(_otStatus), wrap));
                    else if (_otMatchBusy) lines.Add(T("Your match is starting..."));
                    else lines.Add(OtStillIn() ? T("Waiting for the other matches...") : T("You are out. You can watch the other matches."));
                    // ours: watch a match of this round that is being played (one with a player in it; not our own)
                    if (_otPhase == OtPhase.Running && !_otMatchBusy)
                        for (int m = 0; m < 7; m++)
                        {
                            int a, b;
                            if (_otW[m] >= 0 || !OtKnownPair(m, out a, out b) || a == _otMe || b == _otMe || (_otHuman[a] == 0 && _otHuman[b] == 0)) continue;
                            int mm = m;
                            opts.Add(Tr.Format("Watch   {0} - {1}", OtPlaceName(a), OtPlaceName(b)));
                            acts.Add(() => { _otWatchM = mm; OtSendHost(new OtMsg { k = OtWatch, m = mm }); OtSay("Waiting for the match's picture..."); });
                        }
                    break;
                }
            }
            if (_otNote != null && Time.time < _otNoteUntil) lines.AddRange(Wrap(_otNote, wrap));
            // (Find by code has 9 rows of its own and its own Back: no Close there; Triangle goes back to the list)
            bool canClose = !_otMatchBusy && _otPhase != OtPhase.FindCode;
            if (canClose) { opts.Add(_otHost && (_otPhase == OtPhase.Lobby || _otPhase == OtPhase.Running) ? T("Close the tournament") : T("Close")); acts.Add(close); }

            // ours: a list longer than the box scrolls: rows [0, n - tail) scroll in a window, the last `tail` rows stay under it
            if (_otPos >= opts.Count) _otPos = Mathf.Max(0, opts.Count - 1);
            int tail = _otPhase == OtPhase.Browse ? 3 + (canClose ? 1 : 0) : (canClose ? 1 : 0);
            int listN = opts.Count - tail, win = OtRows - tail;
            var shownRows = new List<int>();
            if (opts.Count > OtRows && listN > win)
            {
                int first = OtScrollFor(listN, win);
                for (int i = first; i < first + win; i++) shownRows.Add(i);
                for (int i = listN; i < opts.Count; i++) shownRows.Add(i);
            }
            else for (int i = 0; i < Mathf.Min(opts.Count, OtRows); i++) shownRows.Add(i);
            int cursorRow = Mathf.Max(0, shownRows.IndexOf(_otPos));

            // too many lines for the screen: the oldest results go first (the cup's line stays), then the end is cut
            int maxLines = Mathf.Max(4, 13 - shownRows.Count);
            while (lines.Count > maxLines && (_otPhase == OtPhase.Running || _otPhase == OtPhase.Done) && lines.Count > 2) lines.RemoveAt(1);
            if (lines.Count > maxLines) lines.RemoveRange(maxLines, lines.Count - maxLines);

            // every text is held inside its box: letters past the box's inside width are not drawn
            var s = new SysScreen();
            s.Text(T("ONLINE TOURNAMENT"), TbTx, 0x15, wrap); s.Box(TbX, 0x10, TbW, 0x16);
            int y = 0x31;
            foreach (var l in lines) { s.Text(l, TbTx, y, wrap); y += 12; }
            s.Box(TbX, 0x2C, TbW, 12 * lines.Count + 10);
            int oy = 0x2C + 12 * lines.Count + 10 + 6 + 5;
            for (int r = 0; r < shownRows.Count; r++) s.Text(opts[shownRows[r]], TbTx, oy + 12 * r, wrap);
            if (shownRows.Count > 0)
            {
                s.Box(TbX, oy - 5, TbW, 12 * shownRows.Count + 10);
                s.Cursor(TbTx - 3, oy, TbW - 0x10, 0xC, 12, shownRows.Count);
                s.Pos = cursorRow;
            }
            _sys = _otSys = s;
            if (opts.Count == 0) return;
            int ms = MenuStep();
            int side = (_otPhase == OtPhase.EnterPass && _otPos < 4) || (_otPhase == OtPhase.FindCode && _otPos < 6) || (_otPhase == OtPhase.Browse && _otPos < listN && listN > win)
                ? GameMenus.SideStep() : 0;
            if (side != 0 && _otPhase == OtPhase.EnterPass) { _otDigits[_otPos] = (_otDigits[_otPos] + side + 10) % 10; Snd(2); }
            else if (side != 0 && _otPhase == OtPhase.FindCode) { _otFind[_otPos] = (_otFind[_otPos] + side + OtCodeLetters.Length) % OtCodeLetters.Length; Snd(2); }
            // the list: left / right turns a page (held at its first / last row)
            else if (side != 0) { _otPos = Mathf.Clamp(_otPos + side * win, 0, listN - 1); _otScroll = Mathf.Clamp(_otScroll + side * win, 0, listN - win); Snd(2); }
            else if (ms != 0) { _otPos = (_otPos + ms + opts.Count) % opts.Count; Snd(2); }
            else if (ActionPressed()) { Snd(3); acts[_otPos](); }
            else if (GameMenus.CancelKey() && _otPhase == OtPhase.FindCode) { Snd(4); _otPhase = OtPhase.Browse; _otPos = 0; }
            else if (GameMenus.CancelKey() && canClose) { Snd(4); close(); }
            if (!_otOn && _sys == _otSys) { _sys = null; _otSys = null; }
        }

        bool _otAsking;

        // a No / Yes question over the tournament screen (No chosen first): "You are leaving the tournament" (Close), and the host's
        // "Start now?" when places are still empty. Drawn and answered by the tournament's own loop (as ConfirmNoFirst's box), not a
        // coroutine of its own: a bracket or match starting, or the tournament moving on (the full lobby's own start), puts it away as No
        int _otLeaveSel; bool _otLeaveFresh; SysScreen _otLeaveSys;
        string _otAskText = ""; System.Action _otAskYes; OtPhase _otAskPhase;

        void OtAsk(string text, System.Action yes)
        {
            _otAsking = true; _otLeaveSel = 0; _otLeaveFresh = true;
            _otAskText = text; _otAskYes = yes; _otAskPhase = _otPhase;
            if (_sys != null && _sys == _otSys) { _sys = null; _otSys = null; }
        }

        void OtAskLeave()
        {
            string q = _otHost && (_otPhase == OtPhase.Lobby || _otPhase == OtPhase.Running)
                ? "You are leaving the tournament. It ends for everyone. Leave?" : "You are leaving the tournament. Leave?";
            OtAsk(T(q), () => _otOn = false);
        }

        // ours (the user's request 2026-11-14): Start with places still empty asks first; a full lobby starts at once
        void OtAskStart()
        {
            int empty = 8 - _otE.Count;
            if (empty <= 0) { OtHostStart(); return; }
            OtAsk(empty == 1 ? T("Start now? The empty place gets a computer Digimon.")
                             : Tr.Format("Start now? The {0} empty places get computer Digimon.", empty), OtHostStart);
        }

        void OtLeaveTick()
        {
            if (_otPhase != _otAskPhase) { OtLeaveEnd(false); return; }
            var s = new SysScreen();
            var lines = Wrap(_otAskText, 0x110 - 0x14);
            for (int i = 0; i < lines.Count; i++) s.Text(lines[i], 0x22, 0x37 + 12 * i);
            s.Box(0x18, 0x32, 0x110, 12 * lines.Count + 10);
            int y = 0x37 + 12 * lines.Count + 0x12;
            s.Text(Exe(0x801346B0), 0x22, y); s.Text(Exe(0x801346AC), 0x22, y + 12);     // No / Yes
            s.Box(0x18, y - 5, 0x38, 0x22);
            s.Cursor(0x22 - 3, y, 33, 12, 12, 2);
            s.Pos = _otLeaveSel;
            _sys = _otLeaveSys = s;
            // the frame it opens: the Cross that chose Close / Start is not read again
            if (_otLeaveFresh) { _otLeaveFresh = false; return; }
            int step = MenuStep();
            if (step < 0 && _otLeaveSel > 0) { _otLeaveSel = 0; Snd(2); }
            else if (step > 0 && _otLeaveSel < 1) { _otLeaveSel = 1; Snd(2); }
            else if (GameMenus.CancelKey()) { Snd(4); OtLeaveEnd(false); }
            else if (ActionPressed()) { Snd(3); OtLeaveEnd(_otLeaveSel == 1); }
        }

        void OtLeaveEnd(bool yes)
        {
            _otAsking = false;
            if (_sys != null && _sys == _otLeaveSys) _sys = null;
            _otLeaveSys = null;
            var act = _otAskYes; _otAskYes = null;
            if (yes && act != null) act();
            if (!_otOn && _sys != null && _sys == _otSys) { _sys = null; _otSys = null; }
        }

        bool OtKnownPair(int m, out int a, out int b)
        {
            a = b = -1;
            if (m < 4) { a = 2 * m; b = 2 * m + 1; }
            else if (m < 6) { a = _otW[(m - 4) * 2]; b = _otW[(m - 4) * 2 + 1]; }
            else { a = _otW[4]; b = _otW[5]; }
            return a >= 0 && b >= 0;
        }

        // still in: no decided match of ours went to the other side
        bool OtStillIn()
        {
            if (_otMe < 0) return false;
            for (int m = 0; m < 7; m++)
            {
                if (_otW[m] < 0) continue;
                int a, b;
                if (OtKnownPair(m, out a, out b) && (a == _otMe || b == _otMe) && _otW[m] != _otMe) return false;
            }
            return true;
        }

        IEnumerator OtRefresh()
        {
            if (_otBusy) yield break;
            _otBusy = true; _otListErr = "";
            yield return TournamentClient.List((l, e) => { _otList = l; _otListErr = e; });
            _otBusy = false;
        }

        void OtJoin(TournamentClient.Info info)
        {
            OtCleanup();
            _otHost = false; _otJoinSent = false;
            _otCupName = info.cup; _otHostName = info.host; _otCode = info.code;
            _otStatus = "Linking with the host...";
            _otWs = new TournamentClient.Socket(TournamentClient.PlayerUrl(info.code));
            _otPhase = OtPhase.Joining; _otPos = 0; _otJoinStarted = Time.realtimeSinceStartup;
        }

        // ---------------------------------------------------------------- ours: the long list and Find by code

        /// <summary>The first list row shown when n rows scroll in a window of win: the cursor's row is kept in sight (on the fixed rows
        /// under the list the window stays where it was).</summary>
        int OtScrollFor(int n, int win)
        {
            if (_otPos < n)
            {
                if (_otPos < _otScroll) _otScroll = _otPos;
                if (_otPos >= _otScroll + win) _otScroll = _otPos - win + 1;
            }
            _otScroll = Mathf.Clamp(_otScroll, 0, Mathf.Max(0, n - win));
            return _otScroll;
        }

        /// <summary>One open tournament's row: code, host, players, password mark. A host name too long for the box is cut short ("..").</summary>
        static string OtListRow(TournamentClient.Info info, int width)
        {
            string after = "  " + Tr.Format("{0} of 8", info.players.Length) + (info.locked ? "  " + T("Password") : "");
            string host = info.host ?? "", row = info.code + "  " + host + after;
            var font = Battle.StatFont;
            if (font == null || font.Measure(row) <= width) return row;
            while (host.Length > 0 && font.Measure(info.code + "  " + host + ".." + after) > width) host = host.Substring(0, host.Length - 1);
            return info.code + "  " + host + ".." + after;
        }

        /// <summary>Joins a tournament of the list (or one found by its code): its password first if it has one.</summary>
        void OtPick(TournamentClient.Info info)
        {
            if (!info.locked) { _otJoinPass = ""; OtJoin(info); return; }
            _otPassFor = info; for (int d = 0; d < 4; d++) _otDigits[d] = 0;
            _otPhase = OtPhase.EnterPass; _otPos = 0;
        }

        string OtFindCode()
        {
            var c = new char[6];
            for (int d = 0; d < 6; d++) c[d] = OtCodeLetters[_otFind[d]];
            return new string(c);
        }

        /// <summary>A pasted code: its letters in capitals, spaces and other marks left out; it needs all 6 of the service's letters.</summary>
        void OtPasteCode(string text)
        {
            var got = new List<int>();
            foreach (char ch in (text ?? "").ToUpperInvariant())
            {
                int i = OtCodeLetters.IndexOf(ch);
                if (i >= 0) got.Add(i);
                else if (char.IsLetterOrDigit(ch)) { got.Clear(); break; }     // a letter the codes never have: not a code
            }
            if (got.Count != 6) { OtSay("That is not a tournament code. A code has 6 letters."); return; }
            for (int d = 0; d < 6; d++) _otFind[d] = got[d];
        }

        /// <summary>Looks the code up in a fresh list of the open tournaments (the service lists every one of them), then joins it.</summary>
        IEnumerator OtFind()
        {
            _otFinding = true;
            string code = OtFindCode(), err = "";
            List<TournamentClient.Info> list = null;
            yield return TournamentClient.List((l, e) => { list = l; err = e; });
            _otFinding = false;
            if (_otPhase != OtPhase.FindCode || !_otOn) yield break;
            if (!string.IsNullOrEmpty(err)) { OtSay(err); yield break; }
            if (list != null) _otList = list;
            var found = list != null ? list.Find(t => t.code == code) : null;
            if (found == null) { OtSay("No open tournament has that code."); yield break; }
            OtPick(found);
        }
    }
}
