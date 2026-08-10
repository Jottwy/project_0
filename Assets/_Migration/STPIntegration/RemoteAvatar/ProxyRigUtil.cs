using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Helpers de rig compartidos por los hooks de proxy remoto y el CorpseSpawner. Clase estatica
    /// pura: NO es MonoBehaviour y NO debe serlo. Los hooks siguen siendo los MonoBehaviours
    /// cableados por GUID en RemotePlayerAvatar.prefab / el prefab de corpse; esto solo aloja el
    /// cuerpo de cuatro metodos que estaban copiados byte a byte entre ellos.
    /// </summary>
    public static class ProxyRigUtil
    {
        /// <summary>
        /// Resuelve un bone POR NOMBRE recorriendo toda la jerarquia bajo <paramref name="root"/>,
        /// incluidos los inactivos. Devuelve null si no existe: todos los llamantes hacen no-op.
        /// </summary>
        public static Transform FindBone(Transform root, string boneName)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == boneName)
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Rotacion ADITIVA en espacio de mundo sobre un bone, aplicada en LateUpdate (despues del
        /// Animator). No-op si el bone falta o el angulo es ~0: a peso 0 no se toca ningun bone.
        /// </summary>
        public static void ApplyBend(Transform bone, float degrees, Vector3 axis)
        {
            if (bone == null || Mathf.Approximately(degrees, 0f))
                return;
            bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }

        /// <summary>
        /// Strip every non-visual component so a pickup becomes an inert prop: no physics, no
        /// interaction, no pooling. MeshFilter/MeshRenderer/LODGroup are not MonoBehaviours, so
        /// they survive; the static mesh (with LODs) is exactly what we want to render.
        ///
        /// Ojo: NO apaga sombras. <c>ProxyCarryHook</c> anade ese paso por su cuenta despues de
        /// llamar aqui, porque solo su caso (N planks por peer) lo necesita.
        /// </summary>
        public static void NeutralizeToVisualOnly(GameObject go)
        {
            // EL ORDEN ES EL ARREGLO, no un detalle de estilo: Unity RECHAZA destruir un componente
            // del que otro declara depender por [RequireComponent], y el rechazo es silencioso salvo
            // por una línea de log — el componente sigue vivo, o sea que "neutralizar" no
            // neutralizaba. Medido en el standalone: "Can't remove CapsuleCollider because
            // SurfaceImpactHandler (Script) depends on it", porque los colliders se destruían antes
            // que los MonoBehaviour del vendor que los requieren.
            //
            // Se va de fuera hacia dentro de la cadena de dependencias: primero los MonoBehaviour
            // que dependen de algo, después Interactable (del que dependen las Workstation del
            // vendor, mismo caso que CorpseSpawner.ResolveChestVisual), y solo al final los
            // Rigidbody/Collider de los que dependían los primeros. `Destroy` es diferido a fin de
            // frame, pero la validación ya cuenta lo marcado como pendiente, así que basta con el
            // orden — no hace falta esperar un frame entre pasadas.
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is not Interactable)
                    UnityEngine.Object.Destroy(mb);
            }
            foreach (var interactable in go.GetComponentsInChildren<Interactable>(true))
                UnityEngine.Object.Destroy(interactable);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                UnityEngine.Object.Destroy(rb);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.Destroy(col);
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}
