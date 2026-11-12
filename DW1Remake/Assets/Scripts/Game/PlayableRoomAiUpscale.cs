using System.Collections;
using System.IO;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod AI UPSCALE MAPS, off by default, PC only): the map's painted picture is upscaled on the player's own graphics card by an
    /// AI model (NcnnUpscaler): Upscayl's Remacri, read from the player's Upscayl install (no model ships with the game, user's decision
    /// 2026-11-12). The disc's picture shows at once; the
    /// sharp one takes its place when it is ready, and is kept in the game's own data folder (Application.persistentDataPath/AiUpscale)
    /// so a map is upscaled only once per colour set. Nothing made from the disc is part of the game: every player makes their own.
    /// Prototype: only the backdrop; foreground pieces, objects and Digimon keep the disc's pictures.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        NcnnUpscaler _ai; bool _aiTried; string _aiError;
        Coroutine _aiJob; string _aiJobKey;
        float _aiProgress = -1f;

        // where Upscayl puts its models (Remacri is read from there)
        static string FindUpscaylModel(string name)
        {
            var dirs = new[]
            {
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "Upscayl"),
                Path.Combine(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Programs"), "Upscayl"),
                Path.Combine(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Programs"), "upscayl"),
            };
            foreach (var d in dirs)
            {
                string p = Path.Combine(Path.Combine(Path.Combine(d, "resources"), "models"), name);
                if (File.Exists(p + ".param") && File.Exists(p + ".bin")) return p;
            }
            return null;
        }

        bool AiReady()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            if (_ai != null) return true;
            if (_aiTried) return false;
            _aiTried = true;
            if (!SystemInfo.supportsComputeShaders) { _aiError = "this graphics card cannot run compute shaders"; return false; }
            string path = FindUpscaylModel("remacri-4x");
            if (path == null) { _aiError = "Upscayl's remacri-4x model was not found (Program Files\\Upscayl\\resources\\models)"; return false; }
            float t0 = Time.realtimeSinceStartup;
            _ai = NcnnUpscaler.Load(path, out _aiError);
            if (_ai != null) Debug.Log("[aiupscale] " + _ai.Name + " ready (x" + _ai.Scale + ") in " + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s");
            return _ai != null;
#endif
        }

        static string AiCacheFolder { get { return Path.Combine(Application.persistentDataPath, "AiUpscale"); } }

        // BuildBackdrop and the colour-set change: the sharp picture of this map and colour set, from the cache or made now
        // a job in progress is never stopped from outside (StopCoroutine would leave the network half way through a run): it sees that the
        // map or colour set changed, finishes its current tile and ends; a new job waits for it (_aiBusy)
        bool _aiBusy;

        void AiUpscaleBackdrop()
        {
            if (!ModSettings.AiUpscale || _map == null || _backdrop == null) return;
            if (!AiReady()) { Debug.LogWarning("[aiupscale] off: " + _aiError); return; }
            // pictures of maps left behind are gone: forget their disc pixels
            var gone = new System.Collections.Generic.List<Texture2D>();
            foreach (var t in _aiDiscPx.Keys) if (t == null) gone.Add(t);
            foreach (var t in gone) { _aiDiscPx.Remove(t); _aiDiscSize.Remove(t); }
            StartCoroutine(AiPiecesJob(MapPath, _curPalette));      // the map's small pictures, after it is in
            string key = Path.GetFileNameWithoutExtension(MapPath).ToUpperInvariant() + "_P" + _curPalette + "_" + _ai.Name;
            string file = Path.Combine(AiCacheFolder, key + ".png");
            if (File.Exists(file))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (tex.LoadImage(File.ReadAllBytes(file)) && tex.width == _map.PixelWidth * _ai.Scale && tex.height == _map.PixelHeight * _ai.Scale)
                {
                    SetBackdropPicture(tex);
                    Debug.Log("[aiupscale] " + key + " from the cache");
                    return;
                }
                Destroy(tex);
            }
            if (_aiJob != null && _aiJobKey == key) return;      // already on its way
            _aiJobKey = key;
            _aiJob = StartCoroutine(AiUpscaleJob(key, file, MapPath, _curPalette));
        }

        IEnumerator AiUpscaleJob(string key, string file, string mapPath, int palette)
        {
            while (_aiBusy) yield return null;
            if (MapPath != mapPath || _curPalette != palette || _map == null || _backdrop == null) yield break;
            _aiBusy = true;
            try { yield return AiUpscaleRun(key, file, mapPath, palette); }
            finally { _aiBusy = false; }
        }

        IEnumerator AiUpscaleRun(string key, string file, string mapPath, int palette)
        {
            // the disc's picture, rows from the top
            var pic = _map.BuildTexture(palette);
            var bottomUp = pic.GetPixels32(); int w = pic.width, h = pic.height;
            Destroy(pic);
            var src = new Color32[w * h];
            for (int y = 0; y < h; y++) System.Array.Copy(bottomUp, (h - 1 - y) * w, src, y * w, w);
            float t0 = Time.realtimeSinceStartup;
            Color32[] result = null;
            _aiProgress = 0f;
            yield return _ai.Run(src, w, h, AiBudgetMs, () => MapPath != mapPath || _curPalette != palette, p => _aiProgress = p, r => result = r);
            _aiProgress = -1f;
            if (_aiJobKey == key) { _aiJob = null; _aiJobKey = null; }
            if (result == null || MapPath != mapPath || _curPalette != palette) yield break;
            int s = _ai.Scale, ow = w * s, oh = h * s;
            var flipped = new Color32[ow * oh];
            for (int y = 0; y < oh; y++) System.Array.Copy(result, y * ow, flipped, (oh - 1 - y) * ow, ow);
            var tex = new Texture2D(ow, oh, TextureFormat.RGBA32, true);
            tex.SetPixels32(flipped); tex.Apply(true);
            Debug.Log("[aiupscale] " + key + ": " + w + " x " + h + " -> " + ow + " x " + oh + " in " + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s");
            try { Directory.CreateDirectory(AiCacheFolder); File.WriteAllBytes(file, tex.EncodeToPNG()); }
            catch (System.Exception e) { Debug.LogWarning("[aiupscale] not kept: " + e.Message); }
            SetBackdropPicture(tex);
        }

        // the backdrop shows the upscaled picture; its blur and sharpening steps stay one pixel of the disc's picture (_Texel)
        void SetBackdropPicture(Texture2D tex)
        {
            tex.filterMode = FilterMode.Trilinear; tex.wrapMode = TextureWrapMode.Clamp; tex.anisoLevel = 4;
            var mat = _backdrop.GetComponent<MeshRenderer>().sharedMaterial;
            var old = mat.GetTexture("_BaseMap");
            mat.SetTexture("_BaseMap", tex);
            if (old != null && old != tex) Destroy(old);
        }

        void AiUpscaleDispose() { if (_ai != null) _ai.Dispose(); _ai = null; MmdModel.AtlasMade = null; TimImage.Made = null; }

        // how much of each frame the network may take: about 60% of a frame at the game's frame rate (30 ms of the 50 ms at 20 a second,
        // 10 ms at 60), so a fast card finishes sooner and a slow one leaves the game moving
        static float AiBudgetMs { get { int fps = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60; return 600f / Mathf.Max(20, fps); } }

        // ------------------------------------------------------------------ Digimon and the boy
        // Every model's texture sheet (MmdModel.Build: ALLTIM.TIM, one 128-row block per colour set its faces use) is upscaled once per
        // Digimon type and kept as CHR<type>_<model>.png in the same folder; every copy of that Digimon then uses it. Its see-through texels
        // (colour 0, cut out by the alpha test) stay exactly the disc's: one disc texel's yes / no for its block of upscaled texels.

        readonly System.Collections.Generic.Dictionary<int, Texture2D> _aiChr = new System.Collections.Generic.Dictionary<int, Texture2D>();
        readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Material>> _aiChrWaiting = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Material>>();

        // MmdModel.AtlasMade
        void AiChrSheet(int type, Texture2D atlas, Material mat)
        {
            if (!ModSettings.AiUpscale || atlas == null || mat == null) return;
            Texture2D hd;
            if (_aiChr.TryGetValue(type, out hd) && hd != null) { SetChrPicture(mat, hd); return; }
            if (!AiReady()) return;
            string key = "CHR" + type.ToString("000") + "_" + _ai.Name, file = Path.Combine(AiCacheFolder, key + ".png");
            int s = _ai.Scale;
            if (File.Exists(file))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (tex.LoadImage(File.ReadAllBytes(file)) && tex.width == atlas.width * s && tex.height == atlas.height * s)
                {
                    PrepareChrPicture(tex, atlas.GetPixels32(), atlas.width, s);
                    _aiChr[type] = tex; SetChrPicture(mat, tex);
                    return;
                }
                Destroy(tex);
            }
            System.Collections.Generic.List<Material> list;
            if (_aiChrWaiting.TryGetValue(type, out list)) { list.Add(mat); return; }      // already on its way: this copy gets it too
            _aiChrWaiting[type] = new System.Collections.Generic.List<Material> { mat };
            StartCoroutine(AiChrJob(type, atlas.GetPixels32(), atlas.width, atlas.height, key, file));
        }

        IEnumerator AiChrJob(int type, Color32[] bottomUp, int w, int h, string key, string file)
        {
            while (_aiBusy) yield return null;
            _aiBusy = true;
            Color32[] result = null;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                // rows from the top; see-through texels take the colour of the nearest visible one, so the network does not darken the edges
                var src = new Color32[w * h];
                for (int y = 0; y < h; y++) System.Array.Copy(bottomUp, (h - 1 - y) * w, src, y * w, w);
                FillSeeThrough(src, w, h);
                yield return _ai.Run(src, w, h, AiBudgetMs, () => false, null, r => result = r);
            }
            finally { _aiBusy = false; }
            System.Collections.Generic.List<Material> mats;
            _aiChrWaiting.TryGetValue(type, out mats); _aiChrWaiting.Remove(type);
            if (result == null) yield break;
            int s = _ai.Scale, ow = w * s, oh = h * s;
            var flipped = new Color32[ow * oh];
            for (int y = 0; y < oh; y++) System.Array.Copy(result, y * ow, flipped, (oh - 1 - y) * ow, ow);
            var tex = new Texture2D(ow, oh, TextureFormat.RGBA32, true);
            tex.SetPixels32(flipped); tex.Apply(false);
            try { Directory.CreateDirectory(AiCacheFolder); File.WriteAllBytes(file, tex.EncodeToPNG()); }
            catch (System.Exception e) { Debug.LogWarning("[aiupscale] not kept: " + e.Message); }
            PrepareChrPicture(tex, bottomUp, w, s);
            Debug.Log("[aiupscale] " + key + ": " + w + " x " + h + " -> " + ow + " x " + oh + " in " + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s");
            _aiChr[type] = tex;
            if (mats != null) foreach (var m in mats) if (m != null) SetChrPicture(m, tex);
        }

        // the disc sheet's see-through (alpha 0) per texel onto the upscaled one (rows from the bottom in both), then mipmaps and smooth sampling
        static void PrepareChrPicture(Texture2D tex, Color32[] discPx, int discW, int s)
        {
            var px = tex.GetPixels32();
            int w = tex.width;
            for (int y = 0; y < tex.height; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = Masked(px[y * w + x], AlphaAt(discPx, discW, discPx.Length / discW, x, y, s));
            tex.SetPixels32(px); tex.Apply(true);
            tex.filterMode = FilterMode.Trilinear; tex.wrapMode = TextureWrapMode.Clamp; tex.anisoLevel = 4;
        }

        static void SetChrPicture(Material m, Texture2D tex)
        {
            m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        }

        // the see-through of an upscaled texel: the disc's see-through (0 or 255 per disc texel, rows from the bottom) sampled smoothly at the
        // texel's place, then cut at the middle with one texel of soft edge, so outlines are not stair steps of the disc's big pixels
        // A blended layer (STP colours: added, subtracted, halved) keeps the disc's hard edges (smooth = false): its edge texels would carry
        // colour at part see-through, and an added sprite adds that colour in full (white rims on the sleepy bubble's Z's)
        static byte AlphaAt(Color32[] disc, int dw, int dh, int x, int y, int s, bool smooth = true)
        {
            if (!smooth) return disc[Mathf.Min(y / s, dh - 1) * dw + Mathf.Min(x / s, dw - 1)].a;
            float fx = (x + 0.5f) / s - 0.5f, fy = (y + 0.5f) / s - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, dw - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, dh - 1);
            int x1 = Mathf.Min(x0 + 1, dw - 1), y1 = Mathf.Min(y0 + 1, dh - 1);
            float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
            float a = Mathf.Lerp(Mathf.Lerp(disc[y0 * dw + x0].a, disc[y0 * dw + x1].a, tx), Mathf.Lerp(disc[y1 * dw + x0].a, disc[y1 * dw + x1].a, tx), ty) / 255f;
            // a ramp over about one upscaled texel round the half-way line
            float k = Mathf.Clamp01((a - 0.5f) * s + 0.5f);
            return (byte)Mathf.RoundToInt(k * 255f);
        }

        // a fully see-through texel keeps no colour (as on the disc: colour 0): additive and subtractive sprites add or take away the
        // colour whatever its see-through, so the colour the edges were filled with before upscaling must go (it washed the bubble out)
        static Color32 Masked(Color32 c, byte a) { return a == 0 ? new Color32(0, 0, 0, 0) : new Color32(c.r, c.g, c.b, a); }

        // see-through texels (alpha 0) take the colour of a visible neighbour, 8 steps out; the rest black
        static void FillSeeThrough(Color32[] px, int w, int h)
        {
            var filled = new bool[px.Length];
            for (int i = 0; i < px.Length; i++) filled[i] = px[i].a != 0;
            for (int step = 0; step < 8; step++)
            {
                var next = (bool[])filled.Clone();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (filled[i]) continue;
                        int j = x > 0 && filled[i - 1] ? i - 1 : x < w - 1 && filled[i + 1] ? i + 1 : y > 0 && filled[i - w] ? i - w : y < h - 1 && filled[i + w] ? i + w : -1;
                        if (j < 0) continue;
                        px[i] = px[j]; next[i] = true;
                    }
                filled = next;
            }
            for (int i = 0; i < px.Length; i++) if (!filled[i]) px[i] = new Color32(0, 0, 0, 255);
        }
    
        // ------------------------------------------------------------------ the map's small pictures
        // Every foreground piece of the map (each animation frame is a piece of its own; both STP layers) and every page of the map's video
        // memory its models read (doors, warp crystals...) go on one sheet, which is upscaled once per map and colour set and kept as
        // <MAP>_PIECES_P<n>_<model>.png. Each texture then becomes its sharp version in place (Texture2D.Reinitialize: the same object, so
        // the materials and animations need no change; they all use texture places 0..1). See-through stays per disc texel.
        // The chest and the boulder (their own TIM files) are made sharp with the other TIM pictures (AiTim).

        const int SheetW = 1024, SheetPad = 4;
        readonly System.Collections.Generic.Dictionary<Texture2D, Color32[]> _aiDiscPx = new System.Collections.Generic.Dictionary<Texture2D, Color32[]>();
        readonly System.Collections.Generic.Dictionary<Texture2D, Vector2Int> _aiDiscSize = new System.Collections.Generic.Dictionary<Texture2D, Vector2Int>();

        // the disc's pixels of a texture (rows from the bottom), kept the first time: an upscaled texture no longer has them
        Color32[] DiscPixels(Texture2D t, out int w, out int h)
        {
            Color32[] px; Vector2Int sz;
            if (_aiDiscPx.TryGetValue(t, out px) && _aiDiscSize.TryGetValue(t, out sz)) { w = sz.x; h = sz.y; return px; }
            px = t.GetPixels32(); w = t.width; h = t.height;
            _aiDiscPx[t] = px; _aiDiscSize[t] = new Vector2Int(w, h);
            return px;
        }

        /// <summary>RefreshOverlayPalette: the texture is the disc's again, at the disc's size (its new colours are then written by the caller).</summary>
        void AiForget(Texture2D t, int w, int h)
        {
            _aiDiscPx.Remove(t); _aiDiscSize.Remove(t);
            if (t.width != w || t.height != h) { t.Reinitialize(w, h, TextureFormat.RGBA32, false); t.filterMode = FilterMode.Bilinear; }
        }

        // after the map is in (one frame later: its pieces and doors are made by then)
        IEnumerator AiPiecesJob(string mapPath, int palette)
        {
            yield return null;
            if (MapPath != mapPath || _curPalette != palette || _ai == null) yield break;
            var items = new System.Collections.Generic.List<Texture2D>();
            if (_overlay != null)
            {
                // every piece any placement shows, with the layers it is drawn with (as BuildOverlays / ShowOverlayFrame)
                var used = new System.Collections.Generic.SortedSet<int>();
                foreach (var inst in _overlay.Instances)
                    for (int k = 0; k < 8 && inst.Frames[k] != -1; k++) if (inst.Frames[k] >= 0 && inst.Frames[k] < _overlay.Objects.Length) used.Add(inst.Frames[k]);
                foreach (int obj in used) { if (_overlay.IsSemi(obj)) { GetOverlayMaterial(obj, 1); GetOverlayMaterial(obj, 2); } else GetOverlayMaterial(obj, 0); }
                var keys = new System.Collections.Generic.List<int>(_ovTex.Keys); keys.Sort();
                foreach (int k in keys) if (_ovTex[k] != null) { items.Add(_ovTex[k]); if (k % 4 == 2) _aiHardAlpha.Add(_ovTex[k]); }
            }
            var pk = new System.Collections.Generic.List<long>(_pageTex.Keys); pk.Sort();
            foreach (long k in pk) if (_pageTex[k] != null) { items.Add(_pageTex[k]); if ((k & 3) == 2) _aiHardAlpha.Add(_pageTex[k]); }
            if (items.Count == 0) yield break;
            string key = Path.GetFileNameWithoutExtension(mapPath).ToUpperInvariant() + "_PIECES_P" + palette + "_" + _ai.Name;
            yield return AiSheetJob(key, items, () => MapPath != mapPath || _curPalette != palette);
        }

        // one sheet: the pictures in rows from the top (tallest first, then in the order given), 4 free pixels round each; upscaled (or
        // read from the cache), then each picture takes its part, with its own see-through per disc texel
        IEnumerator AiSheetJob(string key, System.Collections.Generic.List<Texture2D> items, System.Func<bool> stop)
        {
            int n = items.Count;
            var px = new Color32[n][]; var ws = new int[n]; var hs = new int[n];
            for (int i = 0; i < n; i++) px[i] = DiscPixels(items[i], out ws[i], out hs[i]);
            var order = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++) if (ws[i] > 0 && hs[i] > 0 && ws[i] + 2 * SheetPad <= SheetW) order.Add(i);
            order.Sort((a, b) => hs[a] != hs[b] ? hs[b].CompareTo(hs[a]) : a.CompareTo(b));
            var rx = new int[n]; var ry = new int[n];
            int x = 0, y = 0, rowH = 0;
            foreach (int i in order)
            {
                int w = ws[i] + 2 * SheetPad, h = hs[i] + 2 * SheetPad;
                if (x + w > SheetW) { y += rowH; x = 0; rowH = 0; }
                rx[i] = x + SheetPad; ry[i] = y + SheetPad;
                x += w; rowH = Mathf.Max(rowH, h);
            }
            int sheetH = y + rowH;
            if (sheetH <= 0) yield break;
            int s = _ai.Scale, sw = SheetW * s, sh = sheetH * s;
            string file = Path.Combine(AiCacheFolder, key + ".png");
            Color32[] hd = null;        // rows from the top
            if (File.Exists(file))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(File.ReadAllBytes(file)) && tex.width == sw && tex.height == sh)
                {
                    var bu = tex.GetPixels32(); hd = new Color32[sw * sh];
                    for (int r = 0; r < sh; r++) System.Array.Copy(bu, (sh - 1 - r) * sw, hd, r * sw, sw);
                    Debug.Log("[aiupscale] " + key + " from the cache");
                }
                Destroy(tex);
            }
            if (hd == null)
            {
                var src = new Color32[SheetW * sheetH];
                foreach (int i in order)
                    for (int r = 0; r < hs[i]; r++)
                        for (int c = 0; c < ws[i]; c++)
                            src[(ry[i] + r) * SheetW + rx[i] + c] = px[i][(hs[i] - 1 - r) * ws[i] + c];
                FillSeeThrough(src, SheetW, sheetH);
                while (_aiBusy) yield return null;
                if (stop()) yield break;
                _aiBusy = true;
                float t0 = Time.realtimeSinceStartup;
                try { yield return _ai.Run(src, SheetW, sheetH, AiBudgetMs, stop, null, r => hd = r); }
                finally { _aiBusy = false; }
                if (hd == null) yield break;
                var save = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
                var bu = new Color32[sw * sh];
                for (int r = 0; r < sh; r++) System.Array.Copy(hd, r * sw, bu, (sh - 1 - r) * sw, sw);
                save.SetPixels32(bu); save.Apply(false);
                try { Directory.CreateDirectory(AiCacheFolder); File.WriteAllBytes(file, save.EncodeToPNG()); }
                catch (System.Exception e) { Debug.LogWarning("[aiupscale] not kept: " + e.Message); }
                Destroy(save);
                Debug.Log("[aiupscale] " + key + ": " + n + " pictures on a " + SheetW + " x " + sheetH + " sheet in " + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s");
            }
            if (stop()) yield break;
            foreach (int i in order)
            {
                var t = items[i];
                if (t == null || !_aiDiscPx.ContainsKey(t)) continue;      // made the disc's again meanwhile (another colour set)
                int w = ws[i] * s, h = hs[i] * s;
                var outPx = new Color32[w * h];
                for (int r = 0; r < h; r++)
                    for (int c = 0; c < w; c++)
                    {
                        Color32 col = hd[(ry[i] * s + (h - 1 - r)) * sw + rx[i] * s + c];
                        outPx[r * w + c] = Masked(col, AlphaAt(px[i], ws[i], hs[i], c, r, s, !_aiHardAlpha.Contains(t)));
                    }
                t.Reinitialize(w, h, TextureFormat.RGBA32, true);
                t.SetPixels32(outPx); t.Apply(true);
                t.filterMode = FilterMode.Trilinear; t.anisoLevel = 4;
            }
        }
    
        // ------------------------------------------------------------------ every other picture (TimImage.Made)
        // HUD, menus, clock, icons, technique effects, piles, the egg, event pictures...: every texture made from a TIM file is queued and,
        // when its turn comes, upscaled from its pixels at that moment (so colours changed after it was made count) and made sharp in place
        // (Texture2D.Reinitialize). TimImage.SetLogical keeps the disc's size for the code that works in disc pixels (TimImage.W / H).
        // Kept as TIM_<hash of its pixels>_<model>.png. Pictures under 8 pixels a side are left as they are.

        readonly System.Collections.Generic.Queue<Texture2D> _aiTimQueue = new System.Collections.Generic.Queue<Texture2D>();
        readonly System.Collections.Generic.HashSet<Texture2D> _aiTimQueued = new System.Collections.Generic.HashSet<Texture2D>();
        readonly System.Collections.Generic.Dictionary<uint, Color32[]> _aiTimDone = new System.Collections.Generic.Dictionary<uint, Color32[]>();
        bool _aiTimRunning;

        // TimImage.Made
        // textures of a blended layer (TimImage layer 2, piece and page layer 2): hard edges (AlphaAt)
        readonly System.Collections.Generic.HashSet<Texture2D> _aiHardAlpha = new System.Collections.Generic.HashSet<Texture2D>();

        void AiTim(Texture2D t, int layer)
        {
            if (layer == 2 && t != null) _aiHardAlpha.Add(t);
            if (!ModSettings.AiUpscale || t == null || t.width < 8 || t.height < 8 || _aiTimQueued.Contains(t)) return;
            if (!AiReady()) return;
            _aiTimQueued.Add(t); _aiTimQueue.Enqueue(t);
            if (!_aiTimRunning) StartCoroutine(AiTimWorker());
        }

        IEnumerator AiTimWorker()
        {
            _aiTimRunning = true;
            yield return null;       // the code that made it may still change its pixels this frame (the cyan spot)
            while (_aiTimQueue.Count > 0)
            {
                var t = _aiTimQueue.Dequeue();
                _aiTimQueued.Remove(t);
                if (t == null || TimImage.W(t) != t.width) continue;      // gone, or already sharp
                var bu = t.GetPixels32();
                int w = t.width, h = t.height, s = _ai.Scale;
                uint hash = 2166136261;
                foreach (var c in bu) { hash = (hash ^ c.r) * 16777619; hash = (hash ^ c.g) * 16777619; hash = (hash ^ c.b) * 16777619; hash = (hash ^ c.a) * 16777619; }
                hash = (hash ^ (uint)w) * 16777619; hash = (hash ^ (uint)h) * 16777619;
                Color32[] hd;        // rows from the bottom, w * s by h * s
                if (!_aiTimDone.TryGetValue(hash, out hd))
                {
                    string file = Path.Combine(AiCacheFolder, "TIM_" + hash.ToString("X8") + "_" + _ai.Name + ".png");
                    if (File.Exists(file))
                    {
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (tex.LoadImage(File.ReadAllBytes(file)) && tex.width == w * s && tex.height == h * s) hd = tex.GetPixels32();
                        Destroy(tex);
                    }
                    if (hd == null)
                    {
                        var src = new Color32[w * h];
                        for (int y = 0; y < h; y++) System.Array.Copy(bu, (h - 1 - y) * w, src, y * w, w);
                        FillSeeThrough(src, w, h);
                        while (_aiBusy) yield return null;
                        _aiBusy = true;
                        Color32[] result = null;
                        try { yield return _ai.Run(src, w, h, AiBudgetMs, () => false, null, r => result = r); }
                        finally { _aiBusy = false; }
                        if (result == null) continue;
                        int ow = w * s, oh = h * s;
                        hd = new Color32[ow * oh];
                        for (int y = 0; y < oh; y++) System.Array.Copy(result, y * ow, hd, (oh - 1 - y) * ow, ow);
                        var save = new Texture2D(ow, oh, TextureFormat.RGBA32, false);
                        save.SetPixels32(hd); save.Apply(false);
                        try { Directory.CreateDirectory(AiCacheFolder); File.WriteAllBytes(file, save.EncodeToPNG()); }
                        catch (System.Exception e) { Debug.LogWarning("[aiupscale] not kept: " + e.Message); }
                        Destroy(save);
                    }
                    _aiTimDone[hash] = hd;
                }
                if (t == null || TimImage.W(t) != t.width || t.width != w || t.height != h) continue;
                int W2 = w * s, H2 = h * s;
                var outPx = new Color32[W2 * H2];
                for (int y = 0; y < H2; y++)
                    for (int x = 0; x < W2; x++)
                    {
                        Color32 c = hd[y * W2 + x];
                        outPx[y * W2 + x] = Masked(c, AlphaAt(bu, w, h, x, y, s, !_aiHardAlpha.Contains(t)));
                    }
                var wrap = t.wrapMode;
                t.Reinitialize(W2, H2, TextureFormat.RGBA32, false);
                t.SetPixels32(outPx); t.Apply(false);
                t.filterMode = FilterMode.Bilinear; t.wrapMode = wrap;
                TimImage.SetLogical(t, w, h);
                yield return null;
            }
            _aiTimRunning = false;
        }
    }
}
