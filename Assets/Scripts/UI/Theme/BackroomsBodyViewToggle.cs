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
    /// </summary>
    public sealed class BackroomsBodyViewToggle : MonoBehaviour
    {
        [SerializeField] private Button _ropaButton;
        [SerializeField] private Button _heridasButton;
        [SerializeField] private GameObject _previewRoot;
        [SerializeField] private GameObject _previewBackdrop;
        [SerializeField] private GameObject _woundsPanel;

        private void Awake()
        {
            if (_ropaButton != null) _ropaButton.onClick.AddListener(ShowRopa);
            if (_heridasButton != null) _heridasButton.onClick.AddListener(ShowHeridas);
        }

        private void OnEnable() => ShowRopa();

        public void ShowRopa() => Apply(true);

        public void ShowHeridas() => Apply(false);

        private void Apply(bool ropa)
        {
            if (_previewRoot != null) _previewRoot.SetActive(ropa);
            if (_previewBackdrop != null) _previewBackdrop.SetActive(ropa);
            if (_woundsPanel != null) _woundsPanel.SetActive(!ropa);
            if (_ropaButton != null) _ropaButton.interactable = !ropa;
            if (_heridasButton != null) _heridasButton.interactable = ropa;
        }
    }
}
