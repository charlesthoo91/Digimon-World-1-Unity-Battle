using System;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// A fight between the partner and a wild Digimon, played on the map in real time, laid out like the PS1 game (from a recording).
    /// Techniques: each Digimon's techniques, their numbers and their animations come from the disc. Special techniques are projectiles that fly
    /// at where the target was when they were fired, so a target that moves can be missed. Every Digimon has a finishing-move gauge (F-I-N-I-S-H)
    /// that fills with time and with damage taken; when full its next action is its own finishing move (Agumon: Pepper Breath).
    /// From the disc too: enemy numbers and techniques (map record), the damage formula, and learning a technique an enemy used.
    /// Our own (not decoded yet): stat gains, how often a fighter acts, what orders do, the look of each technique's effect.
    /// </summary>
    public sealed class Battle
    {
        public sealed class Tech
        {
            public int Id; public string Name; public int Mp, Power, Element, Status, StatusChance, Stagger; public bool Melee, Finisher, Wide, Self; public Color Col;
            public string Spec { get { string[] n = { "", "poison", "confuse", "stun", "flat" }; return Status >= 0 && Status < n.Length ? n[Status] : ""; } }
        }

        public sealed class Fighter
        {
            public Transform T; public DigimonAnimator A; public string Name;
            public DigimonStats Info;
            public int Off, Def, Spd, Brain, Hp, MaxHp, Mp, MaxMp;
            public List<Tech> Techs = new List<Tech>(); public Tech Finisher;     // the partner has at most 3 (the Techset)
            public int Finish, FinishMax;       // finishing gauge: +1 a frame, 6% on a hit, full at 3000 - Speed
            public int Energy = 100, Style;     // energy bar: + (Speed / 100 + 1) every 2 frames, minus the power of each technique; Style = how full it must be
            public int Wait, Stall, Bore, SelfUses, ConfuseFrames;
            public Tech[] Moves = new Tech[4]; public int[] Weights = new int[4];
            public Tech Pending; public Fighter PendingTarget;
            public int Cool, Flat, PoisonFrames; public bool Poisoned, Blind; public float Adjust; public Vector3 Scale;
            public float NextAct, AnimReset, StunUntil;
            public float ShownHp;               // the HP shown on screen: it drains smoothly after a hit (393, 379, 365 ...)
            public Vector3 Slot;
        }

        sealed class Swing { public Fighter From, To; public float T; public bool Done; public Tech Tech; public Vector3 Start; public float Boost = 1f, Release, Length; public bool Effect, Landed; }
        sealed class Shot { public Fighter From, To; public Tech Tech; public Vector3 Pos, Dir; public float Travelled; public GameObject Go; public float Boost = 1f; }
        sealed class Pop { public string Text; public Vector3 Pos; public float T; public Color Col; }
        sealed class Fx { public GameObject Go; public Material Mat; public Vector3 Vel; public float T, Life, S0, S1; public Color Col; }
        readonly List<Fx> _fx = new List<Fx>();
        sealed class Throw { public int Item; public float T; public int Heal, Mana; }

        enum Phase { Notice, Banner, Fight, Win, Lose, Fled }

        // animation slots, from the battle code: stance 33, walk 35, hit from the front 40 and from behind 41, win 42, a technique = 46 + its place in the move list.
        // collapse 43: played when HP reaches 0 (code 0x8005AFDC).
        public static int IdleSlot = 33, WalkSlot = 35, HitFrontSlot = 40, HitBackSlot = 41, CheerSlot = 42, DownSlot = 43, BlockSlot = 37, RunSlot = 36, StunSlot = 34, StallSlot = 19;

        // the game's order names (BTL_REL.BIN); their icons are the cells of row 0 of BTLCOMND.TIM in this same order
        const string RunOrder = "Run", AttackOrder = "Attack", CallOrder = "Your Call", ModerateOrder = "Moderate", DistanceOrder = "Distance", DefensiveOrder = "Defensive", ChangeOrder = "Change";
        static readonly string[] OrderNames = { RunOrder, AttackOrder, CallOrder, ModerateOrder, DistanceOrder, DefensiveOrder, ChangeOrder };
        static readonly string[] OrderShouts = { "Run!", "Attack!", "Your Call!", "Moderate!", "Distance!", "Defense!", "Change!" };

        public bool Active { get; private set; }
        public bool Won { get; private set; }
        public bool Fled { get; private set; }
        public bool NoFlee;                     // scripted fights (script variable 250 = 1): running away is locked
        public Fighter Partner, Enemy;           // Enemy = the one the partner is after; the others wait in Extras (up to three enemies, "Change target!")
        readonly List<Fighter> Extras = new List<Fighter>();
        public BattleIcons Icons; public ItemIcons Items;
        public int LastItem = -1;               // the room tells which bag item was just used

        readonly ScriptState _s;
        readonly Func<Vector3, Quaternion> _face;
        readonly System.Random _rng = new System.Random();
        Phase _phase; float _time, _phaseStart, _bannerUntil;
        string _banner = "";
        Transform _player; DigimonAnimator _playerA;
        List<Vector2Int> _route; Vector2Int _startTile; bool _boyDone; float _boyStart, _boyRadius;
        const int PlayerRunSlot = 3;
        public Func<Vector3, bool> Walkable;     // the room's walking grid
        readonly Dictionary<Transform, float> _still = new Dictionary<Transform, float>();
        readonly List<string> _orders = new List<string>();
        string _order = CallOrder;
        int _hover = 1, _shoutFrame = -1; string _shout;       // the order under the cursor; numbers count from 1 as the game's do (0 is the finishing move)
        public BattleArt Art;
        readonly List<Swing> _swings = new List<Swing>();
        readonly List<Shot> _shots = new List<Shot>();
        readonly List<Pop> _pops = new List<Pop>();
        readonly List<Throw> _throws = new List<Throw>();
        readonly int[] _before = new int[6], _gain = new int[6], _shown = new int[6], _left = new int[6];
        int _bitsLeft, _statWait; float _countAcc; bool _fanfare;
        public System.Action<int> Sfx;           // playSound(0, n)
        public EffectPlayer Effects; bool _quietHit;
        public System.Action OnStopMusic;
        void Snd(int n) { if (Sfx != null) Sfx(n); }
        int _bits, _enemyBits, _startHp, _used, _blocks, _bigHits;
        readonly List<int> _seen = new List<int>();     // the Tech page techniques the enemy used in this fight (the game keeps up to 12)
        string _learned, _dropped;
        public Func<int, string> ItemName;      // the room tells the names
        string _log = ""; float _logUntil;
        static Texture2D _disc; static Material _shotMat;

        public System.Action OnOpenBag;         // the room opens the bag (items work in battle: Triangle)

        public Battle(ScriptState state, Func<Vector3, Quaternion> face) { _s = state; _face = face; }

        // -------------------------------------------------------------- setup
        // the shot's colour by element: fire, battle, air, earth, ice, mech, filth
        static readonly Color[] ElementCol = { new Color(1f, 0.55f, 0.1f), new Color(1f, 0.8f, 0.5f), new Color(1f, 0.95f, 0.2f), new Color(0.5f, 0.9f, 0.3f), new Color(0.6f, 0.95f, 1f), new Color(0.85f, 0.9f, 1f), new Color(0.65f, 0.45f, 0.2f) };

        /// <summary>Technique n from the game's table. Finishing moves cost no MP (they come from the gauge).</summary>
        public static Tech MakeTech(int id, bool finisher)
        {
            var d = DigimonStatsCatalog.TechOf(id);
            if (d == null) return new Tech { Id = id, Name = "?", Col = Color.white };
            return new Tech
            {
                Id = id, Name = d.Name, Power = d.Power, Mp = finisher ? 0 : d.Mp, Element = d.Element, Stagger = d.HitFx, Status = d.Status, StatusChance = d.StatusChance,
                Melee = d.Range == 1, Wide = d.Range == 3, Self = d.Range == 4, Finisher = finisher, Col = ElementCol[Mathf.Clamp(d.Element, 0, ElementCol.Length - 1)]
            };
        }

        /// <summary>The partner's techniques: its Techset (at most 3, or the first 3 mastered when none is chosen). The enemy's come from its map record; both have their finishing move.</summary>
        void SetupTechs(Fighter f, DigimonStats info, MapDigimon data)
        {
            if (info == null) return;
            if (data == null)
            {
                for (int i = 0; i < info.Learnable.Count && f.Techs.Count < 3; i++)
                    if (_s.Techs[info.Learnable[i]] && (_s.TechSet.Count == 0 || _s.TechSet.Contains(info.Learnable[i]))) f.Techs.Add(MakeTech(info.Learnable[i], false));
                if (f.Techs.Count == 0 && info.Learnable.Count > 0) f.Techs.Add(MakeTech(info.Learnable[0], false));   // Agumon starts with Spit Fire
                for (int i = 0; i < f.Techs.Count; i++) f.Moves[i] = f.Techs[i];
            }
            else for (int k = 0; k < 4; k++)
            {
                int place = data.MoveSlots[k] - DigimonStats.FirstMoveSlot;
                f.Weights[k] = data.MoveChance[k];
                if (place < 0 || place >= info.Moves.Count) continue;
                var t = MakeTech(info.Moves[place], false);
                f.Moves[k] = t; f.Techs.Add(t);
            }
            if (info.SignatureId >= 0) f.Finisher = MakeTech(info.SignatureId, true);
        }

        /// <summary>right = the screen's right direction on the ground.</summary>
        public void Start(Transform player, DigimonAnimator playerAnim, Transform partner, DigimonAnimator partnerAnim, string partnerName, DigimonStats partnerInfo,
                          Transform enemy, DigimonAnimator enemyAnim, string enemyName, DigimonStats enemyInfo, MapDigimon enemyData, Vector3 right, bool noFlee, List<Vector2Int> route, Vector2Int startTile, float boyRadius)
        {
            NoFlee = noFlee;
            Extras.Clear();
            Partner = new Fighter
            {
                T = partner, A = partnerAnim, Name = partnerName, Info = partnerInfo,
                Off = _s.Stats[0], Def = _s.Stats[1], Spd = _s.Stats[2], Brain = _s.Stats[3],
                MaxHp = Mathf.Max(1, _s.Stats[4]), MaxMp = _s.Stats[5], Hp = _s.Stats[6], Mp = _s.Stats[7]
            };
            if (Partner.Hp <= 0) Partner.Hp = 1;
            SetupTechs(Partner, partnerInfo, null);
            Enemy = new Fighter
            {
                T = enemy, A = enemyAnim, Name = enemyName, Info = enemyInfo,
                Off = enemyData.Off, Def = enemyData.Def, Spd = enemyData.Spd, Brain = enemyData.Brain,
                MaxHp = Mathf.Max(1, enemyData.Hp), Hp = Mathf.Max(1, enemyData.Hp), MaxMp = enemyData.Mp, Mp = enemyData.Mp
            };
            _enemyBits = enemyData.Bits;
            SetupTechs(Enemy, enemyInfo, enemyData);
            _seen.Clear(); _learned = null; _dropped = null;
            foreach (var f in new[] { Partner, Enemy })
            {
                f.FinishMax = 3000 - f.Spd;
                f.Bore = f.Brain / 10 + 5;
                f.SelfUses = f.Brain < 400 ? f.Brain / 100 + 1 : f.Brain < 600 ? 4 : 5;
            }
            _wantFinish = false; _frame = 0; _acc = 0f; _used = 0; _blocks = 0; _bigHits = 0;

            Partner.ShownHp = Partner.Hp; Enemy.ShownHp = Enemy.Hp;
            _barFrames = 21; _lifeBarOn = false; _hudAcc = 0f;
            SetupOrders();
            _order = CallOrder; _hover = _orders.Count; _shoutFrame = -1;

            _route = route; _startTile = startTile; _boyRadius = boyRadius; _boyDone = false; _boyStart = 0f;       // a random fight: the boy goes back to the entrance he came in by
            _player = player; _playerA = playerAnim;
            Snd(0x10);
            Active = true; Won = false; Fled = false; _phase = Phase.Notice; _time = 0f; _phaseStart = 0f; _running = false; _bannerUntil = 0f;
            _swings.Clear(); _pops.Clear(); _throws.Clear(); ClearShots(); _log = ""; _startHp = Partner.Hp;
            Partner.Scale = partner.localScale; Enemy.Scale = enemy.localScale;
            enemy.rotation = _face(partner.position - enemy.position);
            Stand(Partner);
            Stand(Enemy);
            Partner.T.rotation = _face(Enemy.T.position - Partner.T.position);     // BTL_handleBattleIntro: nobody takes a place, the partner just turns to the enemy
            foreach (var f in new[] { Partner, Enemy }) if (f.A != null) f.A.BodyMovesItself = true;
        }

        // the order list by Brains, in the game's menu order (code 0x80056F4C). From 500 Brains Attack and Moderate go and the three Techset techniques take their place.
        void SetupOrders()
        {
            _orders.Clear();
            int b = Partner.Brain;
            _orders.Add(RunOrder);                                            // shown in a scripted fight too, but the cursor jumps over it
            if (b >= 300) _orders.Add(ChangeOrder);
            if (b >= 400) _orders.Add(DefensiveOrder);
            if (b >= 300) _orders.Add(DistanceOrder);
            if (b >= 500) { for (int i = Partner.Techs.Count - 1; i >= 0; i--) _orders.Add(Partner.Techs[i].Name); }
            else
            {
                if (b >= 200) _orders.Add(ModerateOrder);
                if (b >= 100) _orders.Add(AttackOrder);
            }
            _orders.Add(CallOrder);
        }

        void Log(string s) { _log = s; _logUntil = _time + 2.5f; }

        bool MoveTo(Transform t, DigimonAnimator a, Vector3 goal, float speed, float dt)
        {
            Vector3 to = goal - t.position; to.y = 0;
            if (to.magnitude < 0.05f) { if (a != null && a.Slot != 0) a.SetSlot(0); return true; }
            float go = a != null ? a.TakeForward() : speed * dt;
            if (a != null)              // its animation carries it; only one with no forward movement at all glides
            {
                float still;
                _still.TryGetValue(t, out still);
                still = go > 0f ? 0f : still + dt;
                _still[t] = still;
                if (still > 0.5f) go = speed * dt;
            }
            t.position += to.normalized * Mathf.Min(to.magnitude, go);
            t.rotation = Quaternion.RotateTowards(t.rotation, _face(to), 600f * dt);
            if (a != null && a.Slot != 2) a.SetSlot(2);
            return false;
        }

        // -------------------------------------------------------------- per frame
        /// <summary>side = left/right (moves the order cursor), action = Cross (gives the order), triangle = item page, cancel = back. Called every frame while Active (not while the bag is open).</summary>
        public void Tick(int side, bool action, bool triangle, bool cancel)
        {
            if (!Active) return;
            float dt = Time.deltaTime; _time += dt;
            if (Effects != null) Effects.Tick(dt, Camera.main);
            Animate(Partner); Animate(Enemy);
            foreach (var x in Extras) Animate(x);
            for (int i = _pops.Count - 1; i >= 0; i--) { _pops[i].T += dt; if (_pops[i].T > 1.2f) _pops.RemoveAt(i); }
            foreach (var f in new[] { Partner, Enemy })
            {
                if (f.A != null && f.A.Slot != WalkSlot && f.A.Slot != RunSlot && f.A.Slot != 2 && f.A.Slot != 4)      // the animation itself throws it back; it stays inside the fight
                {
                    Vector3 next = f.T.position - f.T.forward * f.A.TakeBodyMove();
                    if (InArea(next)) f.T.position = next;
                }
            }
            _hudAcc += dt;
            while (_hudAcc >= Frame)
            {
                _hudAcc -= Frame;
                DrainHp(Partner); DrainHp(Enemy);
                bool thrown = Enemy.A != null && (Enemy.A.Slot == HitFrontSlot || Enemy.A.Slot == HitBackSlot);
                if (!thrown && Enemy.ShownHp <= Enemy.Hp && _barFrames < 21) _barFrames++;
            }
            if (_phase == Phase.Fight && GameMenus.SelectKey()) _lifeBarOn = !_lifeBarOn;
            TickFx(dt);
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
                    TickBoy();
                    if (inPhase > 1.3f) SetPhase(Phase.Banner);
                    break;
                case Phase.Banner:   // BATTLE; everyone takes their place
                {
                    bool c = TickBoy();
                    bool wordDone = TickWord(dt);
                    if (c && wordDone)
                    {
                        Enemy.T.rotation = _face(Partner.T.position - Enemy.T.position);
                        Partner.T.rotation = _face(Enemy.T.position - Partner.T.position);
                        SetPhase(Phase.Fight);
                    }
                    break;
                }
                case Phase.Fight:
                    if (!_charging && SquarePressed() && Partner.Finisher != null && Partner.Finish >= Partner.FinishMax) { _wantFinish = true; Partner.Pending = null; Partner.Wait = 0; }
                    if (triangle) { if (OnOpenBag != null) OnOpenBag(); }
                    if (side != 0) { Snd(2); MoveHover(side); }
                    if (action) { Snd(3); if (_playerA != null) _playerA.SetSlot(14, true); Choose(_hover - 1); }
                    if (_charging) { TickCharge(dt); TickSwings(dt); TickShots(dt); break; }
                    _acc += dt;
                    while (_acc >= Frame) { _acc -= Frame; _frame++; FrameTick(Partner, Enemy); if (Enemy.Hp > 0) FrameTick(Enemy, Partner); foreach (var x in Extras) if (x.Hp > 0) FrameTick(x, Partner); if (_shoutFrame >= 0 && ++_shoutFrame > 20) _shoutFrame = -1; }
                    Move(Partner, Enemy, dt); Move(Enemy, Partner, dt);
                    foreach (var x in Extras) if (x.Hp > 0) Move(x, Partner, dt);
                    TickSwings(dt); TickShots(dt);
                    if (Enemy.Hp <= 0) { if (!NextEnemy()) Finish(true); }
                    else if (Partner.Hp <= 0) Finish(false);
                    break;
                case Phase.Win:
                    TickSwings(dt); TickShots(dt);
                    if (!_fanfare && inPhase >= 41 * Frame) { _fanfare = true; Snd(0x18); }
                    if (_fanfare && inPhase > 1.2f)
                    {
                        bool counting = _bitsLeft > 0 || HasLeft();
                        if (counting && (action || cancel)) SkipCounting();
                        else if (!counting && (action || cancel)) End();
                        else { _countAcc += dt; while (_countAcc >= Frame) { _countAcc -= Frame; CountFrame(); } }
                    }
                    break;
                case Phase.Lose:
                    TickSwings(dt); TickShots(dt);
                    if (inPhase > 3f && (action || cancel)) End();
                    break;
                case Phase.Fled:
                    if (inPhase > 0.8f) End();
                    break;
            }
        }

        bool HasLeft() { foreach (int v in _left) if (v > 0) return true; return false; }

        // the result boxes (battle_ui.c): after 100 frames one stat point a frame, then one Bit a frame, each with sound 0x16; 0x17 when the Bits are done
        void CountFrame()
        {
            if (_statWait > 0) { _statWait--; return; }
            for (int i = 0; i < 6; i++)
                if (_left[i] > 0) { Snd(0x16); _left[i]--; _shown[i]++; return; }
            if (_bitsLeft > 0) { Snd(0x16); if (--_bitsLeft == 0) Snd(0x17); }
        }

        void SkipCounting()     // Cross or Triangle: everything lands at once
        {
            for (int i = 0; i < 6; i++) { _shown[i] += _left[i]; _left[i] = 0; }
            if (_bitsLeft > 0) { _bitsLeft = 0; Snd(0x17); }
            _statWait = 0;
        }

        /// <summary>A second or third enemy of a scripted fight.</summary>
        public void AddEnemy(Transform t, DigimonAnimator a, string name, DigimonStats info, MapDigimon data)
        {
            var f = new Fighter
            {
                T = t, A = a, Name = name, Info = info, Off = data.Off, Def = data.Def, Spd = data.Spd, Brain = data.Brain,
                MaxHp = Mathf.Max(1, data.Hp), Hp = Mathf.Max(1, data.Hp), MaxMp = data.Mp, Mp = data.Mp, Scale = t.localScale
            };
            SetupTechs(f, info, data);
            f.FinishMax = 3000 - f.Spd; f.Bore = f.Brain / 10 + 5; f.SelfUses = f.Brain < 400 ? f.Brain / 100 + 1 : f.Brain < 600 ? 4 : 5;
            f.ShownHp = f.Hp;
            _enemyBits += data.Bits;
            t.rotation = _face(Partner.T.position - t.position);
            Stand(f);
            if (a != null) a.BodyMovesItself = true;
            Extras.Add(f);
        }

        // the partner turns to the next living enemy; false when there is none
        bool NextEnemy()
        {
            foreach (var x in Extras)
                if (x.Hp > 0) { Extras.Remove(x); Extras.Add(Enemy); Enemy = x; _barFrames = 0; return true; }
            return false;
        }

        void SetPhase(Phase p) { _phase = p; _phaseStart = _time; if (p == Phase.Banner) { Snd(0x11); StartWord(); } }

        void End()
        {
            if (_phase == Phase.Win) { int[] idx = { 4, 5, 0, 1, 2, 3 }; for (int i = 0; i < 6; i++) _s.SetStat(idx[i], _before[i] + _gain[i]); _s.Money += _bits; if (Partner.A != null) Partner.A.SetSlot(0); }
            ClearShots();
            Active = false;
            foreach (var f in new[] { Partner, Enemy }) if (f.A != null) f.A.BodyMovesItself = false;
            foreach (var f in Extras) if (f.A != null) f.A.BodyMovesItself = false;
        }

        // left moves on along the row, right back; Run is skipped in a scripted fight (main/battle_tick.c)
        void MoveHover(int side)
        {
            int last = _orders.Count;
            if (side < 0) { _hover++; if (NoFlee && _hover == 1) _hover++; if (_hover > last) _hover = NoFlee ? 2 : 1; }
            else { _hover--; if (NoFlee && _hover == 1) _hover--; if (_hover <= 0) _hover = last; }
        }

        void Choose(int i)
        {
            string o = _orders[i];
            if (o == RunOrder)
            {
                if (NoFlee) return;
                _running = true; _runFrames = 0; _order = RunOrder; Shout("Run!");
                return;
            }
            if (o == ChangeOrder) { Shout("Change!"); NextEnemy(); return; }
            _running = false;
            _order = o;
            int k = System.Array.IndexOf(OrderNames, o);
            Shout(k >= 0 ? OrderShouts[k] : o);
        }

        void Shout(string s) { _shout = s; _shoutFrame = 0; }

        void Animate(Fighter f) { if (f != null && f.A != null && f.AnimReset > 0f && _time >= f.AnimReset) { f.AnimReset = 0f; if (f.Hp > 0) Stand(f); } }

        // the battle stance (models without one use the normal idle)
        void Stand(Fighter f) { if (f.A != null) f.A.SetSlot(f.A.HasSlot(IdleSlot) ? IdleSlot : 0); }
        bool Standing(Fighter f) { return f.A.Slot == IdleSlot || f.A.Slot == 0; }

        void Play(Fighter f, int slot, float seconds)
        {
            if (f.A == null) return;
            if (f.A.HasSlot(slot)) { f.A.SetSlot(slot, true); f.AnimReset = _time + seconds; }
        }

        // ---- the fighters' AI, from BTL_REL.BIN. All counters run in frames at 20 per second.
        const float Frame = 1f / 20f;
        const int EmptyWait = 80, StallFrames = 100;
        float _acc; int _frame;
        bool _wantFinish;                                                  // the player chose the finishing move
        bool _running; int _runFrames; Camera _view;                       // Run: the partner goes to the boy and the fight ends after 41 frames
        static readonly int[] PowerRank = { 50, 20, 5 }, CostRank = { 50, 20, 10 }, ReachRank = { 10, 5, 0 };

        static int EffectBonus(int effect) { return effect == 2 ? 1 : effect == 5 ? 3 : effect == 10 ? 5 : effect == 15 ? 7 : effect == 20 ? 10 : 0; }
        static int FirstType(Fighter f) { return f.Info != null ? f.Info.Specials[0] : 255; }

        // code 0x8005D378: what makes a technique usable now
        bool Usable(Fighter me, Tech t) { return t != null && t.Id != 45 && me.Mp >= Cost(me, t, me == Partner) && (!t.Self || me.SelfUses > 0); }

        // code 0x8005D6E4: how full the energy bar must be before the technique starts, by the fighter's style
        static bool Ready(Fighter me, Tech t) { return me.Style == 2 ? me.Energy >= 100 : me.Style == 1 ? me.Energy == 100 || me.Energy >= t.Power : me.Energy > 0; }

        // runs once per game frame: the decompilation's BTL_tickPartnerAI / BTL_tickEnemyAI / BTL_tickFighterStates / BTL_tickStatusEffects
        void FrameTick(Fighter me, Fighter foe)
        {
            if (me.Hp <= 0) return;
            if (me.Finisher != null) me.Finish = Mathf.Min(me.FinishMax, me.Finish + 1);
            if (_frame % 20 == 0 && me.Bore > 0) me.Bore--;
            if (me.Poisoned && --me.PoisonFrames <= 0)
            {
                me.PoisonFrames = 100;
                int dmg = me.MaxHp * (_rng.Next(3) + 1) / 100;
                if (me == Enemy) _barFrames = 0;
                me.Hp = Mathf.Max(0, me.Hp - dmg);
                _pops.Add(new Pop { Text = dmg.ToString(), Pos = me.T.position + Vector3.up * 0.9f, Col = new Color(0.75f, 0.4f, 1f) });
                Sync();
            }
            if (me.Flat > 0 && --me.Flat == 0) me.T.localScale = me.Scale;
            if (me.ConfuseFrames > 0) me.ConfuseFrames--;
            if (me.Brain <= 300 && me.Stall == 0 && _frame % ((me.Brain / 2 + 1) * 20) == 0)
            {
                int chance = me == Partner ? 70 - _s.Discipline : (300 - me.Brain) / 4;
                if (_rng.Next(100) < chance) { me.Stall = StallFrames; me.Pending = null; }
            }
            if (me.Stall == 0 && _frame % 2 == 0) me.Energy = Mathf.Min(100, me.Energy + me.Spd / 100 + 1);
            if (me.Wait > 1) me.Wait--;
            if (me.Cool > 1) me.Cool--;
            if (me.Stall > 0) { me.Stall--; return; }
            if (foe.Hp <= 0 || _time < me.NextAct || _time < me.StunUntil || me.Flat > 0 || me.Pending != null || me.Wait > 1 || me.Cool > 1 || (me == Partner && _running)) return;
            foreach (var sw in _swings) if (sw.From == me) return;
            if (me == Partner && _wantFinish && me.Finisher != null) { _wantFinish = false; me.Pending = me.Finisher; me.PendingTarget = foe; return; }
            if (me != Partner && me.Finish >= me.FinishMax && me.Finisher != null) { me.Pending = me.Finisher; me.PendingTarget = foe; return; }
            me.Blind = me.ConfuseFrames > 0 && _rng.Next(10) < 7;       // a confused fighter mostly fires at nothing, in the way it happens to face
            if (me.Blind) me.T.rotation *= Quaternion.Euler(0f, _rng.Next(-45, 45), 0f);
            Tech t = me.ConfuseFrames > 0 ? PickAny(me) : me == Partner ? PickPartner(me, foe) : PickEnemy(me);
            if (t == null) { me.Wait = EmptyWait; return; }
            me.Pending = t;
            me.PendingTarget = foe;
        }

        Tech PickAny(Fighter me)
        {
            var can = new List<Tech>();
            foreach (var t in me.Moves) if (Usable(me, t)) can.Add(t);
            return can.Count > 0 ? can[_rng.Next(can.Count)] : null;
        }

        // Your Call (code 0x8006061C). Style 0, the one the partner has: technique weights by rank of power; empty slots take a little of the roll (and then it waits).
        Tech PickPartner(Fighter me, Fighter foe)
        {
            foreach (var t in me.Moves) if (t != null && t.Name == _order) return Usable(me, t) ? t : null;
            me.Style = _order == ModerateOrder ? 2 : 0;
            if (_order == AttackOrder) return PickBest(me, foe, true);
            if (_order == ModerateOrder) return PickBest(me, foe, false);
            int[] w = new int[3];
            bool any = false;
            for (int i = 0; i < 3; i++) if (Usable(me, me.Moves[i])) any = true;
            if (!any) return null;
            for (int i = 0; i < 3; i++)
            {
                var t = me.Moves[i];
                if (t == null) { w[i] += 5; continue; }
                if (!Usable(me, t)) continue;
                int rank = 0;
                foreach (var o in me.Moves) if (o != t && Usable(me, o) && o.Power > t.Power) rank++;
                w[i] = PowerRank[rank];
                if (t.Self) w[i] += me.Bore + EffectBonus(DigimonStatsCatalog.Effect(t.Element, FirstType(me)));
                else w[i] += EffectBonus(DigimonStatsCatalog.Effect(t.Element, FirstType(foe)));
            }
            int r = _rng.Next(Mathf.Max(1, w[0] + w[1] + w[2])), sum = 0;
            for (int i = 0; i < 3; i++) { if (w[i] == 0) continue; sum += w[i]; if (r < sum) return me.Moves[i]; }
            return null;
        }

        // Attack: the strongest technique, Moderate: the cheapest (code 0x8005EFD0, 0x8005F248). On a tie, the one best against the target; then random.
        Tech PickBest(Fighter me, Fighter foe, bool strongest)
        {
            var best = new List<Tech>();
            foreach (var t in me.Moves)
            {
                if (!Usable(me, t)) continue;
                if (best.Count > 0 && (strongest ? t.Power < best[0].Power : t.Mp > best[0].Mp)) continue;
                if (best.Count > 0 && (strongest ? t.Power > best[0].Power : t.Mp < best[0].Mp)) best.Clear();
                best.Add(t);
            }
            if (best.Count == 0) return null;
            int top = -1; var tied = new List<Tech>();
            foreach (var t in best)
            {
                int e = DigimonStatsCatalog.Effect(t.Element, FirstType(foe));
                if (e > top) { top = e; tied.Clear(); }
                if (e == top) tied.Add(t);
            }
            return tied[_rng.Next(tied.Count)];
        }

        // the enemy (code 0x80060328): weights from its map record; a weight on an empty slot stays in the roll and then it waits. It starts only with a full energy bar.
        Tech PickEnemy(Fighter me)
        {
            me.Style = _rng.Next(2);        // on screen: 0 or 1 at random
            int[] w = new int[4]; int total = 0; bool any = false;
            for (int i = 0; i < 4; i++)
            {
                var t = me.Moves[i];
                w[i] = me.Weights[i];
                if (t != null && !Usable(me, t)) w[i] = 0;
                if (t != null && t.Self) w[i] += me.Bore;
                if (t != null && Usable(me, t)) any = true;
                total += w[i];
            }
            if (!any || total <= 0) return null;
            int r = _rng.Next(total), sum = 0;
            for (int i = 0; i < 4; i++) { if (w[i] == 0) continue; sum += w[i]; if (r < sum) return me.Moves[i]; }
            return null;
        }

        // what a fighter does each moment: BTL_tickChargeState, BTL_tickCooldownState, BTL_tickQueuedMove, BTL_handlePartnerMoveCommand.
        // 40 frames of cooldown after every technique; Run, Distance and Defensive take over the partner completely, so with those orders it does not attack.
        void Move(Fighter me, Fighter foe, float dt)
        {
            if (me.Hp <= 0) return;
            foreach (var sw in _swings) if (sw.From == me) return;
            if (me.AnimReset > 0f || _time < me.NextAct) return;
            if (me.Flat > 0) return;
            if (_time < me.StunUntil) { if (me.A != null && me.A.Slot != StunSlot) me.A.SetSlot(StunSlot); return; }
            if (me.Stall > 0) { if (me.A != null && me.A.Slot != StallSlot) me.A.SetSlot(StallSlot); return; }
            bool hurt = me.Poisoned || me.Hp <= me.MaxHp / 5;                    // walks when poisoned or under 20% HP, else runs
            int walk = me.A == null ? 2 : hurt && me.A.HasSlot(WalkSlot) ? WalkSlot : me.A.HasSlot(RunSlot) ? RunSlot : 2;
            bool moving = me.A != null && (me.A.Slot == WalkSlot || me.A.Slot == RunSlot);
            float dSq = Mathf.Pow(Vector3.Distance(Flat(me.T.position), Flat(foe.T.position)) * 100f, 2f);
            float radii = (me.Info != null ? me.Info.Radius : 0) + (foe.Info != null ? foe.Info.Radius : 0);
            if (me.Cool > 1)
            {
                if (me.Style == 2) BackAway(me, foe, dt, walk); else Maintain(me, foe, dt, walk, dSq, radii, moving);
                return;
            }
            if (me == Partner)
            {
                if (_running)
                {
                    if (_player != null && !MoveTo(me.T, me.A, _player.position, 4.5f, dt)) { }
                    if (++_runFrames > 40) { Fled = true; Sync(); ClearShots(); SetPhase(Phase.Fled); }      // always works, 41 frames after the order
                    return;
                }
                if (_order == DefensiveOrder) { Hold(me, foe, dt); return; }
                if (_order == DistanceOrder)
                {
                    float keep = MaxReachSq(foe) + 640000f + (moving ? 160000f : 0f);
                    if (dSq < keep) BackAway(me, foe, dt, walk); else Hold(me, foe, dt);
                    return;
                }
            }
            var t = me.Pending;
            if (t != null && Ready(me, t))
            {
                if (!Usable(me, t)) { me.Pending = null; return; }
                var target = me.PendingTarget ?? foe;
                if (t.Self || me.Blind) { Begin(me, target, t); return; }
                Vector3 to = Flat(foe.T.position - me.T.position).normalized;
                if (t.Melee)     // BTL_tickMeleeAttack: right up to it
                {
                    if (dSq <= radii * radii) Begin(me, target, t); else Step(me, to, foe, dt, walk);
                    return;
                }
                float reach = DigimonStatsCatalog.TechOf(t.Id).Reach * 100f;     // BTL_tickRangedAttack: within 30% of the distance, and after 80 frames of trying it fires anyway
                float v = reach * reach + radii * radii;
                if (me.Adjust <= 80f / 20f && dSq > v * 1.3f) { me.Adjust += dt; Step(me, to, foe, dt, walk); }
                else if (me.Adjust <= 80f / 20f && dSq < v * 0.7f) { me.Adjust += dt; BackAway(me, foe, dt, walk); }
                else { me.Adjust = 0f; Begin(me, target, t); }
                return;
            }
            bool timed = me.Wait > 1;
            if (me.Style == 2) BackAway(me, foe, dt, walk);
            else if (me.Style == 0 ? timed : !timed) Maintain(me, foe, dt, walk, dSq, radii, moving);
            else Hold(me, foe, dt);
        }

        // BTL_maintainDistanceRange: contact range is both radii + 200 units
        void Maintain(Fighter me, Fighter foe, float dt, int walk, float dSq, float radii, bool moving)
        {
            float contact = (radii + 200f) * (radii + 200f);
            float near = contact + (moving ? 160000f : 0f), far = contact + (moving ? 320000f : 480000f);
            if (dSq < near) BackAway(me, foe, dt, walk);
            else if (dSq > far) Step(me, Flat(foe.T.position - me.T.position).normalized, foe, dt, walk);
            else Hold(me, foe, dt);
        }

        // BTL_backAwayFromTarget: straight away; at the edge of the fight it turns a quarter, else stands
        void BackAway(Fighter me, Fighter foe, float dt, int walk)
        {
            Vector3 away = Flat(me.T.position - foe.T.position).normalized, side = Vector3.Cross(Vector3.up, away);
            if (InArea(me.T.position + away * 0.2f)) Step(me, away, foe, dt, walk);
            else if (InArea(me.T.position + side * 0.2f)) Step(me, side, foe, dt, walk);
            else if (InArea(me.T.position - side * 0.2f)) Step(me, -side, foe, dt, walk);
            else Hold(me, foe, dt);
        }

        // the fight stays inside a screen area of 280 x 200 of the PS1's 320 x 240 (entityCheckCombatArea)
        bool InArea(Vector3 p)
        {
            if (_view == null) return true;
            Vector3 v = _view.WorldToViewportPoint(p);
            return Mathf.Abs(v.x - 0.5f) <= 280f / 320f / 2f && Mathf.Abs(v.y - 0.5f) <= 200f / 240f / 2f;
        }

        void Hold(Fighter me, Fighter foe, float dt)
        {
            if (me.A != null && !Standing(me)) Stand(me);
            me.T.rotation = Quaternion.RotateTowards(me.T.rotation, _face(foe.T.position - me.T.position), 400f * dt);
        }

        // the longest reach among a fighter's techniques, squared, in game units (BTL_getMoveWithHighestDistance)
        static float MaxReachSq(Fighter f)
        {
            float best = 0f;
            foreach (var t in f.Moves)
            {
                var d = t != null ? DigimonStatsCatalog.TechOf(t.Id) : null;
                if (d != null) best = Mathf.Max(best, d.Reach * d.Reach * 10000f);
            }
            return best;
        }

        void Step(Fighter me, Vector3 dir, Fighter foe, float dt, int walk)
        {
            me.T.position += dir * (me.A != null ? me.A.TakeForward() : 0f);      // the animation carries it: one step, one step sound
            me.T.rotation = Quaternion.RotateTowards(me.T.rotation, _face(dir), 400f * dt);
            if (me.A != null && me.A.Slot != walk) me.A.SetSlot(walk);
        }

        // the partner's MP cost: 5, 10, 15, 20% less at 700, 800, 900, 999 Brains
        static int Cost(Fighter me, Tech t, bool partner)
        {
            int c = t.Mp;
            if (partner && me.Brain >= 700) c = me.Brain >= 999 ? c * 4 / 5 : me.Brain >= 900 ? c * 17 / 20 : me.Brain >= 800 ? c * 9 / 10 : c * 19 / 20;
            return c;
        }

        void Begin(Fighter me, Fighter target, Tech t)
        {
            me.Pending = null;
            if (!me.Blind) me.T.rotation = _face(target.T.position - me.T.position);
            if (me == Partner || !NoFlee) me.Mp -= Cost(me, t, me == Partner);     // enemies in scripted fights pay nothing
            if (t.Self) me.SelfUses--;
            me.Energy = Mathf.Max(-155, me.Energy - t.Power);
            if (me != Partner && t.Id < 58 && !_seen.Contains(t.Id) && _seen.Count < 12) _seen.Add(t.Id);
            if (t.Finisher)
            {
                me.Finish = 0; _banner = t.Name; _bannerUntil = _time + 2.6f;
                if (me == Partner) { _charging = true; _chargeT = 0f; _chargeCount = 0; _chargeTech = t; _chargeFoe = target; ShowRing(true); }
                else StartSwing(me, target, t, 1f + _rng.Next(0, 101) / 100f);
                return;
            }
            StartSwing(me, target, t, 1f);
        }

        void StartSwing(Fighter me, Fighter foe, Tech tech, float boost)
        {
            // the technique's own animation: slot 46 + its place in this Digimon's move list; it lets go at the animation's last sound event
            int slot = me.Info != null ? me.Info.MoveSlot(tech.Id) : -1;
            bool ranged = !tech.Melee;
            float release = me.A != null ? me.A.ReleaseOf(slot) : -1f, length = me.A != null ? me.A.SecondsOf(slot) : 0f;
            if (release < 0f) release = ranged ? 0.6f : 0.35f;
            length = Mathf.Max(length, release + 0.3f);
            var swing = new Swing { From = me, To = foe, Tech = tech, Start = me.T.position, Boost = boost, Release = release, Length = length };
            _swings.Add(swing);
            StartEffect(swing);
            Play(me, slot, length);
            Sync();
        }

        // the partner's finishing move: 80 frames in which L1 and R1 (Q and E) fill a bar; from just over half it multiplies the blow, up to double
        bool _charging; float _chargeT; int _chargeCount; Tech _chargeTech; Fighter _chargeFoe;
        const float ChargeSeconds = 80f / 20f;

        // Square (X on the keyboard): fires the finishing move once FINISH!! shows
        static bool SquarePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            return k != null && k.xKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.X);
#else
            return false;
#endif
        }

        // the yellow ring on the ground around the partner while the bar fills (reference/4.mp4); our drawing, the game's effect art is not decoded
        GameObject _ring; static Texture2D _ringTex;

        void ShowRing(bool on)
        {
            if (_ring != null) { UnityEngine.Object.Destroy(_ring); _ring = null; }
            if (!on) return;
            if (_ringTex == null)
            {
                const int n = 128;
                _ringTex = new Texture2D(n, n, TextureFormat.RGBA32, false);
                for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x - n / 2f, y - n / 2f).magnitude / (n / 2f);
                    float a = d > 1f ? 0f : Mathf.Clamp01(1f - Mathf.Abs(d - 0.8f) / 0.2f) + 0.25f * (1f - d);
                    _ringTex.SetPixel(x, y, new Color(1f, 1f, 0.7f, Mathf.Clamp01(a)));
                }
                _ringTex.Apply();
            }
            _ring = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var c = _ring.GetComponent<Collider>(); if (c != null) UnityEngine.Object.Destroy(c);
            if (_shotMat == null) _shotMat = new Material(Shader.Find("Sprites/Default"));
            _ring.GetComponent<MeshRenderer>().sharedMaterial = new Material(_shotMat) { mainTexture = _ringTex };
            _ring.transform.position = Partner.T.position + Vector3.up * 0.05f;
            _ring.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _ring.transform.localScale = Vector3.one * 3f;
        }

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
            _chargeCount = Mathf.Min(80, _chargeCount + ChargePresses() * 2);     // each press of L1 or R1 adds 2, up to 80 (code 0x80062148)
            if (_chargeT >= ChargeSeconds)
            {
                _charging = false;
                ShowRing(false);
                StartSwing(Partner, _chargeFoe, _chargeTech, _chargeCount >= 41 ? _chargeCount / 40f : 1f);
            }
        }

        void TickSwings(float dt)
        {
            for (int i = _swings.Count - 1; i >= 0; i--)
            {
                var sw = _swings[i]; sw.T += dt;
                bool ranged = !sw.Tech.Melee;
                if (!sw.Done && sw.T >= sw.Release)
                {
                    sw.Done = true;
                    if (ranged && sw.Tech.Self) Buff(sw.From, sw.Tech);
                    else if (sw.Effect) { }      // its program lands the hit
                    else if (ranged && sw.Tech.Wide && !sw.From.Blind) Apply(sw.From, sw.To, sw.Tech, sw.Boost);   // a wide technique covers the whole area: it cannot be dodged
                    else if (ranged) Fire(sw);
                    else
                    {
                        // the blow lands only if the target is still within the distance it was started from
                        float radii = (sw.From.Info != null ? sw.From.Info.Radius : 0) + (sw.To.Info != null ? sw.To.Info.Radius : 0);
                        if (Vector3.Distance(Flat(sw.From.T.position), Flat(sw.To.T.position)) * 100f <= radii * 1.3f) Apply(sw.From, sw.To, sw.Tech, sw.Boost);
                        else Miss(sw.To);
                    }
                }
                if (sw.T >= Mathf.Max(sw.Length, sw.Release + 0.5f) && (!sw.Effect || sw.Landed || sw.T > sw.Release + 4f))
                {
                    if (sw.Effect && !sw.Landed) Miss(sw.To);
                    _swings.RemoveAt(i);
                    sw.From.Blind = false;
                    sw.From.Cool = 40;                                           // BTL_tickFighterAction: 40 frames of cooldown, +4% finishing gauge, one attack counted
                    if (sw.From == Partner) _used++;
                    AddFinish(sw.From, sw.From.FinishMax * 2 / 50);
                }
            }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v; }

        // BTL_startEFE: the technique's own effect from the disc. Ranged ones then decide the hit themselves; a blow by hand still lands with its animation.
        void StartEffect(Swing sw)
        {
            if (Effects == null || sw.Tech.Self) return;
            Func<Fighter, EffectActor> actor = f => new EffectActor { T = f.T, A = f.A, Type = f.Info != null ? f.Info.Type : 0, Radius = f.Info != null ? f.Info.Radius : 120, Height = f.Info != null ? f.Info.Height : 200 };
            bool ok = false;
            try
            {
                ok = Effects.Start(sw.Tech.Id, actor(sw.From), actor(sw.To), at =>
                {
                    if (sw.Tech.Melee || sw.Landed) return;
                    sw.Landed = true;
                    _quietHit = true;
                    Apply(sw.From, sw.To, sw.Tech, sw.Boost);
                    _quietHit = false;
                });
            }
            catch (Exception e) { Debug.LogWarning("Effect of " + sw.Tech.Name + ": " + e.Message); }
            sw.Effect = ok && !sw.Tech.Melee;
        }

        void Fire(Swing sw)
        {
            // the shot flies at where the target is right now; it does not follow
            Vector3 from = Flat(sw.From.T.position), to = Flat(sw.To.T.position);
            Vector3 dir = sw.From.Blind ? Flat(-sw.From.T.forward).normalized : (to - from).normalized;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
            // a soft flame, 0.8 m long and 0.45 m wide in the recording (53.2 s, against Agumon's 2.5 m height); finishers are not measured
            go.transform.localScale = sw.Tech.Finisher ? Vector3.one * 0.8f : new Vector3(0.45f, 0.45f, 0.8f);
            go.transform.rotation = Quaternion.LookRotation(dir);
            if (_shotMat == null) _shotMat = new Material(Shader.Find("Sprites/Default"));
            var mr = go.GetComponent<MeshRenderer>(); mr.sharedMaterial = new Material(_shotMat) { color = sw.Tech.Finisher ? sw.Tech.Col : Color.Lerp(sw.Tech.Col, new Color(1f, 0.92f, 0.6f), 0.6f) };
            Vector3 p = from + dir * 0.8f + Vector3.up * 0.9f;
            go.transform.position = p;
            _shots.Add(new Shot { From = sw.From, To = sw.To, Tech = sw.Tech, Pos = p, Dir = dir, Go = go, Boost = sw.Boost });
        }

        void TickShots(float dt)
        {
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var s = _shots[i];
                float step = (s.Tech.Finisher ? 10f : 18f) * dt;     // 3.6 m in 0.2 s in the recording
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

        void ClearShots()
        {
            ShowRing(false);
            if (Effects != null) Effects.Clear();
            foreach (var s in _shots) if (s.Go != null) UnityEngine.Object.Destroy(s.Go);
            _shots.Clear();
            foreach (var f in _fx) { if (f.Go != null) UnityEngine.Object.Destroy(f.Go); }
            _fx.Clear();
        }

        // a ball of light or a spark (used for hit effects)
        void NewFx(Vector3 pos, Color col, float s0, float s1, float life, Vector3 vel)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var c = go.GetComponent<Collider>(); if (c != null) UnityEngine.Object.Destroy(c);
            if (_shotMat == null) _shotMat = new Material(Shader.Find("Sprites/Default"));
            var mat = new Material(_shotMat) { color = col };
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.transform.position = pos; go.transform.localScale = Vector3.one * s0;
            _fx.Add(new Fx { Go = go, Mat = mat, Vel = vel, Life = life, S0 = s0, S1 = s1, Col = col });
        }

        // the hit in the recording: a white-yellow flash, then yellow sparks flying out for half a second
        void Burst(Vector3 pos, Color tint, float size, bool big)
        {
            NewFx(pos, new Color(1f, 0.97f, 0.8f, 1f), 3.4f * size, 0.9f * size, 0.22f, Vector3.zero);    // 3.5 m wide at first, gone in about 0.2 s (53.4-53.6 s)
            int n = big ? 34 : 7;     // about 35 sparks of 0.3 m, spreading about 2.5 m in 0.6 s (33.9 and 53.8 s)
            for (int i = 0; i < n; i++)
            {
                Vector3 v = UnityEngine.Random.onUnitSphere; v.y = Mathf.Abs(v.y) * 0.8f + 0.2f;
                NewFx(pos, Color.Lerp(new Color(1f, 0.9f, 0.45f), tint, 0.25f), 0.32f * size, 0.2f, 0.7f, v * UnityEngine.Random.Range(1.5f, 4f));
            }
        }

        void TickFx(float dt)
        {
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var f = _fx[i]; f.T += dt;
                float k = f.T / f.Life;
                if (k >= 1f) { if (f.Go != null) UnityEngine.Object.Destroy(f.Go); _fx.RemoveAt(i); continue; }
                f.Vel.y -= 4f * dt;
                f.Go.transform.position += f.Vel * dt;
                f.Go.transform.localScale = Vector3.one * Mathf.Lerp(f.S0, f.S1, k);
                var c = f.Col; c.a = 1f - k * k; f.Mat.color = c;
            }
        }

        void Miss(Fighter target) { _pops.Add(new Pop { Text = "MISS", Pos = target.T.position + Vector3.up * 1.2f, Col = new Color(0.8f, 0.85f, 1f) }); }

        // the game's damage (code 0x8005BEBC). Element scale: the technique's element against each of the target's three types, 10 = normal.
        // Technique: (power + power x (Off - Def) / 500) x scale / 30, the difference kept within 500. Finishing move: (Off + power) x scale / 30, then the charge.
        // Both then x 0.9 to 1.1.
        float Damage(Fighter a, Fighter d, Tech tech, float boost)
        {
            int scale = 0;
            for (int i = 0; i < 3; i++) scale += d.Info != null ? DigimonStatsCatalog.Effect(tech.Element, d.Info.Specials[i]) : 10;
            int def = tech.Id == 0x2D ? d.Def * 3 / 10 : d.Def;                       // Counter counts only 30% of Defense
            float v = tech.Finisher ? a.Off + tech.Power : tech.Power + tech.Power * Mathf.Clamp(a.Off - def, -500, 500) / 500f;
            return v * scale / 30f * boost * (0.9f + _rng.Next(0, 21) / 100f);
        }

        // the game's stat gains (code 0x800ECEE8), HP, MP, Off, Def, Speed, Brains. With one enemy the scale is 10 (12 and 16 for two and three).
        // An enemy value at or above yours: rounded up enemy / yours. Below: a chance of enemy x 100 / yours percent for +1.
        // A stat still at 0 gets a second chance in percent: HP = damage taken as % of max HP; MP and Off = techniques used x 10; Def = hits of 20%+ taken x 10;
        // Speed = half the damage % + blocks x 10; Brains = (techniques + big hits) x 5.
        void StatGains()
        {
            int[] mine = { Partner.MaxHp, Partner.MaxMp, Partner.Off, Partner.Def, Partner.Spd, Partner.Brain };
            int[] foe = { Enemy.MaxHp, Enemy.MaxMp, Enemy.Off, Enemy.Def, Enemy.Spd, Enemy.Brain };
            foreach (var x in Extras)
            {
                int[] v = { x.MaxHp, x.MaxMp, x.Off, x.Def, x.Spd, x.Brain };
                for (int i = 0; i < 6; i++) if (v[i] > foe[i]) foe[i] = v[i];
            }
            int scale = Extras.Count == 0 ? 10 : Extras.Count == 1 ? 12 : 16;     // table 0x801344D0 by enemy count
            int hurt = (_startHp - Partner.Hp) * 100 / Partner.MaxHp;
            for (int i = 0; i < 6; i++)
            {
                int own = mine[i] * 10; if (own == 0) own = 10;
                if (foe[i] >= mine[i]) _gain[i] = (foe[i] * scale + own - 1) / own;
                else if (_rng.Next(100) < foe[i] * scale * 100 / own) _gain[i] = 1;
                if (_gain[i] != 0) continue;
                int second = i == 0 ? hurt : i == 1 || i == 2 ? _used * 10 : i == 3 ? _bigHits * 10 : i == 4 ? hurt / 2 + _blocks * 10 : (_used + _bigHits) * 5;
                if (_rng.Next(100) < second) _gain[i] = 1;
            }
        }

        // a technique the enemy used in this fight can be learned: it must be on the partner's list and match one of its three element types; the table gives the chance
        void TryLearn()
        {
            var info = Partner.Info; if (info == null) return;
            var can = new List<int>();
            foreach (int t in _seen)
            {
                var d = DigimonStatsCatalog.TechOf(t);
                int type = d != null ? System.Array.IndexOf(info.Specials, d.Element) : -1;
                if (_s.Techs[t] || type < 0 || !info.Moves.Contains(t)) continue;
                if (_rng.Next(100) < DigimonStatsCatalog.LearnChance(t, type)) can.Add(t);
            }
            if (can.Count == 0) return;
            int got = can[_rng.Next(can.Count)];
            _s.Techs[got] = true;
            if (got == 44 || got == 48) { _s.Techs[44] = true; _s.Techs[48] = true; }   // the same technique under two numbers
            if (got == 55 || got == 57) { _s.Techs[55] = true; _s.Techs[57] = true; }
            _learned = DigimonStatsCatalog.NameOf(got);
        }

        // the block roll (code 0x8005BBE8). No block at all: a target below Rookie, stunned, flat or not listening, with an empty energy bar, in the middle of a technique, or against a finishing move.
        // Else it blocks unless a roll falls under Accuracy - (Accuracy / 2) x (target Speed - attacker Speed / 10) / 999; a target standing in its stance gets 6/5 of that reduction.
        bool Blocks(Fighter a, Fighter d, Tech tech)
        {
            if (tech.Finisher || (d.Info != null && d.Info.Level < 3) || _time < d.StunUntil || d.Stall > 0 || d.Flat > 0 || d.Energy <= 0) return false;
            foreach (var sw in _swings) if (sw.From == d) return false;
            var row = DigimonStatsCatalog.TechOf(tech.Id);
            if (row == null) return false;
            int off = row.Accuracy / 2 * (d.Spd - a.Spd / 10) / 999;
            if (d.A != null && (d.A.Slot == 33 || d.A.Slot == 34)) off = off * 6 / 5;
            return _rng.Next(100) >= Mathf.Clamp(row.Accuracy - off, 0, 100);
        }

        // a block (code 0x8005CADC): the pose of slot 37 facing the attacker. A short-range blow still does 10-30% of its damage; a long-range one nothing.
        // The finishing gauges gain here: 6% for the blocker, 8% for the attacker.
        void Block(Fighter a, Fighter d, Tech tech, float v)
        {
            d.T.rotation = _face(a.T.position - d.T.position);
            Play(d, BlockSlot, d.A != null ? d.A.SecondsOf(BlockSlot) : 0f);
            NewFx(d.T.position + Vector3.up * 0.8f, new Color(1f, 0.97f, 0.8f, 1f), 0.2f, 0.7f, 0.2f, Vector3.zero);
            AddFinish(d, d.FinishMax * 3 / 50);
            AddFinish(a, a.FinishMax * 4 / 50);
            if (d == Partner) _blocks++;
            if (!tech.Melee) return;
            int chip = Mathf.Max(1, Mathf.RoundToInt(v * (10 + _rng.Next(21)) / 100f));
            d.Hp = Mathf.Max(0, d.Hp - chip);
            if (d == Enemy) _barFrames = 0;
            _pops.Add(new Pop { Text = chip.ToString(), Pos = d.T.position + Vector3.up * 0.9f, T = 0f, Col = Color.white });
            Sync();
        }

        void AddFinish(Fighter f, int amount) { if (f.Finisher != null) f.Finish = Mathf.Min(f.FinishMax, f.Finish + amount); }

        // self moves (BTL_applyBuffMove): Muscle Charge, War Cry, Mass Morph, Aqua Magic, Full Potential raise stats by a share of their value, up to 999
        void Buff(Fighter f, Tech t)
        {
            int off = 0, def = 0, spd = 0;
            switch (t.Id)
            {
                case 41: off = f.Off * 3 / 10; break;
                case 42: off = f.Off / 10; def = f.Def * 5 / 100; spd = f.Spd / 10; break;
                case 34: def = f.Def / 5; spd = f.Spd / 10; break;
                case 21: off = f.Off * 7 / 100; def = f.Def * 8 / 100; spd = f.Spd * 7 / 100; break;
                case 30: off = f.Off / 4; def = f.Def * 3 / 20; spd = f.Spd * 3 / 20; break;
            }
            f.Off = Mathf.Min(999, f.Off + off); f.Def = Mathf.Min(999, f.Def + def); f.Spd = Mathf.Min(999, f.Spd + spd);
            int shown = off + def + spd;
            if (shown > 0) _pops.Add(new Pop { Text = "+" + shown, Pos = f.T.position + Vector3.up * 1.2f, Col = new Color(1f, 0.7f, 0.2f) });
        }

        void Apply(Fighter a, Fighter d, Tech tech, float boost = 1f)
        {
            if (d.Hp <= 0) return;
            float v = Damage(a, d, tech, boost);
            if (a != d && Blocks(a, d, tech)) { Block(a, d, tech, v); return; }
            int dmg = Mathf.Max(1, Mathf.RoundToInt(v));
            d.Hp = Mathf.Max(0, d.Hp - dmg);
            if (d == Enemy) _barFrames = 0;
            if (d == Partner && dmg * 100 / Partner.MaxHp >= 20) _bigHits++;
            _pops.Add(new Pop { Text = dmg.ToString(), Pos = d.T.position + Vector3.up * 0.9f, T = 0f, Col = Color.white });
            Vector3 away = Flat(d.T.position - a.T.position); if (away.sqrMagnitude < 0.01f) away = Vector3.left;
            if (d.Hp > 0)
            {
                // the game picks the reaction by which side was hit; the fighter can do nothing until it ends. The model's front is -Z.
                bool front = Vector3.Dot(d.T.forward, away) >= 0f;
                int slot = front ? HitFrontSlot : HitBackSlot;
                d.T.rotation = _face(front ? -away : away);         // it turns to the blow (or away from it) and the reaction carries it from there
                // frozen for the technique's byte +7 in frames, then the reaction plays out; a hit stops a technique that has not let go yet
                for (int i = _swings.Count - 1; i >= 0; i--) if (_swings[i].From == d && !_swings[i].Done) _swings.RemoveAt(i);
                float length = (d.A != null ? d.A.SecondsOf(slot) : 0f) + tech.Stagger / 20f;
                Play(d, slot, length);
                d.NextAct = Mathf.Max(d.NextAct, _time + length);
            }
            else Play(d, DownSlot, 3f);
            if (!_quietHit) Burst(d.T.position + Vector3.up * 0.8f, tech.Col, tech.Finisher ? 1.6f : 1f, true);
            if (tech.Status > 0 && d.Hp > 0 && _rng.Next(100) < tech.StatusChance)
            {
                // the game's timers in frames (code 0x8005C4A0): stun 200-240 (no action at all), confuse 200-300. Poison's and flat's effects are not decoded.
                if (tech.Status == 1 && !d.Poisoned) { d.Poisoned = true; d.PoisonFrames = 100; }
                if (tech.Status == 4 && d.Flat == 0) { d.Flat = 224 + _rng.Next(81); d.T.localScale = new Vector3(d.Scale.x, d.Scale.y * 0.3f, d.Scale.z); }
                if (tech.Status == 3) { d.NextAct = Mathf.Max(d.NextAct, _time + (200 + _rng.Next(41)) / 20f); d.StunUntil = d.NextAct; }
                if (tech.Status == 2 && d.ConfuseFrames == 0) d.ConfuseFrames = 200 + _rng.Next(101);
                _pops.Add(new Pop { Text = tech.Spec, Pos = d.T.position + Vector3.up * 1.6f, Col = new Color(1f, 0.95f, 0.4f) });
            }
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
                if (OnStopMusic != null) OnStopMusic();
                Snd(0x15);
                _bits = _enemyBits;
                _bitsLeft = _bits; _statWait = 100; _countAcc = 0f; _fanfare = false;
                _dropped = null;
                if (Enemy.Info != null && _rng.Next(100) < Enemy.Info.DropChance && Enemy.Info.DropItem != 0xFF)    // the enemy's item, at its chance
                {
                    _s.AddItem(Enemy.Info.DropItem, 1);
                    _dropped = Enemy.Name + " dropped " + (ItemName != null ? ItemName(Enemy.Info.DropItem) : "");
                }
                TryLearn();
                int[] idx = { 4, 5, 0, 1, 2, 3 };
                for (int i = 0; i < 6; i++) { _before[i] = _s.Stats[idx[i]]; _gain[i] = 0; }
                StatGains();
                for (int i = 0; i < 6; i++) { _shown[i] = _before[i]; _left[i] = _gain[i]; }
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

        public void OnGUI(Camera cam)
        {
            _view = cam;
            if (!Active || Partner == null) return;
            UiKit.Begin();
            if (_phase == Phase.Notice && cam != null) AngerIcon(cam);
            if (_phase == Phase.Banner) Banner();
            if (_phase != Phase.Notice) Hud();
            if (_phase == Phase.Fight)
            {
                if (_time < _bannerUntil) Outlined(new Rect(0, 250, UiKit.W, 90), _banner + "!", 72, new Color(1f, 0.55f, 0.15f), TextAnchor.MiddleCenter);
                if (_time < _logUntil) Outlined(new Rect(50, 880, 1300, 70), _log, 46, Color.white);
            }
            if (_phase == Phase.Win && _time - _phaseStart > 1.2f) ResultWindow();
            if (_phase == Phase.Lose && _time - _phaseStart > 1.2f)
            {
                var box = new Rect(300, 380, 815, 260);
                UiKit.Box(box, new Color(0.10f, 0.17f, 0.15f, 0.95f));
                UiKit.Text(new Rect(box.x + 50, box.y + 40, box.width - 80, 70), Partner.Name + " collapsed...", 52, UiKit.Yellow, TextAnchor.MiddleLeft);
                UiKit.Text(new Rect(box.x + 50, box.y + 120, box.width - 80, 70), "You carry it back to File City.", 46, Color.white, TextAnchor.MiddleLeft);
            }
            if (cam != null && (_phase == Phase.Fight || _phase == Phase.Win)) EnemyBar(cam);
            UiKit.End();
            if (cam != null)
            {
                if (_phase == Phase.Fight || _phase == Phase.Win)
                {
                    if (_phase == Phase.Fight && _shoutFrame >= 0 && _player != null)     // only when you give an order: it hops up over the boy
                    {
                        float hop = ShoutHop[Mathf.Min(_shoutFrame, 19)] * Screen.height / 240f;
                        Vector3 sp = cam.WorldToScreenPoint(_player.position + Vector3.up * 1.8f);
                        if (sp.z > 0) Outlined(new Rect(sp.x - 150, Screen.height - sp.y - 25 + hop, 300, 50), _shout, 36, Color.white, TextAnchor.MiddleCenter);
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

        static readonly int[] BubbleScale = { 100, 104, 108, 112, 116, 120, 116, 112, 108, 104 };

        // the bubble over the enemy when a fight starts (renderFleeBubble): it pops up from the head, then pulses; its size is 1.44 m at that distance
        void AngerIcon(Camera cam)
        {
            if (Art == null) return;
            int frame = (int)((_time - _phaseStart) * 20f);
            float head = (Enemy.Info != null ? Enemy.Info.Height : 0) * 0.01f + (frame < 5 ? 2f + (frame + 1) * 0.1f : 2.5f);
            Vector3 world = Enemy.T.position + Vector3.up * head, v = cam.WorldToViewportPoint(world);
            if (v.z <= 0f) return;
            float size = Mathf.Abs(cam.WorldToViewportPoint(world + cam.transform.right * 1.44f).x - v.x) * 320f;
            size *= frame < 5 ? (frame + 1) / 5f : BubbleScale[(frame - 5) % 10] / 100f;
            float x = Mathf.Clamp((v.x - 0.5f) * 320f, -140f, 140f), y = Mathf.Clamp((0.5f - v.y) * 240f, -100f, 100f);
            Vector3 feet = cam.WorldToViewportPoint(Enemy.T.position);
            float dx = (feet.x - 0.5f) * 320f - x, dy = (0.5f - feet.y) * 240f - y;
            Art.Bubble(0, x, y, size, Mathf.Atan2(-dx, dy) * Mathf.Rad2Deg);
        }

        // BTL_tickBattleStart: the boy runs back along his waypoints to the tile he came in on, and stops there or at the edge of the screen, whichever comes first
        bool TickBoy()
        {
            if (_player == null || _boyDone) return true;
            if (_time - _boyStart >= 10f) { BoyStops(); return true; }      // 200 frames
            if (_playerA != null && _playerA.Slot != PlayerRunSlot) _playerA.SetSlot(PlayerRunSlot);
            Vector3 pos = _player.position;
            Vector2Int here = TileOf(pos), aim;
            int w = 310, h = 230;
            if (_route.Count > 0)
            {
                aim = _route[_route.Count - 1];
                if (here == aim) _route.RemoveAt(_route.Count - 1);
            }
            else
            {
                aim = _startTile; w = 300; h = 220;
                if (here == aim) { BoyStops(); return true; }
            }
            Vector3 dir = new Vector3(((aim.x - 50) * 100 + 50) * 0.01f - pos.x, 0f, ((50 - aim.y) * 100 - 50) * 0.01f - pos.z);
            if (dir.sqrMagnitude > 0.0001f) { dir.Normalize(); _player.rotation = _face(dir); }
            float step = _playerA != null ? _playerA.TakeForward() : 10f * Time.deltaTime;
            if (step <= 0f) return false;
            Vector3 by = dir * step;
            if (LeavesScreen(pos, pos + by, w, h)) { BoyStops(); return true; }
            if (Walkable == null || Walkable(pos + by)) pos += by;
            else if (Walkable(pos + new Vector3(by.x, 0, 0))) pos.x += by.x;
            else if (Walkable(pos + new Vector3(0, 0, by.z))) pos.z += by.z;
            _player.position = pos;
            return false;
        }

        void BoyStops()
        {
            _boyDone = true;
            if (_playerA != null) _playerA.SetSlot(_playerA.HasSlot(1) ? 1 : 0);
            _player.rotation = _face(Partner.T.position - _player.position);
        }

        // entityCheckCombatArea: a corner of him, at the feet or 2 m up, is past the box round the middle of the screen and going further
        bool LeavesScreen(Vector3 from, Vector3 to, int w, int h)
        {
            Camera cam = Camera.main;
            if (cam == null) return false;
            for (int i = 0; i < 2; i++)
                for (int up = 0; up < 2; up++)
                {
                    var corner = new Vector3((i == 0 ? -1 : 1) * _boyRadius, up * 2f, _boyRadius);
                    Vector3 a = cam.WorldToViewportPoint(from + corner), b = cam.WorldToViewportPoint(to + corner);
                    float ax = (a.x - 0.5f) * 320f, ay = (0.5f - a.y) * 240f, bx = (b.x - 0.5f) * 320f, by = (0.5f - b.y) * 240f;
                    if ((bx < -w / 2 && bx < ax) || (bx > w / 2 && bx > ax) || (by < -h / 2 && by < ay) || (by > h / 2 && by > ay)) return true;
                }
            return false;
        }

        static Vector2Int TileOf(Vector3 u) { int x, y; MapBackground.TileAt(u.x * 100f, u.z * 100f, out x, out y); return new Vector2Int(x, y); }

        static readonly int[] ShoutHop = { 0, -8, -14, -20, -25, -30, -34, -36, -38, -39, -40, -39, -38, -36, -34, -30, -34, -36, -38, -39 };
        static readonly int[] FinishU = { 0, 6, 10, 18, 22, 30, 38, 43 }, FinishW = { 6, 4, 8, 4, 8, 8, 5, 5 };

        // the word BATTLE: 155 little pieces fly in from both sides (BTL_initializeBattleStartText), flash, then burst outward
        sealed class WordPiece { public float X, Y, Speed, BurstX, BurstY; public int Tile; public bool Gone; }
        WordPiece[] _word; int _wordFrame, _wordHold; bool _bursting; float _wordAcc;

        void StartWord()
        {
            if (Art == null) return;
            _word = new WordPiece[Art.WordX.Length]; _wordFrame = 0; _wordHold = 0; _bursting = false; _wordAcc = 0f;
            for (int i = 0; i < _word.Length; i++)
            {
                int sgn = _rng.Next(2) == 1 ? 1 : -1, r = _rng.Next(5);
                _word[i] = new WordPiece
                {
                    X = sgn * (i < 51 ? 500 : i < 101 ? 600 : 700) + _rng.Next(100) - 50, Y = Art.WordY[i], Speed = -sgn * ((_rng.Next(3) + 1) * 32), Tile = _rng.Next(3),
                    BurstX = (r + 8) * Art.WordX[i] / 8f, BurstY = (r + 8) * Art.WordY[i] / 8f
                };
            }
        }

        // true when the word has burst and gone
        bool TickWord(float dt)
        {
            if (_word == null) return true;
            _wordAcc += dt;
            while (_wordAcc >= Frame)
            {
                _wordAcc -= Frame; _wordFrame++;
                int landed = 0, gone = 0;
                for (int i = 0; i < _word.Length; i++)
                {
                    var p = _word[i];
                    if (_bursting)
                    {
                        if (!p.Gone) { p.X += p.BurstX; p.Y += p.BurstY; if (Mathf.Abs(p.X) > 280f || Mathf.Abs(p.Y) > 200f) p.Gone = true; }
                        if (p.Gone) gone++;
                        continue;
                    }
                    if (p.X != Art.WordX[i])
                    {
                        p.X += p.Speed;
                        if (p.Speed > 0 ? p.X > Art.WordX[i] : p.X < Art.WordX[i]) p.X = Art.WordX[i];
                    }
                    else landed++;
                }
                if (_bursting && gone == _word.Length) { _word = null; return true; }
                if (!_bursting && landed == _word.Length && ++_wordHold > 12) _bursting = true;
            }
            return false;
        }

        void Banner()
        {
            if (_word == null || Art == null) return;
            int row = !_bursting && _wordHold > 0 ? 8 + _wordFrame % 6 / 2 : 8;       // the colours cycle once it has landed
            foreach (var p in _word)
            {
                if (p.Gone) continue;
                Vector2 a = BattleArt.WordPoint(p.X, p.Y), b = BattleArt.WordPoint(p.X + 8, p.Y + 12);
                Art.Hud(row, 128 + p.Tile * 8, 0, 8, 8, a.x, a.y, b.x - a.x, b.y - a.y);
            }
        }

        void Hud()
        {
            if (Art == null) return;
            // partner HP and MP, top right (BTL_renderPartnerStatusBars)
            for (int bar = 0; bar < 2; bar++)
            {
                int x0 = Art.BarLeft, y0 = bar == 0 ? -0x64 : -0x58;
                float now = bar == 0 ? Partner.ShownHp : Partner.Mp, max = bar == 0 ? Partner.MaxHp : Partner.MaxMp;
                for (int k = 0; k < 3; k++)
                {
                    int s = bar * 3 + k;                                               // frame, icon, then the fill stretched to 50 pixels at most
                    var b = Art.Bars;
                    Art.Hud(b[s, 0], b[s, 1], b[s, 2], b[s, 3], b[s, 4], x0 + b[s, 5], y0 + b[s, 6], k == 2 ? (max > 0 ? now * 50f / max : 0f) : -1f);
                }
                Art.Number(Mathf.RoundToInt(now), x0 + 0x49, y0 - 3);
            }
            // the order icons, top left, in the game's zigzag, with the cursor (BTL_renderCommandMenu)
            int n = _orders.Count + 1, count = n - 1, baseX = count * 14 - 140, top = -100, bottom = -68;
            bool blink = (int)(_time * 20f / 8f) % 2 == 1;
            int[] labelRun = LabelRun(n);
            int[] labelU = { 0, 11, 25, 39, 50 }, labelW = { 11, 14, 14, 11, 11 };
            for (int i = 0; i < n; i++)
            {
                int stripX = (i > 0 ? (i - 1) * 14 + 11 : 0) - 143;
                Art.Cmd(1, labelU[labelRun[i]], 32, labelW[labelRun[i]], bottom - top, stripX, top);
            }
            bool oddUp = count % 2 != 0;
            if (_phase == Phase.Fight)
            {
                bool upper = (_hover % 2 == 1) == oddUp;
                Art.Cursor(baseX - _hover * 14 - 3, upper ? -0x64 : -0x5a);
            }
            for (int i = 1; i < n; i++)
            {
                int cell = System.Array.IndexOf(OrderNames, _orders[i - 1]), row = 0;
                if (cell < 0) { row = 16; foreach (var t in Partner.Techs) if (t.Name == _orders[i - 1]) cell = Mathf.Clamp(t.Element, 0, 6); }
                bool upper = (i % 2 == 1) == oddUp;
                Art.Cmd(0, cell * 32 + (i == _hover && blink ? 16 : 0), row, 16, 16, baseX - i * 14, top + (upper ? 3 : 13));
            }
            if (_phase == Phase.Fight) Outlined(new Rect(40, 225, 500, 50), _orders[_hover - 1], 36, Color.white);
            // the finishing-move gauge: F, FI ... FINISH, then !! and PUSH (BTL_renderFinisherGauge)
            int letters = Mathf.Clamp(Partner.Finish * 6 / Mathf.Max(1, Partner.FinishMax), 0, 6);
            if (letters == 6) { letters = 8; Art.Hud(2, 128, 8, 37, 9, 0x6e, -0x4b); }
            for (int i = 0; i < letters; i++) Art.Hud(12, FinishU[i], 29, FinishW[i], 15, FinishU[i] + 0x37, -0x4f);
            // the charge bar over the partner's head (BTL_renderFinisherChargeup), up to 10 ticks
            if (_charging && _view != null)
            {
                float height = Partner.Info != null ? Partner.Info.Height : 0;
                Vector3 v = _view.WorldToViewportPoint(Partner.T.position + Vector3.up * (height + 100f) * 0.01f);
                float x = Mathf.Clamp((v.x - 0.5f) * 320f - 23f, -100f, 100f), y = Mathf.Clamp((0.5f - v.y) * 240f - 20f, -100f, 100f);
                Art.Cmd(2, 0x58, 32, 46, 12, x, y);
                for (int i = 0; i < _chargeCount / 8; i++) Art.Cmd(2, 0x88, 32, 4, 6, x + 3 + i * 4, y + 3);
            }
        }

        // which label strip goes in each place, by how many orders there are (BTL_COMMAND_MENU_LAYOUTS)
        static int[] LabelRun(int n)
        {
            int[] r = new int[n];
            r[0] = 0;
            for (int i = 1; i < n - 1; i++) r[i] = i % 2 == 1 ? 1 : 2;
            r[n - 1] = n == 4 || n == 8 ? 4 : 3;
            return r;
        }

        // BTL_renderEnemyHPBars: a green outline with a line down to the enemy, the tinted strip and its HP. It shows for a moment after each hit; Select keeps it on.
        int _barFrames;
        bool _lifeBarOn;
        float _hudAcc;

        void EnemyBar(Camera cam)
        {
            if (Art == null || (_barFrames >= 21 && (!_lifeBarOn || Enemy.Hp <= 0))) return;
            Transform at = Enemy.A != null ? Enemy.A.Node(1) : null;
            Vector3 v = cam.WorldToViewportPoint(at != null ? at.position : Enemy.T.position + Vector3.up);
            if (v.z <= 0f) return;
            float px = (v.x - 0.5f) * 320f, py = (0.5f - v.y) * 240f;
            float y = py < -0x4a ? -0x64 : py - 0x1a, x = px >= 0x42 ? 0x55 : px + 0x14;
            BattleArt.Line(x, y, x + 0x36, y);
            BattleArt.Line(x + 0x36, y, x + 0x36, y + 6);
            BattleArt.Line(x, y + 1, x, y + 6);
            BattleArt.Line(x, y + 6, x + 0x35, y + 6);
            BattleArt.Line(x, y + 6, px, py);
            int hp = Mathf.RoundToInt(Enemy.ShownHp);
            Art.LifeFill(hp * 50 / Enemy.MaxHp, x + 2, y + 2);
            Art.Digits(hp, 4, x + 0x21, y - 9);
        }

        // damageTick: the number runs down in steps each frame
        static void DrainHp(Fighter f)
        {
            float left = f.ShownHp - f.Hp;
            if (left <= 0f) { f.ShownHp = f.Hp; return; }
            if (left > 999) { f.ShownHp -= 900; left -= 900; }
            if (left > 99) { f.ShownHp -= 80; left -= 80; }
            if (left > 9) { f.ShownHp -= 6; left -= 6; }
            if (left > 0) f.ShownHp -= 1;
        }

        void ResultWindow()
        {
            string[] names = { "HP", "MP", "Off", "Def", "Speed", "Brain" };
            var box = new Rect(60, 60, 880, 600);
            UiKit.Box(box, new Color(0.12f, 0.2f, 0.16f, 0.95f));
            UiKit.Fill(new Rect(box.x + 600, box.y + 8, 5, box.height - 16), UiKit.Frame);
            for (int i = 0; i < 6; i++)
            {
                float y = box.y + 30 + i * 90;
                int v = _shown[i];
                UiKit.Text(new Rect(box.x + 30, y, 200, 70), names[i], 52, UiKit.Yellow, TextAnchor.MiddleLeft);
                UiKit.Text(new Rect(box.x + 270, y - 8, 160, 60), v.ToString(), 48, Color.white, TextAnchor.MiddleRight);
                UiKit.Bar(new Rect(box.x + 270, y + 48, 300, 14), i < 2 ? v / 1500f : v / 999f, UiKit.Cyan);
                if (_left[i] > 0) UiKit.Text(new Rect(box.x + 640, y, 220, 70), "^ " + _left[i], 52, new Color(0.35f, 0.75f, 1f), TextAnchor.MiddleLeft);
            }
            var bits = new Rect(60, 690, 880, 120);
            UiKit.Box(bits, new Color(0.12f, 0.2f, 0.16f, 0.95f));
            UiKit.Text(new Rect(bits.x + 30, bits.y + 20, 300, 80), "Bits", 56, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Text(new Rect(bits.x + 360, bits.y + 20, 480, 80), (_s.Money + _bits - _bitsLeft).ToString() + (_bitsLeft > 0 ? "   (+" + _bitsLeft + ")" : ""), 56, Color.white, TextAnchor.MiddleRight);
            if (_learned != null) Outlined(new Rect(0, 50, 1415, 80), _learned + " learned!", 56, UiKit.Yellow, TextAnchor.MiddleCenter);
            if (_dropped != null) Outlined(new Rect(0, 130, 1415, 80), _dropped, 50, Color.white, TextAnchor.MiddleCenter);
        }
    }
}
