using UnityEngine;

namespace PolymindGames
{
    [SelectionBase]
    [AddComponentMenu("Polymind Games/Characters/Player")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public class HumanCharacter : Character
    {
        [SerializeField]
        private string _name;
        
        [SerializeField, NotNull, Title("Body Points")]
        [Tooltip("The head root of this player (you can think of it as the eyes of the character)")]
        private Transform _headTransform;

        [SerializeField, NotNull]
        [Tooltip("The body root of this player.")]
        private Transform _torsoTransform;

        [SerializeField, NotNull]
        [Tooltip("Feet root of this player.")]
        private Transform _feetTransform;

        [SerializeField, NotNull]
        [Tooltip("Hands root of this player.")]
        private Transform _handsTransform;

        public override string Name
        {
            get => _name;
            set => _name = value;
        }

        public override Transform GetTransformOfBodyPoint(BodyPoint point) => point switch
        {
            BodyPoint.Head => _headTransform,
            BodyPoint.Torso => _torsoTransform,
            BodyPoint.Hands => _handsTransform,
            _ => _feetTransform
        };
    }
}