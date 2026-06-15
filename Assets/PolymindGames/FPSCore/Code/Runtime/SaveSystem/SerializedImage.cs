using UnityEngine;
using System;

namespace PolymindGames.SaveSystem
{
    [Serializable]
    public sealed class SerializedImage : ISerializationCallbackReceiver
    {
        [SerializeField]
        private byte[] _imageData;

        [SerializeField]
        private int _imageWidth;
        
        [SerializeField]
        private int _imageHeight;
        
        private Texture2D _image;

        public SerializedImage(Texture2D image)
        {
            _image = image;
            _imageData = image.EncodeToJPG();
            _imageWidth = image.width;
            _imageHeight = image.height;
        }

        public Texture2D Image => _image;
        
        public static implicit operator Texture2D(SerializedImage image) => image.Image;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { } 

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (_imageData == null)
                return;
            
            _image = new Texture2D(_imageWidth, _imageHeight, TextureFormat.RGB24, true);
            _image.LoadImage(_imageData);
            _image.Apply(true);
            _imageData = null;
        }
    }
}