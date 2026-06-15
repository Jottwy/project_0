using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Layout Group controller that arranges children in rows, fitting as many on a line until total width exceeds parent bounds
    /// Based on: https://github.com/StompyRobot/SRF/blob/master/Scripts/UI/Layout/FlowLayoutGroup.cs
    /// </summary>
    public class FlowLayoutGroup : LayoutGroup
    {
        [SerializeField]
        private float _spacing = 0f;

        [SerializeField]
        private bool _childForceExpandWidth = false;
        
        [SerializeField]
        private bool _childForceExpandHeight = false;

        private float _layoutHeight;
        

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();

            float minWidth = GetGreatestMinimumChildWidth() + padding.left + padding.right;

            SetLayoutInputForAxis(minWidth, -1, -1, 0);

        }

        public override void SetLayoutHorizontal()
        {
            SetLayout(rectTransform.rect.width, 0, false);
        }

        public override void SetLayoutVertical()
        {
            SetLayout(rectTransform.rect.width, 1, false);
        }

        public override void CalculateLayoutInputVertical()
        {
            _layoutHeight = SetLayout(rectTransform.rect.width, 1, true);
        }

        protected bool IsCenterAlign =>
            childAlignment == TextAnchor.LowerCenter ||
            childAlignment == TextAnchor.MiddleCenter ||
            childAlignment == TextAnchor.UpperCenter;

        protected bool IsRightAlign =>
            childAlignment == TextAnchor.LowerRight ||
            childAlignment == TextAnchor.MiddleRight ||
            childAlignment == TextAnchor.UpperRight;

        protected bool IsMiddleAlign =>
            childAlignment == TextAnchor.MiddleLeft ||
            childAlignment == TextAnchor.MiddleRight ||
            childAlignment == TextAnchor.MiddleCenter;

        protected bool IsLowerAlign =>
            childAlignment == TextAnchor.LowerLeft ||
            childAlignment == TextAnchor.LowerRight ||
            childAlignment == TextAnchor.LowerCenter;

        /// <summary>
        /// Holds the rects that will make up the current row being processed
        /// </summary>
        private readonly IList<RectTransform> _rowList = new List<RectTransform>();

        /// <summary>
        /// Main layout method
        /// </summary>
        /// <param name="width">Width to calculate the layout with</param>
        /// <param name="axis">0 for horizontal axis, 1 for vertical</param>
        /// <param name="layoutInput">If true, sets the layout input for the axis. If false, sets child position for axis</param>
        public float SetLayout(float width, int axis, bool layoutInput)
        {
            float groupHeight = rectTransform.rect.height;

            // Width that is available after padding is subtracted
            float workingWidth = rectTransform.rect.width - padding.left - padding.right;

            // Accumulates the total height of the rows, including spacing and padding.
            float yOffset = IsLowerAlign ? padding.bottom : (float)padding.top;

            float currentRowWidth = 0f;
            float currentRowHeight = 0f;

            for (var i = 0; i < rectChildren.Count; i++)
            {
                // LowerAlign works from back to front
                int index = IsLowerAlign ? rectChildren.Count - 1 - i : i;

                var child = rectChildren[index];

                float childWidth = LayoutUtility.GetPreferredSize(child, 0);
                float childHeight = LayoutUtility.GetPreferredSize(child, 1);

                // Max child width is layout group with - padding
                childWidth = Mathf.Min(childWidth, workingWidth);

                // Apply spacing if not the first element in a row
                if (_rowList.Count > 0)
                    currentRowWidth += _spacing;

                // If adding this element would exceed the bounds of the row,
                // go to a new line after processing the current row
                if (currentRowWidth + childWidth > workingWidth)
                {

                    // Undo spacing addition if we're moving to a new line (Spacing is not applied on edges)
                    currentRowWidth -= _spacing;

                    // Process current row elements positioning
                    if (!layoutInput)
                    { 
                        float h = CalculateRowVerticalOffset(groupHeight, yOffset, currentRowHeight);
                        LayoutRow(_rowList, currentRowWidth, currentRowHeight, workingWidth, padding.left, h, axis);
                    }

                    // Clear existing row
                    _rowList.Clear();

                    // Add the current row height to total height accumulator, and reset to 0 for the next row
                    yOffset += currentRowHeight;
                    yOffset += _spacing;

                    currentRowHeight = 0;
                    currentRowWidth = 0;

                }

                currentRowWidth += childWidth;
                _rowList.Add(child);

                // We need the largest element height to determine the starting position of the next line
                if (childHeight > currentRowHeight)
                {
                    currentRowHeight = childHeight;
                }
            }

            if (!layoutInput)
            {
                float h = CalculateRowVerticalOffset(groupHeight, yOffset, currentRowHeight);

                // Layout the final row
                LayoutRow(_rowList, currentRowWidth, currentRowHeight, workingWidth, padding.left, h, axis);
            }

            _rowList.Clear();

            // Add the last rows height to the height accumulator
            yOffset += currentRowHeight;
            yOffset += IsLowerAlign ? padding.top : padding.bottom;

            if (layoutInput)
            {
                if (axis == 1)
                    SetLayoutInputForAxis(yOffset, yOffset, -1, axis);
            }

            return yOffset;
        }

        private float CalculateRowVerticalOffset(float groupHeight, float yOffset, float currentRowHeight)
        {
            float h;

            if (IsLowerAlign)
            {
                h = groupHeight - yOffset - currentRowHeight;
            }
            else if (IsMiddleAlign)
            {
                h = groupHeight * 0.5f - _layoutHeight * 0.5f + yOffset;
            }
            else
            {
                h = yOffset;
            }
            
            return h;
        }

        protected void LayoutRow(IList<RectTransform> contents, float rowWidth, float rowHeight, float maxWidth, float xOffset, float yOffset, int axis)
        {
            float xPos = xOffset;

            if (!_childForceExpandWidth && IsCenterAlign)
                xPos += (maxWidth - rowWidth) * 0.5f;
            else if (!_childForceExpandWidth && IsRightAlign)
                xPos += (maxWidth - rowWidth);

            var extraWidth = 0f;

            if (_childForceExpandWidth)
            {
                var flexibleChildCount = 0;

                foreach (var row in _rowList)
                {
                    if (LayoutUtility.GetFlexibleWidth(row) > 0f)
                        flexibleChildCount++;
                }

                if (flexibleChildCount > 0)
                    extraWidth = (maxWidth - rowWidth) / flexibleChildCount;
            }

            for (var j = 0; j < _rowList.Count; j++)
            {
                int index = IsLowerAlign ? _rowList.Count - 1 - j : j;

                var rowChild = _rowList[index];

                float rowChildWidth = LayoutUtility.GetPreferredSize(rowChild, 0);

                if (LayoutUtility.GetFlexibleWidth(rowChild) > 0f)
                    rowChildWidth += extraWidth;

                var rowChildHeight = LayoutUtility.GetPreferredSize(rowChild, 1);

                if (_childForceExpandHeight)
                    rowChildHeight = rowHeight;

                rowChildWidth = Mathf.Min(rowChildWidth, maxWidth);

                var yPos = yOffset;

                if (IsMiddleAlign)
                    yPos += (rowHeight - rowChildHeight) * 0.5f;
                else if (IsLowerAlign)
                    yPos += (rowHeight - rowChildHeight);

                if (axis == 0)
                    SetChildAlongAxis(rowChild, 0, xPos, rowChildWidth);
                else
                    SetChildAlongAxis(rowChild, 1, yPos, rowChildHeight);

                xPos += rowChildWidth + _spacing;

            }
        }

        public float GetGreatestMinimumChildWidth()
        {
            float max = 0f;

            foreach (var child in rectChildren)
            {
                float w = LayoutUtility.GetMinWidth(child);
                max = Mathf.Max(w, max);
            }

            return max;
        }
    }
}