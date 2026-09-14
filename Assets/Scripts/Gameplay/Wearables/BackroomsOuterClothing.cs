using System;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// ADR-149 R4a/R4b: la ropa de encima (<c>Outer</c>) se ve en el muñeco. El <see cref="CharacterClothing"/> del vendor solo
    /// mira cabeza, torso, piernas y pies y pinta UNA prenda por parte del cuerpo; la de encima va aparte: se enciende su
    /// renderer además de lo que haya en el torso (Joel, 2026-09-14: por un agujero de la chaqueta se ve la camiseta). El
    /// material de encima se hincha unos milímetros para no pisar la de debajo. La máscara de piel la junta
    /// <see cref="BackroomsGarmentDamageVisuals"/>.
    /// </summary>
    public sealed class BackroomsOuterClothing : CharacterUIBehaviour
    {
        [SerializeField] private string _outerContainer = "Outer";

        [Tooltip("Renderers de las prendas de encima; el mismo índice en _outerIds dice qué prenda es (los rellena el builder).")]
        [SerializeField] private SkinnedMeshRenderer[] _outerRenderers = Array.Empty<SkinnedMeshRenderer>();
        [SerializeField] private int[] _outerIds = Array.Empty<int>();

        private IItemContainer _outer;
        private int _last = int.MinValue;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _outer = character.Inventory?.FindContainer(ItemContainerFilters.WithName(_outerContainer));
            _last = int.MinValue;
        }

        protected override void OnCharacterDetached(ICharacter character) => _outer = null;

        private void OnEnable() => _last = int.MinValue;

        private void LateUpdate()
        {
            if (_outer == null) return;
            int id = 0;
            if (_outer.SlotsCount > 0)
            {
                var stack = _outer.GetItemAtIndex(0);
                if (stack.HasItem()) id = stack.Item.Id;
            }
            if (id == _last) return;
            _last = id;
            int show = OuterIndex(id, _outerIds);
            for (int i = 0; i < _outerRenderers.Length; i++)
                if (_outerRenderers[i] != null && _outerRenderers[i].gameObject.activeSelf != (i == show))
                    _outerRenderers[i].gameObject.SetActive(i == show);
        }

        /// <summary>Qué renderer de encima se enciende para esa prenda, o -1 (nada puesto o sin malla).</summary>
        public static int OuterIndex(int outerId, int[] outerIdsWithMesh)
            => outerId == 0 ? -1 : Array.IndexOf(outerIdsWithMesh, outerId);
    }
}
