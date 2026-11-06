using System;
using System.Collections.Generic;
using DW1Remake.Formats;

namespace DW1Remake.Game
{
    /// <summary>
    /// What a save file holds: the story state (flags, variables), the partner's numbers, the bag, Bits, learned techniques, cards, names, the clock
    /// and where you stand. Only game progress, nothing from the disc. Written as JSON to Application.persistentDataPath/save1.json.
    /// </summary>
    [Serializable]
    public sealed class SaveGameData
    {
        public int Version = 3;                     // 3 = holds the care numbers (Para) with piles and evolution
        public string Map;
        public float X, Y, Z, Clock;
        public int Day, PartnerType;
        public string PlayerName, PartnerName;
        public byte[] Vars, Flags, Bag, Stored, Slots, Recycle, CardCount, CardOffers;
        public int[] Stats;
        public bool[] Techs, Cards, Raised;
        public List<int> TechSet;
        public int Money, ItemSlots, Happiness, Discipline, Age, Weight, Life, TamerLevel;
        public PartnerPara Para;
        public float PlayMinutes;
        public RegisteredDigimon[] Registry;
        public int PrevScreen = 0xCC, PrevExit, CurExit;     // PREVIOUS_SCREEN (savegame->previousScreen), PREVIOUS_EXIT, CURRENT_EXIT: where Run sends the boy
        public int MainState = 1;           // savegame->mainState: 2 after the game clear save (CONTINUE then plays movie 2)

        public static SaveGameData From(ScriptState s)
        {
            var d = new SaveGameData
            {
                Vars = (byte[])s.Vars.Clone(), Flags = (byte[])s.Flags.Clone(), Bag = (byte[])s.Bag.Clone(),
                Techs = (bool[])s.Techs.Clone(), Cards = (bool[])s.Cards.Clone(), Raised = (bool[])s.Raised.Clone(), TechSet = new List<int>(s.TechSet),
                Money = s.Money, ItemSlots = s.ItemSlots, Happiness = s.Happiness, Discipline = s.Discipline, Age = s.Age, Weight = s.Weight, Life = s.Life,
                TamerLevel = s.TamerLevel, Para = s.Para, PlayMinutes = s.PlayMinutes, PlayerName = s.PlayerName, PartnerName = s.PartnerName
            };
            d.Registry = s.Registry;
            d.Stored = (byte[])s.Stored.Clone();
            d.Recycle = (byte[])s.Recycle.Clone(); d.CardCount = (byte[])s.CardCount.Clone(); d.CardOffers = (byte[])s.CardOffers.Clone();
            d.Slots = (byte[])s.Slots.Clone();
            d.Stats = new int[s.Stats.Length];
            for (int i = 0; i < d.Stats.Length; i++) d.Stats[i] = s.Stats[i];
            return d;
        }

        public void ApplyTo(ScriptState s)
        {
            Array.Copy(Vars, s.Vars, Math.Min(Vars.Length, s.Vars.Length));
            Array.Copy(Flags, s.Flags, Math.Min(Flags.Length, s.Flags.Length));
            Array.Copy(Bag, s.Bag, Math.Min(Bag.Length, s.Bag.Length));
            if (Stored != null) Array.Copy(Stored, s.Stored, Math.Min(Stored.Length, s.Stored.Length));
            if (Recycle != null) Array.Copy(Recycle, s.Recycle, Math.Min(Recycle.Length, s.Recycle.Length));
            if (CardCount != null) Array.Copy(CardCount, s.CardCount, Math.Min(CardCount.Length, s.CardCount.Length));
            if (CardOffers != null) Array.Copy(CardOffers, s.CardOffers, Math.Min(CardOffers.Length, s.CardOffers.Length));
            for (int i = 0; i < s.Slots.Length; i++) s.Slots[i] = Slots != null && i < Slots.Length ? Slots[i] : (byte)0xFF;
            Array.Copy(Techs, s.Techs, Math.Min(Techs.Length, s.Techs.Length));
            Array.Copy(Cards, s.Cards, Math.Min(Cards.Length, s.Cards.Length));
            if (Raised != null) Array.Copy(Raised, s.Raised, Math.Min(Raised.Length, s.Raised.Length));
            for (int i = 0; i < Stats.Length && i < s.Stats.Length; i++) s.Stats[i] = (ushort)Math.Max(0, Math.Min(9999, Stats[i]));
            s.TechSet = new List<int>(TechSet ?? new List<int>());
            s.Money = Money; s.ItemSlots = ItemSlots; s.Happiness = Happiness; s.Discipline = Discipline; s.Age = Age; s.Weight = Weight; s.Life = Life;
            s.TamerLevel = TamerLevel;
            if (Version >= 3 && Para != null) s.Para = Para;
            if (Registry != null && Registry.Length == 40) s.Registry = Registry;
            s.PlayMinutes = PlayMinutes; s.PlayerName = PlayerName ?? "A"; s.PartnerName = PartnerName ?? "B";
        }
    }
}
