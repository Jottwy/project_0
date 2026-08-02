using System;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-046 — puerta de ruido y nivel automático.
    ///
    /// El procesado de audio NO falla con una excepción: falla sonando mal, y eso solo se nota
    /// oyéndolo. Por eso cada propiedad se afirma explícitamente contra señales sintéticas. Estos
    /// mismos casos se ejecutaron además en arnés headless (13/13) porque el editor tenía el
    /// proyecto bloqueado — y **destaparon un bug real**: el AGC seguía adaptando durante el
    /// `hold` de la puerta, o sea justo cuando ya habías dejado de hablar, y 10 s de ruido de sala
    /// le subían la ganancia de 0,57x a 4,37x.
    /// </summary>
    [TestFixture]
    public class VoiceDynamicsTests
    {
        private const float Dt = 0.02f;      // trama de 20 ms
        private const int N = 960;           // 20 ms a 48 kHz
        private const float Threshold = 0.02f;
        private const float Hold = 0.3f;

        private static float[] Tone(float amp)
        {
            var b = new float[N];
            for (int i = 0; i < N; i++) b[i] = amp * (float)Math.Sin(2 * Math.PI * 200 * i / 48000.0);
            return b;
        }

        private static float Rms(float[] b)
        {
            double s = 0;
            foreach (var v in b) s += (double)v * v;
            return (float)Math.Sqrt(s / b.Length);
        }

        private static float Peak(float[] b)
        {
            float p = 0;
            foreach (var v in b) { float a = Math.Abs(v); if (a > p) p = a; }
            return p;
        }

        private static float[] Run(VoiceDynamics d, float amp, int frames)
        {
            float[] last = null;
            for (int f = 0; f < frames; f++)
            {
                last = Tone(amp);
                d.Process(last, N, Rms(last), Dt, Threshold, Hold);
            }
            return last;
        }

        [Test]
        public void AutoGainRaisesAQuietVoiceTowardsTheTarget()
        {
            var d = new VoiceDynamics();
            var quiet = Run(d, 0.05f, 300); // floja, pero por ENCIMA del umbral de la puerta
            Assert.Greater(Rms(quiet), VoiceDynamics.TargetRms * 0.7f,
                $"la voz floja debería acercarse al objetivo (ganancia {d.Gain:F1}x)");
        }

        /// <summary>
        /// Corolario que conviene tener fijado: con la puerta ACTIVA, el AGC no rescata una voz
        /// por debajo del umbral, porque para la puerta eso es ruido. Es correcto, y es la razón
        /// de que el umbral sea ajustable en el panel.
        /// </summary>
        [Test]
        public void ASignalBelowTheThresholdIsGatedAndTheAutoGainDoesNotRescueIt()
        {
            var d = new VoiceDynamics();
            var below = Run(d, 0.02f, 100); // RMS 0,014 < umbral 0,02
            Assert.Less(Rms(below), 0.001f);
        }

        [Test]
        public void ALoudVoiceIsBroughtDownAndNeverClips()
        {
            var d = new VoiceDynamics();
            var loud = Run(d, 0.9f, 200);
            Assert.Less(Peak(loud), 1f, "no puede saturar");
            Assert.LessOrEqual(d.Gain, 1.01f, "y la ganancia debe bajar de 1");
        }

        /// <summary>
        /// EL FALLO CLÁSICO DEL AGC, y el que este test existe para impedir: adaptar mientras no
        /// hay voz sube la ganancia hasta el tope y convierte el ruido de sala en un rugido en
        /// cuanto dejas de hablar. La condición correcta es "RMS por encima del umbral de
        /// APERTURA", no "la puerta está abierta" — la puerta sigue abierta durante el hold.
        /// </summary>
        [Test]
        public void TenSecondsOfRoomToneDoNotMoveTheGain()
        {
            var d = new VoiceDynamics();
            Run(d, 0.3f, 50);
            float afterSpeech = d.Gain;
            Run(d, 0.0008f, 500);
            Assert.AreEqual(afterSpeech, d.Gain, 0.05f,
                "el silencio no puede mover la ganancia");
        }

        [Test]
        public void TheGateOpensOnSpeechAndClosesOnSilence()
        {
            var d = new VoiceDynamics();
            Run(d, 0.3f, 10);
            Assert.Greater(d.GateGain, 0.99f, "abierta mientras hay voz");
            Run(d, 0f, 40); // 0,8 s: supera hold + rampa de cierre
            Assert.Less(d.GateGain, 0.01f, "cerrada tras el silencio");
        }

        /// <summary>
        /// Con UN solo umbral, una voz que ronda el límite abre y cierra decenas de veces por
        /// segundo y se oye como un chisporroteo. La histéresis (cierra al 60 % del umbral de
        /// apertura) es lo que lo impide.
        /// </summary>
        [Test]
        public void HysteresisStopsTheGateFromChatteringAtTheThreshold()
        {
            var d = new VoiceDynamics();
            int flips = 0;
            bool wasOpen = false;
            for (int f = 0; f < 200; f++)
            {
                float amp = Threshold * 1.414f * (f % 2 == 0 ? 1.02f : 0.85f);
                var buf = Tone(amp);
                d.Process(buf, N, Rms(buf), Dt, Threshold, Hold);
                bool open = d.GateGain > 0.5f;
                if (open != wasOpen) { flips++; wasOpen = open; }
            }
            Assert.LessOrEqual(flips, 2, $"{flips} conmutaciones en 4 s es chisporroteo");
        }

        /// <summary>
        /// Pasarse de |1| no es un detalle estético: la conversión a PCM hace
        /// <c>(short)(s * 32767)</c> sin comprobación, así que un 1,001 DESBORDA a un valor
        /// negativo — un chasquido de amplitud máxima en mitad de la frase.
        /// </summary>
        [Test]
        public void SoftClipNeverExceedsOneEvenForAbsurdInput()
        {
            for (float v = -50f; v <= 50f; v += 0.25f)
            {
                float o = VoiceDynamics.SoftClip(v);
                Assert.IsFalse(float.IsNaN(o), $"NaN en {v}");
                Assert.LessOrEqual(Math.Abs(o), 1f, $"desbordaría el short en {v}");
            }
            Assert.AreEqual(0.5f, VoiceDynamics.SoftClip(0.5f), 1e-6f,
                "por debajo de la rodilla no debe tocar nada");
        }

        [Test]
        public void WithBothDisabledTheSignalPassesThroughUntouched()
        {
            var d = new VoiceDynamics { GateEnabled = false, AutoGainEnabled = false };
            var src = Tone(0.3f);
            var copy = (float[])src.Clone();
            d.Process(src, N, Rms(src), Dt, Threshold, Hold);
            Assert.AreEqual(copy, src);
        }

        /// <summary>Sin este reset, la ganancia adaptada al micrófono ANTERIOR se aplica a la
        /// primera trama del siguiente, y entre aparatos de sensibilidad muy distinta eso satura.</summary>
        [Test]
        public void ResetReturnsToUnityGain()
        {
            var d = new VoiceDynamics();
            Run(d, 0.02f, 200);
            d.Reset();
            Assert.AreEqual(1f, d.Gain, 1e-6f);
            Assert.AreEqual(1f, d.GateGain, 1e-6f);
        }
    }
}
