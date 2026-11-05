using System.Collections.Generic;
using DW1Remake.Formats;

namespace DW1Remake.Game
{
    /// <summary>The partner's rules, each frame and for each action: hunger, toilet, sleep, tiredness, moods, sickness, food and items, evolution, rebirth. Port of the US game, bugs included (decompilation: main/partner_impl.c, item.c, evolution.c, evl/evl.c).</summary>
    public sealed partial class PartnerCare
    {
        public bool WantWildPoop, WantLifeLost, WantEvolve, GotSick, WantAutoPilot;   // the room acts on these
        public int AreaResponse;                                       // 1 the map suits it, 2 the map does not
        public bool HasUsedEvoItem;                                    // as in the game, stays on until the program restarts
        public System.Func<int, int> EvoItemTarget;
        public System.Func<int, EvoRequirement> ReqOf;
        public System.Func<int, EvoGain> GainOf;
        public System.Func<int, DigimonStats> InfoOf;
        public System.Func<int, TechData> TechOf;
        public const int Sukamon = 39, Numemon = 11, Nanimon = 53, Vademon = 28;
        int _map;
        bool Immortal { get { return P.HasImmortalHour && P.ImmortalHour == Hour; } }
        bool Has(int bit) { return (P.Condition & bit) != 0; }
        int Level { get { return _levelOf(PartnerType); } }

        // ---------------------------------------------------------------- tickConditions
        void Conditions()
        {
            var p = P;
            DeathCheck();
            SleepRules();
            SicknessRules();
            TirednessRules();
            HungerRules();
            UnhappyRules();
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
            EvolutionRules();
            PoopRules();
        }

        void DeathCheck()
        {
            if (Immortal || P.Lifetime > 0 || WantLifeLost) return;
            P.IsNaturalDeath = true;
            P.Lifetime = 0;
            WantLifeLost = true;
        }

        void SleepRules()
        {
            var p = P;
            bool wasSleepy = Has(Sleepy);
            if (Frame % 20 == 0) p.TirednessSleepTimer += p.Tiredness * 3 / 10;
            if (!wasSleepy && p.TirednessSleepTimer >= 180 && Frame % 200 == 0)    // a tired Digimon gets sleepy earlier
            {
                p.TimeAwakeToday--;
                p.TirednessSleepTimer = 0;
                int gone = p.HoursAwakeDefault * 6 - p.TimeAwakeToday;
                int late = gone / 6;
                if (gone % 6 != 0) late++;
                p.SleepyHour = p.WakeupHour - p.HoursAsleepDefault - late;
                if (p.SleepyHour < 0) p.SleepyHour += 24;
                p.SleepyMinute = p.TimeAwakeToday % 6 * 10;
            }

            int level = Level;
            if (Has(Sleepy))
            {
                if (level == 1) { if (Frame % 200 == 0) { _s.Happiness--; _s.Discipline--; } }
                else if (level == 2) { if (Frame % 300 == 0) { _s.Happiness--; _s.Discipline--; } }
                else if (Frame % 1200 == 0) { _s.Happiness -= 2; _s.Discipline -= 4; }
                if (Frame % 1200 == 0 && Hour != p.SleepyHour) { p.SicknessCounter++; p.MissedSleepHours++; }
            }

            bool wokeAlone = (p.SleepyHour < p.WakeupHour && p.SleepyHour < Hour && p.WakeupHour <= Hour)
                          || (p.WakeupHour < p.SleepyHour && Hour < p.SleepyHour && p.WakeupHour <= Hour);
            if (Has(Sleepy) && wokeAlone)      // it stayed awake all the way to its waking time: a care mistake
            {
                NextSleepTimes();
                p.TimeAwakeToday = p.HoursAwakeDefault * 6;
                p.TirednessSleepTimer = 0;
                p.Condition &= ~Sleepy;
                p.CareMistakes++;
                AddTamerLevel(1, -1);
            }

            if (!wasSleepy)
            {
                if (p.WakeupHour < p.SleepyHour) { if (Hour >= p.SleepyHour || Hour < p.WakeupHour) p.Condition |= Sleepy; }
                else if (p.WakeupHour > p.SleepyHour) { if (Hour >= p.SleepyHour && Hour < p.WakeupHour) p.Condition |= Sleepy; }
            }
        }

        void NextSleepTimes()
        {
            var p = P;
            if (Level < 3)
            {
                p.SleepyHour = (p.WakeupHour + p.HoursAwakeDefault) % 24; p.SleepyMinute = 0;
                p.WakeupHour = (p.SleepyHour + p.HoursAsleepDefault) % 24; p.WakeupMinute = 0;
            }
            else
            {
                var pattern = _sleepOf(Raise.SleepCycle);
                p.SleepyHour = pattern.SleepyHour; p.SleepyMinute = pattern.SleepyMinute;
                p.WakeupHour = pattern.WakeupHour; p.WakeupMinute = pattern.WakeupMinute;
            }
        }

        void TirednessRules()
        {
            var p = P;
            if (p.AreaEffectTimer % 1200 == 0 && p.AreaEffectTimer > 0)
            {
                if (AreaResponse == 1) { _s.Happiness += 1; p.Tiredness -= 2; }
                if (AreaResponse == 2) { _s.Happiness -= 1; p.Tiredness += 1; }
            }
            if (p.SubTiredness >= 60) { p.Tiredness = System.Math.Min(100, p.Tiredness + 1); p.SubTiredness = 0; }
            if (p.Tiredness >= 50) p.TirednessHungerTimer++; else p.TirednessHungerTimer = 0;
            if (p.Tiredness >= 80) { p.Condition |= Tired; if (Frame % 100 == 0) _s.Happiness -= 2; }
            else p.Condition &= ~Tired;
        }

        void HungerRules()
        {
            var p = P; var r = Raise;
            if (Has(Hungry)) { if (Frame % 10 == 0) p.StarvationTimer--; }
            else if (Frame % 20 == 0) p.FoodLevel--;
            if (Frame % 1200 == 0) { p.Energy -= r.EnergyUsage; if (p.Energy < 0) p.Energy = 0; }
            if (!Has(Hungry) && p.FoodLevel <= 0) { p.StarvationTimer = 180; p.Condition |= Hungry; }
            if (Has(Hungry) && p.StarvationTimer <= 0)     // nobody fed it in time
            {
                SetFoodTimer(PartnerType);
                p.StarvationTimer = 0;
                p.Condition &= ~Hungry;
                if (p.Energy < r.EnergyThreshold) p.CareMistakes++;
            }
            if (p.Energy <= 0)
            {
                if (Frame % 200 == 0) { _s.Weight--; if (_s.Weight <= 0) _s.Weight = 1; }
                if (Frame % 20 == 0) p.EmptyStomachTimer++;
            }
            if (p.TirednessHungerTimer >= 50 && Frame % 1200 == 0) p.FoodLevel -= 3;
            if (r.EnergyThreshold <= p.Energy) _s.SetFlag(0x280, true);
        }

        void UnhappyRules()
        {
            var p = P;
            if (p.Condition == 0 && _s.Happiness < 0 && Frame % 200 == 0)
                if (RandomLimit(100) < -_s.Happiness - _s.Discipline) p.Condition |= Unhappy;
            if (Has(Unhappy))
            {
                if (Frame % 15 == 0) _s.Happiness++;
                if (Frame % 300 == 0)
                {
                    int[] max = { 4, 5, 0, 1, 2, 3 };
                    foreach (int i in max) { int v = _s.Stats[i]; v -= v * 2 / 100; _s.Stats[i] = (ushort)(v <= 0 ? 1 : v); }
                    if (_s.Stats[6] > _s.Stats[4]) _s.Stats[6] = _s.Stats[4];
                    if (_s.Stats[7] > _s.Stats[5]) _s.Stats[7] = _s.Stats[5];
                }
            }
            if (Has(Unhappy) && _s.Happiness >= 0) p.Condition &= ~Unhappy;
        }

        void SicknessRules()
        {
            var p = P;
            bool wasSick = Has(Sick);
            if (p.SicknessCounter >= 10 && Frame % 1200 == 0 && !wasSick)
            {
                p.SicknessTries++;
                int chance = (byte)((p.SicknessTries - 10) * 5 + (p.Tiredness - 50));   // the game keeps this in one unsigned byte
                if (RandomLimit(100) < chance)
                {
                    p.Condition |= Sick; p.TimesBeingSick++; p.SicknessTimer = 1;
                    _s.Happiness -= 20; p.SicknessCounter = 0;
                }
            }
            if (!wasSick && p.AreaEffectTimer > 12000 && Frame % 1200 == 0 && AreaResponse == 2)
            {
                p.Condition |= Sick; p.TimesBeingSick++; p.SicknessTimer = 1; p.AreaEffectTimer = 0;
            }
            if (Frame % 1200 == 0 && Has(Sick)) { _s.Happiness -= 10; _s.Discipline -= 5; p.Tiredness += 5; }
            if (!wasSick && p.SicknessTimer > 0) p.SicknessTimer = 0;
            if (Frame % 1200 == 0)
            {
                if (Has(Sick)) p.SicknessTimer++;
                if (Has(Injured)) p.InjuryTimer++;
            }
            if (Immortal) return;
            if (p.InjuryTimer >= 12 && !wasSick)
            {
                p.Condition |= Sick; p.TimesBeingSick++; p.SicknessTimer = 1;
                p.Condition &= ~Injured; p.InjuryTimer = 0;
            }
            if (!wasSick && Has(Sick)) GotSick = true;
            if (p.SicknessTimer >= 12 && p.Lifetime != 0 && !WantLifeLost)     // twelve hours sick: a life is gone
            {
                _s.Life--;
                p.SicknessTimer = 0;
                WantLifeLost = true;
            }
        }

        void PoopRules()
        {
            var p = P;
            if (!Has(Poopy) && Frame % 200 == 0) p.PoopLevel--;
            if (!Has(Poopy) && p.PoopLevel <= 0) { p.Condition |= Poopy; p.PoopingTimer = (_s.Discipline + 20) * 1200 / 100; return; }
            if (Has(Poopy))
            {
                p.PoopingTimer--;
                if (p.PoopingTimer <= 0) { WantWildPoop = true; p.PoopingTimer = -1; p.ItemScoldFlag = 1; }
            }
        }

        // ---------------------------------------------------------------- toilet and poop
        public void PoopWeightLoss() { _s.Weight -= (RandomLimit(4) + Raise.PoopSize) / 4; if (_s.Weight < 1) _s.Weight = 1; }

        public void HandleToilet()
        {
            _s.Happiness += 2; _s.Discipline += 2;
            P.PoopLevel = Raise.PoopTimer + Raise.PoopTimer;
            P.Condition &= ~Poopy;
            PoopWeightLoss();
        }

        public void HandleWildPoop()
        {
            P.Condition &= ~Poopy;
            P.CareMistakes++;
            _s.Happiness -= 10; _s.Discipline -= 5;
            P.PoopLevel = Raise.PoopTimer + Raise.PoopTimer;
            PoopWeightLoss();
        }

        /// <summary>createPoopPile: a pile on the tile behind or ahead by the partner's facing (game angle, 4096 = full turn). A map holds 16, then the oldest goes.</summary>
        public void CreatePile(int map, int tileX, int tileY, int rotation)
        {
            var p = P;
            if (rotation <= 0x300 || rotation >= 0xD00) tileY--;
            if (rotation >= 0x100 && rotation < 0x700) tileX++;
            if (rotation >= 0x500 && rotation <= 0xB00) tileY++;
            if (rotation >= 0x900 && rotation <= 0xF00) tileX--;
            for (bool taken = true; taken; )
            {
                taken = false;
                foreach (var q in p.Piles) if (q.Size != 0 && q.X == tileX && q.Y == tileY && q.Map == map) taken = true;
                if (!taken) break;
                int d = RandomLimit(4);
                if (d == 0) tileX--; else if (d == 1) tileX++; else if (d == 2) tileY--; else tileY++;
            }
            int count = 0, free = -1, first = -1;
            for (int i = 0; i < p.Piles.Length; i++)
            {
                if (p.Piles[i].Size == 0) { if (free < 0) free = i; continue; }
                if (p.Piles[i].Map == map) { count++; if (first < 0) first = i; }
            }
            int slot = count >= 16 ? first : free >= 0 ? free : p.CurrentPoop;
            if (free < 0 || count >= 16) p.CurrentPoop = (p.CurrentPoop + 1) % 100;
            p.Piles[slot] = new PoopPile { Map = map, X = tileX, Y = tileY, Size = Raise.PoopSize };
            if (PartnerType != Sukamon) p.Virus++;
        }

        public void EatPile(int i)
        {
            var q = P.Piles[i];
            int chance;
            if (q.Size < 11) { HealBy(_s.Stats[4] / 20, _s.Stats[5] / 50); _s.Weight += 1; chance = 2; }
            else if (q.Size < 14) { HealBy(_s.Stats[4] / 10, _s.Stats[5] / 20); _s.Weight += 3; chance = 7; }
            else { HealBy(_s.Stats[4] / 2, _s.Stats[5] / 10); _s.Weight += 10; chance = 20; }
            if (_s.Weight > 99) _s.Weight = 99;
            if (RandomLimit(100) < chance)
            {
                if (Has(Injured)) { P.Condition &= ~Injured; P.InjuryTimer = 0; }
                if (Has(Sick)) { P.Condition &= ~Sick; P.SicknessTimer = 0; P.AreaEffectTimer = 0; }
            }
            P.Piles[i] = new PoopPile();
        }

        void HealBy(int hp, int mp)
        {
            _s.Stats[6] = (ushort)System.Math.Min(_s.Stats[4], _s.Stats[6] + hp);
            _s.Stats[7] = (ushort)System.Math.Min(_s.Stats[5], _s.Stats[7] + mp);
        }

        // ---------------------------------------------------------------- sleep
        int HoursUntilWake { get { return Hour < P.WakeupHour ? P.WakeupHour - Hour : P.WakeupHour + 24 - Hour; } }

        /// <summary>sleepRegen, then handleSleeping: the rest itself. True when the partner needs a poop pile (the room makes it).</summary>
        public bool Sleep(bool restPillow)
        {
            var p = P;
            int slept = HoursUntilWake;
            int hours = slept;
            if (Minute > 0 && Hour != p.SleepyHour) hours++;
            int factor = hours * 100 / p.HoursAsleepDefault;
            if (restPillow) factor = factor * 12 / 10;
            if (AreaResponse == 1) factor = factor * 12 / 10;
            else if (AreaResponse == 2) factor = factor * 8 / 10;
            int roll = RandomLimit(10);
            int maxHp = _s.Stats[4], maxMp = _s.Stats[5];
            HealBy(factor * (maxHp * (roll + 70) / 100) / 100, factor * (maxMp * (roll + 70) / 100) / 100);
            roll = RandomLimit(20);
            p.Tiredness -= factor * (p.Tiredness * (roll + 80) / 100) / 100;
            if (p.Tiredness < 0) p.Tiredness = 0;
            _s.Weight -= Raise.DefaultWeight / 10;
            if (_s.Weight <= 0) _s.Weight = 1;

            AdvanceToTime(p.WakeupHour, p.WakeupMinute);
            p.EvoTimer += p.HoursAsleepDefault;
            p.Lifetime -= slept;
            if (p.Lifetime < 0) p.Lifetime = 0;
            NextSleepTimes();
            SetFoodTimer(PartnerType);
            p.StarvationTimer = 0;
            p.Condition &= ~Hungry;
            p.TimeAwakeToday = p.HoursAwakeDefault * 6;
            p.TirednessSleepTimer = 0;
            p.SicknessCounter = 0;
            p.Condition &= ~Sleepy;
            p.HasImmortalHour = true; p.ImmortalHour = Hour;

            bool pile = Has(Poopy);
            if (pile) { p.Condition &= ~Poopy; p.PoopLevel = Raise.PoopTimer; p.PoopingTimer = -1; }
            if (Has(Sick)) { p.SicknessTimer += slept; _s.Happiness -= 20; _s.Discipline -= 10; p.Tiredness += 10; }
            if (Has(Injured)) { p.InjuryTimer += slept; _s.Happiness -= 10; _s.Discipline -= 5; p.Tiredness += 5; }
            Boundaries();
            return pile;
        }

        // ---------------------------------------------------------------- Praise, Scold
        public void Praise() { int disc = _s.Discipline; _s.Discipline += -5; _s.Happiness += disc / 10 + 2; Boundaries(); }

        public void Scold()
        {
            var p = P;
            int oldDisc = _s.Discipline, oldHappy = _s.Happiness;
            if (p.ItemScoldFlag == 1) { _s.Discipline += 8; p.ItemScoldFlag = 2; }     // it had just misbehaved
            else { _s.Discipline += 2; _s.Happiness -= 10; }
            p.RefusedFavFood = 0;
            p.Condition &= ~Unhappy;
            p.NanimonTrigger = Level == 3 && oldDisc == 0 && oldHappy == -100;
            Boundaries();
        }

        // ---------------------------------------------------------------- items
        /// <summary>partnerWillRefuseItem (US version).</summary>
        public bool WillRefuse(int item)
        {
            var p = P;
            p.RefusedFavFood = 0;
            int chance = RandomLimit(10);
            if (item >= 0x73 && item != 0x79 && item != 0x7A && item < 0x7D) return true;
            if (item >= 0x21 && item < 0x26) return true;
            if ((item >= 0x47 && item < 0x73) || (item >= 0x7D && item < 0x80))
            {
                int level = Level;
                if (item == 0x7E) { if (level != 3) return true; }
                else if (item == 0x7D || item == 0x7F) { if (level != 4) return true; }
                else
                {
                    int target = _levelOf(EvoItemTarget(item));
                    if (level == target || level + 2 == target || level - 1 == target) return true;
                }
            }
            if (p.ItemScoldFlag == 2) { p.ItemScoldFlag = 0; return false; }
            if (item >= 0 && item < 0x26 && RandomLimit(100) < 110 - (_s.Discipline + RandomLimit(10) + 10)) { p.ItemScoldFlag = 1; return true; }
            if (!Has(Hungry) && item == Raise.FavoriteFood && chance < 2) p.RefusedFavFood = 2;
            if (p.RefusedFavFood != 0) { if (Has(Hungry)) p.ItemScoldFlag = 1; return true; }
            return false;
        }

        /// <summary>HEALING_VALUES (exe 0x8013435C, four shorts), read by the room from the player's disc.</summary>
        public static int[] HealingValues = new int[4];

        /// <summary>ITEM_FUNCTIONS[item], then partnerHandleFoodFeed. True when the partner cheers (animation 11).</summary>
        public bool UseItem(int item)
        {
            ItemFunction(item);
            return FoodFeed(item);
        }

        /// <summary>ITEM_FUNCTIONS[item] alone: what a thrown item does when it reaches the partner in a fight (tickThrownItem).</summary>
        public void ItemFunction(int item)
        {
            if (item <= 3) { if (_s.Stats[6] != 0) HealBy(HealingValues[item], 0); }                       // handleHPHealingItem
            else if (item <= 6) { if (_s.Stats[6] != 0) HealBy(0, HealingValues[item - 4]); }             // handleMPHealingItem
            else if (item == 7 || item == 9) { if (_s.Stats[6] != 0) HealBy(1500, 1500); }
            else if (item == 11) HealBy(_s.Stats[4] / 2, 0);
            else if (item == 12) HealBy(9999, 0);
            else if (item == 13) { if (_s.Stats[6] != 0) MedicineHealing(3, 2); }
            else if (item == 14) { if (_s.Stats[6] != 0) MedicineHealing(3, 10); }
            else if (item >= 0x16 && item <= 0x20) Chip(item);
            else if ((item >= 0x26 && item <= 0x46) || item == 0x79 || item == 0x7A) Food(item);
            else if ((item >= 0x47 && item < 0x73) || item >= 0x7D) EvoItem(item);
        }

        bool MedicineHealing(int injuryChance, int sicknessChance)
        {
            if (Has(Injured) && RandomLimit(3) < injuryChance) { P.Condition &= ~Injured; P.InjuryTimer = 0; }
            if (Has(Sick) && RandomLimit(10) < sicknessChance) { P.Condition &= ~Sick; P.SicknessTimer = 0; P.AreaEffectTimer = 0; return true; }
            return false;
        }

        void AddBase(int stat, int amount, int limit) { _s.Stats[stat] = (ushort)System.Math.Min(limit, _s.Stats[stat] + amount); }

        void Chip(int id)
        {
            int off = 0, def = 0, speed = 0, brain = 0, hp = 0, mp = 0, life = 0;
            switch (id)
            {
                case 0x17: off = 50; break;
                case 0x18: def = 50; break;
                case 0x19: brain = 50; break;
                case 0x1A: speed = 50; break;
                case 0x1B: hp = 500; break;
                case 0x1C: mp = 500; break;
                case 0x1D: off = 100; brain = 100; life = -24; break;
                case 0x1E: def = 100; speed = 100; life = -24; break;
                case 0x1F: hp = 1000; mp = 1000; life = -24; break;
                case 0x20:
                    if (Has(Poopy)) { P.PoopLevel = Raise.PoopTimer; P.Condition &= ~Poopy; PoopWeightLoss(); }
                    return;
                case 0x16: WantAutoPilot = true; return;      // Auto Pilot: a script of its own, section 0x4DD
                default: return;
            }
            AddBase(4, hp, 9999); AddBase(5, mp, 9999); AddBase(0, off, 999); AddBase(1, def, 999); AddBase(2, speed, 999); AddBase(3, brain, 999);
            P.Lifetime += life; if (P.Lifetime < 0) P.Lifetime = 0;
            if (id > 0x1C && id < 0x20) AddTamerLevel(10, -1);
        }

        // energy, tiredness relief, happiness, discipline, weight, lifetime, max HP, max MP, heal HP, heal MP, off, def, speed, brains, train flag, train value, sickness chance; row = item - 0x26
        static readonly short[,] FoodTable =
        {
            {12,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0}, {24,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,0}, {35,5,3,0,3,0,0,0,0,0,0,0,0,0,0,0,0},
            {10,0,0,0,-2,0,0,0,0,0,0,0,0,0,0x29,12,0}, {15,0,0,0,3,0,0,0,0,0,0,0,0,0,0x16,12,0}, {9,50,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0},
            {12,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0}, {19,0,0,50,2,0,0,0,0,0,0,0,0,0,0,0,0}, {38,0,0,0,4,0,100,100,0,0,10,10,10,10,0,0,0},
            {22,0,0,0,2,0,0,0,0,0,0,0,0,0,0x3F,15,0}, {30,0,50,0,3,0,0,0,0,0,0,0,0,0,0,0,0}, {25,20,20,20,2,0,0,0,0,0,0,0,0,0,0,0,0},
            {40,0,0,0,4,0,0,0,0,0,0,0,0,0,0,0,0}, {100,0,0,0,10,0,0,0,0,0,0,0,0,0,0,0,0}, {20,0,0,0,2,0,0,0,9999,0,0,0,0,0,0,0,0},
            {16,0,0,0,2,0,0,0,0,9999,0,0,0,0,0,0,0}, {33,0,0,0,-5,0,0,0,0,0,0,0,0,0,0,0,0}, {24,0,0,0,2,0,0,0,1000,1000,0,0,0,0,0,0,0},
            {20,0,0,0,2,0,0,0,0,0,20,0,0,0,0,0,0}, {20,0,0,0,2,0,0,0,0,0,0,20,0,0,0,0,0}, {20,0,0,0,2,0,0,0,0,0,0,0,20,0,0,0,0},
            {20,0,0,0,2,0,0,0,0,0,0,0,0,20,0,0,0}, {20,0,0,0,2,0,200,0,0,0,0,0,0,0,0,0,0}, {20,0,0,0,2,0,0,200,0,0,0,0,0,0,0,0,0},
            {8,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0}, {12,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0}, {22,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,0},
            {27,0,0,0,-2,0,10,10,0,0,1,1,1,1,0,0,0}, {49,0,0,0,5,0,0,0,0,0,0,0,0,0,0,0,0}, {35,0,0,0,4,3,0,0,9999,9999,0,0,0,0,0,0,20},
            {30,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,100}, {15,30,30,0,1,0,0,0,0,0,0,0,0,0,0,0,30}, {50,50,50,0,3,20,0,0,0,0,0,0,0,0,0,0,5},
        };

        // handleFood
        void Food(int id)
        {
            var p = P;
            int energy = 0, tired = 0, happy = 0, disc = 0, weight = 0, life = 0, addHp = 0, addMp = 0, heal = 0, mana = 0, off = 0, def = 0, speed = 0, brain = 0, flag = 0, value = 0, sick = 0, boost = 0;
            if (id == 0x79) mana = 1000;
            else if (id == 0x7A) { energy = 32; heal = 1000; sick = 20; }
            else
            {
                int r = id - 0x26;
                energy = FoodTable[r, 0]; tired = FoodTable[r, 1]; happy = FoodTable[r, 2]; disc = FoodTable[r, 3]; weight = FoodTable[r, 4]; life = FoodTable[r, 5];
                addHp = FoodTable[r, 6]; addMp = FoodTable[r, 7]; heal = FoodTable[r, 8]; mana = FoodTable[r, 9];
                off = FoodTable[r, 10]; def = FoodTable[r, 11]; speed = FoodTable[r, 12]; brain = FoodTable[r, 13]; flag = FoodTable[r, 14]; value = FoodTable[r, 15]; sick = FoodTable[r, 16];
                if (flag != 0) boost = 6 * 1200;
            }
            if (id == Raise.FavoriteFood) { energy = energy * 14 / 10; happy += 2; }
            p.Energy = System.Math.Min(p.Energy + energy, Raise.EnergyCap);
            p.Tiredness = System.Math.Max(0, p.Tiredness - tired);
            _s.Happiness = System.Math.Min(100, _s.Happiness + happy);
            _s.Discipline = System.Math.Min(100, _s.Discipline + disc);
            _s.Weight += weight; if (_s.Weight >= 100) _s.Weight = 99; if (_s.Weight <= 0) _s.Weight = 1;
            p.PoopLevel--;
            p.TrainBoostFlag = flag; p.TrainBoostValue = value; p.TrainBoostTimer = boost;
            ItemSickness(sick);
            p.Lifetime += life;
            AddBase(4, addHp, 9999); AddBase(5, addMp, 9999);
            HealBy(heal, mana);
            AddBase(0, off, 999); AddBase(1, def, 999); AddBase(2, speed, 999); AddBase(3, brain, 999);
        }

        void ItemSickness(int chance)
        {
            var p = P;
            if (RandomLimit(100) < chance && !Has(Sick))
            {
                p.Condition |= Sick; p.TimesBeingSick++; p.SicknessTimer = 1;
                if (Has(Injured)) { p.Condition &= ~Injured; p.InjuryTimer = 0; }
                GotSick = true;
            }
        }

        bool FoodFeed(int item)
        {
            var p = P; var r = Raise;
            bool food = (item >= 0x26 && item < 0x47) || item == 0x79 || item == 0x7A;
            if (Has(Hungry))
            {
                if (!food) return false;
                if (r.EnergyThreshold <= p.Energy)
                {
                    _s.Happiness += 5; _s.Discipline += 1;
                    p.Condition &= ~Hungry;
                    SetFoodTimer(PartnerType);
                    p.StarvationTimer = 0;
                }
                return item == r.FavoriteFood || r.EnergyThreshold <= p.Energy;
            }
            if (food) { _s.Happiness -= 3; _s.Discipline -= 2; }
            return false;
        }

        void EvoItem(int item)
        {
            if (item >= 0x7D) P.EvolutionTarget = item == 0x7D ? 0x40 : item == 0x7E ? 0x3F : 0x41;
            else
            {
                int target = EvoItemTarget(item);
                if (_levelOf(target) - 1 != Level) return;
                P.EvolutionTarget = target;
            }
            HasUsedEvoItem = true;
            WantEvolve = true;
        }

        // ---------------------------------------------------------------- injury, tamer level
        void BattleInjury()
        {
            int ratio = _s.Stats[4] > 0 ? 100 * _s.Stats[6] / _s.Stats[4] : 0;
            if (RandomLimit(100) < P.Tiredness - ratio) P.Condition |= Injured;
        }

        public void AddTamerLevel(int chance, int amount)
        {
            if (RandomLimit(100) < chance) _s.TamerLevel = Clamp(_s.TamerLevel + amount, 0, 10);
        }

        // ---------------------------------------------------------------- evolution
        void EvolutionRules()
        {
            var p = P;
            if (p.HasImmortalHour && p.ImmortalHour == Hour) return;
            if (p.HasImmortalHour) { p.HasImmortalHour = false; p.ImmortalHour = -1; }
            int level = Level, timer = p.EvoTimer;
            if (level == 1 && timer >= 6) { p.EvolutionTarget = FreshTarget(PartnerType); p.EvoTimer = 0; }
            else if (level == 2 && timer >= 24) p.EvolutionTarget = InTrainingTarget(PartnerType);
            else if (level == 3 && timer >= 72) p.EvolutionTarget = HigherTarget(PartnerType, true);
            else if (level == 4 && timer >= 144) p.EvolutionTarget = HigherTarget(PartnerType, false);
            if (level == 4 && timer == 360) p.EvolutionTarget = SpecialEvolution(3);
            if (p.Virus >= 16 && PartnerType != Sukamon)
            {
                p.EvolutionTarget = Sukamon;
                _s.Vars[5] = (byte)PartnerType;
                p.SukaHp = _s.Stats[4]; p.SukaMp = _s.Stats[5]; p.SukaOff = _s.Stats[0]; p.SukaDef = _s.Stats[1]; p.SukaSpeed = _s.Stats[2]; p.SukaBrain = _s.Stats[3];
                p.Virus = 0;
            }
            if (p.EvolutionTarget != -1) WantEvolve = true;
        }

        static int FreshTarget(int type) { return type == 1 ? 2 : type == 15 ? 16 : type == 29 ? 30 : type == 43 ? 44 : -1; }

        IEnumerable<int> Targets(int type)
        {
            foreach (int t in InfoOf(type).EvolvesTo) if (t > 0 && t < 63) yield return t;
        }

        int InTrainingTarget(int type)
        {
            int best = -1, bestScore = 0;
            foreach (int t in Targets(type))
            {
                int score = RequirementScore(type, t, best);
                if (bestScore < score && score >= 3) { best = t; bestScore = score; }
            }
            return best;
        }

        // getRookieEvolutionTarget and getChampionEvolutionTarget. As in the game, the sums carry over to the next candidate unless one became the best.
        int HigherTarget(int type, bool rookie)
        {
            int best = -1, bestScore = 0, total = 0, count = 0;
            foreach (int t in Targets(type))
            {
                if (RequirementScore(type, t, best) < 3) continue;
                var r = ReqOf(t);
                if (r.Hp != -1) { total += _s.Stats[4] / 10; count++; }
                if (r.Mp != -1) { total += _s.Stats[5] / 10; count++; }
                if (r.Off != -1) { total += _s.Stats[0]; count++; }
                if (r.Def != -1) { total += _s.Stats[1]; count++; }
                if (r.Speed != -1) { total += _s.Stats[2]; count++; }
                if (r.Brain != -1) { total += _s.Stats[3]; count++; }
                if (count != 0) total /= count;
                if (bestScore < total) { best = t; bestScore = total; total = 0; count = 0; }
            }
            if (rookie && best == -1 && P.EvoTimer >= 96) return Numemon;
            return best;
        }

        int RequirementScore(int current, int target, int currentBest)
        {
            var r = ReqOf(target); var p = P;
            bool maxCare = (r.Flags & 0x10) != 0, maxBattles = (r.Flags & 1) != 0;
            int points = 0;
            if (maxCare ? p.CareMistakes <= r.Care : p.CareMistakes >= r.Care) points++;
            if (r.Weight - 5 <= _s.Weight && _s.Weight <= r.Weight + 5) points++;
            int[] stats = { _s.Stats[4] / 10, _s.Stats[5] / 10, _s.Stats[0], _s.Stats[1], _s.Stats[2], _s.Stats[3] };
            int[] req = { r.Hp, r.Mp, r.Off, r.Def, r.Speed, r.Brain };
            if (_levelOf(target) == 3)
            {
                int highest = 0;
                for (int i = 0; i < 6; i++)
                {
                    bool top = true;
                    for (int j = 0; j < 6; j++) if (stats[i] < stats[j]) top = false;
                    if (top) highest = i;
                }
                if (req[highest] == 1) points++;
            }
            else
            {
                bool all = true;
                for (int i = 0; i < 6; i++) if (stats[i] < req[i]) all = false;
                if (all) points++;
            }
            bool bonus = false;
            if (r.Digimon != -1 && current == r.Digimon) bonus = true;
            if (r.Discipline != -1 && _s.Discipline >= r.Discipline) bonus = true;
            if (r.Happiness != -1 && _s.Happiness >= r.Happiness) bonus = true;
            if (r.Battles != -1 && (maxBattles ? p.Battles <= r.Battles : p.Battles >= r.Battles)) bonus = true;
            if (r.Techs != -1 && MasteredMoves() >= r.Techs) bonus = true;
            if (bonus) points++;
            if (points >= 3 && currentBest != -1)
            {
                bool raised = _s.Raised[GainOf(target).Target], bestRaised = _s.Raised[GainOf(currentBest).Target];
                if (raised && !bestRaised) points = 0;
                if (!raised && bestRaised) points++;
            }
            return points;
        }

        int MasteredMoves() { int n = 0; for (int i = 0; i < 64; i++) if (_s.Techs[i]) n++; return n; }

        /// <summary>handleSpecialEvolutions: mode 2 after sleeping, 3 after Praise or Scold. Returns the target or -1.</summary>
        public int SpecialEvolution(int mode)
        {
            var p = P;
            int type = PartnerType, level = Level, rand = RandomLimit(100), target = -1;
            if (mode == 2)
            {
                if (rand < 30)
                {
                    if ((type == 0x15 || type == 0x0A) && _s.Discipline == 100 && _s.Happiness == 100 && p.Tiredness == 0) target = 7;
                    if (type == 0x19 && p.Battles >= 50 && _s.Discipline == 100) target = 58;
                    if (type == 0x26 && _s.Stats[1] >= 500 && _s.Discipline == 100) target = 47;
                }
                if (rand < 50 && (type == 2 || type == 0x1E || type == 0x10 || type == 0x2C) && _map == 1) target = 32;
            }
            else if (mode == 3)
            {
                if ((type == 0x18 || type == 0x23) && level == 4 && p.EvoTimer == 200 && rand < 30) target = 49;
                if (p.NanimonTrigger) { target = Nanimon; p.NanimonTrigger = false; }
                if (level == 4 && p.EvoTimer >= 240 && rand < 50) target = Vademon;
            }
            if (target != -1) { p.EvolutionTarget = target; WantEvolve = true; }
            return target;
        }

        public void OnMap(int mapId, int response) { _map = mapId; AreaResponse = response; P.AreaEffectTimer = 0; }

        /// <summary>EVL_applyEvolution up to the model change, and what initializeEvolvedPartner does after it. The room swaps the model.</summary>
        public void ApplyEvolution(int target)
        {
            var p = P; var gain = GainOf(target);
            int newType = gain.Target, oldLevel = Level, level = _levelOf(target);
            if (target == Numemon || target == Sukamon || target == Nanimon || target == 6 || PartnerType == Sukamon || level < 3) ScaleStats(gain);
            else if (!HasUsedEvoItem)
            {
                int[] goal = { gain.Hp, gain.Mp, gain.Off, gain.Def, gain.Speed, gain.Brain }, slot = { 4, 5, 0, 1, 2, 3 };
                for (int i = 0; i < 6; i++)
                {
                    int v = _s.Stats[slot[i]];
                    v = v >= goal[i] ? v + goal[i] / 10 : (goal[i] + v) / 2;
                    _s.Stats[slot[i]] = (ushort)v;
                }
                ClampStats();
                if (_levelOf(newType) == 4) AddTamerLevel(20, 1);
                if (_levelOf(newType) == 5) AddTamerLevel(100, 1);
            }
            int newLevel = _levelOf(newType);
            if (newLevel == 5 && !HasUsedEvoItem) p.Lifetime += 96;

            var info = InfoOf(newType);
            int[] bases = { 0, 40, 8, 32, 16, 24, 49 };
            int b = info.Specials[0] < 7 ? bases[info.Specials[0]] : 0, bestMove = -1;
            foreach (int m in info.RawMoves)
                if (b <= m && m <= b + 8 && (bestMove < 0 || (TechOf(bestMove).Power > TechOf(m).Power && TechOf(m).Power != 0))) bestMove = m;
            if (bestMove >= 0) _s.Techs[bestMove] = true;
            _s.TechSet.Clear();
            if (newLevel < 3) { foreach (int m in info.RawMoves) if (m != 0xFF) { _s.TechSet.Add(m); break; } }
            else if (bestMove >= 0) _s.TechSet.Add(bestMove);

            PartnerType = newType;
            _s.Raised[newType] = true;
            p.Condition = 0;
            SetSleepTimes(newType);
            ResetPara(newType);
            p.HasImmortalHour = false; p.ImmortalHour = -1;
            if (oldLevel != newLevel) p.EvoTimer = 0;
            p.EvolutionTarget = -1;
            WantEvolve = false;
        }

        void ScaleStats(EvoGain gain)
        {
            var p = P;
            if (HasUsedEvoItem) return;
            if (PartnerType != Sukamon) foreach (int i in new[] { 4, 5, 0, 1, 2, 3 }) _s.Stats[i] = (ushort)(_s.Stats[i] * gain.Brain / 10);
            else
            {
                _s.Stats[4] = (ushort)(p.SukaHp + (_s.Stats[4] - p.SukaHp / 2) / 2);
                _s.Stats[5] = (ushort)(p.SukaMp + (_s.Stats[5] - p.SukaMp / 2) / 2);
                _s.Stats[0] = (ushort)(p.SukaOff + (_s.Stats[0] - p.SukaOff / 2) / 2);
                _s.Stats[1] = (ushort)(p.SukaDef + (_s.Stats[1] - p.SukaDef / 2) / 2);
                _s.Stats[2] = (ushort)(p.SukaSpeed + (_s.Stats[2] - p.SukaSpeed / 2) / 2);
                _s.Stats[3] = (ushort)(p.SukaBrain + (_s.Stats[3] - p.SukaBrain / 2) / 2);
                p.Virus = 0;
            }
            ClampStats();
            if (_s.Stats[6] > _s.Stats[4]) _s.Stats[6] = _s.Stats[4];
            if (_s.Stats[7] > _s.Stats[5]) _s.Stats[7] = _s.Stats[5];
        }

        void ClampStats()
        {
            for (int i = 4; i < 6; i++) if (_s.Stats[i] > 9999) _s.Stats[i] = 9999;
            for (int i = 0; i < 4; i++) if (_s.Stats[i] > 999) _s.Stats[i] = 999;
        }

        // resetPartnerPara
        void ResetPara(int type)
        {
            var p = P; var r = _raiseOf(type);
            p.PoopLevel = r.PoopTimer; p.PoopingTimer = -1;
            p.Tiredness = 0; p.SubTiredness = 0; p.TirednessHungerTimer = 0; p.TimesBeingSick = 0; p.AreaEffectTimer = 0;
            p.SicknessTimer = 0; p.InjuryTimer = 0; p.SicknessTries = 0;
            SetFoodTimer(type);
            p.StarvationTimer = -1; p.EmptyStomachTimer = 0;
            _s.Weight = r.DefaultWeight;
            p.CareMistakes = 0; p.Battles = 0;
        }

        // ---------------------------------------------------------------- training (trn_reward.c)
        public int LastLearned;

        // TRN_calculateTrainingMultiplier, without the gym upgrades' flag argument: the game calls it with 0
        int TrainMultiplier(int mode, bool manual)
        {
            int tt = Raise.TrainingType, num;
            int[,] byType = { { 10, 10, 9, 11, 8 }, { 10, 9, 11, 10, 8 }, { 10, 11, 10, 9, 8 }, { 10, 10, 9, 11, 8 }, { 10, 11, 10, 9, 8 }, { 10, 9, 11, 10, 8 } };
            num = byType[mode, tt > 3 ? 4 : tt];
            int den = 10, level = Level;
            num *= level <= 2 ? 9 : level == 3 ? 10 : level == 4 ? 11 : 12;
            den *= 10;
            if ((mode == 0 || mode == 3 || mode == 4) && _s.GetFlag(0xDB)) { num *= 6; den *= 5; }
            if ((mode == 2 || mode == 1 || mode == 5) && _s.GetFlag(0xFB)) { num *= 6; den *= 5; }
            if (P.Upgrades[mode] >= 10) { num *= 6; den *= 5; }
            if (manual) { num *= 11; den *= 10; }
            return num * 10 / den;
        }

        int Boosted(int v, int m, int bit)
        {
            return (P.TrainBoostFlag & bit) != 0 ? v * (m * P.TrainBoostValue) / 100 : v * m / 10;
        }

        /// <summary>The gains of a finished session, in the order HP, MP, Off, Def, Speed, Brains, and the hour that passes. kind: 0 HP, 1 Off, 2 Speed, 3 Def, 4 MP, 5 Brains.</summary>
        /// <param name="bonus">the slot machine's payout (5, 20, 40, 100); 10 without it: every gain x bonus / 10 (TRN_award*TrainingGains, argument c)</param>
        public void Train(int kind, int[] g, bool manual, int bonus = 10)
        {
            for (int i = 0; i < 6; i++) g[i] = 0;
            int m, tired;
            if (Level < 3) P.Upgrades[kind == 1 ? 2 : kind == 2 ? 4 : kind == 4 ? 1 : kind]++;
            switch (kind)
            {
                case 0: m = TrainMultiplier(0, manual); g[0] = Boosted(80, m, 0x10); g[2] = Boosted(2, m, 1); tired = 10; break;
                case 1: m = TrainMultiplier(2, manual); g[2] = Boosted(8, m, 1); g[4] = Boosted(2, m, 4); tired = 9; break;
                case 2: m = TrainMultiplier(4, manual); g[0] = Boosted(10, m, 0x10); g[4] = Boosted(8, m, 4); tired = 8; break;
                case 3: m = TrainMultiplier(3, manual); g[3] = Boosted(8, m, 2); g[5] = Boosted(2, m, 8); tired = 9; break;
                case 4: m = TrainMultiplier(1, manual); g[1] = Boosted(80, m, 0x20); g[3] = Boosted(2, m, 2); tired = 7; break;
                default: m = TrainMultiplier(1, manual); g[1] = Boosted(10, m, 0x20); g[5] = Boosted(8, m, 8); tired = 6; break;
            }
            for (int i = 0; i < 6; i++) g[i] = g[i] * bonus / 10;
            if (P.Tiredness == 100) for (int i = 0; i < 6; i++) if (g[i] > 0) g[i] = 1;
            AdvanceTrainingTime(tired, 1, 1);
        }

        // TRN_advanceTrainingTime
        void AdvanceTrainingTime(int tired, int energy, int happy)
        {
            var p = P;
            p.Tiredness += tired; p.Energy -= energy; _s.Happiness -= happy;
            if (p.Tiredness >= 70) { _s.Happiness -= 3; _s.Discipline -= 3; }
            Hour++; Frame += 1200;
            if (Hour >= 24) { _s.Age++; Hour -= 24; Frame -= 28800; if (OnNewDay != null) OnNewDay(); }
            if (Hour % 4 == 0 && _s.Happiness < 80) p.Lifetime -= (_s.Happiness - 80) / -50 + 1;
            if (Has(Sleepy)) { p.SicknessCounter++; p.MissedSleepHours++; }
            if (Has(Hungry))
            {
                p.StarvationTimer -= 120;
                if (p.StarvationTimer <= 0 && p.Energy < Raise.EnergyThreshold) p.CareMistakes++;
            }
            else p.FoodLevel -= 60;
            p.Energy -= Raise.EnergyUsage;
            if (Has(Poopy)) p.PoopingTimer -= 1200; else p.PoopLevel -= 6;
            p.Lifetime--; p.EvoTimer++;
            if (p.Lifetime < 0) p.Lifetime = 0;
            if (Has(Injured)) p.InjuryTimer++;
            if (Has(Sick)) p.SicknessTimer++;
            Boundaries();
        }

        /// <summary>TRN_tryLearnMove: Brains passing 150, 250, 350 ... can teach a technique. Tables from TRN_REL.BIN.</summary>
        public bool LearnFromTraining(byte[] trn, int kind)
        {
            if (kind != 5 || trn == null) return false;
            int total = _s.Stats[3], before = total - 8;
            int[] marks = { 150, 250, 350, 450, 550, 650, 750, 800, 850, 900, 950, 999 };
            bool hit = false;
            foreach (int m in marks) if (before < m && total >= m) hit = true;
            if (!hit) return false;
            const int order = 26956, chance = order + 56;
            var info = InfoOf(PartnerType);
            int best = -1, move = -1;
            for (int i = 0; i < 3; i++)
            {
                int sp = info.Specials[i];
                if (sp == 0xFF) continue;
                for (int k = 0; k < 8; k++)
                {
                    int tech = trn[order + k + sp * 8];
                    if (_s.Techs[tech] || System.Array.IndexOf(info.RawMoves, tech) < 0) continue;
                    int w = (sbyte)trn[chance + k * 3 + i];
                    if (w > best) { best = w; move = tech; }
                    break;
                }
            }
            if (move < 0 || best <= RandomLimit(100)) return false;
            _s.Techs[move] = true; LastLearned = move;
            return true;
        }

        // ---------------------------------------------------------------- death and rebirth
        /// <summary>reincarnatePartner: the egg's starting numbers plus, after a natural death, Tamer Level percent of the old ones.</summary>
        public void Reincarnate(int babyType)
        {
            var p = P;
            int percent = p.IsNaturalDeath ? _s.TamerLevel : 0;
            int[] start = babyType == 1 ? new[] { 90, 110, 10, 11, 9, 10 } : babyType == 15 ? new[] { 120, 100, 16, 8, 8, 6 } : babyType == 29 ? new[] { 100, 140, 12, 6, 6, 8 } : new[] { 100, 120, 8, 8, 8, 8 };
            int[] slot = { 4, 5, 0, 1, 2, 3 };
            for (int i = 0; i < 6; i++) _s.Stats[slot[i]] = (ushort)(start[i] + _s.Stats[slot[i]] * percent / 100);
            _s.Stats[6] = _s.Stats[4]; _s.Stats[7] = _s.Stats[5];
            p.IsNaturalDeath = false;
            PartnerType = babyType;
            p.Condition = 0;
            SetSleepTimes(babyType);
            p.MissedSleepHours = 0; p.TirednessSleepTimer = 0;
            ResetPara(babyType);
            _s.Discipline = 50; _s.Happiness = 50;
            p.TimesBeingSick = 0; p.Lifetime = 360; p.EvoTimer = 0; p.EvolutionTarget = -1;
            _s.Age = 0;
            _s.Life = 3;
            _s.Raised[babyType] = true;
            p.RaisedCount = System.Math.Min(99, p.RaisedCount + 1);
            p.HasImmortalHour = false; p.ImmortalHour = -1;
            WantEvolve = false; WantLifeLost = false;
            _s.TechSet.Clear();
            foreach (int m in InfoOf(babyType).RawMoves) if (m != 0xFF) { _s.TechSet.Add(m); break; }
        }

        /// <summary>handleItemLoss: after a lost fight 30% of the kinds in the bag are gone.</summary>
        public void ItemLoss()
        {
            var kinds = new List<int>();
            for (int i = 0; i < _s.Bag.Length; i++) if (_s.Bag[i] > 0) kinds.Add(i);
            int lose = kinds.Count * 30 / 100;
            for (int n = 0; n < lose; n++) { int k = RandomLimit(kinds.Count); _s.Bag[kinds[k]] = 0; kinds.RemoveAt(k); }
        }
    }
}
