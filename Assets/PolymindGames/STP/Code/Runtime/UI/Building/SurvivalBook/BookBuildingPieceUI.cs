using PolymindGames.BuildingSystem;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class BookBuildingPieceUI : CharacterUIBehaviour
    {
        [SerializeField, DataReference(NullElement = "")]
        private DataIdReference<BuildingPieceCategoryDefinition> _buildingPieceCategory;

        [SerializeField, Title("Template")]
        private BuildingPieceDefinitionUI _template;

        [SerializeField]
        private RectTransform _templateSpawnRect;

        [SerializeField, NotNull, Title("Display")]
        private TextMeshProUGUI _nameTxt;

        [SerializeField, NotNull]
        private Image _categoryImg;
        
        private BuildingPieceDefinitionUI[] _slots;

        protected override void Awake()
        {
            base.Awake();
            CreateSlots();
        } 

        private void StartBuilding(SelectableButton buttonSelectable)
        {
            if (Character == null)
            {
                Debug.LogWarning("This behaviour is not attached to a character.", gameObject);
                return;
            }

            var buildingPieceDef = buttonSelectable.GetComponent<BuildingPieceDefinitionUI>().Data;
            var buildingPiece = Instantiate(buildingPieceDef.Prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity);
            Character.GetCC<IBuildControllerCC>().SetBuildingPiece(buildingPiece);
            Character.GetCC<IWieldablesControllerCC>().TryEquipWieldable(null);
        }

        private void CreateSlots()
        {
            bool wasTemplateActive = _templateSpawnRect.gameObject.activeInHierarchy;
            _templateSpawnRect.gameObject.SetActive(true);

            var buildingPieceDefs = _buildingPieceCategory.Def.Members;
            _slots = new BuildingPieceDefinitionUI[buildingPieceDefs.Length];
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = Instantiate(_template, _templateSpawnRect);
                _slots[i].SetData(buildingPieceDefs[i]);
                _slots[i].Selectable.Clicked += StartBuilding;
            }

            if (!wasTemplateActive)
                _templateSpawnRect.gameObject.SetActive(false);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            UnityUtility.SafeOnValidate(this, () =>
            {
                if (_categoryImg != null)
                    _categoryImg.sprite = _buildingPieceCategory.Icon;

                if (_nameTxt != null)
                    _nameTxt.text = _buildingPieceCategory.Name;
            });
        }
#endif
    }
}