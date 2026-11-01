using System;
using System.Collections.Generic;
using System.IO;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEditor;
using UnityEngine;

namespace DW1Remake.EditorTools
{
    /// <summary>Menu: DW1 Remake > Digimon Viewer. Builds a Digimon straight from the disc into the scene.</summary>
    public class DigimonViewerWindow : EditorWindow
    {
        const string PrefKey = "DW1Remake.DiscPath";
        List<DigimonEntry> _list = new List<DigimonEntry>();
        string _filter = "";
        Vector2 _scroll;
        GameObject _spawned;
        string _status = "";

        [MenuItem("DW1 Remake/Digimon Viewer")]
        static void Open() { GetWindow<DigimonViewerWindow>("DW1 Digimon Viewer"); }

        void OnEnable() { Reload(EditorPrefs.GetString(PrefKey, "")); }

        void Reload(string path)
        {
            _list.Clear();
            if (!File.Exists(path)) { _status = "No disc chosen yet. Use DW1 Remake > Disc Browser > Choose disc first."; return; }
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    _list = DigimonCatalog.Load(new DiscImage(fs));
                _status = _list.Count + " Digimon found on the disc.";
            }
            catch (Exception ex) { _status = "Error: " + ex.Message; }
        }

        void Spawn(DigimonEntry e)
        {
            try
            {
                using (var fs = new FileStream(EditorPrefs.GetString(PrefKey, ""), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var disc = new DiscImage(fs);
                    var model = MmdModel.Parse(disc.ReadFile(e.MmdPath));
                    byte[] allTim = disc.ReadFile("/CHDAT/ALLTIM.TIM");
                    if (_spawned != null) DestroyImmediate(_spawned);
                    _spawned = model.Build(e, allTim);
                    Selection.activeGameObject = _spawned;
                    if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
                    _status = e.Name + ": " + model.Parts.Count + " parts, " + e.BoneCount + " bones.";
                }
            }
            catch (Exception ex) { _status = "Error: " + ex.Message; Debug.LogException(ex); }
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(_status, MessageType.None);
            if (_spawned != null)
            {
                var an = _spawned.GetComponent<DigimonAnimator>();
                if (an != null && an.SlotCount > 0)
                {
                    EditorGUI.BeginChangeCheck();
                    int slot = EditorGUILayout.IntSlider("Animation slot", an.Slot, 0, an.SlotCount - 1);
                    an.Playing = EditorGUILayout.Toggle("Playing", an.Playing);
                    an.TicksPerSecond = EditorGUILayout.Slider("Speed (ticks/sec)", an.TicksPerSecond, 5f, 120f);
                    EditorGUI.EndChangeCheck();
                    if (slot != an.Slot) an.SetSlot(slot);
                    if (!an.HasSlot(slot)) EditorGUILayout.LabelField("(this slot is empty)");
                }
            }
            if (GUILayout.Button("Reload list")) Reload(EditorPrefs.GetString(PrefKey, ""));
            _filter = EditorGUILayout.TextField("Search", _filter);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var e in _list)
            {
                if (_filter.Length > 0 && e.ToString().IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GUILayout.Button(e.ToString(), EditorStyles.label)) Spawn(e);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
