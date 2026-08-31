using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// READ-ONLY diagnostic for the "clicks don't register on inventory/book/build" report.
    /// Two prior attempts (deleting the scene's EventSystem, then destroying every EventSystem
    /// except EventSystem.current) both broke UI that was working, on nothing stronger than log
    /// correlation. This does not touch, destroy, or disable anything — it only logs, so the next
    /// reproduction gives ground truth instead of another guess.
    ///
    /// Logs (tag [EventSystemDiag], grep-able):
    ///   • Whenever the SET of live EventSystems changes — name, instance id, full hierarchy path,
    ///     enabled state, whether it IS EventSystem.current, and its InputModule + ActionsAsset.
    ///   • Whenever EventSystem.current itself changes (the moment input authority hands off).
    ///   • On every left-click: which EventSystem was current, IsPointerOverGameObject(), and a
    ///     manual raycast (EventSystem.RaycastAll) so we see exactly what the click actually hit —
    ///     the missing piece so far. If a click over the inventory/book/build button hits nothing,
    ///     or hits something on the wrong Canvas, that is the real mechanism, not a guess.
    ///
    /// Self-bootstraps, mirrors RespawnRequester; removable. **OFF by default** — set
    /// <c>BACKROOMS_UI_DIAG=1</c> to arm it (see <see cref="Enabled"/>): left running it cost
    /// 1.09 ms/frame on a full-scene scan, which is not a price a diagnostic gets to charge every
    /// session for a question it answers once per scene.
    /// </summary>
    public sealed class EventSystemDiagnostics : MonoBehaviour
    {
        private static EventSystemDiagnostics _instance;

        private readonly HashSet<int> _knownSystemIds = new();
        private EventSystem _lastCurrent;
        private readonly List<RaycastResult> _raycastBuffer = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
        }

        /// <summary>
        /// APAGADO por defecto, y el interruptor está en el arranque y no en el bucle.
        ///
        /// Este diagnóstico barría la escena entera con <c>FindObjectsByType</c> **cada frame**, y
        /// el Profiler lo midió en **1,09 ms por frame** — el 14 % del presupuesto de scripts— para
        /// detectar un cambio que ocurre una vez por escena. Cuando el objeto ni siquiera se crea,
        /// no hay <c>Update</c> que llamar y el coste es exactamente cero, que es la única cifra
        /// aceptable para una herramienta que la mayoría de sesiones no usa.
        ///
        /// Se enciende con <c>BACKROOMS_UI_DIAG=1</c> en el entorno, misma convención que
        /// <c>BACKROOMS_VERBOSE_LOG</c>.
        /// </summary>
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("BACKROOMS_UI_DIAG") == "1";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!Enabled || _instance != null)
                return;

            var go = new GameObject("[EventSystemDiagnostics]");
            _instance = go.AddComponent<EventSystemDiagnostics>();
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

        /// <summary>Cada cuánto se rebarre la escena buscando EventSystems, en segundos. Un
        /// EventSystem aparece al construir una pantalla, no a mitad de un frame, así que un segundo
        /// lo caza igual de bien que sesenta barridos por segundo y cuesta la sesentava parte.</summary>
        private const float ScanInterval = 1f;

        private float _nextScan;

        private void Update()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanInterval;
                ScanLiveSet();
            }

            // El cambio de AUTORIDAD sí se mira por frame: `EventSystem.current` es una propiedad
            // estática, leerla es gratis, y el instante exacto del relevo es justo el dato que este
            // diagnóstico existe para capturar.
            var current = EventSystem.current;
            if (current != _lastCurrent)
            {
                Debug.Log("[EventSystemDiag] EventSystem.current changed: "
                    + (_lastCurrent != null ? DescribeEventSystem(_lastCurrent) : "null")
                    + "  ->  "
                    + (current != null ? DescribeEventSystem(current) : "null"));
                _lastCurrent = current;
            }

#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                LogClickSnapshot();
#endif
        }

        /// <summary>El barrido caro, ya fuera del camino de cada frame.</summary>
        private void ScanLiveSet()
        {
            var systems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            // Log only on a real change to the live set (avoids spamming every frame like Unity's
            // own built-in warning does).
            bool changed = systems.Length != _knownSystemIds.Count;
            if (!changed)
            {
                foreach (var s in systems)
                {
                    if (!_knownSystemIds.Contains(s.GetInstanceID()))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed) return;

            _knownSystemIds.Clear();
            var sb = new StringBuilder();
            sb.Append("[EventSystemDiag] live set changed (" + systems.Length + "):");
            foreach (var s in systems)
            {
                _knownSystemIds.Add(s.GetInstanceID());
                sb.Append("\n  - ").Append(DescribeEventSystem(s));
            }
            Debug.Log(sb.ToString());
        }

#if ENABLE_INPUT_SYSTEM
        private void LogClickSnapshot()
        {
            var sb = new StringBuilder();
            sb.Append("[EventSystemDiag] left-click @ ").Append(Mouse.current.position.ReadValue());
            sb.Append(" | current=").Append(EventSystem.current != null ? DescribeEventSystem(EventSystem.current) : "null");

            if (EventSystem.current == null)
            {
                Debug.Log(sb.ToString());
                return;
            }

            sb.Append(" | IsPointerOverGameObject=").Append(EventSystem.current.IsPointerOverGameObject());

            var ped = new PointerEventData(EventSystem.current) { position = Mouse.current.position.ReadValue() };
            _raycastBuffer.Clear();
            EventSystem.current.RaycastAll(ped, _raycastBuffer);

            if (_raycastBuffer.Count == 0)
            {
                sb.Append(" | raycast HIT NOTHING");
            }
            else
            {
                sb.Append(" | raycast hits (").Append(_raycastBuffer.Count).Append("):");
                for (int i = 0; i < _raycastBuffer.Count; i++)
                {
                    var r = _raycastBuffer[i];
                    sb.Append("\n    ").Append(i).Append(": ").Append(GetPath(r.gameObject.transform))
                      .Append(" (module=").Append(r.module != null ? r.module.GetType().Name : "null")
                      .Append(", raycaster=").Append(r.module != null ? GetPath(r.module.transform) : "null").Append(')');
                }
            }

            Debug.Log(sb.ToString());
        }
#endif

        private static string DescribeEventSystem(EventSystem s)
        {
            if (s == null)
                return "null";

#if ENABLE_INPUT_SYSTEM
            var module = s.GetComponent<InputSystemUIInputModule>();
            string moduleDesc = module != null
                ? "InputSystemUIInputModule(actions=" + (module.actionsAsset != null ? module.actionsAsset.name : "NONE") + ")"
                : "no-InputSystemUIInputModule";
#else
            string moduleDesc = "legacy-input";
#endif
            return $"'{GetPath(s.transform)}' id={s.GetInstanceID()} enabled={s.enabled} active={s.gameObject.activeInHierarchy} isCurrent={s == EventSystem.current} {moduleDesc}";
        }

        private static string GetPath(Transform t)
        {
            if (t == null)
                return "<null>";

            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }
    }
}
