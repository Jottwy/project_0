using BackroomsSurvival.Gameplay.Audio;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El sonido del bote. Lo que se prueba es el GENERADOR, no que suene bonito: un siseo mal
    /// sintetizado no da error, da un zumbido grave o estática de televisión, y eso no se ve
    /// compilando. Las tres propiedades que sí se pueden afirmar sin oídos son que el ruido cae
    /// donde debe en frecuencia, que no lleva continua (una continua CHASQUEA al arrancar y al
    /// parar) y que el mismo bote suena igual en toda máquina.
    /// </summary>
    [TestFixture]
    public class SpraySfxTests
    {
        private const int SampleRate = 44100;

        private static float[] Samples(AudioClip clip)
        {
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            return data;
        }

        [Test]
        public void TheHissIsOneSecondOfMonoAudioWithoutClipping()
        {
            var clip = SpraySfx.GenerateHiss(SampleRate, 1f);
            var data = Samples(clip);

            Assert.AreEqual(1, clip.channels);
            Assert.AreEqual(SampleRate, clip.samples);

            float peak = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                Assert.IsFalse(float.IsNaN(data[i]), $"NaN en la muestra {i}");
                float a = Mathf.Abs(data[i]);
                if (a > peak) peak = a;
            }
            Assert.AreEqual(0.8f, peak, 0.02f, "normalizado a ±0,8, ni saturado ni inaudible");
        }

        /// <summary>
        /// Una continua (media distinta de cero) es un salto de tensión al empezar y al terminar:
        /// se oye como un golpe seco, justo lo que la rampa de volumen intenta evitar.
        /// </summary>
        [Test]
        public void TheHissCarriesNoDcOffset()
        {
            var data = Samples(SpraySfx.GenerateHiss(SampleRate, 1f));

            double sum = 0.0;
            for (int i = 0; i < data.Length; i++) sum += data[i];

            Assert.AreEqual(0.0, sum / data.Length, 0.01, "el pasa-banda no debe dejar continua");
        }

        /// <summary>
        /// LA PRUEBA DE QUE ES UN SISEO Y NO UN ZUMBIDO. El ruido pasa por un pasa-banda centrado
        /// en 3,5 kHz, y el ritmo de cruces por cero de un ruido de banda estrecha ronda el doble
        /// de su centro. Si alguien toca el filtro y lo deja en 200 Hz, el clip sigue existiendo,
        /// sigue sin dar error — y este test se pone rojo.
        /// </summary>
        [Test]
        public void TheHissSitsInTheNozzleBand()
        {
            var data = Samples(SpraySfx.GenerateHiss(SampleRate, 1f));

            int crossings = 0;
            for (int i = 1; i < data.Length; i++)
                if ((data[i - 1] < 0f) != (data[i] < 0f)) crossings++;

            // 1 s de clip ⇒ cruces por segundo. 3,5 kHz ⇒ ~7000, con margen ancho para no atarse
            // a la implementación exacta del filtro.
            Assert.Greater(crossings, 3500, "demasiado grave para ser una boquilla");
            Assert.Less(crossings, 16000, "demasiado agudo, eso ya es siseo de cinta");
        }

        /// <summary>Semilla fija: el bote suena igual en la máquina de cada jugador.</summary>
        [Test]
        public void TheHissIsDeterministic()
        {
            var a = Samples(SpraySfx.GenerateHiss(SampleRate, 0.25f));
            var b = Samples(SpraySfx.GenerateHiss(SampleRate, 0.25f));

            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i += 97)
                Assert.AreEqual(a[i], b[i], 1e-6f, $"divergen en la muestra {i}");
        }

        /// <summary>
        /// El cascabel tiene que APAGARSE: son golpes con cola corta, no un ruido sostenido. Si
        /// alguien toca la envolvente y la deja plana, el bote sonaría a lija cada vez que se saca.
        /// </summary>
        [Test]
        public void TheRattleDecaysToSilence()
        {
            var clip = SpraySfx.GenerateRattle(SampleRate);
            var data = Samples(clip);

            int tail = data.Length / 10;
            double head = 0.0, end = 0.0;
            for (int i = 0; i < data.Length - tail; i++) head += data[i] * data[i];
            for (int i = data.Length - tail; i < data.Length; i++) end += data[i] * data[i];

            head = System.Math.Sqrt(head / (data.Length - tail));
            end = System.Math.Sqrt(end / tail);

            Assert.Greater(head, 0.01, "el cascabel tiene que sonar a algo");
            Assert.Less(end, head * 0.5, "la cola tiene que estar apagándose");
        }

        /// <summary>
        /// El cascabel va por un AudioSource PROPIO. No es cosmético: el emisor del siseo vive con
        /// el volumen a cero mientras no se pinta y <c>PlayOneShot</c> multiplica por el volumen de
        /// la fuente, así que compartirlo dejaba el golpe mudo justo al sacar el bote.
        /// </summary>
        [Test]
        public void TheEmitterKeepsTheLoopAndTheOneShotApart()
        {
            var sfx = SpraySfx.Create("SpraySfx_Test", spatial: true);
            try
            {
                var sources = sfx.GetComponents<AudioSource>();
                Assert.AreEqual(2, sources.Length, "uno para el bucle, otro para los golpes");

                var loop = System.Array.Find(sources, s => s.loop);
                var oneShot = System.Array.Find(sources, s => !s.loop);

                Assert.IsNotNull(loop);
                Assert.IsNotNull(oneShot);
                Assert.AreEqual(0f, loop.volume, 1e-4f, "el bucle nace mudo y sube por rampa");
                Assert.AreEqual(1f, oneShot.volume, 1e-4f, "el golpe suena a su volumen");
                Assert.IsFalse(loop.playOnAwake);
                Assert.IsFalse(oneShot.playOnAwake);
            }
            finally
            {
                Object.DestroyImmediate(sfx.gameObject);
            }
        }
    }
}
