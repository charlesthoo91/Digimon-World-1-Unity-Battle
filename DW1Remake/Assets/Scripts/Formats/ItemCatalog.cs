using System;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Disc;

namespace DW1Remake.Formats
{
    public sealed class ItemEntry
    {
        public int Id;
        public string Name;
        public int Price;
        public int Merit;           // record +24 (Item.meritValue): its price in merit points at ShogunGekomon (0 = not offered)
        public int Category;       // from the record: 0 recovery, 1 cure, 2 food, 3 training disk, 4 chip/part, 5 tool/key (guessed from the names)
        public string Description;
        public int TextColour;      // record +28: 0 cyan, 1 pink, 2 green (seen on the PS1 item page)
        public bool Droppable;      // record +29 (Item.droppable): it can be thrown away, sold or handed over (shopFillSellItemList)
    }

    /// <summary>The game's 128 items, read from the program file at run time (name, price, category, description). Item number = position in the list.</summary>
    public static class ItemCatalog
    {
        const uint TableAddr = 0x801269DC;   // 128 records of 32 bytes: name[20], u16 price, 10 more bytes
        const uint DescPtrs = 0x801279DC;    // 128 pointers, one per item, to its description text (the lines are not in item order)
        public const int Count = 128;

        public static List<ItemEntry> Load(DiscImage disc)
        {
            var list = new List<ItemEntry>();
            if (!disc.Exists("/SLUS_010.32")) return list;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint loadAddr = BitConverter.ToUInt32(exe, 0x18);
            int o = (int)(TableAddr - loadAddr + 0x800);
            for (int i = 0; i < Count; i++)
            {
                int p = o + 32 * i;
                if (p + 32 > exe.Length) break;
                int n = 0;
                while (n < 20 && exe[p + n] >= 32 && exe[p + n] < 127) n++;
                var e = new ItemEntry
                {
                    Id = i,
                    Name = Encoding.ASCII.GetString(exe, p, n),
                    Price = BitConverter.ToUInt16(exe, p + 20),
                    Merit = BitConverter.ToUInt16(exe, p + 24),
                    Category = exe[p + 26],
                    TextColour = exe[p + 28],
                    Droppable = exe[p + 29] != 0
                };
                int dp = (int)(BitConverter.ToUInt32(exe, (int)(DescPtrs - loadAddr + 0x800) + 4 * i) - loadAddr + 0x800), len = 0;
                while (dp >= 0 && dp + len < exe.Length && exe[dp + len] != 0) len++;
                e.Description = dp >= 0 && dp < exe.Length ? Encoding.ASCII.GetString(exe, dp, len) : "";
                list.Add(e);
            }
            // FIXED ORIGINAL BUG (user's decision 2026-11-08): the US text of DV Chip E (item 31) says "Boost Off. Pwr+Speed +1000", but the chip
            // gives +1000 max HP and +1000 max MP (item.c, chip 0x1F). Our own line, in the style of DV Chip A / D, replaces it only when the disc
            // has that wrong text
            if (list.Count > 31 && list[31].Description == "Boost Off. Pwr+Speed +1000") list[31].Description = "Boost HP+MP +1000";
            return list;
        }
    }
}
