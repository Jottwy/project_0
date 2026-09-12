using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Lo que se MUEVE entre plantas recibe la luz de TODAS las plantas.
    /// </summary>
    /// <remarks>
    /// El mundo reparte la luz por planta con <see cref="Wg3StoreyLayers"/> (ADR-104 enm. 2): cada
    /// lámpara ilumina sólo la capa de render de su planta y cada superficie WG3 nace con la
    /// máscara de las plantas que atraviesa. Lo que NO monta WG3 —los proxies de los demás
    /// jugadores y vigilantes, los brazos de primera persona que viven dentro de cada wieldable,
    /// el objeto que llevas en la mano— se queda con la máscara por defecto de Unity, el bit 0.
    /// Con tres sótanos desplazando las capas, el bit 0 es B3 y ninguna otra: en la calle y en
    /// cualquier piso esos objetos sólo los alumbraría una lámpara de B3, y como el ambiente es
    /// negro desde el 07-09 salen NEGROS del todo con las paredes de al lado bien iluminadas.
    /// Cazado el 12-09 con el brazo y un vigilante sentado en su silla: «parece que es por la
    /// altura», y es exactamente la planta.
    ///
    /// La cura no es darles la capa de su cota (eso hace <c>TorchShadowCaster.ApplyStoreyLayer</c>
    /// con las LUCES de mano, que sí tienen que respetar la fuga): un cuerpo mide dos metros y
    /// cambia de planta a cada paso, y la fuga que la máscara cierra es entre losas de 12 cm y
    /// lámparas de 11 m de alcance, no sobre un objeto pequeño y móvil. Un renderer dinámico
    /// lleva TODAS las capas y lo ilumina la lámpara que tenga cerca, sea de la planta que sea.
    ///
    /// Y sin light probes, por lo mismo que <see cref="Wg3StoreyLayers.Apply(Renderer, uint)"/>:
    /// los probes horneados de la escena son del demo del vendor, al aire libre.
    ///
    /// Es un componente y no una pasada al instanciar porque la jerarquía CRECE después: un
    /// wieldable se activa al equiparlo, un pickup se cuelga del proxy al cogerlo. Repasar cada
    /// medio segundo lo que haya colgado cuesta un <c>GetComponentsInChildren</c> sin reservar y
    /// cubre todo lo que llegue tarde.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class Wg3DynamicLitLayers : MonoBehaviour
    {
        /// <summary>Todas las capas de render: la lámpara de cualquier planta lo ilumina.</summary>
        public const uint AllStoreys = uint.MaxValue;

        /// <summary>Cada cuánto se repasa la jerarquía por si colgaron algo nuevo.</summary>
        public const float RescanSeconds = 0.5f;

        private static readonly List<Renderer> Scratch = new List<Renderer>(64);

        private float _next;

        /// <summary>
        /// Cuelga el componente de <paramref name="root"/> (una sola vez) y aplica ya la máscara a
        /// todo lo que haya debajo. Devuelve el componente, o nulo sin raíz.
        /// </summary>
        public static Wg3DynamicLitLayers Attach(GameObject root)
        {
            if (root == null) return null;
            var self = root.GetComponent<Wg3DynamicLitLayers>();
            if (self == null) self = root.AddComponent<Wg3DynamicLitLayers>();
            Apply(root);
            return self;
        }

        /// <summary>
        /// Escribe la máscara y apaga los probes en todos los renderers bajo <paramref name="root"/>,
        /// inactivos incluidos. Devuelve cuántos había que tocar.
        /// </summary>
        public static int Apply(GameObject root)
        {
            if (root == null) return 0;
            root.GetComponentsInChildren(true, Scratch);
            int written = 0;
            for (int i = 0; i < Scratch.Count; i++)
            {
                Renderer r = Scratch[i];
                if (r == null) continue;
                bool dirty = false;
                if (r.renderingLayerMask != AllStoreys)
                {
                    r.renderingLayerMask = AllStoreys;
                    dirty = true;
                }
                if (r.lightProbeUsage != LightProbeUsage.Off)
                {
                    r.lightProbeUsage = LightProbeUsage.Off;
                    dirty = true;
                }
                if (dirty) written++;
            }
            Scratch.Clear();
            return written;
        }

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + RescanSeconds;
            Apply(gameObject);
        }
    }
}
