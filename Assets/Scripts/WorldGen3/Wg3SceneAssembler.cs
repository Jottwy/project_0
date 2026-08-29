using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Los cuatro materiales, en el orden de submalla de
    /// <see cref="Wg3MeshBuilder.SubMesh"/>.</summary>
    [System.Serializable]
    /// <summary>
    /// ADR-107 D3 — lo que un chunk le entrega al director del zumbido.
    ///
    /// **Un alta por CHUNK y no por lámpara**, que es la forma que WG2 ya eligió y por un motivo que
    /// se pierde si se copia sin leerlo: así **ningún <c>AudioSource</c> cuelga jamás de un chunk**, y
    /// descargarlo no puede dejar fuentes huérfanas. El director reparte sus propias fuentes entre las
    /// posiciones que se le dan.
    /// </summary>
    public sealed class Wg3HumBatch
    {
        public readonly List<Vector3> positions = new List<Vector3>();
        public readonly List<float> pitches = new List<float>();
        public readonly List<float> flickerHz = new List<float>();
        public readonly List<float> flickerPhase = new List<float>();
    }

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
    /// <summary>
    /// La CAPA DE RENDER de cada planta, que es lo que impide que la luz de abajo atraviese el forjado.
    ///
    /// # El problema, medido
    ///
    /// Los plafones son puntuales SIN SOMBRA con alcance de hasta 21,75 m contra una losa de 12 cm, así
    /// que la luz de la planta baja ilumina el suelo y las paredes de la de arriba a través del
    /// forjado. Con atrios de 6,40 m (ADR-104) es peor que nunca. **No se arregla bajando alcance ni
    /// intensidad: son valores validados en partida, y además el problema no es que sobre luz — es que
    /// llega a donde no debe.**
    ///
    /// # La regla, y son dos frases asimétricas
    ///
    /// - **Una LUZ ilumina sólo la planta de su propio suelo.**
    /// - **Una superficie pertenece a TODAS las plantas que su volumen atraviesa.**
    ///
    /// De ahí sale todo lo demás sin ningún caso especial. Un plafón de la planta baja no toca las
    /// salas de arriba: fuga cerrada. Un atrio mide dos plantas, así que lleva las dos capas y **lo
    /// iluminan los plafones de las dos** — que es exactamente lo que se quiere en un balcón que se
    /// asoma a él. Y un pilar que cruza el atrio se ilumina desde arriba y desde abajo por lo mismo.
    ///
    /// **Esto es más simple que lo que ADR-104 D9 escribió** —«la cota menos la unión de los vanos de
    /// forjado, con una celda de margen»— y consigue lo mismo sin analizar un solo vano: el volumen de
    /// cada cosa ya dice qué plantas ocupa. Lo que la regla simple NO da son los haces de luz por un
    /// agujero pequeño: la lámpara de arriba no alumbra la sala de abajo a través de él. Queda anotado
    /// como deuda a propósito, y no como descuido.
    /// </summary>
    public static class Wg3StoreyLayers
    {
        /// <summary>Altura de planta, espejo de <c>plan::STOREY_HEIGHT_CM</c>. Si allí cambia, aquí
        /// también: una planta contada mal reparte las capas mal y la fuga vuelve sin avisar.</summary>
        public const float StoreyM = 3.32f;

        /// <summary>URP define ocho capas de render. Por encima de la séptima planta se reutiliza la
        /// última: un edificio de nueve pisos volvería a filtrar, y es mejor que filtre a que se
        /// desborde el desplazamiento y la máscara salga en cero — sin capa, un objeto no lo ilumina
        /// NADA y el síntoma es una sala completamente negra.</summary>
        private const int MaxLayer = 7;

        /// <summary>
        /// La planta a la que pertenece una cota.
        /// </summary>
        /// <remarks>
        /// **SUELO Y NO REDONDEO, y con redondeo esto no separaba nada.** La planta <c>s</c> ocupa
        /// <c>[s·3,32, (s+1)·3,32)</c>, así que una sala normal de la baja llega a 3,08 y sigue siendo
        /// de la planta 0. Redondeando, 3,08 / 3,32 = 0,93 da **1**: toda sala corriente habría
        /// reclamado las dos plantas, todas las capas se habrían solapado y la fuga habría seguido
        /// exactamente igual — con el código puesto, las máscaras asignadas y ningún error.
        ///
        /// El epsilon positivo es para el suelo de una planta alta: 3,32 / 3,32 puede dar 0,99999 en
        /// <c>float</c> y caer una planta por debajo.
        /// </remarks>
        private static int StoreyOf(float y) =>
            Mathf.Clamp(Mathf.FloorToInt(y / StoreyM + 0.001f), 0, MaxLayer);

        /// <summary>La capa de una LUZ: sólo la planta de su suelo.</summary>
        public static uint ForLight(float floorY) => 1u << StoreyOf(floorY);

        /// <summary>Las capas de una SUPERFICIE: todas las que su volumen atraviesa.</summary>
        public static uint ForSurface(float floorY, float height)
        {
            int lo = StoreyOf(floorY);
            // Un epsilon por debajo del remate: una sala de 3,08 acaba en 3,08 y no debe reclamar la
            // planta 1, cuyo suelo está en 3,32. Sin esto, toda sala normal pediría dos capas y la
            // separación no separaría nada.
            int hi = StoreyOf(floorY + Mathf.Max(height - 0.05f, 0f));
            uint mask = 0u;
            for (int i = lo; i <= hi; i++) mask |= 1u << i;
            return mask == 0u ? 1u : mask;
        }
    }

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

                // La pieza autorada también entra en el reparto por plantas: si no, es lo único que
                // sigue iluminándose y iluminando a través del forjado, y la fuga vuelve por la
                // puerta del catálogo.
                uint pieceMask = Wg3StoreyLayers.ForSurface(
                    placement.originY, placement.piece.heightMeters);
                foreach (Renderer pr in go.GetComponentsInChildren<Renderer>(true))
                    pr.renderingLayerMask = pieceMask;
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
            List<BackroomsSurvival.Net.Wg3CarveMsg> carves = null,
            Material lampMaterial = null, Wg3HumBatch hum = null)
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
            // FRENTE A — el papel del espacio decide con qué se viste. `segment.style` llevaba
            // viajando por el cable desde el wire 48 sin que lo leyera nadie, y por eso un pasillo,
            // un almacén y una nave se dibujaban idénticos.
            Material[] mats = Wg3StyleMaterials.Resolve(materials, segment.style);
            if (mats != null) renderer.sharedMaterials = mats;
            // Un atrio mide dos plantas, así que pide las dos capas y lo alumbran los plafones de
            // arriba y los de abajo. Una sala normal pide una sola, y ahí muere la fuga.
            renderer.renderingLayerMask =
                Wg3StoreyLayers.ForSurface(segment.FloorY, segment.Height);

            AddColliders(go, volumes, origin);

            if (addLight) AddSegmentLights(go, segment, lampMaterial, hum);

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

        /// <summary>
        /// Los plafones de un tramo, en REJILLA y no en fila.
        ///
        /// # Lo que estaba mal, y se vio andando antes que en ningún número
        ///
        /// La versión anterior sacaba el número de lámparas de `Max(SizeX, SizeZ)` y las alineaba por
        /// el eje largo: escrita para un conector, donde es correcta. **En una nave de 25 × 25 daba
        /// cuatro plafones en fila por el centro y dejaba las cuatro esquinas negras**, y el síntoma
        /// en pantalla era un techo con manchas oscuras enormes que parecían falta de geometría. El
        /// lado corto no entraba en la cuenta ni para contar ni para colocar.
        ///
        /// Ahora la densidad sale de los DOS ejes por separado. Un pasillo largo y estrecho recibe
        /// exactamente lo de antes —una fila— porque su lado corto pide una sola columna, así que
        /// esto no cambia lo que ya estaba bien.
        ///
        /// # Y en un espacio alto los plafones CUELGAN
        ///
        /// Un atrio mide 6,40 m (ADR-104 D1) y el plafón iba a `Height - 0.2`, o sea a 6,20 m con el
        /// mismo alcance de 9 m: casi todo el alcance se gasta antes de llegar al suelo. Colgarlos
        /// deja el suelo iluminado igual que en una sala normal y el techo alto en penumbra, que en un
        /// atrio es lo que se quiere.
        ///
        /// **No se toca ni el alcance, ni la intensidad, ni el color**: son valores que Joel validó
        /// mirándolos en partida. Lo que cambia aquí es CUÁNTOS y DÓNDE.
        /// </summary>
        private static void AddSegmentLights(GameObject go, Wg3Segment segment,
            Material lampMaterial, Wg3HumBatch hum)
        {
            // Un plafón cada seis metros por eje, que es el ritmo que ya tenía el conector.
            const float Spacing = 6f;
            // Tope por eje: con tramos de 25 m como mucho (MAX_SEGMENT_M) son 4 × 4.
            const int MaxPerAxis = 4;
            // A partir de aquí el techo es alto y el plafón pasa a colgar.
            const float HangHeight = 3f;

            int nx = Mathf.Clamp(Mathf.RoundToInt(segment.SizeX / Spacing), 1, MaxPerAxis);
            int nz = Mathf.Clamp(Mathf.RoundToInt(segment.SizeZ / Spacing), 1, MaxPerAxis);
            float y = Mathf.Min(segment.Height - 0.2f, HangHeight);

            for (int ix = 0; ix < nx; ix++)
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    var lamp = new GameObject($"light_{ix}_{iz}");
                    lamp.hideFlags = HideFlags.DontSave;
                    lamp.transform.SetParent(go.transform, false);
                    lamp.transform.localPosition = new Vector3(
                        segment.SizeX * (ix + 0.5f) / nx,
                        y,
                        segment.SizeZ * (iz + 0.5f) / nz);

                    var light = lamp.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.range = 9f;
                    light.intensity = 1.1f;
                    light.color = new Color(1f, 0.96f, 0.78f);
                    light.shadows = LightShadows.None;
                    // SOLO su planta. Es la mitad de la regla que cierra la fuga, y la que no se
                    // puede deducir mirando el objeto: un plafón parece inofensivo.
                    // `Light.renderingLayerMask` es int y el del Renderer es uint: la conversión
                    // es explícita a propósito en la API de Unity, no un descuido de aquí.
                    light.renderingLayerMask = (int)Wg3StoreyLayers.ForLight(segment.FloorY);

                    // ADR-107 D2 — **la luminaria, que hasta hoy no existía: había luz sin lámpara.**
                    // Es decorativa y sin collider, igual que la de WG2, porque un plafón que frena
                    // es una viga invisible a la altura de la cabeza.
                    if (lampMaterial != null) AddLuminaire(lamp.transform, lampMaterial);

                    // ADR-107 D3 — y el zumbido. Pitch y fase salen de la POSICIÓN con las mismas
                    // funciones que usa WG2, así que dos jugadores oyen la misma lámpara, la misma
                    // lámpara suena igual al volver, y no viaja nada por el cable.
                    if (hum != null)
                    {
                        Vector3 world = lamp.transform.position;
                        int gx = Mathf.RoundToInt(world.x);
                        int gz = Mathf.RoundToInt(world.z);
                        hum.positions.Add(world);
                        hum.pitches.Add(
                            BackroomsSurvival.Gameplay.Audio.FluorescentHumDirector.PitchFor(gx, gz));
                        // Sin parpadeo en esta fase: `LampFlicker` es comportamiento aparte y meterlo
                        // aquí de contrabando sería otro ADR disfrazado de detalle. Cero = luz fija.
                        hum.flickerHz.Add(0f);
                        hum.flickerPhase.Add(
                            BackroomsSurvival.Gameplay.Audio.FluorescentHumDirector
                                .FlickerPhaseFor(gx, gz));
                    }
                }
            }
        }

        /// <summary>
        /// ADR-107 D2 — el panel emisivo que se ve cuando miras al techo.
        ///
        /// Copia la forma de <c>BackroomsLighting.MakeLuminaire</c>: cubo aplanado, **sin collider**
        /// —es decoración, y un plafón que frena es una viga invisible a la altura de la cabeza— y con
        /// la emisión por `MaterialPropertyBlock` para no instanciar un material por lámpara.
        /// </summary>
        private static void AddLuminaire(Transform parent, Material lampMaterial)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Luminaire";
            go.hideFlags = HideFlags.DontSave;
            if (go.TryGetComponent<Collider>(out var col))
            {
                if (Application.isPlaying) Object.Destroy(col);
                else Object.DestroyImmediate(col);
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = new Vector3(1.65f, 0.08f, 1.65f);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = lampMaterial;
            // La luminaria pertenece a la planta de su lámpara (ADR-104 enmienda 2): si no, la de
            // abajo se ve iluminada desde arriba y vuelve el síntoma que esa enmienda quitó.
            r.renderingLayerMask = Wg3StoreyLayers.ForLight(parent.position.y);
        }

        /// <summary>
        /// ADR-105 — monta un MACIZO: una caja llena, con su malla, su colisión y su estilo.
        ///
        /// **Y a ésta NO se le aplican los vanos, que es la regla D2 y el fallo que más fácil sería
        /// reintroducir aquí.** El vano de un atrio cubre su huella ensanchada medio metro, o sea
        /// exactamente donde va un pretil: pasar <c>carves</c> por aquí haría desaparecer cada pretil
        /// emitido, y el síntoma sería «el pretil no sale» sin un solo error en ninguna parte.
        ///
        /// Sin luz propia: un pilar no ilumina, y un pretil tampoco. La luz es del espacio.
        /// </summary>
        public static GameObject AssembleSolid(BackroomsSurvival.Net.Wg3SolidMsg solid,
            Transform parent, Wg3Materials materials, List<Mesh> createdMeshes, string name)
        {
            if (parent == null) return null;

            float sx = solid.sizeXCm / 100f;
            float sz = solid.sizeZCm / 100f;
            float sy = (solid.topYCm - solid.bottomYCm) / 100f;
            if (sx <= 0f || sz <= 0f || sy <= 0f) return null;

            var origin = new Vector3(solid.xCm / 100f, solid.bottomYCm / 100f, solid.zCm / 100f);

            // Una sola caja, y por eso este canal existe: un tramo habría traído además su losa de
            // suelo y la de techo, coplanares con las del atrio.
            var volumes = new List<Wg3Volume>(1)
            {
                new Wg3Volume
                {
                    center = new Vector3(sx * 0.5f, sy * 0.5f, sz * 0.5f),
                    size = new Vector3(sx, sy, sz),
                    yawDegrees = 0f,
                    kind = Wg3VolumeKind.Pillar,
                }
            };

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
            Material[] mats = Wg3StyleMaterials.Resolve(materials, solid.style);
            if (mats != null) renderer.sharedMaterials = mats;
            // Un megapilar cruza el atrio de suelo a techo, así que lleva las dos plantas y se
            // ilumina desde las dos. Un pretil vive en una sola.
            renderer.renderingLayerMask = Wg3StoreyLayers.ForSurface(origin.y, sy);

            AddColliders(go, volumes, origin);
            return go;
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
            // Sólo su planta, igual que el plafón de un tramo. Éste es el que más alcance tiene
            // —hasta 21,75 m— así que es el que peor filtraba.
            light.renderingLayerMask = (int)Wg3StoreyLayers.ForLight(root.transform.position.y);
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
