using PolymindGames.BuildingSystem;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [AddComponentMenu("Polymind Games/User Interface/Slots/Building Piece Slot")]
    public sealed class BuildingPieceDefinitionUI : DataSlotUI<BuildingPieceDefinition>
    {
        [SerializeField]
        [Tooltip("UI element to display the building piece's name.")]
        private TextMeshProUGUI _nameText;

        [SerializeField]
        [Tooltip("UI element to display the building piece's description.")]
        private TextMeshProUGUI _descriptionText;

        [SerializeField]
        [Tooltip("UI element to display the building piece's icon.")]
        private Image _iconImage;

        [SerializeField]
        [Tooltip("Root GameObject for displaying build requirements.")]
        [NotNull]
        private GameObject _requirementsRoot;

        [SerializeField]
        [Tooltip("List of UI elements for displaying individual build requirements.")]
        [ReorderableList(ListStyle.Lined, HasLabels = false)]
        private RequirementUI[] _requirements;

        /// <summary>
        /// Updates the UI elements to reflect the provided building piece definition.
        /// </summary>
        /// <param name="definition">The building piece definition to display.</param>
        protected override void UpdateUI(BuildingPieceDefinition definition)
        {
            if (ValidateData(definition))
            {
                if (_nameText != null)
                    _nameText.text = definition.Name;

                if (_descriptionText != null)
                    _descriptionText.text = definition.Description;

                if (_iconImage != null)
                    _iconImage.sprite = definition.Icon;

                if (_requirementsRoot != null && _requirements != null)
                {
                    var buildRequirements = definition.Prefab.Constructable.GetBuildRequirements();

                    for (int i = 0; i < _requirements.Length; i++)
                    {
                        if (i >= buildRequirements.Count)
                        {
                            _requirements[i]?.gameObject.SetActive(false);
                            continue;
                        }

                        var requirement = buildRequirements[i];
                        _requirements[i]?.gameObject.SetActive(true);
                        if (requirement.BuildMaterial != null)
                        {
                            _requirements[i]?.SetIconAndAmount(requirement.BuildMaterial.Icon, $"x{requirement.RequiredAmount}");
                        }
                    }

                    _requirementsRoot.SetActive(true);
                }
            }
            else
            {
                ClearUI();
            }
        }

        /// <summary>
        /// Validates the building piece definition data.
        /// </summary>
        /// <param name="data">The data to validate.</param>
        /// <returns>True if the data is valid; otherwise, false.</returns>
        private static bool ValidateData(BuildingPieceDefinition data)
        {
            if (data == null)
                return false;

            if (data.Prefab == null)
            {
                Debug.LogError($"Building piece definition '{data.Name}' does not have a prefab specified.", data);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Clears the UI when data is invalid or unavailable.
        /// </summary>
        private void ClearUI()
        {
            if (_nameText != null)
                _nameText.text = string.Empty;

            if (_descriptionText != null)
                _descriptionText.text = string.Empty;

            if (_requirementsRoot != null)
                _requirementsRoot.SetActive(false);
        }
    }
}