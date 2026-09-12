using PolymindGames.SurfaceSystem;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// R5 (2026-09-12) — el sonido de PASOS de un tramo WG3, por <c>segment.style</c>. El collider
    /// de suelo que <see cref="Wg3SceneAssembler.AddColliders"/> monta no llevaba nunca
    /// <c>PhysicsMaterial</c>: el sistema de superficies de PolymindGames (<c>SurfaceManager</c>)
    /// resuelve el sonido del paso por el <c>PhysicsMaterial</c> del collider, así que sin uno todo
    /// el mundo servido sonaba al surface por defecto, oficina y nave por igual.
    /// </summary>
    /// <remarks>
    /// **Sólo dos superficies, no seis.** El papel de <c>fill::style_of</c> tiene seis valores
    /// (oficina, espina, pasillo, nave, servicio, callejón, escalera), pero el catálogo de
    /// <c>SurfaceDefinition</c> del vendor no distingue moqueta de terrazo de hormigón: lo más
    /// parecido a "blando" que hay es <c>FPS_Dirt</c> (la que WG2 ya usa por defecto para
    /// <b>cualquier</b> suelo, `LayerVisualConfig.floorSurfaceName`) y lo más parecido a "duro" es
    /// <c>FPS_Concrete</c>. **No existe una `SurfaceDefinition` de moqueta** — crearla es un asset
    /// nuevo (PhysicsMaterial + definición + clips propios), fuera de lo que toca un cambio de
    /// código. Con esto la oficina ya suena distinta de todo lo demás, que es la mitad audible del
    /// problema; la otra mitad (que "todo lo demás" también se distinga entre sí) espera a que haya
    /// más de dos superficies entre las que elegir.
    ///
    /// Resuelto por NOMBRE (<c>SurfaceDefinition.TryGetWithName</c>), no por ruta de Resources: es
    /// la API que el propio vendor expone para esto, y sobrevive a que alguien reorganice
    /// `Data/Resources/Definitions/Surface/`.
    /// </remarks>
    public static class Wg3StyleSurfaces
    {
        private const string OfficeFloorSurfaceName = "FPS_Dirt";
        private const string HardFloorSurfaceName = "FPS_Concrete";

        private static PhysicsMaterial _officeFloor;
        private static PhysicsMaterial _hardFloor;
        private static bool _resolved;

        /// <summary>El <c>PhysicsMaterial</c> de suelo para este papel de tramo, o <c>null</c> si
        /// el catálogo de superficies del vendor no tiene ninguna de las dos (por ejemplo, un
        /// arnés de test sin `SurfaceDefinition` cargadas) — en ese caso el collider se queda sin
        /// material propio, exactamente el comportamiento de antes de R5.</summary>
        public static PhysicsMaterial FloorFor(byte style)
        {
            if (!_resolved) Resolve();
            return style == Wg3LightCadence.StyleOffice ? _officeFloor : _hardFloor;
        }

        private static void Resolve()
        {
            _resolved = true;
            _officeFloor = FirstMaterialOf(OfficeFloorSurfaceName);
            _hardFloor = FirstMaterialOf(HardFloorSurfaceName);
        }

        private static PhysicsMaterial FirstMaterialOf(string surfaceName)
        {
            if (!SurfaceDefinition.TryGetWithName(surfaceName, out SurfaceDefinition def) || def == null)
                return null;
            PhysicsMaterial[] mats = def.Materials;
            return mats != null && mats.Length > 0 ? mats[0] : null;
        }
    }
}
