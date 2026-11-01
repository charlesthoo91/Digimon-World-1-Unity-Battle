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

        public int SlotCount { get { return _anims == null ? 0 : _anims.Length; } }
        public int Slot { get { return _slot; } }

        public void Init(Transform[] nodes, Anim[] anims)
        {
            _nodes = nodes; _anims = anims;
            for (int i = 0; i < anims.Length; i++) if (anims[i] != null) { SetSlot(i); break; }
        }

        public bool HasSlot(int slot) { return _anims != null && slot >= 0 && slot < _anims.Length && _anims[slot] != null; }

        public void SetSlot(int slot)
        {
            if (!HasSlot(slot)) return;
            _slot = slot;
            _sim = new AnimSim(_anims[slot], _nodes.Length);
            Apply();
        }

        void Apply()
        {
            for (int i = 1; i < _nodes.Length; i++) MmdModel.SetLocal(_nodes[i], _sim.Rot[i], _sim.Loc[i]);
        }

        void Step()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double dt = now - _last; _last = now;
            if (_sim == null || !Playing) return;
            _acc += System.Math.Min(dt, 0.25);
            double tick = 1.0 / TicksPerSecond;
            bool moved = false;
            while (_acc >= tick)
            {
                _acc -= tick;
                if (!_sim.Tick()) { _sim = new AnimSim(_anims[_slot], _nodes.Length); }   // finished: start over
                moved = true;
            }
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
