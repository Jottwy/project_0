using System;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Medical
{
    /// <summary>Zona corporal tratable. Alpha 1 sólo distingue los dos brazos.</summary>
    public enum BodyPartSide
    {
        Left = 0,
        Right = 1,
    }

    /// <summary>Estado médico de UNA zona. Ordinal estable: se escribe en logs y en tests.</summary>
    public enum BodyPartCondition
    {
        Healthy = 0,
        Wounded = 1,
        Bandaged = 2,
    }

    /// <summary>
    /// El estado médico del jugador LOCAL, por zona corporal. Es la única fuente de verdad de la
    /// que beben las tres representaciones: los brazos de primera persona, el bit que viaja a los
    /// peers y —a través de ese bit— el avatar remoto que ven los demás.
    ///
    /// POR QUÉ NO ES UN MonoBehaviour. El rig de STP se reconstruye en runtime (el mismo NRE de
    /// <c>CharacterClothing.Start</c> que obligó a <see cref="Net.PlayerPoseTransmitter"/> a vivir
    /// en su propio objeto), y un componente colgado del jugador se lo lleva por delante. Aquí el
    /// estado es un objeto plano con un <see cref="Local"/> estático: sobrevive a la reconstrucción
    /// del rig, a un cambio de escena y a que no exista todavía ningún jugador, y se prueba en
    /// EditMode sin levantar una escena.
    ///
    /// VOLÁTIL A PROPÓSITO (decisión de Joel, Alpha 1). Nada de esto se guarda ni viaja como campo:
    /// la representación sale por dos bits libres de <c>buttons</c> (ADR-044), que son cosméticos y
    /// no se persisten. Al reconectar, brazos limpios. Persistirlo de verdad significa campo nuevo
    /// en el snapshot del jugador, y eso es cambio del schema de guardado: ADR nuevo, no un parche.
    ///
    /// LA AUTORIDAD ES DEL CLIENTE, y eso también es decisión de alcance: el backend hoy no sabe de
    /// partes del cuerpo (el daño es un escalar contra <c>PlayerStats.health</c>), así que hacer
    /// autoritativas las heridas sería tocar el wire y Rust. Un peer no puede mentir sobre la venda
    /// de OTRO: cada cliente sólo escribe la suya, y lo que se falsearía es una venda de adorno.
    /// </summary>
    public sealed class PlayerMedicalState
    {
        /// <summary>
        /// El estado del jugador de esta máquina. Estático porque sus tres consumidores viven en
        /// sitios que no se ven entre sí: el wieldable (que lo escribe), el transmisor de poses y
        /// el hook de los brazos de primera persona.
        /// </summary>
        public static PlayerMedicalState Local { get; } = new PlayerMedicalState();

        /// <summary>
        /// Daño mínimo, en puntos de salud, que abre una herida. Debajo de esto el golpe duele y no
        /// deja marca — si no, el primer roce con una pared te deja los dos brazos vendables.
        /// </summary>
        public const float MinWoundDamage = 8f;

        /// <summary>
        /// Margen lateral, en metros, por debajo del cual un impacto se considera CENTRADO y no
        /// decide lado. Un golpe al pecho no hiere un brazo concreto: lo resuelve el reparto de
        /// <see cref="PickFallbackSide"/>.
        /// </summary>
        private const float SideDeadZoneMetres = 0.02f;

        /// <summary>
        /// ALPHA 1: todo va al brazo IZQUIERDO (decisión de Joel, 08-09). Con esto en <c>true</c>
        /// las heridas nacen siempre a la izquierda, la venda trata la izquierda y es ahí donde se
        /// ve — el lado deja de decidirlo el golpe.
        ///
        /// Y ES COHERENTE CON LO QUE SE VE EN LAS MANOS: el rollo de gasa se empuña con la DERECHA
        /// (`Hand.R`), así que el brazo que queda libre para vendarse es el izquierdo. Un vendaje
        /// del brazo derecho pediría cambiar de mano, que es animación y está fuera de Alpha 1.
        ///
        /// Es una propiedad de INSTANCIA y no una constante para que los tests puedan apagarla y
        /// seguir cubriendo el reparto por dirección — que sigue vivo debajo, listo para el día que
        /// haya más zonas. El único sitio que la deja en su valor por defecto es el juego.
        /// </summary>
        public bool RestrictToLeftArm { get; set; } = true;

        private readonly BodyPartCondition[] _conditions = new BodyPartCondition[2];

        // Última zona herida: es la que ofrece TryGetWoundedSide, para que vendarse trate lo que
        // acaba de doler y no lo que lleva medio mundo abierto.
        private BodyPartSide _lastWounded = BodyPartSide.Left;

        // Reparto de los impactos que no dicen lado (daño sin punto ni fuerza: caídas, veneno).
        // Alterna en vez de sortear: un test no puede afirmar nada sobre un Random.
        private BodyPartSide _nextFallback = BodyPartSide.Left;

        /// <summary>Zona y estado nuevo. Se dispara SÓLO cuando el estado cambia de verdad.</summary>
        public event Action<BodyPartSide, BodyPartCondition> Changed;

        public BodyPartCondition ConditionOf(BodyPartSide side) => _conditions[(int)side];

        public bool IsBandaged(BodyPartSide side) => _conditions[(int)side] == BodyPartCondition.Bandaged;

        public bool IsWounded(BodyPartSide side) => _conditions[(int)side] == BodyPartCondition.Wounded;

        /// <summary>Hay algo que vendar ahora mismo.</summary>
        public bool HasTreatableWound =>
            _conditions[0] == BodyPartCondition.Wounded || _conditions[1] == BodyPartCondition.Wounded;

        /// <summary>
        /// La herida que toca tratar: la más reciente, si sigue abierta. Devuelve <c>false</c>
        /// cuando no hay ninguna, que es lo que impide gastar una venda sin herida.
        /// </summary>
        public bool TryGetWoundedSide(out BodyPartSide side)
        {
            if (IsWounded(_lastWounded))
            {
                side = _lastWounded;
                return true;
            }

            var other = Other(_lastWounded);
            if (IsWounded(other))
            {
                side = other;
                return true;
            }

            side = _lastWounded;
            return false;
        }

        /// <summary>
        /// Un golpe local. <paramref name="damage"/> es la magnitud (positiva) del daño recibido;
        /// <paramref name="reference"/> es el transform del jugador, que da los ejes con los que se
        /// decide el lado. Devuelve la zona herida, o <c>null</c> si el golpe no abrió nada.
        ///
        /// Un brazo YA vendado vuelve a herirse: la venda se pierde con el golpe. Es lo que hace que
        /// el visual no sea decorativo — se puede perder, así que significa algo mientras está.
        /// </summary>
        public BodyPartSide? ReportDamage(float damage, Vector3 hitPoint, Vector3 hitForce, Transform reference)
        {
            if (damage < MinWoundDamage)
                return null;

            var side = ResolveSide(hitPoint, hitForce, reference);

            _lastWounded = side;
            if (_conditions[(int)side] == BodyPartCondition.Wounded)
                return side; // ya estaba abierta: no hay cambio que anunciar

            Set(side, BodyPartCondition.Wounded);
            return side;
        }

        /// <summary>
        /// Cierra la herida de esa zona con una venda. Devuelve <c>false</c> si ahí no había herida
        /// —una zona sana o ya vendada no consume venda—, y ése es el gate que usa el wieldable
        /// para no gastar el item.
        /// </summary>
        public bool ApplyBandage(BodyPartSide side)
        {
            if (_conditions[(int)side] != BodyPartCondition.Wounded)
                return false;

            Set(side, BodyPartCondition.Bandaged);
            return true;
        }

        /// <summary>
        /// Borrón y cuenta nueva: al morir y reaparecer el cuerpo es otro. Anuncia cada zona que
        /// cambie, para que los visuales se apaguen solos sin que nadie los busque.
        /// </summary>
        public void ResetAll()
        {
            for (int i = 0; i < _conditions.Length; i++)
            {
                if (_conditions[i] != BodyPartCondition.Healthy)
                    Set((BodyPartSide)i, BodyPartCondition.Healthy);
            }

            _lastWounded = BodyPartSide.Right;
            _nextFallback = BodyPartSide.Right;
        }

        /// <summary>
        /// De dónde vino el golpe, en ejes del jugador. Se prueban dos señales y hay un reparto para
        /// cuando ninguna sirve, porque MUCHAS fuentes de daño dejan <c>DamageArgs</c> a cero: una
        /// caída, el hambre o el veneno no tienen punto de impacto.
        ///
        /// La FUERZA va al revés que el punto, y no es un despiste: el empuje sale del atacante y te
        /// aparta, así que un golpe que te lanza a tu derecha entró por tu izquierda.
        /// </summary>
        private BodyPartSide ResolveSide(Vector3 hitPoint, Vector3 hitForce, Transform reference)
        {
            // Alpha 1: el lado no se decide, ES el izquierdo. Va lo PRIMERO para que quede claro que
            // todo lo de abajo está apagado, no roto.
            if (RestrictToLeftArm)
                return BodyPartSide.Left;

            if (reference != null)
            {
                if (hitPoint != Vector3.zero)
                {
                    float x = reference.InverseTransformPoint(hitPoint).x;
                    if (Mathf.Abs(x) > SideDeadZoneMetres)
                        return x > 0f ? BodyPartSide.Right : BodyPartSide.Left;
                }

                if (hitForce != Vector3.zero)
                {
                    float x = reference.InverseTransformDirection(hitForce).x;
                    if (Mathf.Abs(x) > SideDeadZoneMetres)
                        return x > 0f ? BodyPartSide.Left : BodyPartSide.Right;
                }
            }

            return PickFallbackSide();
        }

        /// <summary>
        /// Golpe sin lado. Se prefiere un brazo SANO —dos heridas repartidas se ven mejor que una
        /// zona machacada y otra intacta— y, si los dos están igual, se alterna.
        /// </summary>
        private BodyPartSide PickFallbackSide()
        {
            var preferred = _nextFallback;
            var other = Other(preferred);

            if (_conditions[(int)preferred] != BodyPartCondition.Healthy
                && _conditions[(int)other] == BodyPartCondition.Healthy)
                preferred = other;

            _nextFallback = Other(preferred);
            return preferred;
        }

        private void Set(BodyPartSide side, BodyPartCondition condition)
        {
            _conditions[(int)side] = condition;
            Changed?.Invoke(side, condition);
        }

        private static BodyPartSide Other(BodyPartSide side) =>
            side == BodyPartSide.Left ? BodyPartSide.Right : BodyPartSide.Left;
    }
}
