using PolymindGames.SaveSystem;
using UnityEngine.UI;
using UnityEngine;
using System;
using TMPro;
using UnityEngine.Serialization;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(SelectableButton))]
    public class SaveFileUI : MonoBehaviour
    {
        [SerializeField, NotNull]
        private GameObject _noDataObject;
        
        [SerializeField, NotNull]
        private RawImage _screenshot;

        [SerializeField, SpaceArea, NotNull]
        private TextMeshProUGUI _saveIndexText;
        
        [SerializeField, NotNull]
        private TextMeshProUGUI _saveTimestampText;

        [FormerlySerializedAs("_mapName")]
        [SerializeField, NotNull]
        private TextMeshProUGUI _levelName;

        private int _saveFileIndex = -1;
        
        [NonSerialized]
        private GameMetadata _metadata;
        
        public SelectableButton ButtonSelectable => GetComponent<SelectableButton>();
        public GameMetadata Metadata => _metadata;

        public int SaveFileIndex
        {
            get => _saveFileIndex;
            set
            {
                _saveFileIndex = value;
                _saveIndexText.text = value.ToString();
            }
        }

        /// <summary>
        /// Sets the save file metadata to display.
        /// </summary>
        public virtual void AttachToMetadata(GameMetadata metadata)
        {
            if (metadata != null)
            {
                _screenshot.texture = metadata.Thumbnail;
                _screenshot.enabled = true;
                _noDataObject.SetActive(false);
                _saveTimestampText.text = $"{metadata.SaveTimestamp.ToShortDateString()} - {metadata.SaveTimestamp.ToShortTimeString()}";
                _levelName.text = metadata.LevelName;
            }
            else
            {
                _screenshot.enabled = false;
                _noDataObject.SetActive(true);
                _saveTimestampText.text = string.Empty;
                _levelName.text = string.Empty;
            }

            _metadata = metadata;
        }
    }
}