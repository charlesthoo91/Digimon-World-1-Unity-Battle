using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// The small Digimon pictures of ETCDAT/SBOY.TIM, built at run time. Portrait n sits at row n / 8, column (n % 8) x 2 of a grid of 16 x 16 pixel cells;
    /// the cell next to it is the second animation frame. The order is the order of the Card List page.
    /// </summary>
    public sealed class Portraits
    {
        public const int PerRow = 8, Count = 72;
        public Texture2D Sheet;
        TimImage _tim; readonly Texture2D[] _byColour = new Texture2D[8];

        /// <summary>The sheet in colour set n: each Digimon has its own (see GameMenus.FaceColour).</summary>
        public Texture2D SheetFor(int n)
        {
            if (n == 0) return Sheet;
            if (_byColour[n] == null) _byColour[n] = _tim.ToTexture(n);
            return _byColour[n];
        }

        public static Portraits Load(DiscImage disc)
        {
            if (!disc.Exists("/ETCDAT/SBOY.TIM")) return null;
            TimImage tim;
            if (!TimImage.TryParse(disc.ReadFile("/ETCDAT/SBOY.TIM"), 0, out tim)) return null;
            return new Portraits { Sheet = tim.ToTexture(0), _tim = tim };
        }

        public Rect Uv(int n, int frame = 0)
        {
            float w = 16f / Sheet.width, h = 16f / Sheet.height;
            return new Rect(((n % PerRow) * 2 + frame) * w, 1f - (n / PerRow + 1) * h, w, h);
        }
    }
}
