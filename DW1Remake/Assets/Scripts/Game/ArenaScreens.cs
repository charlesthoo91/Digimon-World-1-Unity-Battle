using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// What the arena draws on the picture itself (std/std_main.c, std/std_hud.c, US build), from the arena's video memory (ArenaStage.Vram):
    /// the bracket (STD_updateBracket / STD_drawBracket with its sprites, punches and hit flashes) and its opening (STD_renderBracketIntro),
    /// the entrants' numbers (STD_renderIntroStats), the arena's BATTLE text (STD_renderBattleStartText / ...Burst), the 1P / 2P marks
    /// (STD_renderPlayerMarker), the LOSE scene's black strips and the model scene (WIN / LOSE / draw / CHAMPION letters, rendered by
    /// ArenaStage.Overlay) at ordering table slot 4. Every primitive goes through PsPage's ordering table: higher slots first, inside a slot the
    /// last added first. The US build draws neither the big intro name (STD_renderIntroNameChar adds no primitive) nor the bracket's names.
    /// </summary>
    public sealed class ArenaScreens : PsPage
    {
        const uint StdBase = 0x80052AE0;
        public PsxVram Vram;
        public byte[] Std;
        public Action<int, int> Sound;          // playSound(bank, n)
        System.Random _rng = new System.Random();
        int S16(uint a) { return BitConverter.ToInt16(Std, (int)(a - StdBase)); }
        int U8(uint a) { return Std[(int)(a - StdBase)]; }

        public static ArenaScreens Load(DiscImage disc, GameFont font, byte[] std, PsxVram vram)
        {
            var s = new ArenaScreens { Std = std, Vram = vram };
            s.Init(disc, font);
            s.ReadTables();
            return s;
        }

        short[,] _pathX = new short[8, 8]; byte[,] _pathFlip = new byte[8, 8];
        short[] _rowY = new short[8]; sbyte[] _hopY = new sbyte[24];
        byte[] _spriteClut = new byte[112], _projIcon = new byte[112], _projClut = new byte[16];
        short[] _barLimit = new short[6];
        short[] _textX = new short[155], _textY = new short[155];
        int[] _textM = new int[12];

        void ReadTables()
        {
            for (int i = 0; i < 8; i++)
                for (int k = 0; k < 8; k++) { _pathX[i, k] = (short)S16(0x8007A598 + (uint)(24 * i + 2 * k)); _pathFlip[i, k] = (byte)U8(0x8007A598 + (uint)(24 * i + 16 + k)); }
            for (int i = 0; i < 8; i++) _rowY[i] = (short)S16(0x8007A48C + 2 * (uint)i);
            for (int i = 0; i < 24; i++) _hopY[i] = (sbyte)U8(0x8007A474 + (uint)i);
            for (int i = 0; i < 112; i++) { _spriteClut[i] = (byte)U8(0x8007A49C + (uint)i); _projIcon[i] = (byte)U8(0x8007A50C + (uint)i); }
            for (int i = 0; i < 16; i++) _projClut[i] = (byte)U8(0x8007A57C + (uint)i);
            for (int i = 0; i < 6; i++) _barLimit[i] = (short)S16(0x8007A364 + 2 * (uint)i);
            for (int i = 0; i < 155; i++) { _textX[i] = (short)S16(0x8007A738 + 4 * (uint)i); _textY[i] = (short)S16(0x8007A738 + 4 * (uint)i + 2); }
            for (int i = 0; i < 9; i++) _textM[i] = S16(0x8007A718 + 2 * (uint)i);
            for (int i = 0; i < 3; i++) _textM[9 + i] = BitConverter.ToInt32(Std, (int)(0x8007A718 + 0x14 + 4 * (uint)i - StdBase));
        }

        void Snd(int bank, int n) { if (Sound != null && !Silent) Sound(bank, n); }
        /// <summary>Ours (online tournament): the bracket is moved on without sounds (the rounds already shown, run through at once).</summary>
        public bool Silent;
        /// <summary>Ours (online tournament): the bracket stops (Finished) when STD_BRACKET_MATCH reaches this (5 = after the first round,
        /// 7 = after the second); 0 = the original's stop at the player's own match. With PlayerSlot -1 every match is played out as the others.</summary>
        public int StopAt;

        // ================================================================ the bracket

        sealed class Slot { public int X, Y, Row, Frame, Flip, State, T8, T9, A0, A1, C; }      // StdUnkBAF4: unk0, unk2, unk4, unk5, unk6, unk7, unk8, unk9, unkA[2], unkC
        sealed class Punch { public int X, Y, Flag, Owner; public bool On; }                   // STD_BRACKET_PROJECTILES
        readonly Slot[] _slots = new Slot[8];
        readonly Punch[] _punch = new Punch[8];
        readonly bool[] _flash = new bool[8];
        public readonly byte[] Types = new byte[8];       // PARTICIPANT_TYPES
        public readonly byte[] Round1 = new byte[4], Round2 = new byte[2];      // STD_ROUND1_WINNERS / STD_ROUND2_WINNERS
        public int PlayerSlot;                 // STD_PLAYER_SLOT
        public int Match;                      // STD_BRACKET_MATCH
        public bool Finished;                  // STD_BRACKET_FINISHED
        public int Fade;                       // STD_BRACKET_FADE
        public int MatchResult;                // STD_MATCH_RESULT
        public bool BracketOn, IntroOn;
        int _introTimer, _introDark, _introTitle;     // STD_BRACKET_INTRO_TIMER, MAIN_D_80135165, MAIN_D_80135166
        public bool TitleShort;                // the Beetle Land title is 0x7D lines tall, the others 0x80 (CURRENT_SCREEN == 0x6A)

        /// <summary>STD_initializeBracket</summary>
        public void InitBracket()
        {
            Match = 1;
            for (int i = 0; i < 8; i++)
            {
                _slots[i] = new Slot { X = _pathX[i, 0], Y = _rowY[0], Frame = 0, Row = 1, Flip = _pathFlip[i, 0] };
                _punch[i] = new Punch();
                _flash[i] = false;
            }
        }

        /// <summary>STD_addBracket</summary>
        public void AddBracket() { Finished = false; Fade = 0; BracketOn = true; }
        public void RemoveBracket() { BracketOn = false; for (int i = 0; i < 8; i++) { _punch[i].On = false; _flash[i] = false; } }

        /// <summary>STD_addBracketIntro</summary>
        public void AddIntro() { Fade = 0x80; _introDark = 0x80; _introTitle = 0x80; _introTimer = 0; IntroOn = true; }
        public void RemoveIntro() { IntroOn = false; }

        /// <summary>STD_tickBracketIntro: from frame 101 the bracket fades in, the darkening eases off and the title fades out, 8 a frame.</summary>
        public void TickIntro()
        {
            if (_introTimer < 0x78) _introTimer++;
            if (_introTimer >= 0x65)
            {
                if (Fade != 0) Fade -= 8;
                if (_introDark >= 9) _introDark -= 8;
                if (_introTitle != 0) _introTitle -= 8;
            }
        }

        /// <summary>STD_updateBracket, one frame (it runs from the bracket's render function), then the punches and hit flashes (their own objects).</summary>
        public void TickBracket()
        {
            if (StopAt > 0 && Match >= StopAt) { Finished = true; return; }
            int a, b;
            if (Match < 5) { a = (Match - 1) * 2; b = (Match - 1) * 2 + 1; }
            else if (Match < 7) { a = Round1[(Match - 5) * 2]; b = Round1[(Match - 5) * 2 + 1]; }
            else { a = Round2[0]; b = Round2[1]; }
            bool match = a == PlayerSlot || b == PlayerSlot;
            for (int i = 0; i < 8; i++)
            {
                if (i != a && i != b) continue;
                var s = _slots[i];
                switch (s.State)
                {
                    case 0:
                        s.T8++; s.T9++;
                        if (s.T8 % 10 == 0) s.Frame = (s.Frame + 1) & 1;
                        if (s.T9 >= 0x29) { s.T9 = 0; s.State++; }
                        break;
                    case 1:
                        s.T8++; s.T9++;
                        if (s.T9 == 5) Snd(8, 5);
                        if (s.Y != _rowY[s.Row]) s.Y--;
                        if (s.T8 % 10 == 0) s.Frame = (s.Frame + 1) & 1;
                        if (s.Y == _rowY[s.Row] && s.X == _pathX[i, s.Row]) { s.A1 = 0; s.T9 = 0; s.State++; }
                        break;
                    case 2:
                        s.T8++; s.T9++;
                        if (s.T8 % 10 == 0) s.Frame = (s.Frame + 1) & 1;
                        if (!match)
                        {
                            if (s.T9 >= 0x29)
                            {
                                s.Frame = 2; Snd(8, 1);
                                s.T8 = 0; s.T9 = 0; s.A0 = 0;
                                int flag = 0;
                                if (Match < 5 && i == Round1[Match - 1]) flag = 1;
                                if (Match >= 5 && Match < 7 && i == Round2[Match - 5]) flag = 1;
                                AddPunch(i, i == a ? b : a, flag);
                                s.State++;
                            }
                        }
                        else { Fade += 3; if (Fade >= 0x81) Fade = 0x80; }
                        break;
                    case 3:
                        if (s.Frame == 2) s.T9++;
                        else { s.T8++; if (s.T8 % 10 == 0) s.Frame = (s.Frame + 1) & 1; }
                        if (s.T9 >= 0x15) { s.Frame = 0; s.T8 = 0; s.T9 = 0; }
                        if (s.A0 == 1)
                        {
                            s.A1 = 3; s.T9 = 0; s.T8 = 0; s.State++;
                            if (s.A1 != 3) _flash[i] = true;
                            else if (s.C == 1) _flash[i] = true;
                            else s.Flip = (s.Flip + 1) & 1;
                        }
                        break;
                    case 4:
                        s.T8++;
                        if (s.A1 != 3) { s.Frame = 3; if (s.T8 >= 0x15) { s.T8 = 0; s.State = 2; } }
                        else if (s.C == 1) { s.Frame = 3; if (s.T8 >= 0x1F) { s.T8 = 0; s.State++; Snd(8, 3); } }
                        else
                        {
                            if (s.T8 == 0x14) s.Flip = (s.Flip + 1) & 1;
                            if (s.T8 >= 0x1F) { s.Row++; s.State += 2; if (s.Frame == 2) s.Frame = 0; }
                        }
                        break;
                    case 5:
                        if (s.T8 < 0x18) { s.Y = _rowY[s.Row] + _hopY[s.T8++]; if (s.T8 == 0x14) Snd(8, 4); }
                        else s.State++;
                        break;
                    case 6:
                        if (match)
                        {
                            if (MatchResult == 1)
                            {
                                if (i == PlayerSlot)
                                {
                                    s.X = _pathX[i, s.Row + 1]; s.Frame = 0; s.Flip = _pathFlip[i, s.Row + 1]; s.Row += 2;
                                    s.T9 = 0; s.T8 = 0; s.State = 0;
                                    if (Match < 5) Round1[PlayerSlot / 2] = (byte)PlayerSlot;
                                    if (Match < 7) Round2[PlayerSlot / 4] = (byte)PlayerSlot;
                                    Match++;
                                }
                                else { s.Y = _rowY[s.Row - 1]; s.Frame = 3; }
                            }
                            else if (i == PlayerSlot) { s.Y = _rowY[s.Row - 1]; s.Frame = 3; }
                            else { s.X = _pathX[i, s.Row - 1]; s.Frame = 0; }
                        }
                        else if (s.C == 0)
                        {
                            s.T8++;
                            if (s.T8 % 10 == 0) s.Frame = (s.Frame + 1) & 1;
                            if (_slots[_punch[i].Owner].State == 6)
                            {
                                if (s.X > _pathX[i, s.Row]) s.X--;
                                if (s.X < _pathX[i, s.Row]) s.X++;
                                if (s.Y == _rowY[s.Row] && s.X == _pathX[i, s.Row])
                                {
                                    s.Flip = _pathFlip[i, s.Row]; s.Row++;
                                    s.T9 = 0; s.T8 = 0; s.Frame = 0; s.State = 0;
                                    Match++;
                                }
                            }
                        }
                        break;
                    case 7:
                        s.T8++;
                        if (i == PlayerSlot) { s.X = _pathX[i, 7]; s.Y = _rowY[7]; s.Frame = 0; s.Flip = _pathFlip[i, 7]; }
                        else { s.X = _pathX[i, 4]; s.Y = _rowY[4]; s.Flip = _pathFlip[i, 4]; s.Frame = 3; }
                        if (s.T8 >= 0x3D) Finished = true;
                        break;
                }
            }
            if (match && PlayerSlot >= 0 && Fade == 0x80 && _slots[PlayerSlot].State == 2)
            {
                int st = Match != 7 ? 6 : 7;
                _slots[PlayerSlot].State = st;
                if (Match == 7) _slots[PlayerSlot].T8 = 0;
                int other = PlayerSlot == a ? b : a;
                _slots[other].State = st;
                if (Match == 7) _slots[other].T8 = 0;
                Finished = true;
            }
            for (int i = 0; i < 8; i++) if (_punch[i].On) TickPunch(i);
            for (int i = 0; i < 8; i++) if (_flash[i] && _slots[i].T8 >= 0x10) _flash[i] = false;
        }

        // STD_addBracketProjectile: from the attacker's front; the winner's punch is a double one and marks the other as the loser
        void AddPunch(int i, int owner, int flag)
        {
            var p = _punch[i]; var s = _slots[i];
            if (s.Flip == 1) { p.X = s.X + 0x10; p.Y = s.Y; if (flag == 1) _slots[owner].C = 1; }
            else
            {
                p.X = s.X - 8;
                if (flag == 0) p.Y = s.Y + 8;
                else { p.Y = s.Y; _slots[owner].C = 1; }
            }
            p.Flag = flag; p.Owner = owner; p.On = true;
        }

        // STD_tickBracketProjectile + the hit test of STD_renderBracketProjectile: 1 a frame in the first round, 3 in the second (the final has none)
        void TickPunch(int id)
        {
            var p = _punch[id]; var s = _slots[id];
            int d = Match < 5 ? 1 : Match < 7 ? 3 : 0;
            p.X += s.Flip == 1 ? d : -d;
            var o = _slots[p.Owner];
            if (s.Flip == 1 ? o.X - p.X < 9 : p.X - o.X < 0x11) { o.A0 = 1; p.On = false; }
        }

        // STD_tickBracketHitFlash: shown on frames 4, 9, 14 of the slot's counter, gone from 16
        bool FlashShows(int i) { return _flash[i] && _slots[i].T8 % 5 == 4; }

        // ================================================================ intro numbers (STD_addIntroStats / STD_tickIntroStats / STD_renderIntroStats)

        public bool StatsOn;
        public readonly int[] StatShown = new int[6], StatOf = new int[6];      // STD_INTRO_STATS_DATA; HP, MP, Offense, Defense, Speed, Brains
        public string[] StatLabel = new string[6];

        public void AddStats(int[] values) { if (StatsOn) return; StatsOn = true; Array.Copy(values, StatOf, 6); StatShown[0] = StatShown[1] = -100; for (int i = 2; i < 6; i++) StatShown[i] = -10; }
        public void RemoveStats() { StatsOn = false; }

        public void TickStats()
        {
            StatShown[0] += 200; StatShown[1] += 200;
            for (int i = 2; i < 6; i++) StatShown[i] += 20;
            for (int i = 0; i < 6; i++) if (StatOf[i] < StatShown[i]) StatShown[i] = StatOf[i];
        }

        // ================================================================ the arena's BATTLE text (STD_initializeBattleStartText ... Burst)

        sealed class Piece { public int Rx, Ry, Rz, X, Y, Bx, By; public sbyte Speed; public int Trail, Tile; }
        Piece[] _pieces;
        public int TextStage;                  // 0 none, 1 coming together, 2 bursting
        public bool TextFinished;              // STD_BATTLE_TEXT_FINISHED
        int _textTimer;                        // STD_BATTLE_START_TEXT_TIMER[0]
        int _lead;                             // pieces whose trail is gone, counted from the first

        public void StartText()
        {
            _textTimer = 0; TextFinished = false; TextStage = 1;
            _pieces = new Piece[155];
            for (int i = 0; i < 155; i++) _pieces[i] = new Piece { Trail = 0x18, Tile = _rng.Next(3) };
            for (int i = 0; i < 155; i++)
            {
                var p = _pieces[i];
                int sgn = _rng.Next(2) == 1 ? 1 : -1;
                p.Y = _textY[i];
                p.Speed = (sbyte)(-sgn * ((_rng.Next(3) + 1) << 5));
                p.X = sgn * (i < 0x33 ? 500 : i < 0x65 ? 600 : 700) + _rng.Next(100) - 50;
                int r = _rng.Next(5);
                p.Bx = (r + 8) * _textX[i] / 8; p.By = (r + 8) * _textY[i] / 8;
                p.Rx = p.Ry = p.Rz = 0;
            }
        }

        public void StartBurst() { TextFinished = false; TextStage = 2; }
        public void StopText() { TextStage = 0; _pieces = null; }

        // one frame of the render function's moves
        public void TickText()
        {
            if (_pieces == null) return;
            if (TextStage == 1)
            {
                int n = 0;
                for (int i = 0; i < 155; i++) { if (_pieces[i].Trail != 0) break; n++; }
                _lead = n;
                if (n == 155) { _textTimer++; TextFinished = true; }
                for (int i = 0; i < 155; i++)
                {
                    var p = _pieces[i];
                    if (p.X != _textX[i])
                    {
                        p.X += p.Speed;
                        if (p.Speed > 0 ? p.X > _textX[i] : p.X < _textX[i]) p.X = _textX[i];
                    }
                    else if (p.Trail != 0) p.Trail -= 4;
                }
            }
            else if (TextStage == 2)
            {
                int dead = 0;
                for (int i = 0; i < 155; i++)
                {
                    var p = _pieces[i];
                    if (p.Bx == 0 && p.By == 0) { dead++; continue; }
                    switch (p.Tile) { case 0: p.Rx += 0x100; p.Ry += 0x100; break; case 1: p.Ry += 0x100; p.Rz += 0x100; break; case 2: p.Rx += 0x100; p.Rz += 0x100; break; }
                    Vector2 c0 = TextPoint(BurstCorner(p, 0));
                    if (c0.x < -0xB4 || c0.x >= 0xB5 || c0.y < -0x8C || c0.y >= 0x8D) { p.Bx = 0; p.By = 0; }
                    p.X += p.Bx; p.Y += p.By;
                }
                if (dead == 155) TextFinished = true;
            }
        }

        // the burst piece's corner j: (+-4, +-6) turned by its angles (RotMatrix), then at (x - 4, y - 6)
        Vector3 BurstCorner(Piece p, int j)
        {
            var c = new Vector3(j % 2 == 0 ? -4 : 4, j < 2 ? -6 : 6, 0);
            Vector3 r = ArenaStage.RotMatrix(p.Rx & 0xFFF, p.Ry & 0xFFF, p.Rz & 0xFFF).MultiplyVector(c);
            return new Vector3(Mathf.Floor(r.x) + p.X - 4, Mathf.Floor(r.y) + p.Y - 6, r.z);
        }

        // GsSetProjection(0x200) and STD_BATTLE_START_TEXT_MATRIX: a point of the word on the screen
        Vector2 TextPoint(Vector3 v)
        {
            float x = (_textM[0] * v.x + _textM[1] * v.y + _textM[2] * v.z) / 4096f + _textM[9];
            float y = (_textM[3] * v.x + _textM[4] * v.y + _textM[5] * v.z) / 4096f + _textM[10];
            float z = (_textM[6] * v.x + _textM[7] * v.y + _textM[8] * v.z) / 4096f + _textM[11];
            if (z < 1f) z = 1f;
            return new Vector2(Mathf.Floor(x * 512f / z), Mathf.Floor(y * 512f / z));
        }

        // ================================================================ 1P / 2P (STD_renderPlayerMarker), the LOSE strips, the model scene

        public bool MarkersOn;
        public Vector2[] MarkerAt = new Vector2[2];     // the fighter's bone 1 on the screen (PS1 pixels from the middle) plus its radius part
        public float[] MarkerZ = new float[2];
        public short[] Wipe;                   // STD_LOSE_WIPE_HEIGHTS while the LOSE scene shows
        public Texture ModelScene;             // the model scene (null when none)

        // ================================================================ drawing

        Material[] _mats = new Material[10];
        Material Mat(int abr, bool textured)
        {
            int k = (abr + 1) * 2 + (textured ? 1 : 0);
            if (_mats[k] != null) return _mats[k];
            var sh = Shader.Find("DW1Remake/Psx");
            if (sh == null) return null;
            var m = new Material(sh);
            m.SetFloat("_Textured", textured ? 1f : 0f);
            m.SetFloat("_ZWrite", 0f); m.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            float one = (float)UnityEngine.Rendering.BlendMode.One, add = (float)UnityEngine.Rendering.BlendOp.Add;
            switch (abr)
            {
                case -1: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", 0f); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", add); break;
                case 0: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); m.SetFloat("_Opacity", 0.5f); m.SetFloat("_BlendOp", add); break;
                case 1: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", add); break;
                case 2: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.ReverseSubtract); break;
                default: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 0.25f); m.SetFloat("_BlendOp", add); break;
            }
            return _mats[k] = m;
        }

        static Vector2 Scr(Vector2 p)
        {
            Rect pic = UiKit.Picture;
            return new Vector2(pic.x + (p.x + 160f) * pic.width / 320f, pic.y + (p.y + 120f) * pic.height / 240f);
        }

        // one GL quad in PS1 pixels (corners 0 1 2 3 = top left, top right, bottom left, bottom right) with picture corners uv (u, v pixels)
        void GlQuad(Texture tex, Vector2[] p, Vector2[] uv, Color32 col, int abr)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var m = Mat(abr, tex != null);
            if (m == null) return;
            if (tex != null) m.SetTexture("_MainTex", tex);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            m.SetPass(0);
            GL.Begin(GL.QUADS);
            int[] order = { 0, 1, 3, 2 };
            foreach (int k in order)
            {
                GL.Color(col);
                if (tex != null) GL.TexCoord2(uv[k].x / TimImage.W(tex), 1f - uv[k].y / TimImage.H(tex));
                Vector2 s = Scr(p[k]);
                GL.Vertex3(s.x, s.y, 0f);
            }
            GL.End();
            GL.PopMatrix();
        }

        /// <summary>A POLY_FT4 from video memory: mode / page / colour set, picture (u, v, w, h) over (x, y, dw, dh); flip = x0 and x1 swapped
        /// with the picture starting one left (STD_setFlippedUV). col 0x80 = as it is. semi: the STP colours with abr, the others solid.</summary>
        void Sprite(int z, int mode, int px, int py, int cx, int cy, int u, int v, int w, int h, int x, int y, int dw, int dh, int col, bool semi, int abr, bool flip = false)
        {
            if (Vram == null) return;
            var c = new Color32((byte)col, (byte)col, (byte)col, 255);
            float u0 = u, u1 = u + w;
            if (flip)
            {
                // swapShort(x0, x1), swapShort(x2, x3), STD_setFlippedUV: the picture from u - 1 (or from 0 one narrower), its right edge on the left
                int ua = u - 1, wb = w;
                if (ua < 0) { ua = 0; wb--; }
                u0 = ua + wb; u1 = ua;
            }
            var pts = new[] { new Vector2(x, y), new Vector2(x + dw, y), new Vector2(x, y + dh), new Vector2(x + dw, y + dh) };
            var uvs = new[] { new Vector2(u0, v), new Vector2(u1, v), new Vector2(u0, v + h), new Vector2(u1, v + h) };
            if (!semi)
            {
                var tex = Vram.Page(mode, px, py, cx, cy, 0);
                Add(z, () => GlQuad(tex, pts, uvs, c, -1));
                return;
            }
            var solid = Vram.Page(mode, px, py, cx, cy, 1); var stp = Vram.Page(mode, px, py, cx, cy, 2);
            Add(z, () => { GlQuad(solid, pts, uvs, c, -1); GlQuad(stp, pts, uvs, c, abr); });
        }

        // a GsBOXF / POLY_F4 rectangle; abr -1 solid, else semi-transparent with that blend
        void BoxF(int z, int x, int y, int w, int h, Color32 c, int abr)
        {
            var pts = new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x, y + h), new Vector2(x + w, y + h) };
            Add(z, () => GlQuad(null, pts, null, c, abr));
        }

        public bool Showing { get { return BracketOn || IntroOn || StatsOn || TextStage != 0 || MarkersOn || Wipe != null || ModelScene != null || HudOn; } }

        public void Draw()
        {
            if (!Showing) return;
            // the text page: STD_drawStatLabelText's rows 0-60, STD_renderMoveName's row 0xD8
            var key = string.Join("|", StatLabel) + "|" + MoveName;
            BuildPage(key, px => { for (int i = 0; i < 6; i++) Write(px, StatLabel[i] ?? "", 0, i * 12); Write(px, MoveName ?? "", 0, 0xD8); });
            Begin();
            if (HudOn) DrawHud();
            if (IntroOn) DrawIntro();
            else if (BracketOn) { for (int i = 0; i < 8; i++) DrawBracketDigimon(i); DrawBracketPicture(); DrawPunches(); DrawFlashes(); }
            if (StatsOn) DrawStats();
            if (TextStage != 0) DrawText();
            if (MarkersOn) DrawMarkers();
            if (Wipe != null) for (int s = 0; s < 32; s++) if (Wipe[s] > 0) BoxF(5, 10 * s - 0xA0, -0x78, 10, Wipe[s], new Color32(0, 0, 0, 255), 0);
            if (ModelScene != null)
            {
                var tex = ModelScene;
                var pts = new[] { new Vector2(-160, -120), new Vector2(160, -120), new Vector2(-160, 120), new Vector2(160, 120) };
                var uvs = new[] { new Vector2(0, 0), new Vector2(TimImage.W(tex), 0), new Vector2(0, TimImage.H(tex)), new Vector2(TimImage.W(tex), TimImage.H(tex)) };
                Add(4, () => GlQuad(tex, pts, uvs, new Color32(128, 128, 128, 255), -1));
            }
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        // STD_renderBracketDigimon: tile = the type; 16 x 16 frames of its 32 x 32 cell (frame bit 0 right, bit 1 down) on page 0x380 + 64 x (tile / 64)
        void DrawBracketDigimon(int id)
        {
            int tile = Types[id];
            int u = (((tile - 1) % 32) * 32 + (_slots[id].Frame & 1) * 16) & 0xFF, v = (((tile - 1) / 8) * 32 + (_slots[id].Frame / 2) * 16) & 0xFF;
            int w = u == 0xF0 ? 0xF : 0x10, h = v == 0xF0 ? 0xF : 0x10;
            int col = 0x80 - Fade;
            Sprite(5, 0, (tile / 64) * 64 + 0x380, 0, (tile / 64) * 16 + 0x120, (tile < 112 ? _spriteClut[tile] : 0) + 0x1E0, u, v, w, h, _slots[id].X, _slots[id].Y, w, h, col, Fade != 0, 1, _slots[id].Flip == 1);
        }

        // STD_drawBracket: the bracket picture (page 0x340, v 0x78, 255 x 135 at (-0x80, -0x61)) and under it a half-transparent 0x20 grey box over the whole picture
        void DrawBracketPicture()
        {
            Sprite(5, 0, 0x340, 0, 0x20, 0x1E0, 0, 0x78, 0xFF, 0x87, -0x80, -0x61, 0xFF, 0x87, 0x80 - Fade, Fade != 0, 1);
            int hw = Mathf.CeilToInt(UiKit.PsHalfWidth);      // the whole view (wider with the mod 16:9)
            BoxF(5, -hw, -0x78, 2 * hw, 0xF0, new Color32(0x20, 0x20, 0x20, 255), 0);
        }

        // STD_renderBracketProjectile: an 8 x 8 icon (STD_BRACKET_PROJECTILE_ICONS by type) on page 0x3C0 v 0xC0; a double one has a second 8 lower
        void DrawPunches()
        {
            for (int id = 0; id < 8; id++)
            {
                var p = _punch[id];
                if (!p.On) continue;
                int t = Types[id], icon = t < 112 ? _projIcon[t] : 1;
                int cy = _projClut[Mathf.Clamp(icon - 1, 0, 15)] + 0x1E0;
                bool flip = _slots[id].Flip == 1;
                Sprite(4, 0, 0x3C0, 0, 0x130, cy, icon * 8 - 8, 0xC0, 8, 8, p.X, p.Y, 8, 8, 0x80, false, 0, flip);
                if (p.Flag == 1) Sprite(4, 0, 0x3C0, 0, 0x130, cy, icon * 8 - 8, 0xC0, 8, 8, p.X, p.Y + 8, 8, 8, 0x80, false, 0, flip);
            }
        }

        void DrawFlashes()
        {
            for (int i = 0; i < 8; i++) if (FlashShows(i)) Sprite(3, 0, 960, 0, 0x130, 0x1E0, 0, 0xC8, 0x10, 0x10, _slots[i].X, _slots[i].Y, 0x10, 0x10, 0x80, false, 0);
        }

        // STD_renderBracketIntro: the bracket (fading in), a full-picture subtract quad of the 2 x 2 texel at (0xFA, 0xFD) of page (896, 256)
        // (the clock sheet of ETCTIM.BIN, colour set (0x100, 0x1F6)) in colour MAIN_D_80135165, and the cup's title (page 0x180, 8-bit,
        // colour set (0, 0x1E6)) at (-0x80, -0x40), additive while the bracket fades
        void DrawIntro()
        {
            for (int i = 0; i < 8; i++) DrawBracketDigimon(i);
            DrawBracketPicture();
            int hw = Mathf.CeilToInt(UiKit.PsHalfWidth);      // the whole view (wider with the mod 16:9)
            Sprite(3, 0, 0x380, 0x100, 0x100, 0x1F6, 0xFA, 0xFD, 2, 2, -hw, -0x78, 2 * hw, 0xF0, _introDark, true, 2);
            int h = TitleShort ? 0x7D : 0x80;
            Sprite(2, 1, 0x180, 0, 0, 0x1E6, 0, 0, 0xFF, h, -0x80, -0x40, 0xFF, h, _introTitle, Fade != 0x80, 1);
        }

        // STD_renderIntroStats: six labels (the text page rows 0, 12 ... 60) at (-100, i x 16 - 28), green bars (POLY_F4 0x50 0xC8 0x50, 8 high,
        // value x 100 / STD_STAT_BAR_LIMITS[i] wide, at least 1) at (-0x32, i x 16 - 0x1A), and the numbers (12 x 12 digits, page (832, 0) v 32) once all are counted
        void DrawStats()
        {
            for (int i = 0; i < 6; i++)
            {
                Words(0, 0, -100, i * 16 - 28, 48, 0, i * 12);
                int w = _barLimit[i] != 0 ? StatShown[i] * 100 / _barLimit[i] : 0;
                if (w == 0) w = 1;
                if (w > 0) BoxF(3, -0x32, i * 16 - 0x1A, w, 8, new Color32(0x50, 0xC8, 0x50, 255), -1);
            }
            bool counting = false;
            for (int i = 0; i < 6; i++) if (StatShown[i] != StatOf[i]) counting = true;
            if (counting) return;
            for (int i = 0; i < 6; i++) StatNumber(52, i * 16 - 28, 4, StatShown[i], 3);
        }

        /// <summary>STD_renderIntroStatNumber: right-aligned 12 x 12 digits of page (832, 0) row v 32, colour set (16, 480).</summary>
        public void StatNumber(int x, int y, int digits, int value, int z)
        {
            string s = Mathf.Max(0, value).ToString();
            for (int i = 0; i < s.Length; i++)
            {
                int d = s[i] - '0', place = s.Length - 1 - i;
                Sprite(z, 0, 832, 0, 16, 480, d * 12, 32, 12, 12, x + ((digits - 1) - place) * 12, y, 12, 12, 0x80, false, 0);
            }
        }

        public bool Counting { get { if (!StatsOn) return false; for (int i = 0; i < 6; i++) if (StatShown[i] != StatOf[i]) return true; return false; } }

        // the pieces: 8 x 8 cells (u 0x80 + 8 x kind, v 0x80) of page (896, 256), colour set (256, 488) (cycling 488-490 once the word stands),
        // stretched over 8 x 12 through the word's matrix; a half-transparent trail behind each moving piece; a black copy 2 pixels down and right
        void DrawText()
        {
            if (_pieces == null) return;
            int cy = TextStage == 1 && _lead == 155 ? (_textTimer % 6 / 2) + 488 : 488;
            for (int i = 0; i < 155; i++)
            {
                var p = _pieces[i];
                Vector2[] c = new Vector2[4];
                if (TextStage == 1)
                {
                    c[0] = TextPoint(new Vector3(p.X, p.Y, 0)); c[1] = TextPoint(new Vector3(p.X + 8, p.Y, 0));
                    c[2] = TextPoint(new Vector3(p.X, p.Y + 12, 0)); c[3] = TextPoint(new Vector3(p.X + 8, p.Y + 12, 0));
                }
                else
                {
                    if (p.Bx == 0 && p.By == 0) continue;
                    for (int j = 0; j < 4; j++) c[j] = TextPoint(BurstCorner(p, j));
                }
                PieceQuad(5, c, 0x80 + p.Tile * 8, 0x80, 8, 8, cy);
                var shadow = new Vector2[4];
                for (int j = 0; j < 4; j++) shadow[j] = c[j] + new Vector2(2, 2);
                Add(6, () => GlQuad(null, shadow, null, new Color32(0, 0, 0, 255), -1));
                if (TextStage == 1 && _lead != 155 && p.Trail > 0)
                {
                    Vector2[] q = new Vector2[4];
                    if (p.Speed > 0)
                    {
                        q[0] = TextPoint(new Vector3(p.X - p.Trail, p.Y, 0)); q[1] = c[0];
                        q[2] = TextPoint(new Vector3(p.X - p.Trail, p.Y + 12, 0)); q[3] = c[2];
                        TrailQuad(q, 0x9E, -24, cy);
                    }
                    else
                    {
                        q[0] = c[1]; q[1] = TextPoint(new Vector3(p.X + 8 + p.Trail, p.Y, 0));
                        q[2] = c[3]; q[3] = TextPoint(new Vector3(p.X + 8 + p.Trail, p.Y + 12, 0));
                        TrailQuad(q, 0x86, 24, cy);
                    }
                }
            }
        }

        void PieceQuad(int z, Vector2[] c, int u, int v, int w, int h, int cy)
        {
            var tex = Vram.Page(0, 896, 256, 256, cy, 0);
            var uvs = new[] { new Vector2(u, v), new Vector2(u + w, v), new Vector2(u, v + h), new Vector2(u + w, v + h) };
            Add(z, () => GlQuad(tex, c, uvs, new Color32(128, 128, 128, 255), -1));
        }

        void TrailQuad(Vector2[] q, int u, int w, int cy)
        {
            var solid = Vram.Page(0, 896, 256, 256, cy, 1); var stp = Vram.Page(0, 896, 256, 256, cy, 2);
            var uvs = new[] { new Vector2(u, 0x80), new Vector2(u + w, 0x80), new Vector2(u, 0x88), new Vector2(u + w, 0x88) };
            Add(5, () => { GlQuad(solid, q, uvs, new Color32(128, 128, 128, 255), -1); GlQuad(stp, q, uvs, new Color32(128, 128, 128, 255), 0); });
        }

        // STD_renderPlayerMarker: 1P / 2P (page (768, 0), colour set (0, 0x1E1), u 0xA0 + 48 x id, v 0xE0, 48 / 47 x 31) over bone 1, moved right by
        // VIEWPORT_DISTANCE x (radius / 2) / depth, 32 up; sorted at the fighter's own depth (ours: over the 3D picture)
        void DrawMarkers()
        {
            for (int id = 0; id < 2; id++)
            {
                int w = id == 0 ? 0x30 : 0x2F;
                Sprite(9, 0, 768, 0, 0, 0x1E1, id * 48 + 0xA0, 0xE0, w, 31, (int)MarkerAt[id].x, (int)MarkerAt[id].y - 0x20, w, 31, 0x80, false, 0);
            }
        }

        // ================================================================ the fight's HUD (STD_renderFighterStatusBars, STD_renderFighterHPBar, STD_renderCommandMenu,
        // STD_renderMoveName, STD_renderFinisherChargeup): partner left, opponent right, the commands along the bottom

        public bool HudOn, Fighting;           // HUD objects added; GAME_STATE == 4
        public readonly int[] Hp = new int[2], MaxHp = new int[2], Mp = new int[2], MaxMp = new int[2];
        public int Finish, FinishMax;          // fighter[0].finisherProgress / finisherGoal
        public bool FinisherOrdered, Charging; public int ChargeCount;
        public int[] Commands = new int[0], CommandElement = new int[0]; public int Hover;
        public string MoveName = "";
        int _segments, _pulseFrame = 0xB, _brightness, _fullFrames; bool _ready;      // STD_FINISHER_SEGMENTS / _PULSE_FRAME / _BRIGHTNESS / _FULL_FRAMES / _READY
        int _menuTimer; bool _blink;           // STD_COMMAND_MENU_TIMER / _BLINK

        /// <summary>STD_addFighterStatusBars + STD_addCommandMenu</summary>
        public void AddHud() { HudOn = true; _segments = 0; _pulseFrame = 0xB; _brightness = 0; _fullFrames = 0; _ready = false; _menuTimer = 0; _blink = false; }

        /// <summary>One frame of the HUD's own counters (the finishing gauge's pulse runs in its render function, the blink in STD_tickCommandMenu).</summary>
        public void TickHud()
        {
            if (!HudOn) return;
            _menuTimer++;
            if (Fighting && _menuTimer % 8 == 0) _blink = !_blink;
            if (!Fighting || FinishMax <= 0) return;
            int n = Finish * 6 / FinishMax;
            if (_segments != n) { _pulseFrame = 0; _segments = n; }
            _brightness = PulseAt(_pulseFrame);
            if (_pulseFrame < 0xB) _pulseFrame++;
            if (n == 6)
            {
                if (_fullFrames < 0xA) _fullFrames++;
                if (_fullFrames == 0xA) { _ready = true; _pulseFrame %= 0xB; }
            }
        }

        int PulseAt(int i) { return BitConverter.ToInt32(Std, (int)(0x8007A9A4 + 4 * (uint)Mathf.Clamp(i, 0, 11) - StdBase)); }
        int ExeU8(uint a) { return Exe[At(a)]; }

        void DrawHud()
        {
            for (int id = 0; id < 2; id++) DrawStatusBars(id);
            if (Fighting) DrawFinisherGauge();
            DrawCommandMenu();
            if (Charging)
            {
                Sprite(7, 0, 960, 256, 272, 498, 0x58, 0xE0, 46, 12, -0x8C, -0x4A, 0x2E, 0xC, 0x80, false, 0);
                for (int i = 0; i < ChargeCount / 8; i++) Sprite(7, 0, 960, 256, 272, 498, 0x88, 0xE0, 4, 6, -0x8C + 3 + i * 4, -0x4A + 3, 4, 6, 0x80, false, 0);
            }
        }

        // STD_STATUS_BAR_SPRITES {clut, u, v, w, h, x, y}: per bar the fill (stretched to value x 50 / max), the icon, the frame; the number (STD_renderNumber) right of it
        void DrawStatusBars(int id)
        {
            for (int bar = 0; bar < 2; bar++)
            {
                int x0 = id == 0 ? -0x8C : 0x24, y0 = bar == 0 ? -0x64 : -0x58;
                int cur = bar == 0 ? Hp[id] : Mp[id], max = bar == 0 ? MaxHp[id] : MaxMp[id];
                int fill = max > 0 ? cur * 50 / max : 0;
                HudNumber(x0 + 0x49, y0 - 3, 4, cur, 8);
                for (int k = 0; k < 3; k++)
                {
                    uint o = 0x8007A9D4 + (uint)(10 * (bar * 3 + 2 - k));
                    int clut = S16(o), u = U8(o + 2), v = U8(o + 3), w = U8(o + 4), h = U8(o + 5), x = S16(o + 6), y = S16(o + 8);
                    Sprite(8, 0, 896, 256, 0x100, clut, u, v, w, h, x0 + x, y0 + y, k == 0 ? fill : w, h, 0x80, false, 0);
                }
            }
        }

        // STD_renderNumber: right-aligned 7 x 11 digits of page (896, 256) v 172, colour set (256, 492)
        void HudNumber(int x, int y, int digits, int value, int z)
        {
            string s = Mathf.Clamp(value, 0, 9999).ToString();
            for (int i = 0; i < s.Length; i++)
            {
                int place = s.Length - 1 - i;
                Sprite(z, 0, 896, 256, 256, 492, (s[i] - '0') * 7, 172, 7, 11, x + ((digits - 1) - place) * 7, y, 7, 11, 0x80, false, 0);
            }
        }

        // STD_renderFighterHPBar(0): the letters F I N I S H (STD_FINISHER_SEGMENT_U / _W / _X, v 0x9D, colour set (256, 0x1EC)) one per sixth of
        // the gauge, the newest and (once ready) all of them pulsing (STD_FINISHER_PULSE); full: two more pieces after 3 and 5 frames and the ready icon
        void DrawFinisherGauge()
        {
            if (FinishMax <= 0) return;
            int n = Finish * 6 / FinishMax;
            if (n == 6)
            {
                if (_fullFrames >= 3) n++;
                if (_fullFrames >= 5)
                {
                    n++;
                    int c = !_ready || FinisherOrdered ? 0x80 : _brightness;
                    Sprite(8, 0, 896, 256, 256, 0x1E2, 0x80, 0x88, 37, 9, -0x56, -0x4B, 0x25, 9, c, false, 0);
                    BoxF(8, -0x58, -0x4C, 0x29, 0xB, new Color32((byte)c, (byte)c, (byte)c, 255), 0);
                }
            }
            int real = Finish * 6 / FinishMax;
            for (int i = 0; i < n && i < 8; i++)
            {
                int c = ((real - 1 == i) || _ready) && !FinisherOrdered ? _brightness : 0x80;
                int u = ExeU8(0x801348C0 + (uint)i), w = ExeU8(0x801348C8 + (uint)i), x = ExeU8(0x801348D0 + (uint)i);
                Sprite(8, 0, 896, 256, 0x100, 0x1EC, u, 0x9D, w, 15, x - 0x8D, -0x4F, w, 15, c, false, 0);
            }
        }

        // STD_renderCommandMenu(0): the label strip (half transparent, STD_COMMAND_MENU_LAYOUTS / STD_COMMAND_LABEL_U / _W), the icons in a zigzag
        // (STD_COMMAND_ICON_UVS, techniques by element STD_SPECIAL_ICON_UVS; the hovered one blinking), the cursor and the hovered name while fighting
        void DrawCommandMenu()
        {
            int n = Commands.Length;
            if (n < 2) return;
            const int top = 0x44, bottom = 0x64;
            int count = n - 1, baseX = -0x8C + (n - 1) * 0xE;
            int layout = Mathf.Clamp(n - 2, 0, 7);
            if (Fighting)
            {
                Words(7, 0, -0x8C, top - 0xE, 0x90, 0, 0xD8);
                int x = baseX - Hover * 0xE;
                int y = count % 2 == 0 ? (Hover % 2 == 0 ? top : top + 0xA) : (Hover % 2 == 1 ? top : top + 0xA);
                Sprite(7, 0, 960, 256, 272, 497, 0x3D, 0xE0, 0x16, 0x16, x - 3, y, 0x16, 0x16, 0x80, false, 0);
            }
            for (int i = 1; i < n; i++)
            {
                int cmd = Commands[i], u, v;
                if (cmd >= 8 && cmd < 0xC) { int e = Mathf.Clamp(i < CommandElement.Length ? CommandElement[i] : 0, 0, 7); u = U8(0x8007A708 + (uint)(2 * e)); v = U8(0x8007A708 + (uint)(2 * e + 1)); }
                else { u = U8(0x8007A6F8 + (uint)(2 * Mathf.Clamp(cmd - 1, 0, 7))); v = U8(0x8007A6F8 + (uint)(2 * Mathf.Clamp(cmd - 1, 0, 7) + 1)); }
                if (i == Hover && _blink) u += 0x10;
                int x = baseX - i * 0xE;
                bool upper = count % 2 == 0 ? i % 2 == 0 : i % 2 == 1;
                int y0 = upper ? top + 3 : top + 0xD, y1 = upper ? bottom - 0xD : bottom - 3;
                Sprite(7, 0, 960, 256, 272, 496, u, v, 0x10, 0xF, x, y0, 0x10, y1 - y0, 0x80, false, 0);
            }
            for (int i = 0; i < n; i++)
            {
                int lab = U8(0x8007A6A8 + (uint)(layout * 10 + i));
                int lu = ExeU8(0x801348B0 + (uint)Mathf.Clamp(lab, 0, 4)), lw = ExeU8(0x801348B8 + (uint)Mathf.Clamp(lab, 0, 4));
                int rowX = i > 0 ? (i - 1) * 0xE + 0xB : 0, width = i == 0 || i == n - 1 ? 0xB : 0xE;
                Sprite(7, 0, 960, 256, 272, 497, lu, 0xE0, lw, 31, rowX - 0x8F, top, width, bottom - top, 0x80, true, 0);
            }
        }

        public void Destroy() { foreach (var m in _mats) if (m != null) UnityEngine.Object.Destroy(m); }
    }
}
