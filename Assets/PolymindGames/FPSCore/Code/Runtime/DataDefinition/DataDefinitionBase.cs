using UnityEngine;

namespace PolymindGames
{
    public abstract class DataDefinition : ScriptableObject, IEditorValidate
    {
        public abstract int Id { get; }
        public abstract string Name { get; set; }
        public virtual string FullName => Name;
        public virtual string Description => string.Empty;
        public virtual Sprite Icon => null;
        public virtual Color Color => Color.white;
        
        #region Editor
#if UNITY_EDITOR
        public readonly struct ValidationContext
        {
            public readonly bool IsFromToolsWindow;
            public readonly ValidationTrigger Trigger;

            public ValidationContext(bool isFromToolsWindow, ValidationTrigger trigger)
            {
                IsFromToolsWindow = isFromToolsWindow;
                Trigger = trigger;
            }
        }
        
        public enum ValidationTrigger
        {
            Created,
            Duplicated,
            Refresh
        }

        public abstract void Validate_EditorOnly(in ValidationContext validationContext);

        public void ValidateInEditor()
        {
            var context = new ValidationContext(true, ValidationTrigger.Refresh);
            Validate_EditorOnly(context);
        }
#endif
        #endregion
    }
}