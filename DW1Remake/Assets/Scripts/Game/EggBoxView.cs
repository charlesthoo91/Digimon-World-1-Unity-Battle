using System;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The egg box of built-in 0x1D (doo2/doo2.c, US build): DOO2_openEggBox, DOO2_renderEggIcons, DOO2_tickEggInput. UI box 3 at (-84, -70)
    /// 168 x 80 growing out of (-8, -8, 16, 16), four eggs (DOO2_EGG_ICON_SPRITE: 32 x 32 at x = i x 36 - 54, y -30, centred on (16, 16),
    /// u = EGG_ICONS[i] x 32 + 96, v 0, colour row 492 + i) and the cursor (DOO2_EGG_CURSOR_SPRITE: 40 x 40 at u 0x60 v 0x20, row 0x1F0), both
    /// added (attribute 0x50000000: semi-transparent, abr 1), from texture page 0x39 = ETCDAT/TAMA.TIM (VRAM 576, 256; colours from 128, 488).
    /// </summary>
    public sealed class EggBoxView : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 3 };
        public int Slot;
        readonly int[] _icons = new int[4];
        TimImage _tama;
        readonly Texture2D[,] _layer = new Texture2D[16, 2];
        readonly Material[] _add = new Material[16];
        float _acc;

        public static EggBoxView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32") || !disc.Exists("/ETCDAT/TAMA.TIM")) return null;
            var v = new EggBoxView();
            v.Init(disc, font);
            for (int i = 0; i < 4; i++) v._icons[i] = v.Exe[v.At(0x80134BA0) + i];      // EGG_ICONS
            TimImage.TryParse(disc.ReadFile("/ETCDAT/TAMA.TIM"), 0, out v._tama);
            return v;
        }

        public bool Showing { get { return Box.State != 0; } }

        /// <summary>DOO2_openEggBox: slot 0, createAnimatedUIBox(3, 0, 2, ...).</summary>
        public void OpenBox()
        {
            Slot = 0;
            Open(Box, new RectInt(-84, -70, 168, 80), new RectInt(-8, -8, 16, 16), 2 << 4);
            Box.Color = 0; Box.Features = 2;
        }

        /// <summary>DOO2_tickEggInput: Right / Left move (sound 2), Cross shrinks the box into (-8, -8, 16, 16) (sound 3). True once it is gone.</summary>
        public bool Tick(int side, bool cross)
        {
            if (Box.State == 0) return true;
            if (Box.State != 1) return false;
            if (side > 0) { Slot = (Slot + 1) % 4; Snd(2); }
            if (side < 0) { Slot = (Slot + 3) % 4; Snd(2); }
            if (cross) { Box.Origin = new RectInt(-8, -8, 16, 16); Close(Box); Snd(3); }
            return false;
        }

        public void Draw()
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); }
            }
            Begin();
            DrawBox(Box, () =>
            {
                int z = 6 - Box.Id;
                for (int i = 0; i < 4; i++) { int k = i; Add(z, () => Sprite(492 - 488 + k, _icons[k] * 32 + 96, 0, 32, 32, k * 36 - 54 - 16, -30 - 16)); }
                Add(z, () => Sprite(0x1F0 - 488, 0x60, 0x20, 40, 40, Slot * 36 - 54 - 20, -30 - 20));
            });
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // a GsSPRITE with semi-transparency 1: the colours without the STP bit solid, those with it added (shader DW1Remake/GuiBlend)
        void Sprite(int row, int u, int v, int w, int h, int x, int y)
        {
            if (_tama == null || row < 0 || row >= 16) return;
            if (_layer[row, 0] == null) { _layer[row, 0] = _tama.ToTextureLayer(row, 1); _layer[row, 1] = _tama.ToTextureLayer(row, 2); }
            var solid = _layer[row, 0]; var stp = _layer[row, 1];
            var r = new Rect(X(x), Y(y), w * 4.42f, h * 4.42f);
            var uv = new Rect(u / (float)solid.width, 1f - (v + h) / (float)solid.height, w / (float)solid.width, h / (float)solid.height);
            GUI.DrawTextureWithTexCoords(r, solid, uv);
            if (Event.current.type != EventType.Repaint) return;
            if (_add[row] == null)
            {
                var sh = Shader.Find("DW1Remake/GuiBlend");
                if (sh == null) return;
                _add[row] = new Material(sh) { mainTexture = stp };
                MmdModel.SetPsxBlend(_add[row], 1);
            }
            Graphics.DrawTexture(r, stp, uv, 0, 0, 0, 0, _add[row]);
        }
    }
}
