using PolymindGames;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Conmutador ALREDEDOR | CRAFTEO de la columna derecha (Joel, 2026-09-13): la misma ventana enseña
    /// o lo que hay cerca (contenedor, cocina, reparación) o el crafteo. Abrir una estación elige la
    /// vista que le toca; sin estación se respeta la última elegida.
    ///
    /// No edita el vendor: el <c>WorkstationInspectControllerUI</c> sigue decidiendo qué panel abre, y
    /// esto actúa DESPUÉS (<c>InspectionPostStarted</c>) mostrando u ocultando esos mismos
    /// <see cref="UIPanel"/> por su API pública. Al cerrar la inspección el vendor los oculta todos.
    /// </summary>
    public sealed class BackroomsAroundViewToggle : CharacterUIBehaviour
    {
        [SerializeField] private Button _aroundButton;
        [SerializeField] private Button _craftButton;
        [SerializeField] private Transform _workstations;
        [SerializeField] private GameObject _emptyLabel;

        private IInventoryInspectionManagerCC _inspection;
        private bool _craft;

        protected override void Awake()
        {
            base.Awake();
            if (_aroundButton != null) _aroundButton.onClick.AddListener(ShowAround);
            if (_craftButton != null) _craftButton.onClick.AddListener(ShowCraft);
            if (_emptyLabel != null) _emptyLabel.SetActive(false);
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

        public void ShowAround() => Select(false);

        public void ShowCraft() => Select(true);

        private void Select(bool craft)
        {
            _craft = craft;
            if (_inspection != null && _inspection.IsInspecting) Apply();
            else RefreshButtons();
        }

        private void OnInspectionPostStarted()
        {
            var workstation = _inspection.Workstation;
            if (workstation != null) _craft = workstation is CraftStation;
            Apply();
        }

        private void OnInspectionEnded()
        {
            if (_emptyLabel != null) _emptyLabel.SetActive(false);
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

            if (crafting != null) { if (_craft) crafting.Show(); else crafting.Hide(); }
            if (around != null) { if (_craft) around.Hide(); else around.Show(); }
            if (_emptyLabel != null) _emptyLabel.SetActive(!_craft && around == null);
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            if (_aroundButton != null) _aroundButton.interactable = _craft;
            if (_craftButton != null) _craftButton.interactable = !_craft;
        }
    }
}
