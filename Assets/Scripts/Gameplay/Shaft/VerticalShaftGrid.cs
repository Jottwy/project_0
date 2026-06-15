using UnityEngine;

namespace BackroomsSurvival.Gameplay.Shaft
{
    /// <summary>
    /// Drop on an empty GameObject to build a 5×5 grid of vertical shafts (25
    /// total, 25×25 m, 12 m deep) at the object's position — on Start at runtime,
    /// or via the context menu in edit mode. Generated geometry is parented under
    /// this object. Generation lives in <see cref="VerticalShaftGenerator"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VerticalShaftGrid : MonoBehaviour
    {
        [Tooltip("Reserved for future per-shaft variation; the grid layout is regular.")]
        public int seed = 12345;
        [Tooltip("Build automatically when entering Play mode.")]
        public bool generateOnStart = true;

        public ShaftGridMetadata Metadata { get; private set; }
        private GameObject _instance;

        private void Start()
        {
            if (generateOnStart && _instance == null) Generate();
        }

        [ContextMenu("Generate / Regenerate")]
        public void Generate()
        {
            Clear();
            _instance = VerticalShaftGenerator.GenerateVerticalShaftGrid(
                transform.position, seed, out var meta);
            _instance.transform.SetParent(transform, worldPositionStays: true);
            Metadata = meta;
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name.StartsWith("VerticalShaftGrid_")) DestroySafe(child.gameObject);
            }
            _instance = null;
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
