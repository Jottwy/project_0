using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    [RequireCharacterComponent(typeof(IWieldablesControllerCC))]
    public sealed class WieldableRetractionHandler : CharacterBehaviour, IWieldableRetractionHandlerCC
    {
        [SerializeField]
        [Tooltip("The transform used in ray-casting.")]
        private Transform _view;

        [SerializeField, Title("Settings")]
        private LayerMask _layerMask = LayerConstants.SimpleSolidObjectsMask;
        
        [SerializeField, Range(0.01f, 5f)]
        [Tooltip("The max detection distance, anything out of range will be ignored.")]
        private float _castDistance = 1f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("If set to a value larger than 0, the detection method will be set to SphereCast instead of Raycast.")]
        private float _castRadius = 0.04f;

        [SerializeField, Range(0.01f, 5f)]
        [Tooltip("If an object gets detected an it's closer than this distance, the ability to use/aim wieldables will be blocked.")]
        private float _blockDistance = 0.5f;

        private Transform _ignoredRoot;
        private IWieldable _wieldable;
        private bool _isBlocked; 
        
        public float ClosestObjectDistance { get; private set; }

        protected override void OnBehaviourStart(ICharacter character)
        {
            base.OnBehaviourStart(character);
            _ignoredRoot = character.transform;
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            var controller = character.GetCC<IWieldablesControllerCC>();
            controller.EquippingStarted += OnEquippingStarted;
            _wieldable = controller.ActiveWieldable;
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            var controller = character.GetCC<IWieldablesControllerCC>();
            controller.EquippingStarted -= OnEquippingStarted;
        }

        private void OnEquippingStarted(IWieldable wieldable)
        {
            BlockWieldableActions(false);
            _wieldable = wieldable;
        }

        private void FixedUpdate()
        {
            if (_wieldable == null)
                return;

            Ray ray = new(_view.position, _view.forward);

            ClosestObjectDistance = _castRadius > 0.001f
                ? PhysicsUtility.SphereCastOptimizedClosestDistance(ray, _castRadius, _castDistance, _layerMask, _ignoredRoot)
                : PhysicsUtility.RaycastOptimizedClosestDistance(ray, _castDistance, _layerMask, _ignoredRoot);

            BlockWieldableActions(ClosestObjectDistance < _blockDistance);
        }

        private void BlockWieldableActions(bool block)
        {
            if (_isBlocked == block || _wieldable == null)
                return;

            if (block)
            {
                if (_wieldable is IUseInputHandler useInput)
                    useInput.UseBlocker.AddBlocker(this);

                if (_wieldable is IAimInputHandler aimInput)
                    aimInput.AimBlocker.AddBlocker(this);
            }
            else
            {
                if (_wieldable is IUseInputHandler useInput)
                    useInput.UseBlocker.RemoveBlocker(this);

                if (_wieldable is IAimInputHandler aimInput)
                    aimInput.AimBlocker.RemoveBlocker(this);
            }

            _isBlocked = block;
        }
    }
}