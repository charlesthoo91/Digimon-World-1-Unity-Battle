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
        int[] _babies;
        EggBoxView _eggBox;
        // REINCARNATE_BABY_TYPE (exe 0x80134BAC, 4 shorts): the Digimon each egg hatches into
        int[] Babies { get { if (_babies == null) { byte[] exe = _disc.ReadFile("/SLUS_010.32"); uint load = System.BitConverter.ToUInt32(exe, 0x18); _babies = new int[4]; for (int i = 0; i < 4; i++) _babies[i] = System.BitConverter.ToInt16(exe, (int)(0x80134BAC - load + 0x800) + 2 * i); } return _babies; } }
        bool _eggOpen; int _eggSlot;
        float _bubbleStart; int _bubbleType = -1, _lastBubble = -1, _emotionLeft = 1;
        int _lifeShow = -1; float _lifeShowAt; Texture2D _lifeTex; FrameBox _lifeBox;
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
            _runnerSection = -1;        // DG.SCN entry 0, never a map's setup section
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
            // the boy's own scenes (pickup, chest): the partner stops where it is rather than walking on the spot
            if (frozen && _partnerAnim != null && (_partnerAnim.Slot == WalkSlot || _partnerAnim.Slot == 3 || _partnerAnim.Slot == RunSlot)) _partnerAnim.SetSlot(IdleSlot);
            StartCoroutine(Finish(scene));
        }

        IEnumerator Finish(IEnumerator scene)
        {
            yield return scene;
            _pBusy = false; _pFrozen = false; _syncBit = true; _camOn = false;
            // steps the animations made while nobody moved the bodies must not all land at once on the next frame (that was the jump after picking up an item)
            if (_anim != null) _anim.TakeForward();
            if (_partnerAnim != null) _partnerAnim.TakeForward();
            if (_partner != null && _player != null) LogGap("scene ended");
        }

        // [care] how far the partner stands from the boy (centre to centre) against the game's own distances, to check the gap after Praise, Scold and feeding
        void LogGap(string when)
        {
            Vector3 d = _player.position - _partner.position; d.y = 0f;
            float r = (_menus.PartnerInfo != null ? _menus.PartnerInfo.Radius : 120) * Unit;
            Debug.Log("[care] " + when + ": partner " + d.magnitude.ToString("0.00") + " m from the boy (game: stands inside " + (r * 2.5f).ToString("0.00") + " m, walks beyond, eats from " + ((DigimonStatsCatalog.TakeDistanceOf(_disc, PartnerType) * 200 / 10 + 160) * Unit).ToString("0.00") + " m)");
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
            // partnerTickPraiseScold: it walks until getPartnerTamerCloseness() == 2, i.e. closer than radius x 5 / 2
            float radius = _menus.PartnerInfo != null ? _menus.PartnerInfo.Radius : 120;
            yield return ApproachTo(radius * 2.5f * Unit);
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
                if (_audio != null) _audio.Play(0, 14);
                if (_partnerAnim != null) _partnerAnim.SetSlot(11, true);
                yield return Play(_anim, small ? 0x26 : 0x27);
                _care.Praise();
            }
            else
            {
                if (_audio != null) _audio.Play(0, 13);
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

        // partnerTickFeedItem / checkEatDistance: the eating distance is ITEM_TAKE_DISTANCE x 200 / 10 + 160 units (Agumon 3.6 m).
        // Farther: it walks towards the boy until it is inside (sub-state 2). Already inside: it turns round (facing + 2048) and walks
        // straight on with its walk animation until it is outside (sub-state 1); then it turns to the boy and eats (sub-state 3).
        IEnumerator ApproachForFood()
        {
            float stop = (DigimonStatsCatalog.TakeDistanceOf(_disc, PartnerType) * 200 / 10 + 160) * Unit;
            Vector3 d = _player.position - _partner.position; d.y = 0f;
            if (d.magnitude > stop) { yield return ApproachTo(stop); yield break; }
            Vector3 away = _partner.forward; away.y = 0f;          // the model's front is -Z: + forward is straight behind it
            if (away.sqrMagnitude < 0.0001f) away = -d;
            away.Normalize();
            _partner.rotation = FaceRot(away);
            if (_partnerAnim != null) _partnerAnim.SetSlot(_partnerAnim.HasSlot(WalkSlot) ? WalkSlot : IdleSlot);
            for (float limit = 8f; limit > 0f; limit -= Time.deltaTime)
            {
                Face(_player, _partner.position);
                d = _player.position - _partner.position; d.y = 0f;
                if (d.magnitude > stop) break;
                float step = _partnerAnim != null ? _partnerAnim.TakeForward() : WalkSpeed * Time.deltaTime;
                Vector3 next = _partner.position + away * step;
                if (!Free(next)) break;                 // a wall: it eats from where it is
                _partner.position = next;
                yield return null;
            }
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        IEnumerator ApproachTo(float stop)
        {
            Vector3 d = _player.position - _partner.position; d.y = 0f;
            if (d.magnitude <= stop) yield break;
            if (_partnerAnim != null) _partnerAnim.SetSlot(_partnerAnim.HasSlot(WalkSlot) ? WalkSlot : IdleSlot);
            for (float limit = 8f; limit > 0f; limit -= Time.deltaTime)
            {
                d = _player.position - _partner.position; d.y = 0f;
                if (d.magnitude <= stop) break;
                Face(_partner, _player.position);
                float step = _partnerAnim != null ? _partnerAnim.TakeForward() : WalkSpeed * Time.deltaTime;
                _partner.position += d.normalized * Mathf.Min(step, d.magnitude - stop + 0.01f);
                yield return null;
            }
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        int _heldItem = -1;            // TAMER_ITEM: the item drawn in the boy's hand (renderFeedingItem, bone 9) while he holds it out
        HealingParticles _healFx;
        byte[] _healVariant;           // HEAL_EFFECT_VARIANT (exe 0x80134364)

        // partnerTickFeedItem: sub-state 3 the boy holds it out (animation 5, sound 12, the item in his hand); from his frame 8 it eats (animation 8: the item
        // leaves his hand at its frame 11, the boy stands again at 15) or refuses (13, then the boy's 4); when the eating ends the item's function runs
        IEnumerator Feed(int item)
        {
            yield return ApproachForFood();
            Face(_partner, _player.position);
            Face(_player, _partner.position);
            if (_audio != null) _audio.Play(0, 12);
            if (_anim != null) _anim.SetSlot(5, true);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            _heldItem = item;
            yield return new WaitForSeconds(8f / 20f);       // the boy's animation reaches frame 8
            if (_care.WillRefuse(item))
            {
                yield return Play(_partnerAnim, 13);
                _heldItem = -1;
                if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
                yield return Play(_anim, 4);
                if (_anim != null) _anim.SetSlot(IdleSlot);
                yield break;
            }
            if (_partnerAnim != null && _partnerAnim.HasSlot(8)) _partnerAnim.SetSlot(8, true);
            yield return new WaitForSeconds(11f / 20f);
            _heldItem = -1;
            yield return new WaitForSeconds(4f / 20f);
            if (_anim != null) _anim.SetSlot(IdleSlot);
            while (_partnerAnim != null && _partnerAnim.Busy) yield return null;
            bool alive = _state.Stats[6] > 0;
            uint cond = (uint)_state.Para.Condition;
            _state.RemoveItem(item, 1);
            bool cheer = _care.UseItem(item);
            HealEffect(item, alive, cond);
            // sub-state 6: tamerSetState(0) at once; partnerHandleFoodFeed's cheer plays on, and the boy walking off cuts it short (the partner follows)
            if (_partnerAnim != null)
            {
                int slot = cheer && _partnerAnim.HasSlot(11) ? 11 : IdleSlot;
                _partnerAnim.SetSlot(slot, slot == 11);
                _partnerStepEnd = Time.time + _partnerAnim.SecondsOf(slot);     // the follow code lets it finish unless the boy goes off
            }
        }

        // ITEM_FUNCTIONS: the items that end in addHealingParticleEffect, and its variant. HP 0-3 / MP 4-6 (HEAL_EFFECT_VARIANT), Double Floppy 7 and 9
        // (handleDoubleFloppy, 0), Restore 0x0B / 0x0C (handleRestore, 1, even at HP 0), 0x0D / 0x0E (handleStatusItems: when handleMedicineHealing healed, 0).
        // The others only when the partner still has HP. (handleMedicineHealing's own test: ours, the partner was sick or injured.)
        void HealEffect(int item, bool alive, uint condBefore)
        {
            if (_partner == null || _disc == null) return;
            if (_healVariant == null)
            {
                byte[] exe = _disc.ReadFile("/SLUS_010.32");
                uint load = System.BitConverter.ToUInt32(exe, 0x18);
                _healVariant = new byte[4];
                System.Array.Copy(exe, (int)(0x80134364 - load + 0x800), _healVariant, 0, 4);
            }
            int variant = -1;
            if (item <= 3 && alive) variant = _healVariant[item];
            else if (item >= 4 && item <= 6 && alive) variant = _healVariant[item - 4];
            else if ((item == 7 || item == 9) && alive) variant = 0;
            else if (item == 0x0B || item == 0x0C) variant = 1;
            else if ((item == 0x0D || item == 0x0E) && alive && (condBefore & (uint)(PartnerCare.Sick | PartnerCare.Injured)) != 0) variant = 0;
            if (variant < 0) return;
            if (_healFx == null) _healFx = new HealingParticles(_disc);
            var info = _menus != null ? _menus.PartnerInfo : null;
            _healFx.Add(ToGame(_partner.position), info != null ? info.Height : 100, info != null ? info.Radius : 120, variant != 0);
        }

        // ---------------------------------------------------------------- sleep
        IEnumerator SleepScene()
        {
            yield return ComeToBoy();
            if (_audio != null) _audio.Play(0, 15);
            yield return Play(_anim, 8);
            yield return Play(_partnerAnim, 11);
            yield return Play(_partnerAnim, 9);
            for (float t = 0; t < 2f; t += Time.deltaTime) { _fade = t / 2f; yield return null; }    // fadeToBlack(40)
            _fade = 1f;
            bool pile = _care.Sleep(_state.Bag[0x22] > 0);
            if (pile) MakePile();
            _mainState = 1;                 // partnerTickSleep: MAIN_STATE = 1 when the sleep menu opens (with a card)
            yield return SleepMenu();       // partnerTickSleep state 5: sleepRegen, handleSleeping, then main menu 0x28 under the black screen
            if (_anim != null) _anim.SetSlot(IdleSlot);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            for (float t = 2f; t > 0f; t -= Time.deltaTime) { _fade = t / 2f; yield return null; }
            _fade = 0f;
            _care.SpecialEvolution(2);
        }

        // ---------------------------------------------------------------- piles on the ground
        // renderPoop: the model ETCNA/UNTI.TMD, its texture at VRAM (320,192) = SYSTEM_W.TIM with colour sets 13 and 14 by face (CLUT y 501, 502);
        // each pile in the middle of its tile, scaled size / 10, no rotation, at the partner's current height. It stays until eaten (Numemon, Sukamon).
        Mesh _pileMesh; Material[] _pileMat, _pileBlend; bool _pileTried;
        readonly List<Transform> _pileObjs = new List<Transform>(); string _pileKey;

        void SyncPiles()
        {
            if (_partner == null) return;
            var key = new System.Text.StringBuilder().Append(_mapId);
            foreach (var q in _state.Para.Piles) if (q.Size != 0 && q.Map == _mapId) key.Append(',').Append(q.X).Append(' ').Append(q.Y).Append(' ').Append(q.Size);
            string k = key.ToString();
            if (k != _pileKey)
            {
                _pileKey = k;
                foreach (var o in _pileObjs) if (o != null) Destroy(o.gameObject);
                _pileObjs.Clear();
                if (!_pileTried) { _pileTried = true; BuildPileModel(); }
                if (_pileMesh != null)
                    foreach (var q in _state.Para.Piles)
                    {
                        if (q.Size == 0 || q.Map != _mapId) continue;
                        var go = new GameObject("Pile");
                        go.transform.SetParent(transform, false);
                        go.AddComponent<MeshFilter>().sharedMesh = _pileMesh;
                        go.AddComponent<MeshRenderer>().sharedMaterials = _pileMat;
                        go.transform.position = TileMiddle(new Vector2Int(q.X, q.Y));
                        go.transform.localScale = Vector3.one * (q.Size / 10f);
                        if (_pileBlend != null)
                        {
                            var b = new GameObject("PileBlend");
                            b.transform.SetParent(go.transform, false);
                            b.AddComponent<MeshFilter>().sharedMesh = _pileMesh;
                            b.AddComponent<MeshRenderer>().sharedMaterials = _pileBlend;
                        }
                        _pileObjs.Add(go.transform);
                    }
            }
            foreach (var o in _pileObjs) { var p = o.position; p.y = _partner.position.y; o.position = p; }
        }

        void BuildPileModel()
        {
            try
            {
                byte[] tmd = _disc.ReadFile("/ETCNA/UNTI.TMD"), buf = new byte[tmd.Length + 4];
                buf[0] = 4; System.Array.Copy(tmd, 0, buf, 4, tmd.Length);
                var model = MmdModel.Parse(buf);
                TimImage tim;
                if (model.Parts.Count == 0 || !TimImage.TryParse(_disc.ReadFile("/ETCDAT/SYSTEM_W.TIM"), 0, out tim)) return;
                // each face names its own colour set: the top uses CLUT y 502 (set 14), the sides y 501 (set 13); one sub-mesh per set
                var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var rows = new List<int>(); var abrs = new List<int>(); var tris = new List<List<int>>();
                foreach (var part in model.Parts)
                    foreach (var f in part.Faces)
                    {
                        int b = verts.Count, k = rows.IndexOf(f.Row);
                        if (k < 0) { k = rows.Count; rows.Add(f.Row); abrs.Add(f.Abr); tris.Add(new List<int>()); }
                        for (int i = 0; i < 3; i++)
                        {
                            int v = f.V[i] * 3;
                            verts.Add(new Vector3(part.Verts[v], -part.Verts[v + 1], part.Verts[v + 2]) * MmdModel.Scale);
                            uvs.Add(new Vector2((f.U[i] + 0.5f) / tim.Width, 1f - (f.T[i] - 128 + 0.5f) / tim.Height));
                        }
                        tris[k].Add(b); tris[k].Add(b + 2); tris[k].Add(b + 1);
                    }
                _pileMesh = new Mesh { name = "UNTI", subMeshCount = rows.Count };
                _pileMesh.SetVertices(verts); _pileMesh.SetUVs(0, uvs);
                for (int k = 0; k < rows.Count; k++) _pileMesh.SetTriangles(tris[k], k);
                _pileMesh.RecalculateNormals(); _pileMesh.RecalculateBounds();
                _pileMat = new Material[rows.Count];
                // every face is semi-transparent with blend mode 2 (back - front): only colours with the STP bit subtract, the rest are solid; the STP black round the pile vanishes
                bool semi = abrs.Exists(a => a >= 0);
                if (semi) _pileBlend = new Material[rows.Count];
                for (int k = 0; k < rows.Count; k++)
                {
                    _pileMat[k] = MmdModel.MakeMaterial(abrs[k] >= 0 ? tim.ToTextureLayer(rows[k], 1) : tim.ToTexture(rows[k]));
                    if (semi) _pileBlend[k] = MmdModel.MakeBlendMaterial(tim.ToTextureLayer(rows[k], 2), abrs[k] >= 0 ? abrs[k] : 0);
                }
            }
            catch (System.Exception e) { Debug.LogWarning("Pile model not available: " + e.Message); }
        }

        // ---------------------------------------------------------------- toilet
        void MakePile()
        {
            Vector2Int tile = TileOf(_partner.position);
            // the game's facing angle is the model's yaw (as EffectPlayer.FacingOf): createPoopPile then moves the pile one tile behind the partner
            float yaw = Mathf.Repeat(_partner.eulerAngles.y - FacingOffset, 360f);
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
                case 1: Run(Curling(), true); break;                    // KAR_start, tamer state 12 (KAR_tick), partner state 11 (PlayableRoomCurling)
                case 2: Run(EvolveScene(_state.Vars[5]), true); break;
                case 10: Run(EvolveScene(_state.Vars[0xFE]), true); break;
                // callDigimonRoutine: 3 is partner state 8 (it comes to the boy first); 7 and 9 set partner state 12, which does nothing, and only
                // stop a walk or run (animation 2-4 -> 0): a partner that fell in the fight stays down (user's PS1 clip 2026-11-05)
                case 3: Run(LifeLost(true), true); break;
                case 7: case 9: Run(LifeLost(false), true); break;
                case 4: Run(Rebirth(), true); break;
                case 6: Run(EndingScene(false), true); break;           // tamerTickEnding (ENDI)
                case 8: Run(MachinedramonSpawn(), true); break;         // tamerTickMachinedramonSpawn (EAB)
                default: _syncBit = true; break;     // training and the ending
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
                if (_objRoot != null) _hidden.Add(_objRoot);                                  // doors and the boulder: drawn only while the map layer is on
                foreach (var c in _chests) if (c.Box != null) _hidden.Add(c.Box.gameObject);
                _hidden.Add(_player.gameObject);
                foreach (var n in _npcs) if (n.T != null) _hidden.Add(n.T.gameObject);
                foreach (var o in _pileObjs) if (o != null) _hidden.Add(o.gameObject);       // renderPoop draws only while the map layer is on
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

        // ---------------------------------------------------------------- a life lost, death, rebirth
        void LoseFight()
        {
            _care.ItemLoss();
            _state.Life = Mathf.Max(0, _state.Life - 1);
            if (_state.Life == 0) _state.Para.Lifetime = 0;
            _battleWait = false; _runner = null;
            StartDgSection(SickSection);
        }

        // MURD_tick / MURD_tickScene (MURD_REL): frames 0-35 an iris closes on the partner (once the partner is bigger than the hole it is
        // drawn over the black); at 35 everything but the partner goes (map layer off, the others hidden, music off) and the lives box opens;
        // at 95 a full-screen quad subtracted from the picture rises (0 to 0xA0 by 135), the lives box closes and the screen holds black while the
        // death map loads (map 0xCD once flag 0xDC or 0xD6 is on or variable 1 >= 50, else 0xDA, exit 1); 145-185 the map comes up from black. With no lives left the game goes
        // on to DOOA (not ported: we go to the same map and its no-lives section).
        IEnumerator LifeLost(bool comesToBoy)
        {
            var p = _state.Para;
            bool natural = p.IsNaturalDeath;
            if (comesToBoy)
            {
                if (!Close(3.5f)) yield return ComeToBoy();
                if (_partnerAnim != null && _partnerAnim.HasSlot(1)) _partnerAnim.SetSlot(1);
            }
            else
            {
                if (_anim != null && (_anim.Slot == 2 || _anim.Slot == 3)) _anim.SetSlot(0);
                if (_partnerAnim != null && _partnerAnim.Slot > 1 && _partnerAnim.Slot < 5) _partnerAnim.SetSlot(0);
            }
            bool late = _state.GetFlag(0xDC) || _state.GetFlag(0xD6) || _state.Vars[1] >= 50;
            int section = natural ? 71 : _state.Life == 0 ? 73 : 72, lives = _state.Life;
            var info = DigimonStatsCatalog.Get(_disc, PartnerType);
            float start = Time.time;
            System.Func<int> timer = () => (int)((Time.time - start) * 20f);
            if (natural)
            {
                // routine 3 (partnerTickDying): an old-age death is not MURD. Once beside the boy DOOA_tick starts with lives left: frame 0,
                // animation 1, its own iris (DOOA_hasIrisClosed / DOOA_renderIrisWindow: closed by 0x21), and at 0x23 straight on into the dissolve
                // (death map, the partner turns into shards and is gone); the death map's section 71 (the egg) runs after it
                _irisOn = true;
                while (timer() < 0x23) { IrisStep(info, timer(), 0x21); yield return null; }
                _irisOn = false; IrisFront(false);
                yield return DeathScene(section, late);
                yield break;
            }
            // phase 0: the iris
            _irisOn = true;
            while (timer() < 0x23) { IrisStep(info, timer()); yield return null; }
            _irisOn = false; IrisFront(false);
            // phase 1
            HideWorld(true);
            if (_partner != null) _partner.gameObject.SetActive(true);
            if (_audio != null) _audio.StopMusic();
            _lifeShow = lives; _lifeShowAt = Time.time;
            // MURD_createLivesBox: UI box 3 (colour 0, features 2) out of a 16 x 12 square on the partner's feet (worldPosToScreenPos)
            if (_lifeBox == null) { _lifeBox = FrameBox.Load(_disc, Battle.StatFont, 3); if (_lifeBox != null) _lifeBox.Sound = Snd; }
            if (_lifeBox != null) _lifeBox.OpenBox(new RectInt(-95, -50, 190, 44), PartnerFeetSquare(), 0, 2);
            while (timer() < 0x5F) yield return null;
            if (lives == 0)
            {
                // MURD_animateLivesBoxOut (it shrinks back into the partner), then DOOA_tick: the death scene goes on from frame 35 (PlayableRoomDeath)
                if (_lifeBox != null) _lifeBox.ShrinkTo(PartnerFeetSquare());
                _lifeShow = -1;
                yield return DeathScene(section, late);
                yield break;
            }
            // phase 3: MURD_renderFullscreenFade, 0 to 0xA0. Its page is blend 2, so it is taken off the picture: the lives box and the partner
            // sink into the black (user's PS1 clip 2026-11-05); ours drew it as white added
            while (timer() < 0x87) { _murdFade = PsLerp(0, 0xA0, 0x5F, 0x87, timer()); yield return null; }
            _lifeShow = -1;                 // MURD_removeLivesBox: removeStaticUIBox, gone at once
            if (_lifeBox != null) _lifeBox.Remove();
            _murdFade = 0xFF;               // phases 4-6: 0xFF, black
            HideWorld(false);
            _runner = null; _line = null; _syncBit = true;
            DoChangeMap(new ScriptAction { Map = late ? 0xCD : 0xDA, Exit = 1, Arrive = 255 }, true);
            while (_changing) yield return null;
            if (_state.Stats[6] == 0) { _state.Stats[6] = (ushort)Mathf.Max(1, _state.Stats[4] / 5); _state.Stats[7] = (ushort)(_state.Stats[5] / 5); }
            while (timer() < 0x91) yield return null;
            // phase 7: 0xA0 to 0, the death map comes up out of the black
            while (timer() < 0xB9) { _murdFade = PsLerp(0xA0, 0, 0x91, 0xB9, timer()); yield return null; }
            _murdFade = 0;
            // tamerTickSicknessLostLife / tamerTickBattleLostLife set SOME_SCRIPT_SYNC_BIT (our Finish): section 72, started on arrival (0x7B) and
            // waiting at 4A D5, goes on; it is not started again
        }

        int _murdFade;      // MURD_renderFullscreenFade's colour (0 = none)

        // ---- the iris (MURD_renderIris, MURD_isDigimonLargerThanIris)
        bool _irisOn; float _irisX, _irisY, _irisR; Texture2D _irisTex; bool _irisTried;
        Camera _frontCam; RenderTexture _frontRt; bool _front;
        const int FrontLayer = 30;

        void IrisStep(DigimonStats info, int t, int end = 0x23)
        {
            if (_partner == null || _cam == null) return;
            int h = info != null ? info.Height : 100, r = info != null ? info.Radius : 50;
            // the middle of the partner (half its height up); the hole's half size is lerp(200, 0, 0, 35, t) * 32 * 256 / 4096 pixels
            Vector3 v = _cam.WorldToViewportPoint(_partner.position + Vector3.up * (h / 2f * Unit));
            _irisX = v.x * 320f; _irisY = (1f - v.y) * 240f;
            int scale = PsLerp(0xC8, 0, 0, end, Mathf.Min(t, end)) * 4096 / 128;
            _irisR = (scale << 8) / 4096f;
            // the partner's size on screen: its box diagonal (getDistance of 2r, h, 2r) at its distance
            float size = Mathf.Sqrt(8f * r * r + h * h) * Unit;
            float px = v.z > 0f ? size / (2f * v.z * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * 240f : 0f;
            IrisFront(v.z <= 0f || _irisR <= px);
        }

        // MURD_renderDigimon at depth 0x21, in front of the iris (0x22). Here the partner moves to its own layer, seen by a second camera
        // with the main camera's view, and that picture is drawn over the iris.
        void IrisFront(bool on)
        {
            if (on == _front || _partner == null) return;
            _front = on;
            if (on)
            {
                if (_frontCam == null)
                {
                    _frontCam = new GameObject("Iris front camera").AddComponent<Camera>();
                    _frontCam.clearFlags = CameraClearFlags.SolidColor; _frontCam.backgroundColor = new Color(0, 0, 0, 0);
                    _frontCam.cullingMask = 1 << FrontLayer;
                    _frontRt = new RenderTexture(Mathf.Max(64, Screen.width), Mathf.Max(64, Screen.height), 24, RenderTextureFormat.ARGB32);
                    _frontCam.targetTexture = _frontRt;
                }
                _frontCam.gameObject.SetActive(true);
                if (_cam != null) _cam.cullingMask &= ~(1 << FrontLayer);
                SyncFrontCamera();
            }
            else if (_frontCam != null) _frontCam.gameObject.SetActive(false);
            foreach (var t in _partner.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = on ? FrontLayer : 0;
        }

        void SyncFrontCamera()
        {
            if (!_front || _frontCam == null || _cam == null) return;
            _frontCam.transform.SetPositionAndRotation(_cam.transform.position, _cam.transform.rotation);
            _frontCam.fieldOfView = _cam.fieldOfView; _frontCam.aspect = _cam.aspect;
            _frontCam.nearClipPlane = _cam.nearClipPlane; _frontCam.farClipPlane = _cam.farClipPlane;
            _frontCam.projectionMatrix = _cam.projectionMatrix;
        }

        // The hole's edge: ETCTIM.BIN image 7 (VRAM 832,384); its top-left 64 x 64 is a quarter circle, white outside. It is drawn four times
        // mirrored round the middle with back-minus-front blending, plus four bars out to the screen edges (overlap lerp(4, 1, 0x1860, 0, scale)).
        // GUI cannot subtract: white there means black, so the picture's brightness is used as the strength of the black.
        void DrawIris()
        {
            if (!_irisOn) return;
            if (!_irisTried)
            {
                _irisTried = true;
                var all = TimImage.ParseAll(_disc.ReadFile("/ETCDAT/ETCTIM.BIN"));
                if (all.Count > 7)
                {
                    var src = all[7].ToTexture(0).GetPixels32();
                    int w = all[7].Width, hh = all[7].Height;
                    var px = new Color32[64 * 64];
                    for (int y = 0; y < 64; y++)
                        for (int x = 0; x < 64; x++)
                        {
                            var c = src[(hh - 1 - y) * w + x];
                            px[(63 - y) * 64 + x] = new Color32(0, 0, 0, (byte)((c.r + c.g + c.b) / 3));
                        }
                    _irisTex = new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                    _irisTex.SetPixels32(px); _irisTex.Apply();
                }
            }
            SyncFrontCamera();
            UiKit.Begin();
            float k = 4.42f, cx = _irisX, cy = _irisY, rr = _irisR;
            float th = PsLerp(4, 1, 0x1860, 0, (int)(rr * 16f));
            float left = cx - rr, right = cx + rr, top = cy - rr, bottom = cy + rr;
            UiKit.Fill(new Rect(0, 0, Mathf.Max(0, left + th) * k, 240 * k), Color.black);
            UiKit.Fill(new Rect((right - th) * k, 0, Mathf.Max(0, 320 - right + th) * k, 240 * k), Color.black);
            UiKit.Fill(new Rect(left * k, 0, (right - left) * k, Mathf.Max(0, top + th) * k), Color.black);
            UiKit.Fill(new Rect(left * k, (bottom - th) * k, (right - left) * k, Mathf.Max(0, 240 - bottom + th) * k), Color.black);
            if (_irisTex != null && rr > 0f)
            {
                float s = rr * k;
                GUI.DrawTextureWithTexCoords(new Rect(left * k, top * k, s, s), _irisTex, new Rect(0, 0, 1, 1));
                GUI.DrawTextureWithTexCoords(new Rect(cx * k, top * k, s, s), _irisTex, new Rect(1, 0, -1, 1));
                GUI.DrawTextureWithTexCoords(new Rect(left * k, cy * k, s, s), _irisTex, new Rect(0, 1, 1, -1));
                GUI.DrawTextureWithTexCoords(new Rect(cx * k, cy * k, s, s), _irisTex, new Rect(1, 1, -1, -1));
            }
            UiKit.End();
            if (_front && _frontRt != null && Event.current.type == EventType.Repaint) GUI.DrawTexture(UiKit.Picture, _frontRt, ScaleMode.StretchToFill, true);
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

        // CONDITION_SPRITES (main/bubble.c): colour row (CLUT y - 480), u, v, w, h, anchor x, anchor y
        static readonly int[,] CondSprite = {
            { 3, 0x90, 0x80, 16, 16, 8, 0x1A }, { 3, 0xD0, 0x90, 16, 16, 8, 0x1A }, { 2, 0xE0, 0xB0, 16, 16, 8, 0x1A }, { 2, 0x80, 0xB0, 8, 8, 0, 0xD },
            { 2, 0x98, 0x90, 8, 8, 8, 0x13 }, { 2, 0x90, 0x90, 8, 8, 3, 0x1C }, { 1, 0xC0, 0x80, 16, 16, 8, 0x17 }, { 1, 0xC0, 0x90, 16, 16, 8, 0x1A },
            { 3, 0xD0, 0xA0, 16, 16, 8, 0x1A }, { 2, 0x80, 0xB0, 16, 16, 8, 0x1A } };
        static readonly int[] HungryFrames = { 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3 }, PoopyFrames = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 2, 2, 2, 1 },
            InjuredFrames = { 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 0 }, EvolvingFrames = { 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3 },
            TiredU = { 0, 0, 16, 16, 32, 32, 48, 48, 48, 48, 48, 48, 48, 48 }, TiredMy = { 9, 9, 9, 9, 9, 9, 9, 9, 9, 8, 6, 3, 0, -6 };
        // CONDITION_ICON_SLEEPY: {frames, step, icon}; icons 4-8 = Z sprites {3}, {3,4}, {3,4,5}, {4,5}, {5}
        static readonly int[,] SleepyIcons = { { 10, 1, 0 }, { 4, 1, 4 }, { 4, 1, 5 }, { 8, 1, 6 }, { 1, 1, 7 }, { 1, 1, 8 }, { 1, -5, 0 } };
        static readonly int[][] SleepyZ = { null, null, null, null, new[] { 3 }, new[] { 3, 4 }, new[] { 3, 4, 5 }, new[] { 4, 5 }, new[] { 5 } };

        void CondSpr(BattleArt art, int s, int u, int v, int h, int my, float x, float y, float scale)
        {
            int w = CondSprite[s, 3], mx = CondSprite[s, 5];
            art.ConditionPs(CondSprite[s, 0], 1, u, v, w, h, x - mx * scale, y - my * scale, w * scale, h * scale);     // attribute 0x50000000: added
        }
        void CondSpr(BattleArt art, int s, float x, float y, float scale) { CondSpr(art, s, CondSprite[s, 1], CondSprite[s, 2], CondSprite[s, 4], CondSprite[s, 6], x, y, scale); }

        // lerp (effect_common.c), with its wrap-around: lerp(0, -65, 0, 10, t - 10) rises smoothly from 0 to -59
        static int PsLerpWrap(int start, int end, int t0, int t1, int t)
        {
            if (t1 == t0) return 0;
            int range = end - start, r = range * (t - t0) / (t1 - t0);
            if (range >= 0) { r %= range + 1; if (r < 0) r += range; }
            else { r %= range - 1; if (r > 0) r += range; }
            return r + start;
        }

        // tickConditionBubble + renderConditionBubble (bubble.c). The tick before this frame's drawing had frameCount fc:
        // fc < 10 grows to scale 0x6000 and rises 65; then it sways 12 (sin of lerp(0, 512, 0, 20, fc)); fc 50 = the burst
        // (scale 0xF000, 230 up, sprite 10 alone). It stands on translateConditionFXToEntity's point, at height
        // -DIGIMON_DATA.height + 20 + the rise over the partner's feet; renderSprite scales by scale x VIEWPORT_DISTANCE / depth.
        void DrawConditionBubble(BattleArt art, int frames)
        {
            int fc = frames - 1, scale, active;
            int baseHeight = -(_menus.PartnerInfo != null ? _menus.PartnerInfo.Height : 200) + 20;
            if (fc >= 50) { active = baseHeight - 230; scale = 0xF000; }
            else if (fc < 10) { scale = PsLerpWrap(0, 0x6000, 0, 10, fc); active = baseHeight + PsLerpWrap(0, -65, 0, 10, fc - 10); }
            else
            {
                scale = 0x6000;
                int angle = PsLerpWrap(0, 512, 0, 20, fc) & 0x1FF;
                active = baseHeight - 65 + (Mathf.RoundToInt(Mathf.Sin(angle * Mathf.PI * 2f / 512f) * 4096f) * 12 >> 12);
            }
            Vector3 p = ConditionFxPoint(_partner, _partnerAnim, PartnerType);
            p.y = _partner.position.y - active * Unit;
            Vector3 s = _cam.WorldToScreenPoint(p);
            if (s.z <= 0f) return;
            float depth = s.z / Unit;                                    // game units along the view
            if (depth <= 0x200 || depth >= 0x10000) return;
            float k = scale / 4096f * _h / depth;                        // PS1 pixels per sprite pixel (VIEWPORT_DISTANCE = the map's view distance)
            float x = ToPs1X(s.x), y = ToPs1Y(s.y);
            float top = (frames >= 51 ? 8 : 32) * k;                     // it never goes above the top of the picture
            if (y + 120f < top) y = top - 120f;
            if (frames >= 51) { art.ConditionPs(4, 0, 0xD0, 0xB0, 16, 16, x - 8f * k, y - 8f * k, 16f * k, 16f * k); return; }     // CONDITION_SPRITES[10], 50% see-through
            // CONDITION_SPRITES[11], the bubble (50% see-through), added last at the same depth, so under the icon
            art.ConditionPs(4, 0, 0xA0, 0x90, 32, 32, x - 16f * k, y - 31f * k, 32f * k, 32f * k);
            DrawBubbleIcon(art, _bubbleType, fc, x, y, k);
        }

        // renderConditionBubble: each icon animates by the bubble's frame count (20 a second)
        void DrawBubbleIcon(BattleArt art, int type, int f, float x, float y, float scale)
        {
            switch (type)
            {
                case 0: { int k = HungryFrames[f % 19]; CondSpr(art, 0, 0x90 + (k << 4), k != 3 ? 0x80 : 0xB0, 16, 0x1A + ((f % 19 + 1) % 5 == 0 ? 3 : 0), x, y, scale); break; }
                case 1: CondSpr(art, 1, 0xD0 + (PoopyFrames[f % 13] << 4), 0x90, 16, 0x1A, x, y, scale); break;
                case 2: CondSpr(art, 7, 0xC0, 0x90 + (((f / 20) & 1) << 4), 16, 0x1A, x, y, scale); break;
                case 3:
                {
                    int ptr = 0, f2 = 0;
                    for (int i = 0; i < f; i++) { if (f2 >= SleepyIcons[ptr, 0]) { f2 = 0; ptr += SleepyIcons[ptr, 1]; } f2++; }
                    var z = SleepyZ[SleepyIcons[ptr, 2]];
                    if (z != null) foreach (int s in z) CondSpr(art, s, x, y, scale);
                    break;
                }
                case 4: CondSpr(art, 6, 0xC0 + TiredU[f % 13], 0x80, 16, 0x17 + TiredMy[f % 13], x, y, scale); break;
                case 5: CondSpr(art, 2, 0xE0 + (((f / 15) & 1) << 4), 0xB0, 16, 0x1A, x, y, scale); break;
                case 6: CondSpr(art, 8, 0xD0 + (InjuredFrames[f % 20] << 4), 0xA0, 16, 0x1A, x, y, scale); break;
                case 7: { int k = EvolvingFrames[f % 19]; if (k == 0) CondSpr(art, 9, 0x80, 0xB8, 8, 0x12, x, y, scale); else CondSpr(art, 9, 0x80 + (k << 4), 0xB0, 16, 0x1A, x, y, scale); break; }
            }
        }

        // renderStatusBars (called with the clock): bottom right, happiness and discipline. It slides 50 a frame out to x 240 while game time stands
        // still and back to 75 when it runs. Texture page 5 = SYSTEM_W.TIM; CLUT (96,500) set 12, (96,503) set 15, (96,498) set 10.
        int _statusX = 75; float _statusAcc;
        void DrawStatusBars()
        {
            var art = _battle != null ? _battle.Art : null;
            if (art == null || _partner == null || _setup != null && _setup.Active || Event.current.type != EventType.Repaint || _arenaOn || _onlineOn) return;      // (not under the online battle screen, ours)
            _statusAcc += Time.deltaTime;
            while (_statusAcc >= 0.05f) { _statusAcc -= 0.05f; _statusX = Mathf.Clamp(_statusX + (GameTimeRuns ? -50 : 50), 75, 240); }
            int x = _statusX, happy = _state.Happiness, disc = _state.Discipline;
            UiKit.Begin();
            GUI.BeginGroup(new Rect(0f, 0f, UiKit.W, UiKit.H));      // clipped to the PlayStation picture, so it can slide out of sight on a wide window
            art.Sys(12, 0, 236, 75, 8, x, 85);
            art.Sys(12, 0, 236, 75, 8, x, 97);
            int hu = happy >= 0 ? 64 : 97;
            if ((int)(Time.time * 20f) % 10 < 5)      /* PLAYTIME_FRAMES % 10 < 5: the face flickers when the area suits it or not */ { if (_care.AreaResponse == 2) hu += 11; else if (_care.AreaResponse == 1) hu -= 11; }
            art.Sys(15, hu, 244, 11, 11, x + 3, 83);
            art.Sys(10, disc >= 50 ? 22 : 33, 244, 11, 11, x + 3, 95);
            int hv, hn;
            if (happy >= 0) { hv = 153; hn = happy / 4; for (int i = 0; i < 25; i++) art.Sys(15, 232, 151, 2, 2, x + 18 + i * 2, 88); }
            else { hv = 151; hn = (happy + 100) / 4; }
            for (int i = 0; i < hn; i++) art.Sys(15, 232, hv, 2, 2, x + 18 + i * 2, 88);
            int dv, dn;
            if (disc >= 50) { dv = 153; dn = (disc - 50) / 2; for (int i = 0; i < 25; i++) art.Sys(15, 232, 151, 2, 2, x + 18 + i * 2, 100); }
            else { dv = 151; dn = disc / 2; }
            for (int i = 0; i < dn; i++) art.Sys(15, 232, dv, 4, 2, x + 18 + i * 2, 100);
            GUI.EndGroup();
            UiKit.End();
        }

        static readonly int[] ClockU = { 128, 160, 128, 160, 160, 128, 160, 128 }, ClockV = { 192, 192, 208, 192, 208, 224, 208, 224 };
        static readonly int[] HourX = { 20, 23, 26, 31, 34, 36, 36, 36, 34, 31, 26, 23, 20, 17, 14, 9, 6, 4, 4, 4, 6, 9, 14, 17 };
        static readonly int[] HourY = { 5, 6, 6, 8, 12, 15, 19, 23, 26, 30, 32, 32, 33, 32, 32, 30, 26, 23, 19, 15, 12, 8, 6, 6 };
        int _clockX = -135; float _clockAcc; FrameBox _clockPlate;

        // renderGameClock: the dial, its sky window, the two icons, the hour dot and the minute hand; it slides out 50 a frame while game time stands still (positions are PS1 pixels from the middle)
        void DrawClock()
        {
            var art = _battle != null ? _battle.Art : null;
            if (art == null || _care == null || _partner == null || _setup != null && _setup.Active || _arenaOn || _onlineOn) { if (_clockPlate != null) _clockPlate.Remove(); return; }
            if (Event.current.type != EventType.Repaint) return;
            _clockAcc += Time.deltaTime;
            while (_clockAcc >= 0.05f) { _clockAcc -= 0.05f; _clockX = Mathf.Clamp(_clockX + (GameTimeRuns ? 50 : -50), -220, -135); }
            if (_menus != null && _menus.Active || _battle.Active) { if (_clockPlate != null) _clockPlate.Remove(); return; }
            int hour = _care.Hour, x = _clockX;
            int night = hour >= 6 && hour < 17 ? 0 : 1, frame = (_care.Frame % 16) / 4;
            UiKit.Begin();
            GUI.BeginGroup(new Rect(0f, 0f, UiKit.W, UiKit.H));
            art.Clock(1, 192, 224, 16, 16, x - 2, -90);
            art.Clock(1, 192, 240, 16, 16, x + 34, -90);
            art.Clock(2, ClockU[night * 4 + frame], ClockV[night * 4 + frame], 32, 16, x + 8, -100);
            art.Clock(0, 208, 215, 48, 40, x, -88);
            art.ClockHand(x + 23, -66, _care.Minute * 6f);
            art.Clock(1, 203, 216, 6, 6, x + HourX[hour], HourY[hour] - 88);
            GUI.EndGroup();
            UiKit.End();
            // our digital time under the dial: a UI box (the game's frame) with HH:MM in the game's font; it slides with the dial. The dial is cut off
            // at the picture's edge but the box is not, so it went on showing in the black bar while the dial was out (scripts, fights, scenes):
            // it is shown only while it is inside the picture, i.e. while game time runs and the dial is in
            // ours and not in the original: shown only with the mod Digital clock (title > MODS)
            if (x + 9 < -160 || !ModSettings.DigitalClock) { if (_clockPlate != null) _clockPlate.Remove(); return; }
            if (_clockPlate == null) _clockPlate = FrameBox.Load(_disc, Battle.StatFont, 5);
            if (_clockPlate != null) _clockPlate.ShowText(new RectInt(x + 9, -46, 30, 10), hour.ToString("00") + ":" + _care.Minute.ToString("00"));
        }

        void DrawCareOverlay()
        {
            var art = _battle != null ? _battle.Art : null;
            if (_cam != null && art != null && !(_menus != null && _menus.Active) && !(_battle != null && _battle.Active) && _partner != null && _partner.gameObject.activeInHierarchy)
            {
                UiKit.Begin();
                if (Time.time - _bubbleStart >= 3f)
                {
                    _lastBubble = _bubbleType; _bubbleType = _pBusy ? -1 : PickBubble(); _bubbleStart = Time.time;
                    if (_bubbleType != -1) _lastBubble = _bubbleType;
                }
                int frames = (int)((Time.time - _bubbleStart) * 20f);      // frameCount: game frames since addConditionBubble
                if (_bubbleType >= 0 && frames >= 1 && frames <= 51) DrawConditionBubble(art, frames);
                if ((_state.Para.Condition & PartnerCare.Unhappy) != 0 && _bubbleType < 0)
                {
                    float f = Time.time * 20f, angle = f * 0x200 / 60f / 4096f * Mathf.PI * 2f, rise = f * 0x600 / 60f / 4096f * Mathf.PI * 2f;
                    Vector3 w = _partner.position + new Vector3(Mathf.Sin(angle) * 1.5f, 1f + Mathf.Sin(rise) * 0.5f, Mathf.Cos(angle) * 1.5f);
                    Vector3 s = _cam.WorldToScreenPoint(w);
                    if (s.z > 0f) art.ConditionPs(2, 1, ((int)f >> 1 & 1) * 16, 176, 16, 16, ToPs1X(s.x) - 8f, ToPs1Y(s.y) - 8f, 16f, 16f);     // BUTTERFLY_SPRITE 0x50000000: added
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
            if (_lifeBox != null && _lifeBox.Showing) _lifeBox.Draw(DrawLives);
            DrawSubtractFade(_murdFade);        // ordering-table place 0: over the lives box too
            if (_eggBox != null && _eggBox.Showing) _eggBox.Draw();
        }

        // MURD_renderLivesBox, inside the box (-95, -50, 190, 44): ETCHI/LIFE.TIM, the word at u 48 (64 x 24) at (-85, -40), lives 24 x 24 at
        // x = 34 i - 11: full (u 0) for the lives left, the one just lost full for 10 frames, blinking every 2 frames until 30, then empty (u 24)
        void DrawLives()
        {
            {
                if (_lifeTex == null && _disc.Exists("/ETCHI/LIFE.TIM")) { TimImage lt; if (TimImage.TryParse(_disc.ReadFile("/ETCHI/LIFE.TIM"), 0, out lt)) _lifeTex = lt.ToTexture(0); }
                System.Func<float, float> px = v => 707.5f + v * 4.42f, py = v => 530f + v * 4.42f;
                if (_lifeTex != null)
                {
                    float tw = _lifeTex.width, th = _lifeTex.height;
                    System.Action<float, float, float, float> spr = (u, w, x, y) => GUI.DrawTextureWithTexCoords(new Rect(px(x), py(y), w * 4.42f, 24 * 4.42f), _lifeTex, new Rect(u / tw, 0f, w / tw, 24f / th));
                    spr(48, 64, -85, -40);
                    int frame = (int)((Time.time - _lifeShowAt) * 20f);
                    for (int i = 0; i < 3; i++)
                    {
                        bool full = i < _lifeShow || (i == _lifeShow && (frame < 10 || (frame < 30 && ((frame / 2) & 1) == 0)));
                        spr(full ? 0 : 24, 24, i * 34 - 11, -40);
                    }
                }
            }
        }

        // the 16 x 12 square round the partner's feet on screen (worldPosToScreenPos, minus (8, 6))
        RectInt PartnerFeetSquare()
        {
            if (_partner == null) return new RectInt(-8, -6, 16, 12);
            Vector2 p = Ps1Point(_partner.position);
            return new RectInt((int)p.x - 8, (int)p.y - 6, 16, 12);
        }

        static float ToPs1X(float screenX) { Rect p = UiKit.Picture; return ((screenX - p.x) / p.width - 0.5f) * 320f; }
        static float ToPs1Y(float screenY) { Rect p = UiKit.Picture; return (0.5f - (screenY - p.y) / p.height) * 240f; }
    }
}
