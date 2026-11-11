using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The box for an item picked up or taken from a chest (tamerTickPickupItem / tamerTickTakeChest, renderItemPickupTextbox in tamer.c, US build):
    /// UI box 1 at (-130, 42) 262 x 59, colour 0, features 2, growing out of the boy (createAnimatedUIBox) and shrinking back into him.
    /// The text page holds the item's name (row 0x18) and MAIN_D_801341FC "Woah!" (row 0x24); state 0 shows the name at y 0x2D in colour 0x0F
    /// and "Woah!" at y 0x39 in colour 0, state 1 ("I can't hold anymore.") and 2 ("Hey! It's empty!") row 0x18 at y 0x39; then the "next" mark (renderUIBox(1)).
    /// </summary>
    public sealed class PickupBox : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 1 };
        public int State;                   // TAKE_CHEST_STATE: 0 the item, 1 the bag is full, 2 an empty chest; ours: 3 the award box
        // tamerTickAwardSomething: up to three lines drawn at rows 0x78, 0x84, 0x90 in setTextColor's colour; renderAwardSomethingTextbox
        // shows them with renderString(0, -0x7D, 0x2D, 0xF0, 0x24, 0, 0x78)
        string[] _award = new string[0]; int _awardColour;
        string _name, _woah;
        int _timer;                         // TEXTBOX_OPEN_TIMER (renderItemPickupTextbox counts it each frame it draws)
        float _acc;

        public static PickupBox Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32")) return null;
            var b = new PickupBox();
            b.Init(disc, font);
            b._woah = DigimonStatsCatalog.ExeText(disc, 0x801341FC);
            return b;
        }

        public bool Showing { get { return Box.State != 0; } }

        /// <summary>drawString into rows 0x18 (the item name or a message) and 0x24 ("Woah!").</summary>
        public void SetText(string line, int state) { _name = line; State = state; }

        /// <summary>The award box (state 3): the lines and their FONT_CLUT colour.</summary>
        public void SetAward(string[] lines, int colour) { _award = lines ?? new string[0]; _awardColour = colour; State = 3; }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); if (Box.State == 1) _timer++; }
            }
            BuildPage((_name ?? "") + "|" + _woah + "|" + State + "|" + string.Join("|", _award) + "|" + _awardColour, px =>
            {
                if (Font == null) return;
                if (State == 3)
                {
                    for (int i = 0; i < _award.Length && i < 3; i++) Font.Render(_award[i] ?? "", px, 256, 256, 0, 0x78 + 12 * i, Font.Colour(_awardColour));
                    return;
                }
                Font.Render(_name ?? "", px, 256, 256, 0, 0x18);
                Font.Render(_woah ?? "", px, 256, 256, 0, 0x24);
            });
            Begin();
            DrawBox(Box, () =>
            {
                int z = 6 - Box.Id;
                if (State == 3) PageText(z, 0, -0x7D, 0x2D, 0xF0, 0x24, 0, 0x78);
                else if (State == 0)
                {
                    PageText(z, 0, -0x7D, 0x39, 0x5A, 12, 0, 0x24);
                    PageText(z, 0x0F, -0x7D, 0x2D, 0x60, 12, 0, 0x18);
                }
                else PageText(z, 0, -0x7D, 0x39, State == 1 ? 0xF0 : 0x90, 12, 0, 0x18);
                // renderUIBox(1)
                SysIcon(z, Box.Rect.x + 0xEF, Box.Rect.y + 0x2B, 16, 12, _timer / 4 % 3 * 16 + 0x4C, 0xB7, 12);
            });
            UiKit.Begin();
            Flush();
            UiKit.End();
        }
    }
}
