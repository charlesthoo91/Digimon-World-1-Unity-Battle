using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The arena tournaments (Greymon's File City Arena, maps ROOM13 / ROOM19, script 168 -> script 213 section 0x36; Beetle Land's BETL02, script 102).
    /// Built-in 0x12 = initTournamentSchedule (dget/dget.c, DGET_REL.BIN): the five-day schedule, a cup's info, registration (flags 37-39, 40;
    /// variables 2 day, 3 cup, 4 partner type). updateTournamentRegistration (main/tournament.c, every frame): the reminders, DG0 sections 0x4D9 / 0x4DA.
    /// Built-in 0x06 = scriptStartTournament -> startTournament (main/utils2.c) -> STD_tournamentMain (std/std_setup.c, STD_REL.BIN): eight
    /// entrants, three matches, then the wins counters, the cup's flag (15 + cup) and variable 255 = matches won; the script gives the prizes.
    /// A cup's data is a table in section 0x0A of the map's own script (getCupDataJumpTable): per cup six entries, 0 its info text, 1 the entry
    /// requirements, 2 the partners that are too strong for it, 3 the partners that may enter, 4 the opponents.
    /// The arena itself (the ring, its camera, the bracket, the introductions, the WIN / LOSE / draw letters, the podium, its HUD): PlayableRoomArena,
    /// ArenaStage, ArenaScreens.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const uint StdBase = 0x80052AE0;            // STD_REL.BIN's load address (std.yaml: file 0x41C8 = 0x80056CA8)
        const uint StdOpponentBaseStats = 0x800799B4, StdWinChances = 0x8007A58C, StdStrDamage = 0x8007A658;
        const uint TournamentNames = 0x801300E0, StdStrDealt = 0x80134878;
        const int FlagRegistered = 37, FlagRemindDay = 38, FlagRemindEvening = 39, FlagTooStrong = 40;

        TournamentView _tourView;
        byte[] _stdBin;
        bool _tourOn;                   // a tournament runs: the room's after-fight code stays out of it (Update)
        readonly List<Npc> _tourHidden = new List<Npc>();

        TournamentView TourView()
        {
            if (_tourView == null) { _tourView = TournamentView.Load(_disc, Battle.StatFont); if (_tourView != null) _tourView.Sound = Snd; }
            return _tourView;
        }

        byte[] StdBin { get { return _stdBin ?? (_stdBin = _disc.ReadFile("/STD_REL.BIN")); } }
        int StdAt(uint a) { return (int)(a - StdBase); }

        int GameDay { get { return _dayCount % 30; } }                      // DAY
        int MinutesOfDay { get { return _care.Hour * 60 + _care.Minute; } }  // minutesOfDay

        /// <summary>TOURNAMENT_NAMES[n] (text code 0x12).</summary>
        string TournamentName(int n)
        {
            if (n < 0 || n >= 23) return "";
            return Exe(System.BitConverter.ToUInt32(ExeBytes, ExeAt(TournamentNames) + 4 * n));
        }

        // ------------------------------------------------------------------ the cup data (getCupDataJumpTable / getCupDataJumpTableEntry)

        byte[] ArenaScript() { return _scripts.GetScriptBytes(_scripts.ScriptIdForMap(_mapId)); }

        static int ScriptSection(byte[] s, int section)
        {
            for (int o = 2; o + 4 <= s.Length; o += 4)
            {
                int id = System.BitConverter.ToUInt16(s, o);
                if (id == 0xFFFF) break;
                if (id == section) return System.BitConverter.ToUInt16(s, o + 2);
            }
            return -1;
        }

        // the byte after entry k's two-byte command (6B 00 data, or 1A 00 for the text) of cup 'cup'; -1 when the map has no table
        static int CupEntry(byte[] s, int cup, int k)
        {
            if (s == null) return -1;
            int sec = ScriptSection(s, 10);
            if (sec < 0) return -1;
            int table = System.BitConverter.ToUInt16(s, sec + cup * 4 + 2);
            return System.BitConverter.ToUInt16(s, table + k * 4 + 2) + 2;
        }

        // a list of Digimon types up to the first byte 0xFE or more
        static List<int> CupList(byte[] s, int at)
        {
            var r = new List<int>();
            if (at < 0) return r;
            for (int p = at; p < s.Length && s[p] < 0xFE; p++) r.Add(s[p]);
            return r;
        }

        // tournamentCheckFair: the partner is in list 2 (too strong for the cup: it may enter, flag 40 lowers the Tamer Level afterwards)
        bool CupTooStrong(byte[] s, int cup) { return CupList(s, CupEntry(s, cup, 2)).Contains(PartnerType); }
        // tournamentCheckEligible: the partner is in list 3
        bool CupEligible(byte[] s, int cup) { return CupList(s, CupEntry(s, cup, 3)).Contains(PartnerType); }

        // isTournamentEnabled: entry 1 = {recruits needed, wins needed, kind}; the recruits counted are those of list 4 (flag 200 + type), or of
        // every Digimon (flags 200-311) when the list is empty. Kind 0 nothing more, 1 the Grade A cup won (flag 18), 2 'wins' of section 0x0B's list
        // recruited, else 'wins' of list 4 recruited, counting only up to the first type above the kind
        bool CupEnabled(byte[] s, int cup)
        {
            int req = CupEntry(s, cup, 1), pool = CupEntry(s, cup, 4);
            if (req < 0) return false;
            int minTriggers = s[req], minWins = s[req + 1], kind = s[req + 2];
            if (minTriggers == 0) return true;
            int count = 0;
            if (s[pool] < 0xFE) { foreach (int t in CupList(s, pool)) if (_state.GetFlag(t + 200)) count++; }
            else for (int f = 200; f < 312; f++) if (_state.GetFlag(f)) count++;
            if (count < minTriggers) return false;
            switch (kind)
            {
                case 0: return true;
                case 1: return _state.GetFlag(18);
                case 2:
                {
                    int b = ScriptSection(s, 0x0B);
                    count = 0;
                    if (b >= 0) foreach (int t in CupList(s, b + 2)) if (_state.GetFlag(t + 200)) count++;
                    return minWins <= count;
                }
                default:
                {
                    count = 0;
                    for (int p = pool; p < s.Length && s[p] <= kind; p++) if (_state.GetFlag(s[p] + 200)) count++;
                    return minWins <= count;
                }
            }
        }

        // fillEnabledTournamentTable: TOURNAMENT_SCHEDULE (exe 0x80130198, 30 days x 6) from today, the cups that are open; then today (only up to
        // 10:00) and tomorrow get bit 0x80 when the partner may enter and 0x40 when it is too strong
        void FillTournamentTable(byte[] cells)
        {
            for (int i = 0; i < cells.Length; i++) cells[i] = 0xFF;
            byte[] s = ArenaScript();
            int sched = ExeAt(0x80130198);
            for (int j = 0, day = GameDay; j < 5; j++, day++)
            {
                int w = j * 6;
                for (int i = 0; i < 6; i++)
                {
                    int cup = ExeBytes[sched + (day % 30) * 6 + i];
                    if (cup == 0xFF) break;
                    if (CupEnabled(s, cup)) cells[w++] = (byte)cup;
                }
            }
            for (int j = MinutesOfDay <= 600 ? 0 : 1; j < 2; j++)
                for (int i = 0; i < 6; i++)
                {
                    int e = cells[j * 6 + i];
                    if (e == 0xFF) break;
                    if (CupEligible(s, e)) cells[j * 6 + i] |= 0x80;
                    if (CupTooStrong(s, e)) cells[j * 6 + i] |= 0x40;
                }
        }

        string CupInfo(int cup)
        {
            byte[] s = ArenaScript();
            int at = CupEntry(s, cup, 0);
            return at < 0 ? "" : ScriptRunner.TextAt(s, at, _state, 0xFF, true) ?? "";
        }

        // ------------------------------------------------------------------ 0x12 initTournamentSchedule (Greymon's machine)

        IEnumerator TournamentSchedule()
        {
            _routineOn = true;
            int owner = _state.Vars[0xFE];
            // state 0: already registered = nothing
            var v = TourView();
            if (_state.GetFlag(FlagRegistered) || v == null) { ShopEnd(); yield break; }
            _state.Vars[3] = 0xFF;
            FillTournamentTable(v.Cells);
            _line = null; _choiceOpts = null;           // triggerBoxCloseFlag(0)
            RectInt origin = DialogueOrigin(owner) ?? new RectInt(0, 0, 10, 10);
            v.OpenSchedule(origin, GameDay);
            // state 2: tickTournamentSchedule (up / down / left / right repeat: setInputRepeatMask(0xF000))
            while (true)
            {
                yield return null;
                if (v.Info.State != 0)
                {
                    // tickTournamentInfo (opened with Start): Start, Cross or Triangle closes it
                    if (v.Info.State == 1 && (ActionPressed() || GameMenus.TriangleOnly())) { v.Close(v.Info); Snd(3); }
                    continue;
                }
                if (v.Grid.State != 1) continue;
                if (GameMenus.TriangleOnly()) { Snd(4); break; }
                int e = v.Cells[v.Col * 6 + v.Row];
                if (GameMenus.StartKey())
                {
                    if (e != 0xFF) { v.OpenInfo(false, origin, CupInfo(e & 0x3F)); Snd(3); }
                    else Snd(0xB);
                    continue;
                }
                if (ShopCross())
                {
                    if (e != 0xFF && (e & 0xC0) != 0) { _state.Vars[3] = (byte)(e & 0x3F); Snd(3); break; }
                    Snd(0xB);
                    continue;
                }
                int side = GameMenus.SideStep();
                if (side < 0) { v.Col = v.Col == 0 ? 4 : v.Col - 1; Snd(2); continue; }
                if (side > 0) { v.Col = v.Col == 4 ? 0 : v.Col + 1; Snd(2); continue; }
                int step = GameMenus.RepeatStep();
                if (step < 0) { v.Row = v.Row == 0 ? 5 : v.Row - 1; Snd(2); continue; }
                if (step > 0) { v.Row = v.Row == 5 ? 0 : v.Row + 1; Snd(2); continue; }
            }
            // state 3
            v.Close(v.Grid); v.Close(v.Label);
            int cup = _state.Vars[3];
            if (cup != 0xFF)
            {
                v.OpenInfo(true, origin, CupInfo(cup));
                bool strong = CupTooStrong(ArenaScript(), cup);
                _state.SetFlag(FlagTooStrong, strong);
                yield return HeadSay(0x4D8, strong ? 3 : 2, owner);
                int pick = 0;
                yield return HeadAsk(0x4D8, 4, 2, a => pick = a, 0xFD);       // state 4: the boy, "Enter / Don't enter"
                v.Close(v.Info);
                if (pick == 0)
                {
                    // state 5: registered for today, or for tomorrow (bit 0x80) when the second column was picked
                    _state.SetFlag(FlagRegistered, true); _state.SetFlag(FlagRemindDay, true); _state.SetFlag(FlagRemindEvening, true);
                    int day = GameDay;
                    if (v.Col != 0) day = (day + 1) | 0x80;
                    _state.Vars[2] = (byte)day;
                    _state.Vars[4] = (byte)PartnerType;
                }
            }
            ShopEnd();
        }

        // ------------------------------------------------------------------ updateTournamentRegistration (every frame)

        // A partner that is not the one registered loses the entry. Registered for today: from 12:00, once between 12:00 and 20:00 (flag 38) and once
        // after (flag 39), the reminder (DG0 0x4D9); from 23:01, or on another day, the "we forgot" text (0x4DA) and the entry is gone. Only while
        // no script runs, the boy walks freely, the partner follows and no box is open.
        void TickTournamentRegistration()
        {
            if (!_state.GetFlag(FlagRegistered)) return;
            if (PartnerType != _state.Vars[4]) { _state.SetFlag(FlagRegistered, false); return; }
            int value = _state.Vars[2];
            if ((value & 0x80) != 0) return;
            bool missed = GameDay != value;
            int minutes = MinutesOfDay;
            if (minutes < 0x2D0) return;
            if (minutes >= 0x565) missed = true;
            if (_runner != null || _pBusy || _routineOn || _changing || _tourOn) return;
            if (_menus != null && _menus.Active) return;
            if (_battle != null && _battle.Active) return;
            if (_tourView != null && _tourView.Showing) return;
            if (missed) { _state.SetFlag(FlagRegistered, false); StartDgSection(0x4DA); return; }
            int flag = minutes < 0x4B0 ? FlagRemindDay : FlagRemindEvening;
            if (!_state.GetFlag(flag)) return;
            _state.SetFlag(flag, false);
            StartDgSection(0x4D9);
        }

        // ------------------------------------------------------------------ 0x06 scriptStartTournament -> startTournament

        IEnumerator Tournament()
        {
            _routineOn = true; _tourOn = true;
            _line = null; _choiceOpts = null;           // closeBox(0)
            int cup = _state.Vars[3];
            byte[] s = ArenaScript();
            // the seven opponents: list 4 holds the types that may come; only the recruited ones (flag 200 + type) except in cup 0x16 (Beetle Cup);
            // an empty list = every recruited Digimon. Fewer than 7: the rest are random repeats. More: the partner's own type is left out, then 7 at random
            var pool = new List<int>();
            int at = CupEntry(s, cup, 4);
            if (at >= 0 && s[at] < 0xFE) { foreach (int t in CupList(s, at)) if (cup == 0x16 || _state.GetFlag(t + 200)) pool.Add(t); }
            else for (int i = 0; i < 0x70; i++) if (_state.GetFlag(i + 200)) pool.Add(i);
            var arg = new int[8]; arg[0] = cup;
            if (pool.Count <= 7)
            {
                for (int i = 0; i < pool.Count; i++) arg[1 + i] = pool[i];
                for (int i = pool.Count; i < 7; i++) arg[1 + i] = pool.Count > 0 ? pool[Random.Range(0, pool.Count)] : PartnerType;
            }
            else
            {
                pool.Remove(PartnerType);
                for (int i = 0; i < 7; i++) { int k = Random.Range(0, pool.Count); arg[1 + i] = pool[k]; pool.RemoveAt(k); }
            }
            if (_audio != null) _audio.StopMusic();
            int won = 0;
            yield return TournamentMain(arg, r => won = r);
            // back in startTournament: flag 37 off; the cup won when every match was won (cup 5: when two were); the counters
            _state.SetFlag(FlagRegistered, false);
            int expected = cup != 5 ? 3 : 2;
            if (won == expected) { _state.AddStat(17, 1); _state.SetFlag(cup + 15, true); }
            else _state.AddStat(19, 1);
            _state.AddStat(18, won);
            _state.Vars[0xFF] = (byte)won;
            Debug.Log("[tournament] " + TournamentName(cup) + ": " + won + " match(es) won");
            _tourOn = false; _routineOn = false;
            if (_runner != null) AdvanceScript();
        }

        // STD_tournamentMain: three type swaps, the arena (STD_initializeTournament), the bracket's opening, then for each match the bracket up to
        // it, the opponent, its introductions and the fight; a champion gets the last bracket and the podium. Returns the matches won
        IEnumerator TournamentMain(int[] arg, System.Action<int> done)
        {
            for (int n = 1; n < 8; n++)
            {
                if (arg[n] == 0x40) arg[n] = 0x36;
                if (arg[n] == 0x3F) arg[n] = 0x30;
                if (arg[n] == 0x41) arg[n] = 0x2A;
            }
            yield return TourFade(1f, 5);          // fadeToBlack(5), STD_tickFrames(5)
            if (_audio != null) { _audio.SetMapSounds(0x10); _audio.BattleSounds(); }      // loadMapSounds(0x10); the fight sounds (bank 8) for the bracket's punches
            int arena = _mapId == 0x6A ? 1 : 0;
            int music = arena == 0 ? 0x1E : 0x20;     // STD_initializeTournament: STD_MUSIC
            // removeMapEntities, the boy off the screen
            _tourHidden.Clear();
            foreach (var n in _npcs) if (n.T != null && n.T.gameObject.activeSelf) { n.T.gameObject.SetActive(false); _tourHidden.Add(n); }
            bool boyShown = _player.gameObject.activeSelf;
            _player.gameObject.SetActive(false);
            Vector3 home = _partner.position;
            Quaternion homeRot = _partner.rotation;
            ArenaOn(arena, arg[0]);
            _p1 = Fighter(_partner, _partnerAnim, PartnerType, true);
            _arenaScr.InitBracket();
            int[] opponents = SetupParticipants(arg);
            _camState = 10; _camTimer = 0;          // STD_setVSPhase(0xA)
            SetPlace(_p1, 0, 0, 0, 0x400);          // ours: somewhere in the ring while the bracket covers it
            _partner.gameObject.SetActive(false);   // STD_addBracketIntro / STD_addBracket: ENTITY_TABLE[1]->isOnScreen = 0
            // STD_addBracketIntro: the cup's title over the darkened picture, the bracket fading in from frame 101
            _arenaScr.AddIntro();
            if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); _audio.PlayMusic(0x1D, 0); }
            yield return TourFade(0f, 5);
            yield return ArenaFrames(0x78);
            _arenaScr.RemoveIntro();
            _arenaScr.MatchResult = 0;
            int i = 0;
            while (_arenaScr.MatchResult != -1 && i != 3)
            {
                // STD_addBracket(1): the other pairs fight it out on the bracket up to the partner's match
                _arenaScr.AddBracket();
                _partner.gameObject.SetActive(false);
                if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); _audio.PlayMusic(0x1D, 1); _audio.SetMapSounds(0x10); }
                while (!_arenaScr.Finished) yield return ArenaFrames(1);
                _arenaScr.RemoveBracket();
                _partner.gameObject.SetActive(true);    // STD_removeBracket: isOnScreen = 1
                if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); }
                Npc foe = SpawnOpponent(opponents[i], i, arg[0]);
                if (foe == null) { _arenaScr.MatchResult = -1; break; }
                int result = 0;
                yield return RunMatch(foe, arena, music, r => result = r);
                _arenaScr.MatchResult = result;
                _arena.ShowScene(0);            // STD_removeWinScene / STD_removeLoseScene
                if (result == 1)
                {
                    if (_audio != null) _audio.SetMapSounds(0x10);
                    InitializeArenaCamera(); _camState = 10;
                }
                if (foe.T != null) Destroy(foe.T.gameObject);       // removeMapEntities
                _tourFoe = null; _p2 = null;
                i++;
            }
            if (_arenaScr.MatchResult == 1)
            {
                // the champion: the bracket's last scene, then the podium
                _arena.LoadChampion(_disc);
                _arenaScr.AddBracket();
                _partner.gameObject.SetActive(false);
                if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); _audio.PlayMusic(0x1D, 2); }
                while (!_arenaScr.Finished) yield return ArenaFrames(1);
                yield return TourFade(1f, 10);
                yield return ArenaFrames(1);
                _arenaScr.RemoveBracket();
                _partner.gameObject.SetActive(true);
                _arena.ShowPodium(true);        // STD_addPodiumRenderer
                InitializeChampionScene();
                yield return ArenaFrames(2);
                yield return TourFade(0f, 15);
                yield return ArenaFrames(1);
                _arena.AddChampion();
                while (!_arena.ChampDone) yield return ArenaFrames(1);
                _arena.ShowPodium(false); _arena.ShowScene(0);
                if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); }
            }
            // STD_deinitializeTournament
            if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); }
            int last = _arenaScr.MatchResult;
            yield return TourFade(1f, 1);
            yield return ArenaFrames(0xB);
            ArenaOff();
            _partner.gameObject.SetActive(true);
            if (_tourView != null) _tourView.ShowTimer = false;
            _p1 = null;
            _partner.position = home; _partner.rotation = homeRot;
            if (_partnerAnim != null) { _partnerAnim.TakeBodyMove(); _partnerAnim.SetSlot(IdleSlot); }
            _player.gameObject.SetActive(boyShown);
            foreach (var n in _tourHidden) if (n.T != null) n.T.gameObject.SetActive(true);
            _tourHidden.Clear();
            if (_audio != null && _mapId >= 0 && _mapId < _mapFlags.Count) _audio.SetMapSounds(_mapFlags[_mapId] & 0x1F);
            yield return TourFade(0f, 5);           // ours: the script's own fade is not ported here
            done(last == 1 ? i : i - 1);
        }

        IEnumerator TourFade(float to, int frames)
        {
            float from = _fade, t = 0f, len = frames / 20f;
            while (t < len) { t += Time.deltaTime; _fade = Mathf.Lerp(from, to, Mathf.Clamp01(t / len)); yield return null; }
            _fade = to;
        }

        // STD_setupParticipants: the partner takes a random place of 8, the seven others are shuffled into the rest; each first-round pair is decided
        // by STD_WIN_CHANCES[level - 3][level - 3] (the first one wins when a roll of 0-99 is under it), and so is the second-round pair that does not
        // meet the partner. The three opponents: the partner's pair, then the winner on its side, then the winner of the other half
        int[] SetupParticipants(int[] list)
        {
            var types = new int[8];
            int me = Random.Range(0, 8);        // STD_PLAYER_SLOT
            types[me] = PartnerType;
            for (int i = 1; i < 8; i++) { int j = Random.Range(0, 7) + 1; int t = list[i]; list[i] = list[j]; list[j] = t; }
            for (int i = 0, p = 1; i < 8; i++) if (i != me) types[i] = list[p++];
            var r1 = new int[4]; var r2 = new int[2];
            for (int i = 0; i < 4; i++) r1[i] = Random.Range(0, 100) < WinChance(types[i * 2], types[i * 2 + 1]) ? i * 2 : i * 2 + 1;
            if (me < 4)
            {
                r2[0] = me < 2 ? r1[1] : r1[0];
                r2[1] = Random.Range(0, 100) < WinChance(types[r1[2]], types[r1[3]]) ? r1[2] : r1[3];
            }
            else
            {
                r2[0] = Random.Range(0, 100) < WinChance(types[r1[0]], types[r1[1]]) ? r1[0] : r1[1];
                r2[1] = me < 6 ? r1[3] : r1[2];
            }
            var o = new int[3];
            o[0] = me % 2 == 0 ? types[me + 1] : types[me - 1];
            switch (me / 2)
            {
                case 0: o[1] = types[r1[1]]; o[2] = types[r2[1]]; break;
                case 1: o[1] = types[r1[0]]; o[2] = types[r2[1]]; break;
                case 2: o[1] = types[r1[3]]; o[2] = types[r2[0]]; break;
                default: o[1] = types[r1[2]]; o[2] = types[r2[0]]; break;
            }
            Debug.Log("[tournament] entrants " + string.Join(", ", System.Array.ConvertAll(types, t => SpeciesName(t))) + "; partner in place " + me);
            if (_arenaScr != null)
            {
                _arenaScr.PlayerSlot = me;
                for (int i = 0; i < 8; i++) _arenaScr.Types[i] = (byte)types[i];
                for (int i = 0; i < 4; i++) _arenaScr.Round1[i] = (byte)r1[i];
                for (int i = 0; i < 2; i++) _arenaScr.Round2[i] = (byte)r2[i];
            }
            return o;
        }

        // STD_WIN_CHANCES (STD_REL.BIN, 4 x 3 bytes) by DIGIMON_DATA level - 3
        int WinChance(int a, int b)
        {
            var ia = DigimonStatsCatalog.Get(_disc, a); var ib = DigimonStatsCatalog.Get(_disc, b);
            int la = (ia != null ? ia.Level : 3) - 3, lb = (ib != null ? ib.Level : 3) - 3;
            int at = StdAt(StdWinChances) + la * 3 + lb;        // read as the game does: a Mega (level 6) reads past its row
            return at >= 0 && at < StdBin.Length ? StdBin[at] : 0;
        }

        string SpeciesName(int type) { var i = DigimonStatsCatalog.Get(_disc, type); return i != null ? i.Name : type.ToString(); }

        // STD_initializeOpponent(type, slot, tier): base = STD_OPPONENT_BASE_STATS[cup]; each stat base +- up to 30% (0x1E - randomLimit(0x3D)),
        // HP and MP ten times that, x 1 (Rookie), x 2 (Champion) or x 2.5 (Ultimate); capped at 999 / 9999. Techniques: three of its first 15 slots at
        // random, leaving out the finishing moves and those whose MP x 3 is more than a tenth of its MP (slot 0x2E if none); slot 3 = its finishing move.
        Npc SpawnOpponent(int type, int slot, int tier)
        {
            if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
            if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
            DigimonEntry entry = null;
            foreach (var e in _entries) if (e.Type == type) { entry = e; break; }
            var info = DigimonStatsCatalog.Get(_disc, type);
            if (entry == null || info == null) { Debug.LogWarning("[tournament] no model for Digimon type " + type); return null; }
            GameObject go;
            try
            {
                MmdModel.UseLit = Hd2D && UseMapLights;
                MmdModel.Brightness = Hd2D && UseMapLights ? CharacterBrightness : 1f;
                go = MmdModel.Parse(_disc.ReadFile(entry.MmdPath)).Build(entry, _allTim);
            }
            finally { MmdModel.UseLit = false; MmdModel.Brightness = 1f; }
            go.transform.SetParent(transform, false);
            int baseStat = System.BitConverter.ToInt16(StdBin, StdAt(StdOpponentBaseStats) + 2 * tier);
            int mul = 1, div = 1;
            if (info.Level == 4) mul = 2; else if (info.Level == 5) { mul = 25; div = 10; }
            System.Func<int, int> roll = b => mul * (b + b * (0x1E - Random.Range(0, 0x3D)) / 100) / div;
            var d = new MapDigimon { Type = type, Script = 0xFF };
            d.Off = Mathf.Min(999, roll(baseStat)); d.Def = Mathf.Min(999, roll(baseStat));
            d.Spd = Mathf.Min(999, roll(baseStat)); d.Brain = Mathf.Min(999, roll(baseStat));
            d.Hp = Mathf.Min(9999, roll(baseStat * 10)); d.Mp = Mathf.Min(9999, roll(baseStat * 10));
            var usable = new List<int>();
            for (int i = 0; i < 15; i++)
            {
                int tech = info.RawMoves[i];
                if (tech == 0xFF || (tech >= 0x3A && tech < 0x71)) continue;
                var td = DigimonStatsCatalog.TechOf(tech);
                if (td == null || d.Mp / 10 < td.Mp) continue;      // MOVE_DATA mpCost x 3 (TechData.Mp is already x 3)
                usable.Add(i);
            }
            for (int i = 0; i < usable.Count; i++) { int j = Random.Range(0, usable.Count); int t = usable[i]; usable[i] = usable[j]; usable[j] = t; }
            for (int k = 0; k < 4; k++) d.MoveSlots[k] = 0xFF;
            if (usable.Count == 0) d.MoveSlots[0] = 0x2E;
            else for (int k = 0; k < 3 && k < usable.Count; k++) d.MoveSlots[k] = usable[k] + 0x2E;
            for (int i = 0; i < 16; i++) { int tech = info.RawMoves[i]; if (tech >= 0x3A && tech < 0x71) { d.MoveSlots[3] = i + 0x2E; break; } }
            // movesPrio is not set by the arena: it keeps what this entity slot had (NPC_ENTITIES[slot], the arena map's record 'slot')
            if (_map != null && slot < _map.Npcs.Count) for (int k = 0; k < 4; k++) d.MoveChance[k] = _map.Npcs[slot].MoveChance[k];
            else { for (int k = 0; k < 4; k++) d.MoveChance[k] = 1; Debug.LogWarning("[tournament] the map has no record " + slot + ": the opponent's technique chances are ours (all equal)"); }
            var n = new Npc { Data = d, T = go.transform, Anim = go.GetComponent<DigimonAnimator>(), Name = entry.Name, Record = -1 - slot };
            if (n.Anim != null) { n.Anim.DigimonType = type; n.Anim.SetSlot(Battle.IdleSlot); }
            Debug.Log("[tournament] opponent " + (slot + 1) + ": " + entry.Name + " HP " + d.Hp + " MP " + d.Mp + " Off " + d.Off + " Def " + d.Def + " Spd " + d.Spd + " Brain " + d.Brain);
            return n;
        }

        // STD_runMatch: the introductions (STD_runIntro), then STD_combatMain with the orbit camera; the knock-out camera goes after it (state 10)
        IEnumerator RunMatch(Npc foe, int which, int music, System.Action<int> done)
        {
            _tourFoe = foe;
            _p2 = Fighter(foe.T, foe.Anim, foe.Data.Type, false);
            if (_partner != null) _partner.gameObject.SetActive(true);
            yield return RunIntro(which, music);
            _camState = 1;
            int result = 0;
            yield return Combat(foe, music, r => result = r);
            RemoveCameraIntro();
            _camState = 10;
            done(result);
        }

        // STD_combatMain (Battle.Tournament): the arena's HUD and BATTLE text (ArenaScreens), its camera hooks, the ring's walls
        IEnumerator Combat(Npc foe, int music, System.Action<int> done)
        {
            var v = TourView();
            _battle.Tournament = true;
            _battle.TournamentWeak = (_state.Para.Condition & 0x60) != 0;
            _battle.TournamentMusic = track => { if (_audio != null) { _audio.StopMusic(); _audio.PlayMusic(music, track); } };
            _battle.OnTechStart = OnTechCamera;
            _battle.OnTechHit = f => SetRandomViewpoint(f == _battle.Partner ? _p1 : _p2, Random.Range(0, 4));
            _battle.OnKnockOut = f => SelectRandomCamera(f == _battle.Partner ? _p1 : _p2, 5, 0);
            _battle.OnVersus = () => { _arena.LoadVersus(_disc); _arena.AddVersus(); };
            _battle.VersusBusy = () => _arena != null && _arena.Scene == 3;
            _battle.MapId = _mapId;
            if (_audio != null) _audio.BattleSounds();
            if (v != null)
            {
                v.PartnerName = _menus != null ? _menus.PartnerName : _partnerName; v.EnemyName = foe.Name;
                v.GaveText = Exe(StdStrDealt); v.DamageText = StdString(StdStrDamage);
            }
            _fightFrames = 0;
            _battle.Start(_player, _anim, _partner, _partnerAnim, _menus != null ? _menus.PartnerName : _partnerName, DigimonStatsCatalog.Get(_disc, PartnerType),
                          foe.T, foe.Anim, foe.Name, DigimonStatsCatalog.Get(_disc, foe.Data.Type), foe.Data, Vector3.right, true, new List<Vector2Int>(), TileOf(_player.position), 0f);
            _battle.Walkable = _arena.Walkable;     // the ring's walls (STD_ARENA_COLLISIONS)
            _arenaScr.AddHud();
            // STD_combatSetup: the BATTLE text comes together (sound 0x10, at least 60 frames) while the orbit eye pulls back to 4200, then bursts
            // (sound 0x11) while it comes back to 1000
            _minDist = 200;
            _arenaScr.StartText();
            Snd(0x10);
            int frames = 0;
            while (frames < 60 || !_arenaScr.TextFinished)
            {
                if (_minDist < 4200) _minDist += 400;
                frames++;
                yield return ArenaFrames(1);
            }
            _arenaScr.StartBurst();         // STD_removeBattleStartText, STD_initializeBattleStartTextBurst: the same pieces fly apart
            Snd(0x11);
            while (!_arenaScr.TextFinished)
            {
                if (_minDist > 1000) _minDist -= 400;
                yield return ArenaFrames(1);
            }
            _arenaScr.StopText();
            _minDist = 1000;
            _camState = 1;
            _battle.TournamentStartDone = true;
            while (_battle.Active)
            {
                if (v != null)
                {
                    v.ShowTimer = true; v.Timer = _battle.Timer;        // STD_addFighterCounter(0x63) in STD_initializeCombat, gone at STD_deinitializeCombat's end
                    v.ShowTimeUpWord = _battle.TimeUpWord;
                    v.DamageByPartner = _battle.TourDamage[1]; v.DamageByEnemy = _battle.TourDamage[0];
                    if (_battle.TimeUpBox && v.TimeUp.State == 0) v.OpenTimeUp();
                    if (!_battle.TimeUpBox && (v.TimeUp.State == 1 || v.TimeUp.State == 2)) v.CloseNow(v.TimeUp);      // removeAnimatedUIBox(0, 0)
                }
                yield return null;
            }
            if (v != null) { v.ShowTimer = false; v.ShowTimeUpWord = false; v.CloseNow(v.TimeUp); }
            _arenaScr.HudOn = false; _arenaScr.MarkersOn = false;
            _battle.Tournament = false; _battle.TournamentMusic = null;
            _battle.OnTechStart = null; _battle.OnTechHit = null; _battle.OnKnockOut = null; _battle.OnVersus = null; _battle.VersusBusy = null;
            done(_battle.Result);
        }

        // a full-width string of STD_REL.BIN's data
        string StdString(uint a)
        {
            var sb = new System.Text.StringBuilder();
            byte[] b = StdBin;
            for (int p = StdAt(a); p < b.Length && b[p] != 0; p++)
            {
                if (b[p] >= 0x80 && p + 1 < b.Length && b[p + 1] != 0) { sb.Append(MapScripts.Pair(b[p] << 8 | b[p + 1])); p++; }
                else sb.Append((char)b[p]);
            }
            return sb.ToString();
        }

        void DrawTournament() { if (_tourView != null) _tourView.Draw(); }

        // ------------------------------------------------------------------ debug (F10)

        void TestArena()
        {
            if (_changing || _runner != null || _tourOn || (_battle != null && _battle.Active)) { Say("Not now."); return; }
            int mi = _mapNames.IndexOf("ROOM13");
            if (mi < 0) { Say("ROOM13 not found."); return; }
            _debugOpen = false;
            StartCoroutine(ChangeMap(PathOfMap(mi), 0));
        }

        // as if registered for 'cup' today and the script's 64 06 ran (no script: variable 255 is only logged)
        void TestTournament(int cup)
        {
            if (_changing || _runner != null || _tourOn || _routineOn || (_battle != null && _battle.Active)) { Say("Not now."); return; }
            if (CupEntry(ArenaScript(), cup, 4) < 0) { Say("This map has no cup table (go to ROOM13, ROOM19 or BETL02)."); return; }
            _debugOpen = false;
            _state.SetFlag(FlagRegistered, true); _state.Vars[2] = (byte)GameDay; _state.Vars[3] = (byte)cup; _state.Vars[4] = (byte)PartnerType;
            StartCoroutine(Tournament());
        }
    }
}
