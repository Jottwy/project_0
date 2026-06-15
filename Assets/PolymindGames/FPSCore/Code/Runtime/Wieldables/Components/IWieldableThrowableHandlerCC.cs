using PolymindGames.WieldableSystem;
using UnityEngine.Events;

namespace PolymindGames
{
    /// <summary>
    /// Handles throwable wieldable objects.
    /// </summary>
    public interface IWieldableThrowableHandlerCC : ICharacterComponent
    {
        /// <summary> Gets the index of the selected throwable. </summary>
        int SelectedIndex { get; }

        /// <summary> Event triggered when a throwable is thrown. </summary>
        event UnityAction<Throwable> OnThrow;

        /// <summary> Event triggered when the count of throwables changes. </summary>
        event UnityAction ThrowableCountChanged;

        /// <summary> Event triggered when the index of the selected throwable changes. </summary>
        event UnityAction ThrowableIndexChanged;

        /// <summary> Tries to throw the selected throwable. </summary>
        bool TryThrow();

        /// <summary> Selects the next throwable. </summary>
        void SelectNext(bool next);

        /// <summary> Gets the count of throwables at the specified index. </summary>
        int GetThrowableCountAtIndex(int index);

        /// <summary> Gets the throwable at the specified index. </summary>
        Throwable GetThrowableAtIndex(int index);
    }

}
