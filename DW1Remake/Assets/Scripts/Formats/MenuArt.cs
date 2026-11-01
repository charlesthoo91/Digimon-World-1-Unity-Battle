using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>The main-menu pictures: the fifth TIM of ETCDAT/ETCTIM.BIN (256 x 64), built at run time. Positions and colour sets are the game's own (DW1-SydPatches GameMenu.cpp).</summary>
    public sealed class MenuArt
    {
        const int SheetOffset = 32544;
        Texture2D[] _sheets;

        public static MenuArt Load(DiscImage disc)
        {
            if (!disc.Exists("/ETCDAT/ETCTIM.BIN")) return null;
            TimImage tim;
            if (!TimImage.TryParse(disc.ReadFile("/ETCDAT/ETCTIM.BIN"), SheetOffset, out tim)) return null;
            return new MenuArt { _sheets = new[] { tim.ToTexture(12), tim.ToTexture(13) } };
        }

        // ITEM, DIGIMON, PLAYER, PRAISE, SCOLD, SLEEP: x, y, width, height, colour set (0 = 12, 1 = 13); the second frame sits 20 pixels to the right
        static readonly int[,] Icons = { { 0, 0, 20, 20, 0 }, { 40, 0, 20, 20, 1 }, { 0, 20, 20, 20, 0 }, { 41, 20, 19, 20, 1 }, { 0, 40, 20, 20, 1 }, { 40, 40, 20, 20, 1 } };

        public Texture2D Sheet(int icon) { return _sheets[Icons[icon, 4]]; }
        public Vector2 Size(int icon) { return new Vector2(Icons[icon, 2], Icons[icon, 3]); }

        public Rect Uv(int icon, int frame)
        {
            float w = 256f, h = 64f;
            return new Rect((Icons[icon, 0] + frame * 20) / w, 1f - (Icons[icon, 1] + Icons[icon, 3]) / h, Icons[icon, 2] / w, Icons[icon, 3] / h);
        }
    }
}
