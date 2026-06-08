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

        private void OnEnable()
        {
            IPCClient.Instance.AddEventListener(OnGameEvent);
        }

        private void OnDisable()
        {
            if (IPCClient.Instance != null)
                IPCClient.Instance.RemoveEventListener(OnGameEvent);
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev.eventType == "chunk_teleported")
                Trigger();
        }

        private void Update()
        {
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
