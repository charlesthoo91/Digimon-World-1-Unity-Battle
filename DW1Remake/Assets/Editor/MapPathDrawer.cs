using System;
using System.Collections.Generic;
using System.IO;
using DW1Remake.Disc;
using DW1Remake.Formats;
using DW1Remake.Game;
using UnityEditor;
using UnityEngine;

namespace DW1Remake.EditorTools
{
    /// <summary>Drop-down list of all maps (grouped by area) for the Map Path field of the PlayableRoom.</summary>
    [CustomPropertyDrawer(typeof(MapPathAttribute))]
    public sealed class MapPathDrawer : PropertyDrawer
    {
        static string _cachedDisc;
        static List<string> _names;

        static List<string> Names(string discPath)
        {
            if (_names != null && _cachedDisc == discPath) return _names;
            _cachedDisc = discPath; _names = null;
            try
            {
                if (!File.Exists(discPath)) return null;
                using (var fs = new FileStream(discPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    _names = MapCatalog.Load(new DiscImage(fs));
            }
            catch (Exception) { _names = null; }
            return _names;
        }

        public override void OnGUI(Rect pos, SerializedProperty prop, GUIContent label)
        {
            var disc = prop.serializedObject.FindProperty("DiscPath");
            var names = disc != null ? Names(disc.stringValue) : null;
            if (names == null) { EditorGUI.PropertyField(pos, prop, label); return; }

            string current = Path.GetFileNameWithoutExtension(prop.stringValue);
            var r = EditorGUI.PrefixLabel(pos, label);
            if (EditorGUI.DropdownButton(r, new GUIContent(string.IsNullOrEmpty(current) ? "(choose a map)" : current), FocusType.Keyboard))
            {
                var menu = new GenericMenu();
                string target = prop.propertyPath;
                var so = prop.serializedObject;
                for (int i = 0; i < names.Count; i++)
                {
                    string n = names[i];
                    if (string.IsNullOrEmpty(n)) continue;
                    string group = n.Length >= 4 ? n.Substring(0, 4) : n;
                    int id = i;
                    string path = MapCatalog.PathFor(names, id);
                    menu.AddItem(new GUIContent(group + "/" + n), n == current, () =>
                    {
                        so.Update();
                        so.FindProperty(target).stringValue = path;
                        so.ApplyModifiedProperties();
                        // while playing: go there right away
                        if (Application.isPlaying)
                            foreach (var room in UnityEngine.Object.FindObjectsByType<PlayableRoom>(FindObjectsSortMode.None)) room.GotoMap(path);
                    });
                }
                menu.DropDown(r);
            }
        }
    }
}
