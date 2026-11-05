using System;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// One animated UI box of the game (main/ui.c: createAnimatedUIBox, removeAnimatedUIBox, removeStaticUIBox) whose inside someone else
    /// draws: the lives box (MURD_createLivesBox), the training slot machine's box, the save machine's boxes. The content action is drawn
    /// once the box is open, under the box's own frame order (ordering table 6 - id).
    /// </summary>
    public sealed class FrameBox : UiBoxPage
    {
        public readonly UiBox Box;
        float _acc;

        FrameBox(int id) { Box = new UiBox { Id = id }; }

        public static FrameBox Load(DiscImage disc, GameFont font, int id)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new FrameBox(id);
            v.Init(disc, font);
            return v;
        }

        public bool Showing { get { return Box.State != 0; } }
        public bool IsOpen { get { return Box.State == 1; } }

        /// <summary>createAnimatedUIBox(id, colour, features, rect, from): sound 0, it grows in 4 frames.</summary>
        public void OpenBox(RectInt rect, RectInt from, int colour, int features)
        {
            Open(Box, rect, from, features << 4);
            Box.Color = colour; Box.Features = features;
        }

        /// <summary>createStaticUIBox: there at once, no sound.</summary>
        public void OpenNow(RectInt rect, int colour, int features)
        {
            OpenStatic(Box, rect, features << 4);
            Box.Color = colour; Box.Features = features;
        }

        /// <summary>removeAnimatedUIBox(id, target): it shrinks into the target (null: where it came from).</summary>
        public void ShrinkTo(RectInt? target) { if (target.HasValue) Box.Origin = target.Value; Close(Box); }

        /// <summary>removeStaticUIBox: gone at once.</summary>
        public void Remove() { CloseNow(Box); }

        /// <summary>Our own digital clock plate (the game has none; the user allowed it): a static UI box with the time in the game's font.</summary>
        public void ShowText(RectInt rect, string text)
        {
            if (Box.State != 1) { OpenStatic(Box, rect, 2 << 4); Box.Color = 0; Box.Features = 2; }
            Box.Rect = rect; Box.Origin = rect;
            // each character in a cell of its own (12 apart, so none is overwritten), then drawn half size, side by side
            BuildPage(text, px =>
            {
                if (Font == null) return;
                for (int i = 0; i < text.Length; i++) Font.Render(text[i].ToString(), px, 256, 256, i * 12, 0, Font.Colour(1));
            });
            const float k = 0.5f;
            var adv = new float[text.Length]; float tw = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                int m = Font != null ? Font.Measure(text[i].ToString()) : 8;
                adv[i] = Mathf.Clamp(m, 4, 12) * k + 0.5f; tw += adv[i];
            }
            Draw(() =>
            {
                var tex = Page; if (tex == null) return;
                int z = 6 - Box.Id;
                float x = rect.x + (rect.width - tw) / 2f, y = rect.y + (rect.height - 12 * k) / 2f + 0.5f;
                for (int i = 0; i < text.Length; i++)
                {
                    float cx = x, w = adv[i] / k;
                    var uv = new Rect(i * 12 / 256f, 1f - 12 / 256f, w / 256f, 12 / 256f);
                    float ax = cx, ay = y;
                    Add(z, () => { var o = GUI.color; GUI.color = TextColor[0]; GUI.DrawTextureWithTexCoords(new Rect(X(ax), Y(ay), adv_(w) * 4.42f, 12 * k * 4.42f), tex, uv); GUI.color = o; });
                    x += adv[i];
                }
            }, true);
        }

        static float adv_(float w) { return w * 0.5f; }

        public void Draw(Action content, bool direct = false)
        {
            if (!Showing) return;
            if (Event.current.type == EventType.Repaint)
            {
                _acc += Time.unscaledDeltaTime;
                while (_acc >= 0.05f) { _acc -= 0.05f; Step(Box); }
            }
            Begin();
            DrawBox(Box, () => { if (content == null) return; if (direct) content(); else Add(6 - Box.Id, content); });
            UiKit.Begin();
            Flush();
            UiKit.End();
        }
    }
}
