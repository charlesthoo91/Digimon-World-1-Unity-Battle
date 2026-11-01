using System;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// A fight between the partner and a wild Digimon, played on the map in real time, laid out like the PS1 game (from a recording).
    /// Techniques: each Digimon's usable techniques and its own finishing move come from the disc. Special techniques are projectiles that fly
    /// at where the target was when they were fired, so a target that moves can be missed. Every Digimon has a finishing-move gauge (F-I-N-I-S-H)
    /// that fills with time and with damage taken; when full its next action is its own finishing move (Agumon: Pepper Breath).
    /// Our own formulas (not decoded yet): enemy stats, damage, technique power and MP cost, rewards.
    /// </summary>
    public sealed class Battle
    {
        public sealed class Tech
        {
            public int Id; public string Name; public int Mp, Power; public float Mult; public bool Melee, Finisher, Wide, Stun; public Color Col;
        }

        /// <summary>The Tech page groups the techniques in rows of 8: Fire, Battle (4), Air, Earth, Ice, Mech, Filth. This is where the row of technique n starts.</summary>
        public static int RowStart(int id) { return id < 8 ? 0 : id < 16 ? 8 : id < 24 ? 16 : id < 32 ? 24 : id < 40 ? 32 : id < 44 ? 40 : 44; }

        public sealed class Fighter
        {
            public Transform T; public DigimonAnimator A; public string Name;
            public DigimonStats Info;
            public int Off, Def, Spd, Brain, Hp, MaxHp, Mp, MaxMp;
            public List<Tech> Techs = new List<Tech>(); public Tech Finisher;
            public float Gauge;                 // finishing-move gauge, 0-6 (one letter of FINISH per unit)
            public float NextAct, AnimReset, WanderWait;
            public Vector3 Slot, Wander;
            public bool Guarding;
        }

        sealed class Swing { public Fighter From, To; public float T; public bool Done; public Tech Tech; public Vector3 Start; public float Boost = 1f; }
        sealed class Shot { public Fighter From, To; public Tech Tech; public Vector3 Pos, Dir; public float Travelled; public GameObject Go; public float Boost = 1f; }
        sealed class Pop { public string Text; public Vector3 Pos; public float T; public Color Col; }
        sealed class Throw { public int Item; public float T; public int Heal, Mana; }

        enum Phase { Notice, Banner, Fight, Win, Lose, Fled }

        // animation slots (guessed from the opening's usage; the dev slot viewer, [ and ] in the editor, shows what each slot looks like)
        public static int AttackSlot = 4, TechSlot = 5, HitSlot = 8, DownSlot = 10, CheerSlot = 15;

        // orders the partner learns as its Brains grow: the first two are always there, then one more per 100 Brains (order of unlocking is a guess)
        static readonly string[] Learned = { "Give it all you got!", "Take it easy!", "Get back!", "Change target!", "Hang in there!" };
        const string CallOrder = "Your Call", RunOrder = "Run away";

        public bool Active { get; private set; }
        public bool Won { get; private set; }
        public bool Fled { get; private set; }
        public bool NoFlee;                     // scripted fights (script variable 250 = 1): running away is locked
        public Fighter Partner, Enemy;
        public BattleIcons Icons; public ItemIcons Items;
        public int LastItem = -1;               // the room tells which bag item was just used

        readonly ScriptState _s;
        readonly Func<Vector3, Quaternion> _face;
        readonly System.Random _rng = new System.Random();
        Phase _phase; float _time, _phaseStart, _fleeAt = -1f, _retreatUntil, _nextAsk, _bannerUntil;
        string _banner = "";
        Transform _player; DigimonAnimator _playerA; Vector3 _playerSlot, _right;
        readonly List<string> _orders = new List<string>();
        string _order = CallOrder;
        bool _menuOpen; int _sel;
        readonly List<Swing> _swings = new List<Swing>();
        readonly List<Shot> _shots = new List<Shot>();
        readonly List<Pop> _pops = new List<Pop>();
        readonly List<Throw> _throws = new List<Throw>();
        readonly int[] _before = new int[6], _gain = new int[6];
        int _bits;
        string _log = ""; float _logUntil;
        static Texture2D _disc; static Material _shotMat;

        public System.Action OnOpenBag;         // the room opens the bag (items work in battle: Triangle)

        public Battle(ScriptState state, Func<Vector3, Quaternion> face) { _s = state; _face = face; }

        // -------------------------------------------------------------- setup
        public static int StartHp(int level) { int[] v = { 80, 80, 200, 400, 900, 2000, 4000, 6500 }; return v[Mathf.Clamp(level, 0, 7)]; }
        public static int StartOff(int level) { int[] v = { 15, 15, 30, 55, 100, 170, 260, 330 }; return v[Mathf.Clamp(level, 0, 7)]; }

        public static Tech MakeTech(int id, string name, bool finisher)
        {
            string n = name.ToLowerInvariant();
            string[] melee = { "kick", "punch", "claw", "jab", "charge", "dive", "slap", "whack", "crane", "sprinter", "scissor", "sword", "drill", "wing", "strike", "fist", "attack" };
            bool m = false; foreach (var k in melee) if (n.Contains(k)) m = true;
            Color c = id <= 7 ? new Color(1f, 0.55f, 0.1f) : id <= 12 ? new Color(1f, 0.95f, 0.2f) : id <= 15 ? new Color(0.7f, 1f, 0.7f)
                : id <= 19 ? new Color(0.6f, 0.95f, 1f) : id <= 23 ? new Color(0.3f, 0.5f, 1f) : id <= 31 ? new Color(0.85f, 0.9f, 1f) : id <= 38 ? new Color(0.5f, 0.9f, 0.3f) : new Color(1f, 0.8f, 0.5f);
            // Real values seen on the PS1 Tech page: Spit Fire power 66 / MP 30 / range L; Odor Spray 88 / 75 / L / stun; Ult Poop Hell 333 / 333 / W / flat.
            // The table itself is not found, so the others follow a formula fitted to Spit Fire (power 50 + 8 x slot in its row, MP 10 + 10 x slot).
            int slot = id - RowStart(id);
            int power = 50 + 8 * slot, mp = 10 + 10 * slot;
            bool wide = n.Contains("hell") || n.Contains("plague") || n.Contains("storm") || n.Contains("blizzard") || n.Contains("inferno") || n.Contains("cloud");
            bool stun = n.Contains("spray") || n.Contains("powder") || n.Contains("perfume") || n.Contains("static") || n.Contains("shock");
            return new Tech { Id = id, Name = name, Power = finisher ? 200 : power, Mp = finisher ? 0 : mp, Mult = (finisher ? 200 : power) / 66f * 1.5f, Melee = m && !finisher, Finisher = finisher, Wide = wide, Stun = stun, Col = c };
        }

        /// <summary>The techniques a Digimon can use: the learned ones (all of them for wild Digimon, the first two), and its own finishing move.</summary>
        void SetupTechs(Fighter f, DigimonStats info, bool isPartner)
        {
            if (info == null) return;
            if (isPartner)
            {
                for (int i = 0; i < info.Learnable.Count; i++)
                    if (_s.Techs[info.Learnable[i]] && (_s.TechSet.Count == 0 || _s.TechSet.Contains(info.Learnable[i]))) f.Techs.Add(MakeTech(info.Learnable[i], info.LearnableNames[i], false));
                if (f.Techs.Count == 0 && info.Learnable.Count > 0) f.Techs.Add(MakeTech(info.Learnable[0], info.LearnableNames[0], false));   // Agumon starts with Spit Fire
            }
            else for (int i = 0; i < info.Learnable.Count && i < 2; i++) f.Techs.Add(MakeTech(info.Learnable[i], info.LearnableNames[i], false));
            if (info.SignatureId >= 0) f.Finisher = MakeTech(info.SignatureId, info.SignatureTech, true);
        }

        /// <summary>right = the screen's right direction on the ground.</summary>
        public void Start(Transform player, DigimonAnimator playerAnim, Transform partner, DigimonAnimator partnerAnim, string partnerName, DigimonStats partnerInfo,
                          Transform enemy, DigimonAnimator enemyAnim, string enemyName, DigimonStats enemyInfo, Vector3 right, bool noFlee)
        {
            NoFlee = noFlee;
            Partner = new Fighter
            {
                T = partner, A = partnerAnim, Name = partnerName, Info = partnerInfo,
                Off = _s.Stats[0], Def = _s.Stats[1], Spd = _s.Stats[2], Brain = _s.Stats[3],
                MaxHp = Mathf.Max(1, _s.Stats[4]), MaxMp = _s.Stats[5], Hp = _s.Stats[6], Mp = _s.Stats[7]
            };
            if (Partner.Hp <= 0) Partner.Hp = 1;
            SetupTechs(Partner, partnerInfo, true);
            int lv = enemyInfo != null ? Mathf.Max(1, enemyInfo.Level) : 3;
            float r = 0.9f + (float)_rng.NextDouble() * 0.2f;
            int off = Mathf.RoundToInt(StartOff(lv) * r);
            Enemy = new Fighter
            {
                T = enemy, A = enemyAnim, Name = enemyName, Info = enemyInfo,
                Off = off, Def = Mathf.RoundToInt(off * 0.8f), Spd = Mathf.RoundToInt(off * 0.9f), Brain = Mathf.RoundToInt(off * 0.8f),
                MaxHp = Mathf.RoundToInt(StartHp(lv) * r), MaxMp = 9999, Mp = 9999
            };
            Enemy.Hp = Enemy.MaxHp;
            SetupTechs(Enemy, enemyInfo, false);

            _orders.Clear(); _orders.Add(CallOrder); _orders.Add(RunOrder);
            for (int i = 0; i < Learned.Length && i < Partner.Brain / 100; i++) _orders.Add(Learned[i]);
            _order = CallOrder; _menuOpen = false; _sel = 0;

            right.y = 0; _right = right.sqrMagnitude < 0.01f ? Vector3.right : right.normalized;
            Vector3 center = (partner.position + enemy.position) * 0.5f; center.y = 0;
            Enemy.Slot = center - _right * 1.8f; Partner.Slot = center + _right * 1.2f; _playerSlot = center + _right * 4.2f;
            Enemy.Wander = Enemy.Slot; Partner.Wander = Partner.Slot;
            _player = player; _playerA = playerAnim;
            Active = true; Won = false; Fled = false; _phase = Phase.Notice; _time = 0f; _phaseStart = 0f; _fleeAt = -1f; _retreatUntil = 0f; _nextAsk = 6f; _bannerUntil = 0f;
            _swings.Clear(); _pops.Clear(); _throws.Clear(); ClearShots(); _log = "";
            Partner.NextAct = Enemy.NextAct = 0f;
            enemy.rotation = _face(partner.position - enemy.position);
            if (Partner.A != null) Partner.A.SetSlot(0);
            if (Enemy.A != null) Enemy.A.SetSlot(0);
        }

        float Interval(Fighter f) { return Mathf.Clamp(4.0f - f.Spd / 60f, 1.3f, 3.8f); }

        void Log(string s) { _log = s; _logUntil = _time + 2.5f; }

        bool MoveTo(Transform t, DigimonAnimator a, Vector3 goal, float speed, float dt)
        {
            Vector3 to = goal - t.position; to.y = 0;
            if (to.magnitude < 0.05f) { if (a != null && a.Slot != 0) a.SetSlot(0); return true; }
            t.position += to.normalized * Mathf.Min(to.magnitude, speed * dt);
            t.rotation = Quaternion.RotateTowards(t.rotation, _face(to), 600f * dt);
            if (a != null && a.Slot != 2) a.SetSlot(2);
            return false;
        }

        // -------------------------------------------------------------- per frame
        /// <summary>step = up/down, action = Cross (order list), triangle = item page, cancel = Triangle/back. Called every frame while Active (not while the bag is open).</summary>
        public void Tick(int step, bool action, bool triangle, bool cancel)
        {
            if (!Active) return;
            float dt = Time.deltaTime; _time += dt;
            Animate(Partner); Animate(Enemy);
            for (int i = _pops.Count - 1; i >= 0; i--) { _pops[i].T += dt; if (_pops[i].T > 1.2f) _pops.RemoveAt(i); }
            for (int i = _throws.Count - 1; i >= 0; i--)
            {
                _throws[i].T += dt;
                if (_throws[i].T >= 0.55f)
                {
                    var th = _throws[i]; _throws.RemoveAt(i);
                    if (th.Heal > 0) _pops.Add(new Pop { Text = th.Heal.ToString(), Pos = Partner.T.position + Vector3.up * 0.9f, Col = new Color(0.4f, 1f, 0.4f) });
                    if (th.Mana > 0) _pops.Add(new Pop { Text = th.Mana.ToString(), Pos = Partner.T.position + Vector3.up * 1.4f, Col = new Color(0.5f, 0.8f, 1f) });
                }
            }
            float inPhase = _time - _phaseStart;

            switch (_phase)
            {
                case Phase.Notice:   // the enemy gets angry
                    if (inPhase > 1.3f) SetPhase(Phase.Banner);
                    break;
                case Phase.Banner:   // BATTLE; everyone takes their place
                {
                    bool a = MoveTo(Enemy.T, Enemy.A, Enemy.Slot, 3.5f, dt);
                    bool b = MoveTo(Partner.T, Partner.A, Partner.Slot, 3.5f, dt);
                    bool c = _player == null || MoveTo(_player, _playerA, _playerSlot, 4.5f, dt);
                    if (a && b && c && inPhase > 1.5f)
                    {
                        Enemy.T.rotation = _face(Partner.T.position - Enemy.T.position);
                        Partner.T.rotation = _face(Enemy.T.position - Partner.T.position);
                        if (_player != null) _player.rotation = _face(Enemy.T.position - _player.position);
                        SetPhase(Phase.Fight);
                        Partner.NextAct = _time + Interval(Partner) * 0.7f;
                        Enemy.NextAct = _time + Interval(Enemy) * (0.9f + (float)_rng.NextDouble() * 0.6f);
                    }
                    break;
                }
                case Phase.Fight:
                    if (triangle && !_menuOpen) { if (OnOpenBag != null) OnOpenBag(); }
                    else if (_menuOpen)
                    {
                        if (step != 0) _sel = (_sel + step + _orders.Count) % _orders.Count;
                        if (cancel) _menuOpen = false;
                        else if (action) { _menuOpen = false; Choose(_sel); }
                    }
                    else if (action) { _menuOpen = true; _sel = Mathf.Max(0, _orders.IndexOf(_order)); }
                    if (_charging) { TickCharge(dt); TickSwings(dt); TickShots(dt); break; }
                    Gauge(Partner, dt); Gauge(Enemy, dt);
                    if (_time >= _retreatUntil) TickFighter(Partner, Enemy, dt); else MoveTo(Partner.T, Partner.A, _playerSlot - (_playerSlot - Partner.Slot).normalized * 1.5f, 4f, dt);
                    if (_retreatUntil > 0f && _time >= _retreatUntil) { _retreatUntil = 0f; Partner.T.rotation = _face(Enemy.T.position - Partner.T.position); }
                    TickFighter(Enemy, Partner, dt);
                    Drift(Partner, Enemy, dt, _time >= _retreatUntil); Drift(Enemy, Partner, dt, true);
                    TickSwings(dt); TickShots(dt);
                    if (_fleeAt > 0f && _time >= _fleeAt) TryFlee();
                    if (Enemy.Hp <= 0) Finish(true);
                    else if (Partner.Hp <= 0) Finish(false);
                    break;
                case Phase.Win:
                case Phase.Lose:
                    TickSwings(dt); TickShots(dt);
                    if (inPhase > 3f && (action || cancel)) End();
                    break;
                case Phase.Fled:
                    if (inPhase > 0.8f) End();
                    break;
            }
        }

        void SetPhase(Phase p) { _phase = p; _phaseStart = _time; }

        void End()
        {
            if (_phase == Phase.Win) { int[] idx = { 4, 5, 0, 1, 2, 3 }; for (int i = 0; i < 6; i++) _s.SetStat(idx[i], _before[i] + _gain[i]); _s.Money += _bits; if (Partner.A != null) Partner.A.SetSlot(0); }
            ClearShots();
            Active = false;
        }

        void Choose(int i)
        {
            string o = _orders[i];
            if (o == RunOrder)
            {
                if (NoFlee) { Log("You can't run away from this fight!"); return; }
                if (_fleeAt < 0f) { _fleeAt = _time + 1.2f; Log(RunOrder + "!"); }
                return;
            }
            if (o == Learned[2]) _retreatUntil = _time + 4f;                 // Get back!
            if (o == Learned[3]) { Log("There is no other target."); return; } // Change target! (only one enemy so far)
            _order = o; Partner.Guarding = o == Learned[4]; Log(o);
        }

        void TryFlee()
        {
            _fleeAt = -1f;
            float chance = Mathf.Clamp(0.4f + (Partner.Spd - Enemy.Spd) / 200f, 0.15f, 0.9f);
            if (_rng.NextDouble() < chance) { Fled = true; Sync(); ClearShots(); SetPhase(Phase.Fled); }
            else Log("Couldn't get away!");
        }

        void Animate(Fighter f) { if (f != null && f.A != null && f.AnimReset > 0f && _time >= f.AnimReset) { f.AnimReset = 0f; if (f.Hp > 0) f.A.SetSlot(0); } }

        void Play(Fighter f, int slot, float seconds)
        {
            if (f.A == null) return;
            if (f.A.HasSlot(slot)) { f.A.SetSlot(slot, true); f.AnimReset = _time + seconds; }
        }

        // the finishing-move gauge fills with time (a full gauge in about 100 s) and with damage taken (see Apply)
        void Gauge(Fighter f, float dt) { if (f.Hp > 0) f.Gauge = Mathf.Min(6f, f.Gauge + dt * 0.06f); }

        // fighters shuffle around so that a shot can miss
        void Drift(Fighter me, Fighter foe, float dt, bool allowed)
        {
            if (!allowed || me.Hp <= 0) return;
            foreach (var sw in _swings) if (sw.From == me) return;
            if (me.AnimReset > 0f) return;
            if (me.WanderWait > 0f) { me.WanderWait -= dt; if (me.A != null && me.A.Slot != 0) me.A.SetSlot(0); return; }
            Vector3 to = me.Wander - me.T.position; to.y = 0;
            if (to.magnitude < 0.1f)
            {
                Vector3 fwd = Vector3.Cross(Vector3.up, _right);
                me.Wander = me.Slot + _right * ((float)_rng.NextDouble() * 1.6f - 0.8f) + fwd * ((float)_rng.NextDouble() * 3f - 1.5f);
                me.WanderWait = 0.4f + (float)_rng.NextDouble() * 1.4f;
                return;
            }
            me.T.position += to.normalized * Mathf.Min(to.magnitude, 1.8f * dt);
            me.T.rotation = Quaternion.RotateTowards(me.T.rotation, _face(foe.T.position - me.T.position), 400f * dt);
            if (me.A != null && me.A.Slot != 2) me.A.SetSlot(2);
        }

        void TickFighter(Fighter me, Fighter foe, float dt)
        {
            if (me.Hp <= 0 || foe.Hp <= 0 || _time < me.NextAct) return;
            foreach (var sw in _swings) if (sw.From == me) return;                    // still swinging
            bool isPartner = me == Partner;
            if (isPartner && _order == Learned[4]) { me.NextAct = _time + Interval(me); me.Mp = Mathf.Min(me.MaxMp, me.Mp + me.MaxMp / 50); me.Guarding = true; Sync(); return; }
            me.Guarding = false;

            Tech tech = null;
            if (me.Gauge >= 6f && me.Finisher != null) { tech = me.Finisher; me.Gauge = 0f; }       // the full gauge: its own finishing move
            else if (me.Techs.Count > 0)
            {
                double want = isPartner ? (_order == Learned[0] ? 0.9 : _order == Learned[1] ? 0.0 : 0.5) : 0.35;   // all out / take it easy / your call
                var t = me.Techs[_rng.Next(me.Techs.Count)];
                if (_rng.NextDouble() < want && (!isPartner || me.Mp >= t.Mp)) { tech = t; if (isPartner) me.Mp -= t.Mp; }
            }
            if (isPartner && tech == null && _order == Learned[1]) me.Mp = Mathf.Min(me.MaxMp, me.Mp + me.MaxMp / 40);   // taking it easy lets MP come back
            me.NextAct = _time + Interval(me) * (0.9f + (float)_rng.NextDouble() * 0.3f);
            me.WanderWait = 0.2f; me.Wander = me.T.position;
            me.T.rotation = _face(foe.T.position - me.T.position);
            if (tech != null && tech.Finisher)
            {
                _banner = tech.Name; _bannerUntil = _time + 2.6f;
                if (isPartner) { _charging = true; _chargeT = 0f; _chargeFill = 0f; _chargeTech = tech; _chargeFoe = foe; return; }   // you charge it first (L1 + R1)
                StartSwing(me, foe, tech, 1f + (float)_rng.NextDouble() * 0.5f);      // the enemy's finisher is not charged by anyone
                return;
            }
            StartSwing(me, foe, tech, 1f);
        }

        void StartSwing(Fighter me, Fighter foe, Tech tech, float boost)
        {
            _swings.Add(new Swing { From = me, To = foe, Tech = tech, Start = me.T.position, Boost = boost });
            Play(me, tech != null && !tech.Melee ? TechSlot : AttackSlot, 0.9f);
            Sync();
        }

        // the partner's finishing move: a bar over it fills as you press L1 and R1 (Q and E); the fuller it is, the stronger the blow (up to double)
        bool _charging; float _chargeT, _chargeFill; Tech _chargeTech; Fighter _chargeFoe;
        const float ChargeSeconds = 2.4f;

        static int ChargePresses()
        {
            int n = 0;
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k != null) { if (k.qKey.wasPressedThisFrame) n++; if (k.eKey.wasPressedThisFrame) n++; }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Q)) n++;
            if (Input.GetKeyDown(KeyCode.E)) n++;
#endif
            return n;
        }

        void TickCharge(float dt)
        {
            _chargeT += dt;
            _chargeFill = Mathf.Clamp01(_chargeFill + ChargePresses() * 0.08f - dt * 0.12f);
            if (_chargeT >= ChargeSeconds)
            {
                _charging = false;
                StartSwing(Partner, _chargeFoe, _chargeTech, 1f + _chargeFill);
                Log(_chargeFill > 0.8f ? "Perfect!" : _chargeFill > 0.4f ? "Good!" : "");
            }
        }

        void TickSwings(float dt)
        {
            for (int i = _swings.Count - 1; i >= 0; i--)
            {
                var sw = _swings[i]; sw.T += dt;
                bool ranged = sw.Tech != null && !sw.Tech.Melee;
                Vector3 dir = sw.To.T.position - sw.Start; dir.y = 0;
                float dist = dir.magnitude; Vector3 d = dist > 0.01f ? dir / dist : Vector3.forward;
                if (!ranged)   // lunge in, hit, and back
                {
                    float go = Mathf.Clamp01(sw.T / 0.35f), back = Mathf.Clamp01((sw.T - 0.5f) / 0.35f);
                    sw.From.T.position = sw.Start + d * Mathf.Max(0f, dist - 1.3f) * (go - back);
                }
                if (!sw.Done && sw.T >= 0.35f)
                {
                    sw.Done = true;
                    if (ranged && sw.Tech.Wide) Apply(sw.From, sw.To, sw.Tech, sw.Boost);   // a wide technique covers the whole area: it cannot be dodged
                    else if (ranged) Fire(sw);
                    else
                    {
                        // the blow lands only if the target is still within reach where the lunge ends
                        float reach = Vector3.Distance(Flat(sw.From.T.position), Flat(sw.To.T.position));
                        if (reach <= 1.9f) Apply(sw.From, sw.To, sw.Tech, sw.Boost);
                        else Miss(sw.To);
                    }
                }
                if (sw.T >= 0.9f) { if (!ranged) sw.From.T.position = sw.Start; _swings.RemoveAt(i); }
            }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v; }

        void Fire(Swing sw)
        {
            // the shot flies at where the target is right now; it does not follow
            Vector3 from = Flat(sw.From.T.position), to = Flat(sw.To.T.position);
            Vector3 dir = (to - from).normalized;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
            float size = sw.Tech.Finisher ? 0.8f : 0.4f;
            go.transform.localScale = Vector3.one * size;
            if (_shotMat == null) _shotMat = new Material(Shader.Find("Sprites/Default"));
            var mr = go.GetComponent<MeshRenderer>(); mr.sharedMaterial = new Material(_shotMat) { color = sw.Tech.Col };
            Vector3 p = from + dir * 0.8f + Vector3.up * 0.9f;
            go.transform.position = p;
            _shots.Add(new Shot { From = sw.From, To = sw.To, Tech = sw.Tech, Pos = p, Dir = dir, Go = go, Boost = sw.Boost });
        }

        void TickShots(float dt)
        {
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var s = _shots[i];
                float step = (s.Tech.Finisher ? 10f : 8f) * dt;
                s.Pos += s.Dir * step; s.Travelled += step;
                if (s.Go != null) s.Go.transform.position = s.Pos;
                bool hit = Vector3.Distance(Flat(s.Pos), Flat(s.To.T.position)) < 0.85f;
                if (hit) { Apply(s.From, s.To, s.Tech, s.Boost); }
                if (hit || s.Travelled > 14f)
                {
                    if (!hit) Miss(s.To);
                    if (s.Go != null) UnityEngine.Object.Destroy(s.Go);
                    _shots.RemoveAt(i);
                }
            }
        }

        void ClearShots() { foreach (var s in _shots) if (s.Go != null) UnityEngine.Object.Destroy(s.Go); _shots.Clear(); }

        void Miss(Fighter target) { _pops.Add(new Pop { Text = "MISS", Pos = target.T.position + Vector3.up * 1.2f, Col = new Color(0.8f, 0.85f, 1f) }); }

        void Apply(Fighter a, Fighter d, Tech tech, float boost = 1f)
        {
            if (d.Hp <= 0) return;
            float mult = (tech != null ? tech.Mult : 0.45f) * boost;
            float v = a.Off * mult * (0.85f + (float)_rng.NextDouble() * 0.3f) * 100f / (100f + d.Def * 0.8f);
            if (d.Guarding) v *= 0.5f;
            int dmg = Mathf.Max(1, Mathf.RoundToInt(v));
            d.Hp = Mathf.Max(0, d.Hp - dmg);
            d.Gauge = Mathf.Min(6f, d.Gauge + dmg / (float)d.MaxHp * 6f);          // taking damage fills the finishing-move gauge
            _pops.Add(new Pop { Text = dmg.ToString(), Pos = d.T.position + Vector3.up * 0.9f, T = 0f, Col = Color.white });
            if (d.Hp > 0) Play(d, HitSlot, 0.6f);
            else Play(d, DownSlot, 3f);
            if (tech != null && tech.Stun && d.Hp > 0) { d.NextAct = Mathf.Max(d.NextAct, _time + 2.5f); _pops.Add(new Pop { Text = "stun", Pos = d.T.position + Vector3.up * 1.6f, Col = new Color(1f, 0.95f, 0.4f) }); }
            Sync();
        }

        void Sync() { _s.Stats[6] = (ushort)Mathf.Clamp(Partner.Hp, 0, 9999); _s.Stats[7] = (ushort)Mathf.Clamp(Partner.Mp, 0, 9999); }

        /// <summary>Items used from the bag during the fight change the stats; this picks them up and starts the boy's throw.</summary>
        public void ReadBack()
        {
            if (Partner == null) return;
            int dh = _s.Stats[6] - Partner.Hp, dm = _s.Stats[7] - Partner.Mp;
            if ((dh > 0 || dm > 0) && LastItem >= 0) _throws.Add(new Throw { Item = LastItem, Heal = Mathf.Max(0, dh), Mana = Mathf.Max(0, dm) });
            if (LastItem >= 0) LastItem = -1;
            Partner.Hp = _s.Stats[6]; Partner.Mp = _s.Stats[7]; Partner.MaxHp = Mathf.Max(1, _s.Stats[4]); Partner.MaxMp = _s.Stats[5];
        }

        void Finish(bool won)
        {
            Won = won;
            ClearShots();
            if (won)
            {
                SetPhase(Phase.Win);
                int lv = Enemy.Info != null ? Mathf.Max(1, Enemy.Info.Level) : 3;
                int[] bitsByLevel = { 10, 10, 30, 100, 300, 800, 2000, 4000 };
                _bits = bitsByLevel[Mathf.Clamp(lv, 0, 7)];
                int[] idx = { 4, 5, 0, 1, 2, 3 };
                for (int i = 0; i < 6; i++) { _before[i] = _s.Stats[idx[i]]; _gain[i] = 0; }
                int n = 3 + _rng.Next(0, 2);                                     // 3-4 values grow by 1 (in the first fight: HP, MP, Speed, Brain)
                var order = new List<int> { 0, 1, 2, 3, 4, 5 };
                for (int i = 0; i < n; i++) { int k = _rng.Next(order.Count); _gain[order[k]] = 1; order.RemoveAt(k); }
                _s.Happiness = Mathf.Min(100, _s.Happiness + 2);
                if (Partner.A != null) Partner.A.SetSlot(CheerSlot, true);
            }
            else
            {
                SetPhase(Phase.Lose);
                if (Partner.A != null) Partner.A.SetSlot(DownSlot, true);
            }
            Sync();
        }

        // -------------------------------------------------------------- drawing
        static Texture2D Disc()
        {
            if (_disc != null) return _disc;
            const int n = 48;
            _disc = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float dx = x - (n - 1) / 2f, dy = y - (n - 1) / 2f, d = Mathf.Sqrt(dx * dx + dy * dy);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(n / 2f - d)));
            }
            _disc.SetPixels32(px); _disc.Apply();
            return _disc;
        }

        static void DrawDisc(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Disc()); GUI.color = o; }

        static void Outlined(Rect r, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft)
        {
            var st = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = a, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
            st.normal.textColor = Color.black;
            foreach (var o in new[] { new Vector2(-2, 0), new Vector2(2, 0), new Vector2(0, -2), new Vector2(0, 2) }) GUI.Label(new Rect(r.x + o.x, r.y + o.y, r.width, r.height), s, st);
            st.normal.textColor = c; GUI.Label(r, s, st);
        }

        // icon of an order (from ETCDAT/BTLCOMND.TIM); null name = Your Call
        void OrderIcon(Rect r, string order, bool dim)
        {
            if (Icons == null) return;
            int col, row = 0; int frame = ((int)(Time.time * 2f)) % 2;
            if (order == CallOrder) col = BattleIcons.YourCall;
            else if (order == RunOrder) col = BattleIcons.RunAway;
            else if (order == Learned[0]) col = BattleIcons.Aggressive;
            else if (order == Learned[1]) col = BattleIcons.Calm;
            else if (order == Learned[2]) { col = BattleIcons.Retreat; row = 1; }
            else if (order == Learned[3]) col = BattleIcons.Eyes;
            else col = BattleIcons.Shout;
            var o = GUI.color; GUI.color = dim ? new Color(1, 1, 1, 0.35f) : Color.white;
            GUI.DrawTextureWithTexCoords(r, Icons.Sheet, Icons.Uv(col, row, frame));
            GUI.color = o;
        }

        public void OnGUI(Camera cam)
        {
            if (!Active || Partner == null) return;
            if (_phase == Phase.Notice && cam != null) AngerIcon(cam, Enemy.T.position + Vector3.up * 2.2f);
            UiKit.Begin();
            if (_phase == Phase.Banner) Banner(_time - _phaseStart);
            if (_phase != Phase.Notice) Hud();
            if (_phase == Phase.Fight)
            {
                if (_time < _bannerUntil) Outlined(new Rect(0, 250, UiKit.W, 90), _banner + "!", 72, new Color(1f, 0.55f, 0.15f), TextAnchor.MiddleCenter);
                if (_menuOpen) Menu();
                else if (_time < _logUntil) Outlined(new Rect(50, 880, 1300, 70), _log, 46, Color.white);
            }
            if (_phase == Phase.Win && _time - _phaseStart > 1.2f) ResultWindow();
            if (_phase == Phase.Lose && _time - _phaseStart > 1.2f)
            {
                var box = new Rect(300, 380, 815, 260);
                UiKit.Box(box, new Color(0.10f, 0.17f, 0.15f, 0.95f));
                UiKit.Text(new Rect(box.x + 50, box.y + 40, box.width - 80, 70), Partner.Name + " collapsed...", 52, UiKit.Yellow, TextAnchor.MiddleLeft);
                UiKit.Text(new Rect(box.x + 50, box.y + 120, box.width - 80, 70), "You carry it back to File City.", 46, Color.white, TextAnchor.MiddleLeft);
            }
            UiKit.End();
            if (cam != null)
            {
                if (_phase == Phase.Fight || _phase == Phase.Win)
                {
                    if (Enemy.Hp > 0) EnemyBar(cam);
                    if (_phase == Phase.Fight && _time >= _nextAsk && _player != null)
                    {
                        if (_time > _nextAsk + 1.6f) _nextAsk = _time + 8f;
                        else Floating(cam, _player.position + Vector3.up * 2.2f, "Your Call!", 36);
                    }
                }
                if (_charging && _phase == Phase.Fight)
                {
                    Vector3 sp = cam.WorldToScreenPoint(Partner.T.position + Vector3.up * 2.1f);
                    if (sp.z > 0)
                    {
                        float w = Screen.height * 0.22f, h = Screen.height * 0.03f;
                        var r = new Rect(sp.x - w / 2, Screen.height - sp.y, w, h);
                        UiKit.Fill(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), Color.black);
                        UiKit.Fill(r, new Color(0.25f, 0.27f, 0.3f));
                        UiKit.Fill(new Rect(r.x, r.y, r.width * _chargeFill, r.height), Color.Lerp(new Color(1f, 0.8f, 0.1f), new Color(1f, 0.25f, 0.1f), _chargeFill));
                        Outlined(new Rect(r.x - 40, r.y - h * 2.2f, r.width + 80, 40), "L1 + R1 (Q + E)!", 30, Color.white, TextAnchor.MiddleCenter);
                    }
                }
                foreach (var p in _pops) Floating(cam, p.Pos + Vector3.up * p.T * 0.9f, p.Text, 40, p.Col);
                foreach (var th in _throws)   // the boy throws the item to the partner
                {
                    if (_player == null || Items == null) continue;
                    float k = Mathf.Clamp01(th.T / 0.55f);
                    Vector3 w = Vector3.Lerp(_player.position, Partner.T.position, k) + Vector3.up * (1.2f + Mathf.Sin(k * Mathf.PI) * 1.3f);
                    Vector3 sp = cam.WorldToScreenPoint(w);
                    if (sp.z > 0) GUI.DrawTextureWithTexCoords(new Rect(sp.x - 24, Screen.height - sp.y - 24, 48, 48), Items.Sheet, Items.Uv(th.Item));
                }
            }
        }

        static void Floating(Camera cam, Vector3 world, string text, int size, Color? col = null)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0) return;
            Outlined(new Rect(sp.x - 150, Screen.height - sp.y - 25, 300, 50), text, size, col ?? Color.white, TextAnchor.MiddleCenter);
        }

        static void AngerIcon(Camera cam, Vector3 world)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0) return;
            float s = Screen.height * 0.07f, bob = Mathf.Sin(Time.time * 14f) * 4f;
            var r = new Rect(sp.x - s / 2, Screen.height - sp.y - s / 2 + bob, s, s);
            DrawDisc(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), new Color(0.35f, 0.05f, 0.1f));
            DrawDisc(r, new Color(0.9f, 0.2f, 0.3f));
            float e = s * 0.18f;
            UiKit.Fill(new Rect(r.x + s * 0.2f, r.y + s * 0.38f, e * 1.4f, e), Color.white);
            UiKit.Fill(new Rect(r.x + s * 0.8f - e * 1.4f, r.y + s * 0.38f, e * 1.4f, e), Color.white);
            UiKit.Fill(new Rect(r.x + s * 0.3f, r.y + s * 0.68f, s * 0.4f, e * 0.6f), Color.white);
        }

        void Banner(float t)
        {
            // the red BATTLE letters slam in, hold, and fade
            float scale = t < 0.25f ? Mathf.Lerp(3f, 1f, t / 0.25f) : 1f;
            float alpha = t < 1.2f ? 1f : Mathf.Clamp01(1f - (t - 1.2f) / 0.3f);
            var rc = new Rect(UiKit.W / 2 - 600 * scale, 330 - 80 * scale, 1200 * scale, 160 * scale);
            var st = new GUIStyle(GUI.skin.label) { fontSize = (int)(150 * scale), alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            st.normal.textColor = new Color(0.2f, 0f, 0.03f, alpha);
            GUI.Label(new Rect(rc.x + 6, rc.y + 6, rc.width, rc.height), "BATTLE", st);
            st.normal.textColor = new Color(0.8f, 0.1f, 0.15f, alpha); GUI.Label(rc, "BATTLE", st);
        }

        void Hud()
        {
            // partner HP / MP, top right
            DrawBar(new Rect(900, 40, 330, 34), Partner.MaxHp > 0 ? Partner.Hp / (float)Partner.MaxHp : 0f, new Color(0.95f, 0.3f, 0.4f), true);
            DrawBar(new Rect(900, 82, 330, 34), Partner.MaxMp > 0 ? Partner.Mp / (float)Partner.MaxMp : 0f, new Color(0.3f, 0.7f, 1f), false);
            Outlined(new Rect(1235, 34, 150, 46), Partner.Hp.ToString(), 40, Color.white, TextAnchor.MiddleRight);
            Outlined(new Rect(1235, 76, 150, 46), Partner.Mp.ToString(), 40, Color.white, TextAnchor.MiddleRight);
            // the orders the partner has learned, top left (the current one is lit)
            for (int i = 0; i < _orders.Count; i++)
            {
                var r = new Rect(40 + i * 100, 36, 92, 92);
                bool cur = _orders[i] == _order, locked = _orders[i] == RunOrder && NoFlee;
                UiKit.Fill(new Rect(r.x - 6, r.y - 6, r.width + 12, r.height + 12), cur ? new Color(1f, 0.85f, 0.1f) : new Color(0.1f, 0.1f, 0.12f, 0.6f));
                UiKit.Fill(r, new Color(0.12f, 0.17f, 0.2f));
                OrderIcon(new Rect(r.x + 6, r.y + 6, r.width - 12, r.height - 12), _orders[i], locked);
            }
            if (_phase == Phase.Fight) Outlined(new Rect(50, 160, 400, 60), "Your Call", 46, Color.white);
            // the finishing-move gauge of the partner: F, FI, FIN ... FINISH
            int letters = Mathf.Clamp(Mathf.FloorToInt(Partner.Gauge), 0, 6);
            if (letters > 0) Outlined(new Rect(930, 128, 300, 60), "FINISH".Substring(0, letters), 44, new Color(1f, 0.9f, 0.2f));
        }

        static void DrawBar(Rect r, float t, Color fill, bool heart)
        {
            DrawDisc(new Rect(r.x - 44, r.y - 4, 42, 42), heart ? new Color(0.95f, 0.25f, 0.35f) : new Color(0.3f, 0.75f, 1f));
            UiKit.Fill(r, new Color(0.08f, 0.08f, 0.1f));
            UiKit.Fill(new Rect(r.x + 3, r.y + 3, r.width - 6, r.height - 6), new Color(0.3f, 0.33f, 0.38f));
            UiKit.Fill(new Rect(r.x + 3, r.y + 3, (r.width - 6) * Mathf.Clamp01(t), r.height - 6), fill);
        }

        void EnemyBar(Camera cam)
        {
            Vector3 sp = cam.WorldToScreenPoint(Enemy.T.position + Vector3.up * 1.9f);
            if (sp.z <= 0) return;
            float w = Screen.height * 0.16f, h = Screen.height * 0.014f;
            var r = new Rect(sp.x - w / 2, Screen.height - sp.y, w, h);
            UiKit.Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(0.1f, 0.5f, 0.15f));
            UiKit.Fill(r, new Color(0.9f, 0.92f, 0.85f));
            UiKit.Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(Enemy.Hp / (float)Enemy.MaxHp), r.height), new Color(0.95f, 0.3f, 0.3f));
            Outlined(new Rect(r.xMax - 80, r.y - h * 3.2f, 90, 30), Enemy.Hp.ToString(), 24, Color.white, TextAnchor.MiddleRight);
        }

        void Menu()
        {
            int n = _orders.Count;
            var box = new Rect(40, 640, 700, 60 + n * 90);
            UiKit.Box(box, UiKit.PanelFill);
            for (int i = 0; i < n; i++)
            {
                string o = _orders[i];
                bool locked = o == RunOrder && NoFlee;
                Color c = i == _sel ? UiKit.Yellow : locked ? UiKit.Grey : o == _order ? UiKit.Cyan : Color.white;
                OrderIcon(new Rect(box.x + 30, box.y + 28 + i * 90, 76, 76), o, locked);
                UiKit.Text(new Rect(box.x + 125, box.y + 28 + i * 90, box.width - 150, 76), (i == _sel ? "> " : "   ") + o, 44, c, TextAnchor.MiddleLeft);
            }
        }

        void ResultWindow()
        {
            string[] names = { "HP", "MP", "Off", "Def", "Speed", "Brain" };
            bool grown = _time - _phaseStart > 2.2f;
            var box = new Rect(60, 60, 880, 600);
            UiKit.Box(box, new Color(0.12f, 0.2f, 0.16f, 0.95f));
            UiKit.Fill(new Rect(box.x + 600, box.y + 8, 5, box.height - 16), UiKit.Frame);
            for (int i = 0; i < 6; i++)
            {
                float y = box.y + 30 + i * 90;
                int v = _before[i] + (grown ? _gain[i] : 0);
                UiKit.Text(new Rect(box.x + 30, y, 200, 70), names[i], 52, UiKit.Yellow, TextAnchor.MiddleLeft);
                UiKit.Text(new Rect(box.x + 270, y - 8, 160, 60), v.ToString(), 48, Color.white, TextAnchor.MiddleRight);
                UiKit.Bar(new Rect(box.x + 270, y + 48, 300, 14), i < 2 ? v / 1500f : v / 999f, UiKit.Cyan);
                if (_gain[i] > 0) UiKit.Text(new Rect(box.x + 640, y, 220, 70), "^ " + _gain[i], 52, new Color(0.35f, 0.75f, 1f), TextAnchor.MiddleLeft);
            }
            var bits = new Rect(60, 690, 880, 120);
            UiKit.Box(bits, new Color(0.12f, 0.2f, 0.16f, 0.95f));
            UiKit.Text(new Rect(bits.x + 30, bits.y + 20, 300, 80), "Bits", 56, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Text(new Rect(bits.x + 360, bits.y + 20, 480, 80), (_s.Money + (grown ? _bits : 0)).ToString() + (grown ? "" : "   (+" + _bits + ")"), 56, Color.white, TextAnchor.MiddleRight);
        }
    }
}
