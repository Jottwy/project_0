#if UNITY_EDITOR
using UnityEditor;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// La única copia de <c>EnsureFolder</c>: crear-si-falta para UNA carpeta del proyecto.
    ///
    /// Vivía duplicada carácter por carácter en los cinco creadores del menú "Backrooms ▸ …"
    /// (<see cref="BackroomsBuildingPieceCreator"/>, <see cref="BackroomsCarryableCreator"/>,
    /// <see cref="GridPrefabCreator"/>, <see cref="ZoneLootTableCreator"/>,
    /// <see cref="BackroomsLayerVisualsCreator"/>). Misma implementación, ahora en un solo sitio.
    ///
    /// NO es recursiva, y eso es contrato y no descuido: <c>AssetDatabase.CreateFolder</c> exige que
    /// la carpeta padre exista YA, así que quien llama enumera los ancestros de fuera hacia dentro
    /// ("Assets/Resources", luego "Assets/Resources/Definitions", luego la hoja). Hacerla recursiva
    /// sí cambiaría comportamiento — no lo "mejores".
    ///
    /// El <c>EnsureFolders()</c> (plural) de cada creador NO se comparte: esa lista de carpetas es
    /// parte del contrato de ese creador concreto.
    /// </summary>
    internal static class BackroomsEditorFolders
    {
        /// <summary>
        /// Crea <paramref name="path"/> si no existe. Espera una ruta de proyecto con al menos un
        /// '/' ("Assets/Algo") cuyo padre ya exista; sin '/' lanza, exactamente igual que las cinco
        /// copias que sustituye.
        /// </summary>
        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            int slash = path.LastIndexOf('/');
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
#endif
