using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DW1Remake.Disc
{
    /// <summary>
    /// Checks that a disc image is the one the remake reads: the USA Digimon World disc (SLUS-01032).
    /// SYSTEM.CNF names the program file (BOOT = cdrom:\SLUS_010.32;1); its SHA-1 is compared with the versions the
    /// decompilation lists (dw_decomp config/*/: USA, Europe, Japan, Japan rev 1). Only the USA program is accepted for now,
    /// because every exe table address we read is the USA one. A modified disc fails the check too.
    /// </summary>
    public static class DiscCheck
    {
        public const string UsaSha1 = "31ec666b987d5b007dec40eecc4092d27ed9085b";

        static readonly string[,] Known =
        {
            { UsaSha1, "USA" },
            { "a805aad8bb6b2a12b145bd2172697aff7a28d25e", "Europe" },
            { "d11336b31aeceec48267062676e9c3f0833ec13f", "Japan" },
            { "82212496037146885e96573bd43aa3f0f4ccee8e", "Japan (revision 1)" },
        };

        /// <summary>Null when the disc is the USA disc; otherwise a message for the player.</summary>
        public static string Check(DiscImage disc)
        {
            if (!disc.Exists("/SYSTEM.CNF")) return "This disc image is not Digimon World (no SYSTEM.CNF).";
            string boot = BootFile(Encoding.ASCII.GetString(disc.ReadFile("/SYSTEM.CNF")));
            if (boot == null || !disc.Exists(boot)) return "This disc image is not Digimon World (its program file was not found).";
            string sha;
            using (var h = SHA1.Create()) sha = Hex(h.ComputeHash(disc.ReadFile(boot)));
            if (sha == UsaSha1) return null;
            for (int i = 0; i < Known.GetLength(0); i++)
                if (Known[i, 0] == sha)
                    return "This is the " + Known[i, 1] + " Digimon World disc. The beta plays the USA disc (SLUS-01032) only. Other versions are planned for later.";
            return "This disc's program file (" + boot.TrimStart('/') + ") does not match the USA Digimon World disc (SLUS-01032). " +
                   "It may be another game, another version, or a modified (patched) copy.";
        }

        // "BOOT = cdrom:\SLUS_010.32;1" -> "/SLUS_010.32"
        static string BootFile(string cnf)
        {
            foreach (string raw in cnf.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("BOOT", StringComparison.OrdinalIgnoreCase)) continue;
                int c = line.IndexOf(':');
                if (c < 0) return null;
                string p = line.Substring(c + 1).Trim().Replace('\\', '/');
                int semi = p.IndexOf(';');
                if (semi >= 0) p = p.Substring(0, semi);
                return p.StartsWith("/") ? p : "/" + p;
            }
            return null;
        }

        static string Hex(byte[] b)
        {
            var s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2"));
            return s.ToString();
        }

        /// <summary>A .cue sheet points at its .bin: the first FILE "name" line, next to the .cue. Other paths are returned as they are.</summary>
        public static string ResolveCue(string path)
        {
            if (!path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return path;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (!line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase)) continue;
                int a = line.IndexOf('"'), b = line.LastIndexOf('"');
                if (a < 0 || b <= a) continue;
                return Path.Combine(Path.GetDirectoryName(path) ?? "", line.Substring(a + 1, b - a - 1));
            }
            return path;
        }
    }
}
