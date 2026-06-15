namespace PolymindGames.Editor
{
    public sealed class CarryableCreationWizard : AssetCreationWizard
    {
        public override string ValidateSettings()
        {
            return string.Empty;
        }

        public override void CreateAsset()
        {
        }

        protected override string GetCreationFolderName()
        {
            return "_CARRAYBLE";
        }
    }
}
