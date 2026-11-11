using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The four conditions a technique can leave (MOVE_DATA status 1 poison, 2 confusion, 3 stun, 4 flat), from battle_main.c and battle_effect.c:
    /// BTL_applyMoveStatus rolls them on a landed hit, BTL_tickStatusEffects runs their timers, and one condition effect at a time is drawn over the
    /// Digimon (statusFxId: stun before confusion before poison). Flat swaps the model for a paper picture of the Digimon (renderFlatDigimon) that
    /// can only fire the flat bullet (technique 0x79, BTL_addAuraProjectile).
    /// </summary>
    public sealed partial class Battle
    {
        public DiscImage GameDisc;                                              // set by the room: BTL_REL.BIN models, ETCTIM.BIN pictures, ALLTIM.TIM flat pictures
        public Func<Transform, DigimonAnimator, int, Vector3> FxPoint;      // translateConditionFXToEntity (the room's ConditionFxPoint)
        public Func<PsxTmd.Lights> MapLights;                               // the map's three flat lights and GsSetAmbient(0x800) (map.c), for the lit confusion model

        const int FlatBulletTech = 0x79;
        const uint ConfusionModel = 0x80054838, StunModel = 0x80054D00, BtlBase = 0x80052AE0, AuraTypes = 0x8007383C;    // BTL_CONFUSION_MODEL, BTL_STUN_MODEL, BTL_AURA_PROJECTILE_TYPES
        const uint StunFxRotation = 0x801347B4, OriginalTypes = 0x8012A84C;  // BTL_STUN_FX_ROTATION, ORIGINAL_DIGIMON_TYPES

        // condition effect objects (BTL_POISON_EFFECTS 4, BTL_POISON_BUBBLES 12, BTL_CONFUSION_EFFECTS 4, BTL_STUN_EFFECTS 5, BTL_STUN_SUB_EFFECTS 25)
        sealed class CondFx { public int Kind; public Fighter Owner; public int Frame, Duration, State, Angle, Spin; public int[] Offsets = new int[3]; public bool Gone; }
        sealed class Bubble { public Fighter Owner; public int Frame, OffX, OffZ; }
        sealed class Spark { public Fighter Owner; public int Frame; }
        sealed class FlatShot { public Fighter Owner; public int Frame, U; public Vector3 Pos, Vel; }
        readonly List<CondFx> _condFx = new List<CondFx>();
        readonly List<Bubble> _bubbles = new List<Bubble>();
        readonly List<Spark> _sparks = new List<Spark>();
        readonly List<FlatShot> _bullets = new List<FlatShot>();
        float _condAcc;
        readonly HashSet<Fighter> _modelHidden = new HashSet<Fighter>();

        bool FlatBusy(Fighter f) { return f.Flattened || f.FlatTimer != 0; }

        // ------------------------------------------------------------------ rolling a condition

        // BTL_applyMoveStatus: after a landed hit, statusChance percent; a condition already there is not started again
        void ApplyMoveStatus(Fighter d, Tech tech)
        {
            if (d.Protected) return;        // FIGHTER_FLAG_PROTECTED (the Protection item)
            if (tech.StatusChance == 0 || _rng.Next(100) >= tech.StatusChance) return;
            switch (tech.Status)
            {
                case 1:
                    if (d.Poisoned) break;
                    d.Poisoned = true; d.PoisonFrames = 100;
                    // BTL_addPoisonStatusVisual
                    if (!d.Confused && !d.Stunned && !d.Flattened && d.FlatTimer == 0) AddCondVisual(d, 1);
                    break;
                case 2:
                    if (d.Confused) break;
                    d.Confused = true; d.ConfuseFrames = _rng.Next(0x65) + 200;
                    // BTL_addConfusionStatusVisual
                    if (!d.Stunned && !d.Flattened && d.FlatTimer == 0)
                    {
                        if (d.Poisoned) RemoveCondVisual(d, 1);
                        AddCondVisual(d, 2);
                    }
                    ResetFighterAction(d);
                    break;
                case 3:
                    if (d.Stunned) break;
                    d.Stunned = true; d.StunFrames = _rng.Next(0x29) + 200;
                    // BTL_addStunStatusVisual
                    if (!d.Flattened && d.FlatTimer == 0)
                    {
                        if (d.Confused) RemoveCondVisual(d, 2);
                        if (d.Poisoned) RemoveCondVisual(d, 1);
                        AddCondVisual(d, 3);
                    }
                    ResetFighterAction(d);
                    break;
                case 4:
                    if (d.Flattened) break;
                    d.FlatTimer = -1;           // starts when the hit reaction is over (BTL_tickFighterAction)
                    RemoveCondVisuals(d);
                    ResetFighterAction(d);
                    break;
            }
        }

        // BTL_resetFighterAction: cooldown, senile timer and the charge / cooldown / senile flags cleared
        void ResetFighterAction(Fighter f) { f.Cool = 0; f.Stall = 0; }

        // handleStatusItems / handleRestoreHealing (item.c), in a fight: Various (8) cures every condition (with the buff rings, BTL_addBuffRingsEffect,
        // not drawn yet), Omnipotent (9) and Sup.restore (12) cure them too, Protection (10) keeps new ones off for the rest of the fight
        void ItemConditions(int item)
        {
            if (Partner == null || Partner.Hp <= 0) return;
            if (item == 8 || item == 9 || item == 12) HealStatus(Partner);
            else if (item == 10) Partner.Protected = true;
        }

        // BTL_healStatusEffect: poison, confusion and stun cleared; a flat Digimon starts getting up (BTL_healFlatten: its timer to 0x41; one still
        // going flat turns round at once)
        void HealStatus(Fighter f)
        {
            if (f.Poisoned) { f.Poisoned = false; f.PoisonFrames = 0; if (!f.Confused && !f.Stunned && !f.Flattened && f.FlatTimer == 0) RemoveCondVisual(f, 1); }
            if (f.Confused) ClearConfusion(f);
            if (f.Stunned) ClearStun(f);
            if (f.Flattened || f.FlatTimer != 0)
            {
                if (f.FlatTimer >= 0x42) f.FlatTimer = 0x41;
                if (f.FlatTimer != 0 && !f.Flattened)
                {
                    f.Flattened = true;
                    if (f.FlatTimer >= 4) f.FlatSprite = 0;
                    f.FlatTimer = 0x41;
                }
            }
        }

        // ------------------------------------------------------------------ timers (BTL_tickStatusEffects), once a game frame

        void TickConditions(float dt)
        {
            if (!Active) return;
            DrawConditions3D();
            DrawBuffDisk();
            _condAcc += dt;
            while (_condAcc >= Frame)
            {
                _condAcc -= Frame;
                bool fight = _phase == Phase.Fight;
                bool noAi = _charging || FinishingFighter() != null;        // NO_AI_FLAG
                if (fight)
                    foreach (var f in Fighters())
                    {
                        if (f.Hp <= 0) { if (f.Poisoned || f.Confused || f.Stunned || FlatBusy(f)) ClearConditions(f); continue; }
                        TickConditionFrame(f, noAi);
                    }
                TickCondFx();
                TickBuffDisk();
                if (fight) TickFlatBullets();
            }
        }

        IEnumerable<Fighter> Fighters() { if (Partner != null) yield return Partner; if (Enemy != null) yield return Enemy; foreach (var x in Extras) yield return x; }

        void TickConditionFrame(Fighter f, bool noAi)
        {
            if (f.Poisoned)
            {
                if (!noAi) f.PoisonFrames--;
                if (f.PoisonFrames == 0)
                {
                    // a 1-3% of its max HP every 100 frames, shown in TEXT_COLORS[0xC]; the enemy's bar shows again (unk25 = 0)
                    f.PoisonFrames = 100;
                    int dmg = f.MaxHp * (_rng.Next(3) + 1) / 100;
                    if (f != Partner) f.BarFrames = 0;
                    f.Hp = Mathf.Max(0, f.Hp - dmg);
                    _pops.Add(new Pop { Text = dmg.ToString(), Pos = f.T.position + Vector3.up * 0.9f, Col = new Color(1f, 0f, 1f) });
                    Sync();
                }
            }
            if (f.Confused)
            {
                if (!noAi && f.ConfuseFrames != 0) f.ConfuseFrames--;
                bool partnerAttacking = false;
                foreach (var sw in _swings) if (sw.From == Partner && !sw.Done) partnerAttacking = true;
                if (f.ConfuseFrames == 0 && !partnerAttacking) ClearConfusion(f);
            }
            if (f.Stunned)
            {
                if (!noAi) f.StunFrames--;
                if (f.StunFrames <= 0) ClearStun(f);
            }
            // BTL_tickFighterAction: the flat timer starts (0x41) once the hit reaction that brought it is over
            if (f.FlatTimer == -1 && _time >= f.HitUntil && _time >= f.NextAct) f.FlatTimer = 0x41;
            // the hit reaction of a flat Digimon is over: flat picture 0 again
            if (f.Flattened && f.FlatSprite == 3 && _time >= f.HitUntil) f.FlatSprite = 0;
            TickFlatAttack(f);
            if (f.FlatTimer <= 0) return;
            if (!noAi || f.FlatTimer < 0x42) f.FlatTimer--;
            ApplyFlattenScale(f, f.FlatTimer);
            if (f.Flattened)
            {
                switch (f.FlatTimer)
                {
                    case 0x40:      // getting back up: animation 0x22, nothing hits it until it is done
                        if (_time < f.HitUntil || f.FlatAttack > 0) { f.FlatTimer++; break; }
                        if (f.A != null && f.A.HasSlot(StunSlot)) f.A.SetSlot(StunSlot);
                        f.HitUntil = _time + 0x40 * Frame;
                        break;
                    case 3: f.FlatSprite = -1; break;
                    case 0:
                        f.Flattened = false; f.HitUntil = _time;
                        if (f.Stunned) AddCondVisual(f, 3);
                        if (f.Confused) AddCondVisual(f, 2);
                        if (f.Poisoned) AddCondVisual(f, 1);
                        ResetFighterAction(f);
                        f.T.localScale = f.Scale;
                        if (!f.Stunned) Stand(f);
                        break;
                }
            }
            else
            {
                switch (f.FlatTimer)
                {
                    case 0x40:      // going flat: animation 0x22, nothing hits it until it is done
                        if (f.A != null && f.A.HasSlot(StunSlot)) f.A.SetSlot(StunSlot);
                        f.HitUntil = _time + 0x40 * Frame;
                        break;
                    case 3: f.FlatSprite = 0; break;
                    case 0:
                        f.Flattened = true; f.FlatTimer = _rng.Next(0x51) + 0xE0;
                        f.HitUntil = _time;
                        break;
                }
            }
        }

        // BTL_applyFlattenScale (with BTL_applyStretchScale / BTL_applySquashScale): from 0x3F down the Digimon stretches and squashes in ever
        // shorter swings, shrinks to nothing at 4 and grows back to full size by 0. _sin is 512 steps a turn.
        void ApplyFlattenScale(Fighter f, int t)
        {
            if (t >= 0x40 || t < 0) return;
            float x, y;
            Func<int, float> sin = a => Mathf.Sin((a & 0x1FF) * Mathf.PI * 2f / 512f);
            if (t >= 0x30) Stretch(sin((0x40 - t) << 4), out x, out y);
            else if (t >= 0x20) Squash(sin((0x30 - t) << 4), out x, out y);
            else if (t >= 0x18) Stretch(sin((0x20 - t) << 5), out x, out y);
            else if (t >= 0x10) Squash(sin((0x18 - t) << 5), out x, out y);
            else if (t >= 0xC) Stretch(sin((0x10 - t) << 6), out x, out y);
            else if (t >= 8) Squash(sin((0xC - t) << 6), out x, out y);
            else if (t >= 6) Stretch(sin((8 - t) << 7), out x, out y);
            else if (t >= 4) x = y = (0x1000 - ((6 - t) << 11)) / 4096f;
            else x = y = ((4 - t) << 10) / 4096f;
            // the game sets the scale outright (1, or 1.5 for type 0x71, a size ours does not give it); ours keeps the model's own size under it
            f.T.localScale = new Vector3(f.Scale.x * x, f.Scale.y * y, f.Scale.z * x);
        }

        static void Stretch(float s, out float x, out float y) { y = 1f + s; x = 1f - s / 2f; }
        static void Squash(float s, out float x, out float y) { y = 1f - s / 2f; x = 1f + s / 2f; }

        // BTL_clearConfusion: its effect goes; poison's comes back (when nothing above it shows)
        void ClearConfusion(Fighter f)
        {
            ResetFighterAction(f);
            f.Confused = false; f.ConfuseFrames = 0; f.Blind = false;
            if (!f.Stunned && !f.Flattened && f.FlatTimer == 0)
            {
                RemoveCondVisual(f, 2);
                if (f.Poisoned) AddCondVisual(f, 1);
            }
        }

        // BTL_clearStun: the stance again; confusion's or poison's effect comes back
        void ClearStun(Fighter f)
        {
            ResetFighterAction(f);
            f.Stunned = false; f.StunFrames = 0;
            if (f.Flattened || f.FlatTimer != 0) return;
            if (f.A != null && f.A.Slot == StunSlot) Stand(f);
            RemoveCondVisual(f, 3);
            if (f.Confused) AddCondVisual(f, 2);
            if (f.Poisoned) AddCondVisual(f, 1);
        }

        // BTL_resetFlatten + BTL_removeStatusEffects + the flags cleared: a fighter that falls, and everyone when the fight ends
        void ClearConditions(Fighter f)
        {
            if (FlatBusy(f) || f.FlatSprite != -1) f.T.localScale = f.Scale;
            f.FlatSprite = -1; f.Flattened = false; f.FlatTimer = 0; f.FlatAttack = 0;
            RemoveCondVisuals(f);
            f.Poisoned = f.Confused = f.Stunned = false; f.PoisonFrames = f.ConfuseFrames = f.StunFrames = 0; f.Blind = false;
        }

        void ClearConditions()
        {
            foreach (var f in Fighters()) ClearConditions(f);
            foreach (var f in _modelHidden) SetModelShown(f, true);
            _modelHidden.Clear();
            _condFx.Clear(); _bubbles.Clear(); _sparks.Clear(); _bullets.Clear();
            _disk = null; _trailTable = null;       // BTL_removeAllBuffDiskEffects; BTL_initializeBuffTrails rolls new places next fight
        }

        // ------------------------------------------------------------------ the effect over the Digimon (statusFxId)

        // BTL_addStatusEffectVisual: only when none shows; 1 poison, 2 confusion, 3 stun
        void AddCondVisual(Fighter f, int kind)
        {
            if (f.FxKind != 0) return;
            int slots = kind == 3 ? 5 : 4, used = 0;
            foreach (var c in _condFx) if (c.Kind == kind && !c.Gone) used++;
            if (used >= slots) return;
            var fx = new CondFx { Kind = kind, Owner = f };
            if (kind == 2) for (int i = 0; i < 3; i++) fx.Offsets[i] = _rng.Next(0x8000);     // rand()
            if (kind == 3) fx.Duration = f.StunFrames;
            _condFx.Add(fx);
            f.FxKind = kind;
        }

        // BTL_removeStatusEffectVisual: removes the effect of that kind (the stun one may already have ended by itself)
        void RemoveCondVisual(Fighter f, int kind)
        {
            if (f.FxKind == 0) return;
            foreach (var c in _condFx) if (c.Owner == f && c.Kind == kind) c.Gone = true;
            _condFx.RemoveAll(c => c.Gone);
            f.FxKind = 0;
        }

        // BTL_removeStatusEffects: stun, confusion, poison, each when its flag is set
        void RemoveCondVisuals(Fighter f)
        {
            if (f.Stunned) RemoveCondVisual(f, 3);
            if (f.Confused) RemoveCondVisual(f, 2);
            if (f.Poisoned) RemoveCondVisual(f, 1);
            // ours: nothing is left behind when the fight ends
            foreach (var c in _condFx) if (c.Owner == f) c.Gone = true;
            _condFx.RemoveAll(c => c.Gone);
            f.FxKind = 0;
        }

        void TickCondFx()
        {
            for (int i = _condFx.Count - 1; i >= 0; i--)
            {
                var c = _condFx[i];
                switch (c.Kind)
                {
                    case 1:     // BTL_tickPoisonEffect: a bubble on frame 1 of every 30
                        c.Frame = (c.Frame + 1) % 0x1E;
                        if (c.Frame == 1 && _bubbles.Count < 12) _bubbles.Add(new Bubble { Owner = c.Owner, OffX = _rng.Next(100) - 0x32, OffZ = _rng.Next(100) - 0x32 });
                        break;
                    case 2:     // BTL_tickConfusionEffect
                        c.Angle += 7; c.Spin += 0x5B;
                        break;
                    case 3:     // BTL_tickStunEffect: a spark ring every 6 frames while it counts, 8 frames blinking at 0, 5 shrinking, then it removes itself
                        c.Frame++;
                        if (c.State == 0)
                        {
                            if (c.Frame % 6 == 0 && _sparks.Count < 0x19) _sparks.Add(new Spark { Owner = c.Owner });
                            if (c.Frame >= c.Duration) { c.State = 1; c.Frame = 1; }
                        }
                        else if (c.State == 1) { if (c.Frame >= 8) { c.State = 2; c.Frame = 1; } }
                        else if (c.Frame >= 5) _condFx.RemoveAt(i);      // statusFxId is left as it is (BTL_removeStunEffect does not clear it)
                        break;
                }
            }
            for (int i = _bubbles.Count - 1; i >= 0; i--) if (++_bubbles[i].Frame >= 0x28) _bubbles.RemoveAt(i);     // BTL_tickPoisonBubble: 40 frames
            for (int i = _sparks.Count - 1; i >= 0; i--) if (++_sparks[i].Frame >= 0x10) _sparks.RemoveAt(i);        // BTL_tickStunSubEffect: 16 frames
            foreach (var f in Fighters())
            {
                // renderFlatDigimon: pictures 0 and 1 take turns every 10 frames (entity.flatTimer counts every frame it is drawn)
                if (f.FlatSprite < 0 || f.FlatSprite >= 2) continue;
                if (f.FlatFlip % 10 == 0) f.FlatSprite = (f.FlatSprite + 1) & 1;
                f.FlatFlip++;
            }
        }

        // ------------------------------------------------------------------ flat: moving and the flat bullet

        // BTL_tickEnemyAI / BTL_tickPartnerAI (flattened: moveRange 2, flat picture 0) and BTL_tickFlatState -> BTL_tickRangedAttack(0x79):
        // it keeps the flat bullet's distance (MOVE_DATA[0x79] + both radii squared, within 30%), and fires every 40th frame once there;
        // after 80 frames of trying it fires anyway. During a fight under NO_AI_FLAG it just stands (handleBattleIdle).
        void MoveFlat(Fighter me, Fighter foe, float dt)
        {
            if (!me.Flattened || me.FlatAttack > 0) return;
            bool hurt = me.Poisoned || me.Hp <= me.MaxHp / 5;
            int walk = me.A == null ? 2 : hurt && me.A.HasSlot(WalkSlot) ? WalkSlot : me.A.HasSlot(RunSlot) ? RunSlot : 2;
            if (me == Partner && _running) { RunAfterBoy(me, walk); return; }      // BTL_handlePartnerMoveCommand
            if (me.FlatSprite == 3) return;
            Tech t = FlatBullet();
            float dSq = Mathf.Pow(Vector3.Distance(Flat(me.T.position), Flat(foe.T.position)) * 100f, 2f);
            float radii = (me.Info != null ? me.Info.Radius : 0) + (foe.Info != null ? foe.Info.Radius : 0);
            float total = _flatBulletDist + radii * radii;
            Vector3 to = Flat(foe.T.position - me.T.position).normalized;
            if (me.Adjust > 80f / 20f) { me.Adjust = 0f; StartFlatAttack(me, foe); return; }
            if (dSq > total * 1.3f) { me.Adjust += dt; Step(me, to, foe, dt, walk); return; }
            if (dSq < total * 0.7f) { me.Adjust += dt; BackAway(me, foe, dt, walk); return; }
            me.Adjust = 0f;
            if (me.A != null && me.A.Slot != IdleSlot && me.A.HasSlot(IdleSlot)) Stand(me);
            if (_frame % 40 == 0) StartFlatAttack(me, foe);
            else me.T.rotation = _face(foe.T.position - me.T.position);
        }

        // BTL_startQueuedMove for a flattened fighter: not while cooling down, not at a target in its hit reaction; flat picture 2, flatAttackTimer 30
        void StartFlatAttack(Fighter me, Fighter foe)
        {
            if (me.Cool > 1) return;
            if (foe.A != null && (foe.A.Slot == HitFrontSlot || foe.A.Slot == HitBackSlot)) return;
            me.T.rotation = _face(foe.T.position - me.T.position);
            me.FlatAttack = 0x1E;
            me.FlatSprite = 2;
        }

        // BTL_tickFighterAction (flattened and attacking): the bullet at 0x1C, flat picture 0 at 0xA, the end at 0: 40 frames of cooldown
        // and the finishing gauge's 2 / 50
        void TickFlatAttack(Fighter f)
        {
            if (!f.Flattened || f.FlatAttack <= 0) return;
            f.FlatAttack--;
            if (f.FlatAttack == 0x1C) AddFlatBullet(f);
            else if (f.FlatAttack == 0xA) f.FlatSprite = 0;
            else if (f.FlatAttack == 0) { f.Cool = 40; AddFinish(f, f.FinishMax * 2 / 50); }
        }

        Tech _flatBullet; float _flatBulletDist;
        int[] _auraU; short[] _origTypes;

        // MOVE_DATA[0x79], the row after the 121 the technique catalog reads: distance, power, iframes, range, element, status, accuracy
        Tech FlatBullet()
        {
            if (_flatBullet != null || Exe == null) return _flatBullet ?? new Tech { Id = FlatBulletTech, Name = "", Col = Color.white };
            int r = (int)(0x8012623C - BitConverter.ToUInt32(Exe, 0x18) + 0x800) + FlatBulletTech * 16;
            _flatBulletDist = BitConverter.ToUInt32(Exe, r);
            _flatBullet = new Tech
            {
                Id = FlatBulletTech, Name = "", Power = BitConverter.ToInt16(Exe, r + 4), Mp = 0, Stagger = Exe[r + 7], Element = Exe[r + 9],
                Status = Exe[r + 10], Accuracy = Exe[r + 11], StatusChance = Exe[r + 12], Col = Color.white
            };
            return _flatBullet;
        }

        // getOriginalType: ORIGINAL_DIGIMON_TYPES[type], or the type itself when that is below 0
        int OriginalType(int type)
        {
            if (_origTypes == null && Exe != null)
            {
                _origTypes = new short[180];
                int o = (int)(OriginalTypes - BitConverter.ToUInt32(Exe, 0x18) + 0x800);
                for (int i = 0; i < 180; i++) _origTypes[i] = BitConverter.ToInt16(Exe, o + 2 * i);
            }
            if (_origTypes == null || type < 0 || type >= _origTypes.Length) return type;
            return _origTypes[type] < 0 ? type : _origTypes[type];
        }

        // BTL_addAuraProjectile: from half its height up and half its height in front, 0x23 a frame straight ahead, its picture from
        // BTL_AURA_PROJECTILE_TYPES[getOriginalType(type)] (12 bytes each, u at +10)
        void AddFlatBullet(Fighter f)
        {
            if (_bullets.Count >= 8) return;
            if (_auraU == null && BtlRel != null)
            {
                _auraU = new int[129];
                for (int i = 0; i < 129; i++) _auraU[i] = BitConverter.ToInt16(BtlRel, (int)(AuraTypes - BtlBase) + 12 * i + 10);
            }
            int type = f.Info != null ? OriginalType(f.Info.Type) : 0, h = f.Info != null ? f.Info.Height : 200;
            Vector3 fwd = Flat(-f.T.forward).normalized;      // the model's front is -Z
            Vector3 g = EffectPlayer.ToGame(f.T.position), gf = new Vector3(fwd.x, 0f, fwd.z);
            Vector3 pos = g + new Vector3(0f, -h * 50 / 100, 0f) + gf * (h * 50 / 100);
            _bullets.Add(new FlatShot { Owner = f, Pos = pos, Vel = gf * 0x23, U = _auraU != null && type >= 0 && type < 129 ? _auraU[type] : 0 });
        }

        // BTL_tickAuraProjectile: 58 frames; it hits the first Digimon (not its owner) whose box (radius / 2, height / 2 about its middle) meets
        // its own (0x2D, 0xC8, 0x2D); one in its hit reaction (isHit) lets it through
        void TickFlatBullets()
        {
            for (int i = _bullets.Count - 1; i >= 0; i--)
            {
                var b = _bullets[i];
                if (++b.Frame >= 0x3A) { _bullets.RemoveAt(i); continue; }
                b.Pos += b.Vel;
                foreach (var d in Fighters())
                {
                    if (d == b.Owner || d.Hp <= 0) continue;
                    Vector3 c = EffectPlayer.ToGame(d.T.position);
                    int r = (d.Info != null ? d.Info.Radius : 100) >> 1, h = (d.Info != null ? d.Info.Height : 200) >> 1;
                    c.y -= h;
                    if (Mathf.Abs(c.x - b.Pos.x) > r + 0x2D || Mathf.Abs(c.y - b.Pos.y) > h + 0xC8 || Mathf.Abs(c.z - b.Pos.z) > r + 0x2D) continue;
                    if (_time < d.HitUntil) continue;
                    _bullets.RemoveAt(i);
                    Apply(b.Owner, d, FlatBullet(), 1f, EffectPlayer.ToUnity((int)b.Pos.x, (int)b.Pos.y, (int)b.Pos.z));
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ drawing

        byte[] _btlRel, _allTim;
        byte[] BtlRel { get { if (_btlRel == null && GameDisc != null && GameDisc.Exists("/BTL_REL.BIN")) _btlRel = GameDisc.ReadFile("/BTL_REL.BIN"); return _btlRel; } }
        PsxVram _condVram;
        PsxTmd.Built _stunBuilt; readonly PsxTmd.Built[] _confBuilt = new PsxTmd.Built[12];
        Vector3 _stunRot; bool _condModelsTried;
        readonly List<UnityEngine.Object> _condOwned = new List<UnityEngine.Object>();
        readonly Dictionary<int, Texture2D> _flatTex = new Dictionary<int, Texture2D>();
        Mesh _flatQuad; Material _flatMat, _bulletSolid, _bulletAdd; readonly MaterialPropertyBlock _condMp = new MaterialPropertyBlock();

        void LoadCondModels()
        {
            if (_condModelsTried) return;
            _condModelsTried = true;
            try
            {
                var btl = BtlRel;
                if (btl == null || GameDisc == null || !GameDisc.Exists("/ETCDAT/ETCTIM.BIN")) return;
                _condVram = new PsxVram();
                _condVram.LoadTims(GameDisc.ReadFile("/ETCDAT/ETCTIM.BIN"));      // the HUD sheet (896,384) the stun rings use, the flat bullet's (768,384)
                Func<uint, int, byte[]> slice = (addr, size) => { var d = new byte[size]; Array.Copy(btl, (int)(addr - BtlBase), d, 0, size); return d; };
                var stun = PsxTmd.Parse(slice(StunModel, 0x628));
                if (stun.Objs.Count > 0) _stunBuilt = PsxTmd.Built.Make(stun.Objs[0], _condVram, (p, layer) => CondMaterial(p, layer), _condOwned);
                var conf = PsxTmd.Parse(slice(ConfusionModel, 0x4C8));
                if (conf.Objs.Count > 0) for (int i = 0; i < _confBuilt.Length; i++) _confBuilt[i] = PsxTmd.Built.Make(conf.Objs[0], _condVram, (p, layer) => CondMaterial(p, layer), _condOwned);
                if (Exe != null)
                {
                    int o = (int)(StunFxRotation - BitConverter.ToUInt32(Exe, 0x18) + 0x800);
                    _stunRot = new Vector3(BitConverter.ToInt16(Exe, o), BitConverter.ToInt16(Exe, o + 2), BitConverter.ToInt16(Exe, o + 4));
                }
            }
            catch (Exception e) { Debug.LogWarning("Condition models not available: " + e.Message); _stunBuilt = null; }
        }

        // a TMD packet's material (shader DW1Remake/Psx): the picture from video memory; semi-transparent colours with the page's blend
        Material CondMaterial(PsxTmd.Prim p, int layer)
        {
            var sh = Shader.Find("DW1Remake/Psx");
            if (sh == null) return null;
            var m = new Material(sh) { renderQueue = 2460 };
            m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
            if (layer < 0) { m.SetFloat("_Textured", 0f); if (p.Semi) PsxBlend(m, (p.Tpage >> 5) & 3); return m; }
            m.SetTexture("_MainTex", _condVram.Page(p.Tpage, p.Clut, layer));
            if (layer == 2) PsxBlend(m, (p.Tpage >> 5) & 3);
            return m;
        }

        // the PS1 blend of a semi-transparent primitive (abr): 0 half and half, 1 added, 2 subtracted, 3 a quarter added
        static void PsxBlend(Material m, int abr)
        {
            float one = (float)UnityEngine.Rendering.BlendMode.One, add = (float)UnityEngine.Rendering.BlendOp.Add;
            switch (abr & 3)
            {
                case 0: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); m.SetFloat("_Opacity", 0.5f); m.SetFloat("_BlendOp", add); break;
                case 1: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", add); break;
                case 2: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.ReverseSubtract); break;
                default: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 0.25f); m.SetFloat("_BlendOp", add); break;
            }
            m.SetFloat("_ZWrite", 0f);
            m.renderQueue = 3000;
        }

        static Matrix4x4 PsRotX(int a) { float c = Mathf.Cos(a * Mathf.PI / 2048f), s = Mathf.Sin(a * Mathf.PI / 2048f); var m = Matrix4x4.identity; m[1, 1] = c; m[1, 2] = -s; m[2, 1] = s; m[2, 2] = c; return m; }
        static Matrix4x4 PsRotY(int a) { float c = Mathf.Cos(a * Mathf.PI / 2048f), s = Mathf.Sin(a * Mathf.PI / 2048f); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 2] = s; m[2, 0] = -s; m[2, 2] = c; return m; }
        static Matrix4x4 PsRotZ(int a) { float c = Mathf.Cos(a * Mathf.PI / 2048f), s = Mathf.Sin(a * Mathf.PI / 2048f); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 1] = -s; m[1, 0] = s; m[1, 1] = c; return m; }

        // renderTMDModel: RotMatrix (X x Y x Z), ScaleMatrix, TransMatrix, all in game space; PsxTmd meshes are already in Unity space (x, -y, z) x 0.01
        static Matrix4x4 GameModelMatrix(Vector3 gamePos, Matrix4x4 rot, Vector3 scale)
        {
            var toUnity = Matrix4x4.Scale(new Vector3(0.01f, -0.01f, 0.01f)); var toGame = Matrix4x4.Scale(new Vector3(100f, -100f, 100f));
            return Matrix4x4.Translate(new Vector3(gamePos.x * 0.01f, -gamePos.y * 0.01f, gamePos.z * 0.01f)) * toUnity * rot * Matrix4x4.Scale(scale) * toGame;
        }

        static void DrawBuilt(PsxTmd.Built b, Matrix4x4 m)
        {
            if (b == null || b.Mesh == null) return;
            for (int k = 0; k < b.Mats.Length; k++) if (b.Mats[k] != null) Graphics.DrawMesh(b.Mesh, m, b.Mats[k], 0, null, k);
        }

        Vector3 FxPointGame(Fighter f)
        {
            Vector3 p = FxPoint != null ? FxPoint(f.T, f.A, f.Info != null ? f.Info.Type : -1) : f.T.position + Vector3.up * ((f.Info != null ? f.Info.Height : 200) * 0.01f);
            return EffectPlayer.ToGame(p);
        }

        void DrawConditions3D()
        {
            foreach (var f in Fighters())
            {
                bool flat = f.FlatSprite >= 0;
                if (flat == _modelHidden.Contains(f)) continue;
                SetModelShown(f, !flat);
                if (flat) _modelHidden.Add(f); else _modelHidden.Remove(f);
            }
            if (_condFx.Count == 0 && _sparks.Count == 0 && _bullets.Count == 0 && !AnyFlatPicture()) return;
            LoadCondModels();
            // BTL_renderConfusionEffect: three models circling 0x78 from the effect point, 0x78 above it, each turning (rot vz 0xE3), lit by the map's lights
            int confSlot = 0;
            PsxTmd.Lights lights = null;
            foreach (var c in _condFx)
            {
                if (c.Kind != 2 || confSlot + 3 > _confBuilt.Length || _confBuilt[0] == null) continue;
                if (lights == null) lights = MapLights != null ? MapLights() : null;
                Vector3 p = FxPointGame(c.Owner);
                for (int i = 0; i < 3; i++)
                {
                    int ang = c.Angle + i * 0xAA;
                    float s512 = Mathf.Sin((ang & 0x1FF) * Mathf.PI * 2f / 512f), c512 = Mathf.Sin(((ang + 0x80) & 0x1FF) * Mathf.PI * 2f / 512f);
                    Vector3 at = new Vector3(p.x + (int)(s512 * 4096f) * 0x78 / 4096, p.y - 0x78, p.z + (int)(c512 * 4096f) * 0x78 / 4096);
                    Matrix4x4 r = PsRotX(0) * PsRotY(c.Offsets[i] + (c.Spin - 0x400 + i * 0x555)) * PsRotZ(0xE3);
                    var b = _confBuilt[confSlot++];
                    if (lights != null) b.Relight(lights, r);
                    DrawBuilt(b, GameModelMatrix(at, r, Vector3.one));
                }
            }
            // BTL_renderStunSubEffect: the ring falls from 1.13 x its height to its feet in 15 frames, x and z scale radius x 4 / 350 (growing from
            // a tenth in the first 4 frames), y scale 3
            if (_stunBuilt != null)
                foreach (var s in _sparks)
                {
                    var f = s.Owner; int h = f.Info != null ? f.Info.Height : 200, rad = f.Info != null ? f.Info.Radius : 100;
                    Vector3 g = EffectPlayer.ToGame(f.T.position);
                    float y = g.y + EffectPlayer.PsLerp(-h * 113 / 100, 0, 0, 0xF, s.Frame);
                    int sc = rad * 0x4000 / 350;
                    if (s.Frame < 4) sc = EffectPlayer.PsLerp(sc * 10 / 100, sc, 0, 4, s.Frame);
                    Matrix4x4 r = PsRotX((int)_stunRot.x) * PsRotY((int)_stunRot.y) * PsRotZ((int)_stunRot.z);
                    DrawBuilt(_stunBuilt, GameModelMatrix(new Vector3(g.x, y, g.z), r, new Vector3(sc / 4096f, 3f, sc / 4096f)));
                }
            DrawFlatPictures();
            DrawFlatBullets();
        }

        bool AnyFlatPicture() { foreach (var f in Fighters()) if (f.FlatSprite >= 0) return true; return false; }

        // renderFlatDigimon: instead of the model, a 15 x 15 picture of the Digimon's own texture (u 0xE0 / 0xF0, v + 0x60 / 0x70; colour set 15 of
        // its TIM, clutPage + 0x3C0) on a square as tall as the Digimon, standing along the way it faces, scaled with it
        void DrawFlatPictures()
        {
            foreach (var f in Fighters())
            {
                if (f.FlatSprite < 0 || f.Info == null) continue;
                var tex = FlatTexture(f.Info.Type);
                if (tex == null) continue;
                if (_flatQuad == null)
                {
                    _flatQuad = new Mesh { name = "FlatDigimon" };
                    _flatQuad.SetVertices(new List<Vector3> { new Vector3(0, 1, -0.5f), new Vector3(0, 1, 0.5f), new Vector3(0, 0, -0.5f), new Vector3(0, 0, 0.5f) });
                    _flatQuad.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);
                    _flatQuad.SetColors(new List<Color32> { new Color32(128, 128, 128, 255), new Color32(128, 128, 128, 255), new Color32(128, 128, 128, 255), new Color32(128, 128, 128, 255) });
                    _flatQuad.RecalculateBounds();
                }
                if (_flatMat == null)
                {
                    var sh = Shader.Find("DW1Remake/Psx");
                    if (sh == null) return;
                    _flatMat = new Material(sh) { renderQueue = 2450 };
                    _flatMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);      // a POLY_FT4 has no back
                }
                int u = f.FlatSprite % 2 == 0 ? 0xE0 : 0xF0, v = f.FlatSprite < 2 ? 0x60 : 0x70;
                var uv = new List<Vector2> { Uv(tex, u, v), Uv(tex, u + 15, v), Uv(tex, u, v + 15), Uv(tex, u + 15, v + 15) };
                var mesh = FlatQuadFor(f);
                mesh.SetUVs(0, uv);
                float hgt = f.Info.Height * 0.01f;
                Matrix4x4 m = Matrix4x4.TRS(f.T.position, f.T.rotation, f.T.lossyScale) * Matrix4x4.Scale(new Vector3(1f, hgt, hgt));
                _condMp.Clear(); _condMp.SetTexture("_MainTex", tex);
                Graphics.DrawMesh(mesh, m, _flatMat, 0, null, 0, _condMp);
            }
        }

        static Vector2 Uv(Texture2D t, int u, int v) { return new Vector2(u / (float)t.width, 1f - v / (float)t.height); }

        readonly Dictionary<Fighter, Mesh> _flatMeshes = new Dictionary<Fighter, Mesh>();
        Mesh FlatQuadFor(Fighter f)
        {
            Mesh m;
            if (_flatMeshes.TryGetValue(f, out m) && m != null) return m;
            m = UnityEngine.Object.Instantiate(_flatQuad);
            _flatMeshes[f] = m;
            return m;
        }

        Texture2D FlatTexture(int type)
        {
            Texture2D t;
            if (_flatTex.TryGetValue(type, out t)) return t;
            if (_allTim == null && GameDisc != null && GameDisc.Exists("/CHDAT/ALLTIM.TIM")) _allTim = GameDisc.ReadFile("/CHDAT/ALLTIM.TIM");
            TimImage tim;
            t = _allTim != null && TimImage.TryParse(_allTim, type * 0x4800, out tim) && tim.PaletteCount > 15 ? tim.ToTexture(15) : null;
            if (t != null) t.filterMode = FilterMode.Point;
            _flatTex[type] = t;
            return t;
        }

        // the model's own pieces are not drawn while the flat picture shows (renderDigimon: flatSprite != -1)
        static void SetModelShown(Fighter f, bool shown)
        {
            if (f.T == null) return;
            foreach (var r in f.T.GetComponentsInChildren<MeshRenderer>(true))
                if (r.enabled != shown && r.GetComponent<MeshFilter>() != null && r.gameObject.name.StartsWith("bone_")) r.enabled = shown;
        }

        // BTL_renderAuraProjectile: a 100 x 100 square standing along its flight, 7 x 7 texels of page (768,256) at (u + 0x60, 0xA0), colour set (192,492), added
        void DrawFlatBullets()
        {
            if (_bullets.Count == 0 || _condVram == null) return;
            if (_bulletSolid == null)
            {
                var sh = Shader.Find("DW1Remake/Psx");
                if (sh == null) return;
                // getTPage(0, 1, 768, 256): x / 64, y / 256 in bit 4 (it was missing: the bullet read page (768, 0), a plain square), abr 1 in bits 5-6
                int tpage = (768 / 64) | ((256 / 256) << 4) | (1 << 5), clut = (192 / 16) | (492 << 6);
                _bulletSolid = new Material(sh) { renderQueue = 2460, mainTexture = _condVram.Page(tpage, clut, 1) };
                _bulletSolid.SetFloat("_Cull", 0f);
                _bulletAdd = new Material(sh) { mainTexture = _condVram.Page(tpage, clut, 2) };
                _bulletAdd.SetFloat("_Cull", 0f);
                PsxBlend(_bulletAdd, 1);
            }
            foreach (var b in _bullets)
            {
                if (_flatQuad == null) break;
                float u = b.U + 0x60, v = 0xA0;
                var mesh = BulletMesh(u, v);
                Vector3 at = new Vector3(b.Pos.x * 0.01f, -b.Pos.y * 0.01f, b.Pos.z * 0.01f) - new Vector3(0f, 0.5f, 0f);
                Vector3 dir = new Vector3(b.Vel.x, 0f, b.Vel.z);
                if (dir.sqrMagnitude < 1e-6f) continue;
                Matrix4x4 m = Matrix4x4.TRS(at, Quaternion.LookRotation(dir), Vector3.one);
                Graphics.DrawMesh(mesh, m, _bulletSolid, 0);
                Graphics.DrawMesh(mesh, m, _bulletAdd, 0);
            }
        }

        readonly Dictionary<int, Mesh> _bulletMeshes = new Dictionary<int, Mesh>();
        Mesh BulletMesh(float u, float v)
        {
            Mesh m; int key = (int)u;
            if (_bulletMeshes.TryGetValue(key, out m)) return m;
            m = UnityEngine.Object.Instantiate(_flatQuad);
            m.SetUVs(0, new List<Vector2> { new Vector2(u / 256f, 1f - v / 256f), new Vector2((u + 7) / 256f, 1f - v / 256f), new Vector2(u / 256f, 1f - (v + 7) / 256f), new Vector2((u + 7) / 256f, 1f - (v + 7) / 256f) });
            _bulletMeshes[key] = m;
            return m;
        }

        // the 2D parts, in the HUD pass: poison bubbles and the stun countdown
        void DrawConditions2D(Camera cam)
        {
            if (Art == null) return;
            // BTL_renderPoisonBubble: from the effect point (plus its own x / z offset) it rises from 0x32 to 0xC8 over 40 frames and sways by
            // _sin x 0x14 sideways; frames at u 0x20 + 0, 16 ... 80 (one every 2 frames) and v 0xB0 of the condition sheet, colour set (272,487),
            // added; its size 0x4EA4 x VIEWPORT_DISTANCE / depth
            foreach (var b in _bubbles)
            {
                Vector3 g = FxPointGame(b.Owner);
                g.x += b.OffX; g.y -= EffectPlayer.PsLerp(0x32, 0xC8, 0, 0x28, b.Frame); g.z += b.OffZ;
                Vector3 w = EffectPlayer.ToUnity((int)g.x, (int)g.y, (int)g.z);
                float depth;
                if (!Ps1Depth(cam, w, out depth) || depth <= 0x200 || depth >= 0x10000) continue;
                Vector2 at = Ps1Of(cam, w);
                int sway = Mathf.RoundToInt(Mathf.Sin((EffectPlayer.PsLerp(0, 0x500, 0, 0x28, b.Frame) & 0x1FF) * Mathf.PI * 2f / 512f) * 4096f) * 0x14 / 4096;
                float vpd = EffectPlayer.ViewportDistance, k = 0x4EA4 / 4096f * vpd / depth;
                at.x += sway * vpd / depth;
                int u = new[] { 0, 16, 32, 48, 64, 80 }[(b.Frame >> 1) % 6] + 0x20;
                Art.ConditionPs(7, 1, u, 0xB0, 16, 16, at.x - 8f * k, at.y - 8f * k, 16f * k, 16f * k);
            }
            // BTL_renderStunEffect: over the Digimon at 1.13 x its height, the time left (value = frames x 100 / 20: seconds and hundredths) in the
            // HUD sheet's digits on two plates; at 0 the plates blink between colour sets 0 and 1 for 8 frames, then it all shrinks in 5. Added (0x50000000).
            foreach (var c in _condFx)
            {
                if (c.Kind != 3) continue;
                int value = 0, cy = 0; float scale = 1f;
                if (c.State == 0) value = (c.Duration - c.Frame) * 100 / 20;
                else if (c.State == 1) cy = c.Frame % 2;
                else scale = EffectPlayer.PsLerp(0x1000, 0xCC, 0, 5, c.Frame) / 4096f;
                int h = c.Owner.Info != null ? c.Owner.Info.Height : 200;
                Vector3 g = EffectPlayer.ToGame(c.Owner.T.position); g.y -= h * 113 / 100;
                Vector3 w = EffectPlayer.ToUnity((int)g.x, (int)g.y, (int)g.z);
                float depth;
                if (!Ps1Depth(cam, w, out depth)) continue;
                Vector2 p = Ps1Of(cam, w);
                // added to the ordering table in this order, so drawn backwards: the plates first
                // plates: colour set (256, 0x1E0 + cy); digits: (256, 0x1E2); v counted from the sheet's top (page v - 0x80)
                StunSprite(cy, 0x28, 0, 12, 22, 6, 0x16, p, scale);
                StunSprite(cy, 0, 0, 40, 22, 0x14, 0x2C, p, scale);
                StunSprite(2, (value % 10) * 4, 0x96 - 0x80, 4, 7, -8, 0x25, p, scale);
                StunSprite(2, (value / 10 % 10) * 4, 0x96 - 0x80, 4, 7, -2, 0x25, p, scale);
                StunSprite(2, (value / 100 % 10) * 8, 0xB7 - 0x80, 8, 9, 7, 0x26, p, scale);
                StunSprite(2, (value / 1000 % 10) * 8, 0xB7 - 0x80, 8, 9, 0xE, 0x26, p, scale);
            }
        }

        // a GsSPRITE drawn by GsSortSprite: its point (mx, my) sits at the screen point, scaled about it
        void StunSprite(int row, int u, int v, int w, int h, int mx, int my, Vector2 p, float scale)
        {
            Art.HudPs(row, 1, u, v, w, h, p.x - mx * scale, p.y - my * scale, w * scale, h * scale);
        }

        // ------------------------------------------------------------------ the disks' wall (BTL_addBuffDiskEffect, battle_effect.c)

        // handleBuffDisks ends with BTL_addBuffDiskEffect(partner) for every disk. One slot (BTL_BUFF_DISKS[1]): a disk thrown while the wall
        // still shows starts none. The wall is object 5 of BUFF_MODEL (BTL_REL 0x80055328, an eight-sided tube 400 high, radius 200; added
        // see-through, its picture in ETCTIM.BIN at (768,384)) at the Digimon's feet: x / z scale radius x 0xBB8 / 200, y height x 0xB22 / 200,
        // from a third to full size in 15 frames, held to frame 0x37, down to a tenth by 0x43, then gone; it turns 0x32 a frame.
        // tickBuffModel swaps its colour set every frame between 0x7ACC and 0x7B0C (rows 491 / 492: the shimmer). BTL_addBuffTrails: 8 streaks
        const uint BuffModel = 0x80055328;
        const int BuffTrailTpage = 0x3C, BuffTrailClut = 0x7A4C;        // getTPage(0, 1, 768, 256), getClut(192, 489)
        sealed class BuffDiskFx { public Fighter Owner; public int Frame, Spin, ScaleXZ, ScaleY, TrailFrame; public readonly Trail[] Trails = new Trail[8]; }
        sealed class Trail { public int Life = -1; public readonly Vector3[] P = new Vector3[4]; }
        BuffDiskFx _disk;
        int _diskModelFrame;            // BUFF_MODEL_FRAME
        int[] _trailTable;              // BTL_BUFF_TRAIL_A_X, A_Z, B_X, B_Z (8 each)
        PsxTmd.Built[] _wallBuilt;      // the wall with colour set 491, then 492
        bool _wallTried;
        Mesh _trailMesh; Material _trailSolid, _trailAdd;

        void AddBuffDiskEffect(Fighter f)
        {
            if (f == null || _disk != null) return;
            if (_trailTable == null) InitBuffTrails();
            int rad = f.Info != null ? f.Info.Radius : 100, hgt = f.Info != null ? f.Info.Height : 200;
            _disk = new BuffDiskFx { Owner = f };
            for (int i = 0; i < 8; i++) _disk.Trails[i] = new Trail();
            _diskTargetXZ = rad * 0xBB8 / 200; _diskTargetY = hgt * 0xB22 / 200;
        }
        int _diskTargetXZ, _diskTargetY;    // scaleTargetXZ, scaleTargetY

        // BTL_initializeBuffTrails: the eight streaks' places round the Digimon, an eighth of a turn each in a random order, each turned a little
        // more at random and 160-198 out (x radius / 200), 60 wide
        void InitBuffTrails()
        {
            _trailTable = new int[32];
            var pool = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7 };
            for (int i = 0; i < 8; i++)
            {
                int r = _rng.Next(32768) % (8 - i);
                int k = pool[r]; pool.RemoveAt(r);
                int s = (_rng.Next(32768) % 0x14 + 0x50) * 200 / 100;
                int vy = k * 0x1000 / 8 + _rng.Next(32768) % 0x200;
                int c = Mathf.RoundToInt(Mathf.Cos(vy * Mathf.PI / 2048f) * 4096f), sn = Mathf.RoundToInt(Mathf.Sin(vy * Mathf.PI / 2048f) * 4096f);
                // RotMatrixZYX (only y) then ApplyMatrixSV on (30, 0, s) and (-30, 0, s)
                _trailTable[i] = (30 * c + s * sn) >> 12;         // A x
                _trailTable[8 + i] = (-30 * sn + s * c) >> 12;    // A z
                _trailTable[16 + i] = (-30 * c + s * sn) >> 12;   // B x
                _trailTable[24 + i] = (30 * sn + s * c) >> 12;    // B z
            }
        }

        // BTL_tickBuffDisk, BTL_tickBuffTrails, tickBuffModel: once a game frame
        void TickBuffDisk()
        {
            _diskModelFrame++;
            var d = _disk;
            if (d == null) return;
            if (d.Frame >= 0x43 || d.Owner.T == null) { _disk = null; return; }
            if (d.Frame < 0xF)
            {
                d.ScaleXZ = EffectPlayer.PsLerp(_diskTargetXZ / 3, _diskTargetXZ, 0, 0xF, d.Frame);
                d.ScaleY = EffectPlayer.PsLerp(_diskTargetY / 3, _diskTargetY, 0, 0xF, d.Frame);
            }
            else if (d.Frame < 0x37) { d.ScaleXZ = _diskTargetXZ; d.ScaleY = _diskTargetY; }
            else
            {
                d.ScaleXZ = EffectPlayer.PsLerp(_diskTargetXZ, _diskTargetXZ * 10 / 100, 0x37, 0x43, d.Frame);
                d.ScaleY = EffectPlayer.PsLerp(_diskTargetY, _diskTargetY * 10 / 100, 0x37, 0x43, d.Frame);
            }
            d.Spin += 0x32;
            d.Frame++;
            // the streaks: a new one each frame for 0x3B frames (slot frame % 8, 8 frames of life) at the feet; the top edge rises 100 a frame,
            // the bottom 0x25
            if (d.TrailFrame < 0x3B)
            {
                int k = d.TrailFrame % 8;
                var t = d.Trails[k];
                Vector3 g = EffectPlayer.ToGame(d.Owner.T.position);
                int rad = d.Owner.Info != null ? d.Owner.Info.Radius : 100;
                t.Life = 8;
                t.P[0] = t.P[2] = new Vector3(g.x + rad * _trailTable[k] / 200, g.y, g.z + rad * _trailTable[8 + k] / 200);
                t.P[1] = t.P[3] = new Vector3(g.x + rad * _trailTable[16 + k] / 200, g.y, g.z + rad * _trailTable[24 + k] / 200);
            }
            foreach (var t in d.Trails)
            {
                t.Life--;
                t.P[0].y -= 100; t.P[1].y -= 100; t.P[2].y -= 0x25; t.P[3].y -= 0x25;
            }
            d.TrailFrame++;
        }

        void LoadWall()
        {
            if (_wallTried) return;
            _wallTried = true;
            LoadCondModels();
            try
            {
                var btl = BtlRel;
                if (btl == null || _condVram == null) return;
                int at = (int)(BuffModel - BtlBase);
                var d = new byte[btl.Length - at];
                Array.Copy(btl, at, d, 0, d.Length);
                _wallBuilt = new PsxTmd.Built[2];
                for (int n = 0; n < 2; n++)
                {
                    var tmd = PsxTmd.Parse(d);
                    if (tmd.Objs.Count < 6) { _wallBuilt = null; return; }
                    var obj = tmd.Objs[5];
                    foreach (var p in obj.Prims) p.Clut = n == 0 ? 0x7ACC : 0x7B0C;       // BUFF_MODEL_MORPH_VALUE[0] / [1]
                    var b = PsxTmd.Built.Make(obj, _condVram, (p, layer) => CondMaterial(p, layer), _condOwned);
                    // TGE packets: the picture as it is, the colour words are not used
                    var grey = new Color32[b.Mesh.vertexCount];
                    for (int i = 0; i < grey.Length; i++) grey[i] = new Color32(128, 128, 128, 255);
                    b.Mesh.colors32 = grey;
                    _wallBuilt[n] = b;
                }
                var tp = new PsxTmd.Prim { Count = 4, Textured = true, Semi = true, Tpage = BuffTrailTpage, Clut = BuffTrailClut };
                _trailSolid = CondMaterial(tp, 1); _trailAdd = CondMaterial(tp, 2);
                foreach (var m in new[] { _trailSolid, _trailAdd }) if (m != null) { m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off); _condOwned.Add(m); }
                _trailMesh = new Mesh { name = "BuffTrails" };
                _trailMesh.MarkDynamic();
                _condOwned.Add(_trailMesh);
            }
            catch (Exception e) { Debug.LogWarning("Disk wall not available: " + e.Message); _wallBuilt = null; }
        }

        // BTL_renderBuffDisk (renderTMDModel: RotMatrix, ScaleMatrix, TransMatrix at the Digimon's place) and BTL_renderBuffTrails (POLY_FT4s,
        // added, u 0x5F-0x30 along the streak, v 0xA0-0xA7 across it, colour life x (200, 255, 180) / 8)
        void DrawBuffDisk()
        {
            var d = _disk;
            if (d == null || d.Owner.T == null) return;
            LoadWall();
            if (_wallBuilt == null) return;
            Vector3 g = EffectPlayer.ToGame(d.Owner.T.position);
            DrawBuilt(_wallBuilt[_diskModelFrame & 1], GameModelMatrix(g, PsRotY(d.Spin), new Vector3(d.ScaleXZ / 4096f, d.ScaleY / 4096f, d.ScaleXZ / 4096f)));
            if (_trailMesh == null) return;
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var col = new List<Color32>(); var tri = new List<int>();
            Vector2[] uvs = { PageUv(0x5F, 0xA0), PageUv(0x5F, 0xA7), PageUv(0x30, 0xA0), PageUv(0x30, 0xA7) };
            foreach (var t in d.Trails)
            {
                if (t.Life <= 0) continue;
                int b = v.Count;
                var c = new Color32((byte)(t.Life * 200 / 8), (byte)(t.Life * 255 / 8), (byte)(t.Life * 180 / 8), 255);
                for (int k = 0; k < 4; k++) { v.Add(new Vector3(t.P[k].x, -t.P[k].y, t.P[k].z) * 0.01f); uv.Add(uvs[k]); col.Add(c); }
                tri.Add(b); tri.Add(b + 1); tri.Add(b + 2); tri.Add(b + 1); tri.Add(b + 3); tri.Add(b + 2);
            }
            _trailMesh.Clear();
            if (tri.Count == 0) return;
            _trailMesh.SetVertices(v); _trailMesh.SetUVs(0, uv); _trailMesh.SetColors(col); _trailMesh.SetTriangles(tri, 0);
            _trailMesh.RecalculateBounds();
            if (_trailSolid != null) Graphics.DrawMesh(_trailMesh, Matrix4x4.identity, _trailSolid, 0);
            if (_trailAdd != null) Graphics.DrawMesh(_trailMesh, Matrix4x4.identity, _trailAdd, 0);
        }

        static Vector2 PageUv(int u, int v) { return new Vector2((u + 0.5f) / 256f, 1f - (v + 0.5f) / 256f); }

        // the depth along the view in game units (worldPosToScreenPos's otz x 4)
        static bool Ps1Depth(Camera cam, Vector3 world, out float depth)
        {
            Vector3 s = cam.WorldToScreenPoint(world);
            depth = s.z * 100f;
            return s.z > 0f;
        }
    }
}
