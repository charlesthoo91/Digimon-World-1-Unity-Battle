using System;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// A fight between the partner and a wild Digimon, played on the map in real time, laid out like the PS1 game (from a recording):
    /// anger icon over the enemy, the red BATTLE banner, the boy standing at the right edge, the enemy on the left and the partner in the middle.
    /// HP/MP of the partner top right, the enemy's HP over its head, damage numbers, FINISH letters, "Your Call" for orders,
    /// and a result window with the stat gains and Bits.
    /// From the disc: names, stage, type, each Digimon's own technique. Our own formulas (not decoded yet): enemy stats, damage, rewards.
    /// </summary>
    public sealed class Battle
    {
        public sealed class Fighter
        {
            public Transform T; public DigimonAnimator A; public string Name;
            public DigimonStats Info;
            public int Off, Def, Spd, Brain, Hp, MaxHp, Mp, MaxMp;
            public string Tech; public int TechMp;
            public float NextAct, AnimReset;
            public Vector3 Slot;
            public bool Guarding;
        }

        sealed class Swing { public Fighter From, To; public float T; public bool Hit, Tech; public Vector3 Start; }
        sealed class Pop { public string Text; public Vector3 Pos; public float T; public Color Col; }

        enum Phase { Notice, Banner, Fight, Win, Lose, Fled }

        // animation slots (guessed from the opening's usage; the dev slot viewer, [ and ] in the editor, shows what each slot looks like)
        public static int AttackSlot = 4, TechSlot = 5, HitSlot = 8, DownSlot = 10, CheerSlot = 15;

        // orders the partner learns as its Brains grow: the first two are always there, then one more per 100 Brains (order of unlocking is a guess)
        static readonly string[] Learned = { "Give it all you got!", "Take it easy!", "Get back!", "Change target!", "Hang in there!" };
        const string CallOrder = "Your Call", RunOrder = "Run away", ItemOrder = "Item";

        public bool Active { get; private set; }
        public bool Won { get; private set; }
        public bool Fled { get; private set; }
        public bool NoFlee;                     // scripted fights (script variable 250 = 1): running away is locked
        public Fighter Partner, Enemy;

        readonly ScriptState _s;
        readonly Func<Vector3, Quaternion> _face;
        readonly System.Random _rng = new System.Random();
        Phase _phase; float _time, _phaseStart, _fleeAt = -1f, _retreatUntil, _nextAsk;
        Transform _player; DigimonAnimator _playerA; Vector3 _playerSlot;
        readonly List<string> _orders = new List<string>();
        string _order = CallOrder;
        bool _menuOpen; int _sel;
        readonly List<Swing> _swings = new List<Swing>();
        readonly List<Pop> _pops = new List<Pop>();
        readonly List<string> _result = new List<string>();
        readonly int[] _before = new int[6], _gain = new int[6];
        int _bits;
        string _log = ""; float _logUntil;
        static Texture2D _disc;

        public System.Action OnOpenBag;         // the room opens the bag (items work in battle)

        public Battle(ScriptState state, Func<Vector3, Quaternion> face) { _s = state; _face = face; }

        // -------------------------------------------------------------- setup
        public static int StartHp(int level) { int[] v = { 80, 80, 200, 400, 900, 2000, 4000, 6500 }; return v[Mathf.Clamp(level, 0, 7)]; }
        public static int StartOff(int level) { int[] v = { 15, 15, 30, 55, 100, 170, 260, 330 }; return v[Mathf.Clamp(level, 0, 7)]; }

        /// <summary>center = middle of the arena, right = the screen's right direction on the ground.</summary>
        public void Start(Transform player, DigimonAnimator playerAnim, Transform partner, DigimonAnimator partnerAnim, string partnerName, DigimonStats partnerInfo,
                          Transform enemy, DigimonAnimator enemyAnim, string enemyName, DigimonStats enemyInfo, Vector3 right, bool noFlee)
        {
            NoFlee = noFlee;
            Partner = new Fighter
            {
                T = partner, A = partnerAnim, Name = partnerName, Info = partnerInfo,
                Off = _s.Stats[0], Def = _s.Stats[1], Spd = _s.Stats[2], Brain = _s.Stats[3],
                MaxHp = Mathf.Max(1, _s.Stats[4]), MaxMp = _s.Stats[5], Hp = _s.Stats[6], Mp = _s.Stats[7],
                Tech = partnerInfo != null ? partnerInfo.SignatureTech : null
            };
            Partner.TechMp = TechCost(partnerInfo);
            if (Partner.Hp <= 0) Partner.Hp = 1;
            int lv = enemyInfo != null ? Mathf.Max(1, enemyInfo.Level) : 3;
            float r = 0.9f + (float)_rng.NextDouble() * 0.2f;
            int off = Mathf.RoundToInt(StartOff(lv) * r);
            Enemy = new Fighter
            {
                T = enemy, A = enemyAnim, Name = enemyName, Info = enemyInfo,
                Off = off, Def = Mathf.RoundToInt(off * 0.8f), Spd = Mathf.RoundToInt(off * 0.9f), Brain = Mathf.RoundToInt(off * 0.8f),
                MaxHp = Mathf.RoundToInt(StartHp(lv) * r), MaxMp = 9999, Mp = 9999,
                Tech = enemyInfo != null ? enemyInfo.SignatureTech : null
            };
            Enemy.Hp = Enemy.MaxHp;

            _orders.Clear(); _orders.Add(CallOrder); _orders.Add(RunOrder);
            for (int i = 0; i < Learned.Length && i < Partner.Brain / 100; i++) _orders.Add(Learned[i]);
            _order = CallOrder; _menuOpen = false; _sel = 0;

            right.y = 0; right = right.sqrMagnitude < 0.01f ? Vector3.right : right.normalized;
            Vector3 center = (partner.position + enemy.position) * 0.5f; center.y = 0;
            Enemy.Slot = center - right * 1.8f; Partner.Slot = center + right * 1.2f; _playerSlot = center + right * 4.2f;
            _player = player; _playerA = playerAnim;
            Active = true; Won = false; Fled = false; _phase = Phase.Notice; _time = 0f; _phaseStart = 0f; _fleeAt = -1f; _retreatUntil = 0f; _nextAsk = 6f;
            _swings.Clear(); _pops.Clear(); _result.Clear(); _log = "";
            Partner.NextAct = Enemy.NextAct = 0f;
            enemy.rotation = _face(partner.position - enemy.position);
            if (Partner.A != null) Partner.A.SetSlot(0);
            if (Enemy.A != null) Enemy.A.SetSlot(0);
        }

        static int TechCost(DigimonStats i) { return i == null ? 30 : 10 * Mathf.Max(1, i.Level); }   // Pepper Breath (Rookie) costs 30 MP in the game

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
        /// <summary>step = up/down, action = Cross, cancel = Triangle. Called every frame while Active (not while the bag is open).</summary>
        public void Tick(int step, bool action, bool cancel)
        {
            if (!Active) return;
            float dt = Time.deltaTime; _time += dt;
            Animate(Partner); Animate(Enemy);
            for (int i = _pops.Count - 1; i >= 0; i--) { _pops[i].T += dt; if (_pops[i].T > 1.2f) _pops.RemoveAt(i); }
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
                    if (_menuOpen)
                    {
                        if (step != 0) _sel = (_sel + step + _orders.Count + 1) % (_orders.Count + 1);
                        if (cancel) _menuOpen = false;
                        else if (action) { _menuOpen = false; Choose(_sel); }
                    }
                    else if (action) { _menuOpen = true; _sel = Mathf.Max(0, _orders.IndexOf(_order)); }
                    if (_time >= _retreatUntil) TickFighter(Partner, Enemy, dt); else MoveTo(Partner.T, Partner.A, _playerSlot - (_playerSlot - Partner.Slot).normalized * 1.5f, 4f, dt);
                    if (_retreatUntil > 0f && _time >= _retreatUntil) { _retreatUntil = 0f; Partner.T.rotation = _face(Enemy.T.position - Partner.T.position); }
                    TickFighter(Enemy, Partner, dt);
                    TickSwings(dt);
                    if (_fleeAt > 0f && _time >= _fleeAt) TryFlee();
                    if (Enemy.Hp <= 0) Finish(true);
                    else if (Partner.Hp <= 0) Finish(false);
                    break;
                case Phase.Win:
                case Phase.Lose:
                    TickSwings(dt);
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
            if (_phase == Phase.Win) { for (int i = 0; i < 6; i++) { int[] idx = { 4, 5, 0, 1, 2, 3 }; _s.SetStat(idx[i], _before[i] + _gain[i]); } _s.Money += _bits; }
            if (_phase == Phase.Win && Partner.A != null) Partner.A.SetSlot(0);
            Active = false;
        }

        void Choose(int i)
        {
            if (i >= _orders.Count) { if (OnOpenBag != null) OnOpenBag(); return; }
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
            if (_rng.NextDouble() < chance) { Fled = true; Sync(); SetPhase(Phase.Fled); }
            else Log("Couldn't get away!");
        }

        void Animate(Fighter f) { if (f != null && f.A != null && f.AnimReset > 0f && _time >= f.AnimReset) { f.AnimReset = 0f; if (f.Hp > 0) f.A.SetSlot(0); } }

        void Play(Fighter f, int slot, float seconds)
        {
            if (f.A == null) return;
            if (f.A.HasSlot(slot)) { f.A.SetSlot(slot, true); f.AnimReset = _time + seconds; }
        }

        void TickFighter(Fighter me, Fighter foe, float dt)
        {
            if (me.Hp <= 0 || foe.Hp <= 0 || _time < me.NextAct) return;
            foreach (var sw in _swings) if (sw.From == me) return;                    // still swinging
            bool isPartner = me == Partner;
            if (isPartner && _order == Learned[4]) { me.NextAct = _time + Interval(me); me.Mp = Mathf.Min(me.MaxMp, me.Mp + me.MaxMp / 50); me.Guarding = true; Sync(); return; }
            me.Guarding = false;
            bool tech;
            if (isPartner)
            {
                double want = _order == Learned[0] ? 0.9 : _order == Learned[1] ? 0.0 : 0.5;   // all out / take it easy / your call
                tech = me.Tech != null && me.Mp >= me.TechMp && _rng.NextDouble() < want;
            }
            else tech = me.Tech != null && _rng.NextDouble() < 0.25;
            if (isPartner)
            {
                if (tech) me.Mp -= me.TechMp;
                else if (_order == Learned[1]) me.Mp = Mathf.Min(me.MaxMp, me.Mp + me.MaxMp / 40);   // taking it easy lets MP come back
            }
            me.NextAct = _time + Interval(me) * (0.9f + (float)_rng.NextDouble() * 0.3f);
            _swings.Add(new Swing { From = me, To = foe, Tech = tech, Start = me.T.position });
            me.T.rotation = _face(foe.T.position - me.T.position);
            Play(me, tech ? TechSlot : AttackSlot, 0.9f);
            if (tech) Log(me.Name + ": " + me.Tech + "!");
            Sync();
        }

        void TickSwings(float dt)
        {
            for (int i = _swings.Count - 1; i >= 0; i--)
            {
                var sw = _swings[i]; sw.T += dt;
                Vector3 dir = sw.To.T.position - sw.Start; dir.y = 0;
                float dist = dir.magnitude; Vector3 d = dist > 0.01f ? dir / dist : Vector3.forward;
                // lunge in, hit, and back (technique users stay back and shoot)
                float go = Mathf.Clamp01(sw.T / 0.35f), back = Mathf.Clamp01((sw.T - 0.5f) / 0.35f);
                float reach = Mathf.Max(0f, dist - 1.3f) * (sw.Tech ? 0f : 0.8f);
                sw.From.T.position = sw.Start + d * reach * (go - back);
                if (!sw.Hit && sw.T >= 0.35f) { sw.Hit = true; Apply(sw); }
                if (sw.T >= 0.9f) { sw.From.T.position = sw.Start; _swings.RemoveAt(i); }
            }
        }

        void Apply(Swing sw)
        {
            var a = sw.From; var d = sw.To;
            if (d.Hp <= 0) return;
            float mult = sw.Tech ? 1.45f + 0.05f * (a.Info != null ? a.Info.Level : 3) : 0.45f;
            float v = a.Off * mult * (0.85f + (float)_rng.NextDouble() * 0.3f) * 100f / (100f + d.Def * 0.8f);
            if (d.Guarding) v *= 0.5f;
            int dmg = Mathf.Max(1, Mathf.RoundToInt(v));
            d.Hp = Mathf.Max(0, d.Hp - dmg);
            _pops.Add(new Pop { Text = dmg.ToString(), Pos = d.T.position + Vector3.up * 0.9f, T = 0f, Col = Color.white });
            if (d.Hp > 0) Play(d, HitSlot, 0.6f);
            else Play(d, DownSlot, 3f);
            Sync();
        }

        void Sync() { _s.Stats[6] = (ushort)Mathf.Clamp(Partner.Hp, 0, 9999); _s.Stats[7] = (ushort)Mathf.Clamp(Partner.Mp, 0, 9999); }

        /// <summary>Items used from the bag during the fight change the stats; this picks them up.</summary>
        public void ReadBack()
        {
            if (Partner == null) return;
            if (_s.Stats[6] > Partner.Hp) ShowHeal(_s.Stats[6] - Partner.Hp);
            Partner.Hp = _s.Stats[6]; Partner.Mp = _s.Stats[7]; Partner.MaxHp = Mathf.Max(1, _s.Stats[4]); Partner.MaxMp = _s.Stats[5];
        }

        /// <summary>An item healed the partner: show the number like the game does.</summary>
        public void ShowHeal(int amount) { if (Partner != null && Partner.T != null) _pops.Add(new Pop { Text = amount.ToString(), Pos = Partner.T.position + Vector3.up * 0.9f, T = 0f, Col = new Color(0.4f, 1f, 0.4f) }); }

        void Finish(bool won)
        {
            Won = won; _result.Clear();
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

        public void OnGUI(Camera cam)
        {
            if (!Active || Partner == null) return;
            if (_phase == Phase.Notice && cam != null) AngerIcon(cam, Enemy.T.position + Vector3.up * 2.2f);
            UiKit.Begin();
            if (_phase == Phase.Banner) Banner(_time - _phaseStart);
            if (_phase != Phase.Notice && _phase != Phase.Banner || (_phase == Phase.Banner))
            {
                Hud();
            }
            if (_phase == Phase.Fight)
            {
                Outlined(new Rect(50, 160, 400, 60), "Your Call", 46, Color.white);
                if (_menuOpen) Menu();
                else if (_time < _logUntil) Outlined(new Rect(50, 880, 1300, 70), _log, 46, Color.white);
            }
            if ((_phase == Phase.Win) && _time - _phaseStart > 1.2f) ResultWindow();
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
                foreach (var p in _pops) Floating(cam, p.Pos + Vector3.up * p.T * 0.9f, p.Text, 40, p.Col);
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
            var c = new Color(0.8f, 0.1f, 0.15f, alpha);
            var st = new GUIStyle(GUI.skin.label) { fontSize = (int)(150 * scale), alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            st.normal.textColor = new Color(0.2f, 0f, 0.03f, alpha);
            GUI.Label(new Rect(rc.x + 6, rc.y + 6, rc.width, rc.height), "BATTLE", st);
            st.normal.textColor = c; GUI.Label(rc, "BATTLE", st);
        }

        void Hud()
        {
            // partner HP / MP, top right
            DrawBar(new Rect(900, 40, 330, 34), Partner.MaxHp > 0 ? Partner.Hp / (float)Partner.MaxHp : 0f, new Color(0.9f, 0.25f, 0.3f), new Color(0.95f, 0.3f, 0.4f), true);
            DrawBar(new Rect(900, 82, 330, 34), Partner.MaxMp > 0 ? Partner.Mp / (float)Partner.MaxMp : 0f, new Color(0.25f, 0.55f, 0.95f), new Color(0.3f, 0.7f, 1f), false);
            Outlined(new Rect(1235, 34, 150, 46), Partner.Hp.ToString(), 40, Color.white, TextAnchor.MiddleRight);
            Outlined(new Rect(1235, 76, 150, 46), Partner.Mp.ToString(), 40, Color.white, TextAnchor.MiddleRight);
            // mood face and the current order, top left
            DrawDisc(new Rect(40, 36, 96, 96), new Color(0.35f, 0.3f, 0.05f));
            DrawDisc(new Rect(46, 42, 84, 84), new Color(1f, 0.85f, 0.15f));
            UiKit.Fill(new Rect(68, 68, 12, 18), Color.black); UiKit.Fill(new Rect(98, 68, 12, 18), Color.black);
            UiKit.Fill(new Rect(70, 98, 38, 8), Color.black);
            UiKit.Fill(new Rect(120, 50, 82, 82), new Color(0.12f, 0.3f, 0.4f));
            UiKit.Fill(new Rect(124, 54, 74, 74), new Color(0.2f, 0.75f, 0.95f));
            UiKit.Text(new Rect(124, 54, 74, 74), _order == RunOrder ? ">>" : _order == CallOrder ? "?" : _order.Substring(0, 1), 46, Color.black, TextAnchor.MiddleCenter);
            // FINISH letters grow as the enemy weakens
            if (Enemy.MaxHp > 0 && Enemy.Hp < Enemy.MaxHp)
            {
                int letters = Mathf.Clamp(Mathf.FloorToInt(6f * (1f - Enemy.Hp / (float)Enemy.MaxHp)), 0, 6);
                if (Enemy.Hp <= 0) letters = 6;
                if (letters > 0) Outlined(new Rect(930, 128, 300, 60), "FINISH".Substring(0, letters), 44, new Color(1f, 0.9f, 0.2f));
            }
        }

        static void DrawBar(Rect r, float t, Color back, Color fill, bool heart)
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
            int n = _orders.Count + 1;
            var box = new Rect(40, 620, 640, 80 + n * 62);
            UiKit.Box(box, UiKit.PanelFill);
            for (int i = 0; i < n; i++)
            {
                string o = i < _orders.Count ? _orders[i] : ItemOrder;
                bool locked = o == RunOrder && NoFlee;
                Color c = i == _sel ? UiKit.Yellow : locked ? UiKit.Grey : o == _order ? UiKit.Cyan : Color.white;
                UiKit.Text(new Rect(box.x + 40, box.y + 30 + i * 62, box.width - 60, 60), (i == _sel ? "> " : "   ") + o, 42, c, TextAnchor.MiddleLeft);
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
