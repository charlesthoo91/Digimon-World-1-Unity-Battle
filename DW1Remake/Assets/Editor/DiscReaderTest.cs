using System.IO;
using System.Linq;
using System.Text;
using DW1Remake.Disc;
using UnityEditor;
using UnityEngine;

namespace DW1Remake.EditorTools
{
    public static class DiscReaderTest
    {
        [MenuItem("DW1 Remake/Test Disc Reader")]
        static void Run()
        {
            string path = EditorUtility.OpenFilePanel("Select your Digimon World disc image (.bin)", "", "bin,iso,img");
            if (string.IsNullOrEmpty(path)) return;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var disc = new DiscImage(fs);
                var files = disc.Files.Where(f => !f.IsDirectory).ToList();
                var sb = new StringBuilder();
                sb.AppendLine("Disc OK. Volume: " + disc.VolumeId);
                sb.AppendLine("Files found: " + files.Count + "   Folders: " + (disc.Files.Count - files.Count));

                string boot = "/SLUS_010.32";
                if (disc.Exists(boot))
                {
                    byte[] exe = disc.ReadFile(boot);
                    string magic = Encoding.ASCII.GetString(exe, 0, 8);
                    sb.AppendLine("Game program " + boot + ": " + exe.Length + " bytes, header '" + magic + "' (should be PS-X EXE)");
                }
                else sb.AppendLine("Game program not found - this may be a different version of the game.");

                sb.AppendLine("Top-level folders: " + string.Join(", ",
                    disc.Files.Where(f => f.IsDirectory && f.Path.LastIndexOf('/') == 0).Select(f => f.Path)));
                Debug.Log(sb.ToString());
            }
        }
    }
}
