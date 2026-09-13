using BackroomsSurvival.WorldGen3;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.1 de MAPPING-PROTOTYPE — pinta el mundo de la escena de playtest del recuerdo.
    /// </summary>
    /// <remarks>
    /// <c>Wg3Materials</c> no es <c>[Serializable]</c>, así que <c>Wg3TestWorld.materials</c> no se
    /// guarda en ninguna escena: llega vacío y cada renderer se pinta magenta. Las referencias viven
    /// aquí, que sí se serializan, y se copian al mundo en <c>Awake</c>, antes de que
    /// <c>Wg3TestWorld.Start</c> genere. Sólo para esta escena: no toca WG3.
    /// </remarks>
    [ExecuteAlways]
    public sealed class MappingPlaytestMaterials : MonoBehaviour
    {
        public Wg3TestWorld world;
        public Material floor;
        public Material structure;
        public Material ceiling;
        public Material decoration;

        private void Awake() => Apply();

        private void OnEnable()
        {
            Apply();
#if UNITY_EDITOR
            // En modo edición el mundo puede estar ya generado sin pintar: se repinta un frame después,
            // igual que hace Wg3TestWorld desde OnValidate.
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (this != null && world != null && world.World != null) world.Generate();
                };
            }
#endif
        }

        private void Apply()
        {
            if (world == null) return;
            world.materials = new Wg3Materials
            {
                floor = floor,
                structure = structure,
                ceiling = ceiling,
                decoration = decoration,
            };
        }
    }
}
