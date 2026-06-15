using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class HealsUI : CharacterUIBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _healsCountText;

        protected override void OnCharacterAttached(ICharacter character)
        {
            if (character.TryGetCC<IWieldableHealingHandlerCC>(out var healing))
            {
                healing.HealsCountChanged += OnHealsCountChanged;
                OnHealsCountChanged(healing.HealsCount);
            }
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (character.TryGetCC<IWieldableHealingHandlerCC>(out var healing))
                healing.HealsCountChanged -= OnHealsCountChanged;
        }

        private void OnHealsCountChanged(int count) => _healsCountText.text = count.ToString();
    }
}