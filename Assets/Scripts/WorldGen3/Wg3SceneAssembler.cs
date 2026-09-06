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
        /// <summary>ADR-130 — sótanos que caben en las capas. Espejo de
        /// <c>plan::REGION_BASEMENTS</c>: la calle pasa a la capa 3 y B1–B3 a la 2, 1 y 0.</summary>
        public const int BasementLayers = 3;

        /// <summary>
        /// La planta CRUDA de una cota: sin desplazar por los sótanos y sin acotar a las ocho capas.
        ///
        /// Es la que IDENTIFICA un piso, y no vale <see cref="StoreyOf"/> para eso: aquélla satura en
        /// la séptima, así que la octava planta y la novena comparten número. Cualquier sorteo «uno
        /// por planta» hecho sobre la capa de render se repetiría solo en los edificios altos.
        /// </summary>
        public static int RawStoreyOf(float y) => Mathf.FloorToInt(y / StoreyM + 0.001f);

        private static int StoreyOf(float y) =>
            Mathf.Clamp(RawStoreyOf(y) + BasementLayers, 0, MaxLayer);

        /// <summary>La capa de una LUZ: sólo la planta de su suelo.</summary>
        public static uint ForLight(float floorY) => 1u << StoreyOf(floorY);

        /// <summary>
        /// Las capas de una luz que vive dentro de un VOLUMEN: las mismas que ese volumen.
        /// </summary>
        /// <remarks>
        /// **La de una sola planta no vale desde que los techos suben de 3,32.** La losa de techo de
        /// una sala de 3,80 arranca por encima del suelo de la planta 1, así que <see
        /// cref="ForSurface"/> le da la capa 1 — y la lámpara de esa misma sala, con la capa de su
        /// suelo, tenía la 0. Sin bit común, **el techo de la sala no lo iluminaba su propia
        /// lámpara**: quedaba a merced del ambiente y salía casi negro mientras el suelo estaba bien
        /// iluminado. Es exactamente lo que se ve en las capturas del playtest.
        ///
        /// La fuga que la máscara vino a cerrar sigue cerrada: lo que se pide es la capa del volumen
        /// de la lámpara, no «todas». Una lámpara de la planta baja no alcanza la sala de arriba
        /// porque el volumen de su sala no llega ahí.
        /// </remarks>
        public static uint ForLightIn(float floorY, float height) => ForSurface(floorY, height);

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
            List<BackroomsSurvival.Net.Wg3CarveMsg> carves = null,
            int worldSeed = 0, Wg3LightCadenceSettings cadence = null)
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

                if (addLights) AddCeilingLight(go, placement, worldSeed, cadence);

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
            Material lampMaterial = null, Wg3HumBatch hum = null,
            int worldSeed = 0, Wg3LightCadenceSettings cadence = null)
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

            if (addLight) AddSegmentLights(go, segment, lampMaterial, hum, worldSeed, cadence);

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
            Material lampMaterial, Wg3HumBatch hum, int worldSeed, Wg3LightCadenceSettings cadence)
        {
            // Un plafón cada nueve metros por eje. Eran seis, y con range 9 cada punto del techo
            // caía dentro de hasta cuatro luces a la vez: en un radio de 9 chunks salían cientos de
            // puntuales realtime, por encima del tope de 256 visibles de Forward+ y muy por encima
            // de la densidad que WG2 dejaba tras su dado de densidad y sus lámparas rotas.
            const float Spacing = 9f;
            // Tope por eje: con tramos de 25 m como mucho (MAX_SEGMENT_M) son 2 × 2. El 4 × 4
            // anterior ponía 16 luces en una nave — más que un chunk entero de WG2.
            const int MaxPerAxis = 2;
            // A partir de aquí el techo es alto y el plafón pasa a colgar.
            const float HangHeight = 3f;
            // Lado mínimo, en metros, para que un tramo se lleve UNA lámpara con sombra.
            //
            // **Y es una por tramo grande, no una por lámpara.** Hasta hoy los dos únicos creadores de
            // `Light` de WG3 fijaban `LightShadows.None`, así que nada del mundo proyectaba: ni un
            // pilar marcaba el suelo, ni una división se despegaba de la pared, y una nave de 700 m²
            // se leía tan plana como un pasillo. Es la mitad de «geometría modular colocada sobre un
            // plano» que no está en el generador.
            //
            // El tope existe porque el atlas de sombras de luces adicionales es finito
            // (`PC_RPAsset`: 4096 con teselas de 256, y `m_ShadowDistance` 50 m). Restringiéndolo a
            // tramos de doce metros por los dos lados sólo lo pagan las naves —que son justo donde
            // hay masa que sombrear— y las salas normales siguen costando lo mismo que ayer.
            const float ShadowMinSide = 12f;
            bool wantsShadow = segment.SizeX >= ShadowMinSide && segment.SizeZ >= ShadowMinSide;

            int nx = Mathf.Clamp(Mathf.RoundToInt(segment.SizeX / Spacing), 1, MaxPerAxis);
            int nz = Mathf.Clamp(Mathf.RoundToInt(segment.SizeZ / Spacing), 1, MaxPerAxis);
            float y = Mathf.Min(segment.Height - 0.2f, HangHeight);

            // Día 2 del cierre (Joel: la foto de la oficina) — los PANELES fluorescentes de 60×120
            // en rejilla sobre las placas del techo, en vez de una luminaria cuadrada por lámpara.
            // Son mallas emisivas, no luces: las luces siguen siendo las de siempre (≤ 2 × 2 por
            // tramo), así que Forward+ no paga nada por esto.
            if (lampMaterial != null) AddPanels(go.transform, segment, lampMaterial);

            // Cadencia de luces (rama unity-lighting-cadence, fusionada el 2026-09-06). La celda de
            // cada fixture es lo que acota el jitter: un plafón se mueve dentro de SU celda y nunca
            // invade la del vecino, así que el ritmo se rompe sin que dos plafones se junten.
            float cellX = segment.SizeX / nx;
            float cellZ = segment.SizeZ / nz;
            // Un metro de margen, o la mitad del tramo si es más estrecho: el jitter no puede pegar
            // la luz a la pared.
            float marginX = Mathf.Min(1.0f, segment.SizeX * 0.5f);
            float marginZ = Mathf.Min(1.0f, segment.SizeZ * 0.5f);

            // LA SOMBRA VA A LA PRIMERA ENCENDIDA, no a la de índice (0,0). Con un 12 % de plafones
            // muertos, atar la sombra al índice deja una nave de cada ocho sin ninguna sombra —
            // justo las que se apuntalaron con esto.
            bool shadowTaken = false;

            // EL DESPACHO A OSCURAS: uno por planta y chunk con todas las lámparas muertas, no el
            // 12 % que le tocaría por la cadencia. La regla y el porqué del sorteo por punto están en
            // `Wg3LightCadence.IsDarkOffice`; aquí sólo se fuerza el resultado, y se fuerza ANTES de
            // la tirada de cada fixture para no correr ninguna: el mundo alrededor no se mueve.
            bool blackout = Wg3LightCadence.IsDarkOffice(worldSeed, segment.style,
                Wg3StoreyLayers.RawStoreyOf(segment.FloorY),
                segment.MinX, segment.MinZ, segment.SizeX, segment.SizeZ);

            // La EMERGENCIA sólo existe en pasillos y cruces. Es lo que hace que un plafón fundido
            // pase de «esta parte del mundo no se ve» a «esta parte del mundo se quedó sin luz», que
            // es la lectura que se busca; en un despacho a oscuras no la hay a propósito — el
            // despacho tiene que quedarse negro, y una emergencia verde lo iluminaría entero.
            bool emergency = segment.style == Wg3LightCadence.StyleCorridor;

            for (int ix = 0; ix < nx; ix++)
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    float nominalX = cellX * (ix + 0.5f);
                    float nominalZ = cellZ * (iz + 0.5f);

                    // El hash se siembra con la posición NOMINAL en coordenadas de MUNDO: dos
                    // clientes montan el mismo tramo en el mismo sitio, así que sacan la misma
                    // lámpara rota sin que nada viaje por el cable.
                    Wg3Fixture fixture = Wg3LightCadence.Resolve(worldSeed,
                        segment.MinX + nominalX, segment.MinZ + nominalZ,
                        ix * nz + iz, cellX, cellZ, cadence);

                    // Tubo muerto: ninguna Light. Los paneles emisivos de la rejilla (510223b8) no
                    // van uno por lámpara, así que aquí no hay difusor que apagar; queda el hueco
                    // marcado en la jerarquía para que «no tiene plafón» y «está fundido» no sean el
                    // mismo silencio al mirar la escena.
                    if (blackout || !fixture.lit)
                    {
                        var dead = new GameObject($"light_off_{ix}_{iz}");
                        dead.hideFlags = HideFlags.DontSave;
                        dead.transform.SetParent(go.transform, false);
                        dead.transform.localPosition = new Vector3(nominalX, y, nominalZ);
                        if (emergency)
                            AddEmergencyLight(dead.transform, lampMaterial,
                                Wg3StoreyLayers.ForLightIn(segment.FloorY, segment.Height));
                        continue;
                    }

                    var lamp = new GameObject($"light_{ix}_{iz}");
                    lamp.hideFlags = HideFlags.DontSave;
                    lamp.transform.SetParent(go.transform, false);
                    lamp.transform.localPosition = new Vector3(
                        Mathf.Clamp(nominalX + fixture.offset.x, marginX, segment.SizeX - marginX),
                        y,
                        Mathf.Clamp(nominalZ + fixture.offset.y, marginZ, segment.SizeZ - marginZ));

                    var light = lamp.AddComponent<Light>();
                    light.type = LightType.Point;
                    // 6 m, el mismo techo que WG2 (`BackroomsLighting` acota lampRange ≤ 6). Con 9 m
                    // el volumen iluminado por lámpara era 3,4 veces el de WG2 y el clustering de
                    // Forward+ lo pagaba entero cada frame.
                    // **Once metros, y el número sale de dónde CUELGA.** Con 6 y la lámpara a 3 m
                    // de altura, al suelo le quedaba un radio útil de 3 m —el resto se lo come la
                    // vertical—, así que cada plafón dibujaba un charco de seis metros de diámetro y
                    // entre charco y charco no había nada. Con 11 el radio en el suelo pasa a 10,6 y
                    // dos plafones del mismo tramo se solapan en vez de dejar un vacío.
                    //
                    // El coste de Forward+ no sube en la misma proporción: el tope de 256 luces
                    // visibles cuenta LUCES, no volumen, y aquí no se añade ni una. Lo que sube es el
                    // trabajo de clustering, y por eso el número no es 20.
                    light.range = 11f;
                    // **El doble de 1,35, a petición de Joel tras el playtest del 2026-09-05.** Con
                    // 1,35 y el ambiente plano en 0,30 una sala corriente se leía a media luz y había
                    // que acercarse a una pared para ver de qué color era. No sube el coste de
                    // Forward+: el clustering cuenta volumen y luces, y ni el alcance ni el número
                    // cambian aquí.
                    light.intensity = 2.7f;
                    // El color validado, empujado ±200 K por el tinte. Con desviación cero el
                    // producto es el mismo color de siempre, bit a bit.
                    light.color = new Color(1f, 0.96f, 0.78f) * fixture.tint;
                    // La PRIMERA ENCENDIDA de un tramo grande proyecta; las demás no. Con 2 × 2 como
                    // tope por eje, eso es una de cuatro en el peor caso.
                    if (wantsShadow && !shadowTaken)
                    {
                        shadowTaken = true;
                        light.shadows = LightShadows.Soft;
                        // Sin bajar la fuerza, el contacto sale negro: la escena tiene ambiente
                        // cálido y una sola puntual sin rebote, así que la sombra dura se lee como
                        // agujero. Tres cuartos deja el volumen y no mata la lectura.
                        light.shadowStrength = 0.72f;
                        light.shadowNearPlane = 0.3f;
                    }
                    else
                    {
                        light.shadows = LightShadows.None;
                    }
                    // SOLO su planta. Es la mitad de la regla que cierra la fuga, y la que no se
                    // puede deducir mirando el objeto: un plafón parece inofensivo.
                    // `Light.renderingLayerMask` es int y el del Renderer es uint: la conversión
                    // es explícita a propósito en la API de Unity, no un descuido de aquí.
                    light.renderingLayerMask =
                        (int)Wg3StoreyLayers.ForLightIn(segment.FloorY, segment.Height);

                    if (fixture.flickers)
                    {
                        var flicker = lamp.AddComponent<
                            BackroomsSurvival.Gameplay.World.LampFlicker>();
                        flicker.target = light;
                        flicker.baseIntensity = light.intensity;
                        flicker.frequency = fixture.flickerHz;
                        flicker.phase = fixture.flickerPhase;
                    }

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
                        // Frecuencia y fase salen del MISMO fixture que gobierna la Light, no de la
                        // posición: si el zumbido reconstruyera su onda por su cuenta, sonaría a
                        // destiempo con el brillo y el efecto se leería como un fallo de audio.
                        hum.flickerHz.Add(fixture.flickers ? fixture.flickerHz : 0f);
                        hum.flickerPhase.Add(fixture.flickers
                            ? fixture.flickerPhase
                            : BackroomsSurvival.Gameplay.Audio.FluorescentHumDirector
                                .FlickerPhaseFor(gx, gz));
                    }
                }
            }
        }

        /// <summary>
        /// LA EMERGENCIA VERDE de un pasillo cuyo plafón está fundido.
        ///
        /// # El presupuesto, que es lo que decide la forma
        ///
        /// **Sin sombra, y con un alcance que es la mitad del de un plafón.** Un plafón fundido no
        /// tenía ninguna <c>Light</c>, así que esto SÍ añade luces donde no había — por eso paga cada
        /// una de las tres cosas que le cuestan a Forward+: no proyecta (el atlas de sombras
        /// adicionales no se toca), alcanza 5 m en vez de 11 (el clustering cuenta volumen, y 5 es un
        /// octavo del volumen de 11) y sólo aparece en pasillos y cruces, que es un tercio largo de
        /// los espacios, sobre el 12 % de plafones muertos. Los 11 m / 2,7 de las lámparas que ya
        /// existían NO se tocan aquí ni en ningún sitio de este cambio.
        ///
        /// # Y por qué la placa emisiva no es opcional
        ///
        /// Una puntual verde flotando bajo el techo es una mancha sin fuente: se lee como un fallo de
        /// render. La placa es lo que dice DE DÓNDE sale, y además es lo único que se ve desde fuera
        /// del alcance de 5 m, que en un pasillo largo es casi siempre.
        /// </summary>
        private static void AddEmergencyLight(Transform parent, Material lampMaterial, uint mask)
        {
            var go = new GameObject("light_emergency");
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(parent, false);
            // Colgada del hueco del plafón fundido, un palmo por debajo: es un aplique atornillado
            // al techo, no el propio plafón encendido de otro color.
            go.transform.localPosition = new Vector3(0f, -0.12f, 0f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = EmergencyGreen;
            // Tenue de verdad. Con la intensidad de un plafón (2,7) el pasillo se leería MEJOR
            // iluminado que uno con la lámpara sana, y el fundido dejaría de ser una avería.
            light.intensity = 0.55f;
            light.range = 5f;
            light.shadows = LightShadows.None;
            light.renderingLayerMask = (int)mask;

            if (lampMaterial == null) return;
            var plate = new GameObject("emergency_plate");
            plate.hideFlags = HideFlags.DontSave;
            plate.transform.SetParent(go.transform, false);
            plate.transform.localPosition = new Vector3(0f, 0.10f, 0f);
            plate.transform.localScale = new Vector3(0.22f, 0.04f, 0.10f);
            plate.AddComponent<MeshFilter>().sharedMesh = LuminaireMesh();
            var r = plate.AddComponent<MeshRenderer>();
            r.sharedMaterial = EmissiveVariant(lampMaterial, EmergencyGreen, 2.2f);
            r.renderingLayerMask = mask;
        }

        /// <summary>El verde de emergencia. Verde por encima del rojo Y del azul: es el orden de
        /// canales que ninguna luz de la escena produce (todas cálidas), el mismo argumento con el
        /// que <see cref="Wg3StyleMaterials"/> viste el servicio.</summary>
        private static readonly Color EmergencyGreen = new Color(0.30f, 1f, 0.42f);

        /// <summary>El azul de un monitor encendido. Frío y saturado contra un mundo entero en ámbar:
        /// a treinta metros de un pasillo, un monitor es un punto azul y no hay nada más azul.</summary>
        private static readonly Color MonitorBlue = new Color(0.24f, 0.52f, 1f);

        /// <summary>
        /// Una copia EMISIVA de un material, cacheada por (original, color).
        ///
        /// Se clona el material que ya está en la escena en vez de pedir un shader por nombre: es lo
        /// que garantiza que el shader viaje en el build. Un <c>Shader.Find</c> devuelve null en un
        /// player donde nadie referencie ese shader, y el síntoma —magenta— se descubre en el
        /// ejecutable y no en el editor.
        ///
        /// Y es un material COMPARTIDO, no un <c>MaterialPropertyBlock</c> por objeto: son dos
        /// variantes en toda la sesión, así que el SRP Batcher sigue metiendo todos los monitores
        /// encendidos del mundo en la misma llamada.
        ///
        /// FUGAS: igual que las variantes de <see cref="Wg3StyleMaterials"/>, estos materiales NO se
        /// destruyen al podar un chunk — los comparten todos los que sigan montados y son dos.
        /// </summary>
        private static readonly Dictionary<(Material, Color), Material> EmissiveCache =
            new Dictionary<(Material, Color), Material>();

        private static Material EmissiveVariant(Material source, Color colour, float boost)
        {
            if (source == null) return null;
            var key = (source, colour);
            if (EmissiveCache.TryGetValue(key, out Material cached) && cached != null) return cached;

            var m = new Material(source) { hideFlags = HideFlags.DontSave };
            m.name = $"{source.name}_emissive";
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, colour * 0.35f);
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, colour * 0.35f);
            if (m.HasProperty(EmissionColorId)) m.SetColor(EmissionColorId, colour * boost);
            EmissiveCache[key] = m;
            return m;
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        /// <summary>
        /// ADR-107 D2 — el panel emisivo que se ve cuando miras al techo.
        ///
        /// Copia la forma de <c>BackroomsLighting.MakeLuminaire</c>: cubo aplanado, **sin collider**
        /// —es decoración, y un plafón que frena es una viga invisible a la altura de la cabeza—.
        ///
        /// A diferencia de aquélla, la emisión NO va por `MaterialPropertyBlock`: el material de
        /// luminaria llega ya resuelto desde <see cref="Wg3ChunkStreamer"/> y se comparte tal cual,
        /// así que no hay nada que sobrescribir por lámpara. Un MPB aquí, además, rompería el SRP
        /// Batcher para las ~900 luminarias de un radio 1.
        /// </summary>
        /// <summary>
        /// ADR-129 — un mueble: el prefab de <c>Resources/Wg3Props/&lt;Kind&gt;</c> instanciado en el
        /// ancla que manda el servidor. Sin colisión propia: la que frena es el macizo invisible
        /// que viaja con él, así que aquí se apagan los colliders del prefab (un prefab con
        /// collider y un macizo debajo sería una mesa que frena dos veces, con dos formas).
        /// </summary>
        public static GameObject AssembleProp(BackroomsSurvival.Net.Wg3PropMsg prop, Transform parent,
            int layer, string name, Wg3Materials materials = null, Material lampMaterial = null)
        {
            if (parent == null) return null;
            if (prop.kind == BackroomsSurvival.Net.Wg3PropMsg.Sign)
                return AssembleSign(prop, parent, layer, name);
            // ADR-105 enm. 20 — el deterioro del falso techo no tiene prefab: se construye aquí.
            if (prop.kind == BackroomsSurvival.Net.Wg3PropMsg.CeilingTileHung
                || prop.kind == BackroomsSurvival.Net.Wg3PropMsg.LightHung)
                return AssembleHungDecay(prop, parent, layer, name, materials, lampMaterial);
            GameObject prefab = Wg3PropCatalog.Prefab(prop.kind, prop.xCm, prop.zCm);
            if (prefab == null) return null;
            var go = Object.Instantiate(prefab, parent);
            go.name = name;
            go.hideFlags = HideFlags.DontSave;
            var pos = new Vector3(prop.xCm * 0.01f, prop.yCm * 0.01f, prop.zCm * 0.01f);
            // La silla CAÍDA (kind 14): el mismo prefab de silla, tumbado de lado. El pivote está en
            // la base, así que al tumbarla medio cuerpo cae bajo el suelo: se sube media anchura.
            bool fallen = prop.kind == BackroomsSurvival.Net.Wg3PropMsg.ChairFallen;
            if (fallen) pos.y += 0.34f;
            go.transform.position = pos;
            go.transform.rotation = fallen
                ? Quaternion.Euler(0f, prop.yawDeg, 0f) * Quaternion.Euler(0f, 0f, 90f)
                : Quaternion.Euler(0f, prop.yawDeg, 0f);
            uint mask = Wg3StoreyLayers.ForLight(prop.yCm * 0.01f);
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) r.renderingLayerMask = mask;
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            // EL MONITOR ENCENDIDO, uno de cada cinco. Va DESPUÉS del bucle de máscaras a propósito:
            // ese bucle pisa el `renderingLayerMask` de todos los renderers, y la pantalla necesita
            // el suyo igual que el resto del mueble — lo que cambia es el material, no la capa.
            if (prop.kind == BackroomsSurvival.Net.Wg3PropMsg.Monitor
                && Wg3LightCadence.MonitorLit(prop.xCm, prop.yCm, prop.zCm))
                LightMonitorScreen(go);
            return go;
        }

        /// <summary>
        /// La pantalla azul de un monitor encendido.
        ///
        /// **SIN <c>Light</c>, y eso es la decisión entera.** Un monitor es atrezo, y hay uno por
        /// puesto de trabajo: en un chunk de cubículos son decenas. Una puntual por monitor, aunque
        /// fuera de un metro de alcance, multiplicaría por diez las luces de un chunk de oficinas y
        /// se comería el tope de 256 visibles de Forward+ con lo que menos aporta de la escena. Lo
        /// que se busca —que un despacho a oscuras tenga un punto azul al fondo— lo da la emisión
        /// sola, porque el material emisivo se ve encendido aunque no ilumine nada.
        ///
        /// La pantalla se localiza POR NOMBRE porque es lo único estable: el prefab del pack la trae
        /// como hijo <c>Screen_ON</c> con su propio <c>MeshRenderer</c> y su propio material, y
        /// tocar el material del monitor entero pintaría de azul también la carcasa.
        /// </summary>
        private static void LightMonitorScreen(GameObject monitor)
        {
            foreach (Renderer r in monitor.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name.IndexOf("Screen", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Material lit = EmissiveVariant(r.sharedMaterial, MonitorBlue, 1.6f);
                if (lit != null) r.sharedMaterial = lit;
                return;
            }
            // Un prefab sin pantalla nombrada no es un error que deba parar nada: el monitor se queda
            // apagado, que es lo que hacían los cinco de cada cinco hasta hoy.
            if (_warnedNoScreen) return;
            _warnedNoScreen = true;
            Debug.LogWarning("[wg3] el prefab de monitor no trae un hijo 'Screen': las pantallas " +
                             "encendidas se quedan apagadas");
        }

        private static bool _warnedNoScreen;

        /// ADR-129 enm. 1 — un CARTEL: el quad de su variante con la celda que le toca del atlas.
        ///
        /// El ancla llega ya despegada un centímetro de la superficie (`SIGN_PROUD_CM`, servidor),
        /// así que aquí no se corrige nada de posición: se instancia donde dicen, mirando hacia
        /// donde dice `yaw_deg`, y `y_cm` es el CENTRO del cartel, no su pie. Sin collider (es una
        /// pegatina) y sin luz (es papel: en un sótano sin corriente no se lee).
        /// </summary>
        private static GameObject AssembleSign(BackroomsSurvival.Net.Wg3PropMsg prop,
            Transform parent, int layer, string name)
        {
            var material = Wg3SignCatalog.Material();
            if (material == null) return null;
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave, layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.position =
                new Vector3(prop.xCm * 0.01f, prop.yCm * 0.01f, prop.zCm * 0.01f);
            go.transform.rotation = Quaternion.Euler(0f, prop.yawDeg, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = Wg3SignCatalog.MeshOf(prop.style);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.renderingLayerMask = Wg3StoreyLayers.ForLight(prop.yCm * 0.01f);
            return go;
        }

        /// <summary>
        /// ADR-105 enm. 20 — **lo que cuelga del falso techo roto**: una placa descolgada de un
        /// lado (kind 15) y una luminaria caída en diagonal (kind 16).
        ///
        /// No son prefabs y no son macizos. Prefab no, porque el pack de oficina no trae ni placa
        /// ni luminaria de techo y las dos son una caja fina; macizo tampoco, porque
        /// <c>Wg3Solid</c> sólo gira en Y (ADR-121 D1) y lo que hace legible una placa caída a
        /// medias es justo la INCLINACIÓN. Así que la bisagra la pone el cliente: el ancla que
        /// manda el servidor es el borde que sigue agarrado al techo, y la pieza baja desde ahí.
        ///
        /// Sin collider, como los paneles y como toda la decoración del techo: lo que cuelga a 2,3
        /// m no puede frenar a nadie, y el servidor tampoco lo estampa en el ráster.
        /// </summary>
        private static GameObject AssembleHungDecay(BackroomsSurvival.Net.Wg3PropMsg prop,
            Transform parent, int layer, string name, Wg3Materials materials, Material lampMaterial)
        {
            bool lamp = prop.kind == BackroomsSurvival.Net.Wg3PropMsg.LightHung;
            Material mat = lampMaterial;
            if (!lamp)
            {
                Material[] mats = Wg3StyleMaterials.Resolve(materials, prop.style);
                mat = mats != null && mats.Length > Wg3MeshBuilder.SubMesh.Ceiling
                    ? mats[Wg3MeshBuilder.SubMesh.Ceiling]
                    : null;
            }
            if (mat == null) return null;

            var size = lamp
                ? new Vector3(PanelLongM, 0.05f, PanelShortM)
                : new Vector3(CeilingTileM, 0.02f, CeilingTileM);
            float tilt = lamp ? HungLampTiltDeg : HungTileTiltDeg;

            var root = new GameObject(name);
            root.hideFlags = HideFlags.DontSave;
            root.transform.SetParent(parent, false);
            root.transform.position = new Vector3(prop.xCm * 0.01f, prop.yCm * 0.01f, prop.zCm * 0.01f);
            // La luminaria va girada respecto a la rejilla de placas: es lo que se lee como
            // "descolgada en diagonal" y no como "un panel más, torcido".
            root.transform.rotation = Quaternion.Euler(0f, prop.yawDeg + (lamp ? 25f : 0f), 0f);

            // La bisagra: el borde que sigue arriba. La pieza gira alrededor de él y su otro
            // extremo cae `largo · sen(tilt)` — 34 cm la placa, 45 la luminaria.
            var hinge = new GameObject("hinge");
            hinge.hideFlags = HideFlags.DontSave;
            hinge.transform.SetParent(root.transform, false);
            hinge.transform.localPosition = new Vector3(-size.x * 0.5f, -size.y * 0.5f, 0f);
            hinge.transform.localRotation = Quaternion.Euler(0f, 0f, -tilt);

            var go = new GameObject(lamp ? "lamp" : "tile");
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(hinge.transform, false);
            go.transform.localPosition = new Vector3(size.x * 0.5f, 0f, 0f);
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = LuminaireMesh();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.renderingLayerMask = Wg3StoreyLayers.ForLight(prop.yCm * 0.01f);
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
            return root;
        }

        /// <summary>ADR-105 enm. 20 — canto por debajo del cual una decoración es una LOSETA lisa y
        /// no un marco. Quince centímetros: la placa caída mide 4 y la baldosa 9, y la jamba de un
        /// marco mide dos metros.</summary>
        private const float FlatDecorationMaxM = 0.15f;

        /// <summary>Cuánto se descuelga una placa: 34° deja su punta a 2,36 m con techo de 2,70, o
        /// sea por encima de la cabeza y por debajo del plano del techo.</summary>
        private const float HungTileTiltDeg = 34f;
        /// <summary>La luminaria es el doble de larga, así que cae menos grados para dejar la misma
        /// holgura: 22° la bajan 45 cm.</summary>
        private const float HungLampTiltDeg = 22f;

        /// <summary>Placa del techo de la oficina: 60 cm. Los paneles se alinean a ella.</summary>
        public const float CeilingTileM = 0.6f;
        /// <summary>Panel fluorescente: dos placas de largo, una de ancho, como en la foto.</summary>
        public const float PanelLongM = 1.2f;
        public const float PanelShortM = 0.6f;
        /// <summary>Paso de la rejilla de paneles, en placas. Cuatro placas = 2,4 m: una fila de
        /// paneles cada dos metros y pico, que es lo que se ve en las referencias del Nivel 0.</summary>
        private const int PanelPitchTiles = 4;
        /// <summary>Tope de paneles por tramo: en una nave de 25 × 25 el paso se abre hasta
        /// cumplirlo. Son mallas, no luces, pero mil paneles en radio 1 también pesan.</summary>
        private const int MaxPanelsPerSegment = 40;

        /// <summary>
        /// Los paneles fluorescentes de un tramo, en rejilla alineada a las placas del techo. Se
        /// omite el que caería a menos de una placa de la pared. El eje largo del panel sigue el
        /// eje largo del tramo. Sin collider: es decoración del techo. Y para un tramo de doble
        /// altura cuelga con su planta de arriba (la del techo), que es la que lo alumbra.
        /// </summary>
        private static void AddPanels(Transform parent, Wg3Segment segment, Material lampMaterial)
        {
            float pitch = PanelPitchTiles * CeilingTileM;
            int cx = Mathf.Max(1, Mathf.FloorToInt(segment.SizeX / pitch));
            int cz = Mathf.Max(1, Mathf.FloorToInt(segment.SizeZ / pitch));
            while (cx * cz > MaxPanelsPerSegment)
            {
                pitch += CeilingTileM;
                cx = Mathf.Max(1, Mathf.FloorToInt(segment.SizeX / pitch));
                cz = Mathf.Max(1, Mathf.FloorToInt(segment.SizeZ / pitch));
            }
            bool alongX = segment.SizeX >= segment.SizeZ;
            var size = alongX
                ? new Vector3(PanelLongM, 0.05f, PanelShortM)
                : new Vector3(PanelShortM, 0.05f, PanelLongM);
            // Centrado: el sobrante de la rejilla se reparte a los dos lados.
            float ox = (segment.SizeX - cx * pitch) * 0.5f + pitch * 0.5f;
            float oz = (segment.SizeZ - cz * pitch) * 0.5f + pitch * 0.5f;
            float y = segment.Height - size.y * 0.5f + 0.01f;
            uint mask = Wg3StoreyLayers.ForLight(segment.FloorY + segment.Height - 0.1f);
            for (int ix = 0; ix < cx; ix++)
            {
                for (int iz = 0; iz < cz; iz++)
                {
                    float px = ox + ix * pitch, pz = oz + iz * pitch;
                    if (px < CeilingTileM + size.x * 0.5f || px > segment.SizeX - CeilingTileM - size.x * 0.5f
                        || pz < CeilingTileM + size.z * 0.5f || pz > segment.SizeZ - CeilingTileM - size.z * 0.5f)
                        continue;
                    var go = new GameObject($"panel_{ix}_{iz}");
                    go.hideFlags = HideFlags.DontSave;
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = new Vector3(px, y, pz);
                    go.transform.localScale = size;
                    go.AddComponent<MeshFilter>().sharedMesh = LuminaireMesh();
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = lampMaterial;
                    r.renderingLayerMask = mask;
                }
            }
        }

        /// <summary>
        /// UNA malla de cubo para todas las luminarias de la sesión.
        ///
        /// Antes cada una salía de <c>GameObject.CreatePrimitive</c>, que por lámpara carga el cubo
        /// de los recursos internos, le añade un <c>BoxCollider</c> y lo destruye acto seguido. Con
        /// ~900 lámparas en radio 1 eso era el grueso del tirón al montar un chunk, y todo para
        /// acabar en la misma caja escalada.
        ///
        /// **NO va a la lista de mallas del chunk**, y es deliberado: la comparten todos los chunks
        /// montados, así que podar uno la destruiría bajo los demás. Muere sola con la recarga de
        /// dominio o el cambio de escena, y el nulo la reconstruye — misma cautela que
        /// <c>Wg3StyleMaterials.Valid</c> toma con sus variantes, y por el mismo motivo: una malla
        /// destruida por debajo deja las luminarias invisibles sin decir por qué.
        /// </summary>
        private static Mesh _luminaireMesh;

        private static Mesh LuminaireMesh()
        {
            if (_luminaireMesh == null)
            {
                _luminaireMesh = Wg3MeshBuilder.BuildUnitCube();
                _luminaireMesh.name = "wg3_luminaire";
                _luminaireMesh.hideFlags = HideFlags.DontSave;
            }
            return _luminaireMesh;
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
            //
            // **El centro es de MUNDO, como en toda lista de volúmenes** (`BuildPlaced` suma el
            // origen de la colocación; `Wg3GeneratedSegment.Build` el del tramo). `Wg3MeshBuilder`
            // y `AddColliders` restan `origin` a cada centro para emitir coordenadas locales, así
            // que un centro local aquí se restaba dos veces: desde wire 50 (2026-08-27) TODOS los
            // macizos —pilares, pretiles, tabiques, vigas— se dibujaban y colisionaban apilados en
            // el origen del mundo, y en su sitio quedaba sólo el ráster del servidor: paredes
            // invisibles. Lo destapó una captura en (3, 0, 14) con un bloque de 4 m plantado en
            // (0, 0) y ninguna cruz de 3 m donde el plan la ponía (2026-09-04).
            var volumes = new List<Wg3Volume>(1)
            {
                new Wg3Volume
                {
                    center = origin + new Vector3(sx * 0.5f, sy * 0.5f, sz * 0.5f),
                    size = new Vector3(sx, sy, sz),
                    // ADR-121 D1 — el giro es alrededor del centro de la huella, que es el que
                    // acabamos de calcular; la caja del cable es la caja SIN girar.
                    yawDegrees = solid.yawDeg,
                    // ADR-125 — la forma dentro de la huella.
                    shape = solid.shape,
                    // ADR-125 enm. 2 — un marco es decoración: submalla de decoración y sin
                    // collider (`IsSolid` falso), igual que un rodapié. `Casing` y no `Decoration`
                    // para que el constructor le talle el perfil y el zócalo.
                    //
                    // ADR-105 enm. 20 — salvo una LOSETA: una placa de techo caída (4 cm de canto)
                    // o una baldosa levantada (9) son cajas lisas, y el perfil de dos escalones y
                    // el zócalo del marco sobre una pieza de dos centímetros no son un marco, son
                    // ruido. El corte va por el canto porque un marco es una banda de dos metros.
                    kind = !solid.IsDecoration
                        ? Wg3VolumeKind.Pillar
                        : (sy <= FlatDecorationMaxM ? Wg3VolumeKind.Decoration : Wg3VolumeKind.Casing),
                }
            };

            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(parent, false);
            go.transform.position = origin;

            // ADR-129 D2 — el macizo INVISIBLE: sólo collider. La malla la pone el prefab del
            // mueble que va encima; dibujar la caja aquí la mostraría debajo de la mesa.
            if (solid.IsHidden)
            {
                AddColliders(go, volumes, origin);
                return go;
            }

            Mesh mesh = Wg3MeshBuilder.Build(volumes, origin);
            mesh.name = $"wg3_{name}";
            mesh.hideFlags = HideFlags.DontSave;
            createdMeshes?.Add(mesh);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            Material[] mats = Wg3StyleMaterials.Resolve(materials, solid.BaseStyle);
            // ADR-105 enm. 20 — una loseta se dibuja con el material del TECHO: una placa caída es
            // la que falta arriba, y la submalla de decoración traería el material del rodapié.
            if (mats != null && solid.IsDecoration && sy <= FlatDecorationMaxM
                && mats.Length > Wg3MeshBuilder.SubMesh.Decoration)
            {
                mats = (Material[])mats.Clone();
                // Y la baldosa levantada (9 cm) con el del SUELO, que es de donde sale.
                mats[Wg3MeshBuilder.SubMesh.Decoration] = sy <= 0.05f
                    ? mats[Wg3MeshBuilder.SubMesh.Ceiling]
                    : mats[Wg3MeshBuilder.SubMesh.Floor];
            }
            if (mats != null) renderer.sharedMaterials = mats;
            // Un megapilar cruza el atrio de suelo a techo, así que lleva las dos plantas y se
            // ilumina desde las dos. Un pretil vive en una sola.
            renderer.renderingLayerMask = Wg3StoreyLayers.ForSurface(origin.y, sy);

            if (solid.IsDecoration)
            {
                // Sin collider: es lo que permite que un marco sobresalga 2 cm de la pared sin
                // cerrar media celda del ráster ni frenar al jugador contra la jamba.
            }
            else if (solid.shape == Wg3Shape.Box)
            {
                AddColliders(go, volumes, origin);
            }
            else
            {
                // ADR-125 — un prisma frena con SU malla, convexa: cilindro, media luna y octógono
                // son convexos, y un `CapsuleCollider` a la altura del pilar metería sus casquetes
                // dos metros dentro del forjado de la planta de abajo.
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                // El arco es cóncavo (el intradós): malla sin casco convexo, que para un collider
                // estático contra un CharacterController es igual de válida.
                mc.convex = solid.shape != Wg3Shape.Arch;
            }
            return go;
        }

        private static void AddColliders(GameObject root, List<Wg3Volume> volumes, Vector3 origin)
        {
            for (int v = 0; v < volumes.Count; v++)
            {
                Wg3Volume vol = volumes[v];
                if (!vol.IsSolid) continue;
                // ADR-125 — los prismas llevan collider de malla, que pone `AssembleSolid`.
                if (vol.shape != Wg3Shape.Box) continue;

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
        private static void AddCeilingLight(GameObject root, Wg3Placement placement,
            int worldSeed, Wg3LightCadenceSettings cadence)
        {
            // La celda de este fixture es la pieza entera: es el único que hay. El jitter queda
            // acotado igual por `jitterMaxMeters`, así que en una pieza grande no se va al rincón.
            float nominalX = placement.SizeX * 0.5f;
            float nominalZ = placement.SizeZ * 0.5f;
            Wg3Fixture fixture = Wg3LightCadence.Resolve(worldSeed,
                placement.originX + nominalX, placement.originZ + nominalZ,
                0, placement.SizeX, placement.SizeZ, cadence);

            // Un tubo muerto aquí es sólo ausencia de Light: esta ruta no dibuja luminaria (ver el
            // R32 pendiente de arriba). Queda el hueco marcado en la jerarquía.
            if (!fixture.lit)
            {
                var dead = new GameObject("light_off");
                dead.transform.SetParent(root.transform, false);
                dead.transform.localPosition = new Vector3(
                    nominalX, placement.piece.heightMeters - 0.25f, nominalZ);
                return;
            }

            float marginX = Mathf.Min(1.0f, placement.SizeX * 0.5f);
            float marginZ = Mathf.Min(1.0f, placement.SizeZ * 0.5f);

            var go = new GameObject("light");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(
                Mathf.Clamp(nominalX + fixture.offset.x, marginX, placement.SizeX - marginX),
                placement.piece.heightMeters - 0.25f,
                Mathf.Clamp(nominalZ + fixture.offset.y, marginZ, placement.SizeZ - marginZ));

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.97f, 0.88f) * fixture.tint;
            // El doble, por lo mismo y a la vez que el plafón de tramo: dos sistemas de luz con
            // intensidades que se separan al doble dejan las piezas del catálogo leyéndose como
            // agujeros oscuros dentro de una sala ya iluminada.
            light.intensity = 3.2f;
            // Acotado a 9 m: la fórmula abierta llegaba a 21,75 m en la pieza más grande, y una
            // puntual así cruza decenas de clusters de Forward+ ella sola. Una pieza grande queda
            // con penumbra en los bordes hasta que declare sus propias luces (R32), que es el plan.
            light.range = Mathf.Min(Mathf.Max(placement.SizeX, placement.SizeZ) * 0.75f + 6f, 9f);
            light.shadows = LightShadows.None;
            // Sólo su planta, igual que el plafón de un tramo. Éste es el que más alcance tiene
            // —hasta 21,75 m— así que es el que peor filtraba.
            light.renderingLayerMask = (int)Wg3StoreyLayers.ForLight(root.transform.position.y);

            if (fixture.flickers)
            {
                var flicker = go.AddComponent<BackroomsSurvival.Gameplay.World.LampFlicker>();
                flicker.target = light;
                flicker.baseIntensity = light.intensity;
                flicker.frequency = fixture.flickerHz;
                flicker.phase = fixture.flickerPhase;
            }
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
