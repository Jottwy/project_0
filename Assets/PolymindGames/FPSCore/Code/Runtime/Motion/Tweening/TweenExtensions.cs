using System.Runtime.CompilerServices;
using UnityEngine.UI;
using UnityEngine;
using System;
using Object = UnityEngine.Object;

namespace PolymindGames.ProceduralMotion
{
    public static partial class TweenExtensions
    {
        /// <summary>
        /// Stops and removes all tweens associated with the specified object.
        /// </summary>
        /// <param name="parent">The object whose tweens should be cleared.</param>
        /// <param name="behavior">
        /// Determines how the tween values should be reset before removal.
        /// - <see cref="TweenResetBehavior.KeepCurrentValue"/>: Leaves the value as-is.
        /// - <see cref="TweenResetBehavior.ResetToStartValue"/>: Resets to the starting value.
        /// - <see cref="TweenResetBehavior.ResetToEndValue"/>: Jumps to the final value.
        /// </param>
        /// <returns>Returns <c>true</c> if the operation was successful.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ClearTweens(this Object parent, TweenResetBehavior behavior = TweenResetBehavior.KeepCurrentValue)
        {
            TweenManager.Instance.StopAndClearTweensForParent(parent, behavior);
            return true;
        }

        /// <summary>
        /// Checks if the specified object has any active tweens running.
        /// </summary>
        /// <param name="parent">The object to check for active tweens.</param>
        /// <returns>Returns <c>true</c> if there are active tweens, otherwise <c>false</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasActiveTweens(this Object parent)
            => TweenManager.Instance.HasTweensForParent(parent);

        /// <summary>
        /// Tweens the anchored position of a RectTransform.
        /// </summary>
        public static Tween<Vector2> TweenAnchoredPosition(this RectTransform self, Vector2 to, float duration) =>
            Tween(self.anchoredPosition, to, duration, value => self.anchoredPosition = value).AttachTo(self);

        /// <summary>
        /// Tweens the anchored scale (sizeDelta) of a RectTransform.
        /// </summary>
        public static Tween<Vector2> TweenAnchoredScale(this RectTransform self, Vector2 to, float duration) =>
            Tween(self.sizeDelta, to, duration, value => self.sizeDelta = value).AttachTo(self);

        /// <summary>
        /// Tweens the alpha value of a CanvasGroup.
        /// </summary>
        public static Tween<float> TweenCanvasGroupAlpha(this CanvasGroup self, float to, float duration)
            => Tween(self.alpha, to, duration, value => self.alpha = value).AttachTo(self);

        /// <summary>
        /// Tweens the alpha value of a CanvasRenderer.
        /// </summary>
        public static Tween<float> TweenCanvasRendererAlpha(this CanvasRenderer self, float to, float duration) =>
            Tween(self.GetAlpha(), to, duration, self.SetAlpha).AttachTo(self);

        /// <summary>
        /// Tweens the alpha value of a Graphic (such as Image, Text).
        /// </summary>
        public static Tween<float> TweenGraphicAlpha(this Graphic self, float to, float duration) =>
            Tween(self.color.a, to, duration, value =>
            {
                Color color = self.color;
                color.a = value;
                self.color = color;
            }).AttachTo(self);

        /// <summary>
        /// Tweens the color of a Graphic.
        /// </summary>
        public static Tween<Color> TweenGraphicColor(this Graphic self, Color to, float duration) =>
            Tween(self.color, to, duration, value => self.color = value).AttachTo(self);

        /// <summary>
        /// Tweens the local position of a Transform.
        /// </summary>
        public static Tween<Vector3> TweenLocalPosition(this Transform self, Vector3 to, float duration) =>
            Tween(self.localPosition, to, duration, value => self.localPosition = value).AttachTo(self);

        /// <summary>
        /// Tweens the local rotation of a Transform using Euler angles.
        /// </summary>
        public static Tween<Vector3> TweenLocalRotation(this Transform self, Vector3 to, float duration) =>
            Tween(self.localEulerAngles, to, duration, value => self.localEulerAngles = value).AttachTo(self);

        /// <summary>
        /// Tweens the local rotation of a Transform using a Quaternion.
        /// </summary>
        public static Tween<Quaternion> TweenLocalRotation(this Transform self, Quaternion to, float duration) =>
            Tween(self.localRotation, to, duration, value => self.localRotation = value).AttachTo(self);

        /// <summary>
        /// Tweens the local scale of a Transform.
        /// </summary>
        public static Tween<Vector3> TweenLocalScale(this Transform self, Vector3 to, float duration) =>
            Tween(self.localScale, to, duration, value => self.localScale = value).AttachTo(self);

        /// <summary>
        /// Tweens the position of a Transform.
        /// </summary>
        public static Tween<Vector3> TweenPosition(this Transform self, Vector3 to, float duration) =>
            Tween(self.position, to, duration, value => self.position = value).AttachTo(self);

        /// <summary>
        /// Tweens the color of a Material.
        /// </summary>
        public static Tween<Color> TweenMaterialColor(this Material self, Color to, float duration) =>
            Tween(self.color, to, duration, value => self.color = value).AttachTo(self);

        /// <summary>
        /// Tweens the rotation of a Transform using Euler angles.
        /// </summary>
        public static Tween<Vector3> TweenRotation(this Transform self, Vector3 to, float duration) =>
            Tween(self.eulerAngles, to, duration, value => self.eulerAngles = value).AttachTo(self);

        /// <summary>
        /// Tweens the rotation of a Transform using a Quaternion.
        /// </summary>
        public static Tween<Quaternion> TweenRotation(this Transform self, Quaternion to, float duration) =>
            Tween(self.rotation, to, duration, value => self.rotation = value).AttachTo(self);

        /// <summary>
        /// Tweens the alpha value of a SpriteRenderer.
        /// </summary>
        public static Tween<float> TweenSpriteRendererAlpha(this SpriteRenderer self, float to, float duration) =>
            Tween(self.color.a, to, duration, value =>
            {
                Color color = self.color;
                color.a = value;
                self.color = color;
            }).AttachTo(self);

        /// <summary>
        /// Tweens the color of a SpriteRenderer.
        /// </summary>
        public static Tween<Color> TweenSpriteRendererColor(this SpriteRenderer self, Color to, float duration) =>
            Tween(self.color, to, duration, value => self.color = value).AttachTo(self);

        /// <summary>
        /// Helper method to create a tween.
        /// </summary>
        public static Tween<T> Tween<T>(this T start, T end, float duration, Action<T> onUpdate) where T : struct
        {
            return TweenManager.GetTween<T>()
                .SetStartValue(start)
                .SetEndValue(end)
                .SetDuration(duration)
                .OnUpdate(onUpdate)
                .Start();
        }
    }
}