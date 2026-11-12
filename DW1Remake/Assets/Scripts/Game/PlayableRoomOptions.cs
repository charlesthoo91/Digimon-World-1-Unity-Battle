using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours: the title screen's OPTIONS page (not in the original game). One row per option, scrolling, in the game's blue box moved up to
    /// the top, and a second box at the bottom that says what the row under the cursor does. Every text here is ours, in English or
    /// Traditional Chinese (OPTIONS > LANGUAGE; the Chinese is drawn with the game font's Chinese glyphs, GameFont); the game's own
    /// texts from the disc follow the same switch (Tr).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        sealed class OptionRow
        {
            public string En, Zh, HelpEn, HelpZh;
            public System.Func<string> Value;          // what shows on the right (null = nothing)
            public System.Func<IEnumerator> Act;       // Cross
            public System.Action<int> Side;            // left / right (-1 / +1), null = none
        }

        static bool Zh { get { return ModSettings.Language == 1 && (DW1Remake.Formats.Tr.Ready || CjkFont != null); } }
        static string L(string en, string zh) { return Zh ? zh : en; }
        static string OnOff(bool on) { return on ? L("ON", "開") : L("OFF", "關"); }

        List<OptionRow> OptionRows()
        {
            var r = new List<OptionRow>();
            System.Func<System.Action, System.Func<IEnumerator>> just = a => () => { a(); return null; };
            r.Add(new OptionRow { En = "DIGITAL CLOCK", Zh = "數位時鐘", Value = () => OnOff(ModSettings.DigitalClock), Act = just(() => ModSettings.DigitalClock = !ModSettings.DigitalClock),
                HelpEn = "Shows the time as numbers under the clock dial.", HelpZh = "在時鐘下方以數字顯示時間。" });
            r.Add(new OptionRow { En = "ONLINE BATTLE", Zh = "線上對戰", Value = () => OnOff(ModSettings.OnlineBattle), Act = just(() => ModSettings.OnlineBattle = !ModSettings.OnlineBattle),
                HelpEn = "At the arena, Greymon offers a battle against the Digimon of a friend on another PC, over the internet.", HelpZh = "在競技場，暴龍獸可讓你透過網路與另一台電腦上朋友的數碼寶貝對戰。" });
            r.Add(new OptionRow { En = "SMOOTH 60 FPS", Zh = "流暢 60 FPS", Value = () => OnOff(ModSettings.Smooth60), Act = just(() => ModSettings.Smooth60 = !ModSettings.Smooth60),
                HelpEn = "Draws up to 60 pictures a second instead of 20. The game itself keeps its original speed.", HelpZh = "畫面每秒最多 60 格，而非原版的 20 格。遊戲本身速度不變。" });
            r.Add(new OptionRow { En = "EVOLUTION GUIDE", Zh = "進化指南", Value = () => OnOff(ModSettings.EvolutionGuide), Act = just(() => ModSettings.EvolutionGuide = !ModSettings.EvolutionGuide),
                HelpEn = "On the Chart page, Square shows what each evolution asks for and which one your partner would take now.", HelpZh = "在圖鑑頁按方塊鍵，顯示每種進化的條件，以及夥伴目前會進化成什麼。" });
            r.Add(new OptionRow { En = "RECRUIT TAB", Zh = "招募分頁", Value = () => OnOff(ModSettings.RecruitTab), Act = just(() => ModSettings.RecruitTab = !ModSettings.RecruitTab),
                HelpEn = "Adds a tab to the Player menu: who has joined File City, and its prosperity.", HelpZh = "在玩家選單加入分頁：列出已加入檔案城的數碼寶貝與繁榮度。" });
            r.Add(new OptionRow { En = "STATUS NUMBERS", Zh = "狀態數值", Value = () => OnOff(ModSettings.StatusNumbers), Act = just(() => ModSettings.StatusNumbers = !ModSettings.StatusNumbers),
                HelpEn = "The Digimon's Status page shows happiness, discipline and virus as numbers on their bars, and the hours of life left.", HelpZh = "數碼寶貝的狀態頁會在快樂度、紀律和病毒的量表上顯示數值，並顯示剩餘壽命的時數。" });
            r.Add(new OptionRow { En = "TENSION VALUE", Zh = "張力數值", Value = () => OnOff(ModSettings.TensionValue), Act = just(() => ModSettings.TensionValue = !ModSettings.TensionValue),
                HelpEn = "Fishing: shows the line tension as a number under the gauge, 0 to 100 percent.", HelpZh = "釣魚時在量表下方以 0 到 100％ 顯示釣線張力。" });
            r.Add(new OptionRow { En = "FISHING INDICATOR", Zh = "釣魚提示", Value = () => OnOff(ModSettings.FishingIndicator), Act = just(() => ModSettings.FishingIndicator = !ModSettings.FishingIndicator),
                HelpEn = "Fishing: shows PRESS X over the float while a bite can be hooked.", HelpZh = "釣魚時，在可以拉竿的時候於浮標上方顯示 PRESS X。" });
            r.Add(new OptionRow { En = "MAP", Zh = "地圖", Value = () => OnOff(ModSettings.MapView), Act = just(() => ModSettings.MapView = !ModSettings.MapView),
                HelpEn = "Select while walking shows a map of the area: exits, Digimon and where you are.", HelpZh = "行走時按 SELECT 顯示區域地圖：出口、數碼寶貝與你的位置。" });
            r.Add(new OptionRow { En = "RECENTRE CAMERA", Zh = "鏡頭回正", Value = () => OnOff(ModSettings.FightRecentre), Act = just(() => ModSettings.FightRecentre = !ModSettings.FightRecentre),
                HelpEn = "After winning a wild battle, the camera glides back to the boy.", HelpZh = "贏得野外戰鬥後，鏡頭會移回主角身上。" });
            r.Add(new OptionRow { En = "AI UPSCALE", Zh = "AI 高畫質", Value = () => OnOff(ModSettings.AiUpscale), Act = ToggleAiUpscale,
                HelpEn = "Sharpens the maps, Digimon and screen pictures with AI on your graphics card. PC only.", HelpZh = "以顯示卡上的 AI 讓地圖、數碼寶貝與畫面圖片更清晰。僅限電腦版。" });
            r.Add(new OptionRow { En = "ASPECT RATIO", Zh = "畫面比例", Value = () => ModSettings.Widescreen ? "16:9" : "4:3", Act = just(() => ModSettings.Widescreen = !ModSettings.Widescreen),
                HelpEn = "4:3 as the original, or 16:9 to see more of the map at the sides.", HelpZh = "4:3 為原版比例；16:9 可看到地圖兩側更多範圍。" });
            r.Add(new OptionRow { En = "SCREEN MODE", Zh = "顯示模式", Value = () => ScreenModeName(ModSettings.ScreenMode),
                Act = () => ChangeScreen(() => ModSettings.ScreenMode = (ModSettings.ScreenMode + 1) % 3),
                Side = d => { ChangeScreenNow(() => ModSettings.ScreenMode = (ModSettings.ScreenMode + d + 3) % 3); },
                HelpEn = "A window, borderless full screen (fills the screen, quick to switch away), or full screen. PC only.", HelpZh = "視窗、無邊框全螢幕（填滿螢幕，切換到其他程式較快）或全螢幕。僅限電腦版。" });
            r.Add(new OptionRow { En = "RESOLUTION", Zh = "解析度", Value = () => ResolutionName(ModSettings.Resolution),
                Act = () => ChangeScreen(() => ModSettings.Resolution = (ModSettings.Resolution + 1) % Resolutions.Length),
                Side = d => { ChangeScreenNow(() => ModSettings.Resolution = (ModSettings.Resolution + d + Resolutions.Length) % Resolutions.Length); },
                HelpEn = "The picture's size in pixels: your screen's own, or 720p, 1080p, 1440p or 4K. PC only.", HelpZh = "畫面的像素大小：螢幕原本的解析度，或 720p、1080p、1440p、4K。僅限電腦版。" });
            r.Add(new OptionRow { En = "VOLUME", Zh = "音量", Value = () => ModSettings.Volume.ToString(), Act = just(() => SetVolume(ModSettings.Volume >= 100 ? 0 : ModSettings.Volume + 10)),
                Side = d => SetVolume(ModSettings.Volume + d * 5),
                HelpEn = "Left and right turn the sound down or up, 0 to 100.", HelpZh = "按左右鍵調整音量，0 到 100。" });
            r.Add(new OptionRow { En = "LANGUAGE", Zh = "語言", Value = () => Zh ? "繁體中文" : "ENGLISH", Act = ToggleLanguage, Side = d => { ToggleLanguageNow(); },
                HelpEn = "English or Traditional Chinese, for the whole game: texts, menus and names. The player and partner names stay as typed.", HelpZh = "英文或繁體中文，套用到整個遊戲：對話、選單與名稱。主角與夥伴的名字維持輸入時的樣子。" });
            r.Add(new OptionRow { En = "CHEATS", Zh = "作弊", Act = () => ModsPage(CheatRows),
                HelpEn = "Cheats that act when a saved game is loaded, and a quick way to pass the time.", HelpZh = "載入存檔時生效的作弊，以及快速推進時間的方法。" });
            r.Add(new OptionRow { En = "CONTROLS", Zh = "操作設定", Act = ControlsPage,
                HelpEn = "Keyboard keys, controller buttons and the touch buttons.", HelpZh = "鍵盤、手把與觸控按鈕的設定。" });
            r.Add(new OptionRow { En = "EXPORT SAVE", Zh = "匯出存檔", Act = ExportSave,
                HelpEn = "Copies a save to a file, to keep it or move it to another PC.", HelpZh = "將存檔複製成檔案，以便保存或移到其他電腦。" });
            r.Add(new OptionRow { En = "IMPORT SAVE", Zh = "匯入存檔", Act = ImportSave,
                HelpEn = "Loads a save file, or a save from a PS1 memory card file, into a slot.", HelpZh = "將存檔檔案或 PS1 記憶卡檔案中的存檔載入欄位。" });
            r.Add(new OptionRow { En = "BACK", Zh = "返回", Act = null,
                HelpEn = "Back to the title screen.", HelpZh = "返回標題畫面。" });
            return r;
        }

        // ours (OPTIONS > CHEATS): each is put into effect when a game is next loaded (CONTINUE), not at once (ApplyCheats)
        List<OptionRow> CheatRows()
        {
            var r = new List<OptionRow>();
            System.Func<System.Action, System.Func<IEnumerator>> just = a => () => { a(); return null; };
            r.Add(new OptionRow { En = "ITEM BANK 99", Zh = "銀行道具 99", Value = () => OnOff(ModSettings.CheatBank), Act = just(() => ModSettings.CheatBank = !ModSettings.CheatBank),
                HelpEn = "On loading a game: 99 of every item in Agumon's item bank, once Agumon has joined the city. Story items are left out.", HelpZh = "載入存檔時：亞古獸加入城鎮後，牠的道具銀行裡每種道具都有 99 個。劇情道具除外。" });
            r.Add(new OptionRow { En = "FULL CHART", Zh = "完整圖鑑", Value = () => OnOff(ModSettings.CheatChart), Act = just(() => ModSettings.CheatChart = !ModSettings.CheatChart),
                HelpEn = "On loading a game: every Digimon of the evolution chart counts as raised.", HelpZh = "載入存檔時：進化圖鑑上的所有數碼寶貝都算作已培育。" });
            r.Add(new OptionRow { En = "ALL MEDALS", Zh = "全部獎牌", Value = () => OnOff(ModSettings.CheatMedals), Act = just(() => ModSettings.CheatMedals = !ModSettings.CheatMedals),
                HelpEn = "On loading a game: every medal is won.", HelpZh = "載入存檔時：獲得全部獎牌。" });
            r.Add(new OptionRow { En = "ALL CARDS", Zh = "全部卡片", Value = () => OnOff(ModSettings.CheatCards), Act = just(() => ModSettings.CheatCards = !ModSettings.CheatCards),
                HelpEn = "On loading a game: at least 1 of each card.", HelpZh = "載入存檔時：每種卡片至少有 1 張。" });
            r.Add(new OptionRow { En = "MAX BITS", Zh = "最多位元", Value = () => OnOff(ModSettings.CheatBits), Act = just(() => ModSettings.CheatBits = !ModSettings.CheatBits),
                HelpEn = "On loading a game: 999,999 Bits.", HelpZh = "載入存檔時：擁有 999,999 位元。" });
            r.Add(new OptionRow { En = "PASS AN HOUR", Zh = "快轉一小時", Value = () => OnOff(ModSettings.CheatHour), Act = just(() => ModSettings.CheatHour = !ModSettings.CheatHour),
                HelpEn = "From the next loaded game: L1 + R1 + Circle while the clock runs moves time on by one hour.", HelpZh = "從下次載入存檔起：時鐘走動時按 L1 + R1 + 圈，時間前進一小時。" });
            r.Add(new OptionRow { En = "BACK", Zh = "返回", Act = null,
                HelpEn = "Back to the options.", HelpZh = "返回設定。" });
            return r;
        }

        const int OptVisible = 11, OptTop = 0x1C, OptBoxY = 0x17, OptHelpY = 0xAA;

        IEnumerator ModsPage() { return ModsPage(OptionRows); }

        IEnumerator ModsPage(System.Func<List<OptionRow>> make)
        {
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            int h = (int)(0x801316B8 - load + 0x800);
            // MENU_HIGHLIGHTS: the title menu's cursor, its place shifted with the rows (the rows started at 0x37 there)
            int curY = System.BitConverter.ToInt16(exe, h + 10) - 0x37 + OptTop;
            int pos = 0, scroll = 0;
            while (true)
            {
                var rows = make();
                int max = Mathf.Max(0, rows.Count - OptVisible);
                var s = new SysScreen();
                s.Refresh = sc => FillOptions(sc, rows);
                s.Cursor(System.BitConverter.ToInt16(exe, h + 8) - (0x52 - 0x1C), curY, 0x106,
                         System.BitConverter.ToInt16(exe, h + 14), System.BitConverter.ToInt16(exe, h + 6), Mathf.Min(OptVisible, rows.Count), max);
                s.Pos = pos; s.Scroll = Mathf.Clamp(scroll, 0, max); s.Sides = true;
                FillOptions(s, rows);
                int answer = 0;
                yield return Ask(s, a => answer = a);
                pos = s.Pos; scroll = s.Scroll;
                var row = rows[Mathf.Clamp(pos + scroll, 0, rows.Count - 1)];
                if (answer == 3 || answer == 4) { if (row.Side != null) { row.Side(answer == 3 ? -1 : 1); Snd(2); } continue; }
                if (answer != 1 || row.Act == null) yield break;
                var e = row.Act();
                if (e != null) yield return e;
            }
        }

        // the visible rows, their values, and the help box for the row under the cursor
        void FillOptions(SysScreen s, List<OptionRow> rows)
        {
            s.Texts.Clear(); s.Width.Clear(); s.Dim.Clear(); s.Boxes.Clear();
            for (int i = 0; i < OptVisible && s.Scroll + i < rows.Count; i++)
            {
                var row = rows[s.Scroll + i];
                s.Text(Zh ? row.Zh : row.En, 0x1C, OptTop + 12 * i);
                if (row.Value != null)
                {
                    // a value wider than its column (ENGLISH) moves left so it ends before the cursor's edge and the - / + marks (0x118)
                    string v = row.Value();
                    int w = Battle.StatFont != null ? Battle.StatFont.MeasureRaw(v) : v.Length * 7;
                    s.Text(v, Mathf.Min(0xF4, 0x114 - w), OptTop + 12 * i);
                }
            }
            if (s.Scroll > 0) s.Text("-", 0x118, OptTop);                                            // more above
            if (s.Scroll + OptVisible < rows.Count) s.Text("+", 0x118, OptTop + 12 * (OptVisible - 1)); // more below
            s.Box(0x12, OptBoxY, 0x110, 12 * OptVisible + 10);
            var cur = rows[Mathf.Clamp(s.Pos + s.Scroll, 0, rows.Count - 1)];
            var lines = Wrap(Zh ? cur.HelpZh : cur.HelpEn, 0x110 - 0x14);
            for (int i = 0; i < lines.Count && i < 4; i++) s.Text(lines[i], 0x1C, OptHelpY + 5 + 12 * i);
            s.Box(0x12, OptHelpY, 0x110, 12 * 4 + 10);
        }

        IEnumerator ToggleAiUpscale()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            yield return Notice(L("AI upscaling works only on the PC version.", "AI 高畫質僅限電腦版使用。"));
#else
            if (ModSettings.AiUpscale) { ModSettings.AiUpscale = false; yield break; }
            bool go = false;
            yield return Confirm(L("AI UPSCALE makes the game sharper with AI on your graphics card. The first time a map, a Digimon or a picture shows, it is upscaled while you play, so the game may stutter for a few seconds. The results are kept as cache files on this PC, so later visits are instant. Turn it on?",
                                   "AI 高畫質會用顯示卡上的 AI 讓遊戲畫面更清晰。每張地圖、每隻數碼寶貝或每張圖片第一次出現時，會在遊玩中進行放大，遊戲可能會卡頓幾秒。結果會以快取檔案存在這台電腦上，之後再看到就會立即顯示。要開啟嗎？"), r => go = r);
            if (!go) yield break;
            ModSettings.AiUpscale = true;
            if (!AiReady()) { ModSettings.AiUpscale = false; yield return Notice(L("AI upscaling cannot run: ", "無法使用 AI 高畫質：") + _aiError + "."); }
#endif
        }

        IEnumerator ToggleLanguage()
        {
            if (ModSettings.Language == 0 && !DW1Remake.Formats.Tr.Ready && CjkFont == null) { yield return Notice("Traditional Chinese needs the font Microsoft JhengHei, which was not found on this PC, so the language stays English. To add it: Windows Settings, Time and language, Language and region, Add a language, Chinese Traditional, with its fonts. Then start the game again."); yield break; }
            ToggleLanguageNow();
        }

        void ToggleLanguageNow()
        {
            if (ModSettings.Language == 0 && !DW1Remake.Formats.Tr.Ready && CjkFont == null) return;
            ModSettings.Language = ModSettings.Language == 1 ? 0 : 1;
        }

        // ---- ours: SCREEN MODE and RESOLUTION (PC; the web build keeps the browser's page)
        static readonly int[,] Resolutions = { { 0, 0 }, { 1280, 720 }, { 1920, 1080 }, { 2560, 1440 }, { 3840, 2160 } };

        static string ScreenModeName(int m) { return m == 0 ? L("WINDOW", "視窗") : m == 1 ? L("BORDERLESS", "無邊框") : L("FULL SCREEN", "全螢幕"); }
        static string ResolutionName(int r) { return r == 0 ? L("DEFAULT", "預設") : r == 4 ? "4K" : Resolutions[r, 1] + "p"; }

        IEnumerator ChangeScreen(System.Action change)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            yield return Notice(L("The screen mode and resolution can be changed only on the PC version.", "顯示模式與解析度僅限電腦版調整。"));
#else
            ChangeScreenNow(change);
            yield break;
#endif
        }

        static void ChangeScreenNow(System.Action change)
        {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            change();
            ApplyScreen();
#endif
        }

        /// <summary>Puts SCREEN MODE and RESOLUTION into effect (at start and on a change). "Default" is the screen's own size; nothing changes in the editor.</summary>
        public static void ApplyScreen()
        {
#if !UNITY_EDITOR && !UNITY_WEBGL
            var mode = ModSettings.ScreenMode == 0 ? FullScreenMode.Windowed : ModSettings.ScreenMode == 1 ? FullScreenMode.FullScreenWindow : FullScreenMode.ExclusiveFullScreen;
            int r = ModSettings.Resolution, w = Resolutions[r, 0], h = Resolutions[r, 1];
            if (r == 0) { w = Display.main.systemWidth; h = Display.main.systemHeight; }
            Screen.SetResolution(w, h, mode);
#endif
        }

        /// <summary>The master volume, 0 to 100 (ours: OPTIONS > VOLUME): every sound and the music go through the one listener.</summary>
        static void SetVolume(int v)
        {
            ModSettings.Volume = Mathf.Clamp(v, 0, 100);
            AudioListener.volume = ModSettings.Volume / 100f;
        }

        // ------------------------------------------------------------------ Traditional Chinese text
        // Chinese is drawn with the game font's Chinese glyphs (GameFont, made from the shipped Cubic 11 pixel font); only a build without them
        // falls back to a Chinese font of the PC (Windows: Microsoft JhengHei).

        static Font _cjkFont; static bool _cjkTried;
        public static Font CjkFont
        {
            get
            {
                if (_cjkTried) return _cjkFont;
                _cjkTried = true;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
                try
                {
                    var have = new HashSet<string>(Font.GetOSInstalledFontNames());
                    foreach (var n in new[] { "Microsoft JhengHei", "Microsoft JhengHei UI", "PMingLiU", "MingLiU", "Noto Sans TC", "Noto Sans CJK TC", "PingFang TC", "Heiti TC", "Microsoft YaHei" })
                        if (have.Contains(n)) { _cjkFont = Font.CreateDynamicFontFromOSFont(n, 48); break; }
                }
                catch (System.Exception e) { Debug.LogWarning("[options] no Chinese font: " + e.Message); }
#endif
                return _cjkFont;
            }
        }

        static bool HasCjk(string s) { foreach (char c in s) if (c >= 0x2E80) return true; return false; }

        GUIStyle _cjkStyle;

        // DrawSysScreen: a line with Chinese in it, in the PC's Chinese font, the height of the game's letters
        void DrawCjk(string s, float x, float y, Color ink)
        {
            if (_cjkStyle == null) _cjkStyle = new GUIStyle(GUI.skin.label) { font = CjkFont, fontSize = 44, clipping = TextClipping.Overflow, wordWrap = false, alignment = TextAnchor.UpperLeft };
            _cjkStyle.normal.textColor = ink;
            GUI.Label(new Rect(x, y - 8f, 1400f, 64f), s, _cjkStyle);
        }
    }
}
