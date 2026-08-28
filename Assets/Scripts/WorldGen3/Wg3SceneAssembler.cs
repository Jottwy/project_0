using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Los cuatro materiales, en el orden de submalla de
    /// <see cref="Wg3MeshBuilder.SubMesh"/>.</summary>
    [System.Serializable]
    public sealed class Wg3Materials
    {
        public Material floor;
        public Material structure;
        public Material ceiling;
        public Material decoration;

        public Material[] AsArray() => new[] { floor, structure, ceiling, decoration };
    }

    /// <summary>
    /// Monta un <see cref="Wg3World"/> en la escena.
    ///
    /// Un GameObject por pieza: una malla con cuatro submallas y los <c>BoxCollider</c> de sus
    /// volúmenes sólidos. La decoración NO recibe collider — es el mismo dato que en
    /// <see cref="Wg3Geometry"/> decidió que el rodapié se ve y no frena, aquí llevado a la escena
    /// sin una sola condición nueva.
    ///
    /// FUGAS: las mallas se crean en tiempo de ejecución y hay que destruirlas a mano. Borrar solo
    /// los GameObjects hijos deja las mallas vivas y la memoria sube en cada regeneración — es
    /// exactamente la fuga que se documentó en <c>VerticalShaftChunk</c>, donde `Clear()` destruía
    /// los hijos y nunca los recursos. Por eso <see cref="Clear"/> existe y por eso lleva la lista.
    /// </summary>
    public static class Wg3SceneAssembler
    {
        /// <summary>Tolerancia bajo la cual un giro se considera nulo y el volumen puede compartir
        /// GameObject. Un <c>BoxCollider</c> no puede girar por su cuenta: solo gira su
        /// transform, así que cada caja con yaw necesita su propio hijo.</summary>
        private const float YawEpsilon = 0.01f;

        public static void Assemble(Wg3World world, Transform parent, Wg3Materials materials,
            List<Mesh> createdMeshes, bool addLights = true,
            List<BackroomsSurvival.Net.Wg3CarveMsg> carves = null)
        {
            if (world == null || parent == null) return;
            Material[] mats = materials != null ? materials.AsArray() : null;

            for (int i = 0; i < world.placements.Count; i++)
            {
                Wg3Placement placement = world.placements[i];
                // ADR-101 — los vanos se restan ANTES de que los volúmenes sean malla o colliders.
                // Es lo que permite que una pieza del catálogo tenga las puertas que el plan decidió
                // en vez de las que traía horneadas; sin esto se monta sellada mientras el servidor
                // la deja pasar.
                List<Wg3Volume> volumes =
                    Wg3Carving.Apply(Wg3Geometry.BuildPlaced(placement), carves);
                var origin = new Vector3(placement.originX, placement.originY, placement.originZ);

                if (placement.piece.visualPrefab != null && carves != null && carves.Count > 0)
                {
                    // Una malla AUTORADA no se puede partir: la resta sólo alcanza a los volúmenes,
                    // así que la colisión se abriría y el dibujo no. Se avisa fuerte porque el
                    // síntoma es una puerta que se atraviesa y se ve como pared — el peor de los dos
                    // sentidos, y no sale en una captura.
                    Debug.LogWarning(
                        $"[WG3] la pieza «{placement.piece.id}» tiene malla autorada y le toca un " +
                        $"vano excavado: la colisión se abrirá y el dibujo no. Hace falta que la " +
                        $"pieza declare sus vanos o que el plan no la elija para este espacio.");
                }

                var go = new GameObject($"{i:D3}_{placement.piece.id}_r{placement.rotation}");

                // DontSave, y no es cosmético: la malla se crea en tiempo de ejecución y no es un
                // asset. Sin esta marca, guardar la escena serializa cientos de objetos con una
                // referencia a malla que al reabrir ya no existe — el fichero engorda y la escena
                // se abre llena de objetos vacíos que parecen geometría perdida.
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(parent, false);
                go.transform.position = origin;

                if (placement.piece.visualPrefab != null)
                {
                    SpawnAuthoredVisual(go, placement);
                }
                else
                {
                    Mesh mesh = Wg3MeshBuilder.Build(volumes, origin);
                    mesh.name = $"wg3_{placement.piece.id}_{i}";
                    mesh.hideFlags = HideFlags.DontSave;
                    createdMeshes?.Add(mesh);

                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    if (mats != null) renderer.sharedMaterials = mats;
                }

                // LOS COLLIDERS SALEN DE LOS VOLÚMENES SIEMPRE, tenga la pieza malla autorada o no.
                // Es lo que mantiene al cliente chocando contra lo mismo que el servidor: la chuleta
                // es el único dato que cruzó la frontera de autoridad.
                AddColliders(go, volumes, origin);

                if (addLights) AddCeilingLight(go, placement);
            }
        }

        /// <summary>
        /// ADR-098 — monta un TRAMO GENERADO: el conector que el servidor sintetizó donde el
        /// catálogo no podía encajar una pieza.
        ///
        /// Mismo camino que una pieza sin prefab autorado —volúmenes, malla, colliders— porque es
        /// exactamente eso: una pieza rectangular que nadie dibujó. Lo único distinto es de dónde
        /// salen sus volúmenes, y de que salgan iguales a los dos lados del cable responde el
        /// oráculo de conectores.
        /// </summary>
        public static GameObject AssembleSegment(Wg3Segment segment, Transform parent,
            Wg3Materials materials, List<Mesh> createdMeshes, string name, bool addLight = true,
            List<BackroomsSurvival.Net.Wg3CarveMsg> carves = null)
        {
            if (segment == null || parent == null) return null;

            // ADR-101 — los tramos se excavan TAMBIÉN. El servidor resta sobre el ráster ya
            // estampado, o sea sobre todo lo que haya en esa caja; restringirlo aquí a las piezas
            // sería una divergencia deliberada entre lo que se ve y lo que frena.
            List<Wg3Volume> volumes = Wg3Carving.Apply(Wg3GeneratedSegment.Build(segment), carves);
            Vector3 origin = segment.Origin;

            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(parent, false);
            go.transform.position = origin;

            Mesh mesh = Wg3MeshBuilder.Build(volumes, origin);
            mesh.name = $"wg3_{name}";
            mesh.hideFlags = HideFlags.DontSave;
            createdMeshes?.Add(mesh);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            Material[] mats = materials != null ? materials.AsArray() : null;
            if (mats != null) renderer.sharedMaterials = mats;

            AddColliders(go, volumes, origin);

            if (addLight)
            {
                // Un plafón cada pocos metros y no uno por tramo: un conector puede medir veinte
                // metros, y un solo punto de luz en el centro deja los dos extremos a oscuras — que
                // es justo donde está lo que hay que ver, la puerta a la que lleva.
                float length = Mathf.Max(segment.SizeX, segment.SizeZ);
                int lamps = Mathf.Clamp(Mathf.RoundToInt(length / 6f), 1, 6);
                for (int i = 0; i < lamps; i++)
                {
                    float t = (i + 0.5f) / lamps;
                    var lamp = new GameObject($"light_{i}");
                    lamp.hideFlags = HideFlags.DontSave;
                    lamp.transform.SetParent(go.transform, false);
                    lamp.transform.localPosition = new Vector3(
                        segment.SizeX > segment.SizeZ ? segment.SizeX * t : segment.SizeX * 0.5f,
                        segment.Height - 0.2f,
                        segment.SizeZ >= segment.SizeX ? segment.SizeZ * t : segment.SizeZ * 0.5f);

                    var light = lamp.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.range = 9f;
                    light.intensity = 1.1f;
                    light.color = new Color(1f, 0.96f, 0.78f);
                    light.shadows = LightShadows.None;
                }
            }

            return go;
        }

        /// <summary>
        /// Mete la malla autorada de la pieza dentro de su GameObject, colocada como su colisión.
        ///
        /// USA <see cref="Wg3Geometry.RotateLocal"/>, la misma función con la que se colocan los
        /// volúmenes, en vez de recomponer el giro aquí. Dos implementaciones del mismo mapeo son
        /// dos que pueden desviarse, y el síntoma —la malla en un sitio y la colisión en otro— no se
        /// ve en una captura: se descubre atravesando una pared que se dibuja un metro más allá.
        ///
        /// El pivote entra en esa cuenta como un punto local más: el editor de salas centra el
        /// prefab en su footprint y WG3 mide desde la esquina mínima, así que sin él la malla sale
        /// corrida media pieza.
        /// </summary>
        private static void SpawnAuthoredVisual(GameObject root, Wg3Placement placement)
        {
            Wg3Piece piece = placement.piece;
            int r = placement.rotation & 3;
            Vector2 p = Wg3Geometry.RotateLocal(piece.visualPivot, r, piece.sizeX, piece.sizeZ);

            GameObject visual = Object.Instantiate(piece.visualPrefab, root.transform, false);
            visual.name = "visual";
            visual.hideFlags = HideFlags.DontSave;
            visual.transform.localPosition = new Vector3(p.x, 0f, p.y);
            visual.transform.localRotation = Quaternion.Euler(0f, r * 90f, 0f);

            // FUERA LOS COLLIDERS QUE TRAIGA EL PREFAB. Un prefab autorado con el editor de salas
            // viene con los suyos, y dejarlos vivos daría al cliente una colisión que el servidor no
            // tiene: se bloquea donde el servidor deja pasar, y el jugador se ve empujado de vuelta
            // por una corrección que desde dentro parece un tirón sin causa.
            foreach (Collider stray in visual.GetComponentsInChildren<Collider>(true))
            {
                if (Application.isPlaying) Object.Destroy(stray);
                else Object.DestroyImmediate(stray);
            }
        }

        private static void AddColliders(GameObject root, List<Wg3Volume> volumes, Vector3 origin)
        {
            for (int v = 0; v < volumes.Count; v++)
            {
                Wg3Volume vol = volumes[v];
                if (!vol.IsSolid) continue;

                float yaw = Mathf.Repeat(vol.yawDegrees, 90f);
                bool axisAligned = yaw < YawEpsilon || yaw > 90f - YawEpsilon;

                if (axisAligned)
                {
                    // Sin giro propio: todas las cajas caben como componentes del mismo objeto, lo
                    // que ahorra un GameObject por pared en un mundo que tiene cientos.
                    var box = root.AddComponent<BoxCollider>();
                    bool swapped = Mathf.Repeat(vol.yawDegrees, 180f) > 45f;
                    box.center = vol.center - origin;
                    box.size = swapped ? new Vector3(vol.size.z, vol.size.y, vol.size.x) : vol.size;
                }
                else
                {
                    var child = new GameObject($"col_{vol.kind}_{v}");
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = vol.center - origin;
                    child.transform.localRotation = Quaternion.Euler(0f, vol.yawDegrees, 0f);
                    child.AddComponent<BoxCollider>().size = vol.size;
                }
            }
        }

        /// <summary>
        /// Un plafón por pieza, en el centro del techo.
        ///
        /// PROVISIONAL Y ANOTADO COMO TAL: la REGLA R32 dice que la posición de la luz es
        /// ESTRUCTURA y va autorada en la pieza —en las referencias, el ritmo de los plafones es
        /// lo único que te dice cuánto falta para el final de un pasillo—. Uno al centro no da
        /// ese ritmo; da que se vea algo. Cuando la pieza declare sus luces, esto se borra.
        /// </summary>
        private static void AddCeilingLight(GameObject root, Wg3Placement placement)
        {
            var go = new GameObject("light");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(
                placement.SizeX * 0.5f, placement.piece.heightMeters - 0.25f, placement.SizeZ * 0.5f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.97f, 0.88f);
            light.intensity = 1.6f;
            light.range = Mathf.Max(placement.SizeX, placement.SizeZ) * 0.75f + 6f;
            light.shadows = LightShadows.None;
        }

        /// <summary>Borra la escena montada Y las mallas que creó. Lo segundo es lo que se olvida.</summary>
        public static void Clear(Transform parent, List<Mesh> createdMeshes)
        {
            if (parent != null)
            {
                for (int i = parent.childCount - 1; i >= 0; i--)
                {
                    GameObject child = parent.GetChild(i).gameObject;
                    if (Application.isPlaying) Object.Destroy(child);
                    else Object.DestroyImmediate(child);
                }
            }

            if (createdMeshes == null) return;
            for (int i = 0; i < createdMeshes.Count; i++)
            {
                if (createdMeshes[i] == null) continue;
                if (Application.isPlaying) Object.Destroy(createdMeshes[i]);
                else Object.DestroyImmediate(createdMeshes[i]);
            }
            createdMeshes.Clear();
        }
    }
}
