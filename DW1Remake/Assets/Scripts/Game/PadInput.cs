using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace DW1Remake.Game
{
    /// <summary>The PS1 pad's buttons (the game reads only these).</summary>
    public enum PadButton { Up, Down, Left, Right, Cross, Circle, Triangle, Square, L1, R1, Start, Select }

    /// <summary>
    /// Ours (mod "Controls"): the one pad every game key goes through (GameMenus, PlayableRoom, Battle, curling). It is held when any of
    /// these holds it: the keyboard keys of the key mapping (title > MODS > CONTROLS > KEYBOARD KEYS; the defaults are the keys the game always had),
    /// a controller (DualShock 4 or any pad Unity knows: face buttons as on the PS1, d-pad or left stick), or the on-screen touch buttons
    /// (TouchPadView). Read once per Unity frame. Escape and the test keys (F1-F12, I, T, [ ]) stay fixed keyboard keys.
    /// </summary>
    public static class Pad
    {
        const int Count = 12;
        static readonly bool[] _held = new bool[Count], _prev = new bool[Count], _down = new bool[Count];
        static int _frame = -1;

        public static bool Held(PadButton b) { Refresh(); return _held[(int)b]; }
        /// <summary>Pressed this Unity frame (held now and not the frame before, or a mapped key's own press).</summary>
        public static bool Pressed(PadButton b) { Refresh(); return _down[(int)b]; }

        // ------------------------------------------------------------------ the keyboard mapping

#if ENABLE_INPUT_SYSTEM
        static readonly Key[][] Defaults =
        {
            new[] { Key.UpArrow, Key.W }, new[] { Key.DownArrow, Key.S }, new[] { Key.LeftArrow, Key.A }, new[] { Key.RightArrow, Key.D },
            new[] { Key.Space }, new[] { Key.Z }, new[] { Key.Tab, Key.C }, new[] { Key.X },
            new[] { Key.Q }, new[] { Key.E }, new[] { Key.Enter, Key.NumpadEnter }, new[] { Key.Backspace },
        };
        static List<Key>[] _keys;
        const string KeysPref = "DW1Remake.Mod.Keys";

        static List<Key>[] Keys
        {
            get
            {
                if (_keys != null) return _keys;
                _keys = new List<Key>[Count];
                for (int i = 0; i < Count; i++) _keys[i] = new List<Key>(Defaults[i]);
                string saved = null;
                try { saved = PlayerPrefs.GetString(KeysPref, ""); } catch { saved = null; }
                if (!string.IsNullOrEmpty(saved))
                {
                    var parts = saved.Split(';');
                    for (int i = 0; i < Count && i < parts.Length; i++)
                    {
                        var list = new List<Key>();
                        foreach (var p in parts[i].Split(',')) { int v; if (int.TryParse(p, out v) && v > 0 && System.Enum.IsDefined(typeof(Key), v)) list.Add((Key)v); }
                        _keys[i] = list;
                    }
                }
                return _keys;
            }
        }

        static void SaveKeys()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Count; i++)
            {
                if (i > 0) sb.Append(';');
                for (int k = 0; k < Keys[i].Count; k++) { if (k > 0) sb.Append(','); sb.Append((int)Keys[i][k]); }
            }
            try { PlayerPrefs.SetString(KeysPref, sb.ToString()); PlayerPrefs.Save(); }
            catch (System.Exception e) { Debug.LogWarning("[pad] " + e.Message); }
        }

        /// <summary>Gives the button this one key (it is taken off any other button).</summary>
        public static void SetKey(PadButton b, Key key)
        {
            for (int i = 0; i < Count; i++) Keys[i].Remove(key);
            Keys[(int)b].Clear(); Keys[(int)b].Add(key);
            SaveKeys();
        }

        public static void ResetKeys()
        {
            for (int i = 0; i < Count; i++) Keys[i] = new List<Key>(Defaults[i]);
            try { PlayerPrefs.DeleteKey(KeysPref); PlayerPrefs.Save(); } catch { }
        }

        /// <summary>The button's keys as shown on the mapping page ("-" = none).</summary>
        public static string KeyNames(PadButton b)
        {
            var list = Keys[(int)b];
            if (list.Count == 0) return "-";
            string all = KeyName(list[0]);
            // the game's font has no '/' (it draws a triangle there), so the keys are joined with OR
            for (int i = 1; i < list.Count; i++) all += " OR " + KeyName(list[i]);
            return all.Length <= 11 ? all : KeyName(list[0]);       // the page has room for about 11 letters
        }

        static string KeyName(Key k)
        {
            switch (k)
            {
                case Key.UpArrow: return "UP"; case Key.DownArrow: return "DOWN"; case Key.LeftArrow: return "LEFT"; case Key.RightArrow: return "RIGHT";
                case Key.NumpadEnter: return "PAD ENTER"; case Key.Backspace: return "BACKSPACE";
            }
            string s = k.ToString().ToUpperInvariant();
            if (s.StartsWith("DIGIT")) s = s.Substring(5);
            return s;
        }

        /// <summary>The key pressed this frame, for the mapping page (Key.None if none; Escape is never handed out: it cancels).</summary>
        public static Key AnyKeyPressed()
        {
            var kb = Keyboard.current;
            if (kb == null) return Key.None;
            foreach (var c in kb.allKeys)
                if (c != null && c.wasPressedThisFrame && c.keyCode != Key.Escape && c.keyCode != Key.None) return c.keyCode;
            return Key.None;
        }

        // ------------------------------------------------------------------ the controller mapping (control paths on the Gamepad layout)

        static readonly string[][] PadDefaults =
        {
            new[] { "dpad/up", "leftStick/up" }, new[] { "dpad/down", "leftStick/down" }, new[] { "dpad/left", "leftStick/left" }, new[] { "dpad/right", "leftStick/right" },
            new[] { "buttonSouth" }, new[] { "buttonEast" }, new[] { "buttonNorth" }, new[] { "buttonWest" },
            new[] { "leftShoulder" }, new[] { "rightShoulder" }, new[] { "start" }, new[] { "select" },
        };
        static List<string>[] _buttons;
        const string ButtonsPref = "DW1Remake.Mod.PadButtons";

        static List<string>[] Buttons
        {
            get
            {
                if (_buttons != null) return _buttons;
                _buttons = new List<string>[Count];
                for (int i = 0; i < Count; i++) _buttons[i] = new List<string>(PadDefaults[i]);
                string saved = null;
                try { saved = PlayerPrefs.GetString(ButtonsPref, ""); } catch { saved = null; }
                if (!string.IsNullOrEmpty(saved))
                {
                    var parts = saved.Split(';');
                    for (int i = 0; i < Count && i < parts.Length; i++)
                    {
                        _buttons[i] = new List<string>();
                        foreach (var p in parts[i].Split(',')) if (p.Length > 0) _buttons[i].Add(p);
                    }
                }
                return _buttons;
            }
        }

        static void SaveButtons()
        {
            var parts = new string[Count];
            for (int i = 0; i < Count; i++) parts[i] = string.Join(",", Buttons[i].ToArray());
            try { PlayerPrefs.SetString(ButtonsPref, string.Join(";", parts)); PlayerPrefs.Save(); }
            catch (System.Exception e) { Debug.LogWarning("[pad] " + e.Message); }
        }

        /// <summary>Gives the button this one controller button (it is taken off any other button).</summary>
        public static void SetPadButton(PadButton b, string path)
        {
            for (int i = 0; i < Count; i++) Buttons[i].Remove(path);
            Buttons[(int)b].Clear(); Buttons[(int)b].Add(path);
            SaveButtons();
        }

        public static void ResetPadButtons()
        {
            for (int i = 0; i < Count; i++) Buttons[i] = new List<string>(PadDefaults[i]);
            try { PlayerPrefs.DeleteKey(ButtonsPref); PlayerPrefs.Save(); } catch { }
        }

        /// <summary>The button's controller buttons as shown on the mapping page ("-" = none), in PS names where the layout has one.</summary>
        public static string PadButtonNames(PadButton b)
        {
            var list = Buttons[(int)b];
            if (list.Count == 0) return "-";
            // the d-pad and the left stick on the same direction: both names would not fit
            if (list.Count == 2 && list[0].StartsWith("dpad/") && list[1] == "leftStick/" + list[0].Substring(5)) return "PAD OR LS";
            string all = PadName(list[0]);
            for (int i = 1; i < list.Count; i++) all += " OR " + PadName(list[i]);
            return all.Length <= 11 ? all : PadName(list[0]);
        }

        static string PadName(string path)
        {
            switch (path)
            {
                case "buttonSouth": return "CROSS"; case "buttonEast": return "CIRCLE"; case "buttonNorth": return "TRIANGLE"; case "buttonWest": return "SQUARE";
                case "leftShoulder": return "L1"; case "rightShoulder": return "R1"; case "leftTrigger": return "L2"; case "rightTrigger": return "R2";
                case "leftStickPress": return "L3"; case "rightStickPress": return "R3"; case "start": return "START"; case "select": return "SELECT";
                case "dpad/up": return "UP"; case "dpad/down": return "DOWN"; case "dpad/left": return "LEFT"; case "dpad/right": return "RIGHT";
                case "leftStick/up": return "LS UP"; case "leftStick/down": return "LS DOWN"; case "leftStick/left": return "LS LEFT"; case "leftStick/right": return "LS RIGHT";
                case "rightStick/up": return "RS UP"; case "rightStick/down": return "RS DOWN"; case "rightStick/left": return "RS LEFT"; case "rightStick/right": return "RS RIGHT";
            }
            return path.Replace('/', ' ').ToUpperInvariant();
        }

        /// <summary>The controller button pressed this frame, as its path on the controller (null if none), for the mapping page.</summary>
        public static string AnyPadButtonPressed()
        {
            var g = Gamepad.current;
            if (g == null) return null;
            // real buttons first; the sticks' directions (made from the stick, "synthetic") only if no real button went down
            for (int pass = 0; pass < 2; pass++)
            foreach (var c in g.allControls)
            {
                var b = c as ButtonControl;
                if (b == null || !b.wasPressedThisFrame || b.synthetic != (pass == 1)) continue;
                string p = b.path;
                string dev = g.path + "/";
                if (!p.StartsWith(dev)) continue;
                p = p.Substring(dev.Length);
                // the sticks' and d-pad's own directions are taken by name; a whole stick press without a direction is skipped
                if (p == "dpad" || p == "leftStick" || p == "rightStick") continue;
                return p;
            }
            return null;
        }
#else
        public static string KeyNames(PadButton b) { return "-"; }
        public static void ResetKeys() { }
        public static string PadButtonNames(PadButton b) { return "-"; }
        public static void ResetPadButtons() { }
#endif

        // ------------------------------------------------------------------ once a frame

        static void Refresh()
        {
            if (Time.frameCount == _frame) return;
            _frame = Time.frameCount;
            System.Array.Copy(_held, _prev, Count);
            for (int i = 0; i < Count; i++) { _held[i] = false; _down[i] = false; }
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
                for (int i = 0; i < Count; i++)
                    foreach (var key in Keys[i])
                    {
                        var c = kb[key];
                        if (c == null) continue;
                        if (c.isPressed) _held[i] = true;
                        if (c.wasPressedThisFrame) _down[i] = true;
                    }
            if (ModSettings.Controller)
            {
                var g = Gamepad.current;
                if (g != null)
                {
                    for (int i = 0; i < Count; i++)
                        foreach (var path in Buttons[i])
                        {
                            var c = g.TryGetChildControl<ButtonControl>(path);
                            if (c == null) continue;
                            if (c.isPressed) _held[i] = true;
                            if (c.wasPressedThisFrame) _down[i] = true;
                        }
                }
            }
            TouchPadView.Read(_held);
#endif
            for (int i = 0; i < Count; i++) if (_held[i] && !_prev[i]) _down[i] = true;
        }

        static void Or(PadButton b, bool on) { if (on) _held[(int)b] = true; }
    }

    /// <summary>
    /// Ours (mod "Controls"): the on-screen pad for touch screens (the web build on a phone or tablet). D-pad bottom left (a touch anywhere on it
    /// gives the nearest one or two directions), Triangle / Circle / Cross / Square bottom right, L1 / R1 above them, Select / Start at the bottom
    /// middle. Several fingers at once. Shown always (TOUCH BUTTONS ON) or once the screen is first touched (AUTO, the default).
    /// </summary>
    public sealed class TouchPadView : MonoBehaviour
    {
        static TouchPadView _view;
        static bool _touched;
        Texture2D _disc, _ring, _arrow, _tri, _circle, _cross, _square;
        GUIStyle _label;

        public static bool Visible { get { int m = ModSettings.TouchButtons; return m == 2 || (m == 1 && _touched); } }

        // the button places, in screen pixels from the top left (IMGUI)
        static float U { get { return Mathf.Min(Screen.width, Screen.height) / 10f; } }
        static Vector2 DpadMid { get { return new Vector2(2.3f * U, Screen.height - 2.6f * U); } }
        static Vector2 FaceMid { get { return new Vector2(Screen.width - 2.3f * U, Screen.height - 2.6f * U); } }

        static Rect Face(PadButton b)
        {
            Vector2 m = FaceMid; float r = 0.68f * U, d = 1.25f * U;
            Vector2 c = b == PadButton.Triangle ? m + new Vector2(0, -d) : b == PadButton.Cross ? m + new Vector2(0, d) : b == PadButton.Square ? m + new Vector2(-d, 0) : m + new Vector2(d, 0);
            return new Rect(c.x - r, c.y - r, 2 * r, 2 * r);
        }
        static Rect Shoulder(bool right)
        {
            float w = 1.7f * U, h = 0.8f * U, y = Screen.height - 5.3f * U;
            return right ? new Rect(Screen.width - 2.3f * U - w / 2, y, w, h) : new Rect(2.3f * U - w / 2, y, w, h);
        }
        static Rect Middle(bool start)
        {
            float w = 1.7f * U, h = 0.6f * U, y = Screen.height - 0.9f * U;
            return start ? new Rect(Screen.width / 2f + 0.2f * U, y, w, h) : new Rect(Screen.width / 2f - 0.2f * U - w, y, w, h);
        }

#if ENABLE_INPUT_SYSTEM
        /// <summary>Adds the buttons the fingers hold now (Pad.Refresh).</summary>
        public static void Read(bool[] held)
        {
            if (ModSettings.TouchButtons == 0) return;
            if (ModSettings.TouchButtons == 2) Ensure();
            var ts = Touchscreen.current;
            if (ts == null) return;
            foreach (var t in ts.touches)
            {
                if (!t.press.isPressed) continue;
                _touched = true;
                Ensure();
                Vector2 p = t.position.ReadValue();
                p.y = Screen.height - p.y;
                Hit(p, held);
            }
        }
#endif

        static void Hit(Vector2 p, bool[] held)
        {
            float u = U;
            Vector2 d = p - DpadMid;
            if (d.magnitude < 2.1f * u && d.magnitude > 0.25f * u)
            {
                // the nearest one or two directions: a finger more than 0.4 of the way off an axis also counts the other one
                float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y);
                if (ax >= ay * 0.4f) held[(int)(d.x < 0 ? PadButton.Left : PadButton.Right)] = true;
                if (ay >= ax * 0.4f) held[(int)(d.y < 0 ? PadButton.Up : PadButton.Down)] = true;
                return;
            }
            foreach (var b in new[] { PadButton.Triangle, PadButton.Circle, PadButton.Cross, PadButton.Square })
            {
                var r = Face(b);
                if ((p - r.center).magnitude <= r.width * 0.62f) { held[(int)b] = true; return; }
            }
            if (Grow(Shoulder(false)).Contains(p)) { held[(int)PadButton.L1] = true; return; }
            if (Grow(Shoulder(true)).Contains(p)) { held[(int)PadButton.R1] = true; return; }
            if (Grow(Middle(false)).Contains(p)) { held[(int)PadButton.Select] = true; return; }
            if (Grow(Middle(true)).Contains(p)) held[(int)PadButton.Start] = true;
        }

        static Rect Grow(Rect r) { float g = 0.15f * U; return new Rect(r.x - g, r.y - g, r.width + 2 * g, r.height + 2 * g); }

        public static void Ensure()
        {
            if (_view != null) return;
            var go = new GameObject("TouchPad");
            DontDestroyOnLoad(go);
            _view = go.AddComponent<TouchPadView>();
        }

        void OnGUI()
        {
            if (!Visible || Event.current.type != EventType.Repaint) return;
            if (_disc == null) MakeTextures();
            GUI.depth = -1000;
            var old = GUI.color;
            float u = U;
            // the d-pad: a disc and four arrows
            Vector2 m = DpadMid;
            GUI.color = new Color(1, 1, 1, 0.22f);
            GUI.DrawTexture(new Rect(m.x - 2f * u, m.y - 2f * u, 4f * u, 4f * u), _disc);
            GUI.color = new Color(1, 1, 1, 0.75f);
            for (int i = 0; i < 4; i++)
            {
                var c = m + (i == 0 ? new Vector2(0, -1.25f) : i == 1 ? new Vector2(1.25f, 0) : i == 2 ? new Vector2(0, 1.25f) : new Vector2(-1.25f, 0)) * u;
                var piv = GUI.matrix;
                GUIUtility.RotateAroundPivot(90f * i, c);
                GUI.DrawTexture(new Rect(c.x - 0.45f * u, c.y - 0.45f * u, 0.9f * u, 0.9f * u), _arrow);
                GUI.matrix = piv;
            }
            // the face buttons in their PS colours
            Face(PadButton.Triangle, _tri, new Color(0.35f, 0.95f, 0.65f));
            Face(PadButton.Circle, _circle, new Color(1f, 0.4f, 0.45f));
            Face(PadButton.Cross, _cross, new Color(0.5f, 0.7f, 1f));
            Face(PadButton.Square, _square, new Color(1f, 0.6f, 0.9f));
            if (_label == null) _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _label.fontSize = Mathf.Max(10, (int)(0.32f * u));
            _label.normal.textColor = new Color(1, 1, 1, 0.85f);
            Pill(Shoulder(false), "L1"); Pill(Shoulder(true), "R1");
            Pill(Middle(false), "SELECT"); Pill(Middle(true), "START");
            GUI.color = old;
        }

        void Face(PadButton b, Texture2D symbol, Color col)
        {
            var r = Face(b);
            bool on = Pad.Held(b);
            GUI.color = new Color(1, 1, 1, on ? 0.45f : 0.25f);
            GUI.DrawTexture(r, _disc);
            GUI.color = new Color(col.r, col.g, col.b, 0.9f);
            float s = r.width * 0.25f;
            GUI.DrawTexture(new Rect(r.x + s, r.y + s, r.width - 2 * s, r.height - 2 * s), symbol);
        }

        void Pill(Rect r, string text)
        {
            GUI.color = new Color(1, 1, 1, 0.25f);
            GUI.DrawTexture(r, _ring);
            GUI.color = Color.white;
            GUI.Label(r, text, _label);
        }

        // ------------------------------------------------------------------ the pictures (drawn here, no art files)

        void MakeTextures()
        {
            const int N = 96;
            _disc = Paint(N, (x, y) => Mathf.Clamp01((N / 2f - 1f - Mathf.Sqrt(x * x + y * y)) * 0.6f));
            _ring = Paint(N, (x, y) => Mathf.Max(Mathf.Clamp01((N / 2f - 1f - Mathf.Sqrt(x * x + y * y)) * 0.6f) * 0.6f, Ring(x, y, N / 2f - 4f, 3f)));
            _circle = Paint(N, (x, y) => Ring(x, y, N * 0.36f, N * 0.06f));
            _square = Paint(N, (x, y) => Mathf.Clamp01(N * 0.06f - Mathf.Abs(Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) - N * 0.32f)));
            _cross = Paint(N, (x, y) => Mathf.Max(Line(x, y, -N * 0.33f, -N * 0.33f, N * 0.33f, N * 0.33f, N * 0.06f), Line(x, y, -N * 0.33f, N * 0.33f, N * 0.33f, -N * 0.33f, N * 0.06f)));
            float a = N * 0.36f;
            Vector2 p0 = new Vector2(0, -a), p1 = new Vector2(a * 0.95f, a * 0.62f), p2 = new Vector2(-a * 0.95f, a * 0.62f);
            _tri = Paint(N, (x, y) => Mathf.Max(Line(x, y, p0.x, p0.y, p1.x, p1.y, N * 0.06f), Mathf.Max(Line(x, y, p1.x, p1.y, p2.x, p2.y, N * 0.06f), Line(x, y, p2.x, p2.y, p0.x, p0.y, N * 0.06f))));
            // an arrow pointing up (the picture's top is the screen's top)
            _arrow = Paint(N, (x, y) => InTriangle(new Vector2(x, y), new Vector2(0, -N * 0.38f), new Vector2(N * 0.36f, N * 0.2f), new Vector2(-N * 0.36f, N * 0.2f)) ? 1f : 0f);
        }

        static float Ring(float x, float y, float r, float w) { return Mathf.Clamp01(w - Mathf.Abs(Mathf.Sqrt(x * x + y * y) - r)); }

        static float Line(float x, float y, float ax, float ay, float bx, float by, float w)
        {
            Vector2 p = new Vector2(x, y), a = new Vector2(ax, ay), ab = new Vector2(bx - ax, by - ay);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Mathf.Clamp01(w - (p - (a + ab * t)).magnitude);
        }

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // x, y from the middle; y grows downwards (IMGUI draws texture row 0 at the bottom, so rows are flipped here)
        static Texture2D Paint(int n, System.Func<float, float, float> alpha)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x = i + 0.5f - n / 2f, y = (n - 1 - j) + 0.5f - n / 2f;
                    px[j * n + i] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(x, y)) * 255f));
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }
    }
}
