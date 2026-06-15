using PolymindGames.ProceduralMotion;
using PolymindGames.BuildingSystem;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class BuildMaterialsUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull, Title("Canvas")]
        private UIPanel _panel;
        
        [SerializeField]
        private WorldToUIScreenPositioner _screenPositioner;

        [SerializeField, Title("Build Materials")]
        private RectTransform _buildMaterialsRoot;

        [SerializeField, PrefabObjectOnly]
        private RequirementUI _buildMaterialTemplate;

        [SerializeField, Range(3, 30)]
        private int _cachedBuildMaterialCount = 10;

        [SerializeField, Title("Cancelling")]
        private Image _cancelProgressImg;

        private BuildMaterialElementUI[] _materialElementDisplays;
        private IConstructableBuilderCC _constructableBuilder;
        private Vector3 _constructableCenter;

        /// <summary>
        /// Initializes the build material display manager.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();

            _materialElementDisplays = new BuildMaterialElementUI[_cachedBuildMaterialCount];
            for (int i = 0; i < _cachedBuildMaterialCount; i++)
            {
                _materialElementDisplays[i] = new BuildMaterialElementUI(Instantiate(_buildMaterialTemplate, _buildMaterialsRoot));
                _materialElementDisplays[i].ToggleVisibility(false);
            }

            _cancelProgressImg.fillAmount = 0f;
        }

        /// <summary>
        /// Called when a character is attached to this component.
        /// </summary>
        protected override void OnCharacterAttached(ICharacter character)
        {
            _constructableBuilder = character.GetCC<IConstructableBuilderCC>();
            _constructableBuilder.ConstructableChanged += UpdateCurrentConstructable;
            _constructableBuilder.CancelConstructableProgressChanged += UpdateCancelProgress;
            _constructableBuilder.BuildMaterialAdded += OnBuildMaterialAdded;
        }

        /// <summary>
        /// Called when a character is detached from this component.
        /// </summary>
        protected override void OnCharacterDetached(ICharacter character)
        {
            _constructableBuilder.ConstructableChanged -= UpdateCurrentConstructable;
            _constructableBuilder.CancelConstructableProgressChanged -= UpdateCancelProgress;
            _constructableBuilder.BuildMaterialAdded -= OnBuildMaterialAdded;
        }

        /// <summary>
        /// Updates the display when a build material is added.
        /// </summary>
        /// <param name="buildMaterial">The added build material.</param>
        /// <param name="added"></param>
        private void OnBuildMaterialAdded(BuildMaterialDefinition buildMaterial, int added)
        {
            foreach (var display in _materialElementDisplays)
            {
                if (display.BuildRequirement.BuildMaterial == buildMaterial)
                {
                    display.AddAmount(added);
                    return;
                }
            }
        }

        /// <summary>
        /// Updates the cancel progress UI element.
        /// </summary>
        /// <param name="progress">The progress value to set.</param>
        private void UpdateCancelProgress(float progress) => _cancelProgressImg.fillAmount = progress;

        /// <summary>
        /// Updates the currently active constructable and its build requirements.
        /// </summary>
        /// <param name="constructable">The constructable to update.</param>
        private void UpdateCurrentConstructable(IConstructable constructable)
        {
            if (constructable != null && !constructable.IsConstructed)
            {
                if (_screenPositioner != null)
                    _screenPositioner.SetTargetPosition(constructable.BuildingPiece.GetCenter());
                
                _panel.Show();
                
                UpdateBuildRequirementsDisplay(constructable);
            }
            else
            {
                if (_screenPositioner != null)
                    _screenPositioner.SetTargetPosition(null);
                
                _panel.Hide();
            }
        }

        /// <summary>
        /// Updates the display of build requirements based on the current constructable.
        /// </summary>
        /// <param name="constructable">The constructable to display build requirements for.</param>
        private void UpdateBuildRequirementsDisplay(IConstructable constructable)
        {
            var parentGroup = constructable.BuildingPiece.ParentGroup;
            UpdateMaterialDisplays(parentGroup == null
                ? constructable.GetBuildRequirements()
                : parentGroup.GetAllBuildRequirements());
        }

        /// <summary>
        /// Updates the UI elements for displaying build materials based on the provided build requirements.
        /// </summary>
        /// <param name="buildRequirements"> list of build requirements to display.</param>
        private void UpdateMaterialDisplays(IReadOnlyList<BuildRequirement> buildRequirements)
        {
            for (int i = 0; i < _materialElementDisplays.Length; i++)
            {
                if (i >= buildRequirements.Count)
                {
                    _materialElementDisplays[i].ToggleVisibility(false);
                }
                else
                {
                    _materialElementDisplays[i].ToggleVisibility(true);
                    _materialElementDisplays[i].SetRequirement(buildRequirements[i]);
                }
            }
        }

        #region Internal Types
        private sealed class BuildMaterialElementUI
        {
            private readonly RequirementUI _requirementUI;
            private BuildRequirement _requirement;

            public BuildMaterialElementUI(RequirementUI requirementUI)
            {
                _requirementUI = requirementUI;
            }

            public BuildRequirement BuildRequirement => _requirement;

            /// <summary>
            /// Toggles the visibility of the UI element.
            /// </summary>
            public void ToggleVisibility(bool isVisible)
            {
                _requirementUI.ToggleVisibility(isVisible);
            }

            /// <summary>
            /// Sets the build requirement and updates the UI with the material's icon and current/required amount.
            /// </summary>
            public void SetRequirement(BuildRequirement buildRequirement)
            {
                _requirement = buildRequirement;
                string amountText = $"{buildRequirement.CurrentAmount}/{_requirement.RequiredAmount}";
                _requirementUI.SetIconAndAmount(_requirement.BuildMaterial.Icon, amountText);
            }

            /// <summary>
            /// Updates only the current amount in the UI, leaving other elements unchanged.
            /// </summary>
            public void AddAmount(int amount)
            {
                _requirement.CurrentAmount += (short)amount;
                string amountText = $"{_requirement.CurrentAmount}/{_requirement.RequiredAmount}";
                _requirementUI.SetAmount(amountText);
                
                PlayUpdateAnimation();
            }

            /// <summary>
            /// Plays an animation to indicate a change in the requirement amount.
            /// </summary>
            private void PlayUpdateAnimation()
            {
                const float ScaleFactor = 1.15f;
                var transform = _requirementUI.transform;
                transform.ClearTweens(TweenResetBehavior.ResetToStartValue);
                transform.TweenLocalScale(Vector3.one * ScaleFactor, 0.075f)
                    .SetEasing(EaseType.SineOut)
                    .SetUnscaledTime(true)
                    .SetLoops(0, true);
            }
        }
        #endregion
    }
}