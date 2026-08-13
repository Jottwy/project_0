#if UNITY_EDITOR
using System.Collections.Generic;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-068 S3 — da de alta el bote de spray en el prefab del jugador, que es lo único que
    /// separa "el item existe" de "el item se puede empuñar".
    ///
    /// POR QUÉ HACE FALTA ESTO. <c>WieldableInventory.OnBehaviourStart</c> llama UNA vez a
    /// <c>InitializeWieldablesCache</c>, que escanea los HIJOS DIRECTOS del root de wieldables
    /// del jugador buscando componentes <c>WieldableItem</c> YA instanciados, y con ellos monta
    /// el diccionario item→wieldable. No es una lista de prefabs que se resuelva bajo demanda:
    /// los wieldables están pre-colocados en la jerarquía. Un prefab que no cuelgue de ahí no
    /// entra en el diccionario, y equipar su item no saca nada.
    ///
    /// SE COLOCA AL LADO DE LA ANTORCHA, y no en una ruta escrita a mano, a propósito: el script
    /// busca dónde vive ya un <c>WieldableItem</c> y mete el nuestro ahí. Así no puede
    /// equivocarse de prefab ni de nodo, y sigue funcionando si el vendor reorganiza su
    /// jerarquía. Copia además la pose local y el estado activo del hermano, que es la
    /// configuración que el vendor ya da por buena.
    ///
    /// COSTE ACEPTADO, DECLARADO: `FPS_Player.prefab` y `STP_Player.prefab` son del VENDOR, así
    /// que un reimport del `.unitypackage` de PolymindGames se lleva esta alta en silencio y el
    /// bote deja de poder equiparse sin que nada dé error. Es la misma clase de riesgo que la
    /// edición de `GameMode.cs` que el proyecto ya asume. Si un día el bote deja de aparecer en
    /// la mano después de reimportar el vendor, se vuelve a ejecutar este menú.
    ///
    /// Idempotente: si el bote ya cuelga, no toca nada.
    /// </summary>
    public static class SprayCanWieldableRegistrar
    {
        private const string WieldablePath = "Assets/Prefabs/Wieldables/BR_Wieldable_SprayCan.prefab";

        // Los dos prefabs de jugador del proyecto. Se procesan los dos porque cuál está vivo
        // depende de la escena, y dar de alta en el que no se usa no rompe nada.
        private static readonly string[] PlayerPrefabs =
        {
            "Assets/PolymindGames/FPSCore/Prefabs/Core/FPS_Player.prefab",
            "Assets/PolymindGames/STP/Prefabs/Core/STP_Player.prefab",
        };

        [MenuItem("Backrooms/Spray/Registrar bote en el jugador", false, 98)]
        public static void Register()
        {
            var wieldable = AssetDatabase.LoadAssetAtPath<GameObject>(WieldablePath);
            if (wieldable == null)
            {
                Debug.LogError($"[SprayCanRegistrar] No hay prefab en '{WieldablePath}'. " +
                               "Ejecuta antes 'Backrooms/Spray/Crear bote de spray'.");
                return;
            }
            if (wieldable.GetComponent<WieldableItem>() == null)
            {
                Debug.LogError("[SprayCanRegistrar] El prefab del bote no tiene WieldableItem en la raíz. " +
                               "Sin él, InitializeWieldablesCache no lo vería aunque cuelgue del sitio correcto.");
                return;
            }

            int registered = 0, already = 0, skipped = 0;
            foreach (var playerPath in PlayerPrefabs)
            {
                switch (RegisterInto(playerPath, wieldable))
                {
                    case Result.Registered: registered++; break;
                    case Result.AlreadyThere: already++; break;
                    default: skipped++; break;
                }
            }

            if (registered > 0)
                AssetDatabase.SaveAssets();

            Debug.Log($"[SprayCanRegistrar] {registered} prefab(s) de jugador con el bote dado de alta, " +
                      $"{already} ya lo tenían, {skipped} sin root de wieldables. " +
                      "OJO: los prefabs de jugador son del VENDOR — un reimport del .unitypackage se lleva " +
                      "esta alta en silencio y habrá que volver a ejecutar este menú.");
        }

        private enum Result { Registered, AlreadyThere, Skipped }

        private static Result RegisterInto(string playerPath, GameObject wieldable)
        {
            var root = PrefabUtility.LoadPrefabContents(playerPath);
            if (root == null)
            {
                Debug.LogWarning($"[SprayCanRegistrar] No se pudo abrir '{playerPath}'.");
                return Result.Skipped;
            }

            try
            {
                // Dónde viven ya los wieldables: el padre de cualquier WieldableItem existente.
                // Preguntárselo a la jerarquía en vez de escribir una ruta evita depender de
                // cómo el vendor haya nombrado sus nodos.
                var existing = new List<WieldableItem>(root.GetComponentsInChildren<WieldableItem>(true));
                if (existing.Count == 0)
                {
                    Debug.LogWarning($"[SprayCanRegistrar] '{playerPath}' no tiene ningún WieldableItem; " +
                                     "no hay dónde colgar el bote. Saltado.");
                    return Result.Skipped;
                }

                var sibling = existing[0];
                var parent = sibling.transform.parent;
                if (parent == null)
                {
                    Debug.LogWarning($"[SprayCanRegistrar] En '{playerPath}' el WieldableItem existente está " +
                                     "en la raíz; no sabría dónde colgar el bote. Saltado.");
                    return Result.Skipped;
                }

                foreach (var item in existing)
                {
                    if (item.name.StartsWith("BR_Wieldable_SprayCan"))
                    {
                        Debug.Log($"[SprayCanRegistrar] '{playerPath}' ya tenía el bote — intacto.");
                        return Result.AlreadyThere;
                    }
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(wieldable, parent);
                if (instance == null)
                {
                    Debug.LogWarning($"[SprayCanRegistrar] No se pudo instanciar el bote dentro de '{playerPath}'.");
                    return Result.Skipped;
                }

                // Se queda como INSTANCIA de prefab, sin desempaquetar: así, afinar el bote más
                // adelante (pintura, color, boquilla) se propaga solo a los jugadores.
                instance.name = "BR_Wieldable_SprayCan";
                instance.transform.SetLocalPositionAndRotation(
                    sibling.transform.localPosition, sibling.transform.localRotation);
                instance.transform.localScale = sibling.transform.localScale;

                // El estado activo se COPIA del hermano y no se fuerza: InitializeWieldablesCache
                // filtra por inactivos, así que si la antorcha está apagada en el prefab, el bote
                // apagado es lo correcto — y al revés.
                instance.SetActive(sibling.gameObject.activeSelf);

                PrefabUtility.SaveAsPrefabAsset(root, playerPath);
                Debug.Log($"[SprayCanRegistrar] Bote dado de alta en '{playerPath}', junto a " +
                          $"'{sibling.name}' bajo '{parent.name}' (activo={instance.activeSelf}).");
                return Result.Registered;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
