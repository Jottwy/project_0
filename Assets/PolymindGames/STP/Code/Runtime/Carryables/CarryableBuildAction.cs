using PolymindGames.BuildingSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(CarryablePickup))]
    public sealed class CarryableBuildAction : MonoBehaviour, ICarryableAction
    {
        [SerializeField]
        [DataReference(NullElement = "", HasAssetReference = true)]
        private DataIdReference<BuildMaterialDefinition> _buildMaterial;

        [SerializeField, Range(1, 10)]
        private int _amount = 1;

        public bool CanDoAction(ICharacter character)
        {
            return character.TryGetCC(out IConstructableBuilderCC builder) && builder.CurrentConstructable != null;
        }

        public bool TryDoAction(ICharacter character)
        {
            var constructableBuilder = character.GetCC<IConstructableBuilderCC>();
            var constructable = constructableBuilder?.CurrentConstructable;
            if (constructable == null)
                return false;

            bool added = false;
            for (int i = 0; i < _amount; i++)
            {
                if (constructableBuilder.TryAddMaterial(_buildMaterial))
                    added = true;
            }

            return added;
        }
    }
}