using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// El arnés del ritmo de luces: TRES mundos con TRES semillas, lado a lado.
    ///
    /// Es lo único que enseña lo que un mundo solo no puede: que dos semillas dan techos distintos
    /// y que la misma semilla da siempre el mismo. Con un único mundo, «no se ve un patrón» y «el
    /// hash no depende de la semilla» son indistinguibles a ojo.
    ///
    /// # Por qué el desplazamiento se aplica DESPUÉS de generar
    ///
    /// <see cref="Wg3SceneAssembler"/> coloca cada pieza en su posición de MUNDO (<c>transform.
    /// position = origin</c>), no relativa al padre: si se moviera el padre antes, las piezas
    /// volverían a caer las unas encima de las otras y los tres mundos se interpenetrarían. Moverlo
    /// después sí arrastra a los hijos, que ya tienen su local puesto.
    ///
    /// Ese mismo detalle es el que obliga a apagar <c>regenerateOnValidate</c> en cada mundo: una
    /// regeneración por su cuenta reconstruiría en coordenadas absolutas y perdería el
    /// desplazamiento. Aquí se regenera todo desde <see cref="Build"/>.
    ///
    /// **El hash se evalúa antes del desplazamiento**, así que cada mundo se sortea como si
    /// estuviera en el origen: es exactamente lo mismo que verá el mundo servido de esa semilla.
    /// </summary>
    [ExecuteAlways]
    public sealed class Wg3LightCadenceRig : MonoBehaviour
    {
        [Tooltip("Una semilla por mundo. Tres es el mínimo que distingue «cambia con la semilla» " +
                 "de «cambió una vez por casualidad».")]
        public int[] seeds = { 42, 1337, 90210 };

        [Tooltip("Separación entre mundos, en metros. Holgada a propósito: dos mundos que se " +
                 "rozan se leen como uno solo mal generado.")]
        public float spacingMeters = 400f;

        public Wg3Materials materials = new Wg3Materials();

        public Wg3ComposerSettings settings = new Wg3ComposerSettings();

        public Wg3LightCadenceSettings lightCadence = new Wg3LightCadenceSettings();

        public bool buildOnStart = true;

        /// <summary>Nace en el primer mundo, que es el de la semilla 42 — la servida.</summary>
        public Vector3 SpawnPoint
        {
            get
            {
                var first = GetComponentInChildren<Wg3TestWorld>();
                return first != null ? first.SpawnPoint : new Vector3(0f, 1f, 0f);
            }
        }

        private void Start()
        {
            if (buildOnStart) Build();
        }

        [ContextMenu("Construir")]
        public void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }

            if (seeds == null || seeds.Length == 0) return;

            for (int i = 0; i < seeds.Length; i++)
            {
                var go = new GameObject($"seed_{seeds[i]}");
                go.transform.SetParent(transform, false);

                var world = go.AddComponent<Wg3TestWorld>();
                world.regenerateOnValidate = false; // ver el comentario de clase
                world.worldSeed = seeds[i];
                world.materials = materials;
                world.settings = settings;
                world.lightCadence = lightCadence;
                world.spawnLights = true;
                world.Generate();

                // Y ahora, con las piezas ya colocadas, se aparta el mundo entero.
                go.transform.position = new Vector3(i * spacingMeters, 0f, 0f);

                Report(go, seeds[i]);
            }
        }

        /// <summary>
        /// Cuenta lo que salió. NO es una comprobación visual: son los tres números que dicen si el
        /// hash está haciendo lo que promete —cuántas lámparas hay, cuántas se quedaron a oscuras y
        /// cuántas parpadean— y que tienen que repetirse clavados al reconstruir la misma semilla.
        /// </summary>
        private static void Report(GameObject root, int seed)
        {
            int lights = root.GetComponentsInChildren<Light>(true).Length;
            int flickering = root
                .GetComponentsInChildren<BackroomsSurvival.Gameplay.World.LampFlicker>(true).Length;
            // Cada plafón de un tramo deja su GameObject aunque el tubo esté muerto; los apagados
            // son los que no llegaron a tener Light.
            int fixtures = 0;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("light")) fixtures++;

            Debug.Log($"[WG3] cadencia, semilla {seed}: {fixtures} plafones, " +
                      $"{fixtures - lights} apagados, {flickering} parpadeando.", root);
        }
    }
}
