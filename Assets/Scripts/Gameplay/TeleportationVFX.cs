using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay
{
    public sealed class TeleportationVFX : MonoBehaviour
    {
        private Canvas _canvas;
        private Image _flashImage;
        private Image _staticImage;
        private float _flashTimer;
        private float _staticTimer;
        private bool _active;

        private const float FlashDuration = 0.15f;
        private const float StaticDuration = 0.5f;

        private Texture2D _noiseTex;

        private void Start()
        {
            _canvas = new GameObject("TeleportCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            DontDestroyOnLoad(_canvas.gameObject);

            _flashImage = CreateOverlay("Flash", new Color(1f, 1f, 1f, 0f));
            _staticImage = CreateOverlay("Static", new Color(1f, 1f, 1f, 0f));

            _noiseTex = GenerateNoise(128, 128);
            _staticImage.sprite = Sprite.Create(
                _noiseTex,
                new Rect(0, 0, _noiseTex.width, _noiseTex.height),
                new Vector2(0.5f, 0.5f));
            _staticImage.type = Image.Type.Tiled;
        }

        private Image CreateOverlay(string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        // Suscripción DIFERIDA, no en OnEnable. Este componente lo añade GameBootstrap.Awake y el
        // IPCClient no existe hasta que NetworkInitializer lo crea en Start: engancharse en
        // OnEnable era pedirle el listener a un singleton que todavía no estaba, así que la
        // suscripción no llegaba a ocurrir NUNCA y este efecto llevaba muerto desde que existe.
        // Mismo patrón que AuthoritativePoseApplier, y por el mismo motivo.
        private IPCClient _ipc;

        private void OnDisable()
        {
            if (_ipc != null)
            {
                _ipc.RemoveEventListener(OnGameEvent);
                _ipc = null;
            }
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            // SOLO `chunk_teleported` (desplazamiento de chunk), que es una anomalía del mundo y
            // debe anunciarse.
            //
            // El cruce de una puerta del Level 4 NO entra aquí, y fue un error meterlo: un flash
            // blanco y estática de televisión son exactamente "acabas de teletransportarte", que
            // es lo contrario de lo que una puerta tiene que sentirse. Atravesar un marco se
            // parece a atravesar un marco; el trabajo de que no se note lo hace la continuidad de
            // la posición, no un efecto que tape el corte.
            if (ev.eventType == "chunk_teleported")
                Trigger();
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }

            if (!_active) return;

            // Flash phase.
            if (_flashTimer > 0f)
            {
                _flashTimer -= Time.deltaTime;
                float a = Mathf.Clamp01(_flashTimer / FlashDuration);
                _flashImage.color = new Color(1f, 1f, 1f, a);
            }
            else
            {
                _flashImage.color = new Color(1f, 1f, 1f, 0f);
            }

            // Static phase.
            if (_staticTimer > 0f)
            {
                _staticTimer -= Time.deltaTime;
                float a = Mathf.Clamp01(_staticTimer / StaticDuration) * 0.7f;
                _staticImage.color = new Color(1f, 1f, 1f, a);

                // Animate the static noise by offsetting UV.
                var rt = _staticImage.rectTransform;
                rt.anchoredPosition = new Vector2(
                    Random.Range(-20f, 20f),
                    Random.Range(-20f, 20f));
            }
            else
            {
                _staticImage.color = new Color(1f, 1f, 1f, 0f);
                _active = false;
            }
        }

        public void Trigger()
        {
            _active = true;
            _flashTimer = FlashDuration;
            _staticTimer = StaticDuration;
            _flashImage.color = Color.white;
        }

        private static Texture2D GenerateNoise(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                float v = Random.value;
                pixels[i] = new Color(v, v, v, 1f);
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private void OnDestroy()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
            if (_noiseTex != null) Destroy(_noiseTex);
        }
    }
}
