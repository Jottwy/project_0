#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// El agarre EXACTO de un donante skinned del vendor (hacha, antorcha…), en espacio de SU
    /// propio hueso: se toman los vértices que pesan sobre el hueso DOMINANTE de esa malla, se
    /// llevan a su espacio con el bindpose, y de ahí salen el centro del mango y el eje del palo
    /// (la dirección de mayor extensión de esos vértices). Nada de esto depende de la pose: las
    /// bindposes son constantes de la malla, así que el resultado es válido en cualquier fotograma
    /// de la animación de equipar — colgar el objeto de ESE hueso, con este offset, sigue
    /// exactamente el camino que el vendor animó (equipar, balanceo, idle).
    ///
    /// Extraído de <c>BackroomsCrankFlashlightModelApplier.TryReadTorchHandle</c> (ADR-133), que
    /// lo usaba solo para la antorcha. El motivo de extraerlo: el destornillador y el bote de
    /// spray colgaban de una posición ESTÁTICA calculada contra los nudillos en pose de BIND
    /// (<c>TryGripFromKnuckles</c>), y ese cálculo se rompe en cuanto entra la animación real de
    /// equipar — el mismo síntoma que ya documentó el creador de la linterna: «correcto en pose de
    /// bind, pero la animación de equipar cierra el puño de otra forma».
    /// </summary>
    internal static class BackroomsDonorGrip
    {
        /// <summary>
        /// Lee el mango de <paramref name="skinNodeName"/> (un <c>SkinnedMeshRenderer</c> en algún
        /// sitio del prefab, activo o no) en espacio de su hueso dominante. <paramref name="tipChildHint"/>
        /// es opcional: el nombre de un hijo del hueso dominante que marque la PUNTA (p. ej. el VFX
        /// de la llama de una antorcha); sin él, la punta se toma como la dirección que se aleja del
        /// padre del hueso.
        /// </summary>
        internal static bool TryReadHandle(GameObject root, string skinNodeName, string tipChildHint,
            out Transform dominantBone, out Vector3 axisLocal, out Vector3 fistLocal, string tag)
        {
            dominantBone = null;
            axisLocal = Vector3.up;
            fistLocal = Vector3.zero;

            SkinnedMeshRenderer skin = null;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.name != skinNodeName) continue;
                skin = smr;
                break;
            }
            if (skin == null || skin.sharedMesh == null)
            {
                Debug.LogWarning($"{tag} Sin SkinnedMeshRenderer '{skinNodeName}' en el prefab.");
                return false;
            }

            var bones = skin.bones;
            var mesh = skin.sharedMesh;
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            var bindposes = mesh.bindposes;
            if (weights == null || weights.Length != vertices.Length || bindposes.Length != bones.Length)
            {
                Debug.LogWarning($"{tag} '{skinNodeName}' no da pesos legibles: {vertices.Length} vértices, " +
                                 $"{weights?.Length ?? 0} pesos, {bindposes.Length} bindposes, {bones.Length} " +
                                 "huesos. ¿Read/Write apagado en el FBX?");
                return false;
            }

            // El hueso que más peso acumula sobre toda la malla es el que la lleva.
            var total = new float[bones.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                var w = weights[i];
                if (w.boneIndex0 >= 0 && w.boneIndex0 < total.Length) total[w.boneIndex0] += w.weight0;
                if (w.boneIndex1 >= 0 && w.boneIndex1 < total.Length) total[w.boneIndex1] += w.weight1;
                if (w.boneIndex2 >= 0 && w.boneIndex2 < total.Length) total[w.boneIndex2] += w.weight2;
                if (w.boneIndex3 >= 0 && w.boneIndex3 < total.Length) total[w.boneIndex3] += w.weight3;
            }
            int boneIndex = 0;
            for (int i = 1; i < total.Length; i++) if (total[i] > total[boneIndex]) boneIndex = i;
            dominantBone = bones[boneIndex];
            if (dominantBone == null) return false;

            // Vértices que ese hueso MANDA, en su espacio, vía bindpose: constante, independiente de la pose.
            var handSpace = new System.Collections.Generic.List<Vector3>(vertices.Length / 2);
            var toBone = bindposes[boneIndex];
            for (int i = 0; i < vertices.Length; i++)
            {
                var w = weights[i];
                float bw = 0f;
                if (w.boneIndex0 == boneIndex) bw += w.weight0;
                if (w.boneIndex1 == boneIndex) bw += w.weight1;
                if (w.boneIndex2 == boneIndex) bw += w.weight2;
                if (w.boneIndex3 == boneIndex) bw += w.weight3;
                if (bw < 0.5f) continue;
                handSpace.Add(toBone.MultiplyPoint3x4(vertices[i]));
            }
            if (handSpace.Count < 16) return false;

            Vector3 centre = Vector3.zero;
            foreach (var v in handSpace) centre += v;
            centre /= handSpace.Count;

            // El eje del palo: la dirección en la que el mango se extiende más. Iteración de
            // potencia sobre la covarianza — unos cientos de puntos, no hace falta más.
            var axis = PrincipalAxis(handSpace, centre);
            if (axis.sqrMagnitude < 1e-8f) return false;

            Vector3 tipHint;
            var tip = string.IsNullOrEmpty(tipChildHint) ? null : FindChild(dominantBone, tipChildHint);
            if (tip != null) tipHint = tip.localPosition;
            else tipHint = dominantBone.parent != null
                ? -dominantBone.InverseTransformPoint(dominantBone.parent.position)
                : axis;
            if (Vector3.Dot(axis, tipHint - centre) < 0f) axis = -axis;

            axisLocal = axis.normalized;
            fistLocal = centre - axis * Vector3.Dot(centre, axis);

            Debug.Log($"{tag} Agarre leído de '{skinNodeName}' en espacio de '{dominantBone.name}': " +
                      $"{handSpace.Count} vértices, centro {centre}, eje {axisLocal}, punta " +
                      $"{(tip != null ? tip.localPosition.ToString() : "(sin hijo, dirección al padre)")}; " +
                      $"agarre lateral en {fistLocal}.");
            return true;
        }

        private static Vector3 PrincipalAxis(System.Collections.Generic.List<Vector3> points, Vector3 centre)
        {
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in points)
            {
                var d = p - centre;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            var v = new Vector3(1f, 1f, 1f).normalized;
            for (int i = 0; i < 32; i++)
            {
                var next = new Vector3(
                    xx * v.x + xy * v.y + xz * v.z,
                    xy * v.x + yy * v.y + yz * v.z,
                    xz * v.x + yz * v.y + zz * v.z);
                if (next.sqrMagnitude < 1e-12f) return Vector3.zero;
                v = next.normalized;
            }
            return v;
        }

        private static Transform FindChild(Transform parent, string name)
        {
            foreach (var t in parent.GetComponentsInChildren<Transform>(true))
                if (t != parent && t.name == name) return t;
            return null;
        }
    }
}
#endif
