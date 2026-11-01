using System;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Disc;

namespace DW1Remake.Formats
{
    /// <summary>List of map names in game order (map number = position in the list), read from the game program.</summary>
    public static class MapCatalog
    {
        const uint Entries = 0x801292d4;   // 16 bytes per map, name first

        public static List<string> Load(DiscImage disc)
        {
            var names = new List<string>();
            if (!disc.Exists("/SLUS_010.32")) return names;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint loadAddr = BitConverter.ToUInt32(exe, 0x18);
            int o = (int)(Entries - loadAddr + 0x800);
            for (int i = 0; i < 255; i++)
            {
                int p = o + 16 * i;
                if (p + 10 > exe.Length) break;
                int n = 0;
                while (n < 10 && exe[p + n] >= 32 && exe[p + n] < 127) n++;
                if (n == 0) break;
                names.Add(Encoding.ASCII.GetString(exe, p, n));
            }
            return names;
        }

        /// <summary>The 'flags' byte of every map. 0x40 = the map has no day/night change.</summary>
        public static List<byte> LoadFlags(DiscImage disc)
        {
            var flags = new List<byte>();
            if (!disc.Exists("/SLUS_010.32")) return flags;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint loadAddr = BitConverter.ToUInt32(exe, 0x18);
            int o = (int)(Entries - loadAddr + 0x800);
            for (int i = 0; i < 255; i++)
            {
                int p = o + 16 * i;
                if (p + 16 > exe.Length || exe[p] < 32 || exe[p] >= 127) break;
                flags.Add(exe[p + 12]);
            }
            return flags;
        }

        /// <summary>How many 8-bit and 4-bit texture pages each map has (they come first in the map file).</summary>
        public static void LoadImageCounts(DiscImage disc, out List<byte> num8, out List<byte> num4)
        {
            num8 = new List<byte>(); num4 = new List<byte>();
            if (!disc.Exists("/SLUS_010.32")) return;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint loadAddr = BitConverter.ToUInt32(exe, 0x18);
            int o = (int)(Entries - loadAddr + 0x800);
            for (int i = 0; i < 255; i++)
            {
                int p = o + 16 * i;
                if (p + 16 > exe.Length || exe[p] < 32 || exe[p] >= 127) break;
                num8.Add(exe[p + 10]); num4.Add(exe[p + 11]);
            }
        }

        public static string PathFor(IList<string> names, int mapId)
        {
            return "/MAP/MAP" + (mapId / 15 + 1) + "/" + names[mapId] + ".MAP";
        }
    }
}
