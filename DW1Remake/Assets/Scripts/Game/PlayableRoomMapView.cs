using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The area names (MAP_NAME_PTR 0x801291BC, MAP_ENTRIES[map].loadingName at +15), shown two ways:
    /// - the original's: addMapNameObject / renderMapName (map.c) draws the next area's name on the fading picture from fade frame 10 until the map
    ///   has loaded (tamerTickChangeMap always; scriptTickChangeMap when SCRIPT_MAP_CHANGE_SHOW_NAME, consumeMapChangeShowName);
    /// - ours, the mod Map (title > OPTIONS, off by default): Select while the boy walks freely opens a see-through map of this screen drawn from
    ///   its walking grid (the exits marked with the area they lead to, the boy, the partner and the map Digimon as dots); a fight, a script, the
    ///   menus or fishing close it.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        string[] _areaNames;          // per map id
        bool _sectionEvent;           // SCRIPT_SECTION_IS_EVENT: the running section was started as an event (a map Digimon talked to, the map's setup)
        string _mapNameShown;         // the name renderMapName draws now (null = none)
        MapNameView _mapNameView;

        string AreaName(int mapId)
        {
            if (_areaNames == null)
            {
                _areaNames = new string[0];
                try
                {
                    byte[] exe = _disc.ReadFile("/SLUS_010.32");
                    uint load = System.BitConverter.ToUInt32(exe, 0x18);
                    System.Func<uint, int> at = a => (int)(a - load + 0x800);
                    var names = new List<string>();
                    for (int i = 0; i < 0xFF; i++)
                    {
                        int idx = (sbyte)exe[at(0x801292D4u + (uint)(16 * i)) + 15];
                        string s = "";
                        if (idx >= 0)
                        {
                            uint p = System.BitConverter.ToUInt32(exe, at(0x801291BCu + (uint)(4 * idx)));
                            if (p >= load && p < load + exe.Length)
                            {
                                int o = at(p), e = o;
                                while (e < exe.Length && exe[e] != 0) e++;
                                s = System.Text.Encoding.ASCII.GetString(exe, o, e - o);
                            }
                        }
                        names.Add(s);
                    }
                    _areaNames = names.ToArray();
                }
                catch (System.Exception e) { Debug.LogWarning("[map] area names: " + e.Message); }
            }
            return mapId >= 0 && mapId < _areaNames.Length ? _areaNames[mapId] : "";
        }

        // consumeMapChangeShowName at a script's 4B: variable 6 = 1 shows the name, 2 hides it, 0 shows it unless the section is an event; then 0
        bool ConsumeShowName()
        {
            int v = _state.Vars[6];
            bool show = v == 1 || (v == 0 && !_sectionEvent);
            _state.Vars[6] = 0;
            return show;
        }

        // renderMapName (US): renderString(0, 12 - (length / 2) x 8, -6, length x 8 + 4, 12, 0, 0, 0, 0): colour 0, no shadow, over everything
        void DrawMapName()
        {
            if (string.IsNullOrEmpty(_mapNameShown) || Event.current.type != EventType.Repaint) return;
            if (_mapNameView == null && _disc != null && Battle.StatFont != null) _mapNameView = MapNameView.Load(_disc, Battle.StatFont);
            if (_mapNameView != null) _mapNameView.Draw(_mapNameShown);
        }

        // ------------------------------------------------------------------ ours: the Map mod
        bool _mapViewOn;
        // the map as the map's camera sees it: one texel per 4 x 4 pixels of the whole painted map, coloured by the walking grid tile under it
        // (the ground at the boy's height); so it has the picture's own shape, and the screen is a rectangle on it
        const int MapViewStep = 4;
        Texture2D _mapViewTex; MapBackground _mapViewFor;
        readonly Vector2[] _mapViewExit = new Vector2[10];      // each exit's middle in map pixels (x < 0: none seen)

        /// <summary>From Update, where the Triangle menu can open: Select switches the map.</summary>
        void MapViewKey()
        {
            if (!ModSettings.MapView || !GameMenus.SelectKey()) return;
            _mapViewOn = !_mapViewOn;
            Snd(_mapViewOn ? 0 : 1);
        }

        /// <summary>Closes the map whenever the boy is not walking freely: a fight, a script, a scene, the menus, fishing.</summary>
        void MapViewAutoOff()
        {
            if (!_mapViewOn) return;
            if (!ModSettings.MapView || _runner != null || _pBusy || _routineOn || _fishTamer || _arenaOn || _onlineOn || _otOn
                || (_battle != null && _battle.Active) || (_menus != null && _menus.Active) || (_setup != null && _setup.Active))
                _mapViewOn = false;
        }

        void BuildMapViewTexture()
        {
            if (_mapViewFor == _map && _mapViewTex != null) return;
            _mapViewFor = _map;
            int tw = Mathf.Max(1, _map.PixelWidth / MapViewStep), th = Mathf.Max(1, _map.PixelHeight / MapViewStep);
            if (_mapViewTex != null) Destroy(_mapViewTex);
            _mapViewTex = new Texture2D(tw, th, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[tw * th];
            var sum = new Vector2[10]; var count = new int[10];
            float groundY = _player != null ? _player.position.y : 0f;
            for (int j = 0; j < th; j++)
                for (int i = 0; i < tw; i++)
                {
                    Vector2 mp = new Vector2((i + 0.5f) * MapViewStep, (j + 0.5f) * MapViewStep);
                    Vector3 g = ToGame(WorldAtPixel(mp, groundY));
                    int c = _map.CollisionAt(g.x, g.z);
                    Color32 col;
                    if (c == 0x80 || c == 0xFF) col = new Color32(0, 0, 0, 0);
                    else if (c >= 110 && c < 120) { col = new Color32(255, 140, 40, 240); sum[c - 110] += mp; count[c - 110]++; }     // an exit
                    else if (c == 120) col = new Color32(170, 120, 70, 210);                     // the toilet
                    else if (c > 50 && c < 110) col = new Color32(140, 190, 255, 190);           // a door or a trigger
                    else col = new Color32(230, 230, 205, 140);                                  // ground he can walk on
                    px[(th - 1 - j) * tw + i] = col;
                }
            _mapViewTex.SetPixels32(px); _mapViewTex.Apply();
            for (int e = 0; e < 10; e++) _mapViewExit[e] = count[e] > 0 ? sum[e] / count[e] : new Vector2(-1f, -1f);
        }

        void DrawMapView()
        {
            if (!_mapViewOn || _map == null || _player == null || _map.Collision == null) return;
            if (Event.current.type != EventType.Repaint) return;
            BuildMapViewTexture();
            UiKit.Begin();
            var panel = new Rect(85, 64, 1245, 932);
            UiKit.Fill(panel, new Color(0f, 0f, 0f, 0.55f));
            UiKit.Text(new Rect(panel.x, panel.y + 14, panel.width, 60), AreaName(_mapId).Trim(), 46, UiKit.Yellow, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(panel.x, panel.yMax - 50, panel.width - 20, 40), DW1Remake.Formats.Tr.Text("Select: close", "ours"), 26, new Color(1f, 1f, 1f, 0.7f), TextAnchor.MiddleRight);

            // the whole painted map fitted in the panel
            float W = _map.PixelWidth, H = _map.PixelHeight;
            var area = new Rect(panel.x + 20, panel.y + 80, panel.width - 40, panel.height - 140);
            float s = Mathf.Min(area.width / W, area.height / H);
            var pic = new Rect(area.center.x - W * s * 0.5f, area.center.y - H * s * 0.5f, W * s, H * s);
            System.Func<Vector2, Vector2> place = mp => new Vector2(pic.x + mp.x * s, pic.y + mp.y * s);
            System.Func<Vector3, Vector2> placeWorld = u => place(MapPixel(u));
            GUI.DrawTexture(pic, _mapViewTex);

            // the exits: the area each one leads to, at the middle of its tiles
            for (int e = 0; e < 10; e++)
            {
                if (_mapViewExit[e].x < 0f) continue;
                string name = AreaName(_map.Warps[e].TargetMap).Trim();
                if (string.IsNullOrEmpty(name)) continue;
                Vector2 p = place(_mapViewExit[e]);
                p.x = Mathf.Clamp(p.x, panel.x + 150, panel.xMax - 150); p.y = Mathf.Clamp(p.y, panel.y + 90, panel.yMax - 70);
                UiKit.Text(new Rect(p.x - 150 + 2, p.y - 18 + 2, 300, 36), name, 26, Color.black, TextAnchor.MiddleCenter);
                UiKit.Text(new Rect(p.x - 150, p.y - 18, 300, 36), name, 26, new Color(1f, 0.75f, 0.4f, 1f), TextAnchor.MiddleCenter);
            }

            // what the screen shows now: the picture's window over the painted map (MAP_TILE_DATA.camera)
            {
                float hw = WindowHeightPx * (_cam != null ? _cam.aspect : 4f / 3f) * 0.5f, hh = WindowHeightPx * 0.5f;
                Vector2 a = place(new Vector2(_scrollPx.x - hw, _scrollPx.y - hh)), b = place(new Vector2(_scrollPx.x + hw, _scrollPx.y + hh));
                var frame = new Color(0.45f, 0.85f, 1f, 0.9f);
                UiKit.Fill(new Rect(a.x, a.y, b.x - a.x, 3f), frame); UiKit.Fill(new Rect(a.x, b.y - 3f, b.x - a.x, 3f), frame);
                UiKit.Fill(new Rect(a.x, a.y, 3f, b.y - a.y), frame); UiKit.Fill(new Rect(b.x - 3f, a.y, 3f, b.y - a.y), frame);
            }

            // the map Digimon, the partner, the boy (with a mark the way he faces), at their feet
            float dot = 14f;
            foreach (var npc in _npcs)
                if (npc.T != null && npc.T.gameObject.activeInHierarchy) Dot(placeWorld(npc.T.position), dot, new Color(1f, 0.35f, 0.3f, 1f));
            if (_partner != null && _partner.gameObject.activeInHierarchy) Dot(placeWorld(_partner.position), dot, new Color(0.4f, 1f, 0.45f, 1f));
            Vector2 me = placeWorld(_player.position);
            Vector3 fwd = _player.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
            Vector2 ahead = placeWorld(_player.position + fwd.normalized * 1.5f);
            Vector2 dir = ahead - me;
            if (dir.sqrMagnitude > 0.01f) Dot(me + dir.normalized * dot, dot * 0.6f, UiKit.Yellow);
            Dot(me, dot * 1.2f, UiKit.Yellow);
            UiKit.End();
        }

        static void Dot(Vector2 p, float size, Color c)
        {
            UiKit.Fill(new Rect(p.x - size * 0.5f - 2f, p.y - size * 0.5f - 2f, size + 4f, size + 4f), new Color(0f, 0f, 0f, 0.8f));
            UiKit.Fill(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), c);
        }
    }

    /// <summary>renderMapName's text: the name drawn into the text area at (0, 0) and the slice shown from the middle of the screen.</summary>
    public sealed class MapNameView : UiBoxPage
    {
        MapNameView() { }

        public static MapNameView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || font == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new MapNameView();
            v.Init(disc, font);
            return v;
        }

        public void Draw(string name)
        {
            BuildPage(name, px => Write(px, name, 0, 0));
            Begin();
            int len = name.Length;
            PageText(0, 0, 12 - (len / 2) * 8, -6, len * 8 + 4, 12, 0, 0);
            UiKit.Begin();
            Flush();
            UiKit.End();
        }
    }
}
