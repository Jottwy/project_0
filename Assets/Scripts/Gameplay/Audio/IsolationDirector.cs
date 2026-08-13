using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// Dirección dinámica de la mezcla según lo AISLADO que esté el jugador.
    ///
    /// QUÉ MIDE Y POR QUÉ ESO. El aislamiento sale de la distancia a la lámpara encendida
    /// más cercana, no de un contador de tensión ni de la IA: en Level 0 la luz ES la
    /// compañía. Un pasillo iluminado es un sitio donde estás; un tramo a oscuras es un
    /// sitio donde estás solo. Ese dato ya lo tiene el director del zumbido —lo calcula
    /// cada 0,25 s para repartir sus fuentes— así que no hay sondeo nuevo ni raycast
    /// añadido: se lee lo que ya estaba.
    ///
    /// CÓMO SUENA. No con un corte de agudos global, que sería lo obvio: eso exigiría
    /// meter otro efecto en el mixer del VENDOR, y ese asset ya ha demostrado dos veces
    /// que se pierde y se desincroniza. Se usan mandos que YA están expuestos:
    ///
    ///   · roomHF  ↓  la sala se apaga de agudos — sordera, algodón en los oídos
    ///   · dry     ↓  te hundes DENTRO del reverb en vez de tenerlo alrededor
    ///   · decay   ↑  la cola se alarga: el sitio se siente más grande de lo que es
    ///   · zumbido ↓  lo poco que quedaba de compañía se aleja
    ///
    /// Los cuatro empujan a lo mismo — el espacio se abre y tú te quedas pequeño dentro —
    /// que es la sensación que el canon de Level 0 llama estar perdido, no el susto.
    ///
    /// NO SUSTITUYE AL REVERB POR ZONA: lo MODULA. La zona sigue decidiendo qué sitio es
    /// éste; esto solo dice cuánto lo estás sufriendo ahora mismo. Con el jugador bajo una
    /// lámpara la modulación es exactamente cero y la sala suena como la autoró su zona.
    /// </summary>
    public static class IsolationDirector
    {
        /// <summary>
        /// A partir de esta distancia a la lámpara más cercana se considera aislamiento
        /// total. 12 m son dos baldosas y media más allá del alcance del zumbido (7 m), o
        /// sea: no oyes ninguna lámpara Y llevas un rato sin oírla.
        /// </summary>
        private const float FullIsolationMetres = 12f;

        /// <summary>
        /// Por debajo de esto no hay modulación ninguna. Coincide con el alcance del
        /// zumbido: mientras oigas una lámpara, no estás solo.
        /// </summary>
        private const float NoIsolationMetres = 7f;

        /// <summary>
        /// Constante de tiempo del aislamiento. MUY lenta a propósito (8 s): esto describe
        /// una situación, no un evento. Si respondiera rápido, asomarse a un hueco oscuro y
        /// volver haría respirar la mezcla y se leería como un efecto en vez de como un
        /// sitio. Es el mismo error que la AudioReverbZone anclada al jugador, en el eje
        /// del tiempo en vez del espacio.
        /// </summary>
        private const float Tau = 8f;

        // Cuánto puede desviar como MÁXIMO cada mando, con aislamiento total. Deliberadamente
        // contenidos: si la modulación puede más que la autoría, deja de haber zonas.
        private const float RoomHFDrop   = -2200f; // mB
        private const float DryDrop      = -900f;  // mB
        private const float DecayStretch = 1.6f;   // × sobre la cola de la zona
        private const float HumDuck      = 0.55f;  // × sobre el volumen del zumbido

        /// <summary>0 = bajo una lámpara, 1 = solo en la oscuridad. Suavizado.</summary>
        public static float Isolation => _isolation;
        private static float _isolation;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _isolation = 0f;

        /// <summary>
        /// Actualiza el aislamiento. <paramref name="nearestLampMetres"/> es
        /// <see cref="float.MaxValue"/> cuando no hay ninguna lámpara audible.
        /// </summary>
        public static void Report(float nearestLampMetres, float unscaledDeltaTime)
        {
            float target = Mathf.InverseLerp(NoIsolationMetres, FullIsolationMetres,
                Mathf.Min(nearestLampMetres, FullIsolationMetres));
            _isolation = Mathf.Lerp(_isolation, target, Mathf.Clamp01(unscaledDeltaTime / Tau));
        }

        /// <summary>
        /// La sala de la zona, teñida por el aislamiento actual. Se aplica DESPUÉS del
        /// resolvedor por zona para que la autoría siga mandando sobre el carácter del
        /// sitio y esto solo mueva cuánto se siente.
        /// </summary>
        public static ReverbMixerDriver.RoomTone Colour(ReverbMixerDriver.RoomTone tone)
        {
            float k = _isolation;
            if (k <= 0.001f) return tone;

            // Una zona MUDA se queda muda: el aislamiento no puede encender reverb donde el
            // autor dijo que no hay. Sin esta guarda, un pasillo autorado sin sala empezaría
            // a resonar solo por alejarse de una lámpara.
            if (tone.room <= ReverbMixerDriver.RoomSilent + 1f) return tone;

            tone.roomHF = Mathf.Max(ReverbMixerDriver.RoomSilent, tone.roomHF + RoomHFDrop * k);
            tone.dry    = Mathf.Max(ReverbMixerDriver.RoomSilent, tone.dry    + DryDrop    * k);
            tone.decay  = Mathf.Clamp(tone.decay * Mathf.Lerp(1f, DecayStretch, k), 0.1f, 20f);
            return tone;
        }

        /// <summary>Volumen del zumbido teñido por el aislamiento.</summary>
        public static float ColourHumVolume(float volume) =>
            volume * Mathf.Lerp(1f, HumDuck, _isolation);
    }
}
