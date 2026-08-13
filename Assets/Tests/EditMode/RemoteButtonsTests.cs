using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El bitfield `buttons` del relay de poses (ADR-044) más los dos bits de lean (Q/E) que se le
    /// añaden encima. No hay mucha lógica que probar y por eso mismo hay test: lo que falla en un
    /// bitfield append-only no es el álgebra, es que alguien REUSE un bit ya asignado y el avatar de
    /// un peer con build vieja se ponga a hacer otra cosa. Estos asserts fijan los valores por
    /// escrito para que ese cambio salga en rojo y no en juego.
    ///
    /// El hook que los pinta (ProxyLeanHook) no se puede probar desde aquí: vive en Assembly-CSharp
    /// y un asmdef no puede referenciarla. Se valida en play-test.
    /// </summary>
    [TestFixture]
    public class RemoteButtonsTests
    {
        [Test]
        public void BitValuesAreFrozen()
        {
            Assert.AreEqual(1, RemoteButtons.Aiming);
            Assert.AreEqual(2, RemoteButtons.Reloading);
            Assert.AreEqual(4, RemoteButtons.LeanLeft);
            Assert.AreEqual(8, RemoteButtons.LeanRight);
        }

        [Test]
        public void EveryBitFitsInTheWireField()
        {
            // El campo es u16 en el wire; un bit fuera de rango se perdería en silencio.
            int all = RemoteButtons.Aiming | RemoteButtons.Reloading
                    | RemoteButtons.LeanLeft | RemoteButtons.LeanRight;

            Assert.AreEqual(all, (ushort)all);
        }

        /// <summary>
        /// El caso real: inclinarse mientras se apunta. Los estados son ortogonales, así que
        /// decodificar uno no puede depender de los otros.
        /// </summary>
        [Test]
        public void LeanDecodesIndependentlyOfWeaponState()
        {
            int buttons = RemoteButtons.LeanRight | RemoteButtons.Aiming;

            Assert.IsTrue(RemoteButtons.Has(buttons, RemoteButtons.LeanRight));
            Assert.IsTrue(RemoteButtons.Has(buttons, RemoteButtons.Aiming));
            Assert.IsFalse(RemoteButtons.Has(buttons, RemoteButtons.LeanLeft));
            Assert.IsFalse(RemoteButtons.Has(buttons, RemoteButtons.Reloading));
        }

        /// <summary>
        /// Un proxy recién sacado del pool llega con buttons 0, y ese es el estado honesto:
        /// centrado, sin apuntar y sin recargar. Sin centinela y sin necesitarlo.
        /// </summary>
        [Test]
        public void ZeroMeansNeitherLeanNorWeaponState()
        {
            Assert.IsFalse(RemoteButtons.Has(0, RemoteButtons.LeanLeft));
            Assert.IsFalse(RemoteButtons.Has(0, RemoteButtons.LeanRight));
            Assert.IsFalse(RemoteButtons.Has(0, RemoteButtons.Aiming));
            Assert.IsFalse(RemoteButtons.Has(0, RemoteButtons.Reloading));
        }
    }
}
