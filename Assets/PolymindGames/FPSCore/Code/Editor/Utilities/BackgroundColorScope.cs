using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    /// <summary>
    /// A disposable struct that temporarily changes the GUI background color.
    /// Restores the previous background color when disposed.
    /// </summary>
    public readonly struct BackgroundColorScope : IDisposable
    {
        private readonly Color _oldBackgroundColor;
        private readonly bool _isValid;

        public BackgroundColorScope(Color newBackgroundColor)
        {
            _oldBackgroundColor = GUI.backgroundColor;
            GUI.backgroundColor = newBackgroundColor;
            _isValid = true;
        }

        public void Dispose()
        {
            if (_isValid) GUI.backgroundColor = _oldBackgroundColor;
        }
    }
}