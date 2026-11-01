using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Sound effects and music, built at run time from SOUND/VHB on the player's disc (main/sound.c, script_interp.c).
    /// Banks as in the game: 0 SS and 1 SL system sounds, 3 the partner's voice (VLALL), 4-7 Digimon sounds (VBALL), 8 the map's sounds (ESALL), music fonts in FAALL.
    /// </summary>
    public sealed class GameAudio
    {
        const int Sector = 2048, FontSectors = 0x27;
        const uint MapSoundPara = 0x80127FF4, VoiceIds = 0x80127EFC, DigimonIds = 0x80127F40;
        const float MusicVolume = 0x50 / 127f;

        readonly DiscImage _disc;
        readonly ScriptState _s;
        readonly byte[] _exe; readonly uint _load;
        readonly byte[] _sb, _vl, _vb, _es, _fa;
        readonly VabBank[] _banks = new VabBank[9];
        readonly Dictionary<int, VabBank> _digimon = new Dictionary<int, VabBank>();
        readonly Dictionary<long, AudioClip> _clips = new Dictionary<long, AudioClip>();
        readonly AudioSource[] _fx = new AudioSource[14];   // the game gives sounds 14 voices
        readonly AudioSource _intro, _loop;
        int _nextFx, _map = -1, _partner = -1;

        sealed class Track { public AudioClip Intro, Loop; public bool Repeats; }
        readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        SeqRenderer _render; int _renderKey = -1, _wanted = -1; bool _wantedRepeat;
        public int ActiveFont = 0xFF, ActiveTrack;
        int _playingKey = -1;

        public GameAudio(DiscImage disc, ScriptState state, GameObject host)
        {
            _disc = disc; _s = state;
            _exe = disc.ReadFile("/SLUS_010.32");
            _load = BitConverter.ToUInt32(_exe, 0x18);
            _banks[0] = VabBank.Parse(disc.ReadFile("/SOUND/VHB/SS.VHB"), 0);
            _banks[1] = VabBank.Parse(disc.ReadFile("/SOUND/VHB/SL.VHB"), 0);
            _vl = disc.ReadFile("/SOUND/VHB/VLALL.VHB");
            _vb = disc.ReadFile("/SOUND/VHB/VBALL.VHB");
            _es = disc.ReadFile("/SOUND/VHB/ESALL.VHB");
            _fa = disc.ReadFile("/SOUND/VHB/FAALL.VHB");
            for (int i = 0; i < _fx.Length; i++) { _fx[i] = host.AddComponent<AudioSource>(); _fx[i].playOnAwake = false; }
            _intro = host.AddComponent<AudioSource>(); _intro.playOnAwake = false; _intro.volume = MusicVolume;
            _loop = host.AddComponent<AudioSource>(); _loop.playOnAwake = false; _loop.volume = MusicVolume; _loop.loop = true;
        }

        byte Exe(uint addr) { return _exe[(int)(addr - _load + 0x800)]; }
        ushort Exe16(uint addr) { return BitConverter.ToUInt16(_exe, (int)(addr - _load + 0x800)); }

        /// <summary>loadMapSounds: id = the map's flags & 0x1F.</summary>
        public void SetMapSounds(int id)
        {
            if (id == _map || id >= 22) return;
            _map = id;
            _banks[8] = VabBank.Parse(_es, Exe16(MapSoundPara + (uint)id * 4) / 2 * Sector);
            ForgetClips(8);
        }

        /// <summary>loadPartnerSounds</summary>
        public void SetPartner(int type)
        {
            if (type == _partner) return;
            _partner = type;
            int id = type > 0 && type < 66 ? Exe(VoiceIds + (uint)type) : 15;
            _banks[3] = VabBank.Parse(_vl, id * 15 * Sector);
            ForgetClips(3);
        }

        VabBank DigimonBank(int type)
        {
            VabBank b;
            if (_digimon.TryGetValue(type, out b)) return b;
            b = VabBank.Parse(_vb, Exe(DigimonIds + (uint)type) * 7 * Sector);
            _digimon[type] = b;
            return b;
        }

        void ForgetClips(int bank)
        {
            var old = new List<long>();
            foreach (var k in _clips.Keys) if (k >> 32 == bank) old.Add(k);
            foreach (var k in old) { UnityEngine.Object.Destroy(_clips[k]); _clips.Remove(k); }
        }

        /// <summary>playSound(bank, val)</summary>
        public void Play(int bank, int val)
        {
            if (bank == 0 || bank == 1 || bank == 8) Effect(bank, _banks[bank], val / 16, (val % 16) + 0x3C);
            else if (bank == 3) Effect(3, _banks[3], 0, val);
        }

        /// <summary>A sound event in a Digimon's animation. Bank 4 means the Digimon's own sounds.</summary>
        public void PlayAnim(int type, int bank, int val)
        {
            if (bank != 4) { Play(bank, val); return; }
            if (type >= 0 && type < 180) Effect(100 + type, DigimonBank(type), 0, val);
        }

        void Effect(int key, VabBank bank, int prog, int note)
        {
            if (bank == null) return;
            long id = (long)key << 32 | (long)(prog << 8 | note);
            AudioClip clip;
            if (!_clips.TryGetValue(id, out clip))
            {
                float[] pcm = SeqRenderer.Effect(bank, prog, note);
                clip = pcm.Length == 0 ? null : AudioClip.Create("sound", pcm.Length, 1, SeqRenderer.Rate, false);
                if (clip != null) clip.SetData(pcm, 0);
                _clips[id] = clip;
            }
            if (clip == null) return;
            _fx[_nextFx].PlayOneShot(clip);
            _nextFx = (_nextFx + 1) % _fx.Length;
        }

        // handleMusicOverride: variable 245 chooses; 0 = by the hour
        void Override(int hour, ref int font, ref int track)
        {
            switch (_s.Vars[245])
            {
                case 0: track = hour >= 6 && hour < 21 ? 0 : 1; break;
                case 1: track = 0; break;
                case 2: font = 6; track = 2; break;
                case 3: font = 0xB; track = 2; break;
                case 4: font = 0xF; track = 2; break;
                case 5: font = 0x15; track = 0; break;
                case 6: font = 0x15; track = 1; break;
                case 7: font = 0x1A; track = 0; break;
                case 8: font = 0x1A; track = 1; break;
                case 9: font = 0x1B; track = 0; break;
                case 10: font = 0x1B; track = 1; break;
            }
        }

        /// <summary>playBGM: the scripts' command 0x5D. 255 = silence.</summary>
        public void SetBgm(int id, int hour)
        {
            if (id == 0xFF) { ActiveFont = 0xFF; StopMusic(); return; }
            int font = id, track = 0;
            Override(hour, ref font, ref track);
            ActiveFont = font; ActiveTrack = track;
            PlayMusic(font, track);
        }

        /// <summary>The fight's music (btl.c): font 33; a scripted fight track 0, a wild one track 1, some strong Digimon track 2.</summary>
        public void BattleMusic(bool scripted, int enemyType)
        {
            int track = scripted ? 0 : enemyType >= 0x43 && enemyType < 0x70 ? Exe(0x8012BAE4 + (uint)(enemyType - 0x43)) : 1;
            PlayMusic(0x21, track);
        }

        /// <summary>Back to the map's music after a fight or a scene.</summary>
        public void RestoreBgm() { if (ActiveFont != 0xFF) PlayMusic(ActiveFont, ActiveTrack); }

        /// <summary>updateBGM: the clock can change the track.</summary>
        public void UpdateBgm(int hour)
        {
            if (ActiveFont == 0xFF) return;
            int font = ActiveFont, track = ActiveTrack;
            Override(hour, ref font, ref track);
            if (font != ActiveFont || track != ActiveTrack) { ActiveFont = font; ActiveTrack = track; PlayMusic(font, track); }
        }

        public void StopMusic() { _wanted = -1; _playingKey = -1; _intro.Stop(); _loop.Stop(); }

        /// <summary>playMusic: font 1-33 in FAALL.VHB, a track in its song table. Songs are made over a few frames, then kept.</summary>
        public void PlayMusic(int font, int track)
        {
            if (font <= 0 || font > 0x21) return;
            int file = (font - 1) * FontSectors * Sector;
            int songs = BitConverter.ToInt32(_fa, file + 8), count = BitConverter.ToInt32(_fa, file + songs) >> 2;
            if (track >= count) track = 0;
            int key = font << 8 | track;
            if (key == _playingKey) return;
            _intro.Stop(); _loop.Stop();
            _playingKey = -1;
            _wanted = key;
            _wantedRepeat = !((font == 0x1D && track != 1) || (font >= 0x1E && font <= 0x20 && track != 2));
            if (_tracks.ContainsKey(key)) { Start(); return; }
            if (_renderKey == key) return;
            var bank = VabBank.Parse(_fa, file);
            var song = SeqSong.Parse(_fa, file + songs + BitConverter.ToInt32(_fa, file + songs + track * 4));
            _render = new SeqRenderer(bank, song); _renderKey = key;
        }

        /// <summary>Each frame: a little more of the song being made.</summary>
        public void Tick()
        {
            if (_render == null) return;
            if (!_render.Advance(SeqRenderer.Rate / 4)) return;
            var r = _render; int key = _renderKey; _render = null; _renderKey = -1;
            float[] pcm = r.Pcm;
            int frames = pcm.Length / 2, from = r.LoopStart >= 0 ? r.LoopStart : 0;
            if (frames < 1) return;
            var t = new Track { Repeats = true };
            if (from > 0) { t.Intro = AudioClip.Create("intro", from, 2, SeqRenderer.Rate, false); var a = new float[from * 2]; Array.Copy(pcm, a, a.Length); t.Intro.SetData(a, 0); }
            t.Loop = AudioClip.Create("music", frames - from, 2, SeqRenderer.Rate, false);
            var b = new float[(frames - from) * 2]; Array.Copy(pcm, from * 2, b, 0, b.Length); t.Loop.SetData(b, 0);
            _tracks[key] = t;
            if (key == _wanted) Start();
        }

        void Start()
        {
            var t = _tracks[_wanted];
            _playingKey = _wanted;
            double at = AudioSettings.dspTime + 0.1;
            if (t.Intro != null) { _intro.clip = t.Intro; _intro.PlayScheduled(at); at += t.Intro.samples / (double)SeqRenderer.Rate; }
            _loop.clip = t.Loop; _loop.loop = _wantedRepeat;
            _loop.PlayScheduled(at);
        }
    }
}
