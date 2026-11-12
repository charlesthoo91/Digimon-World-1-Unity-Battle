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
    public sealed partial class Battle
    {
        public sealed class Tech
        {
            public int Id; public string Name; public int Mp, Power, Element, Status, StatusChance, Stagger, Accuracy; public bool Melee, Finisher, Wide, Self; public Color Col;
            public string Spec { get { string[] n = { "", "poison", "confuse", "stun", "flat" }; return Status >= 0 && Status < n.Length ? n[Status] : ""; } }
        }

        public sealed class Fighter
        {
            public Transform T; public DigimonAnimator A; public string Name;
            public DigimonStats Info;
            public int Off, Def, Spd, Brain, Hp, MaxHp, Mp, MaxMp;
            public List<Tech> Techs = new List<Tech>(); public Tech Finisher;     // the partner has at most 3 (the Techset)
            public int Head = -1; public bool Collided;     // heading (game units) and hasCollidedWhileDistanceCmd of BTL_backAwayFromTarget
            public int Finish, FinishMax;       // finishing gauge: +1 a frame, 6% on a hit, full at 3000 - Speed
            public int Energy = 100, Style;     // energy bar: + (Speed / 100 + 1) every 2 frames, minus the power of each technique; Style = how full it must be
            public int Wait, Stall, Bore, SelfUses, ConfuseFrames;
            public Tech[] Moves = new Tech[4]; public int[] Weights = new int[4];
            public Tech Pending; public Fighter PendingTarget;
            public int Cool, PoisonFrames; public bool Poisoned, Blind; public float Adjust; public Vector3 Scale;
            public float NextAct, AnimReset;
            // FighterData status fields (BTL_applyMoveStatus, BTL_tickStatusEffects; see BattleStatus.cs): stunTimer, flatTimer (-1 = waiting for the
            // hit reaction to end), flatAttackTimer, entity.flatSprite (-1 = the model is drawn), statusFxId (the one condition effect over it)
            public int StunFrames, FlatTimer, FlatAttack, FlatSprite = -1, FlatFlip, FxKind; public bool Confused, Stunned, Flattened, Protected;
            public float HitUntil;              // stats.current.isHit: until its hit reaction is over nothing hits it again (the effects' hit subs skip it)
            public object FxRun; public int FxSection = -1;      // FighterData.activeEffectSlot / stats.current.efeSubEffect: its last technique's effect program
            public float ShownHp;               // the HP shown on screen: it drains smoothly after a hit (393, 379, 365 ...)
            public byte BarFrames = 0xFF;       // the enemy life bar's frame count (player.unk1[i].unk25): 0 on a hit, shown while below 21; a byte, so it wraps (BTL_setupBattle sets 0xFF)
            public Vector3 Slot;
            public Vector3 FleeTo; public int Leaving;      // NPCEntity.flee (Unity); after the fight 0 getting up, 1 running off, 2 done
            // the order it follows: the partner always; with the mod Online battle and orders on, also the friend's Digimon on the host's PC
            // (its orders come from the friend's PC). Others are enemies and use the enemy AI (PickEnemy)
            public bool Commanded; public string Order = "Your Call"; public bool WantFinish;
            // BTL_retargetAfterHit / BTL_selectFighterTarget for an enemy: the other enemy that hit it (targetId + FIGHTER_FLAG_10), kept until its
            // next technique is over; null = the partner (targetId 0)
            public Fighter Turned;
            public int Rec42;                   // NPCEntity.unk1 (map record +42): 1 = never turns on another enemy, no wide-technique weight
            public bool TargetHeld;             // the partner's FIGHTER_FLAG_10: set by BTL_retargetAfterHit, cleared when its technique is over
            public Fighter CounteredBy;         // BTL_tickAttackState: frozen (animFlag bit 0 clear) while this fighter's Counter (0x2D) runs at it
        }

        static Vector3 FleeOf(MapDigimon d, Transform t) { return new Vector3(d.Flee.x * 0.01f, t.position.y, d.Flee.z * 0.01f); }

        sealed class Swing { public Fighter From, To; public float T; public bool Done; public Tech Tech; public Vector3 Start; public float Boost = 1f, Release, Length; public bool Effect, Landed, Fell; public int DrawnAtStart; public Vector3? HitAt; }
        sealed class Shot { public Fighter From, To; public Tech Tech; public Vector3 Pos, Dir; public float Travelled; public GameObject Go; public float Boost = 1f; }
        sealed class Pop { public string Text; public Vector3 Pos; public float T, Wait; public int Shift; public Color Col; public int Icon; public bool Fixed, Sent; public Vector2 At; }     // Icon: addEntityText's icon, 1 HP, 2 MP
        sealed class Fx { public GameObject Go; public Material Mat; public Vector3 Vel; public float T, Life, S0, S1; public Color Col; }
        readonly List<Fx> _fx = new List<Fx>();
        // addThrownItem / tickThrownItem (main.c): the item sits in the boy's hand (bone 9) until his animation 6 reaches frame 7, then flies to the partner in 24 frames:
        // x and z close a 1 / (25 - t) part of the gap each frame, y follows ITEM_THROW_HEIGHT; at the end the item works (ITEM_FUNCTIONS) and the sparkle starts (BTL_addItemParticles)
        sealed class Throw { public int Item, Hold, Time; public Vector3 G; public System.Action Apply; }
        public byte[] Exe;      // SLUS_010.32, set by the room (tables read at run time)
        int[] _throwHeight;     // ITEM_THROW_HEIGHT (exe 0x8012F36C, 24 s16): the item's height each frame of its flight
        float _throwAcc;

        /// <summary>startThrowingItem: false while an item is still in the air (TAMER_ITEM busy).</summary>
        public bool ThrowBusy { get { return _throws.Count > 0; } }

        public void ThrowItem(int item, System.Action apply)
        {
            if (_throws.Count > 0 || _player == null) return;
            if (_playerA != null && _playerA.HasSlot(6)) _playerA.SetSlot(6, true);
            _throws.Add(new Throw { Item = item, Apply = apply, G = EffectPlayer.ToGame(HandOf()) });
        }

        Vector3 HandOf() { var n = _playerA != null ? _playerA.Node(9) : null; return n != null ? n.position : _player.position + Vector3.up; }

        void TickThrows(float dt)
        {
            _throwAcc += dt;
            while (_throwAcc >= Frame)
            {
                _throwAcc -= Frame;
                for (int i = _throws.Count - 1; i >= 0; i--)
                {
                    var th = _throws[i];
                    if (th.Hold < 7) { th.Hold++; th.G = EffectPlayer.ToGame(HandOf()); continue; }
                    Vector3 p = EffectPlayer.ToGame(Partner.T.position);
                    float dx = p.x - th.G.x, dz = p.z - th.G.z;
                    if (th.Time > 0) { th.G.x += (int)dx / (25 - th.Time); th.G.z += (int)dz / (25 - th.Time); }
                    if (_throwHeight == null && Exe != null)
                    {
                        _throwHeight = new int[24];
                        int o = (int)(0x8012F36C - BitConverter.ToUInt32(Exe, 0x18) + 0x800);
                        for (int k = 0; k < 24; k++) _throwHeight[k] = BitConverter.ToInt16(Exe, o + 2 * k);
                    }
                    th.G.y = _throwHeight != null ? _throwHeight[th.Time] : 0;
                    th.Time++;
                    if (th.Time >= 24)
                    {
                        _throws.RemoveAt(i);
                        int[] shown = ItemNumbers(th.Item);       // read before the item works (alive or not, Restore's half of the max)
                        if (th.Apply != null) th.Apply();
                        ItemConditions(th.Item);        // handleStatusItems (BattleStatus.cs)
                        ReadBack();
                        // addEntityText(partner, 0, 0xB, the item's own amount, icon 1 HP / 2 MP): the amount the item gives, not how much went up
                        // (Omnipotent shows 1500 and 1500 even near full); a second number starts 4 pixels on and waits 11 frames
                        for (int k = 0; k < shown.Length; k += 2)
                            _pops.Add(new Pop { Text = shown[k].ToString(), Pos = Partner.T.position + Vector3.up * 1.2f, Col = new Color(0f, 1f, 0f), Icon = shown[k + 1], Shift = k / 2 * 4, Wait = k / 2 * 11f / 20f });
                        if (Effects != null) Effects.AddItemParticles(Partner.T.position, Partner.Info != null ? Partner.Info.Height : 200);
                    }
                }
            }
        }

        // the numbers ITEM_FUNCTIONS show in a fight, as (value, icon) pairs: handleHPHealingItem / handleMPHealingItem HEALING_VALUES (nothing at
        // 0 HP), handleDoubleFloppy and Omnipotent 1500 HP then 1500 MP, handleRestore (even at 0 HP) half the max HP / Sup.restore 9999
        int[] ItemNumbers(int item)
        {
            bool alive = _s.Stats[6] != 0;
            var v = PartnerCare.HealingValues;
            if (item <= 3) return alive ? new[] { v[item], 1 } : new int[0];
            if (item <= 6) return alive ? new[] { v[item - 4], 2 } : new int[0];
            if (item == 7 || item == 9) return alive ? new[] { 1500, 1, 1500, 2 } : new int[0];
            if (item == 11) return new[] { _s.Stats[4] / 2, 1 };
            if (item == 12) return new[] { 9999, 1 };
            return new int[0];
        }

        enum Phase { Notice, Banner, Fight, Win, Lose, Fled, Leave, TimeUp, Cheer }

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
        public bool NoFlee;                     // FLEE_DISABLED[0] = flag 1 (handleBattleStart): running away is locked, enemies pay no MP, they stand after the fight
        public bool Tamerless;                  // script flag 2 at the start: the boy gives no orders and uses no items

        // the arena (STD_REL.BIN, std/std_main.c, the arena's own copy of the fight code): one enemy, no Run, no Change, no items, the 99 counter,
        // no result boxes and no life lost; the room runs the matches (PlayableRoomTournament) and sets these before Start
        public bool Tournament;
        public bool TournamentWeak;             // PARTNER_PARA.condition & 0x60: STD_initializeCombat takes a fifth off Offense, Defense, Speed and Brains for the fight
        public int Timer;                       // STD_TIMER (STD_addFighterCounter(0x63))
        public int TimerStart = 99;             // ours (online battle's time option): the counter's start, 99 as the arena
        public bool NoTimeLimit;                // ours (online battle's time option): the counter never runs down
        public static bool InputLocked;         // ours (online battle's surrender question): the fight's own buttons (Square, L1 / R1) are not read
        public int Result;                      // STD_combatMain: 1 won, -1 lost (a draw is lost too: STD_deinitializeCombat(1, 1) returns -1)
        public int WinnerSide = -1;             // STD_deinitializeCombat's a: 0 the partner, 1 the enemy, 2 nobody (a draw)
        public bool TimeUpWord, TimeUpBox;      // STD_renderTimeoutText / STD_addTimeoutWindow are up
        public readonly int[] TourDamage = new int[2];          // STD_DAMAGE: [0] what the partner lost, [1] what the enemy lost
        public System.Action<int> TournamentMusic;          // playMusic(STD_MUSIC, track)
        public bool TournamentStartDone;        // STD_combatSetup: the room's arena BATTLE text has come together and burst; the fight begins
        public System.Action<Fighter, int> OnTechStart;     // STD_setupMoveExecution's camera choice (the technique's id)
        public System.Action<Fighter> OnTechHit;            // a landed blow (STD_tickAttackHits: STD_setRandomViewpoint on the one hit)
        public System.Action<Fighter> OnKnockOut;           // STD_selectRandomCamera(loser, 5, 0): the WIN / LOSE scene and the knock-out camera
        public System.Action OnVersus; public System.Func<bool> VersusBusy;     // a draw: the draw letters (STD_addVersusModelScene) and whether they still run

        // ---- mod Online battle (ours; PlayableRoomOnline). The host's PC runs the fight and reports it: every technique started, every knock-out.
        // The friend's PC runs this class as a copy (Puppet): no thinking, moving or damage; positions, animations, HP and the rest come from the host
        public bool Puppet;
        public System.Action<Fighter, int, Fighter> NetSwing;      // a technique starts (who, technique, at whom)
        public System.Action<Fighter> NetStopFx;                   // a fighter's technique effect is stopped (RemoveMoveEffect)
        public System.Action<Fighter> NetKnockOut;                 // the arena's knock-out (OnKnockOut)
        public System.Action<Fighter> NetAura;                     // a finishing move's aura starts (ShowRing)
        public System.Action NetVersus;                            // a draw's letters start (OnVersus)
        bool _puppetEnd; int _puppetResult, _puppetWinner;         // the host's end, kept until the copy's draw letters are over
        public System.Action<string> NetOrder;                     // the copy: an order the friend gave (to the host)
        public bool EnemyCommanded;                                // the host with orders on: the friend's Digimon (Enemy) follows the friend's orders
        public const string FinishOrderName = "#finish";
        float _shoutAcc;

        /// <summary>The host: the friend gave its Digimon (the Enemy here, made Commanded) an order.</summary>
        public void RemoteOrder(string order)
        {
            if (Enemy == null || !Enemy.Commanded || _phase != Phase.Fight) return;
            if (order == FinishOrderName) { if (Enemy.Finisher != null && Enemy.Finish >= Enemy.FinishMax) { Enemy.WantFinish = true; Enemy.Pending = null; Enemy.Wait = 0; } return; }
            if (order == RunOrder || order == ChangeOrder) return;
            Enemy.Order = order;
        }
        bool _tVersus; readonly HashSet<Fighter> _tKo = new HashSet<Fighter>();
        // what the arena's own HUD (ArenaScreens) shows: the commands as the game numbers them (0xB the finishing move, 2 Attack, 3 Your Call,
        // 4 Moderate, 5 Distance, 6 Defensive, 8-10 the techniques), the one under the cursor, the finishing move's charge
        public int[] TourCommands = new int[0], TourCommandElement = new int[0];
        public int Hover { get { return _hover; } }
        public bool InFight { get { return _phase == Phase.Fight; } }
        public bool PartnerCharging { get { return _charging && _chargeBy == Partner; } }
        public int ChargeCount { get { return _chargeCount; } }
        public bool FinisherOrdered { get { return _wantFinish || (_charging && _chargeBy == Partner); } }
        // STD_tickBattle's camera test: someone attacking (FIGHTER_FLAG_ATTACKING, until its technique's animation is over: STD_tickAttackState)
        // or thrown back (FIGHTER_FLAG_KNOCKED_BACK); the effect still flying does not count
        public bool Acting
        {
            get
            {
                if (_charging) return true;
                foreach (var sw in _swings) if (sw.T < sw.Length) return true;
                foreach (var f in new[] { Partner, Enemy }) if (f != null && _time < f.HitUntil) return true;
                return false;
            }
        }
        int _tFrames, _tStartHp0, _tStartHp1, _tCount, _tLoser = -1, _tCheer; float _tAcc, _tTimerAcc; bool _tPress;
        public Fighter Partner, Enemy;           // Enemy = the one the partner is after; the others wait in Extras (up to three enemies, "Change target!")
        public readonly List<Fighter> Extras = new List<Fighter>();
        public BattleIcons Icons; public ItemIcons Items;

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
        // the partner's order (it lives on the partner's Fighter, so the online friend's Digimon can have its own)
        string _order { get { return Partner != null ? Partner.Order : CallOrder; } set { if (Partner != null) Partner.Order = value; } }
        int _hover = 1, _shoutFrame = -1; string _shout;       // the order under the cursor; numbers count from 1 as the game's do (0 is the finishing move)
        public BattleArt Art;
        readonly List<Swing> _swings = new List<Swing>();
        readonly List<Shot> _shots = new List<Shot>();
        readonly List<Pop> _pops = new List<Pop>();
        readonly List<Throw> _throws = new List<Throw>();
        readonly int[] _before = new int[6], _gain = new int[6], _shown = new int[6], _left = new int[6];
        int _bitsLeft, _statWait; float _countAcc; bool _fanfare, _cheered;
        public System.Action<int> Sfx;           // playSound(0, n)
        public EffectPlayer Effects;
        public System.Action OnStopMusic;
        public BattleEndView EndView;                   // the boxes after a won fight (the room loads it)
        public int MapId = -1;                          // CURRENT_SCREEN: on map 0x8F nothing drops
        public Func<int> Tiredness;                     // PARTNER_PARA.tiredness, for the injury roll (handleBattleInjury)
        public Func<bool> Injured;                      // PARTNER_PARA.condition & 0x20
        public Action SetInjured;
        int _endStep; bool _skip, _anyHit, _endPress;
        readonly List<KeyValuePair<int, Vector3>> _drops = new List<KeyValuePair<int, Vector3>>();
        readonly List<int> _dropIds = new List<int>(); readonly List<string> _dropBy = new List<string>();
        string[] _endStr = new string[16];              // BTL_REL / exe strings of the battle end text
        public System.Action<int, Vector3> OnDrop;     // spawnDroppedItems (battle_ui.c, with the result boxes): the item lies on the ground at once
        void Snd(int n) { if (Sfx != null) Sfx(n); }
        int _bits, _enemyBits, _startHp, _used, _blocks, _bigHits;
        readonly List<int> _seen = new List<int>();     // the Tech page techniques the enemy used in this fight (the game keeps up to 12)
        string _learned, _dropped, _dropName;
        /// <summary>The item the enemy dropped (-1 none) and where it fell: the room puts it on the ground there (spawnDroppedItems).</summary>
        public int DropItem = -1; public Vector3 DropAt;
        public Func<int, string> ItemName;      // the room tells the names
        static Texture2D _disc; static Material _shotMat;

        public System.Action OnOpenBag;         // the room opens the bag (items work in battle: Triangle)

        public Battle(ScriptState state, Func<Vector3, Quaternion> face) { _s = state; _face = face; }

        // -------------------------------------------------------------- setup
        // the shot's colour by element: fire, battle, air, earth, ice, mech, filth
        static readonly Color[] ElementCol = { new Color(1f, 0.55f, 0.1f), new Color(1f, 0.8f, 0.5f), new Color(1f, 0.95f, 0.2f), new Color(0.5f, 0.9f, 0.3f), new Color(0.6f, 0.95f, 1f), new Color(0.85f, 0.9f, 1f), new Color(0.65f, 0.45f, 0.2f) };

        /// <summary>Technique n from the game's table. Finishing moves cost no MP (they come from the gauge).</summary>
        public static Tech MakeTech(int id, bool finisher)
        {
            // BTL_startQueuedMove / BTL_tickMeleeAttack: any technique 0x3A-0x70 is a finishing move (aura, 80-frame hold, no MP)
            finisher |= IsFinisherId(id);
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
                    if (_s.Techs[info.Learnable[i]] && (!_s.TechSet.Exists(x => x >= 0) || _s.TechSet.Contains(info.Learnable[i]))) f.Techs.Add(MakeTech(info.Learnable[i], false));
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
            // BTL_tickFighterStates: an enemy has a finisher only when the technique of its slot 3 (moves[3]) is one of 0x3A-0x70; the gauge then fills and it fires. Psychemon's
            // slot 3 is Tremar, a normal move, so it has none (its list ends in War Cry, a power-up that never hits)
            if (data != null)
            {
                int place3 = data.MoveSlots[3] - DigimonStats.FirstMoveSlot;
                if (place3 >= 0 && place3 < info.Moves.Count && IsFinisherId(info.Moves[place3])) f.Finisher = MakeTech(info.Moves[place3], true);
            }
            else if (info.SignatureId >= 0) f.Finisher = MakeTech(info.SignatureId, true);
        }

        /// <summary>right = the screen's right direction on the ground.</summary>
        public void Start(Transform player, DigimonAnimator playerAnim, Transform partner, DigimonAnimator partnerAnim, string partnerName, DigimonStats partnerInfo,
                          Transform enemy, DigimonAnimator enemyAnim, string enemyName, DigimonStats enemyInfo, MapDigimon enemyData, Vector3 right, bool noFlee, List<Vector2Int> route, Vector2Int startTile, float boyRadius)
        {
            NoFlee = noFlee;
            Tamerless = _s.GetFlag(2);      // battle_setup.c: IS_TAMERLESS_BATTLE = isTriggerSet(2)
            Extras.Clear(); _enemySlots.Clear();
            Partner = new Fighter
            {
                T = partner, A = partnerAnim, Name = partnerName, Info = partnerInfo,
                Off = _s.Stats[0], Def = _s.Stats[1], Spd = _s.Stats[2], Brain = _s.Stats[3],
                MaxHp = Mathf.Max(1, _s.Stats[4]), MaxMp = _s.Stats[5], Hp = _s.Stats[6], Mp = _s.Stats[7]
            };
            if (Partner.Hp <= 0) Partner.Hp = 1;
            Partner.Commanded = true;
            SetupTechs(Partner, partnerInfo, null);
            Enemy = new Fighter
            {
                T = enemy, A = enemyAnim, Name = enemyName, Info = enemyInfo,
                Off = enemyData.Off, Def = enemyData.Def, Spd = enemyData.Spd, Brain = enemyData.Brain,
                MaxHp = Mathf.Max(1, enemyData.Hp), Hp = Mathf.Max(1, enemyData.Hp), MaxMp = enemyData.Mp, Mp = enemyData.Mp
            };
            _enemyBits = enemyData.Bits;
            Enemy.FleeTo = FleeOf(enemyData, enemy);
            SetupTechs(Enemy, enemyInfo, enemyData);
            Enemy.Commanded = EnemyCommanded;
            Enemy.Rec42 = enemyData.Rec42;
            _enemySlots.Add(Enemy);
            _seen.Clear(); _learned = null; _dropped = null; _dropName = null; DropItem = -1; _endStep = -1; _anyHit = false; _drops.Clear();
            foreach (var f in new[] { Partner, Enemy })
            {
                f.FinishMax = 3000 - f.Spd;
                f.Bore = f.Brain / 10 + 5;
                f.SelfUses = f.Brain < 400 ? f.Brain / 100 + 1 : f.Brain < 600 ? 4 : 5;
            }
            _wantFinish = false; _frame = 0; _acc = 0f; _used = 0; _blocks = 0; _bigHits = 0;
            _deathCount = 0; _cdTimer = -1; _fainted = false; _rising = false; _faintAcc = 0f;

            Partner.ShownHp = Partner.Hp; Enemy.ShownHp = Enemy.Hp;
            _lifeBarOn = false; _hudAcc = 0f;
            SetupOrders();
            _order = CallOrder; _hover = _orders.Count; _shoutFrame = -1;
            // BTL_setupBattle: an injured or sick partner (PARTNER_PARA.condition & 0x60) loses a fifth of Offense, Defense, Speed and Brains for
            // the fight, after its finishing gauge, power-ups and orders were set from the full numbers (the arena does it in StartTournament)
            if (!Tournament && (_s.Para.Condition & 0x60) != 0)
            {
                Partner.Off -= Partner.Off / 5; Partner.Def -= Partner.Def / 5;
                Partner.Spd -= Partner.Spd / 5; Partner.Brain -= Partner.Brain / 5;
            }

            _route = route; _startTile = startTile; _boyRadius = boyRadius; _boyDone = false; _boyStart = 0f;       // a random fight: the boy goes back to the entrance he came in by
            _player = player; _playerA = playerAnim;
            Snd(0x10);
            Active = true; Won = false; Fled = false; _phase = Phase.Notice; _time = 0f; _phaseStart = 0f; _running = false; _partnerHeld = false; _bannerUntil = 0f;
            _swings.Clear(); _pops.Clear(); _throws.Clear(); _runners.Clear(); ClearShots(); _startHp = Partner.Hp;
            Partner.Scale = partner.localScale; Enemy.Scale = enemy.localScale;
            enemy.rotation = _face(partner.position - enemy.position);
            Stand(Partner);
            Stand(Enemy);
            Partner.T.rotation = _face(Enemy.T.position - Partner.T.position);     // BTL_handleBattleIntro: nobody takes a place, the partner just turns to the enemy
            foreach (var f in new[] { Partner, Enemy }) if (f.A != null) f.A.BodyMovesItself = true;
            if (Tournament) StartTournament();
        }

        // STD_initializeCombat: the counter at 99 (it runs once STD_combatSetup is over), each side's HP at the start (STD_STARTING_HP); the partner's
        // finishing gauge, power-ups and orders come from its numbers before the condition's fifth is taken off
        void StartTournament()
        {
            NoFlee = true; _boyDone = true;
            _puppetEnd = false;
            Timer = TimerStart; _tFrames = 1; _tTimerAcc = 0f; Result = 0; WinnerSide = -1; TimeUpWord = TimeUpBox = false; TourDamage[0] = TourDamage[1] = 0;
            _tCount = 0; _tLoser = -1; _tCheer = 0; _tAcc = 0f; _tPress = false;
            _tStartHp0 = Partner.Hp; _tStartHp1 = Enemy.Hp;
            _tVersus = false; _tKo.Clear(); TournamentStartDone = false;
            _phase = Phase.Banner; _phaseStart = _time;         // no angry enemy, no BTL word: the arena's start text runs (the room draws it)
            if (TournamentWeak)
            {
                Partner.Off -= Partner.Off / 5; Partner.Def -= Partner.Def / 5;
                Partner.Spd -= Partner.Spd / 5; Partner.Brain -= Partner.Brain / 5;
            }
            if (TournamentMusic != null) TournamentMusic(2);       // STD_combatMain: playMusic(STD_MUSIC, 2)
        }

        // the order list by Brains, in the game's menu order (code 0x80056F4C). From 500 Brains Attack and Moderate go and the three Techset techniques take their place.
        void SetupOrders()
        {
            _orders.Clear();
            int b = Partner.Brain;
            if (!Tournament) _orders.Add(RunOrder);                           // shown in a scripted fight too, but the cursor jumps over it (none in the arena)
            if (b >= 300 && !Tournament) _orders.Add(ChangeOrder);            // STD_initializeCombat has no Change either
            if (b >= 400) _orders.Add(DefensiveOrder);
            if (b >= 300) _orders.Add(DistanceOrder);
            if (b >= 500) { for (int i = Partner.Techs.Count - 1; i >= 0; i--) _orders.Add(Partner.Techs[i].Name); }
            else
            {
                if (b >= 200) _orders.Add(ModerateOrder);
                if (b >= 100) _orders.Add(AttackOrder);
            }
            _orders.Add(CallOrder);
            if (!Tournament) return;
            // STD_initializeCombat: availableCommands[0] = 0xB, then by Brains (the same list as ours without Run and Change)
            TourCommands = new int[_orders.Count + 1]; TourCommandElement = new int[_orders.Count + 1];
            TourCommands[0] = 0xB;
            for (int i = 0; i < _orders.Count; i++)
            {
                string o = _orders[i]; int c = 3, el = 0;
                if (o == AttackOrder) c = 2; else if (o == ModerateOrder) c = 4; else if (o == DistanceOrder) c = 5; else if (o == DefensiveOrder) c = 6; else if (o == CallOrder) c = 3;
                else for (int k = 0; k < Partner.Techs.Count; k++) if (Partner.Techs[k].Name == o) { c = 8 + k; el = Partner.Techs[k].Element; }
                TourCommands[i + 1] = c; TourCommandElement[i + 1] = el;
            }
        }


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
            if (Puppet) { PuppetTick(side, action); return; }
            float dt = Time.deltaTime; _time += dt;
            if (Effects != null) Effects.Tick(dt, Camera.main);
            TickAura(dt);
            TickConditions(dt);
            Animate(Partner); Animate(Enemy);
            foreach (var x in Extras) Animate(x);
            for (int i = _pops.Count - 1; i >= 0; i--) { if (_pops[i].Wait > 0f) { _pops[i].Wait -= dt; continue; } _pops[i].T += dt; if (_pops[i].T >= 31f / 20f) _pops.RemoveAt(i); }     // renderEntityText: 31 frames; a waiting one is not drawn yet
            foreach (var f in new[] { Partner, Enemy })
            {
                if (f.A != null && f.A.Slot != WalkSlot && f.A.Slot != RunSlot && f.A.Slot != 2 && f.A.Slot != 4)      // the animation itself throws it back; it stays inside the fight
                {
                    Vector3 next = f.T.position - f.T.forward * f.A.TakeBodyMove();
                    if (Tournament || !LeavesScreen(f.T.position, next, 280, 200, RadiusOf(f))) OnGround(f.T, next - f.T.position);     // not into a wall (Glitch FAQ C2, see OnGround); the arena has only its ring's walls
                }
            }
            _hudAcc += dt;
            while (_hudAcc >= Frame)
            {
                _hudAcc -= Frame;
                DrainHp(Partner);
                // the arena draws its own HP (ArenaScreens), so there the number just drains
                if (Tournament) DrainHp(Enemy);
                else if (_phase == Phase.Fight || _phase == Phase.Win) foreach (var f in Enemies()) TickEnemyBar(f);
            }
            if (_phase == Phase.Fight && GameMenus.SelectKey()) _lifeBarOn = !_lifeBarOn;
            TickFx(dt);
            TickThrows(dt);
            float inPhase = _time - _phaseStart;

            switch (_phase)
            {
                case Phase.Notice:   // the enemy gets angry
                    TickBoy();
                    TickRunners();
                    // STD_combatSetup: the arena's start text runs at least 60 frames before sound 0x11 and its burst (no angry enemy there)
                    if (inPhase > (Tournament ? 60 * Frame : 1.3f)) SetPhase(Phase.Banner);
                    break;
                case Phase.Banner:   // BATTLE; everyone takes their place
                {
                    if (Tournament)
                    {
                        TickRunners();
                        if (TournamentStartDone)
                        {
                            Enemy.T.rotation = _face(Partner.T.position - Enemy.T.position);
                            Partner.T.rotation = _face(Enemy.T.position - Partner.T.position);
                            SetPhase(Phase.Fight);
                        }
                        break;
                    }
                    bool c = TickBoy();
                    bool wordDone = TickWord(dt);
                    bool gone = TickRunners();          // the fight waits for the runners to leave the screen
                    if (c && wordDone && gone)
                    {
                        RemoveRunners();
                        Enemy.T.rotation = _face(Partner.T.position - Enemy.T.position);
                        Partner.T.rotation = _face(Enemy.T.position - Partner.T.position);
                        SetPhase(Phase.Fight);
                    }
                    break;
                }
                case Phase.Fight:
                    if (Tournament) TickTimer(dt);
                    // IS_TAMERLESS_BATTLE (script flag 2, battle_setup.c): handleCommands returns at once (no cursor, no order, no Run, no finishing
                    // move) and tamerTickBattle opens no item box. FRZL08's second Garurumon fight ("Only Digimon will fight")
                    if (Tamerless) { triangle = false; side = 0; action = false; }
                    if (!Tamerless && !_charging && SquarePressed() && Partner.Finisher != null && Partner.Finish >= Partner.FinishMax) { _wantFinish = true; Partner.Pending = null; Partner.Wait = 0; Snd(3); Shout(Partner.Finisher.Name); }     // battle_tick.c: command 0xB and its shout
                    if (triangle && !Tournament) { if (OnOpenBag != null) OnOpenBag(); }     // STD_handleCommands has no item page
                    if (side != 0) { Snd(2); MoveHover(side); }
                    if (action) { Snd(3); if (_playerA != null) _playerA.SetSlot(14, true); Choose(_hover - 1); }
                    if (_charging) { HoldOthers(_chargeBy); TickCharge(dt); TickSwings(dt); TickShots(dt); break; }
                    Fighter finishing = FinishingFighter();       // NO_AI_FLAG: while a finishing move runs nobody else thinks, moves or gains anything
                    _acc += dt;
                    while (_acc >= Frame)
                    {
                        _acc -= Frame; _frame++;
                        // tamerTickBattle: every frame of the fight, unless the order is Run (command 1), the boy faces the partner
                        // (entityLookAtLocation, at once); also during a finishing move. The arena has its own tamer code (STD)
                        if (!_running && !Tournament && _player != null && Partner != null && Partner.T != null)
                        {
                            Vector3 look = Partner.T.position - _player.position; look.y = 0f;
                            if (look.sqrMagnitude > 0.0001f) _player.rotation = _face(look);
                        }
                        if (_running)
                        {
                            // battle_tick.c, each frame of the Run order, whatever the partner is doing: handleFleeing moves the boy, FLEE_TIMER counts;
                            // BTL_isBattleFinished: FLEE_TIMER >= 0x29 ends the fight (always works, 41 frames after the order)
                            TickFlee();
                            if (++_runFrames > 40) { Fled = true; Sync(); ClearShots(); SetPhase(Phase.Fled); break; }
                        }
                        if (finishing == null) { FrameTick(Partner, Enemy); if (Enemy.Hp > 0) FrameTick(Enemy, FoeOf(Enemy)); foreach (var x in Extras) if (x.Hp > 0) FrameTick(x, FoeOf(x)); }
                        if (_shoutFrame >= 0 && ++_shoutFrame > 20) _shoutFrame = -1;
                    }
                    if (_phase != Phase.Fight) break;
                    if (finishing != null) HoldOthers(finishing);
                    else { Move(Partner, Enemy, dt); Move(Enemy, FoeOf(Enemy), dt); foreach (var x in Extras) if (x.Hp > 0) Move(x, FoeOf(x), dt); }
                    TickSwings(dt); TickShots(dt);
                    if (Tournament) { TournamentEnd(); break; }
                    if (Enemy.Hp <= 0) { ChoosePartnerTarget(); if (Enemy.Hp <= 0 && !NextEnemy() && AllFallen()) Finish(true); }
                    else if (TickFaint(dt)) Finish(false);
                    break;
                case Phase.Win:
                    TickSwings(dt); TickShots(dt);
                    // BTL_handleVictorySequence: after 41 frames sound 0x18, the boy cheers (slot 11) and the partner its win (42); the boxes come when both are done
                    if (!_fanfare && inPhase >= 41 * Frame)
                    {
                        _fanfare = true; Snd(0x18);
                        if (_playerA != null && _playerA.HasSlot(11)) _playerA.SetSlot(11, true);
                        if (Partner.A != null && Partner.A.HasSlot(CheerSlot)) Partner.A.SetSlot(CheerSlot, true);
                    }
                    if (_fanfare && !_cheered && (_playerA == null || !_playerA.Busy) && (Partner.A == null || !Partner.A.Busy))
                    {
                        _cheered = true;
                        if (_playerA != null) _playerA.SetSlot(_playerA.HasSlot(1) ? 1 : 0);
                        Stand(Partner);
                    }
                    if (_cheered)
                    {
                        // handleBattleEndBox: the end text is built, the stats box grows out of the partner and the Bits box out of the boy
                        if (_endStep < 0)
                        {
                            _endStep = 0; _skip = false; _endPress = false;
                            BuildEndText(_dropIds, _dropBy);
                            if (EndView != null)
                            {
                                RectInt partner = BoneSquare(Partner.T, Partner.A), boy = BoneSquare(_player, _playerA);
                                EndView.OpenBox(EndView.Stats, new RectInt(-88, -78, 176, 96), partner, 0, 2);
                                EndView.OpenBox(EndView.Bits, new RectInt(-88, 18, 176, EndView.HasText ? 66 : 31), boy, 0, 2);
                            }
                        }
                        // a press waits for the next game frame: most Unity frames have no 20 fps tick, and a press seen on one of those was lost
                        if (EndPress()) _endPress = true;
                        _countAcc += dt;
                        while (_countAcc >= Frame && _phase == Phase.Win) { _countAcc -= Frame; TickEnd(_endPress); _endPress = false; }
                    }
                    break;
                case Phase.Leave:
                    // ours: a 10-second safety limit (the game has none; running straight on, every enemy leaves the screen)
                    if (TickLeave() || inPhase > 10f) End();
                    break;
                case Phase.Lose:
                    End();      // BTL_deinitializeCombat: no victory part; the life-lost scene follows on the map
                    break;
                case Phase.Fled:
                    if (inPhase > 0.8f) End();
                    break;
                case Phase.TimeUp:
                case Phase.Cheer:
                    TickSwings(dt); TickShots(dt);
                    if (action) _tPress = true;
                    _tAcc += dt;
                    while (_tAcc >= Frame && Active) { _tAcc -= Frame; if (_phase == Phase.TimeUp) TickTimeUp(_tPress); else TickCheer(); _tPress = false; }
                    break;
            }
        }

        // ---------------------------------------------------------------- mod Online battle: the friend's copy of the host's fight (ours)

        // the copy keeps time, effects, numbers over the heads and the HP drain; the fighters' bodies do not move by their animations here (the host's
        // positions are set from outside), so what an animation would have moved is thrown away
        void PuppetTick(int side, bool action)
        {
            float dt = Time.deltaTime; _time += dt;
            // orders on: the friend picks orders on its own copy of the list (its own partner's Brains and techniques, as on the host's PC) and
            // they go to the host (NetOrder); Square with a full gauge orders the finishing move
            if (_phase == Phase.Fight && !Tamerless && NetOrder != null)
            {
                if (SquarePressed() && Partner.Finisher != null && Partner.Finish >= Partner.FinishMax) { Snd(3); Shout(Partner.Finisher.Name); NetOrder(FinishOrderName); }
                if (side != 0) { Snd(2); MoveHover(side); }
                if (action && _hover >= 1 && _hover <= _orders.Count) { Snd(3); Choose(_hover - 1); NetOrder(_order); }
            }
            if (_shoutFrame >= 0) { _shoutAcc += dt; while (_shoutAcc >= Frame && _shoutFrame >= 0) { _shoutAcc -= Frame; if (++_shoutFrame > 20) _shoutFrame = -1; } }
            if (Effects != null) Effects.Tick(dt, Camera.main);
            TickAura(dt);
            for (int i = _pops.Count - 1; i >= 0; i--) { if (_pops[i].Wait > 0f) { _pops[i].Wait -= dt; continue; } _pops[i].T += dt; if (_pops[i].T >= 31f / 20f) _pops.RemoveAt(i); }
            foreach (var f in new[] { Partner, Enemy }) if (f != null && f.A != null) f.A.TakeBodyMove();
            if (_tVersus && (VersusBusy == null || !VersusBusy())) _tVersus = false;
            if (_puppetEnd && !_tVersus) { _puppetEnd = false; Result = _puppetResult; WinnerSide = _puppetWinner; Active = false; return; }
            _hudAcc += dt;
            while (_hudAcc >= Frame) { _hudAcc -= Frame; DrainHp(Partner); DrainHp(Enemy); }
            TickFx(dt);
            if (_phase == Phase.Banner && TournamentStartDone) SetPhase(Phase.Fight);
        }

        /// <summary>The host's numbers for one fighter (the copy's own Partner or Enemy).</summary>
        public void PuppetNumbers(Fighter f, int hp, int mp, int finish)
        {
            if (f == null) return;
            f.Hp = hp; f.Mp = mp; f.Finish = Mathf.Min(finish, f.FinishMax);
            if (f.ShownHp < f.Hp) f.ShownHp = f.Hp;      // healing shows at once; damage drains as on the host
        }

        /// <summary>A technique the host's fight started: only its picture (the hits come as numbers and animations).</summary>
        public void PuppetSwing(Fighter from, int tech, Fighter to)
        {
            if (Effects == null || from == null || to == null) return;
            var t = MakeTech(tech, IsFinisherId(tech));
            if (Tournament && OnTechStart != null) OnTechStart(from, tech);      // the arena's camera choice, as on the host
            if (t.Self) return;
            from.FxRun = null; from.FxSection = -1;
            try
            {
                if (Effects.Start(tech, ActorFor(from), ActorFor(to), new List<EffectActor> { ActorFor(to) }, a => false, (at, v) => { }, a => true))
                { from.FxRun = Effects.LastRun; from.FxSection = Effects.LastSection; }
            }
            catch (Exception e) { Debug.LogWarning("[online] effect " + tech + ": " + e.Message); }
        }

        /// <summary>The host stopped this fighter's technique effect (it was hit, or started its next technique).</summary>
        public void PuppetStopFx(Fighter f) { RemoveMoveEffect(f); }

        /// <summary>A number over a head on the host's screen (damage, healing, power-ups).</summary>
        public void PuppetPop(string text, Vector3 pos, Color col, int icon) { _pops.Add(new Pop { Text = text, Pos = pos, Col = col, Icon = icon, Sent = true }); }

        /// <summary>The numbers over heads added since the last call (the host sends them).</summary>
        public List<KeyValuePair<Vector3, string>> TakeNewPops(List<Color> cols, List<int> icons)
        {
            var r = new List<KeyValuePair<Vector3, string>>();
            foreach (var p in _pops) if (!p.Sent && !p.Fixed && p.Wait <= 0f) { p.Sent = true; r.Add(new KeyValuePair<Vector3, string>(p.Pos, p.Text)); cols.Add(p.Col); icons.Add(p.Icon); }
            return r;
        }

        /// <summary>The host's fight is over: result 1 the copy's own partner won, -1 lost; winner side as WinnerSide (0 partner, 1 enemy, 2 a draw).</summary>
        /// A draw counts as lost, as on the host (STD_deinitializeCombat(1, 1)); the end waits until the draw letters are over.
        public void PuppetEnd(int result, int winnerSide)
        {
            _puppetEnd = true; _puppetResult = winnerSide == 2 ? -1 : result; _puppetWinner = winnerSide;
        }

        /// <summary>The host's fighter started a finishing move: its aura (80 frames).</summary>
        public void PuppetAura(Fighter f) { if (f == null) return; _chargeBy = f; ShowRing(true); }

        /// <summary>The host's fight ended in a draw: the draw letters, as on the host.</summary>
        public void PuppetVersus() { if (OnVersus != null && !_tVersus) { OnVersus(); _tVersus = true; } }

        /// <summary>The knock-out on the host's screen, for the copy's camera and WIN / LOSE scene.</summary>
        public void PuppetKnockOut(Fighter loser) { if (OnKnockOut != null && loser != null) OnKnockOut(loser); }

        // STD_tickFighterCounter: while the counter runs, BATTLE_FRAME_COUNT counts every frame and, while neither side lies in 0x2B, the counter
        // goes down one every 20th frame
        void TickTimer(float dt)
        {
            _tTimerAcc += dt;
            while (_tTimerAcc >= Frame)
            {
                _tTimerAcc -= Frame;
                _tFrames++;
                bool down = (Partner.A != null && Partner.A.Slot == DownSlot) || (Enemy.A != null && Enemy.A.Slot == DownSlot);
                if (!down && _tFrames % 20 == 0 && Timer != 0 && !NoTimeLimit) Timer--;
            }
        }

        // STD_checkEndCondition (with STD_tickPartnerAI / STD_tickEnemyAI's faint at 0 HP: STD_faintDigimon, animation 0x2B)
        void TournamentEnd()
        {
            foreach (var f in new[] { Partner, Enemy })
            {
                if (f.Hp > 0 || f.AnimReset != 0f) continue;
                if (f.A != null && f.A.HasSlot(DownSlot) && f.A.Slot != DownSlot) f.A.SetSlot(DownSlot, true);
                // STD_tickPartnerAI / STD_tickEnemyAI: the one that faints calls STD_selectRandomCamera(it, 5, 0) when the other one still stands
                if (_tKo.Add(f)) { var other = f == Partner ? Enemy : Partner; if (other.Hp > 0 && OnKnockOut != null) OnKnockOut(f); if (other.Hp > 0 && NetKnockOut != null) NetKnockOut(f); }
            }
            bool partnerDown = Fallen(Partner), enemyDown = Fallen(Enemy);
            if (partnerDown)
            {
                if (Enemy.Hp > 0) { StartCheer(1); return; }        // -1: STD_deinitializeCombat(1, 0)
                if (enemyDown) StartCheer(2);                      // 2: both down, STD_deinitializeCombat(1, 1)
                return;
            }
            if (enemyDown)
            {
                if (Partner.Hp > 0) StartCheer(0);                 // 1: STD_deinitializeCombat(0, 1); else wait for the partner's 0x2B
                return;
            }
            if (Timer != 0 || Partner.Hp <= 0 || Enemy.Hp <= 0) return;
            // time up: the side that lost more HP than it took off the other loses; the same amount is a draw
            TourDamage[0] = _tStartHp0 - Partner.Hp;
            TourDamage[1] = _tStartHp1 - Enemy.Hp;
            ClearShots();
            _tLoser = TourDamage[0] > TourDamage[1] ? 0 : TourDamage[0] < TourDamage[1] ? 1 : 2;
            _tCount = 0; _tAcc = 0f; _tPress = false;
            if (OnStopMusic != null) OnStopMusic();
            TimeUpWord = true;
            SetPhase(Phase.TimeUp);
            if (_tLoser == 2) { Stand(Partner); Stand(Enemy); }     // handleBattleIdle for both
            else
            {
                var winner = _tLoser == 0 ? Enemy : Partner; var loser = _tLoser == 0 ? Partner : Enemy;
                Stand(winner);
                if (loser.A != null && loser.A.HasSlot(DownSlot)) loser.A.SetSlot(DownSlot, true);     // STD_faintDigimon
            }
        }

        // STD_tickBattleResultScreen, one frame: a draw shows the box after two frames for up to 61 frames (Cross ends it); a win on damage shows the
        // box at frame 60 and ends at 121 (Cross from frame 61)
        void TickTimeUp(bool press)
        {
            if (_tLoser == 2)
            {
                if (_tCount == 2) { TimeUpWord = false; TimeUpBox = true; }
                if (_tCount >= 2 && (press || _tCount - 2 >= 0x3D)) { TimeUpBox = false; StartCheer(2); return; }
            }
            else
            {
                if (_tCount >= 0x3D && press) { TimeUpOver(); return; }
                if (_tCount == 0x3C) { TimeUpWord = false; TimeUpBox = true; }
                if (_tCount >= 0x79) { TimeUpOver(); return; }
            }
            _tCount++;
        }

        // the end of STD_tickBattleResultScreen with a loser: STD_selectRandomCamera(loser, 5, 0) (the WIN / LOSE scene), the box goes
        void TimeUpOver()
        {
            TimeUpBox = false;
            var loser = _tLoser == 0 ? Partner : Enemy;
            if (OnKnockOut != null) OnKnockOut(loser);
            if (NetKnockOut != null) NetKnockOut(loser);
            StartCheer(_tLoser == 0 ? 1 : 0);
        }

        // STD_deinitializeCombat(a, b): the winner a plays 0x2A for 100 frames (the partner) or 120 (the enemy) to playMusic(STD_MUSIC, 3 / 4);
        // a draw has no winner. 20 more frames, then the partner's Offense, Defense, Speed and Brains are back (ours only lowered the fight's
        // copy) and it gets a fifth of its HP and MP back. Not ported: the versus picture (STD_addVersusModelScene) after a draw that was not a time-up.
        void StartCheer(int winner)
        {
            ClearShots();
            WinnerSide = winner;
            Result = winner == 0 ? 1 : -1;
            Won = Result == 1;
            _tCheer = winner == 2 ? 0 : (winner == 0 ? 100 : 120);
            _tCount = 0; _tAcc = 0f;
            if (OnStopMusic != null) OnStopMusic();
            if (winner == 2 && OnVersus != null) { OnVersus(); _tVersus = true; }      // STD_addVersusModelScene, waited for until it is over
            if (winner == 2 && NetVersus != null) NetVersus();
            if (winner != 2)
            {
                var f = winner == 0 ? Partner : Enemy;
                if (f.A != null && f.A.HasSlot(CheerSlot)) f.A.SetSlot(CheerSlot, true);
                if (TournamentMusic != null) TournamentMusic(winner == 0 ? 3 : 4);
            }
            SetPhase(Phase.Cheer);
        }

        void TickCheer()
        {
            if (_tVersus) { if (VersusBusy != null && VersusBusy()) return; _tVersus = false; }
            _tCount++;
            if (_tCount <= _tCheer)
            {
                var f = WinnerSide == 0 ? Partner : Enemy;
                if (f.A != null && !f.A.Busy && f.A.HasSlot(CheerSlot)) f.A.SetSlot(CheerSlot, true);
                return;
            }
            if (_tCount < _tCheer + 20) return;
            if (OnStopMusic != null) OnStopMusic();
            Partner.Hp = Mathf.Min(Partner.MaxHp, Mathf.Max(0, Partner.Hp) + Partner.MaxHp / 5);
            Partner.Mp = Mathf.Min(Partner.MaxMp, Mathf.Max(0, Partner.Mp) + Partner.MaxMp / 5);
            Sync();
            End();
        }

        bool HasLeft() { foreach (int v in _left) if (v > 0) return true; return false; }

        // the result boxes (battle_ui.c): after 100 frames one stat point a frame, then one Bit a frame, each with sound 0x16; 0x17 when the Bits are done
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
            f.FleeTo = FleeOf(data, t);
            f.Rec42 = data.Rec42;
            _enemyBits += data.Bits;
            t.rotation = _face(Partner.T.position - t.position);
            Stand(f);
            if (a != null) a.BodyMovesItself = true;
            Extras.Add(f);
            _enemySlots.Add(f);
        }

        // the partner turns to the next living enemy; false when there is none
        bool NextEnemy()
        {
            foreach (var x in Extras)
                if (x.Hp > 0) { Extras.Remove(x); Extras.Add(Enemy); Enemy = x; return true; }
            return false;
        }

        // the one an enemy is after: the partner, or the other enemy that hit it (Turned) until its next technique is over.
        // Ours: one that has gone down sends it back to the partner
        Fighter FoeOf(Fighter f)
        {
            if (f.Turned != null && f.Turned.Hp <= 0) f.Turned = null;
            return f.Turned ?? Partner;
        }

        static readonly int[] TargetAttackerChance = { 70, 40, 30, 0 };     // TARGET_ATTACKER_CHANCE (BTL_REL), by charge mode

        // the enemies in their fight slots (1, 2, 3): the order the original's target loops go through. Enemy / Extras change order when the
        // partner changes target; this list does not
        readonly List<Fighter> _enemySlots = new List<Fighter>();

        // the partner's target becomes x (fighter->targetId): ours keeps the partner's target in Enemy
        void SetPartnerTarget(Fighter x)
        {
            int k = Extras.IndexOf(x);
            if (k < 0) return;
            Extras[k] = Enemy; Enemy = x;
            if (Partner.Pending != null) Partner.PendingTarget = x;
        }

        // BTL_calculateTargetScore(partner, other): TYPE_FACTORS of the partner's first type against the other's first type, turned round
        // (20 -> 2, 15 -> 5, 5 -> 15, 2 -> 20), then partner HP % + that x (partner Defense x 100 / 999) / 10. The HP and Defense are the
        // partner's own (st = &self->stats), so between enemies only the type match differs; lower = the better one to go after
        int TargetScore(Fighter self, Fighter other)
        {
            int m = DigimonStatsCatalog.Effect(FirstType(self), FirstType(other));
            m = m == 20 ? 2 : m == 15 ? 5 : m == 5 ? 15 : m == 2 ? 20 : m;
            return self.Hp * 100 / Mathf.Max(1, self.MaxHp) + m * (self.Def * 100 / 999) / 10;
        }

        // BTL_getWeakestEnemy: the lowest score among the standing enemies other than the current target (first in slot order on a tie).
        // Its score out is the last one worked out, not the lowest (as the original writes *outScore for every candidate)
        Fighter WeakestEnemy(Fighter self, Fighter current, ref int score)
        {
            int best = 1000; Fighter pick = null;
            foreach (var e in _enemySlots)
            {
                if (e.Hp <= 0 || e == current) continue;
                score = TargetScore(self, e);
                if (score < best) { best = score; pick = e; }
            }
            return pick;
        }

        // BTL_getNearestEnemy: the standing enemy nearest the partner (first in slot order on a tie)
        Fighter NearestEnemy(Fighter self)
        {
            float bestD = float.MaxValue; Fighter pick = null;
            foreach (var e in _enemySlots)
            {
                if (e.Hp <= 0) continue;
                float d = (Flat(e.T.position) - Flat(self.T.position)).sqrMagnitude;
                if (d < bestD) { bestD = d; pick = e; }
            }
            return pick;
        }

        // BTL_selectFighterTarget(partner, targetId, 0), run each time the partner picks a technique unless FIGHTER_FLAG_10 holds its target:
        // - order Change (command 7) with a target still standing: kept
        // - Brains 300+: enemies under a condition (poison, confusion, stun, flat): one at random of 2+, or the one; else the weakest
        //   (BTL_getWeakestEnemy) when the target is down, or when the target scores 40+ more than it
        // - Brains 150+: a standing target is kept, else the weakest
        // - below: the nearest
        void ChoosePartnerTarget()
        {
            var me = Partner;
            if (me == null || _enemySlots.Count < 2) return;
            Fighter cur = Enemy, pick = null;
            bool curUp = cur != null && cur.Hp > 0;
            if (_order == ChangeOrder && curUp) return;
            int score = 0;
            if (me.Brain >= 300)
            {
                var hurt = new List<Fighter>();
                foreach (var e in _enemySlots) if (e.Hp > 0 && (e.Poisoned || e.Confused || e.Stunned || e.Flattened)) hurt.Add(e);
                if (hurt.Count >= 2) pick = hurt[_rng.Next(hurt.Count)];
                else if (hurt.Count == 1) pick = hurt[0];
                else
                {
                    var choice = WeakestEnemy(me, cur, ref score);
                    if (!curUp) pick = choice;
                    else if (choice != null && TargetScore(me, cur) - score >= 40) pick = choice;
                }
            }
            else if (me.Brain >= 150) { if (!curUp) pick = WeakestEnemy(me, cur, ref score); }
            else pick = NearestEnemy(me);
            if (pick != null && pick != Enemy)
            {
                SetPartnerTarget(pick);
                Debug.Log("[battle] " + me.Name + " picks " + pick.Name + " (Brains " + me.Brain + ")");
            }
        }

        // BTL_retargetAfterHit, on every landed blow (before its damage):
        // - the partner keeps its target when (TARGET_ATTACKER_CHANCE[charge mode] - discipline) <= a roll of 100 and that target still stands;
        //   else BTL_selectFighterTarget(partner, the attacker's slot, 1): it turns to the attacker unless that one is down
        // - an enemy: BTL_selectFighterTarget(enemy, the attacker's slot, 1): with record +42 = 1 or a roll of 71+ it goes after the partner;
        //   else after the attacker (the partner, or an enemy whose whole-area technique hit it) unless that one is down (FIGHTER_FLAG_10:
        //   kept until its next technique is over, then back to the partner, BTL_tickEnemyAI)
        void RetargetAfterHit(Fighter d, Fighter a)
        {
            if (a == null || a == d) return;
            if (d == Partner)
            {
                if (TargetAttackerChance[Mathf.Clamp(d.Style, 0, 3)] - _s.Discipline <= _rng.Next(100) && Enemy.Hp > 0) return;
                if (a.Hp <= 0) return;
                Partner.TargetHeld = true;      // FIGHTER_FLAG_10: its own target choice waits until its next technique is over
                if (a == Enemy || !Extras.Contains(a)) return;
                SetPartnerTarget(a);
                Debug.Log("[battle] " + Partner.Name + " turns to " + a.Name + ", who hit it");
                return;
            }
            if (d.Rec42 == 1 || _rng.Next(100) >= 71) d.Turned = null;
            else if (a.Hp > 0)
            {
                d.Turned = a == Partner ? null : a;
                if (d.Turned != null) Debug.Log("[battle] " + d.Name + " turns on " + a.Name + ", who hit it");
            }
            if (d.Pending != null) d.PendingTarget = FoeOf(d);
        }

        void SetPhase(Phase p) { _phase = p; _phaseStart = _time; if (p == Phase.Banner) { Snd(0x11); StartWord(); } }

        // after the result boxes (BTL_handleVictorySequence): every enemy gets up (0x2C); in a scripted fight they then stand (0x21);
        // in a wild one they run (0x24) towards their map record's flee point until all of them are off the screen (entityIsOffScreen 0x140 x 0xF0)
        void StartLeave()
        {
            SetPhase(Phase.Leave);
            foreach (var f in Enemies()) { f.Leaving = 0; if (f.A != null && f.A.HasSlot(0x2C)) f.A.SetSlot(0x2C, true); }
        }

        IEnumerable<Fighter> Enemies() { yield return Enemy; foreach (var x in Extras) yield return x; }

        bool TickLeave()
        {
            Camera cam = Camera.main;
            bool done = true;
            foreach (var f in Enemies())
            {
                if (f.T == null) continue;
                if (f.Leaving == 0)
                {
                    if (f.A != null && f.A.Busy) { done = false; continue; }
                    if (NoFlee) { if (f.A != null && f.A.HasSlot(0x21)) f.A.SetSlot(0x21); f.Leaving = 2; continue; }
                    // BTL_handleVictorySequence: run 0x24 at double speed (animFlag 4) and entityLookAtLocation(flee) once, when it starts;
                    // after that it is never turned again, so it runs straight on past its flee point until it is off the screen
                    if (f.A != null && f.A.HasSlot(0x24)) { f.A.SetSlot(0x24); f.A.DoubleSpeed = true; }
                    Vector3 d = f.FleeTo - f.T.position; d.y = 0f;
                    if (d.sqrMagnitude > 0.0001f) f.T.rotation = _face(d);
                    f.Leaving = 1;
                }
                if (f.Leaving != 1) continue;
                if (f.A != null) f.T.position -= f.T.forward * f.A.TakeBodyMove();
                bool off = cam == null || OffScreen(cam, f.T.position, f.Info != null ? f.Info.Radius : 0, f.Info != null ? f.Info.Height : 0);
                if (!off) done = false;
            }
            return done;
        }

        void End()
        {
            if (_phase == Phase.Win || _phase == Phase.Leave) { int[] idx = { 4, 5, 0, 1, 2, 3 }; for (int i = 0; i < 6; i++) _s.SetStat(idx[i], _before[i] + _gain[i]); _s.Money = Math.Min(_s.Money + _bits, 999999); if (Partner.A != null) Partner.A.SetSlot(0); }      // tickBitBox: MONEY >= 1000000 -> 999999
            ClearShots();
            Active = false;
            foreach (var f in AllFighters()) { f.Turned = null; if (f.CounteredBy != null) { f.CounteredBy = null; if (f.A != null) f.A.Playing = true; } }
            foreach (var f in new[] { Partner, Enemy }) if (f.A != null) f.A.BodyMovesItself = false;
            foreach (var f in Extras) if (f.A != null) f.A.BodyMovesItself = false;
            // BTL_deinitializeCombat: animFlag &= 0xFB (the double-speed run ends)
            foreach (var f in Enemies()) if (f.A != null) f.A.DoubleSpeed = false;
        }

        // left moves on along the row, right back; Run is skipped in a scripted fight (main/battle_tick.c)
        void MoveHover(int side)
        {
            int last = _orders.Count;
            bool skipRun = NoFlee && _orders.Count > 0 && _orders[0] == RunOrder;      // the arena's list has no Run to jump over
            if (side < 0) { _hover++; if (skipRun && _hover == 1) _hover++; if (_hover > last) _hover = skipRun ? 2 : 1; }
            else { _hover--; if (skipRun && _hover == 1) _hover--; if (_hover <= 0) _hover = last; }
        }

        void Choose(int i)
        {
            string o = _orders[i];
            if (o == RunOrder)
            {
                if (NoFlee) return;
                _running = true; _runFrames = 0; _order = RunOrder; _partnerHeld = false; Shout("Run!");
                // command 1: the boy's run (animation 3) starts at once, over the animation 0xE of the order
                if (_playerA != null) _playerA.SetSlot(PlayerRunSlot);
                return;
            }
            // command 7: the order stays Change (techniques picked as Your Call, BTL_SAVED_CHARGE_MODE) and the partner keeps the target it
            // turned to while that one stands (BTL_selectFighterTarget)
            if (o == ChangeOrder) { _running = false; _order = ChangeOrder; Shout("Change!"); NextEnemy(); return; }
            _running = false;
            _order = o;
            int k = System.Array.IndexOf(OrderNames, o);
            Shout(k >= 0 ? OrderShouts[k] : o);
        }

        void Shout(string s) { _shout = OrderZh(s); _shoutFrame = 0; }

        // ours (translation): the order names and shouts (our constants; the game draws its own) in the current language
        static readonly Dictionary<string, string> OrderZhTable = new Dictionary<string, string>
        {
            { "Run", "逃跑" }, { "Attack", "攻擊" }, { "Your Call", "你決定" }, { "Moderate", "適度" }, { "Distance", "保持距離" }, { "Defensive", "防守" }, { "Change", "交換" },
            { "Run!", "逃跑！" }, { "Attack!", "攻擊！" }, { "Your Call!", "你決定！" }, { "Moderate!", "適度！" }, { "Distance!", "保持距離！" }, { "Defense!", "防守！" }, { "Change!", "交換！" },
        };
        static string OrderZh(string s) { string z; return s != null && Tr.Chinese && OrderZhTable.TryGetValue(s, out z) ? z : s; }

        // a fighter at 0 HP first finishes its hit reaction, then collapses (slot 43, BTL_faintDigimon)
        void Animate(Fighter f) { if (f != null && f.A != null && f.AnimReset > 0f && _time >= f.AnimReset) { f.AnimReset = 0f; if (f.Hp > 0) Stand(f); else if (f.A.HasSlot(DownSlot)) f.A.SetSlot(DownSlot, true); } }

        // BTL_isBattleFinished: won once every enemy's collapse animation has played to its end
        bool Fallen(Fighter f) { return f.Hp <= 0 && (f.A == null || !f.A.HasSlot(DownSlot) ? f.AnimReset == 0f : f.A.Slot == DownSlot && !f.A.Busy); }
        bool AllFallen() { if (!Fallen(Enemy)) return false; foreach (var x in Extras) if (!Fallen(x)) return false; return true; }

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
        bool _running; int _runFrames; Camera _view;                       // Run (FLEE_TIMER): the boy runs off, the picture fades to black from frame 20, the fight ends after 41 frames
        /// <summary>handleFleeing: where the boy runs once he is back on the tile he came in on: 3000 units out of the map from his arrival point,
        /// against its exit's rotation (MAP_WARPS spawn of CURRENT_EXIT); the room sets it (Unity position).</summary>
        public Vector3 FleeOut;
        static readonly int[] PowerRank = { 50, 20, 5 }, CostRank = { 50, 20, 10 }, ReachRank = { 10, 5, 0 };

        static int EffectBonus(int effect) { return effect == 2 ? 1 : effect == 5 ? 3 : effect == 10 ? 5 : effect == 15 ? 7 : effect == 20 ? 10 : 0; }
        static int FirstType(Fighter f) { return f.Info != null ? f.Info.Specials[0] : 255; }

        // code 0x8005D378: what makes a technique usable now
        // BTL_isMoveUsable: technique 0x2D and the finishing moves (0x3A-0x70) are never picked as normal moves; an enemy's finisher (slot 3) comes only from its full gauge
        bool Usable(Fighter me, Tech t) { return t != null && t.Id != 45 && !IsFinisherId(t.Id) && me.Mp >= Cost(me, t, me == Partner) && (!t.Self || me.SelfUses > 0); }

        static bool IsFinisherId(int id) { return id >= 0x3A && id < 0x71; }

        // code 0x8005D6E4: how full the energy bar must be before the technique starts, by the fighter's style
        static bool Ready(Fighter me, Tech t) { return me.Style == 2 ? me.Energy >= 100 : me.Style == 1 ? me.Energy == 100 || me.Energy >= t.Power : me.Energy > 0; }

        // runs once per game frame: the decompilation's BTL_tickPartnerAI / BTL_tickEnemyAI / BTL_tickFighterStates / BTL_tickStatusEffects
        void FrameTick(Fighter me, Fighter foe)
        {
            if (me.Hp <= 0) return;
            if (me.Finisher != null) me.Finish = Mathf.Min(me.FinishMax, me.Finish + 1);
            if (_frame % 20 == 0 && me.Bore > 0) me.Bore--;
            if (me.Brain <= 300 && me.Stall == 0 && _frame % ((me.Brain / 2 + 1) * 20) == 0)
            {
                int chance = me == Partner ? 70 - _s.Discipline : (300 - me.Brain) / 4;
                if (_rng.Next(100) < chance) { me.Stall = StallFrames; me.Pending = null; }
            }
            if (me.Stall == 0 && _frame % 2 == 0) me.Energy = Mathf.Min(100, me.Energy + me.Spd / 100 + 1);
            if (me.Wait > 1) me.Wait--;
            if (me.Cool > 1) me.Cool--;
            if (me.Stall > 0) { me.Stall--; return; }
            if (foe.Hp <= 0 || _time < me.NextAct || me.Stunned || FlatBusy(me) || me.Pending != null || me.Wait > 1 || me.Cool > 1 || (me == Partner && _running)) return;
            foreach (var sw in _swings) if (sw.From == me) return;
            if (me == Partner && _wantFinish && me.Finisher != null) { _wantFinish = false; me.Pending = me.Finisher; me.PendingTarget = foe; return; }
            // mod Online battle: the friend ordered its finishing move (its gauge was checked when the order came)
            if (me != Partner && me.Commanded && me.WantFinish && me.Finisher != null) { me.WantFinish = false; me.Pending = me.Finisher; me.PendingTarget = foe; return; }
            if (me != Partner && !me.Commanded && me.Finish >= me.FinishMax && me.Finisher != null) { me.Pending = me.Finisher; me.PendingTarget = foe; return; }
            // BTL_tickPartnerAI: before it picks a technique (not confused), BTL_selectFighterTarget unless FIGHTER_FLAG_10 holds the target;
            // under a technique order (8-10) only when the target is down
            if (me == Partner && !me.Confused && !me.TargetHeld)
            {
                bool techOrder = false;
                foreach (var mt in me.Moves) if (mt != null && mt.Name == me.Order) techOrder = true;
                if (!techOrder || Enemy.Hp <= 0) { ChoosePartnerTarget(); foe = Enemy; }
            }
            me.Blind = me.Confused && _rng.Next(10) < 7;       // a confused fighter mostly fires at nothing, in the way it happens to face
            if (me.Blind) me.T.rotation *= Quaternion.Euler(0f, _rng.Next(-45, 45), 0f);
            Tech t = me.Confused ? PickAny(me) : me.Commanded ? PickPartner(me, foe) : PickEnemy(me);
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
            foreach (var t in me.Moves) if (t != null && t.Name == me.Order) return Usable(me, t) ? t : null;
            me.Style = me.Order == ModerateOrder ? 2 : 0;
            if (me.Order == AttackOrder) return PickBest(me, foe, true);
            if (me.Order == ModerateOrder) return PickBest(me, foe, false);
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
                if (t.Self) { w[i] += me.Bore + EffectBonus(DigimonStatsCatalog.Effect(t.Element, FirstType(me))); continue; }
                w[i] += EffectBonus(DigimonStatsCatalog.Effect(t.Element, FirstType(foe)));
                // BTL_selectPartnerMove: a technique that covers the whole area (range 3) with Brains 200+ and two or more enemies standing
                // (BTL_countLivingEnemies) weighs + enemies x 10 (charge mode 0) or x 15 (1)
                if (t.Wide && me.Brain >= 200 && (me.Style == 0 || me.Style == 1))
                {
                    int count = 0;
                    foreach (var e in Enemies()) if (e.Hp > 0) count++;
                    if (count >= 2) w[i] += count * (me.Style == 0 ? 10 : 15);
                }
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
            // BTL_selectEnemyMove: another enemy still standing (not this one) and record +42 = 0: a whole-area technique (range 3) weighs + 20
            bool others = false;
            foreach (var e in Enemies()) if (e != me && e.Hp > 0) others = true;
            for (int i = 0; i < 4; i++)
            {
                var t = me.Moves[i];
                w[i] = me.Weights[i];
                if (t != null && !Usable(me, t)) w[i] = 0;
                else if (t != null)
                {
                    any = true;
                    if (others && me.Rec42 == 0 && t.Wide) w[i] += 20;
                    if (t.Self) w[i] += me.Bore;
                }
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
            if (me.Hp <= 0 || me.CounteredBy != null) return;      // BTL_tickAttackState: frozen while countered
            foreach (var sw in _swings) if (sw.From == me) return;
            if (me.AnimReset > 0f || _time < me.NextAct) return;
            if (FlatBusy(me)) { MoveFlat(me, foe, dt); return; }        // BTL_tickFlatState (BattleStatus.cs)
            if (me.Stunned) { if (me.A != null && me.A.Slot != StunSlot) me.A.SetSlot(StunSlot); return; }     // BTL_tickStunState: animation 0x22, held
            if (me.Stall > 0) { if (me.A != null && me.A.Slot != StallSlot) me.A.SetSlot(StallSlot); return; }
            bool hurt = me.Poisoned || me.Hp <= me.MaxHp / 5;                    // walks when poisoned or under 20% HP, else runs
            int walk = me.A == null ? 2 : hurt && me.A.HasSlot(WalkSlot) ? WalkSlot : me.A.HasSlot(RunSlot) ? RunSlot : 2;
            bool moving = me.A != null && (me.A.Slot == WalkSlot || me.A.Slot == RunSlot);
            float dSq = Mathf.Pow(Vector3.Distance(Flat(me.T.position), Flat(foe.T.position)) * 100f, 2f);
            float radii = (me.Info != null ? me.Info.Radius : 0) + (foe.Info != null ? foe.Info.Radius : 0);
            // Run: BTL_handlePartnerMoveCommand comes first on every path of the partner's AI (queued move, charge, cooldown), so it is before the cooldown here
            if (me == Partner && _running) { RunAfterBoy(me, walk); return; }
            if (me.Cool > 1)
            {
                if (me.Style == 2) BackAway(me, foe, dt, walk); else Maintain(me, foe, dt, walk, dSq, radii, moving);
                return;
            }
            // the finishing move (STD_handleCommands / BTL: Square sets the command to 0xB) replaces the order until it has gone off
            bool finishing = me.Pending != null && me.Pending.Finisher;
            if (me.Commanded && !finishing)
            {
                if (me.Order == DefensiveOrder) { Hold(me, foe, dt); return; }
                if (me.Order == DistanceOrder)
                {
                    float keep = MaxReachSq(foe) + 640000f + (moving ? 160000f : 0f);
                    if (dSq < keep) BackAway(me, foe, dt, walk); else { me.Collided = false; Hold(me, foe, dt); }
                    return;
                }
            }
            var t = me.Pending;
            if (t != null && Ready(me, t))
            {
                // a finishing move is queued from the gauge (BTL_tickEnemyAI / command 0xB) and never checked again; BTL_isMoveUsable only screens normal moves
                if (!t.Finisher && !Usable(me, t)) { me.Pending = null; return; }
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
        // BTL_backAwayFromTarget. Headings in game units (4096 a turn; front = (-sin, -cos)). Straight away until something blocks; then
        // BTL_findUnblockedRotation picks a free heading, and from then on (hasCollidedWhileDistanceCmd) it turns back towards "away" by at most 20 a frame.
        void BackAway(Fighter me, Fighter foe, float dt, int walk)
        {
            int toward = AngleOf(foe.T.position - me.T.position), away = (toward + 0x800) & 0xFFF;
            int orig = me.Head < 0 ? away : me.Head, rot;
            if (!me.Collided)
            {
                rot = away;
                int hit = Blocked(me, rot);
                if (hit != -1) { me.Collided = true; rot = Unblocked(me, rot, hit, orig); }
            }
            else if (orig == FighterAngle(foe))
            {
                rot = (orig + 0x400) & 0xFFF;
                if (Blocked(me, rot) != -1) { rot = (orig + 0xC00) & 0xFFF; if (Blocked(me, rot) != -1) { int hit = Blocked(me, orig); rot = hit != -1 ? Unblocked(me, orig, hit, orig) : orig; } }
            }
            else
            {
                int ccw = (orig - away) & 0xFFF, cw = (away - orig) & 0xFFF, turn = Mathf.Max(1, Mathf.RoundToInt(20f * dt * 20f));
                rot = ccw - cw < 0 ? (ccw > turn ? orig - turn : orig - ccw) : (cw > turn ? orig + turn : orig + cw);
                rot &= 0xFFF;
                if (Blocked(me, rot) != -1) { int hit = Blocked(me, orig); rot = hit != -1 ? Unblocked(me, orig, hit, orig) : orig; }
            }
            if (rot < 0) { Hold(me, foe, dt); return; }
            me.Head = rot;
            Step(me, Front(rot), foe, dt, walk);
        }

        static readonly int[] Cardinal = { 0, 1024, 2048, 3072 };

        // BTL_findUnblockedRotation / STD_findUnblockedRotation: during the fight (GAME_STATE 4) a wall (10) counts as the fight's edge (11): the
        // nearest free quarter turns are tried; anything else, or no way out, goes to collisionGrace
        int Unblocked(Fighter me, int rot, int hit, int orig)
        {
            if (hit == 10) hit = 11;
            if (hit != 11) return Grace(me, orig);
            int b = Cardinal[(rot & 0xFFF) / 1024], i;
            int[] cand = { b, (b + 0x400) & 0xFFF, 0 };
            for (i = 0; i < 2; i++) if (Blocked(me, cand[i]) == -1) break;
            int start = i == 0 ? 0xC00 : i == 1 ? 0x400 : 0x800;
            int k;
            for (k = 0; k < 3; k++)
            {
                cand[k] = (b + start + k * 0x200) & 0xFFF;
                if (Blocked(me, cand[k]) != -1) break;
            }
            if (i >= 2 && k == 3) { int back = (orig + 0x800) & 0xFFF; if (Blocked(me, back) == -1) return back; }
            if (k != 3) return Grace(me, orig);
            int r = (cand[0] + _rng.Next(0x400)) & 0xFFF;
            return Blocked(me, r) == -1 ? r : Grace(me, orig);
        }

        // collisionGrace (map.c): free ahead = as it is; else the first free of COLLISION_GRACE_ROTATION[heading / 512] (exe 0x801289F4, four
        // headings each), turned towards by at most 0x50 a frame the shorter way; none free: 32 further round
        int Grace(Fighter me, int start)
        {
            start &= 0xFFF;
            if (Blocked(me, start) == -1) return start;
            int dir = start / 512;
            for (int i = 0; i < 4; i++)
            {
                int r = GraceRotation(dir, i);
                if (r < 0 || Blocked(me, r) != -1) continue;
                int cw = (start - r) & 0xFFF, ccw = (r - start) & 0xFFF;
                if (cw - ccw < 0) return cw >= 0x51 ? (start + 0xFB0) & 0xFFF : (start - cw) & 0xFFF;
                return ccw >= 0x51 ? (start + 0x50) & 0xFFF : (start + ccw) & 0xFFF;
            }
            return (start + 32) & 0xFFF;
        }

        int GraceRotation(int dir, int i)
        {
            if (Exe == null) return -1;
            uint load = BitConverter.ToUInt32(Exe, 0x18);
            return BitConverter.ToInt16(Exe, (int)(0x801289F4 - load + 0x800) + (dir * 4 + i) * 2) & 0xFFF;
        }

        // entityCheckCollision with the fight box (280 x 200): 11 = it would leave the fight area, 10 = a wall, -1 = free. Other Digimon are not tested here.
        int Blocked(Fighter me, int rot)
        {
            Vector3 next = me.T.position + Front(rot) * 0.2f;
            if (!Tournament && LeavesScreen(me.T.position, next, 280, 200, RadiusOf(me))) return 11;      // the arena's code has no fight box
            if (Walkable != null && !Walkable(next) && Walkable(me.T.position)) return 10;     // already in a wall: let it walk out (see OnGround)
            return -1;
        }

        // Fix for the original's "stuck in the scenery" glitches (GameFAQs Glitch FAQ C2, and E: the boy in Factorial Town): a fighter or the
        // boy never steps from walkable ground into a wall; it slides along it, and one that is already inside a wall may walk out.
        // False when no part of the step could be taken.
        bool OnGround(Transform t, Vector3 by)
        {
            Vector3 pos = t.position;
            if (Walkable == null || !Walkable(pos) || Walkable(pos + by)) { t.position = pos + by; return true; }
            if (Walkable(pos + new Vector3(by.x, 0, 0))) { t.position = pos + new Vector3(by.x, 0, 0); return true; }
            if (Walkable(pos + new Vector3(0, 0, by.z))) { t.position = pos + new Vector3(0, 0, by.z); return true; }
            return false;
        }

        static Vector3 Front(int a) { float r = a * Mathf.PI * 2f / 4096f; return new Vector3(-Mathf.Sin(r), 0f, -Mathf.Cos(r)); }
        static int AngleOf(Vector3 d) { return ((int)(Mathf.Atan2(-d.x, -d.z) / (Mathf.PI * 2f) * 4096f)) & 0xFFF; }
        int FighterAngle(Fighter f) { return f.Head >= 0 ? f.Head : -2; }

        static float RadiusOf(Fighter f) { return (f.Info != null ? f.Info.Radius : 0) * 0.01f; }

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
            OnGround(me.T, dir * (me.A != null ? me.A.TakeForward() : 0f));      // the animation carries it: one step, one step sound
            me.T.rotation = Quaternion.RotateTowards(me.T.rotation, _face(dir), 400f * dt);
            if (me.A != null && me.A.Slot != walk) me.A.SetSlot(walk);
        }

        // the partner's MP cost: 5, 10, 15, 20% less at 700, 800, 900, 999 Brains, and half as much again while it is injured or sick
        // (PARTNER_PARA.condition & 0x60). The two places do it in their own order and rounding:
        // paying (BTL_startQueuedMove): + half first, then cost x 19/20, 9/10, 17/20, 4/5;
        // the "can it pay" test (BTL_isMoveUsable): mp - mp/20, mp/10, mp x 15/100, mp/5 first, then + half
        int Cost(Fighter me, Tech t, bool partner, bool paying = false)
        {
            int c = t.Mp;
            bool weak = partner && (_s.Para.Condition & 0x60) != 0;
            if (paying)
            {
                if (weak) c += c / 2;
                if (partner && me.Brain >= 700) c = me.Brain >= 999 ? c * 4 / 5 : me.Brain >= 900 ? c * 17 / 20 : me.Brain >= 800 ? c * 9 / 10 : c * 19 / 20;
                return c;
            }
            if (partner && me.Brain >= 700) c -= me.Brain >= 999 ? c / 5 : me.Brain >= 900 ? c * 15 / 100 : me.Brain >= 800 ? c / 10 : c / 20;
            if (weak) c += c / 2;
            return c;
        }

        void Begin(Fighter me, Fighter target, Tech t)
        {
            me.Pending = null; me.Collided = false;
            if (!me.Blind) me.T.rotation = _face(target.T.position - me.T.position);
            if (me == Partner || !NoFlee) me.Mp -= Cost(me, t, me == Partner, true);     // enemies in scripted fights pay nothing
            if (t.Self) me.SelfUses--;
            me.Energy = Mathf.Max(-155, me.Energy - t.Power);
            if (me != Partner && t.Id < 58 && !_seen.Contains(t.Id) && _seen.Count < 12) _seen.Add(t.Id);
            if (t.Finisher)
            {
                me.Finish = 0;
                // BTL_tickMeleeAttack / BTL_tickRangedAttack: any fighter's finishing move first gets the aura and holds 80 frames (BTL_FINISHER_TIMER) facing
                // its target while nobody else acts; only the partner fills a bar (BTL_initializeFinisherChargeup)
                _charging = true; _chargeBy = me; _chargeT = 0f; _chargeCount = 0; _chargeTech = t; _chargeFoe = target; ShowRing(true);
                if (NetAura != null) NetAura(me);
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
            StartEffect(swing);      // BTL_setupMoveExecution starts the effect (BTL_startEFE) the moment the move starts; its program times itself
            swing.DrawnAtStart = Effects != null ? Effects.Drawn : 0;
            if (Tournament && OnTechStart != null) OnTechStart(me, tech.Id);
            if (NetSwing != null) NetSwing(me, tech.Id, foe);
            Play(me, slot, length);
            Sync();
        }

        // the partner's finishing move: 80 frames in which L1 and R1 (Q and E) fill a bar; from just over half it multiplies the blow, up to double
        bool _charging; float _chargeT; int _chargeCount; Tech _chargeTech; Fighter _chargeFoe, _chargeBy;
        const float ChargeSeconds = 80f / 20f;

        // Square: fires the finishing move once FINISH!! shows
        static bool SquarePressed()
        {
            return !InputLocked && Pad.Pressed(PadButton.Square);
        }

        // the finisher aura (BTL_addFinisherAura, 80 frames; BTL_renderFinisherAura). BTL_REL.BIN starts with its TIM (VRAM 768,448) and holds its TMD at 0xD20:
        // objects 0-2 the light column in three turns (one every 2 frames), 3 the ground disc, 4 a wider disc turned at random. Added to the picture,
        // colour 60 (90 for the disc), as BTL_initializeFinisherAuraModel sets them. The ten sparks of the first 7 frames are not drawn yet.
        Mesh[] _auraMesh; Material _auraMat; readonly MaterialPropertyBlock _auraMp = new MaterialPropertyBlock();
        int _auraFrame = -1; float _auraAcc; int _auraSpin;

        public void LoadAura(byte[] btl)
        {
            try
            {
                TimImage tim;
                if (btl == null || !TimImage.TryParse(btl, 0, out tim)) return;
                const int At = 0xD20;
                var buf = new byte[btl.Length - At + 4];
                buf[0] = 4; Array.Copy(btl, At, buf, 4, btl.Length - At);
                var model = MmdModel.Parse(buf);
                _auraMesh = new Mesh[model.Parts.Count];
                for (int k = 0; k < _auraMesh.Length; k++)
                {
                    var part = model.Parts[k];
                    var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
                    foreach (var f in part.Faces)
                        for (int n = 0; n < 3; n++)
                        {
                            int q = f.V[n] * 3;
                            t.Add(v.Count);
                            v.Add(new Vector3(part.Verts[q], -part.Verts[q + 1], part.Verts[q + 2]) * 0.01f);
                            uv.Add(new Vector2((f.U[n] + 0.5f) / tim.Width, 1f - (f.T[n] - 192 + 0.5f) / tim.Height));     // the TIM sits at v 192 of its page
                        }
                    var m = new Mesh(); m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateBounds();
                    _auraMesh[k] = m;
                }
                var sh = Shader.Find("DW1Remake/Effect");
                if (sh != null) { _auraMat = new Material(sh); _auraMat.mainTexture = tim.ToTexture(0); }
            }
            catch (Exception e) { Debug.LogWarning("Finisher aura not available: " + e.Message); _auraMesh = null; }
        }

        void ShowRing(bool on) { _auraFrame = on ? 0 : -1; _auraAcc = 0f; }

        static int PsSin(int a) { return Mathf.RoundToInt(Mathf.Sin((a & 0x1FF) * Mathf.PI * 2f / 512f) * 4096f); }

        void TickAura(float dt)
        {
            Fighter who = _chargeBy ?? Partner;
            if (_auraFrame < 0 || who == null) return;
            _auraAcc += dt;
            while (_auraAcc >= Frame) { _auraAcc -= Frame; _auraSpin = _rng.Next(4096); if (++_auraFrame > 80) { _auraFrame = -1; return; } }
            if (_auraMesh == null || _auraMesh.Length < 5 || _auraMat == null) return;
            int fr = _auraFrame;
            int radius = who.Info != null ? who.Info.Radius : 150, height = who.Info != null ? who.Info.Height : 350;
            int sx = fr < 10 ? (4096 - PsSin(fr * 0x80 / 9 + 128)) * 0xB34 / 4096 + 0x4CC : 4096;
            sx = sx * radius / 150;
            int t = (0x200 * fr / 16) % 0x201;
            int sy = fr < 5 ? fr * 4096 / 4 : 4096;
            sy = sy * height / 350;
            sy = sy * 85 / 100 + sy * 15 / 100 * PsSin(t) / 4096;
            Vector3 at = who.T.position, s = new Vector3(sx, sy, sx) / 4096f;
            DrawAura(fr / 2 % 3, at, Quaternion.identity, s, 60);
            DrawAura(3, at, Quaternion.identity, s, 90);
            DrawAura(4, at, Quaternion.Euler(0f, _auraSpin * 360f / 4096f, 0f), new Vector3(s.x * 1.4f, s.y, s.z * 1.4f), 60);
        }

        void DrawAura(int obj, Vector3 at, Quaternion rot, Vector3 scale, int colour)
        {
            float c = colour / 128f;
            _auraMp.SetTexture("_MainTex", _auraMat.mainTexture); _auraMp.SetColor("_Color", new Color(c, c, c, 1f)); _auraMp.SetVector("_Rect", new Vector4(0, 0, 1, 1));
            Graphics.DrawMesh(_auraMesh[obj], Matrix4x4.TRS(at, rot, scale), _auraMat, 0, null, 0, _auraMp);
        }

        static int ChargePresses()
        {
            if (InputLocked) return 0;
            return (Pad.Pressed(PadButton.L1) ? 1 : 0) + (Pad.Pressed(PadButton.R1) ? 1 : 0);
        }

        // the fighter whose finishing move is running (NO_AI_FLAG, FINISHING_ENTITY), if any
        Fighter FinishingFighter()
        {
            foreach (var sw in _swings) if (sw.Tech.Finisher) return sw.From;
            return null;
        }

        // BTL_tickQueuedMove, BTL_tickChargeState...: under NO_AI_FLAG every other fighter that is not in the middle of something (hit, block, attack, stun, flat, down) goes back to its idle stance (handleBattleIdle) and stays
        void HoldOthers(Fighter finishing)
        {
            var all = new List<Fighter> { Partner, Enemy };
            all.AddRange(Extras);
            foreach (var f in all)
            {
                if (f == finishing || f.Hp <= 0 || f.A == null) continue;
                bool acting = f.AnimReset > 0f || _time < f.NextAct || f.Stunned || FlatBusy(f) || f.Stall > 0;
                foreach (var sw in _swings) if (sw.From == f) acting = true;
                if (acting) continue;
                if (f.A.Slot != IdleSlot && f.A.Slot != 0) Stand(f);
                f.A.TakeForward(); f.A.TakeBodyMove();      // what its walk would have moved must not be paid out later
            }
        }

        void TickCharge(float dt)
        {
            var me = _chargeBy;
            if (me.A != null)      // it stands facing the target (BTL_FINISHER_TIMER, entityLookAtLocation)
            {
                if (me.A.Slot != IdleSlot && me.A.Slot != 0) Stand(me);
                me.A.TakeForward(); me.A.TakeBodyMove();
            }
            if (_chargeFoe != null) me.T.rotation = _face(_chargeFoe.T.position - me.T.position);
            _chargeT += dt;
            if (me == Partner) _chargeCount = Mathf.Min(80, _chargeCount + ChargePresses() * 2);     // each press of L1 or R1 adds 2, up to 80 (code 0x80062148)
            if (_chargeT >= ChargeSeconds)
            {
                _charging = false;
                ShowRing(false);
                // the partner's blow x count / 40 from 41 up; an enemy's x a random 1.00 to 2.00 (the damage code)
                StartSwing(me, _chargeFoe, _chargeTech, me == Partner ? (_chargeCount >= 41 ? _chargeCount / 40f : 1f) : 1f + _rng.Next(0, 101) / 100f);
            }
        }

        void TickSwings(float dt)
        {
            // BTL_tickAttackState: a fighter whose blow was countered stays frozen (animation and technique) while the Counter runs at it
            foreach (var f in AllFighters())
            {
                if (f.CounteredBy == null) continue;
                if (!CounterRunning(f.CounteredBy, f)) { f.CounteredBy = null; if (f.A != null) f.A.Playing = true; continue; }
                // its own animation's end and its next action wait as long as it is frozen
                if (f.AnimReset > 0f) f.AnimReset += dt;
                if (f.NextAct > _time) f.NextAct += dt;
            }
            for (int i = _swings.Count - 1; i >= 0; i--)
            {
                var sw = _swings[i];
                if (sw.From.CounteredBy != null) continue;
                sw.T += dt;
                bool ranged = !sw.Tech.Melee;
                if (!sw.Done && sw.T >= sw.Release)
                {
                    sw.Done = true;
                    if (ranged && sw.Tech.Self) Buff(sw.From, sw.Tech);
                    else if (sw.Effect) { }      // its program lands the hit
                    else if (ranged && sw.Tech.Wide && !sw.From.Blind) { foreach (var f in new List<Fighter>(AllFighters())) if (f != sw.From && (f == sw.To || CanBeHit(f))) Apply(sw.From, f, sw.Tech, sw.Boost); }   // a wide technique covers the whole area: it cannot be dodged
                    else if (ranged) Fire(sw);
                    else
                    {
                        // the blow lands only if the target is still within the distance it was started from
                        float radii = (sw.From.Info != null ? sw.From.Info.Radius : 0) + (sw.To.Info != null ? sw.To.Info.Radius : 0);
                        if (Vector3.Distance(Flat(sw.From.T.position), Flat(sw.To.T.position)) * 100f <= radii * 1.3f) Apply(sw.From, sw.To, sw.Tech, sw.Boost, sw.HitAt);
                        else Miss(sw.To);
                    }
                }
                // an effect that put nothing on the screen would leave the shot invisible: use the plain shot instead and say so
                if (ranged && sw.Effect && !sw.Landed && !sw.Fell && Effects != null && sw.T >= sw.Release + 0.4f && Effects.Drawn == sw.DrawnAtStart)
                {
                    sw.Fell = true; sw.Effect = false; sw.Landed = true;
                    Debug.LogWarning("[effect] " + sw.Tech.Name + ": its program drew nothing in 0.4 s, using the plain shot (runs " + Effects.Running + ")");
                    if (ranged && !sw.Tech.Wide) Fire(sw); else Apply(sw.From, sw.To, sw.Tech, sw.Boost);
                }
                if (sw.T >= Mathf.Max(sw.Length, sw.Release + 0.5f) && (!sw.Effect || sw.Landed || sw.T > sw.Release + 4f))
                {
                    if (sw.Effect && !sw.Landed) Miss(sw.To);
                    _swings.RemoveAt(i);
                    sw.From.Blind = false;
                    sw.From.Cool = 40;                                           // BTL_tickFighterAction: 40 frames of cooldown, +4% finishing gauge, one attack counted
                    sw.From.Turned = null; sw.From.TargetHeld = false;                                       // FIGHTER_FLAG_10 cleared: an enemy goes back to the partner
                    if (sw.From == Partner) _used++;
                    AddFinish(sw.From, sw.From.FinishMax * 2 / 50);
                }
            }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v; }

        public IEnumerable<Fighter> AllFightersPublic() { return AllFighters(); }
        IEnumerable<Fighter> AllFighters() { yield return Partner; foreach (var f in Enemies()) yield return f; }

        // isHit: a fighter in its hit reaction (or down) cannot be hit again until the reaction is over
        bool CanBeHit(Fighter f) { return f != null && f.Hp > 0 && _time >= f.HitUntil && f.Leaving == 0; }

        EffectActor ActorFor(Fighter f)
        {
            return new EffectActor { T = f.T, A = f.A, Type = f.Info != null ? f.Info.Type : 0, Original = f.Info != null ? OriginalType(f.Info.Type) : -1, Radius = f.Info != null ? f.Info.Radius : 120, Height = f.Info != null ? f.Info.Height : 200, BoneCount = f.Info != null ? f.Info.BoneCount : 2 };
        }

        // where a blow lands when no effect part said so: the target's bone 1 (ours; the game always has the effect's attack object)
        static Vector3 HitPoint(Fighter d) { var b = d.A != null ? d.A.Node(1) : null; return b != null ? b.position : d.T.position + Vector3.up * 0.8f; }

        // BTL_startEFE: the technique's own effect from the disc. Ranged ones then decide the hit themselves; a blow by hand still lands with its animation.
        void StartEffect(Swing sw)
        {
            RemoveMoveEffect(sw.From);     // BTL_setupMoveExecution: BTL_removeMoveEffect before BTL_startEFE
            if (Effects == null || sw.Tech.Self) return;
            bool ok = false;
            try
            {
                // BTL_applyLineAttackHit / RadiusAttackHit / BoxAttackHit test every Digimon but the source (ENTITY_TABLE 1-9), friends too: Tremar from one enemy
                // also hurts the other enemies. One already in its hit reaction (isHit) or down is skipped.
                var victims = new List<EffectActor>(); var who = new Dictionary<EffectActor, Fighter>(); EffectActor primary = null;
                foreach (var f in AllFighters())
                {
                    if (f == sw.From) continue;
                    var act = ActorFor(f); victims.Add(act); who[act] = f;
                    if (f == sw.To) primary = act;
                }
                if (primary == null) primary = ActorFor(sw.To);
                ok = Effects.Start(sw.Tech.Id, ActorFor(sw.From), primary, victims, a => who.ContainsKey(a) && CanBeHit(who[a]), (at, victim) =>
                {
                    // BTL_addAttackObjectToTarget: the attack object sits where the effect part touched; the hit effect starts there
                    Fighter f = who.ContainsKey(victim) ? who[victim] : sw.To;
                    if (f == sw.To) sw.HitAt = at;
                    if (f == sw.To) { if (sw.Landed) return; sw.Landed = true; }
                    Apply(sw.From, f, sw.Tech, sw.Boost, at);
                }, a => who.ContainsKey(a) && who[a].Hp > 0);
            }
            catch (Exception e) { Debug.LogWarning("Effect of " + sw.Tech.Name + ": " + e.Message); }
            if (ok) { sw.From.FxRun = Effects.LastRun; sw.From.FxSection = Effects.LastSection; }
            Debug.Log("[effect] start " + sw.Tech.Name + " (technique " + sw.Tech.Id + ") by " + sw.From.Name + ": " + (ok ? "program running" : "no program, plain shot"));
            sw.Effect = ok;      // BTL_tickMeleeAttack only starts the move: a blow, like a shot, lands through its program's hit subs (addAttackObject)
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
            ClearConditions();
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

        // a miss shows nothing (the game has no miss text)
        void Miss(Fighter target) { }

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
            // BTL_calculateHitChance: 100 against a stunned or flattened (FIGHTER_FLAG_STUNNED | FIGHTER_FLAG_FLATTENED) target
            if (tech.Finisher || (d.Info != null && d.Info.Level < 3) || d.Stunned || d.Flattened || d.Stall > 0 || d.Energy <= 0) return false;
            foreach (var sw in _swings) if (sw.From == d) return false;
            var row = DigimonStatsCatalog.TechOf(tech.Id);
            if (row == null && tech.Id != FlatBulletTech) return false;
            int accuracy = row != null ? row.Accuracy : tech.Accuracy;     // the flat bullet (0x79) is past the 121 rows the catalog reads
            int off = accuracy / 2 * (d.Spd - a.Spd / 10) / 999;
            if (d.A != null && (d.A.Slot == 33 || d.A.Slot == 34)) off = off * 6 / 5;
            return _rng.Next(100) >= Mathf.Clamp(accuracy - off, 0, 100);
        }

        // a block (code 0x8005CADC): the pose of slot 37 facing the attacker. A short-range blow still does 10-30% of its damage; a long-range one nothing.
        // The finishing gauges gain here: 6% for the blocker, 8% for the attacker.
        void Block(Fighter a, Fighter d, Tech tech, float v, Vector3 at)
        {
            d.T.rotation = _face(a.T.position - d.T.position);
            Play(d, BlockSlot, d.A != null ? d.A.SecondsOf(BlockSlot) : 0f);
            // BTL_tickAttackHits, a blocked blow: createParticleFX(0, 2, attack position, the blocker, 0x11)
            if (Effects != null) Effects.AddHitFx(0, 2, at, ActorFor(d), 0x11);
            AddFinish(d, d.FinishMax * 3 / 50);
            AddFinish(a, a.FinishMax * 4 / 50);
            if (d == Partner) _blocks++;
            if (!tech.Melee) return;
            int chip = Mathf.Max(1, Mathf.RoundToInt(v * (10 + _rng.Next(21)) / 100f));
            d.Hp = Mathf.Max(0, d.Hp - chip);
            if (d != Partner) d.BarFrames = 0;
            _pops.Add(new Pop { Text = chip.ToString(), Pos = d.T.position + Vector3.up * 0.9f, T = 0f, Col = Color.white });
            Sync();
        }

        void AddFinish(Fighter f, int amount) { if (f.Finisher != null) f.Finish = Mathf.Min(f.FinishMax, f.Finish + amount); }

        const int CounterTech = 0x2D;

        // f's Counter is running (its animation is technique 0x2D's) and, when 'at' is given, aimed at that fighter
        bool CounterRunning(Fighter f, Fighter at)
        {
            if (f == null) return false;
            foreach (var sw in _swings) if (sw.From == f && sw.Tech.Id == CounterTech && (at == null || sw.To == at)) return true;
            return false;
        }

        // BTL_tickAttackHits, a blow whose hit roll failed: a short-range one (range 1) on a fighter that is not already blocking or countering
        // goes through its techniques in order (stats.base.moves[0-3]), stopping once MP is under 165; at Counter it rolls 100 against
        // Speed / its level (DIGIMON_DATA). On success the Counter starts at once at the attacker (targetId, BTL_startQueuedMove: it pays its MP
        // as any technique), the attacker freezes (animFlag bit 0 cleared) and the blow does nothing: no block, no chip damage, no gauges
        bool TryCounter(Fighter a, Fighter d, Tech tech)
        {
            if (!tech.Melee || d.Info == null || d.Info.Level <= 0) return false;
            if (d.A != null && d.A.Slot == BlockSlot && _time < d.AnimReset) return false;      // FIGHTER_FLAG_BLOCKING
            if (CounterRunning(d, null)) return false;
            foreach (var t in d.Moves)
            {
                if (d.Mp < 165) break;
                if (t == null || t.Id != CounterTech) continue;
                if (_rng.Next(100) >= d.Spd / d.Info.Level) continue;
                if (d == Partner)
                {
                    // fighter->targetId = the attacker
                    int k = Extras.IndexOf(a);
                    if (k >= 0) { Extras[k] = Enemy; Enemy = a; }
                }
                d.Pending = null;
                for (int i = _swings.Count - 1; i >= 0; i--) if (_swings[i].From == d) _swings.RemoveAt(i);
                Begin(d, a, t);
                a.CounteredBy = d;
                if (a.A != null) a.A.Playing = false;
                Debug.Log("[battle] " + d.Name + " counters " + a.Name + "'s " + tech.Name);
                return true;
            }
            return false;
        }

        /// <summary>
        /// handleBuffDisks (main.c), ITEM_FUNCTIONS for items 0x0F-0x15 when the thrown disk reaches the partner: Off. Disk / Def. Disk /
        /// Hispeed dsk +20, S.Off / S.Def / S.speed +50, each held under 130% of that stat at the fight's start (INITIAL_COMBAT_STATS, at most
        /// 999); Omni Disk +20 to all three (ours holds it under the same limit; the original adds 20 whatever the limit). Each raise is BTL_buffStats: the green number with the stat's icon over the partner, as a
        /// buff technique. Nothing with no HP left. Then the green wall over the partner (BTL_addBuffDiskEffect).
        /// </summary>
        public void BuffDisk(int item)
        {
            var f = Partner;
            if (f == null || f.Hp == 0) return;
            int offLimit = Mathf.Min(999, _s.Stats[0] + _s.Stats[0] * 3 / 10);
            int defLimit = Mathf.Min(999, _s.Stats[1] + _s.Stats[1] * 3 / 10);
            int spdLimit = Mathf.Min(999, _s.Stats[2] + _s.Stats[2] * 3 / 10);
            int n = 0;
            switch (item)
            {
                case 0x0F: DiskStat(f, ref f.Off, f.Off + 20 > offLimit ? offLimit - f.Off : 20, 3, n++); break;
                case 0x10: DiskStat(f, ref f.Def, f.Def + 20 > defLimit ? defLimit - f.Def : 20, 4, n++); break;
                case 0x11: DiskStat(f, ref f.Spd, f.Spd + 20 > spdLimit ? spdLimit - f.Spd : 20, 5, n++); break;
                case 0x12:
                    // ours (user's decision 2026-11-11): the same 130% limit as the other disks. The original computes no limit for it (US: the
                    // tests are missing; the other builds test and then change nothing), so it always adds 20
                    DiskStat(f, ref f.Off, f.Off + 20 > offLimit ? offLimit - f.Off : 20, 3, n++);
                    DiskStat(f, ref f.Def, f.Def + 20 > defLimit ? defLimit - f.Def : 20, 4, n++);
                    DiskStat(f, ref f.Spd, f.Spd + 20 > spdLimit ? spdLimit - f.Spd : 20, 5, n++);
                    break;
                case 0x13: DiskStat(f, ref f.Off, f.Off + 50 > offLimit ? offLimit - f.Off : 50, 3, n++); break;
                case 0x14: DiskStat(f, ref f.Def, f.Def + 50 > defLimit ? defLimit - f.Def : 50, 4, n++); break;
                case 0x15: DiskStat(f, ref f.Spd, f.Spd + 50 > spdLimit ? spdLimit - f.Spd : 50, 5, n++); break;
                default: return;
            }
            Debug.Log("[battle] " + f.Name + " disk " + item.ToString("X2") + ": Off " + f.Off + ", Def " + f.Def + ", Speed " + f.Spd);
            AddBuffDiskEffect(f);       // BTL_addBuffDiskEffect(partner): the turning wall and its streaks (BattleStatus.cs)
        }

        // BTL_buffStats: addWithLimit(stat, value, 999), then addEntityText(colour 0xB, the value, icon): a stat already past the limit gets a
        // negative value (limit - stat) and goes down, and that number is shown. Each later number starts 4 pixels on and waits 11 frames, as
        // the buff techniques' numbers do
        void DiskStat(Fighter f, ref int stat, int value, int icon, int order)
        {
            stat = Mathf.Min(999, stat + value);
            if (f.T == null) return;
            _pops.Add(new Pop { Text = value.ToString(), Pos = f.T.position + Vector3.up * 1.2f, Col = new Color(0f, 1f, 0f), Icon = icon, Shift = order * 4, Wait = order * 11f / 20f });
        }

        // self moves (BTL_applyBuffMove): Muscle Charge, War Cry, Mass Morph, Aqua Magic, Full Potential raise stats by a share of their value, up to 999
        void Buff(Fighter f, Tech t)
        {
            if (FleeDropsHits) return;      // BTL_applyBuffMove comes after the FLEE_TIMER test
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
            // BTL_buffStats -> addEntityText: one number per stat, colour 0xB, icon 3 Off / 4 Def / 5 Speed, each called even for 0.
            // renderEntityText: each later number starts 4 pixels right and down (activeElements x 4) and waits until the one before has risen 11 frames
            int[] gain = t.Id == 41 ? new[] { off } : t.Id == 34 ? new[] { def, spd } : new[] { off, def, spd };
            int[] icon = t.Id == 41 ? new[] { 3 } : t.Id == 34 ? new[] { 4, 5 } : new[] { 3, 4, 5 };
            if (t.Id != 41 && t.Id != 42 && t.Id != 34 && t.Id != 21 && t.Id != 30) return;
            for (int i = 0; i < gain.Length; i++)
                _pops.Add(new Pop { Text = gain[i].ToString(), Pos = f.T.position + Vector3.up * 1.2f, Col = new Color(0f, 1f, 0f), Icon = icon[i], Shift = i * 4, Wait = i * 11f / 20f });
        }

        // the hit code (battle_main.c): from FLEE_TIMER 0x13 on, every attack object is still taken off its target but does nothing (no hit, no miss, no buff)
        bool FleeDropsHits { get { return _running && _runFrames >= 0x13; } }

        // BTL_removeMoveEffect: the fighter's technique effect (the part its program started with) stops; -1 / -1 after
        void RemoveMoveEffect(Fighter f)
        {
            if (f == null) return;
            if (f.FxRun != null && Effects != null) Effects.StopSub(f.FxRun, f.FxSection);
            if (f.FxRun != null && NetStopFx != null) NetStopFx(f);
            f.FxRun = null; f.FxSection = -1;
        }

        void Apply(Fighter a, Fighter d, Tech tech, float boost = 1f, Vector3? at = null)
        {
            if (d.Hp <= 0 || FleeDropsHits) return;
            if (_time < d.HitUntil) return;       // isHit: it is still in the reaction to the last blow (fall and getting up)
            // BTL_tickAttackHits: a fighter in its Counter (technique 0x2D) takes nothing from the one it counters
            if (CounterRunning(d, a)) return;
            Vector3 hitAt = at ?? HitPoint(d);
            // BTL_tickAttackHits: every blow that reaches it, landed or blocked, first stops its own technique's effect (BTL_removeMoveEffect) and
            // clears FIGHTER_FLAG_ATTACKING: a shot of its own still on its way is gone, and a technique that has not let go never lands
            // (the swing itself ends below on a landed hit; a fighter in a technique's animation cannot block: BTL_calculateHitChance)
            RemoveMoveEffect(d);
            // the hit code (battle_main.c), before the hit roll: a blow on the fighter whose finishing move is running ends it
            // (BTL_removeFinisherChargeup, BTL_removeFinisherAura, NO_AI_FLAG and FINISHING_ENTITY cleared, BTL_removeMoveEffect)
            if (_charging && _chargeBy == d) { _charging = false; ShowRing(false); _chargeBy = null; }
            for (int i = _swings.Count - 1; i >= 0; i--) if (_swings[i].From == d && _swings[i].Tech.Finisher && !_swings[i].Done) _swings.RemoveAt(i);
            float v = Damage(a, d, tech, boost);
            if (a != d && Blocks(a, d, tech))
            {
                if (TryCounter(a, d, tech)) return;
                Block(a, d, tech, v, hitAt);
                return;
            }
            RetargetAfterHit(d, a);
            // a landed blow ends the freeze of a countered fighter: it goes into its hit reaction (BTL_tickHitState, not BTL_tickAttackState)
            if (d.CounteredBy != null) { d.CounteredBy = null; if (d.A != null) d.A.Playing = true; }
            int dmg = Mathf.Max(1, Mathf.RoundToInt(v));
            _anyHit = true;      // HAS_TAKEN_DAMAGE: any landed technique hit in the fight
            if (Tournament && OnTechHit != null) OnTechHit(d);
            d.Hp = Mathf.Max(0, d.Hp - dmg);
            if (d != Partner) d.BarFrames = 0;
            if (d == Partner && dmg * 100 / Partner.MaxHp >= 20) _bigHits++;
            _pops.Add(new Pop { Text = dmg.ToString(), Pos = d.T.position + Vector3.up * 0.9f, T = 0f, Col = Color.white });
            Vector3 away = Flat(d.T.position - a.T.position); if (away.sqrMagnitude < 0.01f) away = Vector3.left;
            // the reaction plays even on the last blow; the collapse follows when it ends (Animate)
            {
                // the game picks the reaction by which side was hit; the fighter can do nothing until it ends. The model's front is -Z.
                bool front = Vector3.Dot(d.T.forward, away) >= 0f || d.Flattened;      // BTL_handleHitReaction: flattened, it faces the blow, plays 0x28 and shows flat picture 3
                int slot = front ? HitFrontSlot : HitBackSlot;
                d.T.rotation = _face(front ? -away : away);
                if (d.Flattened) d.FlatSprite = 3;         // it turns to the blow (or away from it) and the reaction carries it from there
                // frozen for the technique's byte +7 in frames, then the reaction plays out; a hit stops a technique that has not let go yet
                for (int i = _swings.Count - 1; i >= 0; i--) if (_swings[i].From == d && !_swings[i].Done) _swings.RemoveAt(i);
                float length = (d.A != null ? d.A.SecondsOf(slot) : 0f) + tech.Stagger / 20f;
                Play(d, slot, length);
                d.NextAct = Mathf.Max(d.NextAct, _time + length);
                d.HitUntil = _time + length;
            }
            // BTL_startHitAnimation: createParticleFX(MOVE_DATA.special, 1, attack position, the hit Digimon, MOVE_DATA.iframes + 0x10)
            if (Effects != null) Effects.AddHitFx(tech.Element, 1, hitAt, ActorFor(d), tech.Stagger + 0x10);
            if (d.Hp > 0) ApplyMoveStatus(d, tech);       // BTL_applyMoveStatus (BattleStatus.cs)
            Sync();
        }

        void Sync() { _s.Stats[6] = (ushort)Mathf.Clamp(Partner.Hp, 0, 9999); _s.Stats[7] = (ushort)Mathf.Clamp(Partner.Mp, 0, 9999); }

        /// <summary>Items used from the bag during the fight change the stats; this picks them up and starts the boy's throw.</summary>
        public void ReadBack()
        {
            if (Partner == null || Puppet) return;      // the online copy: the numbers come from the host's PC
            int dh = _s.Stats[6] - Partner.Hp, dm = _s.Stats[7] - Partner.Mp;
            Partner.Hp = _s.Stats[6]; Partner.Mp = _s.Stats[7]; Partner.MaxHp = Mathf.Max(1, _s.Stats[4]); Partner.MaxMp = _s.Stats[5];
        }

        // ---- fainting (BTL_faintDigimon, BTL_isBattleFinished, BTL_addDeathCountdown / BTL_tickDeathCountdown, handleRestore)
        // At 0 HP the partner falls (animation 0x2B). Once that has played, a counter runs each frame (not while the boy is throwing an item);
        // at 20 the countdown shows over the partner, at 171 the fight is lost. An item that heals it first makes it get up (animation 0x2C)
        // and the fight goes on.
        public static TimImage CountdownTim;        // ETCDAT/ETCTIM.BIN image 0 (VRAM 896,384; colour sets from row 480)
        static readonly int[] CountU = { 0x50, 0x68, 0x58, 0x68 }, CountV = { 0xA8, 0x90, 0x90, 0x80 };     // BTL_DEATH_COUNTDOWN_DIGIT_U / _V
        int _deathCount, _cdTimer = -1, _cdStep, _cdRow = 13; float _cdRingScale = 1f, _cdDigitScale = 1f, _cdRot; bool _fainted, _rising;
        float _faintAcc;
        readonly Texture2D[] _cdTex = new Texture2D[16];

        bool TickFaint(float dt)
        {
            if (Partner.Hp > 0)
            {
                if (_fainted)
                {
                    // handleRestore: it gets up (0x2C); BTL_isBattleFinished clears the countdown and lets it fight again when that ends
                    _fainted = false; _deathCount = 0; _cdTimer = -1;
                    if (Partner.A != null && Partner.A.HasSlot(0x2C)) { Partner.A.SetSlot(0x2C, true); _rising = true; }
                }
                if (_rising && (Partner.A == null || !Partner.A.Busy)) { _rising = false; Stand(Partner); }
                return false;
            }
            _fainted = true;
            if (Partner.A != null && Partner.A.HasSlot(DownSlot) && (Partner.A.Slot != DownSlot || Partner.A.Busy))
            {
                if (Partner.A.Slot != DownSlot) Partner.A.SetSlot(DownSlot, true);
                return false;
            }
            _faintAcc += dt;
            while (_faintAcc >= Frame)
            {
                _faintAcc -= Frame;
                if (!ThrowBusy)
                {
                    _deathCount++;
                    if (_deathCount == 0x14) { _cdTimer = 0; _cdStep = 0; _cdRow = 13; }
                    if (_cdTimer >= 0) TickCountdown();
                }
                if (_deathCount >= 0xAB) { _cdTimer = -1; return true; }
            }
            return false;
        }

        // BTL_tickDeathCountdown: the digit changes every 30 frames (4, 3, 2, 1, then the last picture); in each 31-frame step it pops for 20 frames
        // (+0x199 a frame for 3, -0x199 for 3), the ring's colour set moves on every 3 (rows 13-15); then both shrink 5% a frame and turn 64 degrees
        void TickCountdown()
        {
            _cdTimer++;
            if (_cdTimer % 31 == 0) _cdStep = 0;
            _cdStep++;
            if (_cdStep > 0 && _cdStep < 0x15)
            {
                if (_cdStep == 1) { _cdRingScale = 1f; _cdRot = 0f; }
                if (_cdStep >= 2 && _cdStep < 5) _cdDigitScale += 0x199 / 4096f;
                else if (_cdStep >= 5 && _cdStep < 8) _cdDigitScale -= 0x199 / 4096f;
                else _cdDigitScale = 1f;
                if (_cdStep % 3 == 0 && ++_cdRow >= 16) _cdRow = 13;
            }
            else
            {
                _cdDigitScale = _cdRingScale = (0x64 - (_cdStep - 0x14) * 5) / 100f;
                _cdRot += 64f;
            }
            if (_cdTimer >= 0x96) _cdTimer = -1;      // BTL_renderDeathCountdown removes it at 150
        }

        // BTL_renderDeathCountdown: the 16 x 16 digit (centre 8, 8) over the 40 x 40 ring (centre 20, 20), both from ETCTIM image 0, at the partner
        // (kept inside x -140..140, y -100..100). Where on the partner getEntityScreenPos(mode 1) aims is not decoded: we use its middle.
        void DrawCountdown(Camera cam)
        {
            var tim = CountdownTim;
            if (tim == null || Partner.T == null) return;
            Func<int, Texture2D> tex = row => row < tim.PaletteCount ? (_cdTex[row] ?? (_cdTex[row] = tim.ToTexture(row))) : null;
            int h = Partner.Info != null ? Partner.Info.Height : 100;
            Vector2 at = Ps1Of(cam, Partner.T.position + Vector3.up * (h * 0.5f * 0.01f));
            at.x = Mathf.Clamp(at.x, -0x8C, 0x8C); at.y = Mathf.Clamp(at.y, -0x64, 0x64);
            int frame = (_cdTimer + 0x1D) / 30;
            int du = frame >= 1 && frame < 5 ? CountU[frame - 1] : 0x58, dv = frame >= 1 && frame < 5 ? CountV[frame - 1] : 0x80;
            float cx = 707.5f + at.x * 4.42f, cy = 530f + at.y * 4.42f;
            var ring = tex(_cdRow); var digit = tex(13);
            var old = GUI.matrix;
            if (ring != null)
            {
                GUIUtility.RotateAroundPivot(_cdRot, new Vector2(cx, cy));
                float s = 40f * 4.42f * _cdRingScale;
                GUI.DrawTextureWithTexCoords(new Rect(cx - s / 2f, cy - s / 2f, s, s), ring, new Rect(0x32 / (float)ring.width, 1f - 40f / ring.height, 40f / ring.width, 40f / ring.height));
                GUI.matrix = old;
            }
            if (digit != null)
            {
                GUIUtility.RotateAroundPivot(_cdRot, new Vector2(cx, cy));
                float s = 16f * 4.42f * _cdDigitScale;
                GUI.DrawTextureWithTexCoords(new Rect(cx - s / 2f, cy - s / 2f, s, s), digit, new Rect(du / TimImage.W(digit), 1f - (dv - 0x80 + 16) / TimImage.H(digit), 16f / TimImage.W(digit), 16f / TimImage.H(digit)));
                GUI.matrix = old;
            }
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
                _bitsLeft = _bits; _statWait = 100; _countAcc = 0f; _fanfare = false; _cheered = false;
                _dropped = null; _endStep = -1;
                // battleStatsGainsAndDrops: each enemy drops its item at its chance (none on map 0x8F); spawnDroppedItems puts them down when the
                // boxes go, where the enemy fell
                _drops.Clear(); _dropIds.Clear(); _dropBy.Clear();
                foreach (var f in Enemies())
                {
                    if (f.Info == null || MapId == 0x8F) continue;
                    if (f.Info.DropChance > _rng.Next(100) && f.Info.DropItem != 0xFF)
                    {
                        _drops.Add(new KeyValuePair<int, Vector3>(f.Info.DropItem, f.T.position));
                        _dropIds.Add(f.Info.DropItem); _dropBy.Add(f.Info.Name ?? f.Name);
                        DropItem = f.Info.DropItem; DropAt = f.T.position;
                    }
                }
                TryLearn();
                int[] idx = { 4, 5, 0, 1, 2, 3 };
                for (int i = 0; i < 6; i++) { _before[i] = _s.Stats[idx[i]]; _gain[i] = 0; }
                StatGains();
                for (int i = 0; i < 6; i++) { _shown[i] = _before[i]; _left[i] = _gain[i]; }
                if (_playerA != null) _playerA.SetSlot(_playerA.HasSlot(1) ? 1 : 0);
                Stand(Partner);
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
            if (_mapPops.Count > 0) DrawMapText();
            if (!Active || Partner == null) return;
            UiKit.Begin();
            if (_phase == Phase.Notice && cam != null && !Tournament) AngerIcon(cam);
            if (_phase == Phase.Banner && !Tournament) Banner();
            if (_phase != Phase.Notice && !Tournament) Hud();      // the arena draws its own (ArenaScreens)
            if (EndView != null && EndView.Showing) EndView.Draw(() => DrawStatsBox(Art, -88, -78, 176, _shown, _left, Raised(), false), BitsContent, FinalContent);
            if (_phase == Phase.Fight && _cdTimer >= 0 && cam != null && !Tournament) DrawCountdown(cam);
            if (cam != null && (_phase == Phase.Fight || _phase == Phase.Win) && !Tournament) foreach (var f in Enemies()) EnemyBar(cam, f);
            if (cam != null && _phase == Phase.Fight && _shoutFrame >= 0 && _player != null && !Tournament) DrawShout(cam);
            if (cam != null && (_phase == Phase.Fight || _phase == Phase.Win)) DrawConditions2D(cam);
            if (cam != null) foreach (var p in _pops) DrawPop(cam, p);
            UiKit.End();
            if (cam != null)
            {
                if (_phase == Phase.Fight || _phase == Phase.Win)
                {
                }
                foreach (var th in _throws)   // the boy throws the item to the partner
                {
                    if (_player == null || Items == null) continue;
                    // renderThrownItem -> renderOverworldItem: the item's picture at its place, VIEWPORT_DISTANCE x 128 / depth pixels wide (128 game units)
                    Vector3 w = EffectPlayer.ToUnity((int)th.G.x, (int)th.G.y, (int)th.G.z);
                    Vector3 sp = cam.WorldToScreenPoint(w), up = cam.WorldToScreenPoint(w + cam.transform.up * 1.28f);
                    float size = Mathf.Abs(up.y - sp.y);
                    if (sp.z > 0) GUI.DrawTextureWithTexCoords(new Rect(sp.x - size / 2f, Screen.height - sp.y - size / 2f, size, size), Items.Sheet, Items.Uv(th.Item));
                }
            }
        }

        public GameFont Font;
        sbyte[] _hopOff, _dropOff, _textRise;

        /// <summary>Tables from BTL_REL.BIN (BTL_SHOUT_HOP_OFFSETS 0x80072E54, BTL_SHOUT_DROP_OFFSETS 0x80072E68) and the exe (ENTITY_TEXT_Y_OFFSETS 0x8012AA28).</summary>
        public void LoadTextTables(byte[] btl, byte[] exe)
        {
            _hopOff = new sbyte[20]; _dropOff = new sbyte[20]; _textRise = new sbyte[24];
            for (int i = 0; i < 20; i++) { _hopOff[i] = (sbyte)btl[0x80072E54 - 0x80052AE0 + i]; _dropOff[i] = (sbyte)btl[0x80072E68 - 0x80052AE0 + i]; }
            uint load = BitConverter.ToUInt32(exe, 0x18);
            for (int i = 0; i < 24; i++) _textRise[i] = (sbyte)exe[(int)(0x8012AA28 - load + 0x800) + i];
            _iconU = new int[5];     // 1 HP, 2 MP, 3 Off, 4 Def, 5 Speed
            for (int i = 0; i < 5; i++) _iconU[i] = exe[(int)(0x801343EC - load + 0x800) + i];      // ENTITY_TEXT_ICON_U
            Func<uint, string> str = a => { int p = (int)(a - load + 0x800), j = p; while (exe[j] != 0) j++; return System.Text.Encoding.ASCII.GetString(exe, p, j - p); };
            for (int i = 0; i < 6; i++) _statNames[i] = str(0x80124C0C + (uint)(i * 12));
            _bitsLabel = str(0x801344D4);
            // BTL_STR_*: the battle end text pieces (BTL_REL 0x80072EF8...; the exe 0x80134740...)
            Func<uint, string> bstr = a => { int p = (int)(a - 0x80052AE0), j = p; while (btl[j] != 0) j++; return System.Text.Encoding.ASCII.GetString(btl, p, j - p); };
            uint[] b = { 0x80072EF8, 0x80072F18, 0x80072F2C, 0x80072F44, 0x80072F58, 0x80072F70, 0x80072F90, 0x80072FA4, 0x80072FBC, 0x80072FD8, 0x80072FE4 };
            for (int i = 0; i < b.Length; i++) _endStr[i] = bstr(b[i]);
            uint[] e = { 0x80134740, 0x80134744, 0x8013474C, 0x80134750, 0x80134754, 0x80134758, 0x8013475C };
            _endExtra = new string[e.Length];
            for (int i = 0; i < e.Length; i++) _endExtra[i] = str(e[i]);
        }
        string[] _endExtra = new string[7];     // #C7, #C1#W, 20, 15, 10, 5, %!#W
        const int ListensTo = 0, Dropped = 1, WasInjured = 2, SetTechnique = 3, PutUpWithIt = 4, MoveAway = 5, KeepItDown = 6, GoAllTheWay = 7, MpBonus = 8, ReducedBy = 9, Learned = 10;

        // handleBattleEndBox's text, in its order: every enemy's dropped item, the injury, a new command learned (Brains 100 / 200 / 300 / 400 / 500
        // crossed), the MP bonus (700 / 800 / 900 / 999 crossed), the technique learned (battleMoveLearning)
        // ours (translation): a piece of the battle end text in the current language (Tr; the pieces keep the game's #C / #R / #W codes)
        string EndStr(int i) { return Tr.Text(_endStr[i], "battle end text"); }

        void BuildEndText(List<int> dropped, List<string> droppedBy)
        {
            var v = EndView; if (v == null) return;
            v.InitText();
            string yellow = _endExtra[0], whiteWait = _endExtra[1];
            for (int i = 0; i < dropped.Count; i++) v.Append(yellow + droppedBy[i] + EndStr(Dropped) + (ItemName != null ? ItemName(dropped[i]) : "") + whiteWait, 2);
            if (Injured != null && !Injured())
            {
                if (_anyHit && Tiredness != null && SetInjured != null)
                {
                    int ratio = 100 * _s.Stats[6] / Mathf.Max(1, _s.Stats[4]);
                    if (_rng.Next(100) < Tiredness() - ratio) SetInjured();
                }
                if (Injured()) v.Append(yellow + Partner.Name + EndStr(WasInjured), 2);
            }
            int old = _before[5], total = old + _gain[5];
            if (total >= 100)
            {
                if (total >= 500) { if (old < 500) v.Append(EndStr(SetTechnique) + EndStr(ListensTo), 2); }
                else if (total >= 400) { if (old < 400) v.Append(EndStr(PutUpWithIt) + EndStr(ListensTo), 2); }
                else if (total >= 300) { if (old < 300) v.Append(EndStr(MoveAway) + EndStr(ListensTo), 3); }
                else if (total >= 200) { if (old < 200) v.Append(EndStr(KeepItDown) + EndStr(ListensTo), 2); }
                else if (old < 100) v.Append(EndStr(GoAllTheWay) + EndStr(ListensTo), 2);
            }
            if (total >= 700)
            {
                string pct = "";
                if (total >= 999) { if (old < 999) pct = _endExtra[2]; }
                else if (total >= 900) { if (old < 900) pct = _endExtra[3]; }
                else if (total >= 800) { if (old < 800) pct = _endExtra[4]; }
                else if (old < 700) pct = _endExtra[5];
                // BTL_appendMPBonusText (US): "#C1MP Consumption Bonus!" + "reduced by" + pct + "%!#W", counted as 4 rows, with no row break
                // and no spaces between the pieces, so it ran off the box and froze the game (glitch Q). Ours (user's request 2026-11-09): the
                // same words split into rows that fit, a space between the pieces, and the row count it really has
                if (pct.Length > 0)
                {
                    string colour = "", bonus = EndStr(MpBonus), end = _endExtra[6];
                    if (bonus.StartsWith("#C") && bonus.Length >= 3) { colour = bonus.Substring(0, 3); bonus = bonus.Substring(3); }
                    if (end.EndsWith("#W")) end = end.Substring(0, end.Length - 2);
                    var words = new List<string>(bonus.Split(' '));
                    words.AddRange(EndStr(ReducedBy).Split(' '));
                    // the US font has no '%' glyph (drawGlyph falls on a button icon): "percent" instead (user's decision 2026-11-09)
                    words.Add(pct);
                    words.Add(end.Replace("%", Tr.Chinese ? "％" : "percent"));
                    int rows;
                    v.Append(colour + v.WrapRows(words.ToArray(), out rows) + "#W", rows);
                }
            }
            if (_learned != null) v.Append(yellow + _learned + EndStr(Learned), 2);
        }

        // POLLED_INPUT == CONFIRM_BUTTON or CANCEL_BUTTON, newly
        static bool EndPress() { return GameMenus.CrossPressed() || GameMenus.TriangleOnly(); }

        // handleBattleEndBox, one game frame: 0 the stats and Bits boxes grow; 1 the stats count after 100 frames (Cross / Triangle: all at once);
        // 2 the final balance box grows; 3 the Bits count; 4 the end text; then the drops land, the boxes go and the enemies leave
        void TickEnd(bool press)
        {
            var v = EndView;
            if (v == null) { StartLeave(); return; }
            switch (_endStep)
            {
                case 0:
                    if (v.Stats.State == 1 && v.Bits.State == 1) { if (v.HasText) { v.DrawStep(true); v.DrawStep(true); } _endStep = 1; }
                    break;
                case 1:
                    if (press) _skip = true;
                    if (_skip) { for (int i = 0; i < 6; i++) { if (_left[i] > 0) _shown[i] = StatCap(i, _shown[i] + _left[i]); _left[i] = 0; } _statWait = 0; }
                    else CountStat();
                    if (!HasLeft() && _statWait == 0)
                    {
                        var b = v.Bits;
                        v.OpenBox(v.Final, new RectInt(-88, -13, 176, 31), b.Origin, 1, 0);
                        _endStep = 2;
                    }
                    break;
                case 2:
                    if (v.Final.State == 1) _endStep = 3;
                    break;
                case 3:
                    if (press) _skip = true;
                    if (_bitsLeft == 0) { _skip = false; _endStep = 4; break; }
                    if (_skip) _bitsLeft = 0; else { Snd(0x16); _bitsLeft--; }
                    if (_bitsLeft == 0) Snd(0x17);
                    break;
                case 4:
                    v.TickText(press);
                    if (v.TextFinished)
                    {
                        foreach (var d in _drops) if (OnDrop != null) OnDrop(d.Key, d.Value);
                        _drops.Clear();
                        v.CloseAll();
                        _endStep = 5;
                        StartLeave();
                    }
                    break;
            }
        }

        // renderPostBattleStatsBox counts one point a frame once POST_BATTLE_STATS_TIMER (100) has run out
        void CountStat()
        {
            if (_statWait > 0) { _statWait--; return; }
            for (int i = 0; i < 6; i++)
                if (_left[i] > 0) { Snd(0x16); _left[i]--; _shown[i] = StatCap(i, _shown[i] + 1); return; }
        }

        // renderPostBattleStatsBox / tickPostBattleStatsBox: the count-up holds HP and MP (rows 0, 1) at 9999 and the others at 999; the "+n" still
        // counts down beside it. Ours went past and the 4-digit number showed 10000 as 0000 (user 2026-11-11)
        static int StatCap(int row, int v) { return Math.Min(v, row < 2 ? 9999 : 999); }

        static Vector2 Ps1Of(Camera cam, Vector3 world)
        {
            Vector3 sp = cam.WorldToScreenPoint(world); Rect pic = UiKit.Picture;
            return new Vector2((sp.x - pic.x) / pic.width * 320f - 160f, (Screen.height - sp.y - pic.y) / pic.height * 240f - 120f);
        }

        // BTL_drawCommandShout / BTL_renderCommandShout: the name in the game's font over the boy, in a box of 12 per letter centred on him (kept inside x -140..140, y -100..100);
        // it grows from 0.2 to 1 in the first 4 frames, then hops (or drops when it is high on the screen, y < -40); a black copy 1 pixel down and right lies under it; 20 frames
        void DrawShout(Camera cam)
        {
            if (Font == null || _hopOff == null || string.IsNullOrEmpty(_shout)) return;
            Vector2 p = Ps1Of(cam, _player.position + Vector3.up * 1.8f);
            int w = _shout.Length * 12, f = Mathf.Min(_shoutFrame, 19);
            float x = Mathf.Round(p.x), y = Mathf.Round(p.y);
            if (x - w / 2 < -140) x = w / 2 - 140;
            if (x + w / 2 >= 141) x = 140 - w / 2;
            y = Mathf.Clamp(y, -100, 100);
            y += y < -40 ? _dropOff[f] : _hopOff[f];
            float s = _shoutFrame < 4 ? (_shoutFrame * 2 + 2) / 10f : 1f, k = 4.42f * s;
            float left = 707.5f + x * 4.42f - w / 2f * k, top = 530f + y * 4.42f - 6f * k;
            Font.Draw(_shout, left + 4.42f, top + 4.42f, k, Color.black);
            Font.Draw(_shout, left, top, k, Font.Ink);
        }

        // addEntityText / renderEntityText / drawEntityText: 8 x 8 digits of the HUD sheet (colour set 4, row 55), 7 apart, tinted by TEXT_COLORS
        // (0 plain for damage, 0xB green for items, 0xC purple for poison); starts 8 over the fighter and rises by ENTITY_TEXT_Y_OFFSETS for 21 frames
        void DrawPop(Camera cam, Pop p)
        {
            int n;
            if (p.Wait > 0f || !int.TryParse(p.Text, out n) || Art == null || _textRise == null) return;
            int frame = Mathf.Min((int)(p.T * 20f), 30), rise = 0;
            for (int i = 0; i <= Mathf.Min(frame, 20); i++) rise += _textRise[i];
            Vector2 at = p.Fixed ? p.At : Ps1Of(cam, p.Pos);
            var o = GUI.color; GUI.color = p.Col;
            float x0 = Mathf.Round(at.x) - 14 + p.Shift, y0 = Mathf.Round(at.y) - 8 + rise + p.Shift;
            Art.Digits(n, 4, x0, y0);
            GUI.color = o;
            // drawEntityTextIcon: 8 x 8 at u ENTITY_TEXT_ICON_U[icon - 1] (exe 0x801343EC), v 0xB8 of the same page, colour set 6, 8 pixels left of the number
            if (p.Icon > 0 && _iconU != null && p.Icon <= _iconU.Length)
            {
                int len = Mathf.Clamp(n, 0, 9999).ToString().Length;
                Art.Hud(6, _iconU[p.Icon - 1], 0xB8 - 128, 8, 8, x0 + (4 - len) * 7 - 8, y0);
            }
        }

        int[] _iconU;

        // addEntityText outside a fight (GAME_STATE != 4): the number stays where the Digimon was on screen when it was added, and rises as in a fight
        readonly List<Pop> _mapPops = new List<Pop>();
        public void AddMapText(Camera cam, Vector3 world, int value)
        {
            if (cam == null) return;
            _mapPops.Add(new Pop { Text = value.ToString(), Fixed = true, At = Ps1Of(cam, world), Col = Color.white });     // colour 0: the plain white of a fight's damage numbers
        }

        void DrawMapText()
        {
            if (Event.current.type == EventType.Repaint)
                for (int i = _mapPops.Count - 1; i >= 0; i--) { _mapPops[i].T += Time.deltaTime; if (_mapPops[i].T >= 31f / 20f) _mapPops.RemoveAt(i); }
            UiKit.Begin();
            foreach (var p in _mapPops) DrawPop(null, p);
            UiKit.End();
        }

        static void Floating(Camera cam, Vector3 world, string text, int size, Color? col = null)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0) return;
            Outlined(new Rect(sp.x - 150, Screen.height - sp.y - 25, 300, 50), text, size, col ?? Color.white, TextAnchor.MiddleCenter);
        }

        static readonly int[] BubbleScale = { 100, 104, 108, 112, 116, 120, 116, 112, 108, 104 };

        // the bubble over the enemy when a fight starts (renderFleeBubble): it pops up from the head, then pulses; its size is 1.44 m at that distance
        // ---- the Digimon of the map that do not fight (handleBattleStart / BTL_tickBattleStart): a scared bubble, then animation 0x24 towards
        // the flee point of their map record until they are off the screen; then they are off the map (isOnMap = 0)
        sealed class Runner { public Transform T; public DigimonAnimator A; public Vector3 To; public int Height, Radius; }
        readonly List<Runner> _runners = new List<Runner>();
        public void AddRunner(Transform t, DigimonAnimator a, Vector3 fleeTo, int height, int radius = 0)
        {
            // BTL_handleBattleIntro: run 0x24 at double speed (animFlag 4), entityLookAtLocation(flee) once; it is never turned again
            if (a != null && a.HasSlot(0x24)) { a.SetSlot(0x24); a.DoubleSpeed = true; }
            Vector3 d = fleeTo - t.position; d.y = 0f;
            if (d.sqrMagnitude > 0.0001f) t.rotation = _face(d);
            _runners.Add(new Runner { T = t, A = a, To = fleeTo, Height = height, Radius = radius });
        }

        // QUADRANTS (map.c): the four corners round an entity, x then z
        static readonly int[,] Quadrants = { { -1, 1 }, { 1, 1 }, { -1, -1 }, { 1, -1 } };

        /// <summary>entityIsOffScreen with isOffScreen (map.c): off screen only when none of the 8 corners of its box (DIGIMON_DATA radius round it,
        /// at its feet and at its height) lands inside a w x h rectangle round the middle of the PS1 picture. Radius and height in game units.
        /// The camera draws only the 4:3 picture (its rect), so its viewport is the PS1 screen.</summary>
        public static bool OffScreen(Camera cam, Vector3 pos, int radius, int height, int w = 0x140, int h = 0xF0)
        {
            if (cam == null) return false;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                {
                    var corner = pos + new Vector3(radius * Quadrants[j, 0] * 0.01f, i * height * 0.01f, radius * Quadrants[j, 1] * 0.01f);
                    Vector3 v = cam.WorldToViewportPoint(corner);
                    if (v.z <= 0f) continue;
                    float sx = (v.x - 0.5f) * 320f, sy = (0.5f - v.y) * 240f;
                    if (sx < -w / 2 || sx > w / 2 || sy < -h / 2 || sy > h / 2) continue;
                    return false;
                }
            return true;
        }

        bool TickRunners()
        {
            Camera cam = Camera.main; bool allOff = true;
            foreach (var r in _runners)
            {
                if (r.T == null || !r.T.gameObject.activeSelf) continue;
                // straight on along the heading it was given (past its flee point if that is on the screen); a model's front is -Z
                float step = r.A != null ? r.A.TakeForward() : 0f;
                Vector3 fwd = -r.T.forward; fwd.y = 0f;
                r.T.position += fwd.normalized * step;
                if (cam != null && !OffScreen(cam, r.T.position, r.Radius, r.Height)) allOff = false;     // entityIsOffScreen(0x140, 0xF0)
            }
            return allOff;
        }

        // BTL_handleBattleIntro: isOnMap = 0 and animFlag &= 0xFB
        void RemoveRunners() { foreach (var r in _runners) { if (r.A != null) r.A.DoubleSpeed = false; if (r.T != null) r.T.gameObject.SetActive(false); } _runners.Clear(); }

        void AngerIcon(Camera cam)
        {
            if (Art == null) return;
            DrawBubble(cam, Enemy.T, Enemy.Info != null ? Enemy.Info.Height : 0, 0);
            foreach (var x in Extras) DrawBubble(cam, x.T, x.Info != null ? x.Info.Height : 0, 0);
            foreach (var r in _runners) if (r.T != null && r.T.gameObject.activeSelf) DrawBubble(cam, r.T, r.Height, 1);
        }

        // renderFleeBubble: 0 angry, 1 scared; it rises and grows for 5 frames, then pulses (FLEE_BUBBLE_SCALE)
        void DrawBubble(Camera cam, Transform who, int height, int type)
        {
            int frame = (int)((_time - _phaseStart) * 20f);
            float head = height * 0.01f + (frame < 5 ? 2f + (frame + 1) * 0.1f : 2.5f);
            Vector3 world = who.position + Vector3.up * head, v = cam.WorldToViewportPoint(world);
            if (v.z <= 0f) return;
            float size = Mathf.Abs(cam.WorldToViewportPoint(world + cam.transform.right * 1.44f).x - v.x) * 320f;
            size *= frame < 5 ? (frame + 1) / 5f : BubbleScale[(frame - 5) % 10] / 100f;
            float x = Mathf.Clamp((v.x - 0.5f) * 320f, -140f, 140f), y = Mathf.Clamp((0.5f - v.y) * 240f, -100f, 100f);
            Vector3 feet = cam.WorldToViewportPoint(who.position);
            float dx = (feet.x - 0.5f) * 320f - x, dy = (0.5f - feet.y) * 240f - y;
            Art.Bubble(type, x, y, size, Mathf.Atan2(-dx, dy) * Mathf.Rad2Deg);
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
            // a wall in every direction: he stops where he is instead of running on the spot for ever (Glitch FAQ E, see OnGround)
            if (!OnGround(_player, by)) { BoyStops(); return true; }
            return false;
        }

        // handleFleeing (each frame of Run): the boy faces the last of his waypoints (dropping it once he is on it), then the tile he came in on, and
        // from there he faces FleeOut and runs on the spot; his run animation (3, started with the order) carries him until then
        void TickFlee()
        {
            if (_player == null) return;
            if (_playerA != null && _playerA.Slot != PlayerRunSlot) _playerA.SetSlot(PlayerRunSlot);
            Vector3 pos = _player.position, aim;
            Vector2Int here = TileOf(pos);
            if (_route != null && _route.Count > 0)
            {
                var t = _route[_route.Count - 1];
                aim = TileMid(t);
                if (here == t) _route.RemoveAt(_route.Count - 1);
            }
            else if (here != _startTile) aim = TileMid(_startTile);
            else
            {
                // on the tile he came in on: he faces out of the map and is held there (animFlag |= 2), running on the spot until the fight ends
                Vector3 o = FleeOut - pos; o.y = 0f;
                if (o.sqrMagnitude > 0.0001f) _player.rotation = _face(o.normalized);
                if (_playerA != null) _playerA.TakeForward();
                return;
            }
            Vector3 dir = aim - pos; dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) { dir.Normalize(); _player.rotation = _face(dir); }
            float step = _playerA != null ? _playerA.TakeForward() : 10f * Time.deltaTime;
            if (step > 0f && dir.sqrMagnitude > 0f) OnGround(_player, dir * step);
        }

        // BTL_handlePartnerMoveCommand, order 1 (Run; the order sets moveRange 1, which this path never reads): BTL_setWalking (walk 0x23 when poisoned or
        // at 1/5 HP or less, else run 0x24, BTL_startWalkingAnimation), then BTL_moveTowardLocation: it faces the boy at once (entityLookAtLocation) and its
        // animation carries it; a step into something keeps its old facing (collisionGrace is not ported). Once it is off screen (entityIsOffScreen
        // 0x140 x 0xF0) it takes animation 0x21 with animFlag 2, so its animation no longer moves it, until the fight ends.
        bool _partnerHeld;

        void RunAfterBoy(Fighter me, int walk)
        {
            if (_player == null) return;
            if (_partnerHeld) { if (me.A != null) me.A.TakeForward(); return; }
            if (me.A != null && me.A.Slot != WalkSlot && me.A.Slot != RunSlot) me.A.SetSlot(walk);
            Vector3 to = Flat(_player.position - me.T.position);
            Quaternion was = me.T.rotation;
            if (to.sqrMagnitude > 0.0001f) me.T.rotation = _face(to.normalized);
            float step = me.A != null ? me.A.TakeForward() : 0f;
            if (step > 0f && to.sqrMagnitude > 0.0001f && !OnGround(me.T, to.normalized * step)) me.T.rotation = was;
            Camera cam = _view != null ? _view : Camera.main;
            if (cam != null && OffScreen(cam, me.T.position, me.Info != null ? me.Info.Radius : 0, me.Info != null ? me.Info.Height : 0))
            {
                _partnerHeld = true;
                if (me.A != null && me.A.HasSlot(IdleSlot)) me.A.SetSlot(IdleSlot);
            }
        }

        static Vector3 TileMid(Vector2Int t) { return new Vector3(((t.x - 50) * 100 + 50) * 0.01f, 0f, ((50 - t.y) * 100 - 50) * 0.01f); }

        /// <summary>fadeToBlack(0x14) at FLEE_TIMER 20 (the partner still standing): how dark the picture is, 0 to 1.</summary>
        public float FleeBlack { get { return _running && Partner != null && Partner.Hp > 0 ? Mathf.Clamp01((_runFrames - 20) / 20f) : 0f; } }

        void BoyStops()
        {
            _boyDone = true;
            if (_playerA != null) _playerA.SetSlot(_playerA.HasSlot(1) ? 1 : 0);
            _player.rotation = _face(Partner.T.position - _player.position);
        }

        // entityCheckCombatArea: a corner of him, at the feet or 2 m up, is past the box round the middle of the screen and going further
        bool LeavesScreen(Vector3 from, Vector3 to, int w, int h) { return LeavesScreen(from, to, w, h, _boyRadius); }

        // hasMovedOutsideCombatArea: only a step that takes a corner further out past the box is refused; one already outside may come back in
        bool LeavesScreen(Vector3 from, Vector3 to, int w, int h, float radius)
        {
            Camera cam = _view != null ? _view : Camera.main;
            if (cam == null) return false;
            for (int i = 0; i < 2; i++)
                for (int up = 0; up < 2; up++)
                {
                    var corner = new Vector3((i == 0 ? -1 : 1) * radius, up * 2f, radius);
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
            // mod 16:9 (ours): the HP / MP and the finishing gauge in the top right corner of the wider view, the orders in the top left
            var hudMatrix = GUI.matrix;
            GUI.matrix = hudMatrix * Matrix4x4.Translate(new Vector3(UiKit.EdgeShift(1), 0f, 0f));
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
            GUI.matrix = hudMatrix * Matrix4x4.Translate(new Vector3(UiKit.EdgeShift(-1), 0f, 0f));
            // the order icons, top left, in the game's zigzag, with the cursor (BTL_renderCommandMenu)
            int n = _orders.Count + 1, count = n - 1, baseX = count * 14 - 140, top = -100, bottom = -68;
            bool blink = (int)(_time * 20f / 8f) % 2 == 1;
            int[] labelRun = LabelRun(n);
            int[] labelU = { 0, 11, 25, 39, 50 }, labelW = { 11, 14, 14, 11, 11 };
            for (int i = 0; i < n; i++)
            {
                int stripX = (i > 0 ? (i - 1) * 14 + 11 : 0) - 143;
                Art.CmdHalf(1, labelU[labelRun[i]], 32, labelW[labelRun[i]], bottom - top, stripX, top);      // setSemiTrans(prim, 1)
            }
            bool oddUp = count % 2 != 0;
            for (int i = 1; i < n; i++)
            {
                int cell = System.Array.IndexOf(OrderNames, _orders[i - 1]), row = 0;
                if (cell < 0) { row = 16; foreach (var t in Partner.Techs) if (t.Name == _orders[i - 1]) cell = Mathf.Clamp(t.Element, 0, 6); }
                bool upper = (i % 2 == 1) == oddUp;
                Art.Cmd(0, cell * 32 + (i == _hover && blink ? 16 : 0), row, 16, 16, baseX - i * 14, top + (upper ? 3 : 13));
            }
            // the cursor is added to the ordering table first, so it is drawn last, over the icons
            if (_phase == Phase.Fight)
            {
                bool upper = (_hover % 2 == 1) == oddUp;
                Art.Cursor(baseX - _hover * 14 - 3, upper ? -0x64 : -0x5a);
            }
            // BTL_drawHoveredCommandName: the hovered name in the game's font, colour 0, at (-0x8C, -0x42), in a 0x90-wide slice of the text page
            if (_phase == Phase.Fight) PsText(ClipTo(OrderZh(_orders[_hover - 1]), 0x90), -0x8C, -0x42, Tinted(0x80, 0x80, 0x80));
            GUI.matrix = hudMatrix * Matrix4x4.Translate(new Vector3(UiKit.EdgeShift(1), 0f, 0f));
            // the finishing-move gauge: F, FI ... FINISH, then !! and PUSH (BTL_renderFinisherGauge)
            int letters = Mathf.Clamp(Partner.Finish * 6 / Mathf.Max(1, Partner.FinishMax), 0, 6);
            if (letters == 6) { letters = 8; Art.Hud(2, 128, 8, 37, 9, 0x6e, -0x4b); }
            for (int i = 0; i < letters; i++) Art.Hud(12, FinishU[i], 29, FinishW[i], 15, FinishU[i] + 0x37, -0x4f);
            GUI.matrix = hudMatrix;
            // the charge bar over the partner's head (BTL_renderFinisherChargeup), up to 10 ticks
            if (_charging && _chargeBy == Partner && _view != null)
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

        // BTL_renderEnemyHPBars: one bar per enemy (fighters 1 to ENEMY_COUNT): a green outline with a line down to the enemy, the tinted strip and its HP.
        // Each shows for 21 frames after it is hit (its own BarFrames); Select (BATTLE_TOGGLE_LIFEBAR) keeps every living enemy's bar on.
        bool _lifeBarOn;
        float _hudAcc;

        bool BarShown(Fighter f)
        {
            if (!_lifeBarOn) return f.BarFrames < 21;
            return f.BarFrames < 21 || f.Hp > 0;
        }

        // the counting half of BTL_renderEnemyHPBars, once a game frame: the count goes up only while the bar is drawn,
        // the enemy is not knocked back and its damage has finished draining (damageTick); at 0 HP the drain is dropped
        void TickEnemyBar(Fighter f)
        {
            if (!BarShown(f)) return;
            if (f.Hp == 0) f.ShownHp = f.Hp;
            bool thrown = f.A != null && (f.A.Slot == HitFrontSlot || f.A.Slot == HitBackSlot);
            if (!thrown && f.ShownHp <= f.Hp) f.BarFrames++;
            DrainHp(f);
        }

        void EnemyBar(Camera cam, Fighter f)
        {
            if (Art == null || f.T == null || !BarShown(f)) return;
            Transform at = f.A != null ? f.A.Node(1) : null;
            Vector3 v = cam.WorldToViewportPoint(at != null ? at.position : f.T.position + Vector3.up);
            if (v.z <= 0f) return;
            float px = (v.x - 0.5f) * 320f, py = (0.5f - v.y) * 240f;
            float y = py < -0x4a ? -0x64 : py - 0x1a, x = px >= 0x42 ? 0x55 : px + 0x14;
            BattleArt.Line(x, y, x + 0x36, y);
            BattleArt.Line(x + 0x36, y, x + 0x36, y + 6);
            BattleArt.Line(x, y + 1, x, y + 6);
            BattleArt.Line(x, y + 6, x + 0x35, y + 6);
            BattleArt.Line(x, y + 6, px, py);
            int hp = Mathf.RoundToInt(f.ShownHp);
            Art.LifeFill(hp * 50 / f.MaxHp, x + 2, y + 2);
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

        /// <summary>
        /// The stats box after a fight (renderPostBattleStatsBox) and after training (TRN_renderPostTrainingStatsBox): same layout.
        /// Box (x, y, w, 96) in PS1 pixels from the middle; rows 13 apart from y + 9; name at x + 10, number at x + 68, bar at x + 66 (blue when raised),
        /// a line at x + 122, the blue arrow (12 x 12 at 96, 180 of the HUD sheet, colour row 11) at x + 130 and the gain at x + 142.
        /// Order HP, MP, Off, Def, Speed, Brains. Game font not decoded: names and digits are our text, in the game's colours.
        /// </summary>
        // ---- the game's text in the result and training boxes: drawString into the text page, then renderString / renderNumber with a TEXT_COLORS tint
        public static GameFont StatFont;
        static string[] _statNames = { "HP", "MP", "Off", "Def", "Speed", "Brain" };      // replaced by the exe's strings (0x80124C0C, 12 bytes each)
        static string _bitsLabel = "Bits";                                                  // STR_BITS 0x801344D4

        // a textured primitive multiplies the picture by colour / 128 (and clamps): the font's ink times the TEXT_COLORS entry
        static Color Tinted(int r, int g, int b)
        {
            Color ink = StatFont != null ? StatFont.Ink : Color.white;
            return new Color(Mathf.Min(1f, ink.r * r / 128f), Mathf.Min(1f, ink.g * g / 128f), Mathf.Min(1f, ink.b * b / 128f), 1f);
        }

        // renderString shows only w pixels of the text page: the letters that fit
        static string ClipTo(string s, int w)
        {
            if (StatFont == null || string.IsNullOrEmpty(s)) return s;
            while (s.Length > 0 && StatFont.Measure(s) > w) s = s.Substring(0, s.Length - 1);
            return s;
        }

        // text with its top left at (x, y), PS1 pixels from the middle of the screen
        static void PsText(string s, float x, float y, Color c)
        {
            if (StatFont != null) StatFont.Draw(s, 707.5f + x * 4.42f, 530f + y * 4.42f, 4.42f, c);
            else UiKit.Text(new Rect(707.5f + x * 4.42f, 530f + y * 4.42f - 8, 400, 62), s, 50, c, TextAnchor.MiddleLeft);
        }

        // renderNumber: n places 8 apart (the US DIGIT_WIDTH), the digits right-aligned, no leading zeros
        static void PsNumber(int value, int n, float x, float y, Color c)
        {
            string s = Mathf.Max(0, value).ToString();
            if (s.Length > n) s = s.Substring(s.Length - n);
            for (int i = 0; i < s.Length; i++) PsText(s[i].ToString(), x + (n - s.Length + i) * 8, y, c);
        }

        public static void DrawStatsBox(BattleArt art, float bx, float by, float bw, int[] values, int[] gains, bool[] raised, bool frame = true)
        {
            string[] names = _statNames;
            Func<float, float> px = v => 707.5f + v * 4.42f, py = v => 530f + v * 4.42f;
            if (frame) UiKit.Box(new Rect(px(bx), py(by), bw * 4.42f, 96 * 4.42f), UiKit.DialogFill);
            UiKit.Fill(new Rect(px(bx + 122), py(by + 2), 4.42f, 91 * 4.42f), new Color32(0x90, 0xD9, 0xFA, 255));
            UiKit.Fill(new Rect(px(bx + 123), py(by + 2), 4.42f, 91 * 4.42f), new Color32(2, 2, 2, 255));
            for (int i = 0; i < 6; i++)
            {
                float y = by + 9 + i * 13;
                int stat = values[i], max = i < 2 ? 9999 : 999;
                PsText(names[i], bx + 10, y, Tinted(0xC8, 0xB4, 0x32));          // renderString colour 4
                float ty = py(y + 11);
                UiKit.Fill(new Rect(px(bx + 64), ty - 3 * 4.42f, 53 * 4.42f, 4 * 4.42f), new Color32(0x66, 0x66, 0x66, 255));
                UiKit.Fill(new Rect(px(bx + 66), ty - 2 * 4.42f, 50 * 4.42f, 2 * 4.42f), raised[i] ? new Color32(0x00, 0x5A, 0x96, 255) : new Color32(0x28, 0x28, 0x28, 255));
                UiKit.Fill(new Rect(px(bx + 66), ty - 2 * 4.42f, Mathf.Min(stat, max) * 50 / max * 4.42f, 2 * 4.42f), raised[i] ? new Color32(0x69, 0xC2, 0xFF, 255) : new Color32(0x78, 0x78, 0x78, 255));
                // the number lies over its bar (user's PS1 screenshot 2026-11-04)
                PsNumber(stat, 4, bx + 68, y, Tinted(0x80, 0x80, 0x80));         // renderNumber colour 0, 4 places
                if (gains[i] <= 0) continue;
                if (art != null) art.Hud(11, 96, 52, 12, 12, bx + 130, y);
                PsNumber(gains[i], 3, bx + 142, y, Tinted(0x1E, 0x80, 0x80));    // renderNumber colour 5 (its blue is set from green)
            }
        }


        // renderString of a 'w'-wide slice: letters past the width are not shown
        static void ClipText(string s, float x, float y, int w, Color c) { PsText(ClipTo(s, w), x, y, c); }

        bool[] Raised() { var r = new bool[6]; for (int i = 0; i < 6; i++) r[i] = _gain[i] > 0; return r; }

        // renderBitBox: "Bits" (renderString colour 4) at (-78, 28), the Bits still to come (renderNumber colour 2, 5 places) at (-18, 28)
        void BitsContent()
        {
            PsText(_bitsLabel, -78, 28, Tinted(0xC8, 0xB4, 0x32));
            PsNumber(_bitsLeft, 5, -18, 28, Tinted(0x69, 0xC2, 0xC2));
        }

        // renderFinalBalance: the label (colour 0) at x + 10 and the money (renderNumber colour 0, 6 places) at x + 58, y + 10
        void FinalContent()
        {
            PsText(_bitsLabel, -78, -3, Tinted(0x80, 0x80, 0x80));
            PsNumber(Math.Min(_s.Money + _bits - _bitsLeft, 999999), 6, -30, -3, Tinted(0x80, 0x80, 0x80));
        }

        // getEntityScreenPos(entity, 1) - 5: the 10 x 10 square these boxes grow out of
        RectInt BoneSquare(Transform t, DigimonAnimator a)
        {
            Camera cam = _view != null ? _view : Camera.main;
            var n = a != null ? a.Node(1) : null;
            Vector2 p = cam != null && t != null ? Ps1Of(cam, n != null ? n.position : t.position) : Vector2.zero;
            return new RectInt((int)p.x - 5, (int)p.y - 5, 10, 10);
        }
    }
}
