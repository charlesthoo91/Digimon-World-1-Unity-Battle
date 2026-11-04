using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The game's own shadow under every Digimon and dropped item (renderDropShadow in drop_shadow.c, renderDroppedItemShadow in item.c): a ground quad
    /// as wide as the Digimon's radius, with the 64 x 64 spot picture from ETCDAT/SPOT256.TIM (u 0x40, v 0x80 of page (832,256), colour set (0,487)),
    /// colour 0x30, subtracted from the picture (blend mode 2). Read from the player's disc at run time.
    /// </summary>
    public sealed class DropShadow
    {
        readonly Mesh _quad;
        readonly Material _mat;

        public bool Ready { get { return _mat != null; } }

        public DropShadow(DiscImage disc)
        {
            TimImage tim;
            if (disc == null || !disc.Exists("/ETCDAT/SPOT256.TIM") || !TimImage.TryParse(disc.ReadFile("/ETCDAT/SPOT256.TIM"), 0, out tim)) return;
            var sh = Shader.Find("DW1Remake/Effect");
            if (sh == null) return;
            // a unit quad (corners at -1 and +1 on the ground), picture coordinates from the top left
            _quad = new Mesh();
            _quad.vertices = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);
            _mat = new Material(sh);
            var tex = tim.ToTexture(0);
            tex.filterMode = FilterMode.Point;
            _mat.SetTexture("_MainTex", tex);
            // the 128 x 128 picture holds the spot in its right half, top quarter: u 0x40..0x7F, v 0..0x3F of its rows
            _mat.SetVector("_Rect", new Vector4(0.5f, 0f, 0.5f, 0.5f));
            // colour 0x30 of 0x80 = full brightness
            float c = 0x30 / 128f;
            _mat.SetColor("_Color", new Color(c, c, c, 1f));
            MmdModel.SetPsxBlend(_mat, 2);
            // AddPrim at ordering-table slot 0xFFD: behind everything but the map, so the item or Digimon over it is never darkened by it
            _mat.renderQueue = 2995;
        }

        /// <summary>One shadow this frame: the middle on the ground (Unity position), half its width in game units (the Digimon's radius).</summary>
        public void Draw(Vector3 ground, float radius)
        {
            if (_mat == null) return;
            float r = radius * 0.01f;
            Graphics.DrawMesh(_quad, Matrix4x4.TRS(new Vector3(ground.x, ground.y + 0.03f, ground.z), Quaternion.identity, new Vector3(r, 1f, r)), _mat, 0);
        }
    }
}
