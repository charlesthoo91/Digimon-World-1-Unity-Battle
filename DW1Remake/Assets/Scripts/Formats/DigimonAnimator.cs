using DW1Remake.Formats;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DW1Remake.Formats
{
    /// <summary>Plays the game's animations on a Digimon built by MmdModel.Build.</summary>
    [ExecuteAlways]
    public sealed class DigimonAnimator : MonoBehaviour
    {
        public float TicksPerSecond = 30f;
        public bool Playing = true;

        Transform[] _nodes;
        Anim[] _anims;
        AnimSim _sim;
        int _slot = -1;
        double _last, _acc;
        bool _once, _held, _wasEndless;                       // play once and hold the last pose (getting up, lying down...)
        Quaternion[] _fromRot; Vector3[] _fromPos; float _blend = 1f;
        public float BlendSeconds = 0.2f;        // smooth change from the previous pose to the next animation

        public int SlotCount { get { return _anims == null ? 0 : _anims.Length; } }
        public int Slot { get { return _slot; } }
        int _pending = -1; bool _pendingOnce;    // next scripted animation, waits until the current play-once animation has finished (like the game's per-actor command queue)
        public void ReleaseLoop() { if (_sim != null) _sim.ReleaseLoop(); }
        public bool Busy { get { return _once && !_held && _sim != null && !_sim.Endless; } }   // an endless loop (lying asleep) never blocks the next animation

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

        public bool HasSlot(int slot) { return _anims != null && slot >= 0 && slot < _anims.Length && _anims[slot] != null; }

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
            _sim = new AnimSim(_anims[slot], _nodes.Length);
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
                if (UseRootMotion && _once) MmdModel.SetLocal(_nodes[0], _sim.Rot[0], _sim.Loc[0]);
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

        void Step()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double dt = now - _last; _last = now;
            if (_sim == null || !Playing) return;
            if (_blend < 1f) { _blend = Mathf.Min(1f, _blend + (float)dt / Mathf.Max(0.01f, BlendSeconds)); Apply(); }
            if (_held) return;
            _acc += System.Math.Min(dt, 0.25);
            double tick = 1.0 / TicksPerSecond;
            bool moved = false;
            while (_acc >= tick)
            {
                _acc -= tick;
                if (!_sim.Tick())
                {
                    if (_once)
                    {
                        _held = true; Debug.Log("[anim] slot " + _slot + " finished at " + Time.realtimeSinceStartup.ToString("F1") + "s, body movement max loc " + _maxLoc + " rot " + _maxRot);
                        if (_pending >= 0) { int ps = _pending; SetSlot(ps, _pendingOnce); }   // the queued animation starts now
                        break;
                    }                                   // finished: keep the last pose
                    _sim = new AnimSim(_anims[_slot], _nodes.Length);                    // finished: start over
                }
                moved = true;
            }
            if (_once && _sim != null && _sim.Endless != _wasEndless) { _wasEndless = _sim.Endless; Debug.Log("[anim] slot " + _slot + " endless loop: " + _wasEndless + " at " + Time.realtimeSinceStartup.ToString("F1") + "s"); }
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
