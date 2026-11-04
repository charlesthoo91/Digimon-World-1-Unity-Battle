using System.Collections;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// playMovie(id, 1) -> startMovie -> MOV_playMovie (MOV_REL.BIN, loaded at 0x80010000). The movie table at 0x8001094C has 40-byte
    /// entries: the file name (32), the first frame, the last frame: 0 OP1.STR (title), 1 OP2.STR (after NEW GAME), 2 ED2.STR (CONTINUE of a
    /// game clear save), 3 EDR.STR (the ending). The picture (320 x 176) is put 32 lines down in the 320 x 240 screen, 24-bit; Start (0x800)
    /// stops movies 0-2 (not 3); the movie ends after its last frame. Frames follow the sound (double-speed CD, 150 sectors a second).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const uint MovBase = 0x80010000, MovTable = 0x8001094C;

        sealed class MovieState { public Texture2D Tex; public Color32[] Px; public int Height; }
        MovieState _movie;
        int _mainState = 1;                  // MAIN_STATE: 0 new game, 1 continue, 2 after the game clear save (saved with the game)

        IEnumerator PlayMovie(int id)
        {
            if (_disc == null) yield break;
            if (!_disc.IsRaw) { Debug.LogWarning("[movie] movies need a .bin disc image (raw 2352-byte sectors); movie " + id + " skipped"); yield break; }
            byte[] mov = _disc.Exists("/MOV_REL.BIN") ? _disc.ReadFile("/MOV_REL.BIN") : null;
            if (mov == null) yield break;
            int e = (int)(MovTable - MovBase) + id * 40;
            int n = 0; while (n < 32 && mov[e + n] != 0) n++;
            string path = System.Text.Encoding.ASCII.GetString(mov, e, n).Replace('\\', '/');
            int semi = path.IndexOf(';'); if (semi >= 0) path = path.Substring(0, semi);
            int last = System.BitConverter.ToInt32(mov, e + 36);
            StrMovie m = null;
            try { m = StrMovie.Open(_disc, path, last); }
            catch (System.Exception ex) { Debug.LogWarning("[movie] " + path + ": " + ex.Message); }
            if (m == null || m.FrameDue.Count == 0) yield break;

            if (_audio != null) _audio.StopMusic();       // finalizeMusic
            var st = new MovieState { Tex = new Texture2D(m.Width, m.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp },
                                      Px = new Color32[m.Width * m.Height], Height = m.Height };
            for (int i = 0; i < st.Px.Length; i++) st.Px[i] = new Color32(0, 0, 0, 255);
            st.Tex.SetPixels32(st.Px); st.Tex.Apply();
            _movie = st;
            AudioSource src = null; AudioClip clip = null;
            if (m.Audio != null && m.Audio.Length > 0)
            {
                clip = AudioClip.Create("movie " + id, m.Audio.Length / m.Channels, m.Channels, m.Rate, false);
                clip.SetData(m.Audio, 0);
                src = gameObject.AddComponent<AudioSource>();
                src.clip = clip; src.playOnAwake = false; src.spatialBlend = 0f;
            }
            float t0 = Time.unscaledTime, audioAt = m.FirstAudioSector >= 0 ? m.FirstAudioSector / (float)StrMovie.SectorsPerSecond : 0f;
            bool audioOn = false;
            int shown = -1;
            yield return null;
            while (true)
            {
                float t = Time.unscaledTime - t0;
                if (src != null && !audioOn && t >= audioAt) { src.Play(); audioOn = true; }
                if (audioOn && src.isPlaying) t = audioAt + src.time;          // the picture keeps to the sound
                int k = shown;
                while (k + 1 < m.FrameDue.Count && (m.FrameDue[k + 1] + 1) / (float)StrMovie.SectorsPerSecond <= t) k++;
                if (k != shown)
                {
                    if (m.Decode(k, st.Px)) { st.Tex.SetPixels32(st.Px); st.Tex.Apply(); }
                    shown = k;
                }
                if (shown >= m.FrameDue.Count - 1) { yield return null; break; }      // the last frame (MOVIE_COMPLETED)
                if (id != 3 && GameMenus.StartKey()) break;                       // PadRead(1) & 0x800, not for movie 3
                yield return null;
            }
            if (src != null) { src.Stop(); Destroy(src); }
            if (clip != null) Destroy(clip);
            Destroy(st.Tex);
            _movie = null;
        }

        // MOV_REL: the picture at y 32 of the 320 x 240 screen, black round it (ClearImage)
        void DrawMovie()
        {
            UiKit.Fill(new Rect(0, 0, Screen.width, Screen.height), Color.black);
            if (_movie == null || _movie.Tex == null) return;
            Rect p = UiKit.Picture;
            float k = p.height / 240f;
            GUI.DrawTexture(new Rect(p.x, p.y + 32f * k, p.width, _movie.Height * k), _movie.Tex, ScaleMode.StretchToFill);
        }
    }
}
