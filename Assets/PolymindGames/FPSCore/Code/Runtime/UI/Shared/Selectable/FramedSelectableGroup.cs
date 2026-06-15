using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [AddComponentMenu("Polymind Games/User Interface/Selectables/Framed Selectable Group")]
    public class FramedSelectableGroup : SelectableGroup
    {
        [Title("Frame Settings")]
        [SerializeField, SceneObjectOnly, NotNull]
        private RectTransform _selectionFrame;

        [SerializeField]
        private Vector2 _selectionOffset = Vector2.zero;

        [SerializeField]
        private FrameSelectionMatchType _selectionMatchFlags;

        protected override void OnSelectedChanged(SelectableButton buttonSelectable)
        {
            if (buttonSelectable == null)
            {
                _selectionFrame.gameObject.SetActive(false);
                return;
            }

            _selectionFrame.gameObject.SetActive(true);
            _selectionFrame.SetParent(buttonSelectable.transform);
            _selectionFrame.anchoredPosition = _selectionOffset;
            _selectionFrame.localRotation = Quaternion.identity;
            _selectionFrame.localScale = Vector3.one;
            var localPos = _selectionFrame.localPosition;
            _selectionFrame.localPosition = new Vector3(localPos.x, localPos.y, 0f);

            bool matchXSize = (_selectionMatchFlags & FrameSelectionMatchType.MatchXSize) == FrameSelectionMatchType.MatchXSize;
            bool matchYSize = (_selectionMatchFlags & FrameSelectionMatchType.MatchYSize) == FrameSelectionMatchType.MatchYSize;

            if (matchXSize || matchYSize)
            {
                Vector2 frameSize = _selectionFrame.sizeDelta;
                Vector2 selectableSize = ((RectTransform)buttonSelectable.transform).sizeDelta;
                _selectionFrame.sizeDelta = new Vector2(matchXSize ? selectableSize.x : frameSize.x, matchYSize ? selectableSize.y : frameSize.y);
            }

            bool matchColor = (_selectionMatchFlags & FrameSelectionMatchType.MatchColor) == FrameSelectionMatchType.MatchColor;

            if (matchColor && _selectionFrame.TryGetComponent<Graphic>(out var frameGraphic))
                frameGraphic.color = buttonSelectable.GetComponent<Graphic>().color;
        }

        #region Internal Types
        [Flags]
        private enum FrameSelectionMatchType
        {
            MatchColor = 1,
            MatchXSize = 2,
            MatchYSize = 4
        }
        #endregion
    }
}