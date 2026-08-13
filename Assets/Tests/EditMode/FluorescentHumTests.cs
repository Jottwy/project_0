using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Audio;
using NUnit.Framework;
using UnityEngine;

using HumCandidate = BackroomsSurvival.Gameplay.Audio.FluorescentHumDirector.HumCandidate;
using HumSlotState = BackroomsSurvival.Gameplay.Audio.FluorescentHumDirector.HumSlotState;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Tests headless de las DOS piezas puras del zumbido de fluorescente: el reparto de
    /// fuentes con histéresis y la síntesis del clip. Ninguna necesita escena, motor de
    /// audio ni Play — que es exactamente por qué están factorizadas aparte del
    /// MonoBehaviour.
    ///
    /// FUERA DE ALCANCE declarado, y no por pereza: la asignación real de AudioSource, el
    /// fade de reasignación y la retirada de lotes al descargar un chunk son Play-only
    /// (dependen de Update, de un AudioListener vivo y del ChunkStreamer). Lo que sí queda
    /// fijado aquí es la propiedad de la que depende TODA la ausencia de fugas: una lámpara
    /// que desaparece de la lista de candidatas libera su hueco, y "chunk descargado" y
    /// "jugador fuera de alcance" son el mismo caso desde el selector.
    /// </summary>
    [TestFixture]
    public class FluorescentHumTests
    {
        private const float Hysteresis = 1.5f;

        private static HumSlotState[] FreshSlots(int n)
        {
            var slots = new HumSlotState[n];
            for (int i = 0; i < n; i++) slots[i] = HumSlotState.Free;
            return slots;
        }

        private static List<HumCandidate> Candidates(params (long key, float dist)[] items)
        {
            var list = new List<HumCandidate>(items.Length);
            foreach (var (key, dist) in items)
                list.Add(new HumCandidate { key = key, distance = dist });
            return list;
        }

        private static bool Holds(HumSlotState[] slots, long key)
        {
            foreach (var s in slots)
                if (s.key == key) return true;
            return false;
        }

        // ── Reparto ─────────────────────────────────────────────────────────────

        [Test]
        public void SelectSlots_FillsFreeSlotsWithTheNearestLamps()
        {
            var slots = FreshSlots(3);
            var cands = Candidates((10, 6f), (11, 1f), (12, 4f), (13, 2f), (14, 9f));

            FluorescentHumDirector.SelectSlots(cands, slots, Hysteresis);

            Assert.IsTrue(Holds(slots, 11), "la más cercana (1 m) debe sonar");
            Assert.IsTrue(Holds(slots, 13), "la segunda (2 m) debe sonar");
            Assert.IsTrue(Holds(slots, 12), "la tercera (4 m) debe sonar");
            Assert.IsFalse(Holds(slots, 10), "6 m queda fuera del presupuesto de 3");
            Assert.IsFalse(Holds(slots, 14), "9 m queda fuera del presupuesto de 3");
        }

        [Test]
        public void SelectSlots_NeverExceedsTheBudget()
        {
            var slots = FreshSlots(2);
            var cands = Candidates((1, 1f), (2, 2f), (3, 3f), (4, 4f), (5, 5f));

            FluorescentHumDirector.SelectSlots(cands, slots, Hysteresis);

            int used = 0;
            foreach (var s in slots)
                if (s.key != FluorescentHumDirector.NoKey) used++;
            Assert.AreEqual(2, used);
        }

        [Test]
        public void SelectSlots_KeepsTheHolderWhenTheChallengerIsOnlyMarginallyCloser()
        {
            // Este es el test del parpadeo: el jugador está parado en el umbral entre dos
            // paneles y las distancias oscilan por décimas. Sin histéresis la fuente
            // saltaría de una lámpara a otra cada 0,25 s.
            var slots = FreshSlots(1);
            FluorescentHumDirector.SelectSlots(Candidates((100, 3.0f)), slots, Hysteresis);
            Assert.IsTrue(Holds(slots, 100));

            // 2.0 m contra 3.0 m: mejora 1 m, por debajo del margen de 1,5 m.
            FluorescentHumDirector.SelectSlots(
                Candidates((100, 3.0f), (200, 2.0f)), slots, Hysteresis);

            Assert.IsTrue(Holds(slots, 100), "el titular conserva el hueco dentro del margen");
            Assert.IsFalse(Holds(slots, 200));
        }

        [Test]
        public void SelectSlots_StealsWhenTheChallengerClearsTheHysteresisMargin()
        {
            var slots = FreshSlots(1);
            FluorescentHumDirector.SelectSlots(Candidates((100, 5.0f)), slots, Hysteresis);
            Assert.IsTrue(Holds(slots, 100));

            // 1.0 m contra 5.0 m: mejora 4 m, muy por encima del margen.
            FluorescentHumDirector.SelectSlots(
                Candidates((100, 5.0f), (200, 1.0f)), slots, Hysteresis);

            Assert.IsTrue(Holds(slots, 200), "una lámpara claramente más cercana sí roba");
            Assert.IsFalse(Holds(slots, 100));
        }

        [Test]
        public void SelectSlots_ReleasesAHolderThatIsNoLongerACandidate()
        {
            // La propiedad de la que cuelga la ausencia de fugas: el chunk se descarga (o el
            // jugador se aleja) ⇒ la lámpara deja de aparecer ⇒ su fuente vuelve al pool.
            var slots = FreshSlots(2);
            FluorescentHumDirector.SelectSlots(Candidates((1, 1f), (2, 2f)), slots, Hysteresis);
            Assert.IsTrue(Holds(slots, 1));
            Assert.IsTrue(Holds(slots, 2));

            FluorescentHumDirector.SelectSlots(Candidates((2, 2f)), slots, Hysteresis);

            Assert.IsFalse(Holds(slots, 1), "la lámpara desaparecida suelta su hueco");
            Assert.IsTrue(Holds(slots, 2));
        }

        [Test]
        public void SelectSlots_WithNoCandidatesFreesEverySlot()
        {
            var slots = FreshSlots(4);
            FluorescentHumDirector.SelectSlots(
                Candidates((1, 1f), (2, 2f), (3, 3f), (4, 4f)), slots, Hysteresis);

            FluorescentHumDirector.SelectSlots(new List<HumCandidate>(), slots, Hysteresis);

            foreach (var s in slots)
                Assert.AreEqual(FluorescentHumDirector.NoKey, s.key,
                    "sin candidatas no puede quedar ninguna fuente sonando");
        }

        [Test]
        public void SelectSlots_IsStableAcrossRepeatedPassesWithUnchangedInput()
        {
            var slots = FreshSlots(3);
            var first = Candidates((1, 1f), (2, 2f), (3, 3f), (4, 3.6f));
            FluorescentHumDirector.SelectSlots(first, slots, Hysteresis);

            var before = new long[slots.Length];
            for (int i = 0; i < slots.Length; i++) before[i] = slots[i].key;

            for (int pass = 0; pass < 5; pass++)
                FluorescentHumDirector.SelectSlots(
                    Candidates((1, 1f), (2, 2f), (3, 3f), (4, 3.6f)), slots, Hysteresis);

            for (int i = 0; i < slots.Length; i++)
                Assert.AreEqual(before[i], slots[i].key,
                    "una entrada idéntica no puede mover las asignaciones");
        }

        // ── Clip ────────────────────────────────────────────────────────────────

        [Test]
        public void RenderHumSamples_ProducesOneMonoChannelOfTheRequestedLength()
        {
            var data = FluorescentHumDirector.RenderHumSamples(
                FluorescentHumDirector.ClipSampleRate, FluorescentHumDirector.ClipSeconds);

            // Un solo canal por construcción: el buffer es exactamente rate × segundos, sin
            // entrelazado. Es la mitad del contrato de espacialización — un clip estéreo NO
            // se espacializa en Unity, y ese es el error clásico de este sistema.
            Assert.AreEqual(
                FluorescentHumDirector.ClipSampleRate * FluorescentHumDirector.ClipSeconds,
                data.Length);
        }

        [Test]
        public void RenderHumSamples_LoopsWithoutAClick()
        {
            var data = FluorescentHumDirector.RenderHumSamples(
                FluorescentHumDirector.ClipSampleRate, FluorescentHumDirector.ClipSeconds);

            // "Sin click" no es una tolerancia inventada: el salto en la costura no puede ser
            // mayor que el mayor salto que ya ocurre DENTRO del buffer. Si lo fuera, el bucle
            // introduciría una discontinuidad que la señal no tiene por sí misma — que es
            // literalmente lo que se oye como chasquido.
            float maxInner = 0f;
            for (int i = 0; i < data.Length - 1; i++)
            {
                float d = Mathf.Abs(data[i + 1] - data[i]);
                if (d > maxInner) maxInner = d;
            }
            float wrap = Mathf.Abs(data[0] - data[data.Length - 1]);

            Assert.LessOrEqual(wrap, maxInner,
                $"costura del loop ({wrap}) por encima del mayor salto interno ({maxInner})");
        }

        [Test]
        public void RenderHumSamples_IsNormalisedAndDeterministic()
        {
            var a = FluorescentHumDirector.RenderHumSamples(44100, 1);
            var b = FluorescentHumDirector.RenderHumSamples(44100, 1);

            float peak = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i], b[i], 0f, $"la síntesis debe ser determinista (muestra {i})");
                float m = Mathf.Abs(a[i]);
                if (m > peak) peak = m;
            }
            Assert.AreEqual(0.85f, peak, 0.001f, "normalizado a ±0,85");
        }

        // ── Pitch ───────────────────────────────────────────────────────────────

        [Test]
        public void PitchFor_IsDeterministicPerGlobalTile()
        {
            Assert.AreEqual(FluorescentHumDirector.PitchFor(37, -12),
                            FluorescentHumDirector.PitchFor(37, -12), 0f);

            // Que dos tiles CONCRETOS difieran es una lotería de hash; lo que importa es que
            // el campo no sea plano — si todas las lámparas comparten pitch el resultado es
            // un zumbido uniforme y artificial, que es justo lo que el detune existe para
            // evitar.
            var seen = new HashSet<float>();
            for (int x = 0; x < 12; x++)
            for (int z = 0; z < 12; z++)
                seen.Add(FluorescentHumDirector.PitchFor(x, z));
            Assert.Greater(seen.Count, 100, "144 tiles deben dar pitches casi todos distintos");
        }

        [Test]
        public void PitchFor_StaysInsideTheDeclaredSpread()
        {
            // Fuera de ±2,5 % el detune deja de leerse como "muchas fuentes reales" y empieza
            // a sonar a lámpara averiada.
            for (int x = -40; x <= 40; x += 7)
            for (int z = -40; z <= 40; z += 7)
            {
                float p = FluorescentHumDirector.PitchFor(x, z);
                Assert.GreaterOrEqual(p, 1f - FluorescentHumDirector.PitchSpread - 1e-5f);
                Assert.LessOrEqual(p, 1f + FluorescentHumDirector.PitchSpread + 1e-5f);
            }
        }
    }
}
