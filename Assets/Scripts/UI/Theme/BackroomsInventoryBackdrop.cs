using PolymindGames;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// El mundo oscurecido detrás del inventario (D1: sin desenfoque, sin pausa). Un rectángulo a
    /// pantalla completa que se enciende con la inspección y se apaga al cerrarla — lo único que
    /// hace es mover el alpha de su <see cref="CanvasGroup"/>.
    ///
    /// Es un <see cref="CharacterUIBehaviour"/> del vendor, en NUESTRO ensamblado (subclase, no
    /// edición: memoria <c>stp-no-direct-edits</c>): así cuelga del mismo <c>PlayerUI</c> que el
    /// resto y recibe el personaje por el mismo camino que <c>InventoryInspectionUI</c>. No pide
    /// referencias serializadas: el builder lo monta con su Image y su CanvasGroup al lado.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class BackroomsInventoryBackdrop : CharacterUIBehaviour
    {
        private CanvasGroup _group;
        private IInventoryInspectionManagerCC _inspection;

        protected override void Awake()
        {
            base.Awake();
            _group = GetComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            _inspection = character.GetCC<IInventoryInspectionManagerCC>();
            if (_inspection == null) return;
            _inspection.InspectionStarted += Show;
            _inspection.InspectionEnded += Hide;
            if (_inspection.IsInspecting) Show();
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (_inspection == null) return;
            _inspection.InspectionStarted -= Show;
            _inspection.InspectionEnded -= Hide;
            _inspection = null;
            Hide();
        }

        private void Show() => _group.alpha = 1f;
        private void Hide() => _group.alpha = 0f;
    }
}
