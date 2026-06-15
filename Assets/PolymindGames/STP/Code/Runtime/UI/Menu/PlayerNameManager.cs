using System.Collections;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    public sealed class PlayerNameManager : MonoBehaviour
    {
        [SerializeField]
        private UIPanel _panel;

        [SerializeField]
        private TextMeshProUGUI _nameText;

        [SerializeField]
        private TMP_InputField _nameInputField;

        private const string PlayerNamePref = "POLYMIND_PLAYER_NAME";
        private const string UnnamedPlayerName = "Unnamed";

        public static string GetPlayerName()
        {
            string name = PlayerPrefs.GetString(PlayerNamePref);

            if (string.IsNullOrEmpty(name))
                name = UnnamedPlayerName;

            return name;
        }

        public void SavePlayerNameFromField()
        {
            if (string.IsNullOrEmpty(_nameInputField.text))
                return;

            SavePlayerName(_nameInputField.text);
        }

        public void SavePlayerName(string playerName)
        {
            PlayerPrefs.SetString(PlayerNamePref, playerName);
            _nameText.text = playerName;
        }

        public void ResetPlayerNameField()
        {
            _nameInputField.text = PlayerPrefs.GetString(PlayerNamePref);
        }

        private IEnumerator Start()
        {
            yield return null;
            
            if (!PlayerPrefs.HasKey(PlayerNamePref) || IsDefaultName(PlayerPrefs.GetString(PlayerNamePref)))
            {
                SavePlayerName(UnnamedPlayerName);
                _panel.Show();
                _nameInputField.Select();
            }
            else
                ResetUI();
        }

        private static bool IsDefaultName(string playerName) =>
            string.IsNullOrEmpty(playerName) || playerName == UnnamedPlayerName;

        private void ResetUI()
        {
            string text = PlayerPrefs.GetString(PlayerNamePref);
            _nameInputField.text = text;
            _nameText.text = text;
        }
    }
}