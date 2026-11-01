using DW1Remake.Game;
using UnityEditor;
using UnityEngine;

namespace DW1Remake.EditorTools
{
    public static class PlayableRoomMenu
    {
        [MenuItem("DW1 Remake/Create Playable Room")]
        static void Create()
        {
            var go = new GameObject("PlayableRoom");
            var room = go.AddComponent<PlayableRoom>();
            room.DiscPath = EditorPrefs.GetString("DW1Remake.DiscPath", "");
            Selection.activeGameObject = go;
        }
    }
}
