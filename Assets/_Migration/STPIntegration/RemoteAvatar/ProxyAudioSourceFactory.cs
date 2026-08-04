using PolymindGames; // AudioManager / AudioChannel
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Construccion compartida de la AudioSource 3D que cada hook de audio del proxy cuelga del peer
    /// (ADR-042). Extraida VERBATIM de las copias que vivian en ProxyFootstepHook, ProxyVocalHook,
    /// ProxyDamageAudioHook, ProxyMeleeHook y ProxyRevealHook: mismos ajustes, mismo orden de
    /// asignacion, nada nuevo. La rama de rolloff la comparte ademas ProxyFireAudioHook.
    ///
    /// NO es un MonoBehaviour y no debe serlo: los hooks estan cableados en los prefabs por GUID, asi
    /// que una clase base o un cambio de tipo romperia ese cableado. Solo helpers estaticos.
    /// </summary>
    internal static class ProxyAudioSourceFactory
    {
        /// <summary>
        /// Crea el GameObject hijo y su AudioSource con los ajustes que TODOS los hooks comparten.
        /// Parented al proxy para que el sonido siga a lo que lo produjo: un sonido que se queda donde
        /// el peer ESTABA apunta al jugador a un pasillo vacio.
        /// </summary>
        /// <param name="parent">Transform del proxy del que cuelga la source.</param>
        /// <param name="label">Nombre del GameObject — uno por hook, para que la jerarquia se lea.</param>
        /// <param name="localPosition">Offset sobre el proxy (altura de cabeza/pecho para las voces).
        /// El default es el origen del padre, que es justo lo que un hijo recien parented ya tiene.</param>
        public static AudioSource CreateChildSource(Transform parent, string label,
            Vector3 localPosition = default)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 1f; // fully 3D; at 0 it would be dead-centre at any distance
            src.dopplerLevel = 0f;
            return src;
        }

        /// <summary>
        /// Curva de distancia: la de corte duro de ADR-042, o el rolloff autorizado en el inspector.
        /// </summary>
        public static void ApplyRolloff(AudioSource source, bool hardCutoff, AudioRolloffMode rolloff,
            float minDistance, float maxDistance)
        {
            if (hardCutoff)
            {
                ProxyAudioCurves.ApplyHardCutoff(source, minDistance, maxDistance);
            }
            else
            {
                source.rolloffMode = rolloff;
                source.minDistance = minDistance;
                source.maxDistance = maxDistance;
            }
        }

        /// <summary>Enruta por el grupo Sfx del mixer para que el volumen de efectos del jugador siga
        /// aplicando. No manager (a bare test scene) → still audible, just unmixed. Never an
        /// exception.</summary>
        public static void RouteToSfx(AudioSource source)
        {
            var audio = AudioManager.Instance;
            source.outputAudioMixerGroup = audio != null ? audio.GetMixerGroup(AudioChannel.Sfx) : null;
        }

        /// <summary>El montaje completo para los hooks que SIEMPRE usan la curva de corte duro.</summary>
        public static AudioSource CreateHardCutoffSource(Transform parent, string label,
            float minDistance, float maxDistance, Vector3 localPosition = default)
        {
            var src = CreateChildSource(parent, label, localPosition);
            ProxyAudioCurves.ApplyHardCutoff(src, minDistance, maxDistance);
            RouteToSfx(src);
            return src;
        }
    }
}
