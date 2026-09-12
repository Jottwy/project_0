using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;

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
    ///
    /// R6 (12-09) — <see cref="storeys"/>. Un chunk WG3 mide 50 m en XZ y NO se parte en Y, así que
    /// puede meter siete u ocho plantas en un solo alta. El director aislaba por planta con UN valor
    /// por LOTE entero (el que ya usaba WG2, donde un chunk sí es una sola planta); aplicado a un
    /// lote de WG3 eso comparaba la planta de la primera lámpara contra todas las demás — o, peor,
    /// un valor que nadie llegó a fijar nunca (ver el commit que añade esto). La planta va por
    /// LÁMPARA, calculada aquí con la misma <see cref="Wg3StoreyLayers.RawStoreyOf"/> que ya reparte
    /// la luz, para que oído y ojo estén de acuerdo en qué es «tu planta».
    /// </summary>
    public sealed class Wg3HumBatch
    {
        public readonly List<Vector3> positions = new List<Vector3>();
        public readonly List<float> pitches = new List<float>();
        public readonly List<float> flickerHz = new List<float>();
        public readonly List<float> flickerPhase = new List<float>();
        public readonly List<int> storeys = new List<int>();
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

        /// <summary>
        /// ADR-130 D4.1 — el DECAIMIENTO de una cota. Espejo exacto de
        /// <c>fill::decay_of_floor</c>: la calle da 0 y el sótano más hondo da 1.
        /// </summary>
        /// <remarks>
        /// **No viaja por el cable, y no hace falta que viaje.** Los dos números de los que sale
        /// —la altura de planta y cuántos sótanos hay— ya están aquí arriba como espejos de
        /// <c>plan::STOREY_HEIGHT_CM</c> y <c>plan::REGION_BASEMENTS</c>, y la cota la trae cada
        /// tramo. Un campo nuevo en el mensaje sería una tercera copia del mismo dato, y una que
        /// además podría discrepar de las otras dos.
        ///
        /// El corolario es que si allí cambia el número de sótanos, aquí también: el servidor
        /// decaería el relleno de una planta y el cliente le apagaría las luces a otra.
        ///
        /// **El cuadrado es de D4 y el denominador es el fondo SERVIDO**, no los cien metros de
        /// D1: con tres sótanos, <c>depth²</c> sobre cien metros vale 0,01 en B3 y no se ve. Los
        /// dos extremos son los mismos, así que el día que <see cref="BasementLayers"/> sean
        /// treinta esta división ya es la curva del ADR sin tocar nada.
        /// </remarks>
        public static float DecayOfFloor(float floorY)
        {
            if (floorY >= 0f) return 0f;
            float below = -floorY / StoreyM;
            float d = Mathf.Clamp01(below / Mathf.Max(1, BasementLayers));
            return d * d;
        }

        /// <summary>Plantas reales por encima de la calle (ADR-102 D3): 4, planta 0 a 3.</summary>
        private const int UpperBoostCapStorey = 3;

        /// <summary>Cuánto sube la intensidad de una luz por cada planta sobre la calle.</summary>
        private const float UpperBoostPerStorey = 0.15f;

        /// <summary>
        /// Multiplicador de intensidad por subir de planta (Joel, 07-09: plantas más altas, luz más
        /// potente). Sólo cuenta hacia ARRIBA: un sótano ya tiene su propio recorte con
        /// <see cref="DecayOfFloor"/>, y sumar los dos a la vez dejaría la calle —planta 0, sin
        /// recorte y sin refuerzo— como el punto más oscuro del mundo servido, justo al revés de
        /// lo que pide esto.
        /// </summary>
        public static float UpperFloorBoost(float floorY)
        {
            if (floorY <= 0f) return 1f;
            int storey = Mathf.Clamp(RawStoreyOf(floorY), 0, UpperBoostCapStorey);
            return 1f + UpperBoostPerStorey * storey;
        }

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

        /// <summary>
        /// La capa de una LUZ, puesta donde URP la lee.
        /// </summary>
        /// <remarks>
        /// **URP no mira <c>Light.renderingLayerMask</c>.** `ForwardLights.cs:540` toma la máscara
        /// de <c>UniversalAdditionalLightData.renderingLayers</c>, un campo serializado aparte que
        /// nace en 1 y que el setter sincroniza hacia el <c>Light</c>, nunca al revés. Desde que el
        /// reparto por plantas existe, todo este fichero escribía en el <c>Light</c>: URP lo
        /// ignoraba, cada lámpara se quedaba en la capa 1 y ninguna superficie WG3 (capas ≥ 2)
        /// recibía luz de NINGUNA fuente en tiempo real. Lo que se veía eran el ambiente plano y los
        /// light probes de la escena. Cazado el 07-09 con la linterna encendida a dos metros de una
        /// pared y sin cono.
        /// </remarks>
        public static void Apply(Light light, uint mask)
        {
            light.GetUniversalAdditionalLightData().renderingLayers = mask;
        }

        /// <summary>
        /// La capa de una SUPERFICIE, y sin light probes.
        /// </summary>
        /// <remarks>
        /// Un renderer creado en runtime muestrea por defecto los light probes horneados de la
        /// escena, y cuando los hay Unity usa ESOS en lugar del ambiente de <c>RenderSettings</c>.
        /// `STP_Showcase` arrastra el <c>LightingDataAsset</c> del demo del vendor, horneado al
        /// aire libre: el mundo WG3 se leía con un cielo que no existe — paredes tenues, suelo y
        /// techo negros — y el ambiente que este proyecto fija ni se consultaba. Sin probes, lo que
        /// ilumina es lo que este código decide: las lámparas y el ambiente.
        /// </remarks>
        public static void Apply(Renderer renderer, uint mask)
        {
            renderer.renderingLayerMask = mask;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
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
                    Wg3StoreyLayers.Apply(pr, pieceMask);
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
            // ADR-105 enm. 19 — y el segundo eje: un despacho lleva moqueta de oficina, y si además
            // le bajaron el techo (enm. 18) la placa de 60 con su perfil en T.
            Material[] mats = Wg3StyleMaterials.Resolve(materials, segment.style,
                Wg3Looks.ForSegment(segment.style, segment.heightCm));
            if (mats != null) renderer.sharedMaterials = mats;
            // Un atrio mide dos plantas, así que pide las dos capas y lo alumbran los plafones de
            // arriba y los de abajo. Una sala normal pide una sola, y ahí muere la fuga.
            Wg3StoreyLayers.Apply(renderer,
                Wg3StoreyLayers.ForSurface(segment.FloorY, segment.Height));

            // R5 — el sonido del paso, por el mismo papel que ya viste la superficie.
            AddColliders(go, volumes, origin, Wg3StyleSurfaces.FloorFor(segment.style));

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
        /// **La intensidad y el color siguen sin tocarse**: son valores que Joel validó mirándolos
        /// en partida. Lo que SÍ cambia (R3, 12-09, con su autorización explícita) es el ALCANCE: ver
        /// <see cref="BoundedRange"/> más abajo, una lámpara ya no puede alumbrar más allá de su
        /// propio tramo más el grosor de una pared, así que una sala de 4 m deja de regar las dos
        /// vecinas con un alcance pensado para naves de 25.
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
            // El 07-09 se probó 3 × 3 (Joel: más luces) y se DESHIZO el mismo día: hasta ese día
            // ninguna lámpara tocaba el mundo (ver `Wg3StoreyLayers.Apply`), así que «pocas luces»
            // se estaba juzgando sobre un mundo sin luces. Primero verlas funcionar a 2 × 2; subir
            // la densidad es cosa de una constante, y con el tope de 256 de Forward+ a la vista.
            //
            // 08-09: las dos condiciones que pedía el párrafo de arriba ya se cumplen. Las
            // lámparas funcionan —verificado en juego con capturas— y el tope ya no es 256: el
            // paquete `com.unity.render-pipelines.universal-config` está embebido en `Packages/`
            // con `k_MaxVisibleLightCountDesktop` a 512, precisamente porque el censo medía entre
            // 264 y 332 luces en frustum en una planta de oficinas y URP tiraba las sobrantes sin
            // decir nada. Con 3 × 3 el techo se lee como una rejilla de fluorescentes y no como
            // cuatro charcos sueltos, que es el aspecto Level 0 que se busca.
            const int MaxPerAxis = 3;
            // A partir de aquí el techo es alto y el plafón pasa a colgar.
            const float HangHeight = 3f;
            // Altura del relleno, en metros sobre el suelo del tramo. A la altura del pecho, que
            // es de donde viene el rebote real (ver el bloque que lo crea). No se toca sin leer
            // eso: subirlo devuelve el techo negro con un halo, que es el fallo que corrigió.
            const float FillHeight = 1.2f;
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

            // ADR-130 D4 (r2b) — el decaimiento de ESTA planta. Se calcula una vez por tramo y se
            // reparte a todo lo que lo mira: las luminarias del falso techo, los umbrales de la
            // cadencia y el color de cada lámpara. En la calle vale 0 y nada de lo de abajo cambia.
            float decay = Wg3StoreyLayers.DecayOfFloor(segment.FloorY);

            // Cadencia de luces (rama unity-lighting-cadence, fusionada el 2026-09-06). La celda de
            // cada fixture es lo que acota el jitter: un plafón se mueve dentro de SU celda y nunca
            // invade la del vecino, así que el ritmo se rompe sin que dos plafones se junten.
            float cellX = segment.SizeX / nx;
            float cellZ = segment.SizeZ / nz;
            // Un metro de margen, o la mitad del tramo si es más estrecho: el jitter no puede pegar
            // la luz a la pared.
            float marginX = Mathf.Min(1.0f, segment.SizeX * 0.5f);
            float marginZ = Mathf.Min(1.0f, segment.SizeZ * 0.5f);

            // EL DESPACHO A OSCURAS: uno por planta y chunk con todas las lámparas muertas, no el
            // 12 % que le tocaría por la cadencia. La regla y el porqué del sorteo por punto están en
            // `Wg3LightCadence.IsDarkOffice`; aquí sólo se fuerza el resultado, y se fuerza ANTES de
            // la tirada de cada fixture para no correr ninguna: el mundo alrededor no se mueve.
            bool blackout = Wg3LightCadence.IsDarkOffice(worldSeed, segment.style,
                Wg3StoreyLayers.RawStoreyOf(segment.FloorY),
                segment.MinX, segment.MinZ, segment.SizeX, segment.SizeZ);

            // Día 2 del cierre (Joel: la foto de la oficina) — los PANELES fluorescentes de 60×120
            // en rejilla sobre las placas del techo, en vez de una luminaria cuadrada por lámpara.
            // Son mallas emisivas, no luces: las luces siguen siendo las de siempre (≤ 2 × 2 por
            // tramo), así que Forward+ no paga nada por esto.
            //
            // R3 (12-09, Joel autorizó tocar esto) — necesitan saber DÓNDE quedan las luces REALES
            // del tramo antes de dibujarse: la auditoría de rendimiento contó 12 587 paneles contra
            // 3 101 Light, y hasta ahora los 12 587 brillaban igual sin que la mayoría iluminara
            // nada. Se resuelven aquí las mismas posiciones que el bucle de abajo va a construir
            // —misma llamada a `Wg3LightCadence.Resolve`, determinista por posición— y el bucle de
            // abajo las vuelve a resolver para levantar el `GameObject`: es la misma función pura
            // sobre los mismos números, no una segunda fuente de verdad que pueda discrepar.
            if (lampMaterial != null)
            {
                List<Vector2> lit = ResolveLitFixtureOffsets(segment, worldSeed, cadence, decay,
                    blackout, cellX, cellZ, nx, nz);
                AddPanels(go.transform, segment, lampMaterial, decay, lit);
            }

            // LA SOMBRA VA A LA PRIMERA ENCENDIDA, no a la de índice (0,0). Con un 12 % de plafones
            // muertos, atar la sombra al índice deja una nave de cada ocho sin ninguna sombra —
            // justo las que se apuntalaron con esto.
            bool shadowTaken = false;
            // Por el mismo motivo que la sombra: el relleno va a la primera ENCENDIDA del tramo.
            bool fillTaken = false;

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
                        ix * nz + iz, cellX, cellZ, cadence, decay);

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

                    // R3 — se guardan por separado porque BoundedRange las necesita después,
                    // y recalcular el mismo Clamp dos veces es la clase de duplicación que acaba
                    // divergiendo el día que alguien toque un solo sitio.
                    float lampLocalX = Mathf.Clamp(nominalX + fixture.offset.x, marginX, segment.SizeX - marginX);
                    float lampLocalZ = Mathf.Clamp(nominalZ + fixture.offset.y, marginZ, segment.SizeZ - marginZ);

                    var lamp = new GameObject($"light_{ix}_{iz}");
                    lamp.hideFlags = HideFlags.DontSave;
                    lamp.transform.SetParent(go.transform, false);
                    lamp.transform.localPosition = new Vector3(lampLocalX, y, lampLocalZ);

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
                    // R3 (12-09) — 11 sigue siendo el TECHO validado por Joel; lo nuevo es
                    // que no se entrega siempre: BoundedRange lo recorta a lo que hace falta
                    // para bañar la esquina más lejana de ESTE tramo (más el grosor de una
                    // pared), así que una nave sigue con 11 y una sala de 4 m deja de
                    // atravesar dos vecinas para llegar a ningún sitio.
                    light.range = BoundedRange(lampLocalX, lampLocalZ, segment.SizeX, segment.SizeZ, y, 11f);
                    // **El doble de 1,35, a petición de Joel tras el playtest del 2026-09-05.** Con
                    // 1,35 y el ambiente plano en 0,30 una sala corriente se leía a media luz y había
                    // que acercarse a una pared para ver de qué color era. No sube el coste de
                    // Forward+: el clustering cuenta volumen y luces, y ni el alcance ni el número
                    // cambian aquí.
                    // 3,1 desde el 07-09 (Joel: más Level 0), reforzado por planta con
                    // UpperFloorBoost (Joel, mismo día: plantas más altas, luz más potente). Sigue
                    // sin tocar el rango: el clustering de Forward+ cuenta luces y volumen, no
                    // intensidad.
                    light.intensity = 3.1f * Wg3StoreyLayers.UpperFloorBoost(segment.FloorY);
                    // El color validado, empujado ±200 K por el tinte y después llevado hacia el gris
                    // por la profundidad (ADR-130 D4). El orden importa: el decaimiento va DESPUÉS
                    // del producto porque lo que hay que desaturar es el cálido, no el cociente.
                    light.color = Wg3LightCadence.Decayed(
                        new Color(1f, 0.96f, 0.78f) * fixture.tint, decay);
                    // La PRIMERA ENCENDIDA de un tramo grande proyecta; las demás no. Con 2 × 2 como
                    // tope por eje, eso es una de cuatro en el peor caso.
                    if (wantsShadow && !shadowTaken)
                    {
                        shadowTaken = true;
                        // Sin bajar la fuerza, el contacto sale negro: la escena tiene ambiente
                        // cálido y una sola puntual sin rebote, así que la sombra dura se lee como
                        // agujero. Tres cuartos deja el volumen y no mata la lectura. Se fija
                        // AUNQUE el presupuesto (R4) la deje en None por ahora: Unity ignora estos
                        // dos campos sin sombra, y quedan listos para cuando el jugador se acerque
                        // y Wg3ShadowBudget se la reclame a otra más lejana.
                        light.shadowStrength = 0.72f;
                        light.shadowNearPlane = 0.3f;
                        // R4 — la candidata de ESTE tramo entra a competir por el tope GLOBAL de
                        // sombras del mundo cargado; no se enciende aquí directamente.
                        Wg3ShadowBudget.Register(light);
                    }
                    else
                    {
                        light.shadows = LightShadows.None;
                    }
                    // SOLO su planta. Es la mitad de la regla que cierra la fuga, y la que no se
                    // puede deducir mirando el objeto: un plafón parece inofensivo.
                    uint mask = Wg3StoreyLayers.ForLightIn(segment.FloorY, segment.Height);
                    Wg3StoreyLayers.Apply(light, mask);

                    // EL RELLENO, y es un sucedáneo de rebote, no una lámpara.
                    //
                    // URP 17.0.4 no tiene iluminación indirecta que sirva aquí: los lightmaps y
                    // las sondas adaptativas exigen hornear la escena, y un mundo servido por
                    // chunks no se hornea. Sin rebote, una puntual con el ambiente en negro deja
                    // el suelo iluminado y la pared de enfrente a cero, que es lo contrario de
                    // Level 0 —donde el techo entero de fluorescentes lava la sala hasta que las
                    // esquinas casi no tienen sombra—.
                    //
                    // Una segunda puntual de alcance largo e intensidad baja imita ese lavado a
                    // cambio de UNA luz más. Va sólo en la primera encendida del tramo, no en las
                    // cuatro: el tope de Forward+ cuenta luces, y cuadruplicar el censo para
                    // simular un rebote que es plano por definición no compra nada.
                    // Sin sombras y sin parpadeo a propósito: un rebote que parpadea se lee como
                    // una segunda lámpara estropeada, no como luz indirecta.
                    if (!fillTaken)
                    {
                        fillTaken = true;
                        // GameObject propio y no un segundo Light sobre el plafón: `GetComponent
                        // <Light>` devuelve UNO, y el relay de ADR-042 y el zumbido de ADR-107
                        // resuelven la lámpara por ahí. Dos luces en el mismo objeto convierten
                        // «cuál de las dos» en una tirada de orden de componentes.
                        var fillGo = new GameObject("light_fill");
                        fillGo.hideFlags = HideFlags.DontSave;
                        fillGo.transform.SetParent(lamp.transform, false);
                        // **EL RELLENO VA ABAJO, Y ES LA RAZÓN DE QUE EL TECHO SE VEA.**
                        //
                        // Nació colgado del plafón, o sea a 30 cm del techo, y ahí no ilumina el
                        // techo: lo alumbra de refilón. La cantidad de luz que recibe una
                        // superficie va con el coseno del ángulo entre su normal y el rayo; la
                        // normal del techo mira hacia abajo, así que una luz pegada a él le llega
                        // casi paralela y el coseno se va a cero a un metro de distancia. De ahí
                        // el resultado que se veía: un halo pequeño y quemado alrededor de cada
                        // panel, y el resto del techo negro.
                        //
                        // A la altura del pecho el rayo sube casi vertical, el coseno vale casi
                        // uno en todo el paño, y de paso es de donde viene el rebote de verdad:
                        // la luz que ilumina un techo real ha botado antes en el SUELO. Es el
                        // mismo truco de siempre —una luz falsa puesta donde estaría el rebote—,
                        // sólo que hasta ahora estaba puesta en el sitio contrario.
                        fillGo.transform.localPosition = new Vector3(0f, FillHeight - y, 0f);
                        var fill = fillGo.AddComponent<Light>();
                        fill.type = LightType.Point;
                        // R3 (12-09) — 18 sigue siendo el TECHO validado; BoundedRange lo recorta con
                        // la distancia desde el pecho hasta la esquina de TECHO más lejana (no la de
                        // suelo: el relleno vive a la altura del pecho y su volumen es el que sube).
                        fill.range = BoundedRange(lampLocalX, lampLocalZ, segment.SizeX, segment.SizeZ,
                            Mathf.Max(segment.Height - FillHeight, 0.1f), 18f);
                        // 0,30 y no 0,22: al bajarlo dos metros, el techo queda a más del doble de
                        // distancia y la caída va con el cuadrado. Es la misma luz en el techo que
                        // antes, repartida por todo el paño en vez de amontonada en un círculo.
                        fill.intensity = light.intensity * 0.3f;
                        // Hacia el gris: el rebote de una pared beige no devuelve el cálido de la
                        // lámpara, lo lava. Sin esto el relleno tiñe la sala de amarillo.
                        fill.color = Color.Lerp(light.color, Color.white, 0.5f);
                        fill.shadows = LightShadows.None;
                        Wg3StoreyLayers.Apply(fill, mask);
                    }

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
                        // R6 — la planta del TRAMO, no la de la lámpara: es la misma cota que usa
                        // Wg3StoreyLayers para la luz (ForLight/ForSurface parten de FloorY, nunca
                        // de dónde cuelga el plafón), así que el corte de «tu planta» del zumbido
                        // cae exactamente donde cae el de la luz.
                        hum.storeys.Add(Wg3StoreyLayers.RawStoreyOf(segment.FloorY));
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
        /// R3 (12-09, con autorización explícita de Joel para tocar estos números) — el alcance de
        /// una Light acotado a lo que hace falta para bañar SU propio tramo, nunca más.
        /// </summary>
        /// <remarks>
        /// Antes del reparto por plantas (<see cref="Wg3StoreyLayers"/>) el problema era que una
        /// lámpara no llegaba a NADA; cerrado eso, apareció el segundo: sin sombra propia (la mayoría
        /// de las luces del tramo no proyectan, ver <see cref="ShadowMinSide"/> más arriba) Forward+
        /// no sabe que hay una pared entre dos salas — el alcance fijo de 11 m (plafón) y 18 m
        /// (relleno) se pensó para naves de hasta 25 m y una sala de 4 m se llevaba el mismo alcance,
        /// regando hasta dos salas vecinas por encima y por los lados.
        ///
        /// **La cota es geométrica, no un número inventado.** Se mide la distancia real desde la
        /// lámpara hasta la esquina MÁS LEJANA de su propio tramo —la diagonal en planta más la
        /// altura, con Pitágoras— y se le suma el grosor de una pared (espejo de
        /// <c>segment::WALL_THICKNESS_M</c>, el mismo 0,15 que ya lleva <see cref="Wg3Piece"/>) más un
        /// margen para que la esquina no quede justo en el borde del apagado suave de Unity, que
        /// empieza antes del corte. El resultado nunca SUBE del alcance validado: `Mathf.Min` deja
        /// una nave de 25 m exactamente como estaba.
        /// </remarks>
        private static float BoundedRange(float localX, float localZ, float sizeX, float sizeZ,
            float heightAboveFloor, float maxRange)
        {
            // Espejo de `segment::WALL_THICKNESS_M` — el mismo grosor que ya trae
            // `Wg3Piece.wallThickness`. Que la lámpara siga bañando la cara interior de SU pared es
            // la intención; que atraviese la del vecino no lo es.
            const float WallThicknessM = 0.15f;
            float dxFar = Mathf.Max(localX, sizeX - localX);
            float dzFar = Mathf.Max(localZ, sizeZ - localZ);
            float horizontalToFarCorner = Mathf.Sqrt(dxFar * dxFar + dzFar * dzFar);
            float toFarCorner = Mathf.Sqrt(
                horizontalToFarCorner * horizontalToFarCorner + heightAboveFloor * heightAboveFloor);
            // El ×1,25 deja la esquina más lejana al 80 % del alcance nuevo: dentro de la caída por
            // inverso del cuadrado, no en la rampa de apagado suave de Unity (el último tramo del
            // alcance, donde Unity fuerza la intensidad a cero aunque la fórmula diera más). Sin el
            // margen, una sala pequeña dejaría su propia esquina casi a oscuras.
            return Mathf.Min(maxRange, toFarCorner * 1.25f + WallThicknessM);
        }

        /// <summary>
        /// R3 — las posiciones LOCALES (x, z del tramo) de las luces que de verdad van a encenderse
        /// en este tramo, para que <see cref="AddPanels"/> sepa qué panel atenuar.
        /// </summary>
        /// <remarks>
        /// Resuelve el MISMO fixture que el bucle principal de <see cref="AddSegmentLights"/>, con
        /// los mismos argumentos: `Wg3LightCadence.Resolve` es una función pura sembrada por
        /// posición de mundo (no por RNG con estado), así que llamarla dos veces con los mismos
        /// números da el mismo resultado — no es una segunda fuente de verdad, es la misma consultada
        /// antes de que el bucle principal construya sus `GameObject`.
        /// </remarks>
        private static List<Vector2> ResolveLitFixtureOffsets(Wg3Segment segment, int worldSeed,
            Wg3LightCadenceSettings cadence, float decay, bool blackout,
            float cellX, float cellZ, int nx, int nz)
        {
            var positions = new List<Vector2>(blackout ? 0 : nx * nz);
            if (blackout) return positions;
            for (int ix = 0; ix < nx; ix++)
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    float nominalX = cellX * (ix + 0.5f);
                    float nominalZ = cellZ * (iz + 0.5f);
                    Wg3Fixture fixture = Wg3LightCadence.Resolve(worldSeed,
                        segment.MinX + nominalX, segment.MinZ + nominalZ,
                        ix * nz + iz, cellX, cellZ, cadence, decay);
                    if (fixture.lit)
                        positions.Add(new Vector2(nominalX + fixture.offset.x, nominalZ + fixture.offset.y));
                }
            }
            return positions;
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
            Wg3StoreyLayers.Apply(light, mask);

            if (lampMaterial == null) return;
            var plate = new GameObject("emergency_plate");
            plate.hideFlags = HideFlags.DontSave;
            plate.transform.SetParent(go.transform, false);
            plate.transform.localPosition = new Vector3(0f, 0.10f, 0f);
            plate.transform.localScale = new Vector3(0.22f, 0.04f, 0.10f);
            plate.AddComponent<MeshFilter>().sharedMesh = LuminaireMesh();
            var r = plate.AddComponent<MeshRenderer>();
            r.sharedMaterial = EmissiveVariant(lampMaterial, EmergencyGreen, 2.2f);
            Wg3StoreyLayers.Apply(r, mask);
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
        /// R3 (12-09) — el panel al <see cref="PanelDimFactor"/> cuando ninguna Light real anda cerca.
        /// </summary>
        /// <remarks>
        /// Todos los paneles de un tramo comparten UN <c>lampMaterial</c> (mismo motivo que
        /// <see cref="EmissiveVariant"/>: por SRP Batcher), así que no se puede apagar un panel suelto
        /// sin crear una segunda variante — igual que el monitor o la emergencia, cacheada una vez por
        /// material fuente y compartida por toda la sesión. Sólo escala la EMISIÓN: el albedo del
        /// difusor no cambia, es la misma placa, sólo que su tubo de detrás alumbra menos.
        /// </remarks>
        private static readonly Dictionary<Material, Material> DimmedPanelCache =
            new Dictionary<Material, Material>();

        private static Material DimmedPanelVariant(Material source)
        {
            if (source == null) return null;
            if (DimmedPanelCache.TryGetValue(source, out Material cached) && cached != null) return cached;

            var m = new Material(source) { hideFlags = HideFlags.DontSave };
            m.name = $"{source.name}_dim";
            if (m.HasProperty(EmissionColorId))
                m.SetColor(EmissionColorId, source.GetColor(EmissionColorId) * PanelDimFactor);
            DimmedPanelCache[source] = m;
            return m;
        }

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
            int layer, string name, long worldSeed, Wg3Materials materials = null, Material lampMaterial = null)
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
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) Wg3StoreyLayers.Apply(r, mask);
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;

            // ADR-145 D1/D2/D4/D5 — de los 18 kinds físicos, el que tenga clase de material se
            // puede desmontar. Va DESPUÉS de apagar los colliders del pack (arriba): el hitbox
            // propio que añade es OTRO GameObject, sin relación con esos.
            var harvestClass = BackroomsSurvival.Net.Wg3PropHarvest.ClassFor(prop.kind);
            if (harvestClass.HasValue)
                MakeHarvestable(go, prop, worldSeed, harvestClass.Value);

            // EL MONITOR ENCENDIDO, uno de cada cinco. Va DESPUÉS del bucle de máscaras a propósito:
            // ese bucle pisa el `renderingLayerMask` de todos los renderers, y la pantalla necesita
            // el suyo igual que el resto del mueble — lo que cambia es el material, no la capa.
            if (prop.kind == BackroomsSurvival.Net.Wg3PropMsg.Monitor
                && Wg3LightCadence.MonitorLit(prop.xCm, prop.yCm, prop.zCm))
                LightMonitorScreen(go);
            return go;
        }

        private static readonly Dictionary<BackroomsSurvival.Net.Wg3PropHarvest.MaterialClass,
            PolymindGames.ResourceHarvesting.HarvestableResourceDefinition> _harvestDefCache = new();
        private static readonly HashSet<BackroomsSurvival.Net.Wg3PropHarvest.MaterialClass> _warnedHarvestDef = new();

        private static readonly FieldInfo _hrResourceDefinitionField = typeof(PolymindGames.ResourceHarvesting.HarvestableResource)
            .GetField("_resourceDefinition", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _hrUnharvestedField = typeof(PolymindGames.ResourceHarvesting.HarvestableResource)
            .GetField("_unharvestedObject", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _hrPartiallyField = typeof(PolymindGames.ResourceHarvesting.HarvestableResource)
            .GetField("_partiallyHarvestedObject", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _hrEventsField = typeof(PolymindGames.ResourceHarvesting.HarvestableResource)
            .GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance);
        private static bool _hrReflectionWarned;

        /// <summary>
        /// ADR-145 D4/D2/D5 — un hitbox PROPIO (no el del pack, apagado arriba) en la capa
        /// `StaticObject` (14): libre en todo el proyecto salvo esto, así que se editó la matriz de
        /// físicas para que `Character` YA NO colisione con ella — entra en la máscara fija de
        /// `MeleeHarvestAttack` (Default|StaticObject|DynamicObject) sin bloquear movimiento. El
        /// macizo `Wg3Solid` sigue siendo lo único que frena, sin doble colisión.
        ///
        /// Va en un GameObject hijo DEDICADO (no en <paramref name="go"/>): `HarvestableResource`
        /// exige un <c>Collider</c> en su MISMO GameObject (`[RequireComponent]`, y su `Awake` hace
        /// `GetComponent&lt;Collider&gt;()`, no `GetComponentInChildren`), y el prefab del pack
        /// puede traer collider en su propia raíz — poner el nuevo ahí sería ambiguo sobre cuál
        /// encuentra `HarvestableResource`.
        /// </summary>
        private static void MakeHarvestable(GameObject go, BackroomsSurvival.Net.Wg3PropMsg prop,
            long worldSeed, BackroomsSurvival.Net.Wg3PropHarvest.MaterialClass cls)
        {
            var definition = HarvestDefinitionFor(cls);
            if (definition == null)
                return; // faltan los assets de "Backrooms/Create Office Harvest Assets": decorativo, como hoy.

            var hitbox = new GameObject("HarvestHitbox");
            hitbox.hideFlags = HideFlags.DontSave;
            hitbox.transform.SetParent(go.transform, false);
            hitbox.layer = PolymindGames.LayerConstants.StaticObject;

            Bounds b = LocalRendererBounds(go);
            var box = hitbox.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size;

            var hr = hitbox.AddComponent<PolymindGames.ResourceHarvesting.HarvestableResource>();
            if (!SetHarvestableFields(hr, definition, go))
                return;

            uint id = BackroomsSurvival.Net.Wg3PropHarvest.NetIdFor(worldSeed, prop.kind, prop.xCm, prop.yCm, prop.zCm);
            var nh = hitbox.AddComponent<BackroomsSurvival.Net.NetworkHarvestableInstance>();
            nh.id = id;
            // D3 — el registro en el roster se manda en CADA golpe, no al instanciar: con miles de
            // piezas de atrezo por radio de streaming, registrar todo al instanciar repetiría el
            // coste que la 51.ª tanda quitó de las poses.
            nh.registerOnHarvest = true;

            if (cls == BackroomsSurvival.Net.Wg3PropHarvest.MaterialClass.MetalContainer)
            {
                // D5 — Cabinet/Shelf/Fridge/Rack sueltan el carryable Metal que YA existe, no un
                // item nuevo de bolsa.
                var metal = PolymindGames.WieldableSystem.CarryableDefinition.GetWithName("Metal");
                if (metal != null)
                {
                    nh.logDefId = metal.Id;
                    nh.logCount = 2;
                }

                // D6 — ADEMÁS es cofre. El id es OTRA sal (nunca el mismo que `id`): dos
                // sistemas, dos ids, una posición.
                nh.chestId = BackroomsSurvival.Net.Wg3PropHarvest.ChestIdFor(
                    worldSeed, prop.kind, prop.xCm, prop.yCm, prop.zCm);
                var contents = BackroomsSurvival.Net.Wg3PropHarvest.ChestContentsFor(
                    worldSeed, prop.kind, prop.xCm, prop.yCm, prop.zCm, ChestProfileForStyle(prop.style));
                if (contents.Count > 0)
                {
                    nh.chestLoot = new List<BackroomsSurvival.Net.CorpseLootStack>(contents.Count);
                    foreach (string name in contents)
                    {
                        var def = PolymindGames.InventorySystem.ItemDefinition.GetWithName(name);
                        if (def == null) continue; // catálogo a medio autorar: se salta, no se rompe.
                        nh.chestLoot.Add(new BackroomsSurvival.Net.CorpseLootStack { itemId = def.Id, quantity = 1 });
                    }
                }
            }
            else
            {
                var drops = BackroomsSurvival.Net.Wg3PropHarvest.ItemDropsFor(cls);
                if (drops.Count > 0)
                {
                    nh.itemDrops = new List<BackroomsSurvival.Net.NetworkHarvestableInstance.ItemDrop>(drops.Count);
                    foreach (var d in drops)
                    {
                        var def = PolymindGames.InventorySystem.ItemDefinition.GetWithName(d.Name);
                        if (def == null) continue; // catálogo a medio autorar: se salta, no se rompe.
                        nh.itemDrops.Add(new BackroomsSurvival.Net.NetworkHarvestableInstance.ItemDrop
                        { defId = def.Id, count = d.Count });
                    }
                }
            }

            // D3 — indexado directo: este atrezo YA trae su id determinista (no pasa por
            // `Bind`/emparejamiento por proximidad, que es para los árboles/rocas del vendor y
            // para los tres muebles de ADR-114). Sin esto, la salud autoritativa y el flanco de
            // depleción nunca lo encontrarían aunque aparezca en el roster.
            BackroomsSurvival.Net.StpHarvestableSyncManager.Instance?.TrackDeferredInstance(nh);
        }

        private static BackroomsSurvival.Gameplay.GridWorld.ZoneLootTable _chestLootTable;
        private static bool _chestLootTableLoaded;

        /// <summary>ADR-145 D6 — la tabla por PAPEL, la misma que ya sirve al loot suelto y a
        /// `StpWorldContainerSpawner` (ADR-108 D4): un armario de servicio pesa material y
        /// medicina porque el perfil de su sala ya lo dice, sin tabla nueva. `prop.style` es el
        /// papel de la sala para todo prop que no sea un Sign (ver el comentario de esa
        /// excepción en `AssembleSign`).</summary>
        private static BackroomsSurvival.Net.ZoneLootProfile ChestProfileForStyle(byte style)
        {
            if (!_chestLootTableLoaded)
            {
                _chestLootTable = Resources.Load<BackroomsSurvival.Gameplay.GridWorld.ZoneLootTable>("Loot/ZoneLootTable");
                _chestLootTableLoaded = true;
            }
            if (_chestLootTable != null)
                return _chestLootTable.ProfileForStyle(style);

            var fallback = BackroomsSurvival.Net.ChunkLootRoll.DefaultStyleLootProfiles();
            return fallback[Mathf.Clamp(style, 0, fallback.Length - 1)];
        }

        private static PolymindGames.ResourceHarvesting.HarvestableResourceDefinition HarvestDefinitionFor(
            BackroomsSurvival.Net.Wg3PropHarvest.MaterialClass cls)
        {
            if (_harvestDefCache.TryGetValue(cls, out var cached))
                return cached;
            var def = Resources.Load<PolymindGames.ResourceHarvesting.HarvestableResourceDefinition>(
                $"Wg3Props/Definitions/BR_Office{cls}");
            _harvestDefCache[cls] = def;
            if (def == null && _warnedHarvestDef.Add(cls))
                Debug.LogWarning($"[wg3] sin definición de harvest para la clase {cls}: ejecuta " +
                                  "Backrooms/Create Office Harvest Assets");
            return def;
        }

        /// <summary>Campos privados de `HarvestableResource` sin API pública para ponerlos en
        /// runtime — mismo idioma que `StpHarvestableSyncManager` usa para el resto del vendor.
        /// `_events` necesita un array NO NULO del tipo privado exacto (`RaiseEvent` hace
        /// `foreach` sin comprobar nulo) — de ahí `Array.CreateInstance` en vez de nombrar el tipo.</summary>
        private static bool SetHarvestableFields(PolymindGames.ResourceHarvesting.HarvestableResource hr,
            PolymindGames.ResourceHarvesting.HarvestableResourceDefinition definition, GameObject visual)
        {
            if (_hrResourceDefinitionField == null || _hrUnharvestedField == null ||
                _hrPartiallyField == null || _hrEventsField == null)
            {
                if (!_hrReflectionWarned)
                {
                    _hrReflectionWarned = true;
                    Debug.LogError("[wg3] HarvestableResource cambió sus campos privados: el atrezo " +
                                    "de oficina no se puede dar de alta (ADR-145 D5).");
                }
                return false;
            }

            _hrResourceDefinitionField.SetValue(hr, definition);
            _hrUnharvestedField.SetValue(hr, visual);
            _hrPartiallyField.SetValue(hr, visual);
            _hrEventsField.SetValue(hr, System.Array.CreateInstance(_hrEventsField.FieldType.GetElementType(), 0));
            return true;
        }

        /// <summary>Caja LOCAL (relativa a <paramref name="go"/>) que encierra sus renderers, sin
        /// asumir que el pack los deja en el origen: transforma las 8 esquinas de cada
        /// <c>Renderer.bounds</c> (mundo) al espacio local de <paramref name="go"/> y las encierra —
        /// conservador si hay rotación fina, pero nunca más pequeño que el visual real.</summary>
        private static Bounds LocalRendererBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(Vector3.up * 0.5f, Vector3.one);

            Matrix4x4 worldToLocal = go.transform.worldToLocalMatrix;
            Bounds local = default;
            bool has = false;
            foreach (var r in renderers)
            {
                Vector3 c = r.bounds.center, e = r.bounds.extents;
                for (int xi = -1; xi <= 1; xi += 2)
                for (int yi = -1; yi <= 1; yi += 2)
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    Vector3 corner = c + Vector3.Scale(e, new Vector3(xi, yi, zi));
                    Vector3 lp = worldToLocal.MultiplyPoint3x4(corner);
                    if (!has) { local = new Bounds(lp, Vector3.zero); has = true; }
                    else local.Encapsulate(lp);
                }
            }
            return has ? local : new Bounds(Vector3.up * 0.5f, Vector3.one);
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
            Wg3StoreyLayers.Apply(r, Wg3StoreyLayers.ForLight(prop.yCm * 0.01f));
            return go;
        }

        /// <summary>
        /// ADR-105 enm. 20 — **lo que cuelga del falso techo roto**: una placa descolgada de un
        /// lado (kind 21) y una luminaria caída en diagonal (kind 22).
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
            Wg3StoreyLayers.Apply(r, Wg3StoreyLayers.ForLight(prop.yCm * 0.01f));
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

        /// El prop VISIBLE de una fuente de ambiente de oficina — la rejilla de aire, la impresora.
        ///
        /// No llega por el cable: no tiene `kind`, lo decide el cliente en el mismo sitio donde
        /// pone la fuente. Existe porque una fuente puntual invisible es indiagnosticable: con la
        /// rejilla puesta, que el aire suene desplazado o dentro de una viga se VE en una captura.
        ///
        /// Misma disciplina que <see cref="AssembleProp"/>: sin colliders (es atrezo, no frena),
        /// capa del chunk y máscara de la cota, y <c>DontSave</c> para que no acabe en la escena.
        /// </summary>
        public static GameObject AssembleAmbienceProp(
            BackroomsSurvival.Gameplay.Audio.OfficeAmbienceDirector.Emitter e,
            Transform parent, int layer, string name)
        {
            if (parent == null) return null;
            string resource = BackroomsSurvival.Gameplay.Audio.OfficeAmbienceDirector.VisualPrefabOf(e.kind);
            if (resource == null) return null; // el teléfono y la silla ya se ven: son atrezo servido
            GameObject prefab = Resources.Load<GameObject>("Wg3Props/" + resource);
            if (prefab == null)
            {
                WarnMissingAmbienceProp(resource);
                return null;
            }

            var go = Object.Instantiate(prefab, parent);
            go.name = name;
            go.hideFlags = HideFlags.DontSave;
            go.transform.position = e.position;
            go.transform.rotation = Quaternion.Euler(0f, e.yawDeg, 0f);
            float s = BackroomsSurvival.Gameplay.Audio.OfficeAmbienceDirector.VisualScaleOf(e.kind);
            if (s != 1f) go.transform.localScale = new Vector3(s, s, s);
            uint mask = Wg3StoreyLayers.ForLight(e.position.y);
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) Wg3StoreyLayers.Apply(r, mask);
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            return go;
        }

        // Un aviso por prefab y sesión: sin esto, una rejilla que falte deja una línea por sala de
        // oficina de cada chunk, que son miles.
        private static readonly HashSet<string> _warnedAmbienceProps = new HashSet<string>();

        private static void WarnMissingAmbienceProp(string resource)
        {
            if (!_warnedAmbienceProps.Add(resource)) return;
            Debug.LogWarning($"[wg3] sin prefab de ambiente 'Wg3Props/{resource}': " +
                             "ejecuta Backrooms/WG3/Build Prop Catalog. La fuente se oirá sin verse.");
        }

        /// <summary>Placa del techo de la oficina: 60 cm. Los paneles se alinean a ella.</summary>
        public const float CeilingTileM = Wg3CeilingGrid.TileM;
        /// <summary>Panel fluorescente: dos placas de largo, una de ancho, como en la foto.</summary>
        public const float PanelLongM = 1.2f;
        public const float PanelShortM = 0.6f;
        /// <summary>
        /// Los paneles fluorescentes de un tramo, en rejilla alineada a las placas del techo. Se
        /// omite el que caería a menos de una placa de la pared. El eje largo del panel sigue el
        /// eje largo del tramo. Sin collider: es decoración del techo. Y para un tramo de doble
        /// altura cuelga con su planta de arriba (la del techo), que es la que lo alumbra.
        /// </summary>
        // R3 (12-09) — radio, en el techo, dentro del cual un panel se lee como iluminado por una
        // Light real. Es la mitad del espaciado de luces de AddSegmentLights (Spacing = 9 m): a esa
        // distancia dos plafones vecinos ya se solapan, así que es donde deja de haber una lámpara
        // «cerca» de verdad. Más allá, el panel sigue brillando (sigue siendo tubo sano) pero atenuado.
        private const float PanelLitRadiusM = 4.5f;

        // Al 60 %: brilla menos que uno con lámpara real al lado, pero sigue leyéndose como fluorescente
        // encendido, no como apagado (eso ya lo decide `Wg3LightCadence.PanelMissing` más arriba).
        private const float PanelDimFactor = 0.6f;

        private static void AddPanels(Transform parent, Wg3Segment segment, Material lampMaterial,
            float decay, List<Vector2> litPositions)
        {
            // La retícula vive en Wg3CeilingGrid y no aquí: el detalle sonoro cuelga una rejilla de
            // aire del techo y necesita los mismos números para no meterla dentro de una luminaria.
            Wg3CeilingGrid.Solve(segment.SizeX, segment.SizeZ,
                out float pitch, out int cx, out int cz, out float ox, out float oz);
            bool alongX = segment.SizeX >= segment.SizeZ;
            var size = alongX
                ? new Vector3(PanelLongM, 0.05f, PanelShortM)
                : new Vector3(PanelShortM, 0.05f, PanelLongM);
            float y = segment.Height - size.y * 0.5f + 0.01f;
            uint mask = Wg3StoreyLayers.ForLight(segment.FloorY + segment.Height - 0.1f);
            Material dimmed = DimmedPanelVariant(lampMaterial);
            float litRadiusSq = PanelLitRadiusM * PanelLitRadiusM;
            for (int ix = 0; ix < cx; ix++)
            {
                for (int iz = 0; iz < cz; iz++)
                {
                    float px = ox + ix * pitch, pz = oz + iz * pitch;
                    if (px < CeilingTileM + size.x * 0.5f || px > segment.SizeX - CeilingTileM - size.x * 0.5f
                        || pz < CeilingTileM + size.z * 0.5f || pz > segment.SizeZ - CeilingTileM - size.z * 0.5f)
                        continue;
                    // ADR-130 D4 (r2b) — LA LUMINARIA QUE FALTA. En coordenadas de MUNDO y en
                    // centímetros enteros: la posición local se repite tramo a tramo, así que
                    // sembrar con ella arrancaría el mismo hueco de la retícula en todas las salas.
                    if (Wg3LightCadence.PanelMissing(
                            Mathf.RoundToInt((segment.MinX + px) * 100f),
                            Mathf.RoundToInt((segment.FloorY + y) * 100f),
                            Mathf.RoundToInt((segment.MinZ + pz) * 100f), decay))
                        continue;
                    var go = new GameObject($"panel_{ix}_{iz}");
                    go.hideFlags = HideFlags.DontSave;
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = new Vector3(px, y, pz);
                    go.transform.localScale = size;
                    go.AddComponent<MeshFilter>().sharedMesh = LuminaireMesh();
                    var r = go.AddComponent<MeshRenderer>();
                    // R3 — lo que brilla al 100 % es lo que ilumina de verdad. Un panel a más de
                    // PanelLitRadiusM de TODAS las luces reales del tramo (litPositions vacía en un
                    // despacho a oscuras, o simplemente lejos en una nave grande) se atenúa: sigue
                    // siendo tubo sano, pero deja de prometer una luz que no está.
                    r.sharedMaterial = NearAnyLitFixture(px, pz, litPositions, litRadiusSq)
                        ? lampMaterial
                        : (dimmed != null ? dimmed : lampMaterial);
                    Wg3StoreyLayers.Apply(r, mask);
                }
            }
        }

        private static bool NearAnyLitFixture(float px, float pz, List<Vector2> litPositions, float radiusSq)
        {
            if (litPositions == null) return true; // sin lista, no se sabe: no se penaliza al panel.
            for (int i = 0; i < litPositions.Count; i++)
            {
                float dx = px - litPositions[i].x, dz = pz - litPositions[i].y;
                if (dx * dx + dz * dz <= radiusSq) return true;
            }
            return false;
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

            // Una sola caja, y por eso este canal existe: un tramo habría traído además su losa de
            // suelo y la de techo, coplanares con las del atrio.
            if (!SolidVolumeOf(solid, out Wg3Volume volume, out Vector3 origin, out float sy))
                return null;
            var volumes = new List<Wg3Volume>(1) { volume };

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
            Material[] mats = MaterialsForSolid(materials, solid.BaseStyle,
                Wg3Looks.ForSolid(solid.sizeXCm, solid.sizeZCm, solid.bottomYCm, solid.topYCm),
                TileMaterialOf(solid, sy));
            if (mats != null) renderer.sharedMaterials = mats;
            // Un megapilar cruza el atrio de suelo a techo, así que lleva las dos plantas y se
            // ilumina desde las dos. Un pretil vive en una sola.
            Wg3StoreyLayers.Apply(renderer, Wg3StoreyLayers.ForSurface(origin.y, sy));

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

        /// <summary>
        /// El volumen de un macizo, que es UNO. Extraído de <see cref="AssembleSolid"/> para que el
        /// camino fundido (<see cref="AssembleSolids"/>) lea exactamente el mismo, y no una copia
        /// que pueda derivar: dos recorridos que producen el volumen por su cuenta son el fallo que
        /// la fuente única de <c>Wg3Geometry</c> existe para impedir.
        /// </summary>
        /// <returns>Falso si el macizo es degenerado y no hay nada que montar.</returns>
        private static bool SolidVolumeOf(BackroomsSurvival.Net.Wg3SolidMsg solid,
            out Wg3Volume volume, out Vector3 origin, out float sy)
        {
            volume = default;
            float sx = solid.sizeXCm / 100f;
            float sz = solid.sizeZCm / 100f;
            sy = (solid.topYCm - solid.bottomYCm) / 100f;
            origin = new Vector3(solid.xCm / 100f, solid.bottomYCm / 100f, solid.zCm / 100f);
            if (sx <= 0f || sz <= 0f || sy <= 0f) return false;

            // **El centro es de MUNDO, como en toda lista de volúmenes** (`BuildPlaced` suma el
            // origen de la colocación; `Wg3GeneratedSegment.Build` el del tramo). `Wg3MeshBuilder`
            // y `AddColliders` restan `origin` a cada centro para emitir coordenadas locales, así
            // que un centro local aquí se restaba dos veces: desde wire 50 (2026-08-27) TODOS los
            // macizos —pilares, pretiles, tabiques, vigas— se dibujaban y colisionaban apilados en
            // el origen del mundo, y en su sitio quedaba sólo el ráster del servidor: paredes
            // invisibles. Lo destapó una captura en (3, 0, 14) con un bloque de 4 m plantado en
            // (0, 0) y ninguna cruz de 3 m donde el plan la ponía (2026-09-04).
            volume = new Wg3Volume
            {
                center = origin + new Vector3(sx * 0.5f, sy * 0.5f, sz * 0.5f),
                size = new Vector3(sx, sy, sz),
                // ADR-121 D1 — el giro es alrededor del centro de la huella, que es el que acabamos
                // de calcular; la caja del cable es la caja SIN girar.
                yawDegrees = solid.yawDeg,
                // ADR-125 — la forma dentro de la huella.
                shape = solid.shape,
                // ADR-125 enm. 2 — un marco es decoración: submalla de decoración y sin collider
                // (`IsSolid` falso), igual que un rodapié. `Casing` y no `Decoration` para que el
                // constructor le talle el perfil y el zócalo.
                //
                // ADR-105 enm. 20 — salvo una LOSETA: una placa de techo caída (4 cm de canto) o
                // una baldosa levantada (9) son cajas lisas, y el perfil de dos escalones y el
                // zócalo del marco sobre una pieza de dos centímetros no son un marco, son ruido.
                // El corte va por el canto porque un marco es una banda de dos metros.
                kind = !solid.IsDecoration
                    ? Wg3VolumeKind.Pillar
                    : (sy <= FlatDecorationMaxM ? Wg3VolumeKind.Decoration : Wg3VolumeKind.Casing),
            };
            return true;
        }

        /// <summary>Qué material se le pone a la submalla de decoración de una LOSETA (ADR-105 enm.
        /// 20). Va en la clave del fundido porque distingue dos juegos de materiales que por estilo
        /// y aspecto serían el mismo.</summary>
        private enum TileMaterial : byte { None = 0, FromCeiling = 1, FromFloor = 2 }

        private static TileMaterial TileMaterialOf(BackroomsSurvival.Net.Wg3SolidMsg solid, float sy)
        {
            if (!solid.IsDecoration || sy > FlatDecorationMaxM) return TileMaterial.None;
            // Una placa caída se dibuja con el material del TECHO: es la que falta arriba, y la
            // submalla de decoración traería el material del rodapié. La baldosa levantada (9 cm),
            // con el del SUELO, que es de donde sale.
            return sy <= 0.05f ? TileMaterial.FromCeiling : TileMaterial.FromFloor;
        }

        /// <summary>Los materiales de un macizo. ADR-105 enm. 19 — una mampara de cubículo se
        /// reconoce por su FORMA (12 × 140) y va en tela gris; todo lo demás —pilares, pretiles,
        /// vigas— es de obra, como en cualquier otro espacio.</summary>
        private static Material[] MaterialsForSolid(Wg3Materials materials, byte style,
            Wg3Look look, TileMaterial tile)
        {
            Material[] mats = Wg3StyleMaterials.Resolve(materials, style, look);
            if (mats == null || tile == TileMaterial.None
                || mats.Length <= Wg3MeshBuilder.SubMesh.Decoration) return mats;

            mats = (Material[])mats.Clone();
            mats[Wg3MeshBuilder.SubMesh.Decoration] = tile == TileMaterial.FromCeiling
                ? mats[Wg3MeshBuilder.SubMesh.Ceiling]
                : mats[Wg3MeshBuilder.SubMesh.Floor];
            return mats;
        }

        /// <summary>
        /// Lo que hace que dos macizos NO puedan compartir malla. Todo lo que varía de un renderer
        /// a otro y no cabe en una submalla entra aquí; lo que no está en esta clave, se funde.
        /// </summary>
        private readonly struct FuseKey : System.IEquatable<FuseKey>
        {
            /// <summary>La capa de render por PLANTA, y es la razón de que el fundido no pueda ser
            /// «un chunk, una malla». Ver <see cref="AssembleSolids"/>.</summary>
            public readonly uint Mask;
            public readonly byte Style;
            public readonly Wg3Look Look;
            public readonly TileMaterial Tile;

            public FuseKey(uint mask, byte style, Wg3Look look, TileMaterial tile)
            {
                Mask = mask; Style = style; Look = look; Tile = tile;
            }

            public bool Equals(FuseKey o) =>
                Mask == o.Mask && Style == o.Style && Look == o.Look && Tile == o.Tile;

            public override bool Equals(object o) => o is FuseKey k && Equals(k);

            public override int GetHashCode() =>
                unchecked((int)(Mask * 397) ^ (Style << 16) ^ ((int)Look << 8) ^ (int)Tile);
        }

        /// <summary>
        /// DÍA 3 DEL CONTRATO — los macizos de un chunk, en las MENOS mallas posibles.
        ///
        /// # Qué estaba mal
        ///
        /// Un `GameObject` con su `MeshFilter`, su `MeshRenderer` y su collider por macizo, y una
        /// región lleva del orden de tres mil. El coste no es de triángulos —eso es la otra deuda,
        /// la de las caras enterradas— sino de draw calls y de objetos: la nota F0 de
        /// <c>Wg3MeshBuilder</c> ya separaba las dos y dejaba ésta para más adelante.
        ///
        /// # Por qué NO es «un chunk, una malla», que es lo que uno escribiría
        ///
        /// **El chunk mide 50 m en XZ y no se parte en Y** (<see cref="Wg3ChunkStreamer.ChunkSize"/>),
        /// así que TODAS las plantas de una columna caen dentro del mismo. Con 3,32 m de planta y
        /// los sótanos de ADR-130, una región-torre mete siete u ocho plantas distintas en un solo
        /// chunk — y un `Renderer` tiene UNA `renderingLayerMask`. Colapsarlas devuelve exactamente
        /// la fuga de luz entre pisos que cerró ADR-104 enm. 2, o —si se elige una sola planta— deja
        /// las demás completamente negras, que es el síntoma que <see cref="Wg3StoreyLayers"/>
        /// documenta. Por eso la máscara está en <see cref="FuseKey"/> y no se toca.
        ///
        /// # Lo que se queda fuera del fundido, y no por pereza
        ///
        /// - **El macizo invisible** (ADR-129 D2): no tiene renderer, así que no cuesta un draw call
        ///   y fundirlo no compra nada. Sigue montando su collider y sólo eso.
        /// - **Los prismas y el arco** (ADR-125): frenan con SU malla, y el arco además `convex =
        ///   false`. Un `MeshCollider` no puede apuntar a media malla fundida, y convexo y cóncavo
        ///   no caben en el mismo collider.
        ///
        /// # Lo que sí sobrevive intacto
        ///
        /// El reparto por función en cuatro submallas (suelo, estructura, techo, decoración), que ya
        /// era el eje de continuidad de la regla R31; la colisión, que sigue saliendo de los
        /// VOLÚMENES filtrados por <c>IsSolid</c> y no de la malla —derivarla de la malla metería
        /// marcos y rodapiés en la colisión y rompería el contrato de fuente única—; y las UV, que
        /// desde el 2026-09-06 se anclan al mundo y por tanto no se mueven al cambiar el origen de
        /// la malla (<c>Wg3WorldUvTests.ElOrigenDeLaMalla_NoMueveLaTextura</c> lo fija).
        /// </summary>
        /// <returns>Cuántos renderers salieron. El llamante lo cuenta contra el número de macizos,
        /// que es la medida del día 3.</returns>
        public static int AssembleSolids(
            IReadOnlyList<BackroomsSurvival.Net.Wg3SolidMsg> solids, Transform parent,
            Wg3Materials materials, List<Mesh> createdMeshes, string namePrefix)
        {
            if (parent == null || solids == null) return 0;

            var groups = new Dictionary<FuseKey, List<Wg3Volume>>();
            int renderers = 0;

            for (int i = 0; i < solids.Count; i++)
            {
                var solid = solids[i];
                if (!SolidVolumeOf(solid, out Wg3Volume vol, out Vector3 origin, out float sy))
                    continue;

                // Los dos que no se pueden fundir van por el camino de siempre, uno a uno. El
                // invisible no suma renderer; el prisma sí, y por eso se cuenta.
                if (solid.IsHidden || solid.shape != Wg3Shape.Box)
                {
                    var one = AssembleSolid(solid, parent, materials, createdMeshes,
                        $"{namePrefix}_{i:D3}_s{solid.style}");
                    if (one != null && !solid.IsHidden) renderers++;
                    continue;
                }

                var key = new FuseKey(
                    Wg3StoreyLayers.ForSurface(origin.y, sy),
                    solid.BaseStyle,
                    Wg3Looks.ForSolid(solid.sizeXCm, solid.sizeZCm, solid.bottomYCm, solid.topYCm),
                    TileMaterialOf(solid, sy));

                if (!groups.TryGetValue(key, out List<Wg3Volume> bucket))
                {
                    bucket = new List<Wg3Volume>();
                    groups[key] = bucket;
                }
                bucket.Add(vol);
            }

            // ORDEN ESTABLE, y no es cosmético: el recorrido de un Dictionary no está definido, y
            // emitir la escena en un orden que cambie de una carga a otra haría que dos clientes
            // —o el mismo cliente al volver a un chunk— montaran las mallas en distinto orden. Las
            // capturas dejarían de ser comparables y un diagnóstico por jerarquía, imposible.
            var keys = new List<FuseKey>(groups.Keys);
            keys.Sort((a, b) =>
            {
                int c = a.Mask.CompareTo(b.Mask);
                if (c != 0) return c;
                c = a.Style.CompareTo(b.Style);
                if (c != 0) return c;
                c = ((int)a.Look).CompareTo((int)b.Look);
                return c != 0 ? c : ((int)a.Tile).CompareTo((int)b.Tile);
            });

            for (int k = 0; k < keys.Count; k++)
            {
                FuseKey key = keys[k];
                List<Wg3Volume> volumes = groups[key];
                if (volumes.Count == 0) continue;

                // El origen del lote es el centro del PRIMER volumen, y basta: todos caben en un
                // chunk de 50 m, así que las coordenadas relativas se quedan pequeñas y no vuelve la
                // pérdida de precisión que motivó emitirlas relativas (ver `Wg3MeshBuilder.Build`).
                Vector3 origin = volumes[0].center;

                var go = new GameObject(
                    $"{namePrefix}_L{key.Mask:X2}_s{key.Style}_k{(int)key.Look}{(int)key.Tile}_n{volumes.Count}");
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(parent, false);
                go.transform.position = origin;

                Mesh mesh = Wg3MeshBuilder.Build(volumes, origin);
                mesh.name = $"wg3_{go.name}";
                mesh.hideFlags = HideFlags.DontSave;
                createdMeshes?.Add(mesh);

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                Material[] mats = MaterialsForSolid(materials, key.Style, key.Look, key.Tile);
                if (mats != null) renderer.sharedMaterials = mats;
                Wg3StoreyLayers.Apply(renderer, key.Mask);
                renderers++;

                // La colisión sale de los VOLÚMENES, no de la malla: `AddColliders` filtra por
                // `IsSolid`, así que la decoración del lote se dibuja y no frena, exactamente igual
                // que cuando cada macizo tenía su objeto.
                AddColliders(go, volumes, origin);
            }

            return renderers;
        }

        /// <summary>Los volúmenes que un jugador PISA, y por eso los únicos que reciben
        /// <paramref name="floorMaterial"/> en <see cref="AddColliders"/> — R5. Una pared o un techo
        /// con el mismo <c>PhysicsMaterial</c> que el suelo no cambia nada audible (nadie choca de
        /// lado contra un collider lo bastante despacio para que suene) y sí complica leer qué
        /// superficie es cada una en el inspector.</summary>
        private static bool IsWalkable(Wg3VolumeKind kind) =>
            kind == Wg3VolumeKind.Floor || kind == Wg3VolumeKind.Step;

        private static void AddColliders(GameObject root, List<Wg3Volume> volumes, Vector3 origin,
            PhysicsMaterial floorMaterial = null)
        {
            for (int v = 0; v < volumes.Count; v++)
            {
                Wg3Volume vol = volumes[v];
                if (!vol.IsSolid) continue;
                // ADR-125 — los prismas llevan collider de malla, que pone `AssembleSolid`.
                if (vol.shape != Wg3Shape.Box) continue;

                bool walkable = floorMaterial != null && IsWalkable(vol.kind);

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
                    if (walkable) box.sharedMaterial = floorMaterial;
                }
                else
                {
                    var child = new GameObject($"col_{vol.kind}_{v}");
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = vol.center - origin;
                    child.transform.localRotation = Quaternion.Euler(0f, vol.yawDegrees, 0f);
                    var box = child.AddComponent<BoxCollider>();
                    box.size = vol.size;
                    if (walkable) box.sharedMaterial = floorMaterial;
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
            // ADR-130 D4 (r2b) — el mismo decaimiento que un tramo. Aquí importa aunque el catálogo
            // esté casi apagado (0,6 piezas por región): una pieza en un sótano con su plafón cálido
            // encendido al lado de un tramo gris se lee como un fallo de montaje, no como una pieza.
            float decay = Wg3StoreyLayers.DecayOfFloor(placement.originY);
            Wg3Fixture fixture = Wg3LightCadence.Resolve(worldSeed,
                placement.originX + nominalX, placement.originZ + nominalZ,
                0, placement.SizeX, placement.SizeZ, cadence, decay);

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
            light.color = Wg3LightCadence.Decayed(
                new Color(1f, 0.97f, 0.88f) * fixture.tint, decay);
            // El doble, por lo mismo y a la vez que el plafón de tramo: dos sistemas de luz con
            // intensidades que se separan al doble dejan las piezas del catálogo leyéndose como
            // agujeros oscuros dentro de una sala ya iluminada.
            // 3,6 desde el 07-09 (Joel: más Level 0), el mismo +15% que el plafón de tramo para
            // que los dos sistemas sigan separados al doble entre sí, y el mismo refuerzo por
            // planta que el plafón (UpperFloorBoost).
            light.intensity = 3.6f * Wg3StoreyLayers.UpperFloorBoost(placement.originY);
            // Acotado a 9 m: la fórmula abierta llegaba a 21,75 m en la pieza más grande, y una
            // puntual así cruza decenas de clusters de Forward+ ella sola. Una pieza grande queda
            // con penumbra en los bordes hasta que declare sus propias luces (R32), que es el plan.
            light.range = Mathf.Min(Mathf.Max(placement.SizeX, placement.SizeZ) * 0.75f + 6f, 9f);
            light.shadows = LightShadows.None;
            // Sólo su planta, igual que el plafón de un tramo. Éste es el que más alcance tiene
            // —hasta 21,75 m— así que es el que peor filtraba.
            Wg3StoreyLayers.Apply(light, Wg3StoreyLayers.ForLight(root.transform.position.y));

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
