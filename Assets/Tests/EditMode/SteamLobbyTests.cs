using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Spike Steam: cubre las DOS funciones puras del camino Steam — el parseo de la
    /// metadata del lobby (único punto donde datos de otra máquina entran al camino de
    /// conexión) y el saneado del nombre de persona antes de viajar como NET_NAME.
    ///
    /// El resto de SteamLobbyManager (Init, lobby, overlay, auto-join) NO es alcanzable
    /// headless: exige cliente de Steam vivo. Se valida en el playtest a dos instancias.
    /// </summary>
    public sealed class SteamLobbyTests
    {
        // ─── TryParseConnectData ───

        [Test]
        public void ParsesValidLoopbackConnectData()
        {
            Assert.IsTrue(SteamLobbyManager.TryParseConnectData("127.0.0.1", "7778", out string ip, out int port));
            Assert.AreEqual("127.0.0.1", ip);
            Assert.AreEqual(7778, port);
        }

        [Test]
        public void TrimsSurroundingWhitespaceInIp()
        {
            Assert.IsTrue(SteamLobbyManager.TryParseConnectData("  192.168.1.20  ", "7778", out string ip, out _));
            Assert.AreEqual("192.168.1.20", ip);
        }

        [Test]
        public void RejectsMissingIp()
        {
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("", "7778", out _, out _));
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData(null, "7778", out _, out _));
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("   ", "7778", out _, out _));
        }

        [Test]
        public void RejectsNonNumericPort()
        {
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("127.0.0.1", "", out _, out _));
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("127.0.0.1", null, out _, out _));
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("127.0.0.1", "seven", out _, out _));
        }

        [Test]
        public void RejectsOutOfRangePort()
        {
            // Un lobby de otra máquina (o de una versión distinta) puede traer basura;
            // el camino de conexión no debe recibir jamás un puerto ilegal.
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("127.0.0.1", "0", out _, out _));
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("127.0.0.1", "-1", out _, out _));
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData("127.0.0.1", "65536", out _, out _));
        }

        [Test]
        public void AcceptsPortRangeBoundaries()
        {
            Assert.IsTrue(SteamLobbyManager.TryParseConnectData("127.0.0.1", "1", out _, out int low));
            Assert.AreEqual(1, low);
            Assert.IsTrue(SteamLobbyManager.TryParseConnectData("127.0.0.1", "65535", out _, out int high));
            Assert.AreEqual(65535, high);
        }

        [Test]
        public void FailedParseLeavesOutputsNeutral()
        {
            Assert.IsFalse(SteamLobbyManager.TryParseConnectData(null, null, out string ip, out int port));
            Assert.IsNull(ip);
            Assert.AreEqual(0, port);
        }

        // ─── SanitizePlayerName ───

        [Test]
        public void KeepsPlainPersonaNameUntouched()
        {
            Assert.AreEqual("Joel", SteamLobbyManager.SanitizePlayerName("Joel"));
        }

        [Test]
        public void FallsBackToPlayerOnEmptyPersona()
        {
            Assert.AreEqual("Player", SteamLobbyManager.SanitizePlayerName(null));
            Assert.AreEqual("Player", SteamLobbyManager.SanitizePlayerName(""));
            Assert.AreEqual("Player", SteamLobbyManager.SanitizePlayerName("   "));
        }

        [Test]
        public void StripsControlCharacters()
        {
            // NET_NAME viaja como variable de entorno a un proceso hijo; un salto de línea
            // o un NUL ahí dentro es un valor degenerado.
            Assert.AreEqual("abc", SteamLobbyManager.SanitizePlayerName("a\nb\tc"));
            Assert.AreEqual("ab", SteamLobbyManager.SanitizePlayerName("a\0b"));
        }

        [Test]
        public void CapsLengthAt32()
        {
            string long65 = new string('x', 65);
            Assert.AreEqual(32, SteamLobbyManager.SanitizePlayerName(long65).Length);
        }

        [Test]
        public void PreservesNonAsciiPersona()
        {
            // No se transcodifica: el Unicode pasa tal cual. El nametag TMP puede quedarse
            // sin glifo (deuda cosmética declarada), pero el valor NO se muta aquí.
            Assert.AreEqual("Ñoño", SteamLobbyManager.SanitizePlayerName("Ñoño"));
        }

        [Test]
        public void OnlyControlCharsFallsBackToPlayer()
        {
            // "\n\t\r" corta antes, en el guard IsNullOrWhiteSpace.
            Assert.AreEqual("Player", SteamLobbyManager.SanitizePlayerName("\n\t\r"));
        }

        [Test]
        public void NonWhitespaceControlCharsOnlyFallsBackToPlayer()
        {
            // NUL no es whitespace, así que pasa el guard IsNullOrWhiteSpace y llega al
            // bucle, que lo descarta y deja el builder vacío. Es la única rama que ejercita
            // el fallback final (cleaned.Length == 0) en vez del guard de entrada.
            Assert.AreEqual("Player", SteamLobbyManager.SanitizePlayerName("\0\0\0"));
        }
    }
}
