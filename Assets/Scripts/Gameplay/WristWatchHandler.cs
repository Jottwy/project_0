using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Saca y guarda el reloj de muñeca con una tecla — MODO OVERLAY (2026-08-15).
    ///
    /// La primera versión registraba el reloj en el WieldablesController del vendor y pagaba el
    /// pipeline completo (animación de equipar, mixer, FOV, exclusión con la otra mano). Tras una
    /// jornada de diagnóstico, se retiró entero: <see cref="WristWatchOverlay"/> poda la instancia
    /// y la cuelga del rig de cámara. Este handler solo instancia una vez y hace toggle.
    ///
    /// LA TECLA ES `T` Y ES PROVISIONAL. `TAB` es lo que pide el concepto, pero está atado a la
    /// acción `Inventory` del vendor (FPS_InputActions). Libres hoy: k, l, m, o, p, t, x, z, 9, 0.
    ///
    /// Historial de diagnóstico (sondas F9, series temporales, dumps): eliminado de aquí el
    /// 2026-08-15 al cerrar la investigación; está en el log de la sesión si hiciera falta
    /// exhumarlo. Conclusiones clave que quedan vigentes: el prefab se guarda con la raíz APAGADA
    /// (los Awake del vendor corren con Character null si nace activa) y el warp `_FOV` del
    /// shader del brazo diverge del canvas de la cara — el overlay lo apaga por material.
    /// </summary>
    public sealed class WristWatchHandler : MonoBehaviour
    {
        /// <summary>Ruta bajo Resources del prefab que genera <c>BackroomsWatchCreator</c>.</summary>
        public const string PrefabResourcePath = "Wieldables/BR_Wieldable_Watch";

        [Tooltip("Tecla provisional para sacar/guardar el reloj. TAB está ocupado por el inventario.")]
        public Key toggleKey = Key.T;

        private WristWatchOverlay _overlay;
        private bool _prefabMissingLogged;
        private string _lastWarning;

        /// <summary>Avisa una vez por motivo: esto vive en Update y sin filtro inunda la consola.</summary>
        private void WarnOnce(string reason)
        {
            if (_lastWarning == reason)
                return;

            _lastWarning = reason;
            Debug.LogWarning($"[WristWatch] No se puede sacar el reloj: {reason}", this);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard[toggleKey].wasPressedThisFrame)
                return;

            if (!EnsureSpawned())
                return;

            _overlay.Toggle();

            // Log en cada pulsación, a propósito: quitar los diagnósticos al reescribir en modo
            // overlay dejó "pulso T y no pasa nada" sin ninguna forma de distinguir entre "no llega
            // la tecla", "no hay personaje", "no carga el prefab" y "sale pero no se ve".
            Debug.Log($"[WristWatch] T -> visible={_overlay.IsShown} " +
                      $"activo={_overlay.gameObject.activeSelf} " +
                      $"escala={_overlay.transform.localScale.x:F3} " +
                      $"padre={_overlay.transform.parent?.name ?? "NINGUNO"} " +
                      $"posMundo={_overlay.transform.position}", this);
        }

        private bool EnsureSpawned()
        {
            if (_overlay != null)
                return true;

            // Resolución perezosa: el personaje no existe al arrancar la escena (el mundo tarda
            // en streamear), así que buscarlo en Start devolvería null para siempre.
            var character = LocalPlayerLocator.Find<Character>();
            if (character == null)
            {
                WarnOnce("no hay Character local todavía (¿mundo aún cargando?)");
                return false;
            }

            if (!character.TryGetCC(out IWieldablesControllerCC controller))
            {
                WarnOnce("el Character no expone IWieldablesControllerCC");
                return false;
            }

            var prefab = Resources.Load<GameObject>(PrefabResourcePath);
            if (prefab == null)
            {
                if (!_prefabMissingLogged)
                {
                    _prefabMissingLogged = true;
                    Debug.LogWarning(
                        $"[WristWatch] No hay prefab en Resources/{PrefabResourcePath}. " +
                        "Genéralo con el menú Backrooms/Create Wrist Watch (Placeholder).");
                }
                return false;
            }

            // El prefab viene con la raíz apagada, así que la instancia nace inactiva y ningún
            // Awake del vendor corre antes de que Setup pode los componentes. El único uso que
            // queda del controller es su WieldablesRoot: el punto del rig de cámara del que
            // cuelgan los wieldables, con pitch y yaw ya resueltos.
            var instance = Instantiate(prefab);
            instance.name = "BR_WristWatch_Overlay";

            _overlay = instance.AddComponent<WristWatchOverlay>();
            // `this` como runner: el handler vive en un objeto que nunca se apaga, y la
            // restauración de proyección tiene que sobrevivir a que el reloj se guarde.
            _overlay.Setup(controller.WieldablesRoot, this);

            Debug.Log($"[WristWatch] Overlay creado bajo '{controller.WieldablesRoot.name}'. " +
                      $"Renderers: {instance.GetComponentsInChildren<Renderer>(true).Length}. " +
                      $"ViewModel: {(instance.transform.Find("ViewModel") != null ? "OK" : "NO ENCONTRADO")}.",
                      this);

            return true;
        }

        private void OnDestroy()
        {
            if (_overlay != null)
                Destroy(_overlay.gameObject);
        }
    }
}
