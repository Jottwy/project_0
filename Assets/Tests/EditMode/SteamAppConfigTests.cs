using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La resolución del App ID. Existe porque el 480 estaba hardcodeado dentro del código de red
    /// y publicar exigía editarlo — y porque el escalón de en medio, `steam_appid.txt`, tiene que
    /// coincidir con lo que se le pasa a `SteamClient.Init` o Steam rechaza la inicialización sin
    /// dar un motivo legible.
    ///
    /// Se prueba el núcleo puro (<see cref="SteamAppConfig.Resolve"/>), no el que toca disco: lo
    /// que puede equivocarse es la PRECEDENCIA y el saneo del valor, no `File.ReadAllText`.
    /// </summary>
    public sealed class SteamAppConfigTests
    {
        private const uint Fallback = SteamAppConfig.ProductionAppId;

        // ─── Precedencia ───

        [Test]
        public void EnvironmentWinsOverFileAndDefault()
        {
            uint id = SteamAppConfig.Resolve("480", null, "5072740", Fallback, out var source);

            Assert.AreEqual(SteamAppConfig.DevAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.EnvironmentVariable, source);
        }

        [Test]
        public void FileWinsOverDefaultWhenThereIsNoEnvironment()
        {
            uint id = SteamAppConfig.Resolve(null, null, "480", Fallback, out var source);

            Assert.AreEqual(SteamAppConfig.DevAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.AppIdFile, source);
        }

        [Test]
        public void FallsBackToTheCompiledConstantWhenNeitherIsSet()
        {
            uint id = SteamAppConfig.Resolve(null, null, null, Fallback, out var source);

            Assert.AreEqual(Fallback, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.Default, source);
        }

        /// Un valor degenerado en el escalón de arriba tiene que CAER al siguiente, no ganar. Si
        /// un `BS_STEAM_APPID=` vacío se colara como App ID, el juego arrancaría sin Steam y el
        /// fichero correcto de al lado no se leería nunca.
        [Test]
        public void AnUnreadableEnvironmentValueFallsThroughToTheFile()
        {
            uint id = SteamAppConfig.Resolve("   ", null, "480", Fallback, out var source);

            Assert.AreEqual(SteamAppConfig.DevAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.AppIdFile, source);
        }

        [Test]
        public void AnUnreadableFileFallsThroughToTheDefault()
        {
            uint id = SteamAppConfig.Resolve(null, null, "no soy un numero", Fallback, out var source);

            Assert.AreEqual(Fallback, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.Default, source);
        }

        // ─── Saneo ───

        /// `steam_appid.txt` lo escribe cualquiera con cualquier editor: salto de línea final,
        /// espacios y BOM son lo normal, no la excepción.
        [TestCase("480\n")]
        [TestCase("480\r\n")]
        [TestCase("  480  ")]
        [TestCase("﻿480")]
        [TestCase("﻿480\r\n")]
        public void RealWorldFileContentsParse(string raw)
        {
            Assert.IsTrue(SteamAppConfig.TryParse(raw, out uint id));
            Assert.AreEqual(480u, id);
        }

        /// Steam trata el 0 como "sin id", así que aceptarlo sería propagar un valor que garantiza
        /// un Init fallido en vez de caer al escalón que sí funciona.
        [TestCase("0")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("-480")]
        [TestCase("480.0")]
        [TestCase("0x1E0")]
        [TestCase("480 5072740")]
        public void DegenerateValuesAreRejected(string raw)
        {
            Assert.IsFalse(SteamAppConfig.TryParse(raw, out uint id));
            Assert.AreEqual(0u, id);
        }

        // ─── Contrato ───

        /// El número real del juego en Steam. Está aquí para que cambiarlo sea un acto deliberado
        /// con un test rojo delante, no una edición silenciosa.
        [Test]
        public void ProductionAppIdIsTheRealOne()
        {
            Assert.AreEqual(5072740u, SteamAppConfig.ProductionAppId);
            Assert.AreEqual(480u, SteamAppConfig.DevAppId);
            Assert.AreNotEqual(SteamAppConfig.DevAppId, SteamAppConfig.ProductionAppId);
        }

        /// En el Editor el default TIENE que ser 480: la cuenta de desarrollo puede no tener aún
        /// acceso al App ID real, y un Editor que no inicializa Steam impide probar el navegador.
        [Test]
        public void TheEditorDefaultIsSpacewar()
        {
            Assert.AreEqual(SteamAppConfig.DevAppId, SteamAppConfig.DefaultAppId);
        }

        /// El nombre lo fija Valve. Si alguien lo "mejora", Steam deja de encontrar el fichero y
        /// el player deja de inicializar fuera del cliente.
        [Test]
        public void TheAppIdFileKeepsValvesName()
        {
            Assert.AreEqual("steam_appid.txt", SteamAppConfig.AppIdFileName);
        }

        // ─── El escalón que pone Steam al lanzar ───

        /// **El caso que desbloquea SteamPipe.** Steam lanza el playtest como app 5200320; con este
        /// escalón el MISMO binario se identifica como el playtest sin recompilar y sin que 5200320
        /// aparezca en ninguna constante de resolución.
        [Test]
        public void TheAppIdSteamLaunchedUsWithWinsOverTheFileAndTheDefault()
        {
            uint id = SteamAppConfig.Resolve(null, "5200320", "480", Fallback, out var source);

            Assert.AreEqual(SteamAppConfig.PlaytestAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.SteamLaunch, source);
        }

        /// El mismo binario, lanzado como el juego en vez de como el playtest.
        [Test]
        public void TheSameBinaryAlsoAnswersAsTheGameApp()
        {
            uint id = SteamAppConfig.Resolve(null, "5072740", null, SteamAppConfig.DevAppId, out var source);

            Assert.AreEqual(SteamAppConfig.ProductionAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.SteamLaunch, source);
        }

        /// El override manual sigue por ENCIMA: QA tiene que poder forzar 480 aunque Steam haya
        /// lanzado el juego como otra cosa. Si Steam ganara, `BS_STEAM_APPID` dejaría de servir
        /// justo en el único sitio donde hay un cliente de Steam delante.
        [Test]
        public void TheManualOverrideStillBeatsWhatSteamSays()
        {
            uint id = SteamAppConfig.Resolve("480", "5200320", null, Fallback, out var source);

            Assert.AreEqual(SteamAppConfig.DevAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.EnvironmentVariable, source);
        }

        /// Lanzado FUERA de Steam no hay variable, y entonces manda el fichero. Es el caso de un
        /// doble clic al .exe, que es como se ha probado todo esta noche.
        [Test]
        public void WithoutSteamLaunchingUsTheFileStillDecides()
        {
            uint id = SteamAppConfig.Resolve(null, null, "480", Fallback, out var source);

            Assert.AreEqual(SteamAppConfig.DevAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.AppIdFile, source);
        }

        /// Un valor degenerado en ese escalón cae al siguiente como todos los demás.
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("0")]
        [TestCase("no soy un numero")]
        public void ADegenerateSteamLaunchValueFallsThrough(string raw)
        {
            uint id = SteamAppConfig.Resolve(null, raw, "480", Fallback, out var source);

            Assert.AreEqual(SteamAppConfig.DevAppId, id);
            Assert.AreEqual(SteamAppConfig.AppIdSource.AppIdFile, source);
        }

        /// El nombre lo fija Valve, igual que el del fichero.
        [Test]
        public void TheSteamLaunchVariableKeepsValvesName()
        {
            Assert.AreEqual("SteamAppId", SteamAppConfig.SteamLaunchAppIdEnvVar);
        }

        /// El id del playtest está para poner nombre al número en el log, y para que cambiarlo sea
        /// deliberado. **No participa en la resolución**: quien decide es Steam.
        [Test]
        public void ThePlaytestAppIdIsTheRealOneAndIsDistinct()
        {
            Assert.AreEqual(5200320u, SteamAppConfig.PlaytestAppId);
            Assert.AreNotEqual(SteamAppConfig.PlaytestAppId, SteamAppConfig.ProductionAppId);
            Assert.AreNotEqual(SteamAppConfig.PlaytestAppId, SteamAppConfig.DevAppId);
            StringAssert.Contains("playtest", SteamAppConfig.Describe(SteamAppConfig.PlaytestAppId));
        }

        /// La ruta se deriva de `Application.dataPath`, que en el Editor apunta a `Assets/`. El
        /// fichero de desarrollo vive en la raíz del proyecto, un nivel por encima.
        [Test]
        public void TheAppIdFileSitsBesideTheProjectRootInTheEditor()
        {
            string path = SteamAppConfig.AppIdFilePath;

            Assert.IsNotNull(path);
            StringAssert.EndsWith(SteamAppConfig.AppIdFileName, path);
            Assert.IsTrue(
                System.IO.File.Exists(path),
                $"Falta {path}. Sin él el Editor cae a la constante compilada, que hoy coincide, " +
                "pero el día que el default sea el de producción el Editor dejaría de inicializar " +
                "Steam sin que nadie tocara nada.");
        }
    }
}
