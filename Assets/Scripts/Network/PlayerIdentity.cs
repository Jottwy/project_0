using System;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-045 Fase 1: acuña (o recupera) la identidad opaca del jugador y la manda a SU PROPIO
    /// backend vía <see cref="IPCClient.SendSetIdentity"/> tan pronto como el IPC conecta, para
    /// que el backend sepa qué fichero de jugador cargar/escribir (Fase 2).
    ///
    /// Normalmente es un GUID acuñado una sola vez y persistido en <see cref="PlayerPrefs"/>, con
    /// el namespace obligatorio "uuid:" (ADR-045 punto 2). PERO <see cref="PlayerPrefs"/> en
    /// Windows vive en el registro por company+product name — COMPARTIDO entre todas las
    /// instancias del mismo build en la misma máquina, no por proceso. Sin más, host y joiner del
    /// playtest local (misma máquina) leerían el MISMO GUID por defecto. Por eso, ANTES de tocar
    /// PlayerPrefs, se comprueba la env var PLAYER_IDENTITY_KEY (mismo patrón ya establecido por
    /// NetworkInitializer para NET_ID/NET_PORT/etc. vía Environment.GetEnvironmentVariable) — si
    /// está presente, esa es la clave que se manda, sin acuñar ni leer GUID.
    ///
    /// Self-bootstraps; sin dependencia del rig del personaje (la identidad no necesita que el
    /// personaje exista, a diferencia de InventoryReporter). Envía una vez por CONEXIÓN IPC, no
    /// una vez por proceso: el latch es <see cref="IPCClient.ConnectionEpoch"/>, no un bool.
    ///
    /// El motivo es la SEGUNDA sesión. Este GameObject es DontDestroyOnLoad y
    /// <see cref="ResetStatics"/> solo corre en SubsystemRegistration, así que sobrevive al
    /// volver al menú — pero el backend NO: Host/Join lanza un proceso nuevo, con
    /// <c>player.identity_key = None</c>. Con un latch de proceso ese backend nuevo nunca
    /// recibía <c>set_identity</c>, la resolución del fichero de jugador (game_loop.rs, gated en
    /// `identity_key`) nunca corría, y la sesión entera se jugaba sin hidratación, sin autosave
    /// y sin save-on-quit — en silencio, porque nada falla, simplemente no ocurre.
    ///
    /// Reenviar en una reconexión al MISMO backend es inofensivo (el handler de
    /// <c>set_identity</c> es idempotente: reasigna la misma clave y sigue) y esa es la razón de
    /// gatear en el epoch y no en "¿es un backend nuevo?", que el cliente no puede saber.
    ///
    /// El override de <see cref="IdentityEnvVar"/> se manda tal cual: aquí no se valida el
    /// namespace "uuid:" (ADR-045 punto 2) porque el contrato real del backend es aceptar
    /// cualquier clave arbitraria en <c>set_identity</c>, no solo GUIDs con ese prefijo.
    /// </summary>
    public sealed class PlayerIdentity : MonoBehaviour
    {
        private const string IdentityEnvVar = "PLAYER_IDENTITY_KEY";
        private const string GuidPrefKey = "identity.guid";

        private static PlayerIdentity _instance;
        private static string _cachedKey;

        /// <summary>Epoch de la conexión en la que ya se envió la identidad. 0 = ninguna, y
        /// <see cref="IPCClient.ConnectionEpoch"/> empieza en 1 en su primera conexión.</summary>
        private int _sentEpoch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _cachedKey = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[PlayerIdentity]");
            _instance = go.AddComponent<PlayerIdentity>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void Update()
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            // Read once: the network thread can bump the epoch between the guard and the send,
            // and recording the value we actually sent on is what keeps the retry correct.
            int epoch = ipc.ConnectionEpoch;
            if (epoch == _sentEpoch)
                return;

            if (!ipc.SendSetIdentity(ResolveKey()))
                return;

            _sentEpoch = epoch;
            Debug.Log($"[PlayerIdentity] identity sent to backend (connection epoch {epoch})");
        }

        /// <summary>
        /// Resuelve la clave una vez y la cachea: la identidad no cambia durante el proceso, y
        /// ahora hay más de una llamada (una por conexión, más los reintentos mientras el socket
        /// no acepta el frame). Sin la caché, cada reintento reharía la lectura de PlayerPrefs y
        /// repetiría el log de resolución en bucle.
        /// </summary>
        private static string ResolveKey()
        {
            if (_cachedKey != null)
                return _cachedKey;

            string envOverride = Environment.GetEnvironmentVariable(IdentityEnvVar);
            if (!string.IsNullOrWhiteSpace(envOverride))
            {
                Debug.Log($"[PlayerIdentity] resolved from env override {IdentityEnvVar}, prefix={KeyPrefix(envOverride)}");
                _cachedKey = envOverride;
                return _cachedKey;
            }

            _cachedKey = "uuid:" + ResolveOrCreateGuid();
            Debug.Log($"[PlayerIdentity] resolved from PlayerPrefs, prefix={KeyPrefix(_cachedKey)}");
            return _cachedKey;
        }

        /// <summary>Primeros caracteres de una clave para logging — nunca la clave entera (es
        /// identidad de jugador y estos logs pueden acabar en reportes de bug compartidos).</summary>
        private static string KeyPrefix(string key)
        {
            const int PrefixLength = 8;
            return key.Length <= PrefixLength ? key : key[..PrefixLength] + "…";
        }

        private static string ResolveOrCreateGuid()
        {
            string existing = PlayerPrefs.GetString(GuidPrefKey, string.Empty);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            string fresh = Guid.NewGuid().ToString();
            PlayerPrefs.SetString(GuidPrefKey, fresh);
            PlayerPrefs.Save();
            return fresh;
        }
    }
}
