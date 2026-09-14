using PolymindGames;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Conmutador ALREDEDOR | CRAFTEO | SASTRERÍA de la columna derecha (Joel, 2026-09-13 y 2026-09-14): la misma ventana
    /// enseña lo que hay cerca (contenedor, cocina, reparación), el crafteo o la sastrería de la ropa puesta. Abrir una
    /// estación elige la vista que le toca; sin estación se respeta la última elegida.
    ///
    /// No edita el vendor: el <c>WorkstationInspectControllerUI</c> sigue decidiendo qué panel abre, y
    /// esto actúa DESPUÉS (<c>InspectionPostStarted</c>) mostrando u ocultando esos mismos
    /// <see cref="UIPanel"/> por su API pública. Al cerrar la inspección el vendor los oculta todos; la sastrería es nuestra
    /// y la apaga esto.
    /// </summary>
    public sealed class BackroomsAroundViewToggle : CharacterUIBehaviour
    {
        private enum View
        {
            Around,
            Craft,
            Tailor,
        }

        [SerializeField] private Button _aroundButton;
        [SerializeField] private Button _craftButton;
        [SerializeField] private Button _tailorButton;
        [SerializeField] private Transform _workstations;
        [SerializeField] private GameObject _emptyLabel;
        [SerializeField] private GameObject _tailoringPanel;

        private IInventoryInspectionManagerCC _inspection;
        private View _view;

        protected override void Awake()
        {
            base.Awake();
            if (_aroundButton != null) _aroundButton.onClick.AddListener(ShowAround);
            if (_craftButton != null) _craftButton.onClick.AddListener(ShowCraft);
            if (_tailorButton != null) _tailorButton.onClick.AddListener(ShowTailoring);
            if (_emptyLabel != null) _emptyLabel.SetActive(false);
            if (_tailoringPanel != null) _tailoringPanel.SetActive(false);
            RefreshButtons();
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            _inspection = character.GetCC<IInventoryInspectionManagerCC>();
            if (_inspection == null) return;
            _inspection.InspectionPostStarted += OnInspectionPostStarted;
            _inspection.InspectionEnded += OnInspectionEnded;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (_inspection == null) return;
            _inspection.InspectionPostStarted -= OnInspectionPostStarted;
            _inspection.InspectionEnded -= OnInspectionEnded;
            _inspection = null;
        }

        public void ShowAround() => Select(View.Around);

        public void ShowCraft() => Select(View.Craft);

        public void ShowTailoring() => Select(View.Tailor);

        private void Select(View view)
        {
            _view = view;
            if (_inspection != null && _inspection.IsInspecting) Apply();
            else RefreshButtons();
        }

        private void OnInspectionPostStarted()
        {
            var workstation = _inspection.Workstation;
            if (workstation != null) _view = workstation is CraftStation ? View.Craft : View.Around;
            Apply();
        }

        private void OnInspectionEnded()
        {
            if (_emptyLabel != null) _emptyLabel.SetActive(false);
            if (_tailoringPanel != null) _tailoringPanel.SetActive(false);
        }

        private void Apply()
        {
            var workstation = _inspection.Workstation;
            UIPanel crafting = null, around = null;
            if (_workstations != null)
            {
                foreach (Transform child in _workstations)
                {
                    if (!child.TryGetComponent<IWorkstationInspector>(out var inspector) || !child.TryGetComponent<UIPanel>(out var panel))
                        continue;
                    if (inspector.WorkstationType == typeof(CraftStation)) crafting = panel;
                    else if (workstation != null && inspector.WorkstationType == workstation.GetType()) around = panel;
                }
            }

            if (crafting != null) { if (_view == View.Craft) crafting.Show(); else crafting.Hide(); }
            if (around != null) { if (_view == View.Around) around.Show(); else around.Hide(); }
            if (_emptyLabel != null) _emptyLabel.SetActive(_view == View.Around && around == null);
            if (_tailoringPanel != null) _tailoringPanel.SetActive(_view == View.Tailor);
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            if (_aroundButton != null) _aroundButton.interactable = _view != View.Around;
            if (_craftButton != null) _craftButton.interactable = _view != View.Craft;
            if (_tailorButton != null) _tailorButton.interactable = _view != View.Tailor;
        }
    }
}
