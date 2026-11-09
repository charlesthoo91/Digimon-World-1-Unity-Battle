using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod "Remastered maps", ModSettings.RemasteredMaps; not in the original game): a bigger, smoothed and sharpened copy of a map
    /// picture or map piece, made on the GPU of the player's PC from their own disc each time a map loads (shader DW1Remake/Upscale).
    /// Nothing is saved or shipped. The copy covers exactly the same area as the original, so pieces, doors and the walking grid line up.
    /// </summary>
    public static class MapRemaster
    {
        static Material _mat;
        static bool _tried;

        public static bool On { get { return ModSettings.RemasteredMaps && Mat != null; } }

        static Material Mat
        {
            get
            {
                if (_mat == null && !_tried)
                {
                    _tried = true;
                    var sh = Shader.Find("DW1Remake/Upscale");
                    if (sh != null) _mat = new Material(sh); else Debug.LogWarning("[remaster] shader DW1Remake/Upscale not found");
                }
                return _mat;
            }
        }

        // 4 times bigger, less for big pictures: at most 4096 pixels on the long side; no copy when not even 2 times fits
        public static int ScaleFor(int w, int h)
        {
            int s = Mathf.Min(4, 4096 / Mathf.Max(1, Mathf.Max(w, h)));
            return s >= 2 ? s : 1;
        }

        /// <summary>A new enlarged copy of src (a RenderTexture), or null when the mod is off or the picture is too big.</summary>
        public static RenderTexture Make(Texture2D src)
        {
            if (!On || src == null) return null;
            int s = ScaleFor(src.width, src.height);
            if (s < 2) return null;
            var rt = new RenderTexture(src.width * s, src.height * s, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Remastered " + src.width + "x" + src.height
            };
            rt.Create();
            Into(src, rt);
            return rt;
        }

        /// <summary>Makes dst (from Make) again from src, after src's colours changed (time of day).</summary>
        public static void Into(Texture2D src, RenderTexture dst)
        {
            if (Mat == null || src == null || dst == null) return;
            var tmp = RenderTexture.GetTemporary(dst.width, dst.height, 0, RenderTextureFormat.ARGB32);
            Mat.SetVector("_SrcSize", new Vector4(src.width, src.height, 1f / src.width, 1f / src.height));
            Mat.SetFloat("_Sharp", 0.6f);
            Graphics.Blit(src, tmp, Mat, 0);
            Mat.SetVector("_SrcSize", new Vector4(dst.width, dst.height, 1f / dst.width, 1f / dst.height));
            Graphics.Blit(tmp, dst, Mat, 1);
            RenderTexture.ReleaseTemporary(tmp);
        }
    }
}
