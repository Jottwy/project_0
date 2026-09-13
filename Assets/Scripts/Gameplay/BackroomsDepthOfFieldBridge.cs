using PolymindGames.PostProcessing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// La profundidad de campo AJUSTADA (Joel, 13-09: «queda bien pero hay que adaptarlo»): con la
    /// opción encendida, lo cercano siempre nítido y el fondo del pasillo difuminado.
    /// </summary>
    /// <remarks>
    /// # Por qué no bastaba con otros números
    ///
    /// El perfil del juego (<c>STP_DemoProfile_URP</c>) trae el DoF en modo <b>Bokeh</b> con foco a
    /// 10 m. Un Bokeh enfocado lejos difumina SIEMPRE lo cercano —es óptica, no un ajuste—, así que
    /// el reloj, el libro y las manos salían borrosos. El modo que hace lo que se pide es
    /// <b>Gaussian</b>: sólo difumina a partir de <see cref="GaussianStartM"/> y nunca delante.
    ///
    /// # Por qué hace falta un puente y no sólo cambiar el modo
    ///
    /// El desenfoque del LIBRO y del INVENTARIO es del vendor (<c>DepthOfFieldAnimation</c>, con su
    /// parche local de URP): anima <c>focusDistance</c> y <c>aperture</c> sobre el MISMO perfil, y
    /// esos dos parámetros sólo existen en Bokeh. Con Gaussian fijo se perdían. Este componente mira
    /// cada fotograma si esos dos valores se han apartado de los autorados —eso es que un efecto los
    /// está animando— y durante ese tiempo devuelve el modo autorado; al cerrar el libro el vendor
    /// restaura los valores y vuelve Gaussian. Sin tocar el vendor (regla dura: PolymindGames no se
    /// edita).
    ///
    /// Con la opción apagada no hace nada salvo dejar el modo autorado, que es lo que el efecto del
    /// libro necesita para funcionar. Encender o apagar el componente sigue siendo cosa de
    /// <see cref="BackroomsGraphicsApplier"/>.
    /// </remarks>
    public sealed class BackroomsDepthOfFieldBridge : MonoBehaviour
    {
        /// <summary>Metros a los que empieza el desenfoque. Un despacho entero queda nítido.</summary>
        public const float GaussianStartM = 10f;
        /// <summary>Metros a los que el desenfoque es máximo. Con la niebla de WG3 (0,02) el fondo
        /// ya se apaga a esa distancia: el DoF acompaña, no compite.</summary>
        public const float GaussianEndM = 35f;
        /// <summary>Radio máximo del Gaussian de URP (tope 1,5). Suave: es ambiente, no efecto.</summary>
        public const float GaussianMaxRadius = 1f;

        /// <summary>Por encima de esto un parámetro se considera animado. Los valores autorados
        /// son números redondos (10 m, f/5,6): una tolerancia de centésima basta.</summary>
        private const float AnimatedEpsilon = 0.01f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject("[BackroomsDepthOfFieldBridge]") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<BackroomsDepthOfFieldBridge>();
        }

        private void LateUpdate()
        {
            Volume volume = PostProcessingManager.Instance != null ? PostProcessingManager.Instance.ActiveVolume : null;
            if (volume == null || volume.sharedProfile == null)
                return;
            if (!volume.sharedProfile.TryGet(out DepthOfField authored) || !volume.profile.TryGet(out DepthOfField runtime))
                return;

            var options = BackroomsGraphicsOptions.Instance;
            bool optionOn = options != null && options.DepthOfField.Value;
            bool animated = IsAnimatedByEffect(runtime.focusDistance.value, runtime.aperture.value,
                authored.focusDistance.value, authored.aperture.value);

            DepthOfFieldMode wanted = ModeFor(optionOn, animated, authored.mode.value);
            if (runtime.mode.value != wanted || !runtime.mode.overrideState)
                runtime.mode.Override(wanted);

            if (wanted != DepthOfFieldMode.Gaussian)
                return;
            if (!Mathf.Approximately(runtime.gaussianStart.value, GaussianStartM))
                runtime.gaussianStart.Override(GaussianStartM);
            if (!Mathf.Approximately(runtime.gaussianEnd.value, GaussianEndM))
                runtime.gaussianEnd.Override(GaussianEndM);
            if (!Mathf.Approximately(runtime.gaussianMaxRadius.value, GaussianMaxRadius))
                runtime.gaussianMaxRadius.Override(GaussianMaxRadius);
            if (!runtime.highQualitySampling.value)
                runtime.highQualitySampling.Override(true);
        }

        /// <summary>Un efecto del vendor (libro, inventario, pausa, muerte) está animando el enfoque
        /// si el valor en uso se ha apartado del autorado.</summary>
        public static bool IsAnimatedByEffect(float focusDistance, float aperture,
            float authoredFocusDistance, float authoredAperture) =>
            Mathf.Abs(focusDistance - authoredFocusDistance) > AnimatedEpsilon
            || Mathf.Abs(aperture - authoredAperture) > AnimatedEpsilon;

        /// <summary>Gaussian sólo con la opción encendida y sin efecto en marcha; si no, el modo
        /// autorado, que es el que los efectos del vendor saben animar.</summary>
        public static DepthOfFieldMode ModeFor(bool optionOn, bool animatedByEffect, DepthOfFieldMode authored) =>
            optionOn && !animatedByEffect ? DepthOfFieldMode.Gaussian : authored;
    }
}
