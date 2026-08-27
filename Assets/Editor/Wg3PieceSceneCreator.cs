#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// La escena de UNA pieza autorada, para recorrerla.
    ///
    /// POR QUÉ UNA SOLA PIEZA Y NO UN MUNDO: la biblioteca autorada SUSTITUYE al catálogo de código,
    /// así que activarla con una pieza convertiría el mundo entero en un pasillo repetido. Esta
    /// escena monta la pieza a mano, sin tocar la biblioteca ni el mundo que se está jugando: es lo
    /// que permite andarla hoy, con una sola pieza dibujada, en vez de esperar a tener catálogo.
    ///
    /// Es también la escena aislada que pide la REGLA R9 —sin backend, sin red— aplicada al
    /// contenido en vez de al código.
    /// </summary>
    public static class Wg3PieceSceneCreator
    {
        private const string PiecePath = "Assets/WorldGen3/Pieces/cor_alcove.asset";
        private const string ScenePath = "Assets/Scenes/WorldGen3Piece.unity";

        [MenuItem("Backrooms/WorldGen3/Crear escena de la pieza autorada")]
        public static void CreateScene()
        {
            var asset = AssetDatabase.LoadAssetAtPath<Wg3PieceAsset>(PiecePath);
            if (asset == null || !asset.IsBaked)
            {
                Debug.LogError($"[WG3] no hay pieza horneada en {PiecePath}. Se crea con " +
                               "Backrooms/WorldGen3/Crear la pieza autorada de prueba.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.10f, 0.10f, 0.095f);
            RenderSettings.fog = false;
            RenderSettings.skybox = null;

            // La pieza NO se monta aquí: se monta al arrancar. El ensamblador marca lo que crea con
            // `HideFlags.DontSave` —sus mallas son de runtime, no assets— así que montarla en el
            // editor y guardar la escena la guardaría VACÍA. Mismo patrón que `Wg3TestWorld`.
            var root = new GameObject("Pieza");
            var single = root.AddComponent<Wg3SinglePieceWorld>();
            single.piece = asset;
            single.spawnLights = true;

            // Solo para calcular los extremos del recorrido: la colocación real la hace el
            // componente al arrancar, con estos mismos valores (origen 0, sin girar).
            Wg3Piece piece = asset.ToPiece();
            var placement = new Wg3Placement
            {
                piece = piece,
                rotation = 0,
                originX = 0f,
                originZ = 0f,
                socketState = new byte[piece.sockets.Length]
            };

            var playerGo = new GameObject("Player");
            var controller = playerGo.AddComponent<CharacterController>();
            controller.height = 1.75f;
            controller.radius = 0.3f;
            controller.center = new Vector3(0f, 0.875f, 0f);
            controller.stepOffset = 0.32f;
            var player = playerGo.AddComponent<Wg3TestPlayer>();

            var eyeGo = new GameObject("Eye");
            eyeGo.transform.SetParent(playerGo.transform, false);
            eyeGo.transform.localPosition = new Vector3(0f, 0.72f, 0f);
            var cam = eyeGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 70f;

            // Los extremos del recorrido salen de las BOCAS HORNEADAS, no de números escritos aquí:
            // si el horno cambiara dónde caen, la escena se movería con él en vez de quedarse
            // apuntando a un sitio que ya no es una puerta.
            Vector3 from = MouthInside(placement, 0);
            Vector3 to = MouthInside(placement, 1);

            var probeGo = new GameObject("WalkProbe");
            var probe = probeGo.AddComponent<Wg3PieceWalkProbe>();
            probe.player = player;
            probe.from = from;
            probe.to = to;

            playerGo.transform.position = from + Vector3.up * 0.5f;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log($"[WG3] escena de pieza creada en {ScenePath}. Recorrido de {from} a {to}, " +
                      $"{Vector3.Distance(from, to):0.0} m. Entra a Play y el sondeo la cruza solo.");
        }

        /// <summary>
        /// Un punto DENTRO de la pieza, justo detrás de la boca <paramref name="index"/>.
        ///
        /// Dentro y no en el plano de la boca porque la pieza está sola en la escena: fuera de ella
        /// no hay suelo, y arrancar el recorrido en el propio vano dejaría al jugador medio en el
        /// aire. 0,8 m adentro lo pone sobre la losa y deja el vano por delante.
        /// </summary>
        private static Vector3 MouthInside(Wg3Placement placement, int index)
        {
            Vector2 point = placement.WorldPoint(index);
            Vector2 inward = -Wg3Piece.OutwardNormal(placement.WorldSide(index)) * 0.8f;
            return new Vector3(point.x + inward.x, 0f, point.y + inward.y);
        }
    }
}
#endif
