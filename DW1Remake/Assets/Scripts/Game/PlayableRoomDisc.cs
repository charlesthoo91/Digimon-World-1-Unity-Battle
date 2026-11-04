using System;
using System.IO;
using System.Runtime.InteropServices;
using DW1Remake.Disc;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Choosing the player's own disc image at run time (our screen, not the game's: the game has no disc to read yet).
    /// The chosen path is remembered in PlayerPrefs. The file is read in place every time; nothing from it is copied or saved.
    /// In the Editor the room's Disc Path is tried first, so testing works as before.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const string DiscPref = "DW1Remake.DiscPath";

        sealed class DiscPicker
        {
            public string Path = "";
            public string Error = "";
        }
        DiscPicker _picker;

        // the disc to try at start, or null when none is known yet
        string StartDiscPath(out string why)
        {
            why = "";
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(DiscPath) && File.Exists(DiscPath)) return DiscPath;
#endif
            string p = PlayerPrefs.GetString(DiscPref, "");
            if (p.Length == 0) return null;
            if (File.Exists(p)) return p;
            why = "The disc file chosen last time is no longer there:\n" + p;
            return null;
        }

        /// <summary>Opens and checks a disc image. Null when it is the USA disc (then _disc is set and the path remembered), else the reason.</summary>
        string OpenDisc(string path)
        {
            path = DiscCheck.ResolveCue((path ?? "").Trim().Trim('"'));
            if (path.Length == 0) return "Choose your disc image first.";
            if (!File.Exists(path)) return "File not found:\n" + path;
            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var disc = new DiscImage(fs);
                string bad = DiscCheck.Check(disc);
                if (bad != null) { fs.Dispose(); return bad; }
                _fs = fs; _disc = disc;
                PlayerPrefs.SetString(DiscPref, path);
                PlayerPrefs.Save();
                return null;
            }
            catch (Exception e)
            {
                if (fs != null) fs.Dispose();
                return e is InvalidDataException || e is EndOfStreamException
                    ? "This file is not a PlayStation disc image (.bin or .iso)."
                    : "The file could not be read: " + e.Message;
            }
        }

        void UsePickedDisc()
        {
            string err = OpenDisc(_picker.Path);
            if (err != null) { _picker.Error = err; return; }
            _picker = null;
            try { Load(); }
            catch (Exception e) { _error = e.Message; Debug.LogException(e); }
        }

        string BrowseForDisc()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorUtility.OpenFilePanelWithFilters("Choose your Digimon World disc", "", new[] { "Disc image", "bin,iso,cue", "All files", "*" });
#elif UNITY_STANDALONE_WIN
            return WinOpenFile.Show();
#else
            return null;
#endif
        }

        GUIStyle _pickTitle, _pickText, _pickError;

        void DrawDiscPicker()
        {
            var p = _picker;
            UiKit.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.06f, 0.06f, 0.09f));
            float s = Mathf.Max(1f, Screen.height / 720f);
            if (_pickTitle == null)
            {
                _pickTitle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
                _pickText = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, wordWrap = true };
                _pickError = new GUIStyle(_pickText);
            }
            _pickTitle.fontSize = Mathf.RoundToInt(30 * s);
            _pickText.fontSize = _pickError.fontSize = Mathf.RoundToInt(17 * s);
            _pickTitle.normal.textColor = Color.white;
            _pickText.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            _pickError.normal.textColor = new Color(1f, 0.55f, 0.45f);

            float w = Mathf.Min(Screen.width - 40f, 760f * s), x = (Screen.width - w) / 2f, y = Screen.height * 0.18f;
            GUI.Label(new Rect(x, y, w, 50 * s), "Choose your Digimon World disc", _pickTitle);
            y += 64 * s;
            GUI.Label(new Rect(x, y, w, 90 * s),
                "This remake needs your own copy of the USA PlayStation disc (SLUS-01032) as a .bin, .iso or .cue file.\n" +
                "The file is only read on this computer. Nothing from it is uploaded or copied.", _pickText);
            y += 100 * s;

#if UNITY_WEBGL && !UNITY_EDITOR
            GUI.Label(new Rect(x, y, w, 60 * s), "Loading a disc in the browser version is not built yet.", _pickError);
#else
            var field = new GUIStyle(GUI.skin.textField) { fontSize = _pickText.fontSize };
            var button = new GUIStyle(GUI.skin.button) { fontSize = _pickText.fontSize };
            float bh = 36 * s;
            p.Path = GUI.TextField(new Rect(x, y, w - 150 * s, bh), p.Path, field);
            if (GUI.Button(new Rect(x + w - 140 * s, y, 140 * s, bh), "Browse...", button))
            {
                string got = BrowseForDisc();
                if (!string.IsNullOrEmpty(got)) { p.Path = got; UsePickedDisc(); }
            }
            y += bh + 14 * s;
            if (GUI.Button(new Rect(x + w / 2f - 110 * s, y, 220 * s, bh), "Use this disc", button)) UsePickedDisc();
            y += bh + 24 * s;
            if (_picker != null && p.Error.Length > 0) GUI.Label(new Rect(x, y, w, 120 * s), p.Error, _pickError);
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // The Windows "Open" dialog (comdlg32 GetOpenFileNameW). Text buffers are passed as raw memory so the dialog can write the path back.
        static class WinOpenFile
        {
            [StructLayout(LayoutKind.Sequential)]
            struct OpenFileName
            {
                public int lStructSize;
                public IntPtr hwndOwner, hInstance, lpstrFilter, lpstrCustomFilter;
                public int nMaxCustFilter, nFilterIndex;
                public IntPtr lpstrFile;
                public int nMaxFile;
                public IntPtr lpstrFileTitle;
                public int nMaxFileTitle;
                public IntPtr lpstrInitialDir, lpstrTitle;
                public int Flags;
                public short nFileOffset, nFileExtension;
                public IntPtr lpstrDefExt, lCustData, lpfnHook, lpTemplateName, pvReserved;
                public int dwReserved, FlagsEx;
            }

            [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)] static extern bool GetOpenFileNameW(ref OpenFileName ofn);
            [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();

            public static string Show()
            {
                const int Max = 4096;
                IntPtr file = Marshal.AllocHGlobal(Max * 2), filter = IntPtr.Zero, title = IntPtr.Zero;
                try
                {
                    Marshal.WriteInt16(file, 0);
                    filter = Marshal.StringToHGlobalUni("Disc image (*.bin, *.iso, *.cue)\0*.bin;*.iso;*.cue\0All files\0*.*\0\0");
                    title = Marshal.StringToHGlobalUni("Choose your Digimon World disc");
                    var o = new OpenFileName
                    {
                        lStructSize = Marshal.SizeOf(typeof(OpenFileName)),
                        hwndOwner = GetActiveWindow(),
                        lpstrFilter = filter, nFilterIndex = 1,
                        lpstrFile = file, nMaxFile = Max,
                        lpstrTitle = title,
                        Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008,      // OFN_EXPLORER | FILEMUSTEXIST | PATHMUSTEXIST | NOCHANGEDIR
                    };
                    return GetOpenFileNameW(ref o) ? Marshal.PtrToStringUni(file) : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(file);
                    if (filter != IntPtr.Zero) Marshal.FreeHGlobal(filter);
                    if (title != IntPtr.Zero) Marshal.FreeHGlobal(title);
                }
            }
        }
#endif
    }
}
