using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Conmutador Ropa | Heridas de la columna Personaje (INVENTORY-ROADMAP.md, sección «Pulido del
    /// reparto», y greybox <c>1d0e788e</c>). Solo cambia qué se ve en el hueco del preview: la vista
    /// previa del vendor, o un panel de zonas. PLACEHOLDER estructural: las zonas son de ejemplo, sin
    /// datos reales de cuerpo por zonas (eso vive en «Sistemas anotados, sin empezar» y pide su
    /// propio ADR antes de tener contenido real).
    ///
    /// Pulido 6: el cambio es un fundido cruzado corto. Durante el fundido las dos vistas están activas y se escala el
    /// alpha de sus gráficos sobre el suyo propio; al acabar, la que no toca se apaga. No se usa CanvasGroup: el de
    /// estos objetos ya es de <c>BackroomsInspectionOnly</c>.
    /// </summary>
    public sealed class BackroomsBodyViewToggle : MonoBehaviour
    {
        private const float FadeSeconds = 0.18f;

        [SerializeField] private Button _ropaButton;
        [SerializeField] private Button _heridasButton;
        [SerializeField] private GameObject _previewRoot;
        [SerializeField] private GameObject _previewBackdrop;
        [SerializeField] private GameObject _woundsPanel;

        private readonly Dictionary<Graphic, float> _baseAlpha = new();
        private readonly List<Graphic> _scan = new();
        private float _ropa = 1f;
        private float _target = 1f;

        private void Awake()
        {
            if (_ropaButton != null) _ropaButton.onClick.AddListener(ShowRopa);
            if (_heridasButton != null) _heridasButton.onClick.AddListener(ShowHeridas);
        }

        private void OnEnable()
        {
            _ropa = _target = 1f;
            Finish();
        }

        public void ShowRopa() => Select(true);

        public void ShowHeridas() => Select(false);

        private void Select(bool ropa)
        {
            _target = ropa ? 1f : 0f;
            if (_ropaButton != null) _ropaButton.interactable = !ropa;
            if (_heridasButton != null) _heridasButton.interactable = ropa;
            if (_ropa == _target) return;
            SetActive(_previewBackdrop, true);
            SetActive(_woundsPanel, true);
            SetActive(_previewRoot, ropa);
        }

        private void Update()
        {
            if (_ropa == _target) return;
            _ropa = Mathf.MoveTowards(_ropa, _target, Time.unscaledDeltaTime / FadeSeconds);
            float eased = _ropa * _ropa * (3f - 2f * _ropa);
            Scale(_previewBackdrop, eased);
            Scale(_woundsPanel, 1f - eased);
            if (_ropa == _target) Finish();
        }

        private void Finish()
        {
            bool ropa = _target >= 1f;
            Scale(_previewBackdrop, 1f);
            Scale(_woundsPanel, 1f);
            SetActive(_previewRoot, ropa);
            SetActive(_previewBackdrop, ropa);
            SetActive(_woundsPanel, !ropa);
            if (_ropaButton != null) _ropaButton.interactable = !ropa;
            if (_heridasButton != null) _heridasButton.interactable = ropa;
        }

        private void Scale(GameObject root, float factor)
        {
            if (root == null) return;
            root.GetComponentsInChildren(true, _scan);
            foreach (var graphic in _scan)
            {
                if (!_baseAlpha.TryGetValue(graphic, out float baseAlpha))
                {
                    baseAlpha = graphic.color.a;
                    _baseAlpha[graphic] = baseAlpha;
                }
                var color = graphic.color;
                color.a = baseAlpha * factor;
                graphic.color = color;
            }
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }
    }
}
