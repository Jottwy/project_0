using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 fase A — que los demás vean y oigan pintar mientras se pinta.
    ///
    /// Lo que viaja es UN BIT en los bits libres de `buttons` que ADR-044 dejó reservados, así que
    /// no hay campo nuevo, ni bump de esquema, ni una línea de Rust: el backend relaya `buttons`
    /// sin mirarlo. Lo único que puede romperse en silencio es la ASIGNACIÓN del bit — dos features
    /// pidiendo el mismo número se manifiestan como "el chorro sale cuando el otro se asoma", no
    /// como un error.
    /// </summary>
    [TestFixture]
    public class SprayRelayTests
    {
        [Test]
        public void SprayingRidesItsOwnBit()
        {
            Assert.AreEqual(1 << 4, RemoteButtons.Spraying,
                "si esto cambia, cambia el significado de un bit que ya viaja entre versiones");
        }

        /// <summary>
        /// Ningún bit puede valer por dos. Se comprueban TODOS juntos y no solo el nuevo: el modo
        /// de fallo real es que alguien añada el sexto estado sostenido copiando la línea de arriba
        /// y olvide subir el desplazamiento.
        /// </summary>
        [Test]
        public void NoTwoSustainedStatesShareABit()
        {
            int[] bits =
            {
                RemoteButtons.Aiming, RemoteButtons.Reloading,
                RemoteButtons.LeanLeft, RemoteButtons.LeanRight,
                RemoteButtons.Spraying,
            };

            int seen = 0;
            foreach (int bit in bits)
            {
                Assert.AreNotEqual(0, bit, "un estado sostenido con bit 0 no viaja");
                Assert.AreEqual(0, bit & (bit - 1), $"{bit} no es una potencia de dos");
                Assert.AreEqual(0, seen & bit, $"el bit {bit} ya estaba cogido");
                seen |= bit;
            }
        }

        [Test]
        public void HasReadsTheBitAndIgnoresTheOthers()
        {
            int buttons = RemoteButtons.Spraying | RemoteButtons.LeanRight;

            Assert.IsTrue(RemoteButtons.Has(buttons, RemoteButtons.Spraying));
            Assert.IsTrue(RemoteButtons.Has(buttons, RemoteButtons.LeanRight));
            Assert.IsFalse(RemoteButtons.Has(buttons, RemoteButtons.Aiming));
            Assert.IsFalse(RemoteButtons.Has(buttons, RemoteButtons.Reloading));
        }

        /// <summary>
        /// El transmisor de poses pregunta esto CADA envío, exista o no un bote en la partida — y
        /// el `SprayPainter` se crea perezosamente, así que la mayor parte del tiempo no hay
        /// ninguno. Sin instancia tiene que contestar "no", nunca reventar.
        /// </summary>
        [Test]
        public void WithoutAPainterNobodyIsSpraying()
        {
            Assert.IsFalse(SprayPainter.IsSprayingNow);
        }
    }
}
