using System.Threading;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (the user's request 2026-11-14): the Windows build runs at most two copies at once (two players on one PC, e.g. to try
    /// ONLINE BATTLE). Each copy takes one of two named Windows mutexes when it starts and holds it until it closes (Windows lets go of
    /// it then, even after a crash); a third copy finds both taken, says so and closes. The editor and the web build are not limited.
    /// </summary>
    public static class InstanceLimit
    {
        public const int Max = 2;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        static Mutex _held;      // kept for the whole run: letting it go would free the place

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int MessageBoxW(System.IntPtr owner, string text, string caption, uint type);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Take()
        {
            for (int i = 0; i < Max; i++)
            {
                Mutex m = null;
                try
                {
                    // Local\: this Windows sign-in only; the name is ours alone
                    m = new Mutex(false, "Local\\DW1UnityBattle_Copy" + i);
                    bool mine;
                    try { mine = m.WaitOne(0); }
                    catch (AbandonedMutexException) { mine = true; }      // the copy that had it closed without letting go: the place is free
                    if (mine) { _held = m; return; }
                    m.Dispose();
                }
                catch (System.Exception e)
                {
                    // the limit cannot be checked (should not happen on Windows): let the game run rather than refuse it
                    Debug.LogWarning("[instances] not checked: " + e.Message);
                    if (m != null) m.Dispose();
                    return;
                }
            }
            MessageBoxW(System.IntPtr.Zero,
                "Digimon World 1 Unity Battle is already open twice. Close one of them to open it again.\n\n" +
                "Digimon World 1 Unity Battle 已經開啟兩個視窗。請先關閉其中一個再開啟。",
                "Digimon World 1 Unity Battle", 0x30);      // MB_ICONWARNING
            Application.Quit();
        }
#endif
    }
}
