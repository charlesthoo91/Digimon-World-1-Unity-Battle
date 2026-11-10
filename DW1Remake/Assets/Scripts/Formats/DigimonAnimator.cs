using DW1Remake.Formats;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DW1Remake.Formats
{
    /// <summary>Plays the game's animations on a Digimon built by MmdModel.Build.</summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    public sealed class DigimonAnimator : MonoBehaviour
    {
        public float TicksPerSecond = 20f;     // the game runs at 20 frames a second (VSync(3))
        public bool Playing = true;

        Transform[] _nodes;
        Anim[] _anims;
        AnimSim _sim;
        int _slot = -1;
        double _last, _acc;
        bool _once, _held, _wasEndless;                       // play once and hold the last pose (getting up, lying down...)
        Quaternion[] _fromRot; Vector3[] _fromPos; float _blend = 1f;
        public float BlendSeconds = 0.2f;        // smooth change from the previous pose to the next animation

        public int DigimonType = -1;                                // whose sounds it plays
        public static System.Action<DigimonAnimator, int, int> SoundSink;     // set by the room: a sound event of an animation
        void Heard(int bank, int val) { if (SoundSink != null) SoundSink(this, bank, val); }

        public int SlotCount { get { return _anims == null ? 0 : _anims.Length; } }
        public int Slot { get { return _slot; } }
        int _pending = -1; bool _pendingOnce;    // next scripted animation, waits until the current play-once animation has finished (like the game's per-actor command queue)
        public void ReleaseLoop() { if (_sim != null) _sim.ReleaseLoop(); }
        public bool PlaysOnce { get { return _once; } }      // the animation was started to play once (SetSlot(slot, true))
        public bool Busy { get { return _once && !_held && _sim != null && !_sim.Endless; } }   // an endless loop (lying asleep) never blocks the next animation
        /// <summary>animFlag bit 0: a play-once animation has not reached its end yet (an endless loop counts as still running).</summary>
        public bool Running { get { return _sim != null && (!_once || !_held); } }
        /// <summary>anim.loopCount = 1: the loop the animation is in ends after this pass.</summary>
        public void LoopOnce() { if (_sim != null) _sim.LoopOnce(); }

        /// <summary>Scripted animation: starts now, or right after the current play-once animation ends.</summary>
        public void QueueSlot(int slot, bool once)
        {
            if (!HasSlot(slot)) return;
            if (Busy) { _pending = slot; _pendingOnce = once; }
            else { _pending = -1; SetSlot(slot, once); }
        }

        public void Init(Transform[] nodes, Anim[] anims)
        {
            _nodes = nodes; _anims = anims;
            for (int i = 0; i < anims.Length; i++) if (anims[i] != null) { SetSlot(i); break; }
        }

        public Transform Node(int i) { return _nodes != null && i < _nodes.Length ? _nodes[i] : null; }

        public bool HasSlot(int slot) { return _anims != null && slot >= 0 && slot < _anims.Length && _anims[slot] != null; }

        int _forward;

        /// <summary>animFlag bit 4 (NPCEntityTickBattle): the animation is ticked twice a game frame, so it plays and carries the body twice as fast.
        /// The battle sets it on Digimon running off to their flee point and clears it when they are off the map.</summary>
        public bool DoubleSpeed;

        /// <summary>
        /// How far this animation has carried the body forward since the last call, in metres. In the original nothing moves by a set speed: every walk and run moves by
        /// the forward value keyed in bone 0 of its animation (entityMoveForward), which is why each step lands on its step sound.
        /// </summary>
        public float TakeForward() { float m = _forward * 0.01f; _forward = 0; _bodyMove = 0; return m; }

        int _bodyMove;

        /// <summary>In battle every animation moves the fighter for real (hit reactions throw it back and it gets up where it landed): the battle reads this and the model stops doing it visually.</summary>
        public bool BodyMovesItself;

        /// <summary>As TakeForward, with its sign: plus is towards the model's front.</summary>
        public float TakeBodyMove() { float m = _bodyMove * 0.01f; _bodyMove = 0; _forward = 0; return m; }

        /// <summary>Length of a slot in seconds (0 if it has no animation).</summary>
        public float SecondsOf(int slot) { return HasSlot(slot) ? _anims[slot].Frames / TicksPerSecond : 0f; }

        /// <summary>When a technique's animation lets go, in seconds: its last sound event (Spit Fire frame 11, the mouth opens). -1 if it has none.</summary>
        public float ReleaseOf(int slot)
        {
            if (!HasSlot(slot)) return -1f;
            int f = -1;
            foreach (var x in _anims[slot].Ins) if (x.Kind == InsKind.Snd) f = x.Frame;
            return f < 0 ? -1f : f / TicksPerSecond;
        }

        /// <param name="once">play the animation once and keep its last pose instead of repeating</param>
        public void SetSlot(int slot, bool once = false)
        {
            if (!HasSlot(slot)) return;
            if (_nodes != null && _sim != null && BlendSeconds > 0f && Application.isPlaying)
            {
                if (_fromRot == null || _fromRot.Length != _nodes.Length) { _fromRot = new Quaternion[_nodes.Length]; _fromPos = new Vector3[_nodes.Length]; }
                for (int i = 1; i < _nodes.Length; i++) { _fromRot[i] = _nodes[i].localRotation; _fromPos[i] = _nodes[i].localPosition; }
                _blend = 0f;
            }
            else _blend = 1f;
            _once = once; _held = false; _pending = -1; _maxLoc = Vector3.zero; _maxRot = Vector3.zero;
            _slot = slot;
            _forward = 0; _bodyMove = 0;        // steps nobody took so far must not all land at once later
            _sim = new AnimSim(_anims[slot], _nodes.Length);
            _sim.OnSound = Heard;
            Apply();
        }

        Vector3 _maxLoc, _maxRot;
        public static bool UseRootMotion = true;   // scripted animations (jump, flip) also move/turn the whole body (bone 0)

        void Apply()
        {
            for (int i = 1; i < _nodes.Length; i++) MmdModel.SetLocal(_nodes[i], _sim.Rot[i], _sim.Loc[i]);
            if (_nodes.Length > 0 && _nodes[0] != null)
            {
                if (_once)
                {
                    for (int k = 0; k < 3; k++) { _maxLoc[k] = Mathf.Max(_maxLoc[k], Mathf.Abs(_sim.Loc[0][k])); _maxRot[k] = Mathf.Max(_maxRot[k], Mathf.Abs(_sim.Rot[0][k])); }
                }
                if (UseRootMotion && _once) MmdModel.SetLocal(_nodes[0], _sim.Rot[0], BodyMovesItself ? new[] { _sim.Loc[0][0], _sim.Loc[0][1], 0 } : _sim.Loc[0]);
                else { _nodes[0].localRotation = Quaternion.identity; _nodes[0].localPosition = Vector3.zero; }
            }
            if (_blend < 1f && _fromRot != null)
            {
                float k = _blend * _blend * (3f - 2f * _blend);
                for (int i = 1; i < _nodes.Length; i++)
                {
                    _nodes[i].localRotation = Quaternion.Slerp(_fromRot[i], _nodes[i].localRotation, k);
                    _nodes[i].localPosition = Vector3.Lerp(_fromPos[i], _nodes[i].localPosition, k);
                }
            }
        }

        /// <summary>Set by the room while the Triangle menu is open: every model stops where it is, as on the PS1.</summary>
        public static bool Paused;
        static int _sharedFrame = -1, _sharedTicks;
        static double _sharedAcc;

        // the PS1 ticks every model in the same frame: all animators share one clock, so the boy, the partner and the camera never step at different moments
        static int SharedTicks(double tick)
        {
            if (Time.frameCount != _sharedFrame)
            {
                _sharedFrame = Time.frameCount;
                _sharedTicks = 0;
                if (Paused) return 0;
                _sharedAcc += System.Math.Min(Time.unscaledDeltaTime, 0.25);
                _sharedTicks = 0;
                while (_sharedAcc >= tick) { _sharedAcc -= tick; _sharedTicks++; }
            }
            return _sharedTicks;
        }

        void Step()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double dt = now - _last; _last = now;
            if (_sim == null) return;
            if (Paused && Application.isPlaying) return;
            if (_blend < 1f) { _blend = Mathf.Min(1f, _blend + (float)dt / Mathf.Max(0.01f, BlendSeconds)); Apply(); }
            if (_held || !Playing) return;      // Playing off = animFlag bit 0 clear (tickAnimation does nothing): the pose stays
            double tick = 1.0 / TicksPerSecond;
            int ticks = 0;
            if (Application.isPlaying) ticks = SharedTicks(tick);
            else
            {
                _acc += System.Math.Min(dt, 0.25);
                while (_acc >= tick) { _acc -= tick; ticks++; }
            }
            if (DoubleSpeed) ticks *= 2;
            bool moved = false;
            while (ticks-- > 0)
            {
                int zBefore = _sim.Loc[0][2];
                bool ticking = _sim.Tick();
                _forward += Mathf.Abs(_sim.Loc[0][2] - zBefore);
                _bodyMove -= _sim.Loc[0][2] - zBefore;
                if (!ticking)
                {
                    if (_once)
                    {
                        _held = true;                         if (_pending >= 0) { int ps = _pending; SetSlot(ps, _pendingOnce); }   // the queued animation starts now
                        break;
                    }                                   // finished: keep the last pose
                    _sim = new AnimSim(_anims[_slot], _nodes.Length);                    // finished: start over
                    _sim.OnSound = Heard;
                }
                moved = true;
            }
            if (_once && _sim != null && _sim.Endless != _wasEndless) { _wasEndless = _sim.Endless; }
            if (moved) Apply();
        }

        void OnEnable()
        {
            _last = Time.realtimeSinceStartupAsDouble;
#if UNITY_EDITOR
            EditorApplication.update += EditorTick;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorTick;
#endif
        }

        void Update() { if (Application.isPlaying) Step(); }

#if UNITY_EDITOR
        void EditorTick()
        {
            if (Application.isPlaying || this == null) return;
            Step();
            SceneView.RepaintAll();
        }
#endif
    }
}
