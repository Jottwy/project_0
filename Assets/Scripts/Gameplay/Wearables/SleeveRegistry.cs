using System;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// ADR-149 R4c: las mangas de primera persona. Los brazos 1P viven dentro de cada wieldable y cada FBX trae su propia malla
    /// de brazo, así que para cada una hay una FUNDA horneada en el editor (<c>BackroomsSleeveBuilder</c>): los mismos huesos y
    /// pesos, solo antebrazo y brazo, unos milímetros por fuera. Y para cada prenda de manga larga, su material de primera
    /// persona (shader <c>Backrooms/Garment Lit FP</c>, con el warp del viewmodel). Se hornea en el editor porque en un build las
    /// mallas del vendor no se pueden leer.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Garments/Sleeve Registry")]
    public sealed class SleeveRegistry : ScriptableObject
    {
        public const string ResourcesPath = "BR_SleeveRegistry";

        [SerializeField] private Mesh[] _armMeshes = Array.Empty<Mesh>();
        [SerializeField] private Mesh[] _sleeveMeshes = Array.Empty<Mesh>();
        [SerializeField] private int[] _garmentIds = Array.Empty<int>();
        [SerializeField] private Material[] _garmentMaterials = Array.Empty<Material>();

        public int SleeveCount => Mathf.Min(_armMeshes.Length, _sleeveMeshes.Length);

        public Mesh SleeveFor(Mesh arm)
        {
            if (arm == null) return null;
            for (int i = 0; i < _armMeshes.Length && i < _sleeveMeshes.Length; i++)
                if (_armMeshes[i] == arm) return _sleeveMeshes[i];
            return null;
        }

        public Material MaterialFor(int garmentId)
        {
            for (int i = 0; i < _garmentIds.Length && i < _garmentMaterials.Length; i++)
                if (_garmentIds[i] == garmentId) return _garmentMaterials[i];
            return null;
        }
    }
}
