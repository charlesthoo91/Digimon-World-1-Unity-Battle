using System.Runtime.InteropServices;

namespace DW1Remake.Game
{
    /// <summary>
    /// The web build's file and clipboard helpers (ours; Plugins/WebGL/DW1Web.jslib). Elsewhere they do nothing.
    /// Answers that come later arrive as SendMessage(objectName, method, text) on the PlayableRoom.
    /// </summary>
    public static class WebFiles
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void DW1_PickDisc(string objectName, string method);
        [DllImport("__Internal")] static extern void DW1_SyncFiles();
        [DllImport("__Internal")] static extern void DW1_KeepDisc();
        [DllImport("__Internal")] static extern void DW1_LoadKeptDisc(string objectName, string method);
        [DllImport("__Internal")] static extern void DW1_CopyText(string text);
        [DllImport("__Internal")] static extern void DW1_PasteText(string objectName, string method);
        [DllImport("__Internal")] static extern void DW1_DownloadText(string name, string text);
        [DllImport("__Internal")] static extern void DW1_PickText(string objectName, string method);
#endif

        /// <summary>The browser's choose-file box for the disc; the answer: a path in the page's memory, "" cancelled, "#" note, "!" error.</summary>
        public static void PickDisc(string objectName, string method)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_PickDisc(objectName, method);
#endif
        }

        /// <summary>The disc kept from an earlier visit, answered like PickDisc ("" = none kept).</summary>
        public static void LoadKeptDisc(string objectName, string method)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_LoadKeptDisc(objectName, method);
#endif
        }

        /// <summary>Keeps the disc just chosen (and checked) in the browser's storage for the next visit.</summary>
        public static void KeepDisc()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_KeepDisc();
#endif
        }

        /// <summary>After writing or deleting a save: keeps it in the browser's storage.</summary>
        public static void Sync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_SyncFiles();
#endif
        }

        public static void CopyText(string text)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_CopyText(text);
#endif
        }

        /// <summary>The browser's clipboard; the answer is its text ("" = empty or not allowed).</summary>
        public static void PasteText(string objectName, string method)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_PasteText(objectName, method);
#endif
        }

        /// <summary>A text file to the browser's Downloads folder.</summary>
        public static void DownloadText(string name, string text)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_DownloadText(name, text);
#endif
        }

        /// <summary>The choose-file box for a .json file; the answer is its text ("" = cancelled).</summary>
        public static void PickText(string objectName, string method)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DW1_PickText(objectName, method);
#endif
        }
    }
}
