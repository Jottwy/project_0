using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El aviso de entrada. Poca lógica, pero la que hay es justo la que se equivoca en silencio:
    /// anunciar al anfitrión como si se hubiera unido, o pintar un nombre vacío.
    /// </summary>
    [TestFixture]
    public class PlayerJoinMessagesTests
    {
        // `Assert.AreEqual` y no `Is.EqualTo`: el shim del arnés headless sólo tiene la forma
        // numérica de `Is.EqualTo`, y el arnés es donde esto se ejecuta sin editor.
        [Test]
        public void Un_companero_se_anuncia_con_su_nombre()
        {
            Assert.AreEqual("Joel se ha unido a la partida", PlayerJoinMessages.Joined("Joel"));
        }

        /// En un joiner el evento también llega al registrar al anfitrión. Ése se calla.
        [Test]
        public void El_anfitrion_no_se_anuncia_y_los_demas_si()
        {
            Assert.IsFalse(PlayerJoinMessages.ShouldAnnounce(isHost: true));
            Assert.IsTrue(PlayerJoinMessages.ShouldAnnounce(isHost: false));
        }

        [Test]
        public void Un_nombre_vacio_o_en_blanco_no_deja_el_cartel_roto()
        {
            Assert.AreEqual(PlayerJoinMessages.Unknown, PlayerJoinMessages.Clean(null));
            Assert.AreEqual(PlayerJoinMessages.Unknown, PlayerJoinMessages.Clean(""));
            Assert.AreEqual(PlayerJoinMessages.Unknown, PlayerJoinMessages.Clean("   "));
        }

        [Test]
        public void Un_nombre_se_recorta_y_se_limpia_de_espacios()
        {
            Assert.AreEqual("Joel", PlayerJoinMessages.Clean("  Joel  "));

            string tooLong = new string('x', PlayerJoinMessages.MaxNameLength + 10);
            string cleaned = PlayerJoinMessages.Clean(tooLong);
            Assert.That(cleaned.Length, Is.EqualTo(PlayerJoinMessages.MaxNameLength));
        }
    }
}
