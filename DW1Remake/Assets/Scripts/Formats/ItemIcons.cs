using System;
using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// The 128 item pictures from ETCDAT/ITEM.TIM, built at run time. The file is a grid of 16 x 8 icons of 16 x 16 pixels
    /// (item number = position in the grid) with 24 colour sets; the program file says which set each item uses.
    /// </summary>
    public sealed class ItemIcons
    {
        const uint PaletteAddr = 0x80127BDC;   // 128 bytes: colour set of each item
        const int Size = 16, Columns = 16, Rows = 8;

        public Texture2D Sheet;

        public static ItemIcons Load(DiscImage disc)
        {
            if (!disc.Exists("/ETCDAT/ITEM.TIM") || !disc.Exists("/SLUS_010.32")) return null;
            TimImage tim;
            if (!TimImage.TryParse(disc.ReadFile("/ETCDAT/ITEM.TIM"), 0, out tim)) return null;
            byte[] exe = disc.ReadFile("/SLUS_010.32");
            int table = (int)(PaletteAddr - BitConverter.ToUInt32(exe, 0x18) + 0x800);
            if (table < 0 || table + ItemCatalog.Count > exe.Length) return null;

            var sets = new Color32[tim.PaletteCount][];
            var px = new Color32[tim.Width * tim.Height];
            for (int id = 0; id < ItemCatalog.Count; id++)
            {
                int set = Mathf.Min(exe[table + id], tim.PaletteCount - 1);
                if (sets[set] == null) sets[set] = tim.GetPixels(set);
                int x0 = (id % Columns) * Size, y0 = tim.Height - Size - (id / Columns) * Size;   // Unity rows run bottom to top
                for (int y = 0; y < Size; y++)
                    Array.Copy(sets[set], (y0 + y) * tim.Width + x0, px, (y0 + y) * tim.Width + x0, Size);
            }
            var tex = new Texture2D(tim.Width, tim.Height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(px);
            tex.Apply();
            return new ItemIcons { Sheet = tex };
        }

        /// <summary>Where item 'id' sits on the sheet, in the 0-1 units GUI.DrawTextureWithTexCoords wants.</summary>
        public Rect Uv(int id)
        {
            float w = 1f / Columns, h = 1f / Rows;
            return new Rect((id % Columns) * w, 1f - (id / Columns + 1) * h, w, h);
        }
    }
}
