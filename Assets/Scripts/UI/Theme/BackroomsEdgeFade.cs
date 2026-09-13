using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Fundido de bordes (pulido del inventario, 2026-09-13): el render del personaje se funde con el color de la caja
    /// en vez de acabar en seco. La textura se genera al tamaño del rect (a un cuarto) para que el ancho del fundido sea
    /// el mismo en píxeles por los cuatro lados; sin asset nuevo. Hasta tener textura, el RawImage va apagado: un
    /// RawImage sin textura pinta un bloque blanco encima del muñeco.
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    public sealed class BackroomsEdgeFade : MonoBehaviour
    {
        private const float TextureScale = 0.25f;

        [SerializeField] private Color _color = Color.black;
        [SerializeField] private float _sides = 48f;
        [SerializeField] private float _top = 24f;
        [SerializeField] private float _bottom = 72f;

        private Texture2D _texture;
        private Vector2 _builtFor;

        private void OnEnable() => Rebuild();

        private void OnRectTransformDimensionsChange()
        {
            if (isActiveAndEnabled) Rebuild();
        }

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
        }

        private void Rebuild()
        {
            var size = ((RectTransform)transform).rect.size;
            if (size.x < 1f || size.y < 1f || size == _builtFor) return;
            _builtFor = size;

            int w = Mathf.Max(4, Mathf.CeilToInt(size.x * TextureScale));
            int h = Mathf.Max(4, Mathf.CeilToInt(size.y * TextureScale));
            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) Destroy(_texture);
                _texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
            }

            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float fromBottom = (y + 0.5f) / TextureScale;
                for (int x = 0; x < w; x++)
                {
                    float fromLeft = (x + 0.5f) / TextureScale;
                    float a = Mathf.Max(Mathf.Max(Edge(fromLeft, _sides), Edge(size.x - fromLeft, _sides)),
                        Mathf.Max(Edge(fromBottom, _bottom), Edge(size.y - fromBottom, _top)));
                    var c = _color;
                    c.a *= a;
                    pixels[y * w + x] = c;
                }
            }
            _texture.SetPixels32(pixels);
            _texture.Apply(false);

            var image = GetComponent<RawImage>();
            image.texture = _texture;
            image.color = Color.white;
            image.enabled = true;
        }

        /// <summary>Opacidad a <paramref name="distance"/> px de un borde con un fundido de <paramref name="width"/> px.</summary>
        public static float Edge(float distance, float width)
            => width <= 0f ? 0f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distance / width));
    }
}
