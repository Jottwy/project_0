using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.MovementSystem;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// ADR-068 — la barra de pintura del bote, bajo la retícula.
    ///
    /// El vendor NO trae ninguna barra: lo único que existe es `WieldableDurabilityUI`, un aviso
    /// de TEXTO que se asoma por debajo del 35 %. Sirve para avisar de que algo se acaba, no para
    /// dosificar — y dosificar es justo lo que hace falta aquí, porque la pintura se gasta por
    /// metro pintado y el jugador necesita saber cuánto trazo le queda MIENTRAS pinta.
    ///
    /// Lee `SprayCan.PaintFraction`, que a su vez sale de la propiedad `Durability` del item: o
    /// sea de la carga de ESE bote, no de un número del prefab. Se construye sola en runtime, sin
    /// prefab ni escena que tocar, igual que `JoinSessionUI` monta sus widgets.
    /// </summary>
    [DisallowMultipleComponent]
    public class SprayChargeBar : MonoBehaviour
    {
        private const float PollSeconds = 0.15f;
        private const float FadeSpeed = 6f;
        private const float WidthPx = 140f;
        private const float HeightPx = 6f;
        /// <summary>Cuánto por debajo del centro de pantalla. Lejos de la retícula para no
        /// estorbar al apuntar, cerca para verse sin apartar la vista.</summary>
        private const float DropPx = 64f;

        private static SprayChargeBar _instance;

        private CanvasGroup _group;
        private Image _fill;
        private CharacterControllerMotor _motor;
        private ICharacter _character;
        private IWieldablesControllerCC _controller;
        private SprayCan _can;
        private GameObject _canCachedFor;
        private float _nextPoll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("SprayChargeBar");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SprayChargeBar>();
        }

        private void OnEnable() => Build();

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Build()
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Por debajo del HUD del vendor: esto informa, no compite con la retícula.
            canvas.sortingOrder = 50;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var root = new GameObject("Bar", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            root.transform.SetParent(canvasGo.transform, false);

            var rt = root.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(WidthPx, HeightPx);
            rt.anchoredPosition = new Vector2(0f, -DropPx);

            var back = root.GetComponent<Image>();
            back.color = new Color(0f, 0f, 0f, 0.55f);
            back.raycastTarget = false;

            _group = root.GetComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(root.transform, false);
            var frt = fillGo.GetComponent<RectTransform>();
            // Anclado a la IZQUIERDA y escalando por anchor: así la barra se vacía hacia la
            // izquierda sin tener que recalcular tamaños en cada frame.
            frt.anchorMin = new Vector2(0f, 0f);
            frt.anchorMax = new Vector2(1f, 1f);
            frt.offsetMin = new Vector2(1f, 1f);
            frt.offsetMax = new Vector2(-1f, -1f);

            _fill = fillGo.GetComponent<Image>();
            _fill.raycastTarget = false;
            _fill.type = Image.Type.Filled;
            _fill.fillMethod = Image.FillMethod.Horizontal;
            _fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _fill.fillAmount = 1f;
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                _can = ResolveActiveCan();
            }

            // Solo se ve con el bote en la mano: una barra permanente sería ruido en pantalla el
            // 99 % de la partida.
            float target = _can != null ? 1f : 0f;
            if (_group != null)
                _group.alpha = Mathf.MoveTowards(_group.alpha, target, FadeSpeed * Time.unscaledDeltaTime);

            if (_can == null || _fill == null) return;

            float fraction = _can.PaintFraction;
            _fill.fillAmount = fraction;
            // Ámbar cuando queda poco: el mismo lenguaje que el aviso de texto del vendor, que
            // se enciende por debajo del 35 %.
            _fill.color = fraction < 0.35f
                ? Color.Lerp(new Color(0.85f, 0.25f, 0.15f), new Color(0.95f, 0.7f, 0.2f), fraction / 0.35f)
                : new Color(0.85f, 0.85f, 0.82f);
        }

        /// <summary>
        /// Mismo baile con el null que `SprayPainter` y `TorchShadowCaster`, y por la misma razón:
        /// un wieldable DESTRUIDO devuelve una referencia de interfaz no-nula y tocar su
        /// `.gameObject` lanza.
        /// </summary>
        private SprayCan ResolveActiveCan()
        {
            if (_motor == null)
            {
                _motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
                _character = null;
                _controller = null;
            }
            if (_motor == null) return null;

            if (_character == null) _character = _motor.GetComponentInParent<ICharacter>();
            if (_controller == null && _character != null)
                _controller = _character.GetCC<IWieldablesControllerCC>();
            if (_controller == null) return null;

            var wieldable = _controller.ActiveWieldable;
            bool destroyed = wieldable is Object uo && uo == null;
            var go = (wieldable == null || destroyed) ? null : wieldable.gameObject;
            if (go == null)
            {
                _canCachedFor = null;
                return null;
            }

            if (!ReferenceEquals(_canCachedFor, go))
            {
                _canCachedFor = go;
                _can = go.GetComponentInChildren<SprayCan>(true);
            }
            return _can;
        }
    }
}
