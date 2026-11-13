using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod "Online battle"; not in the original game): the match between two friends' partners, as one tournament match in Greymon's arena
    /// (the arena world, introductions, BATTLE text, HUD, cameras, WIN / LOSE: PlayableRoomTournament / PlayableRoomArena). Neither player gives
    /// orders (script flag 2, Battle.Tamerless): both Digimon fight by their own Brains, as tournament opponents do.
    /// The host's PC runs the real fight (Battle). Twenty times a second it sends both fighters' place, facing, animation, HP, MP and finishing
    /// gauge, plus the counter; it also sends every technique started (its picture), every number over a head, the knock-out and the result.
    /// The friend's PC runs Battle as a copy (Battle.Puppet) and shows all of that. Its world is the host's turned half round the arena's middle,
    /// so each player sees their own Digimon on their own side.
    /// Messages (after MsgProfile = 1): 2 ready, 3 start, 4 state (sent once, may be lost), 5 technique, 6 number, 7 knock-out, 8 end (sure),
    /// 9 order, 10 introductions done, 11 finishing-move aura, 12 draw letters, 13 a technique's effect stopped, 14 the friend surrenders.
    /// The host also chooses the time (99 seconds, 300, none; MsgStart's third byte). L2 + R2 asks "Surrender?" (Yes / No); Yes puts one's own
    /// Digimon at 0 HP, so the match ends as a knock-out (the friend's PC asks the host to do it).
    /// Sides in messages are the host's: 0 the host's partner, 1 the friend's partner.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const byte MsgReady = 2, MsgStart = 3, MsgState = 4, MsgSwing = 5, MsgPop = 6, MsgKo = 7, MsgEnd = 8, MsgOrder = 9, MsgIntroDone = 10, MsgAura = 11, MsgVersus = 12, MsgStopFx = 13, MsgSurrender = 14;
        int _onlineTime;                           // 0 = 99 seconds, 1 = 300, 2 = no limit (the host chooses; MsgStart carries it)
        bool _surrenderAsk; int _surrenderSel;     // the "Surrender?" question is up; 0 = Yes, 1 = No

        static string OnlineTimeName(int t)
        {
            return t == 2 ? DW1Remake.Formats.Tr.Text("No limit", "ours") : DW1Remake.Formats.Tr.Format("{0} s", t == 1 ? 300 : 99);
        }
        bool _onlineOrders, _friendIntroDone;      // orders on (the host chooses; MsgStart carries it); the friend has finished the introductions
        bool _meReady, _friendReady, _onlineMatch, _onlineHost, _endSent, _netBattleSeen;
        float _stateAcc;
        readonly Vector3[] _netPos = new Vector3[2];
        readonly Quaternion[] _netRot = new Quaternion[2];
        readonly bool[] _netHave = new bool[2];
        string _onlineResult;
        int _onlineLastResult;                     // the last match: 1 won, -1 lost, 0 a draw or the link was lost (_onlineLinkLost)
        bool _onlineLinkLost, _onlineSudden;       // _onlineSudden: the online tournament's player vs player rule (Battle.SuddenDeath)

        // ---------------------------------------------------------------- the match (both PCs)

        // cpuType >= 0 (ours, the online tournament): a match against a computer Digimon of the cup on this PC alone (SpawnOpponent, RunMatch),
        // set up and put back the same way
        // watch != null (ours, the online tournament): this PC only watches another match as its copy: this PC's partner is swapped for the
        // watched side's Digimon (watch) for the match and put back afterwards. final: the champion's scene and podium after a won final
        IEnumerator OnlineMatch(bool host, int cpuType = -1, int cpuSlot = 0, int cup = 0, FriendProfile watch = null, bool final = false)
        {
            bool orders = _onlineOrders; _friendIntroDone = false;
            _onlineMatch = true; _onlineHost = host; _endSent = false; _netBattleSeen = false; _stateAcc = 0f; _onlineResult = null;
            _netHave[0] = _netHave[1] = false;
            _sdSeenAt = -1f; _netSudden = false; _netAuraOn = false;
            _tourOn = true;
            _line = null; _choiceOpts = null;
            // a friendly match changes nothing in the game: the partner's numbers, techniques, Bits, condition and mood are put back afterwards
            var statsBack = (ushort[])_state.Stats.Clone(); var techsBack = (bool[])_state.Techs.Clone();
            int moneyBack = _state.Money, happyBack = _state.Happiness, discBack = _state.Discipline;
            int condBack = _state.Para.Condition, tiredBack = _state.Para.Tiredness;
            int typeBack = PartnerType; string nameBack = _state.PartnerName;
            bool flag2 = _state.GetFlag(2);
            _state.SetFlag(2, !orders);             // orders off: IS_TAMERLESS_BATTLE (no orders, no items, no finishing-move button) on both PCs
            // both start with full HP and MP (the friend's numbers come as its maximums)
            _state.Stats[6] = _state.Stats[4]; _state.Stats[7] = _state.Stats[5];
            // the arena moves and re-lenses the main camera; the room's painted picture hangs on the map camera, so it is put back afterwards
            Vector3 camPos = _cam.transform.position; Quaternion camRot = _cam.transform.rotation;
            float camFov = _cam.fieldOfView, camFocal = _cam.focalLength; Vector2 camSensor = _cam.sensorSize, camShift = _cam.lensShift;
            bool camPhys = _cam.usePhysicalProperties; var camGate = _cam.gateFit;
            _battle.TimerStart = _onlineTime == 1 ? 300 : 99; _battle.NoTimeLimit = _onlineTime == 2;
            _surrenderAsk = false; Battle.InputLocked = false;
            yield return TourFade(1f, 5);
            if (_audio != null) { _audio.SetMapSounds(0x10); _audio.BattleSounds(); }
            int arena = _mapId == 0x6A ? 1 : 0;
            int music = arena == 0 ? 0x1E : 0x20;
            _tourHidden.Clear();
            foreach (var n in _npcs) if (n.T != null && n.T.gameObject.activeSelf) { n.T.gameObject.SetActive(false); _tourHidden.Add(n); }
            bool boyShown = _player.gameObject.activeSelf;
            _player.gameObject.SetActive(false);
            if (watch != null)
            {
                // watching: the watched side's Digimon and numbers stand in for this PC's partner until the match ends
                SwapPartner(watch.Type);
                _state.PartnerName = watch.Name;
                _state.Stats[4] = (ushort)watch.Hp; _state.Stats[5] = (ushort)watch.Mp; _state.Stats[0] = (ushort)watch.Off;
                _state.Stats[1] = (ushort)watch.Def; _state.Stats[2] = (ushort)watch.Spd; _state.Stats[3] = (ushort)watch.Brain;
                _state.Stats[6] = _state.Stats[4]; _state.Stats[7] = _state.Stats[5];
            }
            Vector3 home = _partner.position; Quaternion homeRot = _partner.rotation;
            ArenaOn(arena, cpuType >= 0 ? cup : 0);
            _p1 = Fighter(_partner, _partnerAnim, PartnerType, true);
            _arenaScr.InitBracket();
            _camState = 10; _camTimer = 0;
            Npc foe = cpuType >= 0 ? SpawnOpponent(cpuType, cpuSlot, cup) : SpawnFriend(_friend);
            int result = 0; bool linkLost = false;
            if (foe != null)
            {
                _battle.Puppet = !host;
                _battle.EnemyCommanded = host && orders && cpuType < 0;
                _battle.SuddenDeath = _onlineSudden;
                if (!host && orders) _battle.NetOrder = o => { if (_net != null) _net.SendSure(OrderMessage(o)); };
                if (host)
                {
                    _battle.NetSwing = (f, tech, to) => MatchSend(new[] { MsgSwing, (byte)SideOf(f), (byte)tech }, true);
                    _battle.NetKnockOut = f => MatchSend(new[] { MsgKo, (byte)SideOf(f) }, true);
                    _battle.NetAura = f => MatchSend(new[] { MsgAura, (byte)SideOf(f) }, true);
                    _battle.NetVersus = () => MatchSend(new[] { MsgVersus }, true);
                    _battle.NetStopFx = f => MatchSend(new[] { MsgStopFx, (byte)SideOf(f) }, true);
                }
                yield return TourFade(0f, 5);
                if (cpuType >= 0) yield return RunMatch(foe, arena, music, r => result = r);
                else yield return OnlineRunMatch(foe, arena, music, r => result = r);
                if (_battle.WinnerSide == 2) result = 0;      // a draw (Result counts it as lost, as the tournament does)
                linkLost = cpuType < 0 && (_net == null || _net.Now != NetLink.State.Linked);
                _arena.ShowScene(0);
                if (foe.T != null) Destroy(foe.T.gameObject);
                // ours (online tournament): the champion's scene and podium, as STD_tournamentMain after a won final
                if (final && result == 1 && watch == null) yield return OtChampionScene();
            }
            else Debug.LogWarning("[online] the friend's Digimon could not be made (type " + (_friend != null ? _friend.Type : -1) + ")");
            _tourFoe = null; _p2 = null;
            _battle.TimerStart = 99; _battle.NoTimeLimit = false; _battle.SuddenDeath = false; _surrenderAsk = false; Battle.InputLocked = false;
            _onlineLastResult = result; _onlineLinkLost = linkLost;
            _battle.Puppet = false; _battle.NetSwing = null; _battle.NetKnockOut = null; _battle.NetAura = null; _battle.NetVersus = null; _battle.NetStopFx = null; _battle.NetOrder = null; _battle.EnemyCommanded = false;
            // as STD_deinitializeTournament: back to the room
            if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); }
            yield return TourFade(1f, 1);
            yield return ArenaFrames(0xB);
            ArenaOff();
            if (watch != null) { SwapPartner(typeBack); _state.PartnerName = nameBack; }
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
            _state.SetFlag(2, flag2);
            System.Array.Copy(statsBack, _state.Stats, statsBack.Length); System.Array.Copy(techsBack, _state.Techs, techsBack.Length);
            _state.Money = moneyBack; _state.Happiness = happyBack; _state.Discipline = discBack;
            _state.Para.Condition = condBack; _state.Para.Tiredness = tiredBack;
            _onlineResult = result == 1 ? "You won!" : result == -1 ? "You lost." : linkLost ? "The link was lost." : "A draw.";
            yield return TourFade(0f, 5);
            _onlineMatch = false; _tourOn = false; _meReady = _friendReady = false;
        }

        // RunMatch (STD_runMatch) with one stop: the fight starts only when both PCs are past the introductions (each skips its own with Cross)
        IEnumerator OnlineRunMatch(Npc foe, int which, int music, System.Action<int> done)
        {
            _tourFoe = foe;
            _p2 = Fighter(foe.T, foe.Anim, foe.Data.Type, false);
            if (_partner != null) _partner.gameObject.SetActive(true);
            yield return RunIntro(which, music);
            if (!_otWatching) MatchSend(new[] { MsgIntroDone }, true);
            float waited = 0f;
            while (!_otWatching && !_friendIntroDone && _net != null && _net.Now == NetLink.State.Linked && waited < 120f)
            {
                if (waited > 0.5f) Say(DW1Remake.Formats.Tr.Text("Waiting for your friend to finish the introductions...", "ours"));
                waited += Time.deltaTime;
                yield return null;
            }
            _camState = 1;
            int result = 0;
            yield return Combat(foe, music, r => result = r);
            RemoveCameraIntro();
            _camState = 10;
            done(result);
        }

        byte[] OrderMessage(string order)
        {
            var t = Encoding.UTF8.GetBytes(order ?? "");
            var b = new byte[1 + t.Length]; b[0] = MsgOrder; t.CopyTo(b, 1);
            return b;
        }

        // the friend's partner, made like a tournament opponent (SpawnOpponent) but with the friend's own numbers and techniques
        Npc SpawnFriend(FriendProfile p)
        {
            if (p == null) return null;
            if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
            if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
            DigimonEntry entry = null;
            foreach (var e in _entries) if (e.Type == p.Type) { entry = e; break; }
            var info = DigimonStatsCatalog.Get(_disc, p.Type);
            if (entry == null || info == null) return null;
            GameObject go;
            try
            {
                MmdModel.UseLit = Hd2D && UseMapLights;
                MmdModel.Brightness = Hd2D && UseMapLights ? CharacterBrightness : 1f;
                go = MmdModel.Parse(_disc.ReadFile(entry.MmdPath)).Build(entry, _allTim);
            }
            finally { MmdModel.UseLit = false; MmdModel.Brightness = 1f; }
            go.transform.SetParent(transform, false);
            var d = new MapDigimon { Type = p.Type, Script = 0xFF, Hp = p.Hp, Mp = p.Mp, Off = p.Off, Def = p.Def, Spd = p.Spd, Brain = p.Brain };
            for (int k = 0; k < 4; k++)
            {
                d.MoveSlots[k] = 0xFF;
                int at = p.Techs[k] != 0xFF ? info.Moves.IndexOf(p.Techs[k]) : -1;
                if (at >= 0) d.MoveSlots[k] = at + DigimonStats.FirstMoveSlot;
            }
            if (d.MoveSlots[0] == 0xFF) d.MoveSlots[0] = DigimonStats.FirstMoveSlot;
            // the technique chances: the arena's record 0, as for a tournament opponent in that place (STD_initializeOpponent)
            if (_map != null && _map.Npcs.Count > 0) for (int k = 0; k < 4; k++) d.MoveChance[k] = _map.Npcs[0].MoveChance[k];
            else for (int k = 0; k < 4; k++) d.MoveChance[k] = 1;
            string name = string.IsNullOrEmpty(p.Name) ? entry.Name : p.Name;
            var n = new Npc { Data = d, T = go.transform, Anim = go.GetComponent<DigimonAnimator>(), Name = name, Record = -1 };
            if (n.Anim != null) { n.Anim.DigimonType = p.Type; n.Anim.SetSlot(Battle.IdleSlot); }
            return n;
        }

        // ---------------------------------------------------------------- every frame while the match runs

        void OnlineFrame()
        {
            if (_battle == null) return;
            SurrenderTick();
            if (_battle.Active) _netBattleSeen = true;
            if (_onlineHost)
            {
                if (_battle.Active && _netBattleSeen)
                {
                    _stateAcc += Time.deltaTime;
                    if (_stateAcc >= 0.05f) { _stateAcc = 0f; MatchSend(StateMessage(), false); }
                    var cols = new List<Color>(); var icons = new List<int>();
                    var pops = _battle.TakeNewPops(cols, icons);
                    for (int i = 0; i < pops.Count; i++) MatchSend(PopMessage(pops[i].Key, pops[i].Value, cols[i], icons[i]), true);
                }
                else if (_netBattleSeen && !_endSent)
                {
                    _endSent = true;
                    MatchSend(new[] { MsgEnd, (byte)(sbyte)_battle.Result, (byte)Mathf.Max(0, _battle.WinnerSide) }, true);
                }
                return;
            }
            // the friend's PC: glide to the host's places (sent 20 times a second) so the movement looks smooth
            if (_battle.Active && _battle.Puppet)
            {
                // the link was lost: the copy cannot go on (watching: nothing came for 15 s)
                bool lost = _otWatching ? Time.realtimeSinceStartup - _otFeedAt > 15f : _net == null || _net.Now != NetLink.State.Linked;
                if (lost) { _battle.PuppetEnd(0, 2); return; }
                float k = 1f - Mathf.Exp(-15f * Time.deltaTime);
                for (int s = 0; s < 2; s++)
                {
                    var f = NetSide(s);
                    if (f == null || f.T == null || !_netHave[s]) continue;
                    f.T.position = Vector3.Lerp(f.T.position, _netPos[s], k);
                    f.T.rotation = Quaternion.Slerp(f.T.rotation, _netRot[s], k);
                }
            }
        }

        // ---------------------------------------------------------------- the messages

        void ReadMatch(byte[] m)
        {
            switch (m[0])
            {
                case MsgReady: _friendReady = true; break;
                case MsgStart:
                    if (_onlineRole == 0 && !_onlineMatch && _friend != null)
                    {
                        _onlineOrders = m.Length >= 2 && m[1] != 0; _onlineTime = m.Length >= 3 ? Mathf.Clamp(m[2], 0, 2) : 0;
                        StartCoroutine(OnlineMatch(false));
                    }
                    break;
                case MsgIntroDone: _friendIntroDone = true; break;
                case MsgSurrender: if (_onlineHost && _onlineMatch && _battle != null && _battle.Active && _battle.Enemy != null) _battle.Enemy.Hp = 0; break;
                case MsgOrder: if (_onlineHost && _onlineMatch && _battle != null && _battle.Active) _battle.RemoteOrder(Encoding.UTF8.GetString(m, 1, m.Length - 1)); break;
                case MsgState: if (!_onlineHost && _onlineMatch) ReadState(m); break;
                case MsgSwing:
                    if (!_onlineHost && _onlineMatch && m.Length >= 3 && _battle != null && _battle.Active)
                        _battle.PuppetSwing(NetSide(m[1]), m[2], NetSide(m[1] == 0 ? 1 : 0));
                    break;
                case MsgPop: if (!_onlineHost && _onlineMatch && _battle != null && _battle.Active) ReadPop(m); break;
                case MsgAura: if (!_onlineHost && _onlineMatch && m.Length >= 2 && _battle != null && _battle.Active) { _battle.PuppetAura(NetSide(m[1])); _netAuraOn = true; } break;
                case MsgStopFx: if (!_onlineHost && _onlineMatch && m.Length >= 2 && _battle != null && _battle.Active) _battle.PuppetStopFx(NetSide(m[1])); break;
                case MsgVersus: if (!_onlineHost && _onlineMatch && _battle != null && _battle.Active) _battle.PuppetVersus(); break;
                case MsgKo: if (!_onlineHost && _onlineMatch && m.Length >= 2 && _battle != null) _battle.PuppetKnockOut(NetSide(m[1])); break;
                case MsgEnd:
                    if (!_onlineHost && _onlineMatch && m.Length >= 3 && _battle != null && _battle.Active)
                    {
                        // the host's result is the other way round here: its partner is this PC's enemy
                        int r = (sbyte)m[1], w = m[2];
                        _battle.PuppetEnd(-r, w == 0 ? 1 : w == 1 ? 0 : w);
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- surrender (ours): L2 + R2, then Yes / No

        // a match message to the other PC, and (ours, online tournament) a copy for the players watching it
        System.Action<byte[]> _matchMirror;
        void MatchSend(byte[] m, bool sure)
        {
            if (_net != null) { if (sure) _net.SendSure(m); else _net.SendFast(m); }
            if (_matchMirror != null) _matchMirror(m);
        }

        void SurrenderTick()
        {
            bool fighting = _battle.Active && _battle.TournamentStartDone && !_otWatching;
            if (!fighting) { if (_surrenderAsk) { _surrenderAsk = false; Battle.InputLocked = false; } return; }
            if (!_surrenderAsk)
            {
                if (Pad.Held(PadButton.L2) && Pad.Held(PadButton.R2) && (Pad.Pressed(PadButton.L2) || Pad.Pressed(PadButton.R2)))
                {
                    _surrenderAsk = true; _surrenderSel = 1; Battle.InputLocked = true; Snd(3);      // No first: a slip of the fingers gives nothing away
                }
                return;
            }
            int side = GameMenus.SideStep(), step = MenuStep();
            if (side != 0 || step != 0) { _surrenderSel = 1 - _surrenderSel; Snd(2); }
            if (GameMenus.TriangleKey() || GameMenus.CancelKey()) { _surrenderAsk = false; Battle.InputLocked = false; Snd(4); return; }
            if (!ActionPressed()) return;
            Snd(3);
            _surrenderAsk = false; Battle.InputLocked = false;
            if (_surrenderSel != 0) return;
            // Yes: one's own Digimon goes to 0 HP and the match ends as a knock-out (the host runs the fight)
            if (_onlineHost) { if (_battle.Partner != null) _battle.Partner.Hp = 0; }
            else if (_net != null) _net.SendSure(new[] { MsgSurrender });
        }

        // ---------------------------------------------------------------- sudden death (ours, the online tournament): the word, then a small label

        float _sdSeenAt = -1f; bool _netSudden, _netAuraOn;

        void DrawSuddenDeath()
        {
            if (!_onlineMatch || _battle == null || !_battle.Active) return;
            bool on = _onlineHost ? _battle.SuddenDeathOn : _netSudden;
            if (!on) return;
            if (_sdSeenAt < 0f) { _sdSeenAt = Time.time; Snd(0x11); }
            float t = Time.time - _sdSeenAt;
            string word = DW1Remake.Formats.Tr.Text("SUDDEN DEATH", "ours");
            UiKit.Begin();
            var pic = new Rect(0f, 0f, 1415f, 1060f);      // the canvas inside UiKit.Begin
            if (t < 2.5f)
            {
                // the word drops in from twice its size, holds, then fades
                float k = Mathf.Clamp01(t / 0.25f), size = Mathf.Lerp(220f, 110f, k), a = t < 2f ? 1f : 1f - (t - 2f) / 0.5f;
                var r = new Rect(pic.x, pic.y + pic.height * 0.32f, pic.width, pic.height * 0.3f);
                UiKit.Text(new Rect(r.x + 8, r.y + 8, r.width, r.height), word, (int)size, new Color(0f, 0f, 0f, a), TextAnchor.MiddleCenter);
                UiKit.Text(r, word, (int)size, new Color(1f, 0.25f + 0.2f * Mathf.PingPong(t * 4f, 1f), 0.15f, a), TextAnchor.MiddleCenter);
            }
            else
            {
                var r = new Rect(pic.x, pic.y + pic.height * 0.195f, pic.width, 50f);      // just under the counter's plate
                UiKit.Text(new Rect(r.x + 3, r.y + 3, r.width, r.height), word, 40, Color.black, TextAnchor.MiddleCenter);
                UiKit.Text(r, word, 40, new Color(1f, 0.35f, 0.15f), TextAnchor.MiddleCenter);
            }
            UiKit.End();
        }

        void DrawSurrender()
        {
            DrawSuddenDeath();
            if (!_surrenderAsk || !_onlineMatch) return;
            UiKit.Begin();
            var r = new Rect(457, 420, 500, 190);
            UiKit.Box(r, UiKit.DialogFill);
            UiKit.Text(new Rect(r.x, r.y + 22, r.width, 60), DW1Remake.Formats.Tr.Text("Surrender?", "ours"), 48, Color.white, TextAnchor.MiddleCenter);
            string yes = DW1Remake.Formats.Tr.Text("Yes", "ours"), no = DW1Remake.Formats.Tr.Text("No", "ours");
            UiKit.Text(new Rect(r.x, r.y + 105, r.width / 2f, 60), (_surrenderSel == 0 ? "> " : "") + yes, 46, _surrenderSel == 0 ? UiKit.Yellow : Color.white, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(r.x + r.width / 2f, r.y + 105, r.width / 2f, 60), (_surrenderSel == 1 ? "> " : "") + no, 46, _surrenderSel == 1 ? UiKit.Yellow : Color.white, TextAnchor.MiddleCenter);
            UiKit.End();
        }

        // side 0 = the host's partner, 1 = the friend's partner
        Battle.Fighter NetSide(int s)
        {
            if (_battle == null) return null;
            if (_onlineHost) return s == 0 ? _battle.Partner : _battle.Enemy;
            return s == 0 ? _battle.Enemy : _battle.Partner;
        }

        int SideOf(Battle.Fighter f) { return f == _battle.Partner ? 0 : 1; }

        // places travel in the arena's game units; the friend's world is the host's turned half round the arena's middle (it is its own inverse)
        Vector3 NetOut(Vector3 unity) { return ArenaStage.ToGame(unity); }
        Vector3 NetIn(Vector3 game) { return ArenaStage.ToUnity(_onlineHost ? game : new Vector3(-game.x, game.y, -game.z)); }

        byte[] StateMessage()
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgState);
            w.Write((ushort)Mathf.Clamp(_battle.Timer, 0, 65535));
            w.Write((byte)((_battle.TimeUpWord ? 1 : 0) | (_battle.TimeUpBox ? 2 : 0) | (_battle.SuddenDeathOn ? 4 : 0)));
            // the finishing move's aura, if one shows (side, 255 = none): the copy starts its own when it has not (MsgAura can come too late)
            var aura = _battle.AuraFighter;
            w.Write((byte)(aura == null ? 255 : SideOf(aura)));
            w.Write((ushort)Mathf.Clamp(_battle.TourDamage[0], 0, 65535)); w.Write((ushort)Mathf.Clamp(_battle.TourDamage[1], 0, 65535));
            for (int s = 0; s < 2; s++)
            {
                var f = NetSide(s);
                Vector3 g = NetOut(f.T.position);
                w.Write(g.x); w.Write(g.y); w.Write(g.z);
                w.Write(f.T.rotation.eulerAngles.y);
                w.Write((byte)(f.A != null ? f.A.Slot : 0)); w.Write((byte)(f.A != null && f.A.PlaysOnce ? 1 : 0));
                w.Write((ushort)Mathf.Clamp(f.Hp, 0, 65535)); w.Write((ushort)Mathf.Clamp(f.Mp, 0, 65535)); w.Write((ushort)Mathf.Clamp(f.Finish, 0, 65535));
                w.Write((ushort)Mathf.Clamp(f.FinishMax, 0, 65535));
                // the conditions (poison, confusion, stun, flat), their effect and the flat timer / picture
                w.Write((byte)((f.Poisoned ? 1 : 0) | (f.Confused ? 2 : 0) | (f.Stunned ? 4 : 0) | (f.Flattened ? 8 : 0))); w.Write((byte)f.FxKind);
                w.Write((ushort)Mathf.Clamp(f.StunFrames, 0, 65535)); w.Write((short)Mathf.Clamp(f.FlatTimer, -1, 32767)); w.Write((sbyte)Mathf.Clamp(f.FlatSprite, -1, 127));
            }
            return ms.ToArray();
        }

        void ReadState(byte[] m)
        {
            if (_battle == null || !_battle.Active || !_battle.Puppet) return;
            var r = new BinaryReader(new MemoryStream(m));
            try
            {
                r.ReadByte();
                _battle.Timer = r.ReadUInt16();
                int flags = r.ReadByte();
                _battle.TimeUpWord = (flags & 1) != 0; _battle.TimeUpBox = (flags & 2) != 0; _netSudden = (flags & 4) != 0;
                int auraSide = r.ReadByte();
                if (auraSide < 2 && !_netAuraOn) _battle.PuppetAura(NetSide(auraSide));
                _netAuraOn = auraSide < 2;
                int d0 = r.ReadUInt16(), d1 = r.ReadUInt16();
                _battle.TourDamage[0] = d1; _battle.TourDamage[1] = d0;          // [0] = what this PC's partner lost
                for (int s = 0; s < 2; s++)
                {
                    var g = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    float yaw = r.ReadSingle();
                    int slot = r.ReadByte(); bool once = r.ReadByte() != 0;
                    int hp = r.ReadUInt16(), mp = r.ReadUInt16(), fin = r.ReadUInt16(), finMax = r.ReadUInt16();
                    int cflags = r.ReadByte(), cfx = r.ReadByte(), stun = r.ReadUInt16(), flatT = r.ReadInt16(), flatS = r.ReadSByte();
                    var f = NetSide(s);
                    if (f == null) continue;
                    _netPos[s] = NetIn(g);
                    _netRot[s] = Quaternion.Euler(0f, yaw + (_onlineHost ? 0f : 180f), 0f);
                    if (!_netHave[s] && f.T != null) { f.T.position = _netPos[s]; f.T.rotation = _netRot[s]; }
                    _netHave[s] = true;
                    if (f.A != null && f.A.Slot != slot && f.A.HasSlot(slot)) f.A.SetSlot(slot, once);
                    _battle.PuppetFinishMax(f, finMax);
                    _battle.PuppetNumbers(f, hp, mp, fin);
                    _battle.PuppetConditions(f, cflags, cfx, stun, flatT, flatS);
                }
            }
            catch (EndOfStreamException) { }
        }

        byte[] PopMessage(Vector3 pos, string text, Color col, int icon)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgPop);
            Vector3 g = NetOut(pos);
            w.Write(g.x); w.Write(g.y); w.Write(g.z);
            w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(col.r * 255f), 0, 255)); w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(col.g * 255f), 0, 255));
            w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(col.b * 255f), 0, 255)); w.Write((byte)icon);
            var t = Encoding.UTF8.GetBytes(text ?? ""); w.Write(t);
            return ms.ToArray();
        }

        void ReadPop(byte[] m)
        {
            if (m.Length < 17) return;
            var r = new BinaryReader(new MemoryStream(m));
            r.ReadByte();
            var g = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var col = new Color(r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f, 1f);
            int icon = r.ReadByte();
            string text = Encoding.UTF8.GetString(m, 17, m.Length - 17);
            _battle.PuppetPop(text, NetIn(g), col, icon);
        }
    }
}
