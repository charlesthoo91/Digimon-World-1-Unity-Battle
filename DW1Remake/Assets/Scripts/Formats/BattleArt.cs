using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// The battle screen's art, built at run time: the first TIM of ETCDAT/ETCTIM.BIN (HP and MP bars, digits, FINISH letters, PUSH, the BATTLE tile) and ETCDAT/BTLCOMND.TIM (orders),
    /// and the game's own position tables from BTL_REL.BIN. All positions are PS1 pixels from the middle of the 320 x 240 screen.
    /// </summary>
    public sealed class BattleArt
    {
        const uint OverlayBase = 0x80052AE0, BattleWord = 0x80073014, BarX = 0x80073280, BarSprites = 0x800732C0;
        const int BattleWordPieces = 155;
        const float Scale = 1415f / 320f, MiddleX = 1415f / 2f, MiddleY = 530f;

        readonly Texture2D[] _hud = new Texture2D[16], _cmd = new Texture2D[16];
        TimImage _hudTim, _cmdTim, _condTim;
        readonly Texture2D[] _cond = new Texture2D[16];
        Texture2D _cursor, _lifeFill;
        Color32[] _lifeGrey;
        int _lifeWidth = -1;

        public short[] WordX, WordY;              // where each 8 x 12 piece of the word BATTLE ends up
        public int BarLeft;
        public int[,] Bars = new int[6, 7];        // colour row, u, v, w, h, x, y

        public static BattleArt Load(DiscImage disc)
        {
            if (!disc.Exists("/ETCDAT/ETCTIM.BIN") || !disc.Exists("/BTL_REL.BIN")) return null;
            var a = new BattleArt();
            byte[] etc = disc.ReadFile("/ETCDAT/ETCTIM.BIN");
            if (!TimImage.TryParse(etc, 0, out a._hudTim) || !TimImage.TryParse(etc, 8736, out a._cmdTim) || !TimImage.TryParse(etc, 23808, out a._condTim)) return null;
            byte[] b = disc.ReadFile("/BTL_REL.BIN");
            System.Func<uint, int> at = v => (int)(v - OverlayBase);
            a.WordX = new short[BattleWordPieces]; a.WordY = new short[BattleWordPieces];
            for (int i = 0; i < BattleWordPieces; i++)
            {
                a.WordX[i] = System.BitConverter.ToInt16(b, at(BattleWord) + i * 4);
                a.WordY[i] = System.BitConverter.ToInt16(b, at(BattleWord) + i * 4 + 2);
            }
            a.BarLeft = System.BitConverter.ToInt16(b, at(BarX) + 14);
            for (int i = 0; i < 6; i++)
            {
                int o = at(BarSprites) + i * 10;
                a.Bars[i, 0] = System.BitConverter.ToInt16(b, o) - 480;
                a.Bars[i, 1] = b[o + 2]; a.Bars[i, 2] = b[o + 3] - 128; a.Bars[i, 3] = b[o + 4]; a.Bars[i, 4] = b[o + 5];
                a.Bars[i, 5] = System.BitConverter.ToInt16(b, o + 6); a.Bars[i, 6] = System.BitConverter.ToInt16(b, o + 8);
            }
            a._cursor = a._cmdTim.ToTexture(1, 10);    // the cursor uses the colour set 10 entries further on
            return a;
        }

        static void Draw(Texture2D t, float u, float v, float w, float h, float x, float y, float dw, float dh, float sheet = 256f)
        {
            var uv = new Rect(u / sheet, 1f - (v + h) / t.height, w / sheet, h / t.height);
            GUI.DrawTextureWithTexCoords(new Rect(MiddleX + x * Scale, MiddleY + y * Scale, dw * Scale, dh * Scale), t, uv);
        }

        /// <summary>From the first sheet; row = colour y - 480, v = from the top of the sheet.</summary>
        public void Hud(int row, float u, float v, float w, float h, float x, float y, float dw = -1f, float dh = -1f)
        {
            if (_hud[row] == null) _hud[row] = _hudTim.ToTexture(row);
            Draw(_hud[row], u, v, w, h, x, y, dw < 0f ? w : dw, dh < 0f ? h : dh);
        }

        /// <summary>From the sheet of the condition bubbles (ETCTIM.BIN at 23808, VRAM 960,384); row = colour y - 480, v as the game has it, 128 up.</summary>
        public void Condition(int row, float u, float v, float w, float h, float x, float y, float dw, float dh)
        {
            if (_cond[row] == null) _cond[row] = _condTim.ToTexture(row);
            Draw(_cond[row], u, v - 128f, w, h, x, y, dw, dh);
        }

        /// <summary>From the orders sheet; row = colour y - 496.</summary>
        public void Cmd(int row, float u, float v, float w, float h, float x, float y)
        {
            if (_cmd[row] == null) _cmd[row] = _cmdTim.ToTexture(row);
            Draw(_cmd[row], u, v, w, h, x, y, w, h);
        }

        public void Cursor(float x, float y) { Draw(_cursor, 61, 32, 22, 22, x, y, 22, 22); }

        /// <summary>The game's four-digit number: digits 7 x 11, at sheet row y 44.</summary>
        public void Number(int value, float x, float y)
        {
            string s = Mathf.Clamp(value, 0, 9999).ToString();
            for (int i = 0; i < s.Length; i++) Hud(12, (s[i] - '0') * 7, 44, 7, 11, x + (4 - s.Length + i) * 7, y);
        }

        /// <summary>drawEntityText: digits 8 x 8 at sheet row y 55, 7 apart, n places, right-aligned.</summary>
        public void Digits(int value, int n, float x, float y)
        {
            string s = Mathf.Clamp(value, 0, 9999).ToString();
            for (int i = 0; i < s.Length; i++) Hud(4, (s[i] - '0') * 8, 55, 8, 8, x + (n - s.Length + i) * 7, y);
        }

        /// <summary>renderFleeBubble: the balloon (24 x 24 at 184 + 24 x type, 0), turned so its tail points at the Digimon, and the face over it (16 x 16 at 200 + 16 x type, 24). Type 0 angry, 1 scared.</summary>
        public void Bubble(int type, float x, float y, float size, float degrees)
        {
            var c = new Vector3(MiddleX + x * Scale, MiddleY + y * Scale, 0f);
            Matrix4x4 m = GUI.matrix;
            GUI.matrix = m * Matrix4x4.TRS(c, Quaternion.Euler(0, 0, degrees), Vector3.one) * Matrix4x4.Translate(-c);
            Hud(7, 184 + type * 24, 0, 24, 24, x - size / 2f, y - size / 2f, size, size);
            GUI.matrix = m;
            float face = size * 2f / 3f;
            Hud(7, 200 + type * 16, 24, 16, 16, x - face / 2f, y - face / 2f, face, face);
        }

        /// <summary>The green outline of the enemy's life bar.</summary>
        public static void Line(float x0, float y0, float x1, float y1)
        {
            var a = new Vector2(MiddleX + x0 * Scale, MiddleY + y0 * Scale);
            var b = new Vector2(MiddleX + x1 * Scale, MiddleY + y1 * Scale);
            float len = (b - a).magnitude, deg = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            Matrix4x4 m = GUI.matrix; Color c = GUI.color;
            GUI.matrix = m * Matrix4x4.TRS(a, Quaternion.Euler(0, 0, deg), Vector3.one);
            GUI.color = new Color32(0x0A, 0xB4, 0x0A, 0xFF);
            GUI.DrawTexture(new Rect(-Scale / 2f, -Scale / 2f, len + Scale, Scale), Texture2D.whiteTexture);
            GUI.matrix = m; GUI.color = c;
        }

        /// <summary>The fill of the enemy's life bar (BTL_renderEnemyHPBars): the grey strip at 88, 44 of the orders sheet, width 0 to 50, tinted green to yellow to orange.</summary>
        public void LifeFill(int w, float x, float y)
        {
            if (w <= 0) return;
            if (_lifeFill == null)
            {
                _lifeFill = new Texture2D(50, 3, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                var all = _cmdTim.GetPixels(3);
                int sheetW = _cmdTim.Width, sheetH = _cmdTim.Height;
                _lifeGrey = new Color32[50 * 3];
                for (int v = 0; v < 3; v++) for (int u = 0; u < 50; u++) _lifeGrey[(2 - v) * 50 + u] = all[(sheetH - 1 - (44 + v)) * sheetW + 88 + u];
            }
            if (w != _lifeWidth)
            {
                _lifeWidth = w;
                int r = w >= 15 ? (50 - w) * 127 / 35 + 128 : 255;
                int g = w >= 15 ? 255 : w >= 5 ? 255 - (15 - w) * 127 / 10 : 128;
                var px = new Color32[_lifeGrey.Length];
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 c = _lifeGrey[i];
                    px[i] = new Color32((byte)Mathf.Min(255, c.r * r / 128), (byte)Mathf.Min(255, c.g * g / 128), c.b, 255);    // b is 128: unchanged
                }
                _lifeFill.SetPixels32(px); _lifeFill.Apply();
            }
            Draw(_lifeFill, 0, 0, w, 3, x, y, w, 3, 50f);
        }

        /// <summary>The word BATTLE lies tilted in space: this is the game's matrix and projection, for a point of the word.</summary>
        public static Vector2 WordPoint(float x, float y)
        {
            float z = 724f + y * 3422f / 4096f;
            return new Vector2(512f * x * 4106f / 4096f / z, 512f * (y * 2276f / 4096f - 2f) / z);
        }
    }
}
