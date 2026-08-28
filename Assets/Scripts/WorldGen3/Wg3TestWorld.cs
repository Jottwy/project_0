using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// El mundo de la escena de prueba de WG3.
    ///
    /// REGLA R9 — esta escena está AISLADA a propósito: sin backend, sin IPC, sin red, sin
    /// streaming y sin sesión. Compone, monta y ya. Es lo que permite que un fallo aquí solo pueda
    /// venir de la composición o de la geometría, en vez de tener que descartar antes media docena
    /// de sistemas.
    ///
    /// Genera en <c>Start</c> y también fuera de Play desde el menú, para poder mirar la planta en
    /// la vista de escena sin entrar al juego — que es como se ven de verdad las costuras entre
    /// piezas y las zonas que el campo de escala está apretando.
    /// </summary>
    [ExecuteAlways]
    public sealed class Wg3TestWorld : MonoBehaviour
    {
        [Header("Semilla")]
        public int worldSeed = 42;

        [Tooltip("Regenera al cambiar cualquier valor en el inspector, sin volver a entrar a Play.")]
        public bool regenerateOnValidate = true;

        [Header("Composición")]
        public Wg3ComposerSettings settings = new Wg3ComposerSettings();

        [Header("Materiales")]
        public Wg3Materials materials = new Wg3Materials();

        public bool spawnLights = true;

        [Header("Gizmos")]
        public bool drawSockets = true;
        public bool drawCaps = true;
        public bool drawScaleField;

        private readonly List<Mesh> _meshes = new List<Mesh>();
        private Wg3World _world;

        /// <summary>El mundo compuesto, o null si aún no se ha generado.</summary>
        public Wg3World World => _world;

        /// <summary>Centro de la pieza semilla, a la altura de los ojos. Es donde nace el jugador:
        /// la semilla es siempre un pasillo, así que se empieza mirando un pasillo, que es la
        /// primera impresión correcta del juego.</summary>
        public Vector3 SpawnPoint
        {
            get
            {
                if (_world == null || _world.placements.Count == 0) return new Vector3(0f, 1f, 0f);
                Wg3Placement p = _world.placements[0];
                return new Vector3(p.originX + p.SizeX * 0.5f, 1.0f, p.originZ + p.SizeZ * 0.5f);
            }
        }

        private void Start() => Generate();

        private void OnDestroy() => Wg3SceneAssembler.Clear(transform, _meshes);

        /// <summary>
        /// **EL BUCLE QUE SE COMÍA EL EDITOR, Y POR QUÉ NO SE VEÍA.** 2026-08-28.
        ///
        /// `Generate()` destruye y crea GameObjects, y crear objetos en una escena marca la escena
        /// sucia; eso vuelve a disparar `OnValidate`, que vuelve a encolar un `delayCall`, que vuelve
        /// a generar. El editor entra en una noria que no para nunca: el log crecía a 4 KB cada 20
        /// segundos con mundos cada vez más grandes (1081 piezas, 1665…) y, lo peor, **con la noria
        /// girando Unity no llega a compilar**, así que cualquier cambio de código se quedaba fuera y
        /// el DLL parecía al día por su fecha. Se descubrió intentando lanzar una sesión de juego.
        ///
        /// La cura es que solo pueda haber UNA regeneración encolada a la vez. No es un contador de
        /// seguridad ni un cooldown: es que encolar la segunda no tiene ningún sentido — la primera
        /// todavía no ha corrido.
        /// </summary>
        private bool _regenerationQueued;

        private void OnValidate()
        {
            if (!regenerateOnValidate || !isActiveAndEnabled) return;
            if (_regenerationQueued) return;
            _regenerationQueued = true;
            // OnValidate corre dentro de la serialización: destruir objetos ahí es ilegal y Unity
            // lo avisa con un error rojo por cada uno. Se aplaza un frame.
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                _regenerationQueued = false;
                Generate();
            };
#else
            _regenerationQueued = false;
#endif
        }

        [ContextMenu("Regenerar")]
        public void Generate()
        {
            Wg3SceneAssembler.Clear(transform, _meshes);

            List<Wg3Piece> catalog = Wg3Catalog.Build();
            List<string> issues = Wg3Validator.ValidateCatalog(catalog);
            if (issues.Count > 0)
            {
                // R6 — una pieza que no valida no existe. Se avisa fuerte porque el modo de fallo
                // silencioso (se hornea, se sortea, se descarta) es el que ya se pagó dos veces.
                Debug.LogError($"[WG3] catálogo inválido, {issues.Count} motivos:\n" +
                               string.Join("\n", issues), this);
                return;
            }

            _world = Wg3Composer.Compose(worldSeed, catalog, settings);
            Wg3SceneAssembler.Assemble(_world, transform, materials, _meshes, spawnLights);

            int[] histogram = _world.ScaleHistogram();
            Debug.Log($"[WG3] semilla {worldSeed}: {_world.placements.Count} piezas, " +
                      $"{_world.caps.Count} tapones ({_world.forcedCaps} forzados), " +
                      $"{_world.DeadEndCount()} callejones, " +
                      $"escalas n·m·l·w = {histogram[0]}·{histogram[1]}·{histogram[2]}·{histogram[3]}, " +
                      $"rechazos: {_world.rejectedByOverlap} por solape / " +
                      $"{_world.rejectedByValidator} por junta", this);
        }

        /// <summary>Otra semilla, derivada de la actual para que la secuencia sea reproducible:
        /// pulsar regenerar tres veces desde la 42 lleva siempre a los mismos tres mundos.</summary>
        public void Reseed()
        {
            worldSeed = unchecked((int)Wg3Hash.Mix(worldSeed, 0x5EED, 0, 0));
            Generate();
        }

        private void OnDrawGizmosSelected()
        {
            if (_world == null) return;

            if (drawSockets)
            {
                Gizmos.color = new Color(0.76f, 0.84f, 0.20f);
                foreach (Wg3Placement p in _world.placements)
                    for (int s = 0; s < p.socketState.Length; s++)
                    {
                        if (p.socketState[s] != Wg3World.SocketConnected) continue;
                        Vector2 q = p.WorldPoint(s);
                        Gizmos.DrawLine(new Vector3(q.x, 0.1f, q.y), new Vector3(q.x, 2.6f, q.y));
                    }
            }

            if (drawCaps)
            {
                foreach (Wg3Cap c in _world.caps)
                {
                    // Rojo el sellado por falta de candidata, violeta el deliberado (L21). Un mapa
                    // lleno de rojo significa catálogo corto, no una decisión de composición.
                    Gizmos.color = c.forced ? new Color(0.85f, 0.35f, 0.2f) : new Color(0.6f, 0.5f, 0.8f);
                    Gizmos.DrawLine(new Vector3(c.point.x, 0.1f, c.point.y),
                                    new Vector3(c.point.x, 2.6f, c.point.y));
                }
            }

            if (drawScaleField)
            {
                Bounds b = _world.FootprintBounds();
                const float step = 8f;
                for (float x = b.min.x; x < b.max.x; x += step)
                    for (float z = b.min.z; z < b.max.z; z += step)
                    {
                        switch (Wg3ScaleField.ScaleAt(worldSeed, x + step * 0.5f, z + step * 0.5f))
                        {
                            case Wg3Scale.Narrow: Gizmos.color = new Color(0.25f, 0.30f, 0.24f, 0.5f); break;
                            case Wg3Scale.Medium: Gizmos.color = new Color(0.31f, 0.37f, 0.29f, 0.5f); break;
                            case Wg3Scale.Large: Gizmos.color = new Color(0.37f, 0.44f, 0.34f, 0.5f); break;
                            default: Gizmos.color = new Color(0.42f, 0.41f, 0.20f, 0.5f); break;
                        }
                        Gizmos.DrawCube(new Vector3(x + step * 0.5f, -0.4f, z + step * 0.5f),
                                        new Vector3(step * 0.92f, 0.05f, step * 0.92f));
                    }
            }
        }
    }
}
