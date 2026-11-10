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
            MoveOldSave();
            while (pick < 0)
            {
                // the memory cards are always there (CONNECTED_CARDS): CONTINUE and DELETE are never dimmed
                var s = InitialMenu(true);
                int answer = 0;
                yield return Ask(s, a => answer = a);
                if (answer != 1) continue;      // Circle / Triangle: nothing
                if (s.Pos == 0)                                  // NEW GAME: menus 0xA (card), 0xE (empty slot), 0xF (confirm) or 0x13 (no card)
                {
                    int got = 0;
                    yield return CardFlow(0, r => got = r);
                    if (got == 1) pick = 0;
                }
                else if (s.Pos == 1)                             // CONTINUE GAME: menus 0x14, 0x17, 0x18
                {
                    int got = 0;
                    yield return CardFlow(1, r => got = r);
                    if (got == 1) pick = 1;
                }
                else if (s.Pos == 2) { int got = 0; yield return CardFlow(2, r => got = r); }     // DELETE GAME: menus 0x1E, 0x21, 0x22
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

        // ------------------------------------------------------------------ the memory cards (tickMainMenu / renderMainMenu, US)

        // MENU_HIGHLIGHTS[k] (exe 0x801316B8, 16 bytes): rows at +1, row height +6, x +8, y +10, width +12, height +14
        void HighlightCursor(SysScreen s, int k, int max = 0)
        {
            byte[] exe = ExeBytes;
            int h = (int)(0x801316B8 - System.BitConverter.ToUInt32(exe, 0x18) + 0x800) + 16 * k;
            s.Cursor(System.BitConverter.ToInt16(exe, h + 8), System.BitConverter.ToInt16(exe, h + 10), System.BitConverter.ToInt16(exe, h + 12),
                     System.BitConverter.ToInt16(exe, h + 14), System.BitConverter.ToInt16(exe, h + 6), (sbyte)exe[h + 1], max);
        }

        // SLOT_ACTION_TITLES[action] for card 2: STR_2 is drawn over its "1"
        string CardTitle(int action, int card) { string t = SlotTitle(action); return card == 1 && t.EndsWith("1") ? t.Substring(0, t.Length - 1) + Exe(0x80134662) : t; }

        /// <summary>action 0 new game, 1 continue, 2 delete; result 1 = a slot was chosen and confirmed (_card / _cardSlot set, or _noCard for a new
        /// game without a card), 0 = back to the title menu</summary>
        IEnumerator CardFlow(int action, System.Action<int> result)
        {
            while (true)
            {
                // view 1 (renderSelectNewGameCard: START SLOT 1 / 2, NO MEMORY CARD) or views 2 / 4 (renderSelectCardSlot)
                var s = new SysScreen();
                int x = action == 0 ? 0x4B : 0x46;
                s.Text(CardTitle(action, 0), x, 0x37); s.Text(CardTitle(action, 1), x, 0x43);
                if (action == 0) { s.Text(SlotTitle(3), x, 0x4F); s.Box(0x41, 0x32, 0xBE, 0x2E); }
                else s.Box(0x3C, 0x32, 0xB7, 0x22);
                HighlightCursor(s, action == 0 ? 1 : action == 1 ? 2 : 4);
                int answer = 0;
                yield return Ask(s, a => answer = a);
                if (answer != 1) { result(0); yield break; }
                if (action == 0 && s.Pos == 2)
                {
                    // menu 0x13, view 6 (renderConfirmNoCard): Yes starts the game with no card (MEMORY_CARD_ID = -1)
                    var c = new SysScreen();
                    c.Text(SlotTitle(3), 0x3A, 0x18); c.Box(0x30, 0x13, 0xBE, 0x16);
                    uint[] warn = { 0x801312A0, 0x801312B8, 0x801312D0, 0x801312E4 };
                    for (int i = 0; i < 4; i++) c.Text(Exe(warn[i]), 0x22, 0x5A + 12 * i);
                    c.Box(0x18, 0x55, 0x110, 0x46);
                    c.Text(Exe(0x801346AC), 0x6E, 0x37); c.Text(Exe(0x801346B0), 0x6E, 0x43); c.Box(0x64, 0x32, 0x38, 0x22);
                    HighlightCursor(c, 6);
                    int a2 = 0;
                    yield return Ask(c, a => a2 = a);
                    if (a2 == 1 && c.Pos == 0) { _card = -1; _cardSlot = -1; _noCard = true; result(1); yield break; }
                    continue;
                }
                int card = s.Pos;
                while (true)
                {
                    int slot = -1;
                    yield return SlotList(action, card, r => slot = r);
                    if (slot < 0) break;            // back to the card choice
                    bool yes = false;
                    yield return ConfirmSlot(action, card, slot, r => yes = r);
                    if (!yes) continue;             // back to the list
                    if (action == 2)
                    {
                        // menu 0x23: the file is deleted, then the card is read again (back to the list)
                        try { File.Delete(SlotPath(card, slot)); WebFiles.Sync(); } catch (System.Exception e) { Debug.LogWarning("[title] " + e.Message); }
                        continue;
                    }
                    _card = card; _cardSlot = slot; _noCard = false;
                    result(1); yield break;
                }
            }
        }

        // menus 0xE / 0x17 / 0x21, view 7 (renderSelectSlot / renderSaveSlotBox, tickSelectSlotInput): the title, then six boxes 0x24 apart of
        // the card's 15 slots; five cursor rows, the list scrolls to slot 10. A new game takes only an empty slot, continue and delete only a used
        // one; the others are dimmed (colour 1). A used slot shows the player name and the partner species (US save titles have no place)
        IEnumerator SlotList(int action, int card, System.Action<int> result, string title = null, bool any = false)
        {
            var info = new SaveGameData[SlotsPerCard];
            for (int i = 0; i < SlotsPerCard; i++) info[i] = ReadSlot(card, i);
            var s = new SysScreen();
            HighlightCursor(s, 7, SlotsPerCard - 5);
            s.Refresh = v =>
            {
                v.Texts.Clear(); v.Boxes.Clear(); v.Dim.Clear();
                v.Text(title ?? CardTitle(action, card), 0x3A, 0x18); v.Box(0x30, 0x13, 0xE0, 0x16);
                for (int i = 0; i < 6; i++)
                {
                    int slot = v.Scroll + i, y = 0x29 + 0x24 * i;
                    if (slot >= SlotsPerCard) break;
                    var d = info[slot];
                    bool dim = !any && (d != null) == (action == 0);
                    if (d != null)
                    {
                        var sp = DigimonStatsCatalog.Get(_disc, d.PartnerType);
                        string who = d.PlayerName ?? "", kind = sp != null ? sp.Name : "";
                        if (dim) { v.DimText(who, 0x58, y + 5); v.DimText(kind, 0xA6, y + 5); }
                        else { v.Text(who, 0x58, y + 5); v.Text(kind, 0xA6, y + 5); }
                    }
                    v.Box(0x30, y, 0xE0, 0x24);
                }
            };
            s.Refresh(s);
            while (true)
            {
                int answer = 0;
                yield return Ask(s, a => answer = a);
                if (answer != 1) { result(-1); yield break; }
                int slot = s.Scroll + s.Pos;
                if (any || (info[slot] != null) != (action == 0)) { result(slot); yield break; }
                // the wrong kind of slot: nothing happens (the cursor stays)
            }
        }

        // menus 0xF / 0x18 / 0x22, view 8 (renderConfirmSlotSelection, US): the title, the slot (Not used when empty), Yes / No and
        // SLOT_ACTION_QUESTIONS[action]; delete starts on No
        IEnumerator ConfirmSlot(int action, int card, int slot, System.Action<bool> result)
        {
            var d = ReadSlot(card, slot);
            var s = new SysScreen();
            s.Text(CardTitle(action, card), 0x3A, 0x18); s.Box(0x30, 0x13, 0xB0, 0x16);
            if (d != null)
            {
                var sp = DigimonStatsCatalog.Get(_disc, d.PartnerType);
                s.Text(d.PlayerName ?? "", 0x58, 0x2E); s.Text(sp != null ? sp.Name : "", 0xA6, 0x2E);
            }
            else s.Text(Exe(0x80131340), 0x58, 0x2E);
            s.Box(0x30, 0x29, 0xE0, 0x24);
            s.Text(Exe(0x801346AC), 0x3A, 0x52); s.Text(Exe(0x801346B0), 0x3A, 0x5E); s.Box(0x30, 0x4D, 0x38, 0x22);
            byte[] exe = ExeBytes;
            uint q = System.BitConverter.ToUInt32(exe, (int)(0x801318DC - System.BitConverter.ToUInt32(exe, 0x18) + 0x800) + 4 * action);
            s.Text(Exe(q), 0x3E, 0x74); s.Box(0x30, 0x6F, 0xE0, 0x16);
            HighlightCursor(s, 8);
            if (action == 2) s.Pos = 1;
            int answer = 0;
            yield return Ask(s, a => answer = a);
            result(answer == 1 && s.Pos == 0);
        }

        // ------------------------------------------------------------------ ours: Escape while walking = back to the title (not in the original)

        static bool s_toTitle;      // the scene is loaded again for the title screen (whatever the editor's test ticks say)

        IEnumerator BackToTitle()
        {
            _routineOn = true;
            // laid out like the game's own Do you want to save? (YesNoScreen), wider for two lines of our words
            var s = YesNoScreen("Return to the title screen?", 0x3A, 0x37, 0x57, 0x63, 0x37, 87);
            s.Text("Anything not saved is lost.", 0x3A, 0x37 + 12);
            s.Box(0x30, 0x32, 0xE0, 0x20); s.Box(0x30, 0x52, 0x38, 0x22);
            int answer = 0;
            yield return Ask(s, a => answer = a);
            if (answer == 1 && s.Pos == 0)
            {
                // the same way the game's own end goes back to the title (PlayableRoomFinale): the whole scene starts again
                s_toTitle = true;
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
                yield break;
            }
            _routineOn = false;
        }

        // ------------------------------------------------------------------ ours: save files out and in (MODS page; not in the original game)
        // A slot's save as a file the player keeps (Downloads folder), and such a file back into an empty slot: to back up, or to move a
        // game between the browser and the PC version. The slot lists are the memory card screens' own (SlotList)

        IEnumerator PickCard(string verb, System.Action<int> result)
        {
            var s = new SysScreen();
            s.Text(verb, 0x46, 0x22); s.Box(0x3C, 0x1D, 0xB7, 0x16);
            s.Text("MEMORY CARD 1", 0x46, 0x37); s.Text("MEMORY CARD 2", 0x46, 0x43);
            s.Box(0x3C, 0x32, 0xB7, 0x22);
            HighlightCursor(s, 2);
            int answer = 0;
            yield return Ask(s, a => answer = a);
            result(answer == 1 ? s.Pos : -1);
        }

        IEnumerator Notice(string text)
        {
            var s = new SysScreen();
            var lines = Wrap(text, 0x110 - 0x14);
            for (int i = 0; i < lines.Count; i++) s.Text(lines[i], 0x22, 0x37 + 12 * i);
            s.Box(0x18, 0x32, 0x110, 12 * lines.Count + 10);
            yield return Ask(s, a => { });
        }

        IEnumerator Confirm(string text, System.Action<bool> result)
        {
            var s = new SysScreen();
            var lines = Wrap(text, 0x110 - 0x14);
            for (int i = 0; i < lines.Count; i++) s.Text(lines[i], 0x22, 0x37 + 12 * i);
            s.Box(0x18, 0x32, 0x110, 12 * lines.Count + 10);
            int y = 0x37 + 12 * lines.Count + 0x12;
            s.Text(Exe(0x801346AC), 0x22, y); s.Text(Exe(0x801346B0), 0x22, y + 12);     // Yes / No
            s.Box(0x18, y - 5, 0x38, 0x22);
            s.Cursor(0x22 - 3, y, 33, 12, 12, 2);
            s.Pos = 1;
            int answer = 0;
            yield return Ask(s, a => answer = a);
            result(answer == 1 && s.Pos == 0);
        }

        IEnumerator ExportSave()
        {
            int card = -1;
            yield return PickCard("EXPORT SAVE", r => card = r);
            if (card < 0) yield break;
            int slot = -1;
            yield return SlotList(1, card, r => slot = r, "EXPORT  CARD " + (card + 1));       // used slots only
            if (slot < 0) yield break;
            string name = "DW1Remake_card" + (card + 1) + "_slot" + (slot + 1).ToString("00") + ".json", text = null, err = null;
            bool go = false;
#if UNITY_WEBGL && !UNITY_EDITOR
            yield return Confirm("The save goes to your downloads as " + name + ". If a file of that name is already there, the browser keeps both or asks you.", r => go = r);
#else
            string target = Path.Combine(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads"), name);
            yield return Confirm(File.Exists(target)
                ? "Your Downloads folder already has " + name + ". It will be replaced by this save. Go on?"
                : "The save goes to your Downloads folder as " + name + ". A file there with the same name would be replaced. Go on?", r => go = r);
#endif
            if (!go) yield break;
            try { text = File.ReadAllText(SlotPath(card, slot)); }
            catch (System.Exception e) { err = "The save could not be read. " + e.Message; }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (err == null) WebFiles.DownloadText(name, text);
#else
            if (err == null)
            {
                try
                {
                    string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, name), text);
                }
                catch (System.Exception e) { err = "The save could not be written. " + e.Message; }
            }
#endif
            yield return Notice(err ?? "The save is in your Downloads folder: " + name);
        }

        string _importText; bool _importWaiting;

        /// <summary>The web build's save file text (DW1Web.jslib DW1_PickText); "" = cancelled; a file that is not .json comes as
        /// "\u0001" + its bytes in base64 (a memory card file).</summary>
        public void OnWebSaveText(string text) { _importText = text; _importWaiting = false; }

        IEnumerator ImportSave()
        {
            _importText = null;
            byte[] bytes = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            _importWaiting = true;
            WebFiles.PickText(gameObject.name, "OnWebSaveText");
            while (_importWaiting) yield return null;
            if (!string.IsNullOrEmpty(_importText) && _importText[0] == '\u0001')
            {
                try { bytes = System.Convert.FromBase64String(_importText.Substring(1)); } catch { bytes = null; }
                _importText = null;
            }
#else
            string path = BrowseForSave();
            if (!string.IsNullOrEmpty(path))
            {
                try { bytes = File.ReadAllBytes(path); }
                catch (System.Exception e) { bytes = null; Debug.LogWarning("[title] " + e.Message); }
            }
#endif
            // a .json of this game, or a PS1 memory card file (PlayableRoomCardImport)
            if (bytes != null && bytes.Length > 0)
            {
                int first = 0;
                while (first < bytes.Length && first < 4 && (bytes[first] == 0xEF || bytes[first] == 0xBB || bytes[first] == 0xBF || bytes[first] <= 0x20)) first++;
                if (first < bytes.Length && bytes[first] == '{') { _importText = System.Text.Encoding.UTF8.GetString(bytes); bytes = null; }
            }
            if (string.IsNullOrEmpty(_importText) && (bytes == null || bytes.Length == 0)) yield break;
            SaveGameData d = null;
            if (bytes != null)
            {
                var saves = ReadCardFile(bytes);
                if (saves == null) { yield return Notice("That file is not a save of this game or a memory card file."); yield break; }
                if (saves.Count == 0) { yield return Notice("No Digimon World (USA) save on that memory card."); yield break; }
                int k = 0;
                if (saves.Count > 1) yield return PickCardSave(saves, r => k = r);
                if (k < 0) yield break;
                d = saves[k].Save;
                _importText = JsonUtility.ToJson(d);
            }
            else
            {
                try { d = JsonUtility.FromJson<SaveGameData>(_importText); } catch { d = null; }
            }
            if (d == null || string.IsNullOrEmpty(d.Map)) { yield return Notice("That file is not a save of this game."); yield break; }
            int card = -1;
            yield return PickCard("IMPORT SAVE", r => card = r);
            if (card < 0) yield break;
            int slot = -1;
            yield return SlotList(0, card, r => slot = r, "IMPORT  CARD " + (card + 1), true);       // any slot; a used one asks first
            if (slot < 0) yield break;
            var old = ReadSlot(card, slot);
            if (old != null)
            {
                var sp = DigimonStatsCatalog.Get(_disc, old.PartnerType);
                bool go = false;
                yield return Confirm("Slot " + (slot + 1) + " of memory card " + (card + 1) + " holds " + (old.PlayerName ?? "") + " and " + (sp != null ? sp.Name : "a Digimon") +
                                     ". That save will be replaced by the imported one and cannot be brought back. Go on?", r => go = r);
                if (!go) yield break;
            }
            bool ok = true;
            try { File.WriteAllText(SlotPath(card, slot), _importText); WebFiles.Sync(); }
            catch (System.Exception e) { ok = false; Debug.LogWarning("[title] " + e.Message); }
            yield return Notice(ok ? "The save is in memory card " + (card + 1) + ", slot " + (slot + 1) + "." : "The save could not be written.");
        }

        // ------------------------------------------------------------------ ours: the MODS page (not in the original game)
        const string ModsLabel = "OPTIONS";

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
                // the game's letters are about 11 pixels wide: the names start at 0x1C and ON / OFF at 0xF4, in a box 0x110 wide (FISHING INDICATOR ran into 0xD8)
                s.Text("DIGITAL CLOCK", 0x1C, 0x37); s.Text(ModSettings.DigitalClock ? "ON" : "OFF", 0xF4, 0x37);
                s.Text("ONLINE BATTLE", 0x1C, 0x37 + 12); s.Text(ModSettings.OnlineBattle ? "ON" : "OFF", 0xF4, 0x37 + 12);
                s.Text("SMOOTH 60 FPS", 0x1C, 0x37 + 24); s.Text(ModSettings.Smooth60 ? "ON" : "OFF", 0xF4, 0x37 + 24);
                s.Text("EVOLUTION GUIDE", 0x1C, 0x37 + 36); s.Text(ModSettings.EvolutionGuide ? "ON" : "OFF", 0xF4, 0x37 + 36);
                s.Text("RECRUIT TAB", 0x1C, 0x37 + 48); s.Text(ModSettings.RecruitTab ? "ON" : "OFF", 0xF4, 0x37 + 48);
                s.Text("TENSION VALUE", 0x1C, 0x37 + 60); s.Text(ModSettings.TensionValue ? "ON" : "OFF", 0xF4, 0x37 + 60);
                s.Text("FISHING INDICATOR", 0x1C, 0x37 + 72); s.Text(ModSettings.FishingIndicator ? "ON" : "OFF", 0xF4, 0x37 + 72);
                s.Text("MAP", 0x1C, 0x37 + 84); s.Text(ModSettings.MapView ? "ON" : "OFF", 0xF4, 0x37 + 84);
                s.Text("CONTROLS", 0x1C, 0x37 + 96);
                s.Text("EXPORT SAVE", 0x1C, 0x37 + 108);
                s.Text("IMPORT SAVE", 0x1C, 0x37 + 120);
                s.Text("BACK", 0x1C, 0x37 + 132);
                s.Box(0x12, 0x32, 0x110, 0x2E + 108);
                s.Cursor(System.BitConverter.ToInt16(exe, h + 8) - (0x52 - 0x1C), System.BitConverter.ToInt16(exe, h + 10), 0x106,
                         System.BitConverter.ToInt16(exe, h + 14), System.BitConverter.ToInt16(exe, h + 6), 12);
                s.Pos = pos;
                int answer = 0;
                yield return Ask(s, a => answer = a);
                pos = s.Pos;
                if (answer != 1 || pos == 11) yield break;
                if (pos == 5) ModSettings.TensionValue = !ModSettings.TensionValue;
                else if (pos == 6) ModSettings.FishingIndicator = !ModSettings.FishingIndicator;
                else if (pos == 7) ModSettings.MapView = !ModSettings.MapView;
                else if (pos == 8) yield return ControlsPage();
                else if (pos == 9) yield return ExportSave();
                else if (pos == 10) yield return ImportSave();
                else if (pos == 0) ModSettings.DigitalClock = !ModSettings.DigitalClock;
                else if (pos == 1) ModSettings.OnlineBattle = !ModSettings.OnlineBattle;
                else if (pos == 2) ModSettings.Smooth60 = !ModSettings.Smooth60;
                else if (pos == 3) ModSettings.EvolutionGuide = !ModSettings.EvolutionGuide;
                else ModSettings.RecruitTab = !ModSettings.RecruitTab;
            }
        }

        // ours: MODS > CONTROLS (Pad, TouchPadView): the touch buttons, the controller and the keyboard keys
        IEnumerator ControlsPage()
        {
            byte[] exe = ExeBytes;
            int h = (int)(0x801316B8 - System.BitConverter.ToUInt32(exe, 0x18) + 0x800);
            int pos = 0;
            while (true)
            {
                var s = new SysScreen();
                int touch = ModSettings.TouchButtons;
                s.Text("TOUCH BUTTONS", 0x1C, 0x37); s.Text(touch == 0 ? "OFF" : touch == 1 ? "AUTO" : "ON", 0xD0, 0x37);
                s.Text("CONTROLLER", 0x1C, 0x37 + 12); s.Text(ModSettings.Controller ? "ON" : "OFF", 0xD0, 0x37 + 12);
                s.Text("KEYBOARD KEYS", 0x1C, 0x37 + 24);
                s.Text("CONTROLLER KEYS", 0x1C, 0x37 + 36);
                s.Text("BACK", 0x1C, 0x37 + 48);
                s.Box(0x12, 0x32, 0xF0, 0x2E + 24);
                s.Cursor(System.BitConverter.ToInt16(exe, h + 8) - (0x52 - 0x1C), System.BitConverter.ToInt16(exe, h + 10), 0xE6,
                         System.BitConverter.ToInt16(exe, h + 14), System.BitConverter.ToInt16(exe, h + 6), 5);
                s.Pos = pos;
                int answer = 0;
                yield return Ask(s, a => answer = a);
                pos = s.Pos;
                if (answer != 1 || pos == 4) yield break;
                if (pos == 0) ModSettings.TouchButtons = (touch + 1) % 3;          // AUTO -> ON -> OFF -> AUTO
                else if (pos == 1) ModSettings.Controller = !ModSettings.Controller;
                else yield return KeyMapPage(pos == 3);
            }
        }

        static readonly string[] PadNames = { "UP", "DOWN", "LEFT", "RIGHT", "CROSS", "CIRCLE", "TRIANGLE", "SQUARE", "L1", "R1", "START", "SELECT" };

        // ours: one row per PS1 button with its keyboard key (or controller button); Cross on a row, then the new key / button (Escape, or 10 seconds
        // without one, keeps the old one); RESET = the game's own keys / the PS layout
        IEnumerator KeyMapPage(bool controller)
        {
            byte[] exe = ExeBytes;
            int h = (int)(0x801316B8 - System.BitConverter.ToUInt32(exe, 0x18) + 0x800);
            const int Top = 0x14;
            int n = PadNames.Length, pos = 0;
            System.Func<int, SysScreen> page = waiting =>
            {
                var s = new SysScreen();
                for (int i = 0; i < n; i++)
                {
                    s.Text(PadNames[i], 0x1C, Top + 12 * i);
                    s.Text(i == waiting ? (controller ? "PRESS A BUTTON" : "PRESS A KEY") : controller ? Pad.PadButtonNames((PadButton)i) : Pad.KeyNames((PadButton)i), 0x80, Top + 12 * i);
                }
                s.Text("RESET", 0x1C, Top + 12 * n);
                s.Text("BACK", 0x1C, Top + 12 * (n + 1));
                s.Box(0x12, Top - 5, 0xF0, 12 * (n + 2) + 10);
                return s;
            };
            while (true)
            {
                var s = page(-1);
                s.Cursor(System.BitConverter.ToInt16(exe, h + 8) - (0x52 - 0x1C), System.BitConverter.ToInt16(exe, h + 10) + (Top - 0x37), 0xE6,
                         System.BitConverter.ToInt16(exe, h + 14), System.BitConverter.ToInt16(exe, h + 6), n + 2);
                s.Pos = pos;
                int answer = 0;
                yield return Ask(s, a => answer = a);
                pos = s.Pos;
                if (answer != 1 || pos == n + 1) yield break;
                if (pos == n) { if (controller) Pad.ResetPadButtons(); else Pad.ResetKeys(); continue; }
#if ENABLE_INPUT_SYSTEM
                _sys = page(pos);
                yield return null;      // the Cross that chose the row is not the new key
                float waited = 0f;
                while (waited < 10f)
                {
                    if (GameMenus.EscapeKey()) { Snd(4); break; }
                    if (controller)
                    {
                        string b = Pad.AnyPadButtonPressed();
                        if (b != null) { Pad.SetPadButton((PadButton)pos, b); Snd(3); break; }
                    }
                    else
                    {
                        var key = Pad.AnyKeyPressed();
                        if (key != UnityEngine.InputSystem.Key.None) { Pad.SetKey((PadButton)pos, key); Snd(3); break; }
                    }
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
                _sys = null;
                yield return null;      // the new key's press is not also a Cross on the page
#endif
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
