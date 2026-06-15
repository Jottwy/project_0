using PolymindGames.SurfaceSystem;
using JetBrains.Annotations;

namespace PolymindGames.Editor
{
    [UsedImplicitly]
    public sealed class SurfacesPage : GenericDataDefinitionPage<SurfaceDefinition>
    {
        public override string DisplayName => "Surfaces";
        public override int Order => 10;
    }
}