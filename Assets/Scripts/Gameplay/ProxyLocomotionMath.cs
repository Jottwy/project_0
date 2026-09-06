using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Aritmética pura de la locomoción de los proxies (avatares de peers remotos). Sin estado, sin
    /// MonoBehaviour, sin Animator: solo la conversión de una velocidad a las coordenadas que come el
    /// BlendTree.
    ///
    /// VIVE AQUÍ Y NO EN EL FEEDER A PROPÓSITO. <c>ProxyLocomotionFeeder</c> está en
    /// <c>_Migration/</c>, o sea en Assembly-CSharp, y un asmdef de tests NO puede referenciar
    /// Assembly-CSharp — el sentido de la marcha de las referencias es el contrario. Poniendo la
    /// cuenta en el assembly <c>BackroomsSurvival</c>, `EditModeTests` sí la ve y la parte que puede
    /// equivocarse en silencio queda cubierta por tests. El feeder sigue siendo el único que habla
    /// con el Animator.
    ///
    /// LA ESCALA ES DE "TIERS", NO m/s. Es la escala del vendor que ADR-013 ya calibró a ojo en Play
    /// (Idle=0, Walk=1, Run=3) y que arrastra el suavizado del lerp de <c>RemotePlayerManager</c>;
    /// las velocidades reales del jugador (2,8 m/s andando, 5,5 corriendo — FPS_Player.prefab) NO son
    /// las que mide el proxy, y por eso los umbrales del feeder son más bajos que las del motor. No se
    /// tocan aquí.
    ///
    /// INVARIANTE que ata las dos funciones y que el BlendTree 2D da por hecho:
    /// <c>VelocityToTiers(v).magnitude == SpeedToTier(|v_planar|)</c>. Es lo que permite que los
    /// anillos del árbol direccional estén a radio 1 (walk) y 3 (run) — los MISMOS números que la
    /// escala escalar — en vez de en una segunda escala que habría que mantener sincronizada.
    /// </summary>
    public static class ProxyLocomotionMath
    {
        /// <summary>Quieto.</summary>
        public const float IdleTier = 0f;

        /// <summary>Andando. Radio del primer anillo del árbol direccional.</summary>
        public const float WalkTier = 1f;

        /// <summary>Corriendo. Radio del segundo anillo. Es también el TOPE: este juego no tiene un
        /// estado "sprint" separado — <c>MovementStateType</c> del vendor va Idle/Walk/Run/Crouch/
        /// Slide/Jump/Airborne, y correr (5,5 m/s) YA es la velocidad máxima. Un tercer anillo sería
        /// un estado que el jugador no puede producir.</summary>
        public const float RunTier = 3f;

        /// <summary>
        /// Mapea una velocidad planar (m/s) a la escala de tiers. Curva idéntica a la que
        /// <c>ProxyLocomotionFeeder</c> traía desde ADR-013: zona muerta, rampa 0..1 hasta
        /// <paramref name="walkSpeed"/>, rampa 1..3 hasta <paramref name="runSpeed"/>, y clamp.
        /// </summary>
        public static float SpeedToTier(float speed, float deadzoneSpeed, float walkSpeed, float runSpeed)
        {
            if (speed <= deadzoneSpeed)
                return IdleTier;

            if (speed <= walkSpeed)
            {
                float denom = Mathf.Max(0.0001f, walkSpeed - deadzoneSpeed);
                return Mathf.Clamp01((speed - deadzoneSpeed) / denom);
            }

            if (speed <= runSpeed)
            {
                float denom = Mathf.Max(0.0001f, runSpeed - walkSpeed);
                float t = Mathf.Clamp01((speed - walkSpeed) / denom);
                return Mathf.Lerp(WalkTier, RunTier, t);
            }

            return RunTier;
        }

        /// <summary>
        /// Convierte una velocidad EN ESPACIO LOCAL del avatar (X = su derecha, Z = su frente; la Y se
        /// ignora, el salto lo lleva <c>ProxyJumpFeeder</c>) en el par (MoveX, MoveY) del BlendTree
        /// direccional: misma dirección que la velocidad, y módulo igual al tier de su rapidez.
        ///
        /// Por debajo de la zona muerta devuelve cero EXACTO y no una dirección diminuta: a velocidad
        /// casi nula la dirección es ruido del lerp de red, y un vector unitario de ruido haría girar
        /// el pie de apoyo de un peer parado.
        /// </summary>
        public static Vector2 VelocityToTiers(Vector3 localVelocity, float deadzoneSpeed, float walkSpeed, float runSpeed)
        {
            var planar = new Vector2(localVelocity.x, localVelocity.z);
            float speed = planar.magnitude;
            float tier = SpeedToTier(speed, deadzoneSpeed, walkSpeed, runSpeed);

            if (tier <= 0f || speed <= 0.000001f)
                return Vector2.zero;

            return planar * (tier / speed);
        }
    }
}
