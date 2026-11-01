using System;
using DW1Remake.Formats;

namespace DW1Remake.Game
{
    /// <summary>
    /// The game clock and the partner's care numbers, ported from the US game (decompilation: main/clock.c, partner_impl.c, tamer.c).
    /// One Tick is one game frame, 20 a second: 20 frames = 1 game minute, 1200 frames = 1 game hour.
    /// Hunger, toilet, sleep, sickness, moods and evolution are built on this in the following steps.
    /// </summary>
    public sealed class PartnerCare
    {
        public const int Sleepy = 1, Tired = 2, Hungry = 4, Poopy = 8, Unhappy = 16, Injured = 32, Sick = 64;   // PartnerPara.Condition bits
        public const int Lost = -1, FledBattle = 0, WonBattle = 1;                                              // result of a fight

        readonly ScriptState _s;
        readonly Func<int, RaiseData> _raiseOf;
        readonly Func<int, SleepPattern> _sleepOf;
        readonly Func<int, int> _levelOf;
        readonly Random _rng = new Random();
        public int PartnerType;
        public int Hour = 8, Minute, Frame = 9600;   // the game starts at 08:00, frame 8 x 1200
        public Action OnNewDay;

        public PartnerCare(ScriptState state, Func<int, RaiseData> raiseOf, Func<int, SleepPattern> sleepOf, Func<int, int> levelOf)
        {
            _s = state; _raiseOf = raiseOf; _sleepOf = sleepOf; _levelOf = levelOf;
            _s.AfterStatChange = ScriptEnergyBoundaries;
        }

        PartnerPara P { get { return _s.Para; } }
        RaiseData Raise { get { return _raiseOf(PartnerType); } }
        int RandomLimit(int limit) { return _rng.Next(limit); }

        /// <summary>The clock as hours, 0..24, for the day / night code.</summary>
        public float ClockHours { get { return Hour + (Minute + (Frame % 20) / 20f) / 60f; } }

        /// <summary>Puts the clock at a time without anything else happening (a loaded save).</summary>
        public void SetClock(int hour, int minute) { Hour = hour % 24; Minute = minute; Frame = Hour * 1200 + Minute * 20; }

        // initializePartner: every value a partner starts with. Age is not set by the game here.
        public void InitPartner(int type)
        {
            PartnerType = type;
            var r = _raiseOf(type);
            var p = P;
            p.Condition = 0;
            SetSleepTimes(type);
            p.MissedSleepHours = 0;
            p.PoopLevel = r.PoopTimer;
            p.PoopingTimer = -1;
            p.Virus = 0; p.Tiredness = 0;
            _s.Discipline = 50; _s.Happiness = 50;
            SetFoodTimer(type);
            p.Energy = r.EnergyThreshold;
            p.Lifetime = 360;
            _s.Weight = r.DefaultWeight;
            p.TrainBoostFlag = 0; p.TrainBoostValue = 0; p.TrainBoostTimer = 0;
            p.CareMistakes = 0; p.Battles = 0; p.EvoTimer = 0;
            p.TimesBeingSick = 0; p.AreaEffectTimer = 0; p.SicknessTimer = 0; p.InjuryTimer = 0;
            _s.Life = 3;
        }

        // setSleepTimes: Baby I and II sleep by the hours they have been awake, the others by the clock
        void SetSleepTimes(int type)
        {
            var p = P;
            var pattern = _sleepOf(_raiseOf(type).SleepCycle);
            int level = _levelOf(type);
            if (level < 3)
            {
                int awake = (level == 1 ? 3 : 7) + RandomLimit(1);
                p.SleepyHour = (Hour + awake) % 24;
                p.SleepyMinute = 0;
                p.WakeupHour = (p.SleepyHour + pattern.SleepyHours) % 24;
                p.WakeupMinute = 0;
                p.HoursAwakeDefault = awake;
            }
            else
            {
                p.SleepyHour = pattern.SleepyHour; p.SleepyMinute = pattern.SleepyMinute;
                p.WakeupHour = pattern.WakeupHour; p.WakeupMinute = pattern.WakeupMinute;
                p.HoursAwakeDefault = pattern.AwakeHours;
            }
            p.HoursAsleepDefault = pattern.SleepyHours;
            p.TimeAwakeToday = p.HoursAwakeDefault * 6;
            p.SicknessCounter = 0;
            p.TirednessSleepTimer = 0;
        }

        // setFoodTimer (US version): when it next gets hungry, and FoodLevel = the minutes until then
        public void SetFoodTimer(int type)
        {
            var p = P;
            int level = _levelOf(type);
            if (level == 1) { p.NextHungerHour = Hour / 2 * 2 + 2; if (p.NextHungerHour >= 24) p.NextHungerHour -= 24; }
            else if (level == 2) { p.NextHungerHour = Hour / 3 * 3 + 3; if (p.NextHungerHour >= 24) p.NextHungerHour -= 24; }
            else NextHungerFromTable(_raiseOf(type).HungerTimes);

            if (Hour <= p.NextHungerHour) p.FoodLevel = (p.NextHungerHour - Hour) * 60 - Minute;
            else p.FoodLevel = (24 - Hour) * 60 + p.NextHungerHour * 60 - Minute;
        }

        void NextHungerFromTable(int[] times)
        {
            var p = P;
            for (int i = 0; i < 8; i++)
            {
                if (Hour <= p.NextHungerHour)
                {
                    if (p.NextHungerHour < times[i]) { p.NextHungerHour = times[i]; return; }
                }
                else if (Hour < times[i]) { p.NextHungerHour = times[i]; return; }

                if (i == 7 || times[i + 1] == -1)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        if (Hour < times[j]) { p.NextHungerHour = times[j]; return; }
                        if (j == 7 || times[j + 1] == -1) { p.NextHungerHour = times[0]; return; }
                    }
                    return;
                }
            }
        }

        /// <summary>One game frame while game time runs: tickGameClock, then the parts of tickConditions built so far.</summary>
        public void Tick()
        {
            var p = P;
            Frame++;
            if (Frame % 20 == 0)
            {
                Minute++;
                if (Minute == 60)
                {
                    Hour++; Minute = 0;
                    p.EvoTimer++; p.Lifetime--;
                    if (Hour == 24)
                    {
                        _s.Age++; Hour = 0; Frame = 0;
                        if (OnNewDay != null) OnNewDay();
                        if (p.Lifetime < 0) p.Lifetime = 0;
                    }
                }
            }

            Boundaries();
            p.AreaEffectTimer++;
            if (p.AreaEffectTimer > 28800) p.AreaEffectTimer = 0;
            p.TrainBoostTimer--;
            if (p.TrainBoostTimer <= 0) { p.TrainBoostFlag = 0; p.TrainBoostTimer = 0; }
            if (Frame % 4800 == 0 && _s.Happiness < 80)   // every 4 hours an unhappy Digimon loses extra life
            {
                p.Lifetime -= (_s.Happiness - 80) / -50 + 1;
                if (p.Lifetime < 0) p.Lifetime = 0;
            }
        }

        // tickConditionBoundaries
        public void Boundaries()
        {
            var p = P;
            _s.Happiness = Clamp(_s.Happiness, -100, 100);
            _s.Discipline = Clamp(_s.Discipline, 0, 100);
            p.Tiredness = Clamp(p.Tiredness, 0, 100);
            p.Energy = Clamp(p.Energy, 0, Raise.EnergyCap);
            _s.Weight = Clamp(_s.Weight, 1, 99);
        }

        static int Clamp(int v, int low, int high) { return v < low ? low : v > high ? high : v; }

        // scriptUpdateEnergyBoundaries: after a script changes a partner value
        void ScriptEnergyBoundaries()
        {
            var p = P;
            if (p.Energy > Raise.EnergyCap) p.Energy = Raise.EnergyCap;
            if ((p.Condition & Hungry) != 0 && Raise.EnergyThreshold <= p.Energy)
            {
                p.Condition &= ~Hungry;
                SetFoodTimer(PartnerType);
                p.StarvationTimer = 0;
            }
        }

        /// <summary>skipHours (US version), then the clock moves on: the game's scripts use this when time jumps. Also the dev keys' way of passing hours.</summary>
        public void SkipHours(int hours)
        {
            var p = P;
            p.EvoTimer += hours;
            p.Lifetime -= hours;
            if (Hour + hours >= 24) _s.Age += (Hour + hours) / 24;

            if (Hour + hours % 4 == 0 && _s.Happiness < 80) p.Lifetime -= (_s.Happiness - 80) / -50 + 1;
            if (p.Lifetime < 0) p.Lifetime = 0;

            if ((p.Condition & Sleepy) != 0) { p.SicknessCounter += hours; p.MissedSleepHours += hours; }
            if ((p.Condition & Hungry) != 0)
            {
                p.StarvationTimer -= hours * 120;
                if (p.StarvationTimer <= 0 && p.Energy < Raise.EnergyThreshold) p.CareMistakes++;
            }
            else p.FoodLevel -= hours * 60;
            p.Energy -= Raise.EnergyUsage;
            if ((p.Condition & Poopy) != 0) p.PoopingTimer -= 1200;
            else p.PoopLevel -= hours * 6;

            if ((p.Condition & Sick) != 0) { p.SicknessTries += hours; p.SicknessTimer += hours; }
            if ((p.Condition & Injured) != 0) p.InjuryTimer += hours;

            int days = (Hour + hours) / 24;
            SetClock(Hour + hours, Minute);
            if (OnNewDay != null) for (int i = 0; i < days; i++) OnNewDay();
            Boundaries();
        }

        /// <summary>advanceToTime: jumps to a time of day; passing midnight counts a day of age.</summary>
        public void AdvanceToTime(int hour, int minute)
        {
            if (hour < Hour || (Hour == hour && minute < Minute)) { _s.Age++; if (OnNewDay != null) OnNewDay(); }
            SetClock(hour, minute);
        }

        /// <summary>What a fight does when it is over (startBattle in tamer.c), then 20 game minutes pass (advanceBattleTime).</summary>
        public void AfterBattle(int result)
        {
            if (result == Lost) { _s.Happiness -= 30; _s.Discipline -= 20; }
            else if (result == FledBattle) { _s.Happiness -= 10; _s.Discipline -= 6; P.Tiredness += 2; PostBattleTiredness(); }
            else { _s.Happiness += 2; PostBattleTiredness(); P.Battles++; }
            Boundaries();
            AdvanceBattleTime();
        }

        // handlePostBattleTiredness. In the game the MP part divides whole numbers, so it only counts when MP is at 0.
        void PostBattleTiredness()
        {
            int maxMp = _s.Stats[5], mp = _s.Stats[7];
            P.Tiredness += 5;
            int diff = maxMp * 2 / 10;
            if (maxMp > 0 && mp < maxMp - diff) P.Tiredness += (maxMp - mp) / maxMp * 10;
            P.FoodLevel -= 15;
        }

        // advanceBattleTime: note the game does not count evolution time or age for an hour passed this way
        void AdvanceBattleTime()
        {
            var p = P;
            Minute += 20;
            Frame += 400;
            if (Minute > 59)
            {
                Hour++; Minute -= 60;
                p.Lifetime--;
                if (p.Lifetime < 0) p.Lifetime = 0;
                if ((p.Condition & Sleepy) != 0) { p.SicknessCounter++; p.MissedSleepHours++; }
                if ((p.Condition & Sick) != 0) { p.SicknessTries++; p.SicknessTimer++; }
                if ((p.Condition & Injured) != 0) p.InjuryTimer++;
                if (Hour > 23)
                {
                    Hour = 0;
                    Frame = Minute * 20;
                    if (OnNewDay != null) OnNewDay();
                }
            }

            if ((p.Condition & Hungry) != 0)
            {
                p.StarvationTimer -= 40;
                if (p.StarvationTimer < 1 && p.Energy < Raise.EnergyThreshold) p.CareMistakes++;
            }
            else p.FoodLevel -= 20;

            if ((p.Condition & Poopy) != 0) p.PoopingTimer -= 400;
            else p.PoopLevel -= 2;
        }
    }
}
