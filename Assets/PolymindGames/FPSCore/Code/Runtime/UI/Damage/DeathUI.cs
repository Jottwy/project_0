using System.Collections;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class DeathUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        private TextMeshProUGUI _respawnTimeText;

        [SerializeField, NotNull]
        private SelectableButton _respawnButton;

        [SerializeField, Range(0f, 50f)]
        private float _enableRespawnDelay = 7f;

        private int _effectId;
        
        private const float MaxRespawnWait = 15f;
        
        protected override void OnCharacterAttached(ICharacter character)
        {
            _respawnButton.Clicked += RespawnPlayer;
            character.HealthManager.Death += OnPlayerDeath;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            _respawnButton.Clicked -= RespawnPlayer;
            character.HealthManager.Death -= OnPlayerDeath;
        }

        protected override void Awake()
        {
            base.Awake();
            _respawnButton.gameObject.SetActive(false);
            _respawnButton.IsInteractable = false;
            _respawnTimeText.enabled = false;
        }

        private void OnPlayerDeath(in DamageArgs args) => StartCoroutine(RespawnRoutine());

        private IEnumerator RespawnRoutine()
        {
            EnableButton(true);

            float endTime = Time.time + _enableRespawnDelay;
            while (endTime >= Time.time)
            {
                float timeLeft = endTime - Time.time;
                _respawnTimeText.text = timeLeft.ToString("0.0");
                yield return null;
            }

            _respawnTimeText.text = "Respawn";
            _respawnButton.IsInteractable = true;

            endTime = Time.time + MaxRespawnWait;
            while (endTime >= Time.time)
            {
                if (_respawnButton.IsInteractable)
                    yield return null;
                else
                    yield break;
            }

            RespawnPlayer(null);
        }

        private void RespawnPlayer(SelectableButton buttonSelectable)
        {
            Character.HealthManager.ResetHealth();
            EnableButton(false);
        }

        private void EnableButton(bool enable)
        {
            _respawnButton.gameObject.SetActive(enable);
            _respawnButton.IsInteractable = false;
            _respawnTimeText.enabled = enable;
        }
    }
}