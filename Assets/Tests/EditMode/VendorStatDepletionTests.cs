using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Clava las tasas de drenaje de hambre/sed del VENDOR contra las del backend Rust
    /// (`backend/src/player/stats.rs`, `update()`).
    ///
    /// POR QUÉ EXISTE. Hay dos drenajes y son MUTUAMENTE EXCLUYENTES, no simultáneos:
    /// `StatInterpolator` apaga los managers del vendor al conectar el IPC y los reenciende al
    /// perder la conexión. O sea que el valor del vendor manda en tres ventanas reales — entre la
    /// carga de escena y el connect, tras un rig rebuild, y todo el modo desconectado — y las dos
    /// superficies de HUD (reloj de muñeca y `PlayerStatsUI`) leen el campo del manager, no el
    /// snapshot. Si las dos tasas divergen, el jugador ve una barra que no corresponde al servidor.
    /// Ya divergían antes de este test: sed 0.09 en el cliente contra 0.07 en el backend, un 28,6%
    /// más rápido offline, y nadie lo notó.
    ///
    /// POR QUÉ LEE EL YAML Y NO EL COMPONENTE. `_depletionSpeed` es un `private` serializado sin
    /// setter ni propiedad pública dentro de `StatDepletionSettings`, así que leerlo por API
    /// exigiría que esta asamblea referenciara la del vendor y además reflexión sobre un campo
    /// anidado. Leer el texto del prefab es más barato Y prueba mejor lo que hay que proteger: el
    /// riesgo real es que un reimport del `.unitypackage` de PolymindGames pise el fichero y
    /// devuelva los valores de fábrica sin avisar (ya pasó con las escenas demo). Lo que se
    /// defiende es el VALOR SERIALIZADO, que es justo lo que este test mira.
    ///
    /// SI ESTE TEST SE PONE ROJO: no toques el número de aquí. Comprueba primero si el prefab
    /// volvió a los valores de fábrica (0.05 / 0.09) — entonces es un reimport del vendor y hay
    /// que reaplicar el cambio en el prefab. Solo si el balance del backend cambió a propósito se
    /// actualizan las constantes de este fichero, en el mismo commit que `stats.rs`.
    /// </summary>
    [TestFixture]
    public class VendorStatDepletionTests
    {
        /// <summary>Espejo de `self.hunger -= 0.005 * dt` (backend/src/player/stats.rs).</summary>
        private const float BackendHungerDrainPerSecond = 0.005f;
        /// <summary>Espejo de `self.thirst -= 0.007 * dt` (backend/src/player/stats.rs).</summary>
        private const float BackendThirstDrainPerSecond = 0.007f;

        private const string PrefabRelativePath =
            "PolymindGames/STP/Prefabs/Core/STP_Player.prefab";

        private static string PrefabPath =>
            Path.Combine(Application.dataPath, PrefabRelativePath);

        [Test]
        public void HungerManager_DepletionSpeed_MatchesBackend()
        {
            Assert.AreEqual(BackendHungerDrainPerSecond, ReadDepletionSpeed("_hunger"), 1e-6f,
                "el drenaje de hambre del vendor debe coincidir con el del backend");
        }

        [Test]
        public void ThirstManager_DepletionSpeed_MatchesBackend()
        {
            Assert.AreEqual(BackendThirstDrainPerSecond, ReadDepletionSpeed("_thirst"), 1e-6f,
                "el drenaje de sed del vendor debe coincidir con el del backend");
        }

        [Test]
        public void PlayerPrefab_HasExactlyTwoDepletionSpeeds()
        {
            // Si el vendor añade un tercer manager con drenaje (fatiga, temperatura…), este test
            // cae y obliga a decidir si también necesita espejo en el backend, en vez de que el
            // nuevo drene en local sin que nadie se entere.
            string yaml = ReadPrefab();
            Assert.AreEqual(2, Regex.Matches(yaml, @"_depletionSpeed:").Count,
                "STP_Player.prefab debe tener exactamente dos _depletionSpeed (hambre y sed)");
        }

        private static string ReadPrefab()
        {
            Assert.IsTrue(File.Exists(PrefabPath), $"no se encuentra el prefab del jugador en {PrefabPath}");
            return File.ReadAllText(PrefabPath);
        }

        /// <summary>
        /// Lee el `_depletionSpeed` del bloque que sigue al campo ancla dado (`_hunger` / `_thirst`).
        /// Se ancla en el campo del manager y no en el orden de aparición porque el orden de los
        /// MonoBehaviour de un prefab no es contractual: Unity lo reescribe al reserializar.
        /// </summary>
        private static float ReadDepletionSpeed(string anchorField)
        {
            string yaml = ReadPrefab();
            var match = Regex.Match(
                yaml,
                anchorField + @":\s*\d+.*?_depletionSpeed:\s*([0-9.eE+-]+)",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success,
                $"no se encontró un _depletionSpeed tras '{anchorField}' en STP_Player.prefab");
            return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
