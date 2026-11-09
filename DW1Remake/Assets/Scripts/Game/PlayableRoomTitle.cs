using System.Collections;
using System.IO;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The title screens (main.c main loop): runLandingScreen ("PRESS START" over ETCNA/TITLE256.TIM) and runMainMenu with menu view 0
    /// (renderInitialMenu over ETCNA/TITLE2.TIM, US layout). Our save file stands in for the memory card: CONTINUE GAME and DELETE GAME
    /// are dimmed when there is no save file, as the game dims them with no card. The opening movies (playMovie 0 / 1) are not played.
    /// These loops run once a video frame (VSync(0)), counted here at 60 a second.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        [Tooltip("Start at the title screen (PRESS START, then NEW GAME / CONTINUE GAME). Ticking Quick Start, New Game or Load Save On Start skips it for testing.")]
        public bool TitleScreen = true;

        bool WantTitle { get { return TitleScreen && !QuickStart && !NewGame && !LoadSaveOnStart; } }

        sealed class TitleState
        {
            public Texture2D Picture;
            public int PictureHeight;
            public bool Landing = true, Pressed;
            public int Counter;               // FIRST_SCREEN_FRAME_COUNTER
            public float Black = 1f;         // the fade, 0 clear to 1 black
        }
        TitleState _title;
        float _titleAcc;

        // whole video frames (1/60 s) passed since the last call
        int TitleTicks()
        {
            _titleAcc += Time.unscaledDeltaTime;
            int n = (int)(_titleAcc * 60f);
            _titleAcc -= n / 60f;
            return Mathf.Min(n, 4);
        }

        Texture2D TitlePicture(string path, out int height)
        {
            height = 240;
            TimImage tim;
            byte[] d = _disc.ReadFile(path);
            if (d == null || !TimImage.TryParse(d, 0, out tim)) return null;
            height = tim.Height;
            return tim.ToTexture(0);         // colour set row 480: the TIM's own (setClut(0, 480))
        }

        // fadeFromBlack / fadeToBlack (0x28 frames)
        IEnumerator TitleFade(bool toBlack)
        {
            var t = _title;
            for (int f = 0; f < 0x28;)
            {
                int n = TitleTicks();
                for (int i = 0; i < n; i++) { f++; t.Counter++; if (t.Pressed) t.Counter += 0x1D; }
                t.Black = toBlack ? f / 40f : 1f - f / 40f;
                yield return null;
            }
            t.Black = toBlack ? 1f : 0f;
        }

        IEnumerator Title()
        {
            var t = _title;
            _titleAcc = 0f;
            // runLandingScreen: 0x258 frames for Start (sound 3), then the fade; with no Start the game plays the opening movie and comes back
            t.Picture = TitlePicture("/ETCNA/TITLE256.TIM", out t.PictureHeight);
            while (!t.Pressed)
            {
                yield return PlayMovie(0);          // main loop: playMovie(0, 1) before every landing screen
                _titleAcc = 0f;
                t.Landing = true; t.Counter = 0;
                yield return TitleFade(false);
                for (int timer = 0x258; timer > 0 && !t.Pressed;)
                {
                    int n = TitleTicks();
                    for (int i = 0; i < n; i++) { timer--; t.Counter++; }
                    if (GameMenus.StartKey()) { Snd(3); t.Pressed = true; }      // POLLED_INPUT & 0x800: Start only
                    yield return null;
                }
                yield return TitleFade(true);
            }

            // runMainMenu: TITLE2.TIM, view 0
            t.Landing = false; t.Pressed = false;
            if (t.Picture != null) Destroy(t.Picture);
            t.Picture = TitlePicture("/ETCNA/TITLE2.TIM", out t.PictureHeight);
            yield return TitleFade(false);
            int pick = -1;
            while (pick < 0)
            {
                bool hasSave = File.Exists(SavePath);
                var s = InitialMenu(hasSave);
                int answer = 0;
                yield return Ask(s, a => answer = a);
                if (answer != 1) continue;      // Circle / Triangle: nothing
                if (s.Pos == 0) pick = 0;                        // NEW GAME
                else if (s.Pos == 1 && hasSave) pick = 1;        // CONTINUE GAME
                else if (s.Pos == 2 && hasSave)                  // DELETE GAME
                {
                    var c = YesNoScreen(SlotTitle(2), 0x46, 0x3C, 0x57, 0x63, 67, 87);
                    c.Box(0x3C, 0x32, 0xBC, 0x20); c.Box(0x3C, 0x52, 0x38, 0x22);
                    int a2 = 0;
                    yield return Ask(c, a => a2 = a);
                    if (a2 == 1 && c.Pos == 0) { try { File.Delete(SavePath); } catch (System.Exception e) { Debug.LogWarning("[title] " + e.Message); } }
                }
                // BATTLE MODE (pos 3): the VS mode is not ported
                else if (s.Pos == 4) yield return ModsPage();    // ours: the optional mods (ModSettings)
            }
            yield return TitleFade(true);

            if (t.Picture != null) Destroy(t.Picture);
            // MAIN_STATE 2 (a game clear save): playMovie(2, 1) before the map
            if (pick == 1) { var sv = ReadSave(); if (sv != null && sv.MainState == 2) { _title.Black = 1f; yield return PlayMovie(2); } }
            _title = null;
            NewGame = pick == 0;
            LoadSaveOnStart = pick == 1;
            if (pick == 0) _mainState = 0;
            try { StartGame(); }
            catch (System.Exception e) { _error = e.Message; Debug.LogException(e); }
            if (pick == 1) { _fade = 1f; StartCoroutine(FadeInAfterTitle()); }     // a new game fades in its own opening (newGameScene)
        }

        // main loop: fadeFromBlack(0x28) once the game is set up (0x28 frames at the game's 20 a second)
        IEnumerator FadeInAfterTitle()
        {
            for (float s = 0f; s < 2f; s += Time.deltaTime) { _fade = Mathf.Min(_fade, 1f - s / 2f); yield return null; }
            _fade = 0f;
        }

        // drawMainMenuStrings view 0 / renderInitialMenu (US): the four TITLE_MENU_ITEMS (exe 0x80131868) at x 0x52, rows 12 apart from y 0x37,
        // the box (0x48, 0x32, 0xB0, 0x3A); CONTINUE and DELETE in colour 1 with no card; the cursor MENU_HIGHLIGHTS[0] (exe 0x801316B8)
        SysScreen InitialMenu(bool hasSave)
        {
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            System.Func<uint, int> at = a => (int)(a - load + 0x800);
            var s = new SysScreen();
            for (int i = 0; i < 4; i++)
            {
                string label = Exe(System.BitConverter.ToUInt32(exe, at(0x80131868) + 4 * i));
                if ((i == 1 || i == 2) && !hasSave) s.DimText(label, 0x52, 0x37 + 12 * i);
                else s.Text(label, 0x52, 0x37 + 12 * i);
            }
            // ours: a fifth row, MODS, under the game's four; the box and the cursor's range grow by one row
            s.Text(ModsLabel, 0x52, 0x37 + 12 * 4);
            s.Box(0x48, 0x32, 0xB0, 0x3A + 12);
            int h = at(0x801316B8);
            s.Cursor(System.BitConverter.ToInt16(exe, h + 8), System.BitConverter.ToInt16(exe, h + 10), System.BitConverter.ToInt16(exe, h + 12),
                     System.BitConverter.ToInt16(exe, h + 14), System.BitConverter.ToInt16(exe, h + 6), (sbyte)exe[h + 1] + 1);
            return s;
        }

        // ------------------------------------------------------------------ ours: the MODS page (not in the original game)
        const string ModsLabel = "MODS";

        // one row per mod (Cross switches it), then BACK; Triangle / Circle also go back. Same box and cursor as the title menu
        IEnumerator ModsPage()
        {
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            int h = (int)(0x801316B8 - load + 0x800);
            int pos = 0;
            while (true)
            {
                var s = new SysScreen();
                // the game's letters are about 11 pixels wide: the names start at 0x1C and ON / OFF at 0xD8, in a box 0xF0 wide
                s.Text("REMASTERED MAPS", 0x1C, 0x37); s.Text(ModSettings.RemasteredMaps ? "ON" : "OFF", 0xD8, 0x37);
                s.Text("ONLINE BATTLE", 0x1C, 0x37 + 12); s.Text(ModSettings.OnlineBattle ? "ON" : "OFF", 0xD8, 0x37 + 12);
                s.Text("BACK", 0x1C, 0x37 + 24);
                s.Box(0x12, 0x32, 0xF0, 0x2E);
                s.Cursor(System.BitConverter.ToInt16(exe, h + 8) - (0x52 - 0x1C), System.BitConverter.ToInt16(exe, h + 10), 0xE6,
                         System.BitConverter.ToInt16(exe, h + 14), System.BitConverter.ToInt16(exe, h + 6), 3);
                s.Pos = pos;
                int answer = 0;
                yield return Ask(s, a => answer = a);
                pos = s.Pos;
                if (answer != 1 || pos == 2) yield break;
                if (pos == 0) ModSettings.RemasteredMaps = !ModSettings.RemasteredMaps;
                else ModSettings.OnlineBattle = !ModSettings.OnlineBattle;
            }
        }

        // renderPressStartToContinue / renderMainMenuBackground: the 320 x 240 picture (two quads, 256 + 64 wide) and, on the landing screen,
        // "PRESS START" from the picture's rows 0xF1-0xFA (121 x 10) at (-0x36, 0x32), tinted (0, 0x80, 0), shown 30 frames of every 60
        void DrawTitle()
        {
            var t = _title;
            UiKit.Fill(new Rect(0, 0, Screen.width, Screen.height), Color.black);
            UiKit.Begin();
            if (t.Picture != null)
            {
                float H = t.PictureHeight;
                GUI.DrawTextureWithTexCoords(new Rect(SysX(0), SysY(0), 320 * 4.42f, 240 * 4.42f), t.Picture, new Rect(0f, (H - 240f) / H, 1f, 240f / H));
                if (t.Landing && t.Counter % 0x3C < 0x1E && H >= 0xF1 + 10)
                {
                    var o = GUI.color; GUI.color = new Color(0f, 1f, 0f, 1f);
                    GUI.DrawTextureWithTexCoords(new Rect(SysX(160 - 0x36), SysY(120 + 0x32), 121 * 4.42f, 10 * 4.42f), t.Picture,
                                                 new Rect(0f, (H - 0xF1 - 10) / H, 121f / t.Picture.width, 10f / H));
                    GUI.color = o;
                }
            }
            UiKit.End();
            DrawSysScreen();
            if (t.Black > 0.001f)
            {
                var o = GUI.color; GUI.color = new Color(0, 0, 0, t.Black);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = o;
            }
            if (!string.IsNullOrEmpty(_error)) GUI.Label(new Rect(10, 10, Screen.width - 20, 60), _error);
        }
    }
}
