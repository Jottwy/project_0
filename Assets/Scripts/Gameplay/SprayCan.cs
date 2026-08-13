using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-068 S3 — el bote de spray, visto desde nuestro lado.
    ///
    /// Es un componente PUENTE que se cuelga del prefab del wieldable, no una edición dentro de
    /// PolymindGames: la regla del proyecto es que el código del vendor no se toca ni se guarda
    /// dentro, porque un reimport del `.unitypackage` se lo lleva en silencio.
    ///
    /// Y es la razón por la que <see cref="SprayPainter"/> nunca pregunta "¿esto es el bote de
    /// spray?". Pregunta "¿el wieldable activo trae uno de estos?", igual que <c>ReadLightOn</c>
    /// (ADR-042) nunca pregunta si el objeto es la antorcha. Cualquier item futuro que quiera
    /// pintar solo tiene que traer este componente.
    /// </summary>
    [DisallowMultipleComponent]
    public class SprayCan : MonoBehaviour
    {
        [Header("Carga")]
        [Tooltip("Metros de trazo que pinta un bote LLENO. Es constante de diseño, igual para " +
                 "todos los botes; lo que varía por bote es cuánto le queda, y eso vive en el " +
                 "item, no aquí.")]
        [SerializeField] private float capacityMeters = 40f;

        [Tooltip("Reserva para cuando no hay item detrás (arnés de editor, tests). En juego NO " +
                 "se usa: manda la propiedad del item.")]
        [SerializeField] private float fallbackFraction = 1f;

        [Header("Trazo")]
        [Tooltip("Índice en la paleta del cliente (0..15). El wire manda el índice, no el color.")]
        [SerializeField] private byte colorIndex = 2;

        [Tooltip("Grosor de boquilla en la retícula de 256 del lienzo.")]
        [SerializeField] private byte strokeWidth = 8;

        [Tooltip("Lado del lienzo en metros. El host lo acota a [0.1, 2.0].")]
        [SerializeField] private float canvasMeters = 1.2f;

        /// <summary>
        /// LA CARGA VIVE EN EL ITEM, no en este componente, y ésa es la corrección que hace que
        /// cada bote tenga la SUYA. El prefab del wieldable es único y compartido: guardar aquí
        /// los metros restantes significaba que todos los botes del mundo compartían depósito,
        /// que coger uno nuevo no lo llenaba, y que la carga se perdía al guardar.
        ///
        /// Se usa la propiedad `Durability` del vendor y no una propia por dos razones que se
        /// obtienen gratis: `WieldableItem` ya BLOQUEA el uso cuando baja de 0,01, y la UI que
        /// pinta el estado del wieldable equipado ya la lee — la misma barra que la antorcha,
        /// sin escribir una línea de interfaz.
        /// </summary>
        private ItemProperty PaintProperty
        {
            get
            {
                if (_wieldableItem == null) _wieldableItem = GetComponentInParent<WieldableItem>();
                var item = _wieldableItem != null ? _wieldableItem.Slot.GetItem() : null;
                return item != null && item.TryGetProperty(ItemConstants.Durability, out var p) ? p : null;
            }
        }

        private WieldableItem _wieldableItem;

        /// <summary>Fracción de pintura que queda, 0..1. Es lo que la UI del vendor dibuja.</summary>
        public float PaintFraction
        {
            get
            {
                var p = PaintProperty;
                return Mathf.Clamp01(p != null ? p.Float : fallbackFraction);
            }
            private set
            {
                float v = Mathf.Clamp01(value);
                var p = PaintProperty;
                if (p != null) p.Float = v;
                else fallbackFraction = v;
            }
        }

        public float PaintMeters => PaintFraction * capacityMeters;
        public float CapacityMeters => capacityMeters;

        /// <summary>Mismo umbral que usa `WieldableItem` para bloquear el uso, para que "vacío"
        /// signifique lo mismo aquí y en el vendor.</summary>
        public bool IsEmpty => PaintFraction < 0.01f;

        public byte ColorIndex => colorIndex;
        public byte StrokeWidth => strokeWidth;

        /// <summary>Lado del lienzo, ya acotado a lo que el host acepta.</summary>
        public float CanvasMeters =>
            Mathf.Clamp(canvasMeters, SprayCanvas.MinCanvasMeters, SprayCanvas.MaxCanvasMeters);

        /// <summary>
        /// Gasta pintura. Devuelve lo que REALMENTE se pudo gastar, que puede ser menos de lo
        /// pedido si el bote se acaba a mitad del tramo — así el último trazo se corta donde se
        /// acabó la pintura en vez de dibujarse entero gratis o desaparecer entero.
        /// </summary>
        public float Spend(float meters)
        {
            if (meters <= 0f || capacityMeters <= 0f) return 0f;

            float before = PaintFraction;
            if (before <= 0f) return 0f;

            float after = Mathf.Max(0f, before - meters / capacityMeters);
            PaintFraction = after;
            return (before - after) * capacityMeters;
        }

        /// <summary>Rellena el bote (recarga, o un bote nuevo recogido del suelo).</summary>
        public void Refill(float meters)
        {
            if (capacityMeters <= 0f) return;
            PaintFraction = PaintFraction + Mathf.Max(0f, meters) / capacityMeters;
        }
    }
}
