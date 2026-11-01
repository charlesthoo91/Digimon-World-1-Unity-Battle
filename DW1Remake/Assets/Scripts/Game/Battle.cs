using System;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// A fight between the partner and a wild Digimon, played out on the map in real time.
    /// You give orders (Give it all you got! / Take it easy! / Hang in there! / Get back!) and use items; the partner fights by itself.
    /// What comes from the disc: names, stage, type and each Digimon's own technique. The numbers (enemy stats, damage, technique
    /// power and cost, rewards) are our own formulas until the game's battle tables are decoded.
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
            public Vector3 Home;
            public bool Guarding;
        }

        sealed class Swing { public Fighter From, To; public float T; public bool Hit, Tech; public Vector3 Start; }
        sealed class Pop { public string Text; public Vector3 Pos; public float T; public Color Col; }

        enum Phase { Intro, Fight, Win, Lose, Fled }

        // animation slots (guessed from the opening's usage; the dev slot viewer, [ ] and \ in the editor, shows what each slot looks like)
        public static int AttackSlot = 4, TechSlot = 5, HitSlot = 8, DownSlot = 10, CheerSlot = 15;

        public bool Active { get; private set; }
        public bool Won { get; private set; }
        public Fighter Partner, Enemy;

        readonly ScriptState _s;
        readonly Func<Vector3, Quaternion> _face;
        readonly System.Random _rng = new System.Random();
        Phase _phase; float _time, _fleeAt = -1f;
        int _order, _sel;                       // _order: 0 all out, 1 take it easy, 2 guard; _sel = highlighted line
        readonly List<Swing> _swings = new List<Swing>();
        readonly List<Pop> _pops = new List<Pop>();
        List<string> _result = new List<string>();
        string _log = "";
        float _logUntil;

        public System.Action OnOpenBag;         // the room opens the bag (items work in battle)
        public static readonly string[] Orders = { "Give it all you got!", "Take it easy!", "Hang in there!", "Get back!", "Item" };

        public Battle(ScriptState state, Func<Vector3, Quaternion> face) { _s = state; _face = face; }

        // -------------------------------------------------------------- setup
        public static int StartHp(int level) { int[] v = { 120, 150, 350, 600, 1300, 2600, 4500, 6500 }; return v[Mathf.Clamp(level, 0, 7)]; }
        public static int StartOff(int level) { int[] v = { 20, 20, 35, 55, 100, 170, 260, 330 }; return v[Mathf.Clamp(level, 0, 7)]; }

        public void Start(Transform partner, DigimonAnimator partnerAnim, string partnerName, DigimonStats partnerInfo,
                          Transform enemy, DigimonAnimator enemyAnim, string enemyName, DigimonStats enemyInfo)
        {
            Partner = new Fighter
            {
                T = partner, A = partnerAnim, Name = partnerName, Info = partnerInfo,
                Off = _s.Stats[0], Def = _s.Stats[1], Spd = _s.Stats[2], Brain = _s.Stats[3],
                MaxHp = Mathf.Max(1, _s.Stats[4]), MaxMp = _s.Stats[5], Hp = _s.Stats[6], Mp = _s.Stats[7],
                Tech = partnerInfo != null ? partnerInfo.SignatureTech : null
            };
            Partner.TechMp = TechCost(partnerInfo);
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
            Enemy.Hp = Enemy.MaxHp; Enemy.TechMp = 0;
            if (Partner.Hp <= 0) Partner.Hp = 1;

            Vector3 toPartner = partner.position - enemy.position; toPartner.y = 0;
            if (toPartner.sqrMagnitude < 0.01f) toPartner = Vector3.back;
            Enemy.Home = enemy.position;
            Partner.Home = enemy.position + toPartner.normalized * 2.6f;
            enemy.rotation = _face(partner.position - enemy.position);
            Active = true; Won = false; _phase = Phase.Intro; _time = 0f; _order = 0; _sel = 0; _fleeAt = -1f;
            _swings.Clear(); _pops.Clear(); _result.Clear(); _log = "";
            Partner.NextAct = Enemy.NextAct = 0f;
            if (Partner.A != null) Partner.A.SetSlot(2);
            if (Enemy.A != null) Enemy.A.SetSlot(0);
            Log(Enemy.Name + " appeared!");
        }

        static int TechCost(DigimonStats i) { return i == null ? 100 : 40 + 20 * Mathf.Max(1, i.Level); }

        float Interval(Fighter f) { return Mathf.Clamp(4.2f - f.Spd / 45f, 1.3f, 3.8f); }

        void Log(string s) { _log = s; _logUntil = _time + 2.5f; }

        // -------------------------------------------------------------- per frame
        /// <summary>step = up/down, action = Cross, cancel = Triangle. Called every frame while Active (not while the bag is open).</summary>
        public void Tick(int step, bool action, bool cancel)
        {
            if (!Active) return;
            float dt = Time.deltaTime; _time += dt;
            Animate(Partner); Animate(Enemy);
            for (int i = _pops.Count - 1; i >= 0; i--) { _pops[i].T += dt; if (_pops[i].T > 1.2f) _pops.RemoveAt(i); }

            switch (_phase)
            {
                case Phase.Intro:
                {
                    Vector3 to = Partner.Home - Partner.T.position; to.y = 0;
                    if (to.magnitude < 0.1f) { Partner.T.rotation = _face(Enemy.T.position - Partner.T.position); if (Partner.A != null) Partner.A.SetSlot(0); BeginFight(); break; }
                    Partner.T.position += to.normalized * Mathf.Min(to.magnitude, 4f * dt);
                    Partner.T.rotation = Quaternion.RotateTowards(Partner.T.rotation, _face(to), 540f * dt);
                    break;
                }
                case Phase.Fight:
                    if (step != 0) _sel = (_sel + step + Orders.Length) % Orders.Length;
                    if (action) Choose(_sel);
                    TickFighter(Partner, Enemy, dt);
                    TickFighter(Enemy, Partner, dt);
                    TickSwings(dt);
                    if (_fleeAt > 0f && _time >= _fleeAt) TryFlee();
                    if (Enemy.Hp <= 0) Finish(true);
                    else if (Partner.Hp <= 0) Finish(false);
                    break;
                case Phase.Win:
                case Phase.Lose:
                    TickSwings(dt);
                    if (_time - _endTime > 0.9f && (action || cancel)) Active = false;
                    break;
                case Phase.Fled:
                    if (_time - _endTime > 0.4f && (action || cancel)) Active = false;
                    break;
            }
        }

        void BeginFight()
        {
            _phase = Phase.Fight;
            Partner.NextAct = _time + Interval(Partner) * 0.6f;
            Enemy.NextAct = _time + Interval(Enemy) * (0.8f + (float)_rng.NextDouble() * 0.6f);
        }

        void Choose(int i)
        {
            if (i < 3) { _order = i; Partner.Guarding = i == 2; Log(Orders[i]); }
            else if (i == 3) { if (_fleeAt < 0f) { _fleeAt = _time + 1.2f; Log("Get back!"); } }
            else if (OnOpenBag != null) OnOpenBag();
        }

        void TryFlee()
        {
            _fleeAt = -1f;
            float chance = Mathf.Clamp(0.4f + (Partner.Spd - Enemy.Spd) / 200f, 0.15f, 0.9f);
            if (_rng.NextDouble() < chance) { _result.Clear(); _result.Add("You got away safely."); _phase = Phase.Fled; _endTime = _time; Won = false; if (Partner.A != null) Partner.A.SetSlot(0); Sync(); }
            else Log("Couldn't get away!");
        }

        float _endTime;

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
            if (isPartner && _order == 2) { me.NextAct = _time + Interval(me); me.Mp = Mathf.Min(me.MaxMp, me.Mp + me.MaxMp / 50); me.Guarding = true; Sync(); return; }
            me.Guarding = false;
            bool tech;
            if (isPartner) tech = _order == 0 && me.Tech != null && me.Mp >= me.TechMp && _rng.NextDouble() < 0.7;
            else tech = me.Tech != null && _rng.NextDouble() < 0.25;
            if (isPartner)
            {
                if (tech) me.Mp -= me.TechMp;
                else if (_order == 1) me.Mp = Mathf.Min(me.MaxMp, me.Mp + me.MaxMp / 40);   // taking it easy lets MP come back
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
                Vector3 target = sw.To.T.position; Vector3 dir = target - sw.Start; dir.y = 0;
                float dist = dir.magnitude; Vector3 d = dist > 0.01f ? dir / dist : Vector3.forward;
                // lunge in, hit, and back
                float go = Mathf.Clamp01(sw.T / 0.35f), back = Mathf.Clamp01((sw.T - 0.5f) / 0.35f);
                float reach = Mathf.Max(0f, dist - 1.3f) * (sw.Tech ? 0.25f : 1f);
                sw.From.T.position = sw.Start + d * reach * (go - back);
                if (!sw.Hit && sw.T >= 0.35f) { sw.Hit = true; Apply(sw); }
                if (sw.T >= 0.9f) { sw.From.T.position = sw.Start; _swings.RemoveAt(i); }
            }
        }

        void Apply(Swing sw)
        {
            var a = sw.From; var d = sw.To;
            if (d.Hp <= 0) return;
            float mult = sw.Tech ? 2.2f + 0.25f * (a.Info != null ? a.Info.Level : 3) : 1f;
            float v = a.Off * mult * (0.85f + (float)_rng.NextDouble() * 0.3f) * 150f / (100f + d.Def * 0.8f);
            if (d.Guarding) v *= 0.5f;
            int dmg = Mathf.Max(1, Mathf.RoundToInt(v));
            d.Hp = Mathf.Max(0, d.Hp - dmg);
            _pops.Add(new Pop { Text = dmg.ToString(), Pos = d.T.position + Vector3.up * 1.3f, T = 0f, Col = d == Partner ? new Color(1f, 0.5f, 0.45f) : Color.white });
            if (d.Hp > 0) Play(d, HitSlot, 0.6f);
            else Play(d, DownSlot, 3f);
            Sync();
        }

        void Sync() { _s.Stats[6] = (ushort)Mathf.Clamp(Partner.Hp, 0, 9999); _s.Stats[7] = (ushort)Mathf.Clamp(Partner.Mp, 0, 9999); }

        /// <summary>Items used from the bag during the fight change the stats; this picks them up.</summary>
        public void ReadBack()
        {
            if (Partner == null) return;
            Partner.Hp = _s.Stats[6]; Partner.Mp = _s.Stats[7]; Partner.MaxHp = Mathf.Max(1, _s.Stats[4]); Partner.MaxMp = _s.Stats[5];
        }

        void Finish(bool won)
        {
            Won = won; _endTime = _time; _result.Clear();
            if (won)
            {
                _phase = Phase.Win;
                int lv = Enemy.Info != null ? Mathf.Max(1, Enemy.Info.Level) : 3;
                int bits = 25 * lv * lv + _rng.Next(0, 15 * lv + 1);
                _s.Money += bits;
                _result.Add(Partner.Name + " won!");
                _result.Add("Got " + bits + " Bits.");
                int[] pick = { 0, 1, 2, 3 }; string[] nm = { "Offense", "Defense", "Speed", "Brains" };
                int k = pick[_rng.Next(4)]; int gain = 1 + _rng.Next(0, 3);
                _s.AddStat(k, gain); _result.Add(nm[k] + " +" + gain);
                _s.Happiness = Mathf.Min(100, _s.Happiness + 2);
                if (_rng.NextDouble() < 0.3 && _s.AddItem(38, 1)) _result.Add("Found Meat.");
                if (Partner.A != null) { Partner.A.SetSlot(CheerSlot, true); }
                if (Enemy.A != null && !Enemy.A.HasSlot(DownSlot)) Enemy.T.gameObject.SetActive(false);
            }
            else
            {
                _phase = Phase.Lose;
                _result.Add(Partner.Name + " collapsed...");
                _result.Add("You carry it back to File City.");
                if (Partner.A != null) Partner.A.SetSlot(DownSlot, true);
            }
            Sync();
        }

        // -------------------------------------------------------------- drawing
        public void OnGUI(Camera cam)
        {
            if (!Active || Partner == null) return;
            UiKit.Begin();
            // partner (left) and enemy (right)
            Plate(new Rect(30, 25, 560, 150), Partner, true);
            Plate(new Rect(825, 25, 560, 150), Enemy, false);
            if (_phase == Phase.Fight || _phase == Phase.Intro)
            {
                var box = new Rect(30, 640, 560, 380);
                UiKit.Box(box, UiKit.PanelFill);
                for (int i = 0; i < Orders.Length; i++)
                {
                    bool cur = i == _sel, active = i < 3 && i == _order;
                    UiKit.Text(new Rect(box.x + 40, box.y + 22 + i * 68, box.width - 60, 64), (cur ? "> " : "   ") + Orders[i], 44, cur ? UiKit.Yellow : (active ? UiKit.Cyan : Color.white), TextAnchor.MiddleLeft);
                }
                if (_time < _logUntil) UiKit.Text(new Rect(30, 560, 1000, 60), _log, 44, Color.white, TextAnchor.MiddleLeft);
            }
            else
            {
                var box = new Rect(300, 330, 815, 380);
                UiKit.Box(box, new Color(0.10f, 0.17f, 0.15f, 0.95f));
                for (int i = 0; i < _result.Count; i++) UiKit.Text(new Rect(box.x + 50, box.y + 30 + i * 70, box.width - 80, 66), _result[i], 50, i == 0 ? UiKit.Yellow : Color.white, TextAnchor.MiddleLeft);
                if ((int)(Time.time * 2f) % 2 == 0) UiKit.Text(new Rect(box.xMax - 90, box.yMax - 70, 60, 50), "X", 40, new Color(0.5f, 0.6f, 1f), TextAnchor.MiddleCenter);
            }
            UiKit.End();
            if (cam != null)
                foreach (var p in _pops)
                {
                    Vector3 sp = cam.WorldToScreenPoint(p.Pos + Vector3.up * p.T * 0.9f);
                    if (sp.z <= 0) continue;
                    var st = new GUIStyle(GUI.skin.label) { fontSize = 34, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                    var rc = new Rect(sp.x - 80, Screen.height - sp.y - 20, 160, 40);
                    st.normal.textColor = Color.black; GUI.Label(new Rect(rc.x + 2, rc.y + 2, rc.width, rc.height), p.Text, st);
                    st.normal.textColor = p.Col; GUI.Label(rc, p.Text, st);
                }
        }

        void Plate(Rect r, Fighter f, bool showMp)
        {
            UiKit.Box(r, UiKit.PanelFill);
            string lv = f.Info != null ? "   " + f.Info.LevelName : "";
            UiKit.Text(new Rect(r.x + 25, r.y + 8, r.width - 50, 56), f.Name + lv, 40, UiKit.Yellow, TextAnchor.MiddleLeft);
            UiKit.Text(new Rect(r.x + 25, r.y + 62, 90, 40), "HP", 34, Color.white, TextAnchor.MiddleLeft);
            UiKit.Bar(new Rect(r.x + 100, r.y + 66, 300, 32), f.MaxHp > 0 ? f.Hp / (float)f.MaxHp : 0f, f.Hp * 4 < f.MaxHp ? new Color(0.95f, 0.35f, 0.3f) : new Color(0.3f, 0.85f, 0.4f));
            if (showMp) UiKit.Text(new Rect(r.x + 410, r.y + 62, 140, 40), f.Hp + "/" + f.MaxHp, 30, Color.white, TextAnchor.MiddleLeft);
            if (showMp)
            {
                UiKit.Text(new Rect(r.x + 25, r.y + 104, 90, 40), "MP", 34, Color.white, TextAnchor.MiddleLeft);
                UiKit.Bar(new Rect(r.x + 100, r.y + 108, 300, 28), f.MaxMp > 0 ? f.Mp / (float)f.MaxMp : 0f, UiKit.Cyan);
                UiKit.Text(new Rect(r.x + 410, r.y + 104, 140, 40), f.Mp + "/" + f.MaxMp, 30, Color.white, TextAnchor.MiddleLeft);
            }
        }
    }
}
