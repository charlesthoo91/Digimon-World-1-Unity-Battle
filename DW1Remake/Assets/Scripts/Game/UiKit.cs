using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Small drawing helpers for the game's own looking panels (dark blue panel, cyan frame, tabs, bars).
    /// Everything is drawn on a 4:3 canvas of 1415 x 1060 units that is scaled to fit the screen, so layouts can be
    /// written once. Colours and shapes are our own; nothing is taken from the disc.
    /// </summary>
    public static class UiKit
    {
        public const float W = 1415f, H = 1060f;

        public static readonly Color PanelFill = new Color(0.15f, 0.21f, 0.26f, 0.97f);
        public static readonly Color Frame = new Color(0.30f, 0.78f, 0.98f, 1f);
        public static readonly Color Yellow = new Color(1f, 0.95f, 0.40f, 1f);
        public static readonly Color Field = new Color(0.20f, 0.30f, 0.40f, 1f);
        public static readonly Color TabOn = new Color(0.45f, 0.92f, 1f, 1f);
        public static readonly Color TabOff = new Color(0.17f, 0.38f, 0.45f, 1f);
        public static readonly Color Cyan = new Color(0.30f, 0.80f, 1f, 1f);
        public static readonly Color Grey = new Color(0.45f, 0.45f, 0.45f, 1f);

        static Matrix4x4 _old;

        public static void Begin()
        {
            _old = GUI.matrix;
            float s = Mathf.Min(Screen.width / W, Screen.height / H);
            GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - W * s) / 2f, (Screen.height - H * s) / 2f, 0f), Quaternion.identity, new Vector3(s, s, 1f));
        }

        public static void End() { GUI.matrix = _old; }

        public static void Fill(Rect r, Color c)
        {
            var o = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = o;
        }

        /// <summary>A panel: black outline, cyan frame, dark fill.</summary>
        public static void Box(Rect r, Color fill, float frame = 5f)
        {
            Fill(r, Color.black);
            Fill(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), Frame);
            Fill(new Rect(r.x + 2 + frame, r.y + 2 + frame, r.width - 4 - 2 * frame, r.height - 4 - 2 * frame), fill);
        }

        public static void Text(Rect r, string s, int size, Color c, TextAnchor a = TextAnchor.UpperLeft, bool wrap = false)
        {
            var st = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = a, wordWrap = wrap, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), s, st);   // little shadow so text reads on any background
            st.normal.textColor = c;
            GUI.Label(r, s, st);
        }

        public static void Tab(Rect r, string label, bool on)
        {
            Fill(r, Color.black);
            Fill(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 2), on ? TabOn : TabOff);
            Text(r, label, 46, on ? Color.white : new Color(0.25f, 0.6f, 0.95f), TextAnchor.MiddleCenter);
        }

        /// <summary>Yellow label tag like PROFILE / CONDITION / PARAMETER.</summary>
        public static void Tag(Rect r, string label)
        {
            Fill(r, new Color(1f, 0.82f, 0.15f, 1f));
            Fill(new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8), new Color(0.35f, 0.25f, 0.05f, 1f));
            Text(r, label, 38, new Color(1f, 0.9f, 0.4f), TextAnchor.MiddleCenter);
        }

        public static void Value(Rect r, string s, int size = 44, TextAnchor a = TextAnchor.MiddleLeft)
        {
            Fill(r, new Color(0.08f, 0.12f, 0.16f, 1f));
            Fill(new Rect(r.x + 3, r.y + 3, r.width - 6, r.height - 6), Field);
            Text(new Rect(r.x + 12, r.y, r.width - 24, r.height), s, size, new Color(0.88f, 0.92f, 0.95f), a);
        }

        public static void Bar(Rect r, float t, Color fill)
        {
            Fill(r, new Color(0.05f, 0.08f, 0.1f, 1f));
            Fill(new Rect(r.x + 3, r.y + 3, r.width - 6, r.height - 6), Field);
            Fill(new Rect(r.x + 3, r.y + 3, (r.width - 6) * Mathf.Clamp01(t), r.height - 6), fill);
        }

        public static void Square(Rect r) { Fill(r, new Color(0.35f, 0.35f, 0.35f)); Fill(new Rect(r.x + 3, r.y + 3, r.width - 6, r.height - 6), Grey); }
    }
}
