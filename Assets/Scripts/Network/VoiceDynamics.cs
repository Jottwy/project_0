using System;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-046 — puerta de ruido y nivel automático (AGC) de la voz local.
    ///
    /// Clase PURA y sin Unity, y no por elegancia: el procesado de audio se rompe en silencio —
    /// una constante mal puesta no da error, da un zumbido o un chasquido que hay que oír para
    /// notar. Aislado aquí se puede EJECUTAR contra señales sintéticas y comprobar cada propiedad
    /// (que el silencio no se amplifica, que no satura, que no chisporrotea en el umbral) sin
    /// abrir el editor.
    ///
    /// Orden: primero la puerta, después el AGC. Al revés, el AGC mediría el ruido de fondo que la
    /// puerta va a tirar y adaptaría su ganancia contra él.
    /// </summary>
    public sealed class VoiceDynamics
    {
        /// <summary>Nivel RMS al que el AGC intenta llevar la voz.</summary>
        public const float TargetRms = 0.12f;

        /// <summary>Topes del AGC. El mínimo evita hundir una voz muy fuerte; el máximo es lo que
        /// impide que un micrófono flojo amplifique el ruido de sala hasta el rugido.</summary>
        public const float MinGain = 0.5f;
        public const float MaxGain = 12f;

        /// <summary>Punto donde empieza la compresión suave. Por encima, la curva se aplana hacia
        /// 1 asintóticamente: nunca recorta, así que nunca mete los armónicos que hacen que un
        /// recorte duro suene a distorsión de radio.</summary>
        public const float Knee = 0.8f;

        /// <summary>La puerta CIERRA en este múltiplo del umbral de apertura. Dos umbrales y no
        /// uno: con uno solo, una voz que ronda el límite abre y cierra decenas de veces por
        /// segundo y se oye como un chisporroteo.</summary>
        public const float CloseRatio = 0.6f;

        private const float AttackSeconds = 0.005f;   // abrir tarde se come la primera sílaba
        private const float ReleaseSeconds = 0.080f;  // cerrar de golpe chasquea
        private const float GainDownSeconds = 0.050f; // pasarse de alto satura: bajar deprisa
        private const float GainUpSeconds = 0.800f;   // subir despacio, o se oye "bombeo"

        public bool GateEnabled = true;
        public bool AutoGainEnabled = true;

        /// <summary>Ganancia de puerta actual, 0..1. Expuesta para poder afirmar sobre ella.</summary>
        public float GateGain { get; private set; } = 1f;

        /// <summary>Ganancia del AGC actual.</summary>
        public float Gain { get; private set; } = 1f;

        private float _hold;

        /// <summary>Vuelve al estado neutro. Obligatorio al cambiar de micrófono: la ganancia
        /// adaptada al anterior aplicada a la primera trama del siguiente satura si el salto de
        /// sensibilidad entre aparatos es grande.</summary>
        public void Reset()
        {
            GateGain = 1f;
            Gain = 1f;
            _hold = 0f;
        }

        /// <summary>
        /// Procesa una trama en sitio.
        /// </summary>
        /// <param name="buffer">Muestras mono, -1..1. Se modifica.</param>
        /// <param name="count">Muestras válidas de <paramref name="buffer"/>.</param>
        /// <param name="rms">Nivel RMS de la trama SIN procesar.</param>
        /// <param name="dt">Duración real de la trama en segundos.</param>
        /// <param name="openThreshold">Umbral de apertura de la puerta.</param>
        /// <param name="holdSeconds">Cuánto aguanta abierta tras caer bajo el umbral de cierre.</param>
        public void Process(float[] buffer, int count, float rms, float dt,
            float openThreshold, float holdSeconds)
        {
            if (buffer == null || count <= 0) return;

            if (GateEnabled)
            {
                float closeAt = openThreshold * CloseRatio;
                if (rms >= openThreshold) _hold = holdSeconds;
                else if (rms < closeAt) _hold -= dt;

                float target = _hold > 0f ? 1f : 0f;
                float step = target > GateGain ? dt / AttackSeconds : dt / ReleaseSeconds;
                if (step < 0.0001f) step = 0.0001f;
                GateGain = MoveTowards(GateGain, target, step);
            }
            else
            {
                GateGain = 1f;
                _hold = 0f;
            }

            if (AutoGainEnabled)
            {
                // SOLO adapta con señal por encima del umbral de APERTURA. La condición NO puede
                // ser "la puerta está abierta": la puerta sigue abierta durante el `hold` y la
                // rampa de cierre, o sea justo cuando ya has dejado de hablar, y ahí el AGC ve un
                // RMS minúsculo y dispara la ganancia. Medido con la versión anterior de esta
                // línea: 10 s de ruido de sala tras hablar la subían de 0,57x a 4,37x, que es
                // EXACTAMENTE el fallo clásico que este comentario decía estar evitando.
                if (rms >= openThreshold && rms > 0.0005f)
                {
                    float want = Clamp(TargetRms / rms, MinGain, MaxGain);
                    float rate = want < Gain ? dt / GainDownSeconds : dt / GainUpSeconds;
                    Gain += (want - Gain) * Clamp01(rate);
                }
            }
            else
            {
                Gain = 1f;
            }

            float g = GateGain * Gain;
            if (g > 0.999f && g < 1.001f) return;

            for (int i = 0; i < count; i++)
                buffer[i] = SoftClip(buffer[i] * g);
        }

        /// <summary>Compresión suave por encima de <see cref="Knee"/>. Nunca devuelve |x| ≥ 1.</summary>
        public static float SoftClip(float s)
        {
            if (s > Knee) return Knee + (1f - Knee) * (float)Math.Tanh((s - Knee) / (1f - Knee));
            if (s < -Knee) return -Knee - (1f - Knee) * (float)Math.Tanh((-s - Knee) / (1f - Knee));
            return s;
        }

        private static float MoveTowards(float from, float to, float maxDelta)
        {
            float d = to - from;
            if (d > maxDelta) return from + maxDelta;
            if (d < -maxDelta) return from - maxDelta;
            return to;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
