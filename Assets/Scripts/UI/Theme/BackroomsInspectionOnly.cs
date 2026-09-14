using PolymindGames;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Visible solo con el inventario abierto. Para lo que el builder añade FUERA de los paneles del
    /// vendor (cabecera y caja de Alrededor, cajas de la barra inferior, conmutadores): esos paneles se
    /// ocultan solos al cerrar TAB y lo nuestro no, así que se quedaba pintado en juego.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class BackroomsInspectionOnly : CharacterUIBehaviour
    {
        private CanvasGroup _group;
        private IInventoryInspectionManagerCC _inspection;

        protected override void Awake()
        {
            // El grupo ANTES de base.Awake: si el personaje ya existe, AddBehaviour engancha en el acto y llama a Set. Un
            // panel que nace apagado y se enciende con TAB abierto (Sastrería, Heridas) reventaba ahí, se quedaba invisible
            // y cada abrir/cerrar lanzaba una excepción que cortaba a los demás oyentes de la inspección.
            _group = GetComponent<CanvasGroup>();
            Set(false);
            base.Awake();
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            _inspection = character.GetCC<IInventoryInspectionManagerCC>();
            if (_inspection == null) return;
            _inspection.InspectionStarted += Show;
            _inspection.InspectionEnded += Hide;
            Set(_inspection.IsInspecting);
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (_inspection == null) return;
            _inspection.InspectionStarted -= Show;
            _inspection.InspectionEnded -= Hide;
            _inspection = null;
            Set(false);
        }

        private void Show() => Set(true);

        private void Hide() => Set(false);

        private void Set(bool visible)
        {
            _group.alpha = visible ? 1f : 0f;
            _group.blocksRaycasts = visible;
            _group.interactable = visible;
        }
    }
}
