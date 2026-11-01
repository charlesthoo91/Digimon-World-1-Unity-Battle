using System;
using System.Collections.Generic;
using System.Text;
using DW1Remake.Disc;

namespace DW1Remake.Formats
{
    /// <summary>One Digimon as described by the game program (names, bones, model file).</summary>
    public sealed class DigimonEntry
    {
        public int Type;            // Digimon id used all over the game (Agumon = 3)
        public string Name;         // e.g. "Agumon"
        public string FileName;     // e.g. "AGUM"
        public int BoneCount;
        public sbyte[] NodeObject;  // per bone: which model part it shows (-1 = none)
        public sbyte[] NodeParent;  // per bone: parent bone (-1 = root)
        public string MmdPath { get { return "/CHDAT/MMD" + (Type / 30) + "/" + FileName + ".MMD"; } }
        public override string ToString() { return Type + "  " + Name + "  (" + FileName + ")"; }
    }

    /// <summary>
    /// Reads the Digimon tables out of the game program on the disc (SLUS_010.32, USA version).
    /// Addresses come from the DW1-SydPatches labels.
    /// </summary>
    public static class DigimonCatalog
    {
        const uint DigimonData = 0x8012ceb4;   // 52 bytes per Digimon
        const uint FileNames = 0x80133b44;     // 8 bytes per Digimon
        const uint Skeletons = 0x8011ce60;     // pointer per Digimon
        const int DataStride = 52;
        const int MaxTypes = 180;

        public static List<DigimonEntry> Load(DiscImage disc)
        {
            var list = new List<DigimonEntry>();
            if (!disc.Exists("/SLUS_010.32")) return list;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            uint loadAddr = BitConverter.ToUInt32(exe, 0x18);

            Func<uint, int> off = a => (int)(a - loadAddr + 0x800);
            Func<uint, bool> inside = a => a >= loadAddr && off(a) >= 0 && off(a) < exe.Length - 4;

            for (int t = 0; t < MaxTypes; t++)
            {
                int d = off(DigimonData) + t * DataStride;
                int bones = BitConverter.ToInt32(exe, d + 20);
                string name = Ascii(exe, d, 20);
                string file = Ascii(exe, off(FileNames) + t * 8, 8);
                if (bones < 2 || bones > 80 || file.Length == 0) continue;

                uint ptr = BitConverter.ToUInt32(exe, off(Skeletons) + t * 4);
                if (!inside(ptr) || off(ptr) + bones * 2 > exe.Length) continue;

                var e = new DigimonEntry
                {
                    Type = t, Name = name, FileName = file, BoneCount = bones,
                    NodeObject = new sbyte[bones], NodeParent = new sbyte[bones]
                };
                bool ok = true;
                for (int i = 0; i < bones; i++)
                {
                    e.NodeObject[i] = (sbyte)exe[off(ptr) + i * 2];
                    e.NodeParent[i] = (sbyte)exe[off(ptr) + i * 2 + 1];
                    if (e.NodeParent[i] >= i) ok = false;           // parents must come first
                    if (i == 0 && e.NodeParent[i] != -1) ok = false;
                }
                if (!ok || !disc.Exists(e.MmdPath)) continue;
                list.Add(e);
            }
            return list;
        }

        static string Ascii(byte[] b, int o, int max)
        {
            int n = 0;
            while (n < max && b[o + n] >= 32 && b[o + n] < 127) n++;
            return Encoding.ASCII.GetString(b, o, n);
        }
    }
}
