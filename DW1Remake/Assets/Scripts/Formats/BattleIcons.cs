using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// The order and mood icons of ETCDAT/BTLCOMND.TIM (16 x 16 each, two animation frames side by side), built at run time.
    /// Row 0, in the order of the game's order names: Run, Attack, Your Call, Moderate, Distance, Defensive, Change.
    /// Row 1: fire, dark runner, lightning, plant, water drop, gear, poop.
    /// </summary>
    public sealed class BattleIcons
    {
        public const int Fire = 0, Retreat = 2, Thunder = 4, Plant = 6, Water = 8, Gear = 10, Poop = 12;           // row 1, first frame

        public Texture2D Sheet;

        public static BattleIcons Load(DiscImage disc)
        {
            if (!disc.Exists("/ETCDAT/BTLCOMND.TIM")) return null;
            TimImage tim;
            if (!TimImage.TryParse(disc.ReadFile("/ETCDAT/BTLCOMND.TIM"), 0, out tim)) return null;
            return new BattleIcons { Sheet = tim.ToTexture(0) };
        }

        /// <summary>Texture area of the icon in column col (0-15) of row 0 or 1; frame 0 or 1 picks the animation frame.</summary>
        public Rect Uv(int col, int row, int frame = 0)
        {
            float w = 16f / Sheet.width, h = 16f / Sheet.height;
            return new Rect((col + frame) * w, 1f - (row + 1) * h, w, h);
        }
    }
}
