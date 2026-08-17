using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// El ruido del bote de spray: siseo mientras se pinta, cascabel de la bolita al sacarlo y
    /// escupitajo cuando ya no queda pintura.
    ///
    /// SINTETIZADO Y NO GRABADO, igual que el zumbido de <see cref="FluorescentHumDirector"/>: un siseo
    /// es ruido filtrado, no hay nada que grabar que salga mejor, y así no entra un .wav al
    /// repositorio ni hay que importarlo. Si algún día hay clip autorado, se deja caer en
    /// <c>Resources/Audio/SprayHiss</c> y este componente lo prefiere sin tocar código.
    ///
    /// El componente NO decide cuándo suena: eso lo sabe <see cref="SprayPainter"/>, que es quien
    /// mira el gatillo y la pared. Aquí solo se recibe "sí/no" cada frame y se hacen las
    /// transiciones suaves, porque un loop que arranca y para en seco hace CLICK.
    ///
    /// Nace preparado para el jugador remoto (<c>spatial</c>) aunque hoy solo lo use el local: la
    /// fase A de tiempo real necesita exactamente el mismo emisor, colgado del proxy en vez de la
    /// cámara.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpraySfx : MonoBehaviour
    {
        /// <summary>Clip autorado opcional. Si existe, gana al sintetizado.</summary>
        private const string AuthoredHissResource = "Audio/SprayHiss";

        /// <summary>Volumen del siseo a pleno. Es un SFX en la mano, no un fondo de sala.</summary>
        private const float HissVolume = 0.30f;

        /// <summary>El bote vacío escupe aire: mismo ruido, más flojo y más grave.</summary>
        private const float SputterVolume = 0.10f;
        private const float SputterPitch = 0.75f;

        private const float AttackSeconds = 0.06f;
        private const float ReleaseSeconds = 0.09f;

        // Un clip por proceso, no uno por bote: son idénticos y ocupan ~170 KB cada uno.
        private static AudioClip _sharedHiss;
        private static AudioClip _sharedRattle;

        private AudioSource _source;
        private AudioSource _oneShot;
        private bool _routed;
        private float _target;
        private float _level;

        /// <summary>Transform al que se pega el emisor cada frame (la cámara, o la mano del proxy).</summary>
        public Transform follow;

        /// <summary>
        /// Crea el emisor. <paramref name="spatial"/> a false para el bote propio — lo llevas a
        /// medio palmo de la oreja y espacializarlo solo le quita cuerpo — y a true para el de
        /// otro jugador, que sí tiene que sonar desde donde está.
        /// </summary>
        public static SpraySfx Create(string name, bool spatial)
        {
            var go = new GameObject(name);
            var sfx = go.AddComponent<SpraySfx>();
            sfx.Build(spatial);
            return sfx;
        }

        private void Build(bool spatial)
        {
            if (_sharedHiss == null) _sharedHiss = ResolveHiss();
            if (_sharedRattle == null) _sharedRattle = GenerateRattle();

            var src = gameObject.AddComponent<AudioSource>();
            src.clip = _sharedHiss;
            src.loop = true;
            src.playOnAwake = false;
            src.volume = 0f;
            src.dopplerLevel = 0f;
            src.spatialBlend = spatial ? 1f : 0.25f;
            src.minDistance = 1.5f;
            src.maxDistance = 14f;
            src.rolloffMode = AudioRolloffMode.Linear;
            _source = src;

            // Emisor aparte para el cascabel, y no `PlayOneShot` sobre el del siseo: ese vive con
            // el volumen a cero mientras no se pinta, y `PlayOneShot` MULTIPLICA por el volumen de
            // la fuente — el golpe habría salido mudo justo cuando se quiere oír, al sacar el bote.
            var one = gameObject.AddComponent<AudioSource>();
            one.loop = false;
            one.playOnAwake = false;
            one.volume = 1f;
            one.dopplerLevel = 0f;
            one.spatialBlend = src.spatialBlend;
            one.minDistance = src.minDistance;
            one.maxDistance = src.maxDistance;
            one.rolloffMode = src.rolloffMode;
            _oneShot = one;
        }

        /// <summary>Pinta: siseo a pleno.</summary>
        public void SetSpraying(bool on)
        {
            _target = on ? HissVolume : 0f;
            if (on && _source != null) _source.pitch = 1f;
        }

        /// <summary>Aprieta con el bote vacío: aire y nada más.</summary>
        public void SetSputtering(bool on)
        {
            if (!on) return;
            _target = SputterVolume;
            if (_source != null) _source.pitch = SputterPitch;
        }

        /// <summary>La bolita. Se dispara al sacar el bote; un día lo hará la animación de agitar.</summary>
        public void Shake()
        {
            if (_oneShot == null || _sharedRattle == null) return;
            RouteToSfxMixer();
            _oneShot.PlayOneShot(_sharedRattle, 0.5f);
        }

        private void LateUpdate()
        {
            if (follow != null) transform.position = follow.position;
            if (_source == null) return;

            // Subida y bajada por rampa: el loop no arranca ni para en seco. La rampa se mide
            // contra el volumen de trabajo, no contra 1: si no, "60 ms" serían 18 ms reales.
            float rate = _target > _level ? AttackSeconds : ReleaseSeconds;
            float step = HissVolume * Time.unscaledDeltaTime / Mathf.Max(1e-4f, rate);
            _level = Mathf.MoveTowards(_level, _target, step);
            _source.volume = _level;

            if (_level > 0.001f)
            {
                RouteToSfxMixer();
                if (!_source.isPlaying) _source.Play();
            }
            else if (_source.isPlaying && _source.loop)
            {
                // Parar de verdad al llegar a cero: un AudioSource mudo sigue costando una voz.
                _source.Stop();
            }

            // El objetivo se consume cada frame: quien quiera que siga sonando lo vuelve a pedir.
            // Así "soltar el gatillo" no necesita mensaje propio y no hay estado que se atasque
            // encendido si el que pintaba desaparece a mitad de trazo.
            _target = 0f;
        }

        /// <summary>
        /// Al bus de SFX. Sin esto el emisor sale por Master y NINGUNA bajada de volumen lo
        /// corrige — ya pasó con el zumbido y costó tres commits encontrarlo. Perezoso y
        /// reintentable porque el AudioManager puede no existir todavía (o nunca, en una escena
        /// de test pelada: entonces suena sin mezclar, jamás una excepción).
        /// </summary>
        private void RouteToSfxMixer()
        {
            if (_routed || _source == null) return;
            var mgr = AudioManager.Instance;
            if (mgr == null) return;
            var group = mgr.GetMixerGroup(AudioChannel.Sfx);
            if (group == null) return;
            _source.outputAudioMixerGroup = group;
            _routed = true;
        }

        private static AudioClip ResolveHiss()
        {
            var authored = Resources.Load<AudioClip>(AuthoredHissResource);
            return authored != null ? authored : GenerateHiss();
        }

        /// <summary>
        /// El siseo: ruido blanco por un pasa-banda centrado en ~3,5 kHz (la boquilla) con un poco
        /// de agudo crudo encima (el aire), una respiración lenta de amplitud para que no suene a
        /// estática de televisión, y la cola cruzada con la cabeza para que el bucle no chasquee.
        ///
        /// Determinista a propósito (semilla fija): el mismo bote suena igual en toda máquina, y
        /// el test puede comprobar la costura del bucle.
        /// </summary>
        public static AudioClip GenerateHiss(int sampleRate = 44100, float duration = 1f)
        {
            int sc = Mathf.Max(256, Mathf.RoundToInt(sampleRate * duration));
            int fade = Mathf.Min(2048, sc / 2);
            int total = sc + fade;

            var buf = new float[total];
            var rng = new System.Random(5150);

            // Filtro de variable de estado: f fija la frecuencia central, q el ancho de banda.
            double f = 2.0 * System.Math.Sin(System.Math.PI * 3500.0 / sampleRate);
            const double q = 0.6;                    // boquilla, no silbato
            double low = 0.0, band = 0.0;
            float maxAbs = 0f;

            // Y un pasa-bajo de una polo a 7 kHz por encima de todo. NO sobra: sin él el ruido
            // cruzaba el cero 21.900 veces por segundo (contenido dominante cerca de 11 kHz), que
            // es siseo de cinta, no bote. Con él baja a ~11.600, que es un chorro con cuerpo.
            double lpCoeff = System.Math.Exp(-2.0 * System.Math.PI * 7000.0 / sampleRate);
            double lpState = 0.0;

            for (int i = 0; i < total; i++)
            {
                double white = rng.NextDouble() * 2.0 - 1.0;

                double high = white - low - q * band;
                band += f * high;
                low += f * band;

                // Banda (cuerpo del chorro) + una pizca de agudo sin filtrar (el aire suelto).
                double s = band + 0.05 * high;

                lpState = (1.0 - lpCoeff) * s + lpCoeff * lpState;
                s = lpState;

                // Respiración lenta: la presión del bote no es constante.
                double t = (double)i / sampleRate;
                s *= 0.85 + 0.15 * System.Math.Sin(2.0 * System.Math.PI * 6.3 * t);

                buf[i] = (float)s;
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > maxAbs) maxAbs = a;
            }

            float norm = maxAbs > 1e-6f ? 0.8f / maxAbs : 1f;
            for (int i = 0; i < total; i++) buf[i] *= norm;

            for (int i = 0; i < fade; i++)
            {
                float w = (float)i / fade;
                buf[i] = buf[i] * w + buf[sc + i] * (1f - w);
            }

            var data = new float[sc];
            System.Array.Copy(buf, data, sc);

            var clip = AudioClip.Create("SprayHiss", sc, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// El cascabel: cinco golpes secos de la bolita contra la lata. Cada golpe es un impulso
        /// de ruido que decae en ~18 ms más dos parciales metálicos, que es lo que separa "canica
        /// en un bote" de "chasquido".
        /// </summary>
        public static AudioClip GenerateRattle(int sampleRate = 44100, float duration = 0.42f)
        {
            int sc = Mathf.Max(256, Mathf.RoundToInt(sampleRate * duration));
            var buf = new float[sc];
            var rng = new System.Random(1971);

            const int hits = 5;
            const double TwoPi = 2.0 * System.Math.PI;
            float maxAbs = 0f;

            for (int h = 0; h < hits; h++)
            {
                // Repartidos con desorden: una bolita no golpea a compás.
                double at = (h + rng.NextDouble() * 0.7) / hits * duration * 0.9;
                int start = Mathf.Clamp((int)(at * sampleRate), 0, sc - 1);
                double gain = 0.55 + rng.NextDouble() * 0.45;
                double ring1 = 1700.0 + rng.NextDouble() * 500.0;
                double ring2 = ring1 * 1.6;

                for (int i = start; i < sc; i++)
                {
                    double dt = (double)(i - start) / sampleRate;
                    double env = System.Math.Exp(-dt / 0.018);
                    if (env < 1e-4) break;

                    double noise = (rng.NextDouble() * 2.0 - 1.0) * 0.6;
                    double metal = 0.5 * System.Math.Sin(TwoPi * ring1 * dt)
                                 + 0.3 * System.Math.Sin(TwoPi * ring2 * dt);
                    buf[i] += (float)(gain * env * (noise + metal));
                }
            }

            for (int i = 0; i < sc; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > maxAbs) maxAbs = a;
            }
            float norm = maxAbs > 1e-6f ? 0.8f / maxAbs : 1f;
            for (int i = 0; i < sc; i++) buf[i] *= norm;

            var clip = AudioClip.Create("SprayRattle", sc, 1, sampleRate, false);
            clip.SetData(buf, 0);
            return clip;
        }
    }
}
