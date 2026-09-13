#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Una sola pasada headless para la UI del inventario: tema → variante → captura, en el mismo
    /// proceso de Unity (cada arranque cuesta un minuto largo; tres seguidos, cuatro). La lanza
    /// <c>tools/dev/InventoryUiPipeline.sh</c>, que además compila con Roslyn antes y corre los
    /// tests después. Menú Backrooms/UI/Run Pipeline para hacer lo mismo con el editor abierto.
    ///
    /// Sin <c>-nographics</c>: la captura necesita GPU. El tema y la variante no la necesitan
    /// pero tampoco les estorba.
    /// </summary>
    public static class BackroomsUiPipeline
    {
        [MenuItem("Backrooms/UI/Run Pipeline (theme + variant + capture)")]
        public static void RunAll()
        {
            Debug.Log("[UiPipeline] 1/3 tema");
            BackroomsUiThemeBuilder.Build();
            Debug.Log("[UiPipeline] 2/3 variante");
            BackroomsInventoryUiBuilder.Build();
            Debug.Log("[UiPipeline] 3/3 captura");
            BackroomsInventoryUiShot.Capture();
            Debug.Log("[UiPipeline] OK");
        }
    }
}
#endif
