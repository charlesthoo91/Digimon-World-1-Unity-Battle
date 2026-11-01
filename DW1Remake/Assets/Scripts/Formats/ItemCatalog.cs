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
        public int Category;        // from the record: 0 recovery, 1 cure, 2 food, 3 training disk, 4 chip/part, 5 tool/key (guessed from the names)
        public string Description;
        public int TextColour;      // record +28: 0 cyan, 1 pink, 2 green (seen on the PS1 item page)
    }

    /// <summary>The game's 128 items, read from the program file at run time (name, price, category, description). Item number = position in the list.</summary>
    public static class ItemCatalog
    {
        const uint TableAddr = 0x801269DC;   // 128 records of 32 bytes: name[20], u16 price, 10 more bytes
        const uint DescAddr = 0x80125298;    // 128 text lines, each padded to a multiple of 4 bytes
        public const int Count = 128;

        public static List<ItemEntry> Load(DiscImage disc)
        {
            var list = new List<ItemEntry>();
            if (!disc.Exists("/SLUS_010.32")) return list;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint loadAddr = BitConverter.ToUInt32(exe, 0x18);
            int o = (int)(TableAddr - loadAddr + 0x800);
            int dp = (int)(DescAddr - loadAddr + 0x800);
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
                    Category = exe[p + 26],
                    TextColour = exe[p + 28]
                };
                int len = 0;
                while (dp + len < exe.Length && exe[dp + len] != 0) len++;
                e.Description = Encoding.ASCII.GetString(exe, dp, len);
                dp += (len + 1 + 3) & ~3;
                list.Add(e);
            }
            return list;
        }
    }
}
