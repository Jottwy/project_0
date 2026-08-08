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
    /// personaje exista, a diferencia de InventoryReporter). Envía una sola vez por proceso: no
    /// se reenvía si el IPC se desconecta y reconecta (ver <see cref="_sent"/>) — asume que el
    /// backend que responde es el mismo proceso y conserva su <c>identity_key</c> en memoria
    /// entre conexiones, no que ha reiniciado.
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

        private bool _sent;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
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
            if (_sent)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            _sent = ipc.SendSetIdentity(ResolveKey());
            if (_sent)
                Debug.Log("[PlayerIdentity] identity sent to backend");
        }

        private static string ResolveKey()
        {
            string envOverride = Environment.GetEnvironmentVariable(IdentityEnvVar);
            if (!string.IsNullOrWhiteSpace(envOverride))
            {
                Debug.Log($"[PlayerIdentity] resolved from env override {IdentityEnvVar}, prefix={KeyPrefix(envOverride)}");
                return envOverride;
            }

            string key = "uuid:" + ResolveOrCreateGuid();
            Debug.Log($"[PlayerIdentity] resolved from PlayerPrefs, prefix={KeyPrefix(key)}");
            return key;
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
