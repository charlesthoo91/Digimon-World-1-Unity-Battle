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
    /// <summary>Menu: DW1 Remake > Disc Browser. Look inside the disc without extracting anything.</summary>
    public class DiscBrowserWindow : EditorWindow
    {
        const string PrefKey = "DW1Remake.DiscPath";

        FileStream _fs;
        DiscImage _disc;
        List<DiscFile> _tims = new List<DiscFile>();
        string _filter = "";
        Vector2 _scroll;
        DiscFile _sel;
        List<TimImage> _images = new List<TimImage>();
        int _imgIdx, _pal;
        Texture2D _tex;
        string _status = "Click 'Choose disc' and pick game.bin";

        [MenuItem("DW1 Remake/Disc Browser")]
        static void Open() { GetWindow<DiscBrowserWindow>("DW1 Disc Browser"); }

        void OnEnable()
        {
            string p = EditorPrefs.GetString(PrefKey, "");
            if (File.Exists(p)) LoadDisc(p);
        }

        void OnDisable()
        {
            if (_fs != null) _fs.Dispose();
            if (_tex != null) DestroyImmediate(_tex);
        }

        void LoadDisc(string path)
        {
            try
            {
                if (_fs != null) _fs.Dispose();
                _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                _disc = new DiscImage(_fs);
                _tims = _disc.Files.Where(f => !f.IsDirectory && f.Path.EndsWith(".TIM", StringComparison.OrdinalIgnoreCase)).ToList();
                EditorPrefs.SetString(PrefKey, path);
                _status = "Disc OK. " + _tims.Count + " .TIM texture files found.";
            }
            catch (Exception e) { _status = "Could not read disc: " + e.Message; _disc = null; }
        }

        void Select(DiscFile f)
        {
            _sel = f; _imgIdx = 0; _pal = 0;
            try
            {
                _images = TimImage.ParseAll(_disc.ReadFile(f.Path));
                _status = _images.Count == 0 ? f.Path + ": no standard TIM image found (may be a container file)." : f.Path + ": " + _images.Count + " image(s)";
            }
            catch (Exception e) { _images.Clear(); _status = "Error: " + e.Message; }
            Rebuild();
        }

        void Rebuild()
        {
            if (_tex != null) DestroyImmediate(_tex);
            _tex = null;
            if (_images.Count == 0) return;
            _imgIdx = Mathf.Clamp(_imgIdx, 0, _images.Count - 1);
            var img = _images[_imgIdx];
            _pal = Mathf.Clamp(_pal, 0, img.PaletteCount - 1);
            _tex = img.ToTexture(_pal);
        }

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Choose disc...", EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                string p = EditorUtility.OpenFilePanel("Select your Digimon World disc image", "", "bin,iso,img");
                if (!string.IsNullOrEmpty(p)) LoadDisc(p);
            }
            GUILayout.Label(_status);
            EditorGUILayout.EndHorizontal();
            if (_disc == null) return;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.Width(300));
            _filter = EditorGUILayout.TextField("Search", _filter);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var f in _tims)
            {
                if (_filter.Length > 0 && f.Path.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var style = f == _sel ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button(f.Path + "  (" + f.Size / 1024 + " KB)", style)) Select(f);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            if (_images.Count > 0)
            {
                var img = _images[_imgIdx];
                EditorGUILayout.LabelField(img.Width + " x " + img.Height + ", " + img.BitsPerPixel + "-bit, " + img.PaletteCount + " palette(s)");
                EditorGUI.BeginChangeCheck();
                if (_images.Count > 1) _imgIdx = EditorGUILayout.IntSlider("Image", _imgIdx, 0, _images.Count - 1);
                if (img.PaletteCount > 1) _pal = EditorGUILayout.IntSlider("Palette", _pal, 0, img.PaletteCount - 1);
                if (EditorGUI.EndChangeCheck()) Rebuild();
                if (_tex != null)
                {
                    Rect r = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                    EditorGUI.DrawRect(r, new Color(0.2f, 0.2f, 0.2f));
                    float s = Mathf.Min(r.width / _tex.width, r.height / _tex.height);
                    var tr = new Rect(r.x + (r.width - _tex.width * s) / 2, r.y + (r.height - _tex.height * s) / 2, _tex.width * s, _tex.height * s);
                    GUI.DrawTexture(tr, _tex, ScaleMode.ScaleToFit, true);
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }
    }
}
