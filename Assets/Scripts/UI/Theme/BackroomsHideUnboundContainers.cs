using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Esconde los paneles de contenedor de la variante que el jugador no tiene (Joel, 2026-09-13: «ocultar las secciones
    /// sin contenedor»). En <c>STP_Showcase</c> el jugador es <c>STP_Player</c> con sus 6 contenedores, así que la
    /// espalda, la cintura, encima, guantes, cara y la sección de la mochila no tienen nada detrás. Un panel escondido
    /// no ocupa sitio: <see cref="BackroomsInventorySections"/> se salta la sección cuya rejilla está apagada.
    ///
    /// Solo mira los nombres de <see cref="_names"/>, no cualquier <c>ItemContainerUI</c>: el panel de Alrededor también
    /// es un <c>ItemContainerUI</c> y se ata a contenedores ajenos al jugador. Los contenedores de <c>Inventory</c> se
    /// crean una sola vez en su <c>Start</c> (sin alta ni baja en runtime), así que basta con decidir una vez.
    /// </summary>
    public sealed class BackroomsHideUnboundContainers : CharacterUIBehaviour
    {
        /// <summary>Los contenedores que la variante añade por los prototipos y el vendor no trae.</summary>
        public static readonly string[] PrototypeContainers =
            { "Back", "BackStorage", "Waist", "Outer", "GloveL", "GloveR", "Face", "OuterPockets", "LegsPockets" };

        [SerializeField]
        private string[] _names = PrototypeContainers;

        private IInventory _inventory;
        private bool _applied;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _inventory = character.Inventory;
            _applied = false;
        }

        protected override void OnCharacterDetached(ICharacter character) => _inventory = null;

        private void LateUpdate()
        {
            if (_applied || _inventory?.Containers == null || _inventory.Containers.Count == 0) return;
            _applied = true;

            int hidden = 0;
            foreach (var ui in GetComponentsInChildren<ItemContainerUI>(true))
            {
                if (!ShouldHide(ui.ContainerName, _names, name => _inventory.FindContainer(ItemContainerFilters.WithName(name)) != null))
                    continue;
                ui.gameObject.SetActive(false);
                hidden++;
            }
            if (hidden > 0) Debug.Log($"[ShowcaseUI] {hidden} paneles sin contenedor escondidos.");
        }

        /// <summary>Se esconde si es uno de <paramref name="names"/> y el inventario no lo tiene. Pura, con test.</summary>
        public static bool ShouldHide(string containerName, string[] names, System.Func<string, bool> inventoryHas)
        {
            if (string.IsNullOrEmpty(containerName) || System.Array.IndexOf(names, containerName) < 0) return false;
            return !inventoryHas(containerName);
        }
    }
}
