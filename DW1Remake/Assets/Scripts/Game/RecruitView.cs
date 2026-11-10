using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours, an OPTIONS mod (RECRUIT TAB, off by default): a fifth Player menu tab ("Rec.") listing every Digimon that can join File City, whether
    /// it has, the prosperity points it brings and the city's prosperity now. Read from the player's disc at run time: every recruit event in DG.SCN
    /// sets the recruit flag 0xC8 + type (the same flag the arena's opponent pool reads, main/tournament.c) next to "1F 00 01 n", variable 1
    /// (PSTAT_PROSPERITY_POINTS, include/dw/pstat.h) += n. One event can recruit several Digimon for one sum (Devimon, Airdramon, MetalGreymon: flags 0xCE, 0xCF, 0xD4 in script 184).
    /// </summary>
    public sealed class RecruitView
    {
        public sealed class Recruit { public int[] Types; public int Points; }
        public readonly List<Recruit> All = new List<Recruit>();
        public int Total;               // every recruit's points together

        public static RecruitView Load(DiscImage disc)
        {
            if (disc == null || !disc.Exists("/SCN/DG.SCN")) return null;
            byte[] d = disc.ReadFile("/SCN/DG.SCN");
            var v = new RecruitView();
            var seen = new HashSet<int>();
            for (int p = 0; p + 4 <= d.Length; p++)
            {
                // ADD_VARIABLE 1 (prosperity), n
                if (d[p] != 0x1F || d[p + 1] != 0 || d[p + 2] != 1) continue;
                int n = d[p + 3];
                var types = new List<int>();
                // the SET_FLAG commands right before it (most events), else right after it (Monochromon)
                for (int q = p - 4; q >= 0 && d[q] == 0x1C && d[q + 1] == 0; q -= 4) AddType(types, d[q + 2] | d[q + 3] << 8);
                if (types.Count == 0) for (int q = p + 4; q + 4 <= d.Length && d[q] == 0x1C && d[q + 1] == 0; q += 4) AddType(types, d[q + 2] | d[q + 3] << 8);
                types.RemoveAll(t => seen.Contains(t));      // a Digimon with several recruit scenes (Nanimon, Gabumon) once
                if (types.Count == 0) continue;
                types.Sort();
                foreach (int t in types) seen.Add(t);
                v.All.Add(new Recruit { Types = types.ToArray(), Points = n });
                v.Total += n;
            }
            v.All.Sort((a, b) => a.Types[0].CompareTo(b.Types[0]));
            return v;
        }

        // a recruit flag is 0xC8 + a Digimon type of the Chart (1-62); other flags set there are story flags
        static void AddType(List<int> types, int flag) { int t = flag - 0xC8; if (t >= 1 && t <= 62 && !types.Contains(t)) types.Add(t); }

        static readonly Color Joined = new Color(0.45f, 1f, 0.45f), NotYet = new Color(0.60f, 0.64f, 0.68f);

        /// <summary>Draws the tab's page inside the Player menu box (canvas units, inside UiKit.Begin).</summary>
        public void Draw(DiscImage disc, ScriptState s)
        {
            int types = 0, joined = 0;
            foreach (var r in All) foreach (int t in r.Types) { types++; if (s.GetFlag(0xC8 + t)) joined++; }
            UiKit.Text(new Rect(80, 160, 800, 50), "File City  " + joined + " / " + types + " joined", 34, UiKit.Yellow);
            UiKit.Text(new Rect(700, 160, 640, 50), "Prosperity  " + s.Vars[1] + " (all recruits: " + Total + ")", 34, UiKit.Yellow, TextAnchor.UpperRight);
            const float x0 = 80, y0 = 222, colW = 318, rowH = 49;
            int rows = Mathf.CeilToInt(types / 4f), i = 0;
            bool together = false;
            foreach (var r in All)
                foreach (int t in r.Types)
                {
                    bool on = s.GetFlag(0xC8 + t);
                    var info = DigimonStatsCatalog.Get(disc, t);
                    string pts = "+" + r.Points + (r.Types.Length > 1 ? "*" : "");
                    if (r.Types.Length > 1) together = true;
                    float x = x0 + colW * (i / rows), y = y0 + rowH * (i % rows);
                    UiKit.Text(new Rect(x, y, 34, rowH), on ? "●" : "○", 26, on ? Joined : NotYet);
                    UiKit.Text(new Rect(x + 34, y, colW - 110, rowH), info != null ? info.Name : "#" + t, 26, on ? Color.white : NotYet);
                    UiKit.Text(new Rect(x + colW - 80, y, 70, rowH), pts, 26, on ? Joined : NotYet, TextAnchor.UpperRight);
                    i++;
                }
            string foot = "Green: joined.  Grey: not yet.  +n: prosperity points it brings.";
            if (together)
                foreach (var r in All)
                    if (r.Types.Length > 1)
                    {
                        string names = "";
                        for (int k = 0; k < r.Types.Length; k++) { var info = DigimonStatsCatalog.Get(disc, r.Types[k]); names += (k == 0 ? "" : k == r.Types.Length - 1 ? " and " : ", ") + (info != null ? info.Name : "#" + r.Types[k]); }
                        foot += "  *" + names + " join together in one event for " + r.Points + " points.";
                    }
            UiKit.Text(new Rect(80, y0 + rowH * rows + 6, 1260, 80), foot, 22, NotYet, TextAnchor.UpperLeft, true);
        }
    }
}
