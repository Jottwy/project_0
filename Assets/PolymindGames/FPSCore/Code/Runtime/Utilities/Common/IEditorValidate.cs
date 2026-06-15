namespace PolymindGames
{
    public interface IEditorValidate
    {
#if UNITY_EDITOR
        void ValidateInEditor();
#endif
    }
}