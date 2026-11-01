using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEditor;
using UnityEngine;

namespace DW1Remake.EditorTools
{
    /// <summary>Menu: DW1 Remake > Map Viewer. Shows the game's pre-rendered map backgrounds.</summary>
    public class MapViewerWindow : EditorWindow
    {
        const string PrefKey = "DW1Remake.DiscPath";
        List<string> _maps = new List<string>();
        string _filter = "";
        Vector2 _scroll;
        string _status = "";
        GameObject _quad;
        MapBackground _current;
        string _currentName;
        int _palette;

        [MenuItem("DW1 Remake/Map Viewer")]
        static void Open() { GetWindow<MapViewerWindow>("DW1 Map Viewer"); }

        void OnEnable()
        {
            _maps.Clear();
            string path = EditorPrefs.GetString(PrefKey, "");
            if (!File.Exists(path)) { _status = "No disc chosen yet. Use DW1 Remake > Disc Browser > Choose disc first."; return; }
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var disc = new DiscImage(fs);
                    _maps = disc.Files.Where(f => !f.IsDirectory && f.Path.StartsWith("/MAP/") && f.Path.EndsWith(".MAP")).Select(f => f.Path).ToList();
                }
                _status = _maps.Count + " maps found on the disc.";
            }
            catch (Exception ex) { _status = "Error: " + ex.Message; }
        }

        void Show(string mapPath)
        {
            try
            {
                using (var fs = new FileStream(EditorPrefs.GetString(PrefKey, ""), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var disc = new DiscImage(fs);
                    _current = MapBackground.Parse(disc.ReadFile(mapPath), disc.ReadFile(mapPath.Substring(0, mapPath.Length - 4) + ".TFS"));
                    _currentName = Path.GetFileNameWithoutExtension(mapPath);
                    _palette = 0;
                    Rebuild();
                    _status = _currentName + ": " + _current.TilesX + " x " + _current.TilesY + " tiles (" + _current.PixelWidth + " x " + _current.PixelHeight +
                              " px), " + _current.PaletteCount + " palette(s). Camera at " + _current.Viewpoint + " looking at " + _current.ReferencePoint + ", view distance " + _current.ViewDistance;
                }
            }
            catch (Exception ex) { _status = "Error: " + ex.Message; Debug.LogException(ex); }
        }

        void Rebuild()
        {
            if (_quad != null) { var old = _quad.GetComponent<MeshRenderer>().sharedMaterial; if (old != null && old.mainTexture != null) DestroyImmediate(old.mainTexture); DestroyImmediate(_quad); }
            if (_current == null) return;
            var tex = _current.BuildTexture(_palette);
            _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quad.name = "Map_" + _currentName;
            DestroyImmediate(_quad.GetComponent<MeshCollider>());
            _quad.transform.localScale = new Vector3(_current.PixelWidth / 100f, _current.PixelHeight / 100f, 1f);
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(sh);
            mat.SetTexture("_BaseMap", tex);
            _quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            Selection.activeGameObject = _quad;
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(_status, MessageType.None);
            if (_current != null && _current.PaletteCount > 1)
            {
                int p = EditorGUILayout.IntSlider("Palette (0 day, 1 evening, 2 night)", _palette, 0, _current.PaletteCount - 1);
                if (p != _palette) { _palette = p; Rebuild(); }
            }
            _filter = EditorGUILayout.TextField("Search", _filter);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var m in _maps)
            {
                if (_filter.Length > 0 && m.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GUILayout.Button(m, EditorStyles.label)) Show(m);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
