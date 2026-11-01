using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// What the partner does on screen outside fights (the game's partner states): being fed, toilet, accidents, sleep, Praise and Scold, evolving, losing a life, rebirth;
    /// and the bubbles, butterfly and piles. While one of these runs the boy cannot move (tamer state 6). Timing from main/partner.c, evl/evl.c; each of them is one coroutine here.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        bool _pBusy, _pFrozen, _syncBit = true;   // _pBusy: a partner state other than normal; _pFrozen: game time stopped too; _syncBit: SOME_SCRIPT_SYNC_BIT
        const int SickSection = 1246, ToiletSection = 1250;
        static readonly int[] Babies = { 1, 15, 29, 43 };   // REINCARNATE_BABY_TYPE
        bool _eggOpen; int _eggSlot;
        float _bubbleStart; int _bubbleType = -1, _lastBubble = -1, _emotionLeft = 1;
        int _lifeShow = -1;
        float _flash;
        readonly List<Vector4> _sparks = new List<Vector4>();   // x, y, z, time left

        bool PartnerSlow { get { return (_state.Para.Condition & (PartnerCare.Sleepy | PartnerCare.Tired | PartnerCare.Injured | PartnerCare.Sick)) != 0; } }

        // updateConditionAnimation: the pose it takes now and then when it stands still
        int StandSlot()
        {
            int c = _state.Para.Condition, h = _state.Happiness, want;
            if (c == 0) want = h < -30 ? 7 : h >= 31 ? 5 : IdleSlot;
            else if ((c & (PartnerCare.Injured | PartnerCare.Sick)) != 0) want = 14;
            else if ((c & PartnerCare.Poopy) != 0) want = 16;
            else if ((c & PartnerCare.Hungry) != 0) want = 15;
            else if ((c & PartnerCare.Tired) != 0) want = 18;
            else if ((c & PartnerCare.Sleepy) != 0) want = 17;
            else want = 19;
            return _partnerAnim != null && _partnerAnim.HasSlot(want) ? want : IdleSlot;
        }

        int IdleOrSlow { get { return PartnerSlow && _partnerAnim != null && _partnerAnim.HasSlot(1) ? 1 : IdleSlot; } }

        // what the partner picks at the end of a stand loop: after 1 to 5 loops, its mood (EMOTION_ANIM_TIMEOUT)
        int NextStand()
        {
            if (--_emotionLeft > 0) return IdleOrSlow;
            _emotionLeft = Random.Range(1, 6);
            int s = StandSlot();
            return s == IdleSlot ? IdleOrSlow : s;
        }

        void StartDgSection(int section)
        {
            _line = null; _choiceOpts = null;
            _runner = new ScriptRunner(_scripts, _state, MapScripts.DgZero, section, new List<string>());
            AdvanceScript();
        }

        // the requests of the care rules, between game frames, when no script runs
        void CheckCareRequests()
        {
            var c = _care;
            if (c.GotSick) { c.GotSick = false; Say(_menus.PartnerName + " " + DigimonStatsCatalog.ExeText(_disc, 0x801225A0)); }
            if (_pBusy || _changing || _runner != null) return;
            if (c.WantLifeLost) { c.WantLifeLost = false; StartDgSection(SickSection); return; }
            if (c.WantEvolve) { c.WantEvolve = false; if (_state.Para.EvolutionTarget != -1) Run(EvolveScene(_state.Para.EvolutionTarget), true); return; }
            if (c.WantWildPoop) { c.WantWildPoop = false; Run(WildPoop(), false); return; }
            if (PartnerType == PartnerCare.Numemon || PartnerType == PartnerCare.Sukamon)     // tickPoopDetection: it goes for a pile near the boy
            {
                var piles = _state.Para.Piles;
                for (int i = 0; i < piles.Length; i++)
                {
                    if (piles[i].Size == 0 || piles[i].Map != _mapId) continue;
                    float r = (piles[i].Size < 11 ? 200 : 300) * Unit;
                    Vector3 at = TileMiddle(new Vector2Int(piles[i].X, piles[i].Y));
                    if (Mathf.Abs(at.x - _player.position.x) < r && Mathf.Abs(at.z - _player.position.z) < r) { Run(EatPile(i, at), false); return; }
                }
            }
        }

        void Run(IEnumerator scene, bool frozen)
        {
            _pBusy = true; _pFrozen = frozen;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            StartCoroutine(Finish(scene));
        }

        IEnumerator Finish(IEnumerator scene)
        {
            yield return scene;
            _pBusy = false; _pFrozen = false; _syncBit = true; _camOn = false;
        }

        void Face(Transform who, Vector3 at)
        {
            Vector3 d = at - who.position; d.y = 0f;
            if (d.sqrMagnitude > 0.0001f) who.rotation = FaceRot(d);
        }

        bool Close(float radii)
        {
            float r = (_menus.PartnerInfo != null ? _menus.PartnerInfo.Radius : 120) * Unit;
            Vector3 a = _player.position - _partner.position; a.y = 0f;
            return a.magnitude < r * radii;
        }

        IEnumerator Play(DigimonAnimator an, int slot)
        {
            if (an == null || !an.HasSlot(slot)) yield break;
            an.SetSlot(slot, true);
            yield return null;
            while (an != null && an.Busy) yield return null;
        }

        // the walk of tickEntityWalkTo: facing the goal, moved by its own animation
        IEnumerator WalkTo(Vector3 goal, int slot)
        {
            if (_partnerAnim != null) _partnerAnim.SetSlot(_partnerAnim.HasSlot(slot) ? slot : WalkSlot);
            Vector2Int tile = TileOf(goal);
            for (float limit = 8f; limit > 0f && TileOf(_partner.position) != tile; limit -= Time.deltaTime)
            {
                Face(_partner, goal);
                Vector3 d = goal - _partner.position; d.y = 0f;
                float step = _partnerAnim != null ? _partnerAnim.TakeForward() : WalkSpeed * Time.deltaTime;
                _partner.position += d.normalized * Mathf.Min(step, d.magnitude);
                yield return null;
            }
        }

        IEnumerator ComeToBoy()
        {
            Face(_player, _partner.position);
            if (!Close(2.5f)) yield return WalkTo(_player.position, WalkSlot);
            while (!Close(3.5f)) { yield return WalkTo(_player.position, WalkSlot); }
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            Face(_partner, _player.position);
            Face(_player, _partner.position);
        }

        // ---------------------------------------------------------------- menu commands
        void CareCommand(int state)
        {
            if (_pBusy) return;
            if (state == 3) { if ((_state.Para.Condition & PartnerCare.Sleepy) != 0) Run(SleepScene(), true); }
            else Run(PraiseScold(state == 15), false);
        }

        IEnumerator PraiseScold(bool praise)
        {
            yield return ComeToBoy();
            if (praise)
            {
                int level = _menus.PartnerInfo != null ? _menus.PartnerInfo.Level : 3;
                bool small = level < 4 || PartnerType == 0x0D || PartnerType == 0x1B || PartnerType == 0x38;
                if (_partnerAnim != null) _partnerAnim.SetSlot(11, true);
                yield return Play(_anim, small ? 0x26 : 0x27);
                _care.Praise();
            }
            else
            {
                if (_partnerAnim != null) _partnerAnim.SetSlot(_state.Para.ItemScoldFlag == 1 ? 25 : 12, true);
                yield return Play(_anim, 7);
                _care.Scold();
            }
            if (_anim != null) _anim.SetSlot(IdleSlot);
            while (_partnerAnim != null && _partnerAnim.Busy) yield return null;
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            _care.SpecialEvolution(3);
        }

        void FeedItem(int item) { if (!_pBusy) Run(Feed(item), false); }

        IEnumerator Feed(int item)
        {
            if (!Close(1.5f + 2f)) yield return ComeToBoy();
            Face(_partner, _player.position);
            Face(_player, _partner.position);
            if (_anim != null) _anim.SetSlot(5, true);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            yield return new WaitForSeconds(8f / 20f);       // the boy's animation reaches frame 8
            if (_care.WillRefuse(item))
            {
                yield return Play(_partnerAnim, 13);
                if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
                yield return Play(_anim, 4);
                if (_anim != null) _anim.SetSlot(IdleSlot);
                yield break;
            }
            yield return Play(_partnerAnim, 8);
            if (_anim != null) _anim.SetSlot(IdleSlot);
            _state.RemoveItem(item, 1);
            bool cheer = _care.UseItem(item);
            if (cheer) yield return Play(_partnerAnim, 11);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        // ---------------------------------------------------------------- sleep
        IEnumerator SleepScene()
        {
            yield return ComeToBoy();
            yield return Play(_anim, 8);
            yield return Play(_partnerAnim, 11);
            yield return Play(_partnerAnim, 9);
            for (float t = 0; t < 2f; t += Time.deltaTime) { _fade = t / 2f; yield return null; }    // fadeToBlack(40)
            _fade = 1f;
            bool pile = _care.Sleep(_state.Bag[0x22] > 0);
            if (pile) MakePile();
            if (_anim != null) _anim.SetSlot(IdleSlot);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            for (float t = 2f; t > 0f; t -= Time.deltaTime) { _fade = t / 2f; yield return null; }
            _fade = 0f;
            _care.SpecialEvolution(2);
        }

        // ---------------------------------------------------------------- toilet
        void MakePile()
        {
            Vector2Int tile = TileOf(_partner.position);
            float yaw = Mathf.Repeat(_partner.eulerAngles.y - FacingOffset + 180f, 360f);
            _care.CreatePile(_mapId, tile.x, tile.y, (int)(yaw / 360f * 4096f));
        }

        IEnumerator WildPoop()
        {
            if (_partnerAnim != null && _partnerAnim.HasSlot(RunSlot)) _partnerAnim.SetSlot(RunSlot);
            Face(_player, _partner.position);
            if (!Close(3.5f)) yield return WalkTo(_player.position, RunSlot);
            Face(_partner, _player.position);
            yield return Play(_partnerAnim, 10);
            MakePile();
            _care.HandleWildPoop();
            yield return Play(_partnerAnim, 12);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            _care.AddTamerLevel(1, -1);
        }

        IEnumerator ToiletVisit()
        {
            int id = DigimonStatsCatalog.ToiletIdOf(_disc, _mapId);
            if (id <= 0 || id > 10) { _care.HandleToilet(); yield break; }
            int[] t = DigimonStatsCatalog.ToiletOf(_disc, id);
            Vector3 one = new Vector3(t[0] * Unit, _partner.position.y, t[1] * Unit), two = new Vector3(t[2] * Unit, _partner.position.y, t[3] * Unit);
            _camOn = true; _camPos = _player.position; _camGoal = two; _camSpeed = Vector3.Distance(_camPos, two);    // over 20 frames, one second
            yield return WalkTo(one, RunSlot);
            yield return WalkTo(two, RunSlot);
            Face(_partner, one);
            yield return Play(_partnerAnim, 10);
            _care.HandleToilet();
            _camGoal = _player.position; _camSpeed = Vector3.Distance(_camPos, _camGoal);
            yield return WalkTo(one, WalkSlot);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        IEnumerator EatPile(int i, Vector3 at)
        {
            yield return WalkTo(at, WalkSlot);
            yield return Play(_partnerAnim, 8);
            _care.EatPile(i);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        // ---------------------------------------------------------------- 0x49 callDigimonRoutine
        void DigimonRoutine(int n)
        {
            _syncBit = false;
            switch (n)
            {
                case 0: Run(ToiletVisit(), false); break;
                case 2: Run(EvolveScene(_state.Vars[5]), true); break;
                case 10: Run(EvolveScene(_state.Vars[0xFE]), true); break;
                case 3: case 7: case 9: Run(LifeLost(), true); break;
                case 4: Run(Rebirth(), true); break;
                default: _syncBit = true; break;     // curling, training and the ending
            }
        }

        // ---------------------------------------------------------------- evolution (partnerTickEvolving, evl.c). Frames are 20 a second.
        readonly List<GameObject> _hidden = new List<GameObject>();

        void HideWorld(bool hide)
        {
            if (hide)
            {
                _hidden.Clear();
                if (_backdrop != null) _hidden.Add(_backdrop);
                if (_overlayRoot != null) _hidden.Add(_overlayRoot);
                _hidden.Add(_player.gameObject);
                foreach (var n in _npcs) if (n.T != null) _hidden.Add(n.T.gameObject);
                _hidden.RemoveAll(g => g == null || !g.activeSelf);
            }
            foreach (var g in _hidden) if (g != null) g.SetActive(!hide);
            if (_cam != null) { if (hide) { _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = Color.black; } }
        }

        void SwapPartner(int type)
        {
            Vector3 pos = _partner != null ? _partner.position : _player.position;
            Quaternion rot = _partner != null ? _partner.rotation : _player.rotation;
            if (_partner != null) Destroy(_partner.gameObject);
            PartnerType = type;
            BuildPartner();
            _partner.position = pos; _partner.rotation = rot;
            _menus.PartnerInfo = DigimonStatsCatalog.Get(_disc, type);
            _emotionLeft = 1;
        }

        IEnumerator EvolveScene(int target)
        {
            if (!Close(3.5f)) yield return ComeToBoy();
            int newType = DigimonStatsCatalog.GainOf(_disc, target).Target;
            var oldInfo = _menus.PartnerInfo; var newInfo = DigimonStatsCatalog.Get(_disc, newType);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            yield return new WaitForSeconds(55f / 20f);
            if (_partnerAnim != null && _partnerAnim.HasSlot(1)) _partnerAnim.SetSlot(1);
            _bubbleType = 7; _bubbleStart = Time.time;
            yield return new WaitForSeconds(55f / 20f);
            _bubbleType = -1;

            _camOn = true; _camPos = _camGoal = _partner.position;
            var clear = _cam != null ? _cam.clearFlags : CameraClearFlags.Skybox; var back = _cam != null ? _cam.backgroundColor : Color.black;
            float time = 0f, turn = 0f; int frame = 0; Vector3 scale = _partner.localScale; float last = 0f;
            bool swapped = false, started = false;
            while (frame < 0x162)
            {
                frame = (int)time;
                if (frame < 0x20) _fade = frame / 32f;
                else if (!started) { started = true; _fade = 0f; HideWorld(true); }
                if (frame == 0x6A && _partnerAnim != null && _partnerAnim.HasSlot(12) && last < 0x6A) _partnerAnim.SetSlot(12, true);
                if (frame >= 0x6A && frame < 0x11C && _sparks.Count < 60) _sparks.Add(new Vector4(Random.Range(-1f, 1f), Random.Range(0f, 2f), Random.Range(-1f, 1f), 1f));
                if (frame >= 0xA5)     // the game turns its camera round the model at up to 0x16 a frame; the same picture, by turning the model on the black stage
                {
                    float speed = frame >= 0xC3 ? 0x16 : 0x16 * (frame - 0xA4) / 30f;
                    turn += speed * (time - last) * 360f / 4096f;
                    _partner.rotation = FaceRot(_player.position - _partner.position) * Quaternion.Euler(0, turn, 0);
                }
                if (!swapped && frame >= 0xCC && frame < 0x108 && oldInfo != null && newInfo != null)
                {
                    float k = (frame - 0xCC) / 60f;
                    float r = Mathf.Lerp(1f, newInfo.Radius / (float)oldInfo.Radius, k), h = Mathf.Lerp(1f, newInfo.Height / (float)oldInfo.Height, k);
                    _partner.localScale = new Vector3(scale.x * r, scale.y * h, scale.z * r);
                }
                _flash = frame >= 0xD7 && frame < 0x11C ? Mathf.InverseLerp(0xD6, 0x11C, frame) * (Random.value * 0.5f + 0.5f) : 0f;
                if (!swapped && frame >= 0x11C)
                {
                    swapped = true;
                    _care.ApplyEvolution(target);
                    SwapPartner(newType);
                    if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
                }
                last = time;
                yield return null;
                time += Time.deltaTime * 20f;
            }
            _sparks.Clear(); _flash = 0f;
            HideWorld(false);
            if (_cam != null) { _cam.clearFlags = clear; _cam.backgroundColor = back; }
            Face(_partner, _player.position);
            yield return Play(_partnerAnim, 11);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        // ---------------------------------------------------------------- a life lost, death, rebirth
        void LoseFight()
        {
            _care.ItemLoss();
            _state.Life = Mathf.Max(0, _state.Life - 1);
            if (_state.Life == 0) _state.Para.Lifetime = 0;
            _battleWait = false; _runner = null;
            StartDgSection(SickSection);
        }

        IEnumerator LifeLost()
        {
            var p = _state.Para;
            bool natural = p.IsNaturalDeath;
            if (!Close(3.5f)) yield return ComeToBoy();
            if (_partnerAnim != null && _partnerAnim.HasSlot(1)) _partnerAnim.SetSlot(1);
            for (float t = 0; t < 1.75f; t += Time.deltaTime) { _fade = t / 1.75f; yield return null; }    // the iris closes in 35 frames
            _fade = 1f;
            _lifeShow = _state.Life;
            yield return new WaitForSeconds(95f / 20f);
            _lifeShow = -1;
            bool late = _state.GetFlag(0xDC) || _state.GetFlag(0xD6) || _state.Vars[1] >= 50;
            int section = natural ? 71 : _state.Life == 0 ? 73 : 72;
            _runner = null; _line = null; _syncBit = true;
            DoChangeMap(new ScriptAction { Map = late ? 0xCD : 0xDA, Exit = 1, Arrive = section });
            if (_state.Stats[6] == 0) { _state.Stats[6] = (ushort)Mathf.Max(1, _state.Stats[4] / 5); _state.Stats[7] = (ushort)(_state.Stats[5] / 5); }
        }

        IEnumerator Rebirth()
        {
            int slot = _state.Para.IsNaturalDeath ? _eggSlot : Random.Range(0, 4);
            for (float t = 0; t < 1f; t += Time.deltaTime) { _fade = t; yield return null; }
            _care.Reincarnate(Babies[slot]);
            SwapPartner(Babies[slot]);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            yield return new WaitForSeconds(0.5f);
            for (float t = 1f; t > 0f; t -= Time.deltaTime) { _fade = t; yield return null; }
            _fade = 0f;
            yield return Play(_partnerAnim, 11);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        // ---------------------------------------------------------------- on the map
        void CareOnMap(byte[] mapBytes)
        {
            int o = System.BitConverter.ToInt32(mapBytes, 0) + 28 * 4, favoured = DigimonStatsCatalog.RaiseOf(_disc, PartnerType).FavoredRegion, response = 0;
            for (int j = 0; j < 4; j++)
            {
                if ((sbyte)System.BitConverter.ToInt32(mapBytes, o + j * 4) == favoured) { response = 1; break; }
                if ((sbyte)System.BitConverter.ToInt32(mapBytes, o + 16 + j * 4) == favoured) { response = 2; break; }
            }
            _care.OnMap(_mapId, response);
        }

        // handleConditionBubble: every 60 frames the next of its needs shows for 50 frames; a butterfly when only unhappy
        int PickBubble()
        {
            int c = _state.Para.Condition, b = -1;
            if ((c & PartnerCare.Sick) != 0 && _lastBubble != 2) b = 2;
            if ((c & PartnerCare.Injured) != 0 && _lastBubble != 6) b = 6;
            if ((c & PartnerCare.Poopy) != 0 && _lastBubble != 1) b = 1;
            if ((c & PartnerCare.Hungry) != 0 && _lastBubble != 0) b = 0;
            if ((c & PartnerCare.Tired) != 0 && _lastBubble != 4) b = 4;
            if ((c & PartnerCare.Sleepy) != 0 && _lastBubble != 3) b = 3;
            return b;
        }

        // sprite of each bubble type: colour row, u, v, w, h, anchor x, anchor y (CONDITION_SPRITES)
        static readonly int[,] BubbleIcon = { { 3, 144, 128, 16, 16, 8, 26 }, { 3, 208, 144, 16, 16, 8, 26 }, { 1, 192, 144, 16, 16, 8, 26 }, { 2, 144, 144, 8, 8, 3, 28 }, { 1, 192, 128, 16, 16, 8, 23 }, { 0, 0, 0, 0, 0, 0, 0 }, { 3, 208, 160, 16, 16, 8, 26 }, { 2, 128, 176, 16, 16, 8, 26 } };

        void DrawCareOverlay()
        {
            var art = _battle != null ? _battle.Art : null;
            if (_cam != null && art != null && !(_menus != null && _menus.Active) && !(_battle != null && _battle.Active) && _partner != null && _partner.gameObject.activeInHierarchy)
            {
                UiKit.Begin();
                foreach (var q in _state.Para.Piles)
                {
                    if (q.Size == 0 || q.Map != _mapId) continue;
                    Vector3 s = _cam.WorldToScreenPoint(TileMiddle(new Vector2Int(q.X, q.Y)));
                    if (s.z > 0f) art.Condition(3, 208, 144, 16, 16, ToPs1X(s.x) - 8f, ToPs1Y(s.y) - 16f, 16f, 16f);
                }
                if (Time.time - _bubbleStart >= 3f)
                {
                    _lastBubble = _bubbleType; _bubbleType = _pBusy ? -1 : PickBubble(); _bubbleStart = Time.time;
                    if (_bubbleType != -1) _lastBubble = _bubbleType;
                }
                float age = (Time.time - _bubbleStart) * 20f;
                if (_bubbleType >= 0 && age < 50f)
                {
                    float height = (_menus.PartnerInfo != null ? _menus.PartnerInfo.Height : 200) * Unit;
                    float pop = Mathf.Clamp01(age / 10f), bob = age < 10f ? 0f : Mathf.Sin(age * Mathf.PI / 40f) * 0.12f;
                    Vector3 head = _partner.position + Vector3.up * (height - 0.2f + 0.65f * pop + bob);
                    Vector3 a = _cam.WorldToScreenPoint(head), b = _cam.WorldToScreenPoint(head + Vector3.up);
                    float scale = Mathf.Abs(ToPs1Y(a.y) - ToPs1Y(b.y)) * 0.06f * pop;
                    if (a.z > 0f)
                    {
                        float x = ToPs1X(a.x), y = ToPs1Y(a.y);
                        art.Condition(4, 160, 144, 32, 32, x - 16f * scale, y - 31f * scale, 32f * scale, 32f * scale);
                        int t = _bubbleType;
                        art.Condition(BubbleIcon[t, 0], BubbleIcon[t, 1], BubbleIcon[t, 2], BubbleIcon[t, 3], BubbleIcon[t, 4], x - BubbleIcon[t, 5] * scale, y - BubbleIcon[t, 6] * scale, BubbleIcon[t, 3] * scale, BubbleIcon[t, 4] * scale);
                    }
                }
                if ((_state.Para.Condition & PartnerCare.Unhappy) != 0 && _bubbleType < 0)
                {
                    float f = Time.time * 20f, angle = f * 0x200 / 60f / 4096f * Mathf.PI * 2f, rise = f * 0x600 / 60f / 4096f * Mathf.PI * 2f;
                    Vector3 w = _partner.position + new Vector3(Mathf.Sin(angle) * 1.5f, 1f + Mathf.Sin(rise) * 0.5f, Mathf.Cos(angle) * 1.5f);
                    Vector3 s = _cam.WorldToScreenPoint(w);
                    if (s.z > 0f) art.Condition(2, ((int)f >> 1 & 1) * 16, 176, 16, 16, ToPs1X(s.x) - 8f, ToPs1Y(s.y) - 8f, 16f, 16f);
                }
                UiKit.End();
            }

            if (_sparks.Count > 0 && _cam != null && _partner != null)
            {
                for (int i = _sparks.Count - 1; i >= 0; i--)
                {
                    var v = _sparks[i]; v.w -= Time.deltaTime; v.y += Time.deltaTime; _sparks[i] = v;
                    if (v.w <= 0f) { _sparks.RemoveAt(i); continue; }
                    Vector3 s = _cam.WorldToScreenPoint(_partner.position + new Vector3(v.x, v.y, v.z));
                    GUI.color = new Color(1f, 0.6f + 0.4f * v.w, 0.4f, v.w);
                    GUI.DrawTexture(new Rect(s.x - 3f, Screen.height - s.y - 3f, 6f, 6f), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
            }
            if (_flash > 0f)
            {
                GUI.color = new Color(1f, 1f, 1f, _flash * 0.8f);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }
            if (_lifeShow >= 0)
            {
                UiKit.Begin();
                UiKit.Text(new Rect(420, 380, 575, 90), "Life", 60, UiKit.Yellow, TextAnchor.MiddleCenter);
                for (int i = 0; i < 3; i++) UiKit.Fill(new Rect(530 + i * 125, 500, 100, 96), i < _lifeShow ? new Color(0.95f, 0.3f, 0.45f) : UiKit.Grey);
                UiKit.End();
            }
            if (_eggOpen)
            {
                UiKit.Begin();
                UiKit.Box(new Rect(330, 440, 755, 180), UiKit.PanelFill);
                for (int i = 0; i < 4; i++)
                    UiKit.Text(new Rect(350 + i * 180, 480, 170, 90), DigimonStatsCatalog.Get(_disc, Babies[i]).Name, 34, i == _eggSlot ? UiKit.Yellow : Color.white, TextAnchor.MiddleCenter);
                UiKit.End();
            }
        }

        static float ToPs1X(float screenX) { return (screenX / Screen.width - 0.5f) * 320f; }
        static float ToPs1Y(float screenY) { return (0.5f - screenY / Screen.height) * 240f; }
    }
}
