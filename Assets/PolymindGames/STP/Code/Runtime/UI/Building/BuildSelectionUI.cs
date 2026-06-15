using PolymindGames.ProceduralMotion;
using PolymindGames.BuildingSystem;
using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    public sealed class BuildSelectionUI : CharacterUIBehaviour
    {
        [SerializeField, Title("Canvas")]
        private CanvasGroup _canvasGroup;

        [SerializeField, Range(0f, 5f)]
        private float _alphaLerpDuration = 0.35f;

        [SerializeField, Title("Group Building Pieces")]
        private GameObject _groupPiecesRoot;
        
        [SerializeField, NotNull]
        private Image _previousImg;

        [SerializeField, NotNull]
        private Image _currentImg;

        [SerializeField, NotNull]
        private Image _nextImg;

        private IBuildControllerCC _buildController;

        protected override void Awake()
        {
            base.Awake();
            _canvasGroup.alpha = 0f;
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            _buildController = character.GetCC<IBuildControllerCC>();
            _buildController.BuildingPieceChanged += CurrentBuildingPieceChanged;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            _buildController.BuildingPieceChanged -= CurrentBuildingPieceChanged;
        }

        private void CurrentBuildingPieceChanged(BuildingPiece buildingPiece)
        {
            _canvasGroup.ClearTweens();
            
            if (buildingPiece == null)
            {
                _canvasGroup.TweenCanvasGroupAlpha(0f, _alphaLerpDuration)
                    .SetEasing(EaseType.SineInOut);
            }
            else
            {
                _canvasGroup.TweenCanvasGroupAlpha(1f, _alphaLerpDuration)
                    .SetEasing(EaseType.SineInOut);

                if (buildingPiece is GroupBuildingPiece)
                {
                    _groupPiecesRoot.SetActive(true);

                    var socketBuildingPieces = BuildingPieceDefinition.GroupBuildingPiecesDefinitions;

                    int currentIdx = Array.IndexOf(socketBuildingPieces, buildingPiece.Definition);
                    int previousIdx = (int)Mathf.Repeat(currentIdx - 1, socketBuildingPieces.Length - 1);
                    int nextIdx = (int)Mathf.Repeat(currentIdx + 1, socketBuildingPieces.Length - 1);

                    if (currentIdx != -1)
                    {
                        _currentImg.sprite = socketBuildingPieces[currentIdx].ParentGroup.Icon;
                        _previousImg.sprite = socketBuildingPieces[previousIdx].ParentGroup.Icon;
                        _nextImg.sprite = socketBuildingPieces[nextIdx].ParentGroup.Icon;
                    }
                }
                else
                {
                    _groupPiecesRoot.SetActive(false);
                }
            }
        }
    }
}