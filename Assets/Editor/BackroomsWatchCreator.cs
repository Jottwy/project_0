#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.UI;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// PLACEHOLDER (2026-08-15) — genera el prefab del reloj de muñeca CLONANDO la brújula del
    /// vendor. Ejecutar con "Backrooms/Create Wrist Watch (Placeholder)".
    ///
    /// POR QUÉ LA BRÚJULA ES EL DONANTE: <c>STP_Wieldable_Compass</c> ya es exactamente la forma
    /// que hace falta — un <c>WieldableTool</c> que se saca a la muñeca con el rig completo de
    /// brazos en primera persona (Hand.L, Forearm.L, los dedos, ambos brazos) y su animación de
    /// sacar/guardar. Clonarla deja como único trabajo real la cara de la pantalla.
    ///
    /// EL ORIGINAL NO SE TOCA. Se instancia, se modifica la copia y se guarda en
    /// <see cref="PrefabPath"/>, fuera del árbol del vendor — regla <c>stp-no-direct-edits</c>:
    /// nada dentro de <c>Assets/PolymindGames</c> se edita, porque un reimport del paquete lo
    /// borra sin avisar.
    ///
    /// DOS COMPONENTES SE CAEN EN LA COPIA, y no es limpieza cosmética: <c>WieldableItem</c> ata
    /// el wieldable a una <c>ItemDefinition</c> del inventario y <c>WieldableDurabilityDepleter</c>
    /// drena la durabilidad de ESE item. El reloj placeholder no es un item — no ocupa slot, no
    /// se puede perder y no viaja por el wire — así que ambos apuntarían a un item inexistente.
    /// Cuando se decida si el reloj es perdible, el depleter es justo lo que da la pila del
    /// concepto sin escribir código.
    ///
    /// LA MALLA DE LA BRÚJULA SE APAGA, NO SE BORRA (fase A de la pose, 2026-08-15). El prefab
    /// trae tres <c>SkinnedMeshRenderer</c> independientes — <c>LeftArm</c>, <c>RightArm</c> y
    /// <c>Compass</c> — así que apagar el tercero deja los brazos con las manos vacías, que es el
    /// encuadre del concepto. Se apaga con <c>SetActive(false)</c> en vez de destruirse para
    /// poder encenderlo desde el Inspector y comparar sin regenerar nada.
    ///
    /// EL PERFIL DE MOVIMIENTO SE CLONA, y esto es lo importante de la fase A: la pose en pantalla
    /// no vive en ninguna animación — el compass usa los clips genéricos
    /// (<c>Template_Wieldable.controller</c>, que nadie animó para él). Vive en el
    /// <c>MotionProfile</c> que cuelga de <c>WieldableMotion</c>, un ScriptableObject con pares
    /// PositionOffset/RotationOffset y sus muelles. Se clona a <see cref="ProfilePath"/> porque
    /// tunear el del vendor movería también la brújula de verdad — y porque un reimport del
    /// paquete se lo llevaría por delante.
    ///
    /// crear-si-falta, igual que el resto de creadores de esta carpeta: si el prefab ya existe no
    /// se toca, para no pisar la colocación de la cara que se haya ajustado a mano en el
    /// inspector. Para rehacerlo desde cero está el menú "Rebuild".
    /// </summary>
    public static class BackroomsWatchCreator
    {
        // Bajo Resources porque WristWatchHandler lo carga por ruta: el componente lo añade
        // GameBootstrap con EnsureComponent, que no puede rellenar un campo serializado.
        private const string PrefabFolder = "Assets/Resources/Wieldables";
        private const string PrefabPath = PrefabFolder + "/BR_Wieldable_Watch.prefab";
        private const string RootName = "BR_Wieldable_Watch";

        private const string DonorPath =
            "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_Compass.prefab";

        // Junto al prefab a propósito: todo lo del reloj en una carpeta. Cuesta que el asset entre
        // en el build por vivir bajo Resources, y son unos pocos KB — barato a cambio de que nadie
        // tenga que buscarlo en otro árbol.
        private const string ProfilePath = PrefabFolder + "/BR_Watch_Motion.asset";

        private const string DonorProfilePath =
            "Assets/PolymindGames/STP/Data/Motion/STP_Compass.asset";

        // Nombre del nodo de la malla de la brújula dentro del rig. Los otros dos renderers son
        // los brazos y esos se quedan.
        private const string CompassMeshNodeName = "Compass";

        // Encuadre validado en juego por Joel (2026-08-15): antebrazo cruzando el cuadro con la
        // muñeca legible. Van en WieldableMotion, NO en su Pivot Offset — el pivot es el centro
        // de rotación de los movimientos procedurales, y moverlo aplana el sway y el bob. Todo
        // encuadre alcanzable moviendo el pivot lo es también con posición + rotación, porque
        // girar alrededor de un punto lejano equivale a girar en el origen y trasladar.
        private static readonly Vector3 FramingPositionOffset = new Vector3(0.1f, 0.11f, 0f);
        private static readonly Vector3 FramingRotationOffset = new Vector3(-1.95f, 0f, 0f);

        [MenuItem("Backrooms/Create Wrist Watch (Placeholder)")]
        public static void Create()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                Debug.Log($"[WatchCreator] Ya existe {PrefabPath}; no se toca. " +
                          "Usa \"Backrooms/Rebuild Wrist Watch (Placeholder)\" para rehacerlo.");
                return;
            }

            Build();
        }

        /// <summary>
        /// Rehace el prefab desde el donante. El <c>MotionProfile</c> clonado NO se borra: la pose
        /// es lo que más se tunea y perderla en cada rebuild haría el ciclo inservible. Para
        /// volver a los valores del compass, borra <see cref="ProfilePath"/> a mano y repite.
        /// </summary>
        [MenuItem("Backrooms/Rebuild Wrist Watch (Placeholder)")]
        public static void Rebuild()
        {
            // La pose del brazo se rescata ANTES de borrar y se vuelve a poner después. Autorarla
            // cuesta una sesión entera de girar huesos a mano y regenerar el prefab la borraría
            // en silencio — el fallo sería "el brazo volvió a la pose del compass" un día
            // cualquiera, sin nada que lo relacione con haber pulsado Rebuild.
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Gameplay.WristPoseOverride.BoneRotation[] rescuedPose = null;

            if (existing != null)
            {
                var pose = existing.GetComponent<Gameplay.WristPoseOverride>();
                if (pose != null && pose.CapturedPose != null && pose.CapturedPose.Length > 0)
                {
                    rescuedPose = pose.CapturedPose;
                    Debug.Log($"[WatchCreator] Rescatada la pose de {rescuedPose.Length} huesos " +
                              "antes de regenerar.");
                }

                AssetDatabase.DeleteAsset(PrefabPath);
            }

            Build(rescuedPose);
        }

        private static void Build(
            Gameplay.WristPoseOverride.BoneRotation[] rescuedPose = null)
        {
            var donor = AssetDatabase.LoadAssetAtPath<GameObject>(DonorPath);
            if (donor == null)
            {
                Debug.LogError($"[WatchCreator] No se encuentra el donante en {DonorPath}.");
                return;
            }

            if (!Directory.Exists(PrefabFolder))
            {
                Directory.CreateDirectory(PrefabFolder);
                AssetDatabase.Refresh();
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(donor);
            if (instance == null)
            {
                Debug.LogError("[WatchCreator] No se pudo instanciar el donante.");
                return;
            }

            try
            {
                // Se rompe el vínculo con el prefab del vendor ANTES de tocar nada: sobre una
                // instancia vinculada, quitar componentes deja overrides en vez de quitarlos, y
                // un reimport del paquete los resucitaría.
                PrefabUtility.UnpackPrefabInstance(
                    instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                instance.name = RootName;

                StripComponent<WieldableDurabilityDepleter>(instance);
                StripComponent<WieldableItem>(instance);

                // TERCERO, y descubierto por sonda en vivo: WieldableRotatingElement hace girar la
                // rosa de la brújula leyendo la durabilidad DEL ITEM del wieldable. Al quitar
                // WieldableItem arriba se queda huérfano: su Awake escupe "No wieldable item found"
                // y su LateUpdate lanza NullReferenceException UNA VEZ POR FRAME, para siempre
                // (3664 en una ventana de sondeo de tres segundos y pico). Una excepción por frame
                // aborta ese LateUpdate y entierra la consola — el síntoma no es un error visible,
                // es que el juego "se ralla". Quitar WieldableItem obliga a quitar también esto.
                StripComponent<WieldableRotatingElement>(instance);

                // EN LA RAÍZ, a propósito. El componente se ancla solo al hueso que diga
                // `anchorBoneName`, así que dónde viva no afecta al resultado — pero sí a poder
                // ajustarlo: en la raíz sale en el Inspector nada más seleccionar el wieldable,
                // en vez de obligar a cazar un hueso concreto dentro de un rig de 60 nodos.
                if (instance.GetComponent<WristWatchDisplay>() == null)
                    instance.AddComponent<WristWatchDisplay>();

                // Fase B de la pose: los offsets del MotionProfile mueven el rig en bloque y no
                // pueden abrir ni cerrar una mano. Este dobla huesos encima de la animación.
                var poseOverride = instance.GetComponent<Gameplay.WristPoseOverride>()
                                   ?? instance.AddComponent<Gameplay.WristPoseOverride>();

                if (rescuedPose != null && rescuedPose.Length > 0)
                    poseOverride.CapturedPose = rescuedPose;

                HideCompassMesh(instance);
                AssignClonedMotionProfile(instance);

                // SE GUARDA CON LA RAÍZ APAGADA, y no es cosmético: es un requisito del contrato
                // de WieldablesController.RegisterWieldable, que hace
                //
                //     wieldable = InstantiateIfNeeded(wieldable);   // Awake() corre aquí
                //     wieldable.SetCharacter(Character);            // Character se asigna aquí
                //     if (!go.activeSelf) go.SetActive(true);       // ...y Awake se fuerza aquí
                //     go.SetActive(false);
                //
                // WieldableMotion.Awake() lee wieldable.Character. Con la raíz encendida, Awake
                // corre en la primera línea —cuando Character todavía es null—, deja _character
                // en null para siempre y su OnEnable revienta con NullReferenceException ANTES de
                // llamar a ResetMixer: los offsets de encuadre no se aplican jamás y el síntoma
                // es "toco los números y no pasa nada".
                //
                // Los wieldables del vendor no lo sufren porque están colocados en la escena y
                // guardados inactivos, así que su Awake cae en el SetActive(true) de después.
                // Un prefab instanciado nace activo, y ahí se rompe la secuencia.
                instance.SetActive(false);

                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath, out bool saved);
                if (!saved)
                {
                    Debug.LogError($"[WatchCreator] Falló el guardado en {PrefabPath}.");
                    return;
                }

                Debug.Log($"[WatchCreator] Creado {PrefabPath}. " +
                          "Pruébalo en juego con la tecla T.");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Apaga la malla de la brújula y deja los brazos. Se busca el nodo por nombre y se apaga
        /// entero en vez de quitarle el renderer: así vuelve con un click en el Inspector, que es
        /// lo que hace falta para comparar "con objeto" contra "muñeca vacía" sin regenerar.
        /// </summary>
        private static void HideCompassMesh(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != CompassMeshNodeName)
                    continue;

                t.gameObject.SetActive(false);
                return;
            }

            Debug.LogWarning($"[WatchCreator] No se encontró el nodo \"{CompassMeshNodeName}\"; " +
                             "el reloj sale con la brújula en la mano. Apágalo a mano.");
        }

        /// <summary>
        /// Clona el <c>MotionProfile</c> del donante y se lo asigna al <c>WieldableMotion</c> de la
        /// copia. Sin esto, tunear la pose del reloj movería también la brújula del vendor, porque
        /// los dos apuntarían al mismo ScriptableObject.
        ///
        /// El campo es privado, así que se escribe por <see cref="SerializedObject"/>: no hay
        /// setter público para el perfil (sí lo hay para los offsets, que son los que se tocan en
        /// Play).
        /// </summary>
        private static void AssignClonedMotionProfile(GameObject root)
        {
            var motion = root.GetComponentInChildren<WieldableMotion>(true);
            if (motion == null)
            {
                Debug.LogWarning("[WatchCreator] El donante no trae WieldableMotion; " +
                                 "la pose se queda con la del compass y no será tuneable aparte.");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(ProfilePath) == null
                && !AssetDatabase.CopyAsset(DonorProfilePath, ProfilePath))
            {
                Debug.LogError($"[WatchCreator] No se pudo clonar {DonorProfilePath} a {ProfilePath}.");
                return;
            }

            var profile = AssetDatabase.LoadAssetAtPath<Object>(ProfilePath);
            if (profile == null)
            {
                Debug.LogError($"[WatchCreator] El perfil clonado no se pudo cargar de {ProfilePath}.");
                return;
            }

            var so = new SerializedObject(motion);
            var prop = so.FindProperty("_motionProfile");
            if (prop == null)
            {
                Debug.LogError("[WatchCreator] WieldableMotion ya no tiene el campo _motionProfile; " +
                               "el vendor cambió y hay que revisar este creador.");
                return;
            }

            prop.objectReferenceValue = profile;
            SetVector(so, "_positionOffset", FramingPositionOffset);
            SetVector(so, "_rotationOffset", FramingRotationOffset);
            // _pivotOffset se deja como venga del donante, a propósito: ver el comentario de
            // FramingRotationOffset.
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetVector(SerializedObject so, string propertyName, Vector3 value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"[WatchCreator] WieldableMotion no tiene \"{propertyName}\"; " +
                                 "el encuadre habrá que ponerlo a mano.");
                return;
            }

            prop.vector3Value = value;
        }

        private static void StripComponent<T>(GameObject root) where T : Component
        {
            var found = root.GetComponentsInChildren<T>(true);
            for (int i = 0; i < found.Length; i++)
                Object.DestroyImmediate(found[i], allowDestroyingAssets: false);
        }

    }
}
#endif
