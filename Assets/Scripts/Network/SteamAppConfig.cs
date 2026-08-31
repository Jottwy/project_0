using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// **La única fuente del App ID de Steam.** Antes vivía como constante `SpacewarAppId = 480`
    /// dentro de <see cref="SteamLobbyManager"/>; publicar exigía editar código de red.
    ///
    /// Se resuelve en cuatro escalones, y el que gana se REGISTRA (ver <see cref="Resolve"/>):
    ///
    /// 1. la variable de entorno <see cref="AppIdEnvVar"/> — el override de desarrollo y QA, el
    ///    único que no deja rastro en disco. Va primero para poder forzar 480 sobre cualquier cosa;
    /// 2. la variable <see cref="SteamLaunchAppIdEnvVar"/>, **que pone el propio cliente de Steam
    ///    al lanzar el proceso**. Es la respuesta más autorizada que existe a "¿qué app soy?": no
    ///    la deducimos, la dice quien nos arrancó. Gracias a este escalón **el mismo binario sirve
    ///    para el juego (5072740) y para el playtest (5200320) sin recompilar ni tocar constantes**,
    ///    que es justo lo que hacía falta al subir a SteamPipe;
    /// 3. el fichero <see cref="AppIdFileName"/> junto al ejecutable (en el Editor, en la raíz del
    ///    proyecto) — el mecanismo de Valve para arrancar **fuera** del cliente. Leerlo aquí es lo
    ///    que garantiza que el fichero y el `SteamClient.Init` nunca discrepen; si discrepan, Init
    ///    falla y el motivo no aparece por ningún sitio. **No se empaqueta en el depot**: dentro de
    ///    un build de Steam ganaría a nada útil y pisaría al escalón 2 si éste faltara;
    /// 4. <see cref="DefaultAppId"/>, la constante compilada.
    ///
    /// El **default de release es producción**, no 480. Un build de release que saliera con el id
    /// público de pruebas de Valve es un fallo silencioso: funciona, lista lobbies ajenos, y nadie
    /// se entera hasta que un jugador ve partidas de otro juego. Para hacer playtest con 480 se
    /// deja `steam_appid.txt` con `480` junto al exe (lo que escribe
    /// <c>SteamAppIdBuildPostprocessor</c>) o se exporta <c>BS_STEAM_APPID=480</c>.
    /// </summary>
    public static class SteamAppConfig
    {
        /// Spacewar: el App ID público de pruebas de Valve. **Compartido con todo el mundo** —
        /// por eso la consulta filtra por `bs_game` (ver <see cref="SteamLobbyManager.GameKey"/>).
        public const uint DevAppId = 480;

        /// El App ID real del juego en Steam.
        public const uint ProductionAppId = 5072740;

        /// El App ID del **Playtest**, que en Steam es una app aparte de la del juego.
        /// **No participa en la resolución**: existe sólo para que <see cref="Describe"/> pueda
        /// poner nombre al número en el log. Quien decide que somos el playtest es el cliente de
        /// Steam por <see cref="SteamLaunchAppIdEnvVar"/>, no una constante nuestra — si fuera una
        /// constante, habría que compilar un binario por app y acordarse de cuál es cuál.
        public const uint PlaytestAppId = 5200320;

        public const string AppIdEnvVar = "BS_STEAM_APPID";

        /// La variable que **pone el cliente de Steam** en el entorno del proceso que lanza. El
        /// nombre lo fija Valve; no es nuestro. Es la respuesta más autorizada a "¿qué app soy?",
        /// porque no la deducimos: la dice quien nos arrancó.
        public const string SteamLaunchAppIdEnvVar = "SteamAppId";

        /// El nombre lo fija Valve; no es nuestro.
        public const string AppIdFileName = "steam_appid.txt";

        /// <summary>De dónde salió el App ID que se está usando. Va al log en el arranque.</summary>
        public enum AppIdSource
        {
            Default,
            EnvironmentVariable,

            /// Lo dijo el cliente de Steam al lanzarnos. Es el caso normal de un build instalado.
            SteamLaunch,

            AppIdFile,
        }

        /// <summary>
        /// La constante compilada. Development build y Editor arrancan en 480 porque ahí nunca hay
        /// un lanzamiento desde Steam; release arranca en el id real.
        /// </summary>
        public static uint DefaultAppId =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DevAppId;
#else
            ProductionAppId;
#endif

        /// <summary>
        /// Núcleo puro, sin Unity ni disco dentro: es lo que se puede probar. Los dos primeros
        /// argumentos son el contenido crudo (pueden ser null, vacíos o basura).
        /// </summary>
        public static uint Resolve(string envValue, string steamLaunchValue, string fileContents,
            uint fallback, out AppIdSource source)
        {
            if (TryParse(envValue, out uint fromEnv))
            {
                source = AppIdSource.EnvironmentVariable;
                return fromEnv;
            }

            if (TryParse(steamLaunchValue, out uint fromSteam))
            {
                source = AppIdSource.SteamLaunch;
                return fromSteam;
            }

            if (TryParse(fileContents, out uint fromFile))
            {
                source = AppIdSource.AppIdFile;
                return fromFile;
            }

            source = AppIdSource.Default;
            return fallback;
        }

        /// <summary>
        /// Un App ID es un entero positivo. Se aceptan espacios, saltos de línea y BOM porque
        /// `steam_appid.txt` lo escribe cualquiera con cualquier editor; se rechaza el 0 (Steam lo
        /// trata como "sin id") y todo lo que no sea decimal — un valor degenerado tiene que caer
        /// al escalón siguiente, no colarse como App ID.
        /// </summary>
        public static bool TryParse(string raw, out uint appId)
        {
            appId = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string trimmed = raw.Trim().TrimStart('﻿').Trim();
            if (!uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)) return false;
            if (parsed == 0) return false;

            appId = parsed;
            return true;
        }

        /// <summary>
        /// La carpeta donde vive <see cref="AppIdFileName"/>: la del ejecutable en un player, la
        /// raíz del proyecto en el Editor. `Application.dataPath` apunta a `&lt;exe&gt;_Data` en el
        /// primer caso y a `&lt;proyecto&gt;/Assets` en el segundo, así que el padre sirve para los
        /// dos.
        /// </summary>
        public static string AppIdFileDirectory
        {
            get
            {
                try
                {
                    return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static string AppIdFilePath
        {
            get
            {
                string dir = AppIdFileDirectory;
                return dir == null ? null : Path.Combine(dir, AppIdFileName);
            }
        }

        /// <summary>
        /// El App ID efectivo, con la procedencia. **No se cachea**: se llama una vez por proceso
        /// (desde <see cref="SteamLobbyManager"/>) y un valor cacheado sobreviviría a una recarga
        /// de dominio del Editor con el fichero ya cambiado.
        /// </summary>
        public static uint Current(out AppIdSource source, out string detail)
        {
            string env = ReadEnv(AppIdEnvVar);
            string steamLaunch = ReadEnv(SteamLaunchAppIdEnvVar);

            string path = AppIdFilePath;
            string contents = null;
            if (path != null)
            {
                try
                {
                    if (File.Exists(path)) contents = File.ReadAllText(path);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SteamAppConfig] No se pudo leer {path}: {e.Message}");
                }
            }

            uint appId = Resolve(env, steamLaunch, contents, DefaultAppId, out source);

            switch (source)
            {
                case AppIdSource.EnvironmentVariable:
                    detail = $"{AppIdEnvVar}={env}";
                    break;
                case AppIdSource.SteamLaunch:
                    detail = $"{SteamLaunchAppIdEnvVar}={steamLaunch} (lo puso el cliente de Steam al lanzar)";
                    break;
                case AppIdSource.AppIdFile:
                    detail = path;
                    break;
                default:
                    detail = "constante compilada";
                    break;
            }

            return appId;
        }

        /// <summary>Lee una variable de entorno; un entorno inaccesible cae al escalón siguiente.</summary>
        private static string ReadEnv(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SteamAppConfig] No se pudo leer {name}: {e.Message}");
                return null;
            }
        }

        /// <summary>Etiqueta legible para el log: distingue 480 de un id propio de un vistazo.</summary>
        public static string Describe(uint appId)
        {
            if (appId == DevAppId) return appId + " (Spacewar / desarrollo)";
            if (appId == ProductionAppId) return appId + " (producción)";
            if (appId == PlaytestAppId) return appId + " (playtest)";
            return appId.ToString(CultureInfo.InvariantCulture);
        }
    }
}
