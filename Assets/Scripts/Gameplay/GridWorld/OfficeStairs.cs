using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// ZONE_OFFICE — "escalera a ninguna parte". Geometría de CLIENTE, derivada por hash
    /// puro de (chunk, room_zones), igual que las knee walls, los dinteles y los props:
    /// el bitmask de <c>walls[tx,tz]</c> tiene sus dos nibbles ocupados (aristas + pilares)
    /// y <see cref="RoomZoneMsg"/> no tiene campo libre, así que meter "aquí hay escalera"
    /// en el wire sería cambio de protocolo (regla dura #7 → ADR). No hace falta: todos
    /// los peers ven los mismos <c>room_zones</c> y las mismas coordenadas de chunk, así
    /// que el mismo hash produce la misma escalera en todas las máquinas.
    ///
    /// ES DECORATIVA Y VIVE ENTERA DENTRO DE SU CAPA. No toca
    /// <c>require_walkable_above</c>/<c>_below</c>, no toca <c>collision.rs</c> y no
    /// desbloquea nada de ADR-026 — ver la NOTA DE BACKLOG (2026-08-08) de
    /// <c>docs/DECISIONS.md</c>, que declara la verticalidad jugable como pieza aparte.
    ///
    /// EL INVARIANTE DURO, y por qué la escalera tiene la forma que tiene:
    /// el backend deriva la capa del jugador con
    /// <c>layer_from_player_y(y) = round((y − PLAYER_BASE_Y) / LAYER_HEIGHT)</c>
    /// (<c>backend/src/world/collision.rs</c>). La Y que le llega es "pies + PLAYER_BASE_Y"
    /// (el emisor compensa en origen, ver la NOTA ACLARATORIA de la enmienda de ADR-026),
    /// así que la expresión se reduce a <c>round(pies / 4)</c> y el jugador cambia de capa
    /// en cuanto sus PIES pasan de <see cref="LayerFlipFeetY"/> = 2 m. Eso es interacción
    /// cross-layer — recalcularía <c>chunk_key_at</c>, la colisión y el ownership.
    ///
    /// Como el jugador salta 1.05 m (FPS_Player.prefab <c>_jumpHeight</c>), la altura máxima
    /// PISABLE no puede ser 2 m sino 2 − 1.05 − margen. De ahí que el tramo jugable sean 3
    /// escalones hasta un rellano a 0.66 m y no una escalera completa: con el umbral de hoy
    /// no cabe más. El tramo SUPERIOR existe igualmente pero está desconectado y arranca a
    /// <see cref="GhostFlightBottomY"/> = 2.6 m, fuera del alcance de un salto desde el
    /// rellano (0.66 + 1.05 = 1.71) — y va SIN COLLIDER, así que ni siquiera alcanzándolo
    /// por otra vía se podría estar de pie ahí. Doble barrera a propósito: la primera es
    /// geométrica, la segunda sobrevive a que alguien cambie la primera.
    /// </summary>
    public static class OfficeStairs
    {
        /// <summary>Espejo de `ZONE_OFFICE` (backend/src/world/chunk/surface_profiles.rs).</summary>
        public const int ZoneOffice = 12;

        // ── Tramo jugable ────────────────────────────────────────────────────────────

        /// <summary>Contrahuella. DEBE quedar por debajo del <c>m_StepOffset</c> del
        /// CharacterController de FPS_Player.prefab (0.275): por encima, el motor no sube el
        /// escalón y la escalera se vuelve una pared con relieve.</summary>
        public const float StepRise = 0.22f;

        /// <summary>Huella (profundidad de cada escalón).</summary>
        public const float StepRun = 0.55f;

        /// <summary>Escalones del tramo jugable. El límite lo pone
        /// <see cref="LayerFlipFeetY"/>, no la estética — ver el doc de la clase.</summary>
        public const int StepCount = 3;

        /// <summary>Altura del rellano = la superficie más alta que el jugador puede pisar
        /// en toda la pieza.</summary>
        public const float LandingY = StepRise * StepCount;

        /// <summary>Profundidad del rellano.</summary>
        public const float LandingDepth = 1.35f;

        // ── Tramo decorativo (sin collider, inalcanzable) ────────────────────────────

        /// <summary>Superficie más BAJA del tramo superior. Tiene que quedar por encima del
        /// ápice de un salto desde el rellano (<see cref="LandingY"/> + 1.05) o el tramo
        /// dejaría de ser decorativo.</summary>
        public const float GhostFlightBottomY = 2.6f;

        /// <summary>Contrahuella del tramo decorativo: más alta que
        /// <see cref="StepRise"/> y que el <c>m_StepOffset</c> del jugador, así que sería
        /// inescalable aunque alguien lo bajara hasta ponerlo a mano.</summary>
        public const float GhostRise = 0.32f;

        /// <summary>Huella del tramo decorativo. Más corta que la del jugable: se lee como
        /// una escalera más empinada, y así el tramo entero cabe en el tile.</summary>
        public const float GhostRun = 0.30f;

        // ── Invariante de capa ───────────────────────────────────────────────────────

        /// <summary>Altura de PIES a la que el backend reclasifica al jugador a la capa de
        /// arriba. Derivada, no copiada: <c>round(pies / LayerHeight) >= 1</c> ⟺
        /// <c>pies >= LayerHeight / 2</c>. Si `LayerHeight` cambia, esto la sigue.</summary>
        public const float LayerFlipFeetY = GridConstants.LayerHeight * 0.5f;

        // ── Geometría auxiliar ───────────────────────────────────────────────────────

        private const float Width = 1.6f;        // ancho del tramo, holgado para la cápsula (r=0.45)
        private const float StringerThickness = 0.10f;
        private const float TileSize = GridVisualConstants.TileSize;

        private const uint StairSaltZone = 0x53545A4EU; // "STZN" — qué sub-región aloja la escalera
        private const uint StairSaltYaw  = 0x53545941U; // "STYA" — orientación del tramo

        /// <summary>Plan determinista de la escalera de un chunk: qué tile reserva y con qué
        /// orientación. Es PURO (solo hash + aritmética de rects), para poder probar la
        /// colocación sin construir un chunk.</summary>
        public struct Plan
        {
            public bool valid;
            public int tx, tz;
            public float yaw;
        }

        /// <summary>
        /// Plan de la escalera de <paramref name="zoneKind"/>/<paramref name="zones"/>, o un
        /// <see cref="Plan"/> inválido cuando este chunk no lleva ninguna.
        ///
        /// UNA por chunk, no una por sub-región: cuatro escaleras en 50 m se leerían como
        /// decorado repetido en vez de como una rareza. La sub-región que la aloja sale de
        /// un hash propio, así que no compite con ningún otro sorteo del chunk.
        /// </summary>
        public static Plan PlanFor(int zoneKind, RoomZoneMsg[] zones, byte[,] walls,
            int chunkX, int chunkZ)
        {
            var plan = default(Plan);
            if (zoneKind != ZoneOffice || zones == null || zones.Length == 0) return plan;

            int zi = Mathf.Clamp(
                (int)(GridChunkBuilder.Hash01(chunkX, chunkZ, StairSaltZone) * zones.Length),
                0, zones.Length - 1);
            var z = zones[zi];

            // Centro del rect en TILES. Los rects de sub-región nacen con origen y tamaño
            // PARES (`align_origin_to_tile`/`tile_aligned_size`; en el camino de sub-región
            // el backend alinea TODOS los RoomType, no solo los de perímetro sellado), y con
            // tamaño 6 el rect ocupa 3 tiles por eje, así que este es el tile CENTRAL de los
            // tres — nunca uno de los dos de los extremos, que son los que en una SealedRoom
            // llevan el perímetro. No se filtra por `Kind` a propósito: la aritmética vale
            // igual para una sub-región `Open` (el 20% de OFFICE), que simplemente no tiene
            // perímetro del que apartarse. Quien de verdad protege en runtime es el chequeo
            // de `walls` de abajo.
            int tx = (z.x0 + z.x1) / 4;
            int tz = (z.z0 + z.z1) / 4;
            if (tx < 0 || tz < 0 ||
                tx >= GridChunkBuilder.TilesPerChunk || tz >= GridChunkBuilder.TilesPerChunk)
                return plan;

            // Mismos dos rechazos que aplica la colocación de props, y por el mismo motivo:
            // un tile macizo o con columna no tiene suelo libre donde apoyar la pieza. Van
            // AQUÍ y no en el sitio de construcción para que el tile que los props reservan
            // y el tile donde de verdad se construye no puedan discrepar nunca.
            if (walls != null)
            {
                byte b = walls[tx, tz];
                if ((b & 0x0F) == 0x0F) return plan;                       // tile cerrado
                if ((b & GridChunkBuilder.PillarMask) != 0) return plan;   // columna en el tile
            }

            plan.valid = true;
            plan.tx = tx;
            plan.tz = tz;
            // Clamp, no Floor a secas: Hash01 puede devolver 1.0 exacto (sus 24 bits bajos
            // todos a 1), y `Floor(1.0 * 4) * 90` daría 360 — inofensivo al rotar, pero
            // rompe cualquier comprobación de "es uno de los 4 cuadrantes".
            plan.yaw = Mathf.Clamp(
                (int)(GridChunkBuilder.Hash01(chunkX, chunkZ, StairSaltYaw) * 4f), 0, 3) * 90f;
            return plan;
        }

        /// <summary>
        /// Construye la escalera bajo <paramref name="parent"/>, con su origen local en
        /// <paramref name="localPos"/> (el suelo del tile) y girada <paramref name="yaw"/>
        /// grados. El tramo sube hacia +Z antes del giro.
        ///
        /// Devuelve la raíz para que las pruebas puedan inspeccionarla; el runtime la ignora.
        /// </summary>
        public static GameObject Build(Transform parent, Material mat, Color tint,
            Vector3 localPos, float yaw)
        {
            var root = new GameObject("OfficeStairs");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPos;
            root.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            // El tramo entero se centra en el tile: arranca medio recorrido por detrás del
            // centro para que el rellano no se salga por el lado opuesto.
            float runLength = StepCount * StepRun + LandingDepth;
            float z0 = -runLength * 0.5f;

            // Escalones jugables. Cada uno es un bloque MACIZO desde el suelo hasta su huella
            // (no una losa flotante): así no queda hueco por debajo donde encajar la cápsula,
            // y el collider de caja coincide con lo que se ve.
            for (int i = 0; i < StepCount; i++)
            {
                float top = StepRise * (i + 1);
                Slab(root.transform, mat, tint, "Step" + i,
                    new Vector3(0f, top * 0.5f, z0 + StepRun * (i + 0.5f)),
                    new Vector3(Width, top, StepRun), collider: true);
            }

            // Rellano.
            Slab(root.transform, mat, tint, "Landing",
                new Vector3(0f, LandingY * 0.5f, z0 + StepCount * StepRun + LandingDepth * 0.5f),
                new Vector3(Width, LandingY, LandingDepth), collider: true);

            // Tramo decorativo: arranca en GhostFlightBottomY, desconectado del rellano, y
            // sube hasta comerse el techo. SIN COLLIDER — ver el doc de la clase.
            float ghostZ = z0 + StepCount * StepRun + LandingDepth;
            int ghostSteps = 0;
            for (int i = 0; ; i++)
            {
                float top = GhostFlightBottomY + GhostRise * (i + 1);
                float far = ghostZ + GhostRun * (i + 1);
                // Los dos cortes van ANTES de estampar, no después: el techo por arriba y el
                // borde del tile por detrás. Comprobarlos al final del cuerpo colocaría
                // siempre un escalón de más, justo el que se sale.
                if (top > GridConstants.LayerHeight) break;
                if (far > TileSize * 0.5f) break;
                Slab(root.transform, mat, tint, "GhostStep" + i,
                    new Vector3(0f, top - GhostRise * 0.5f, ghostZ + GhostRun * (i + 0.5f)),
                    new Vector3(Width, GhostRise, GhostRun), collider: false);
                ghostSteps = i + 1;
            }

            // Zanca lateral del tramo decorativo: sin ella los escalones flotan sueltos y se
            // lee como un glitch en vez de como una escalera rota. Sin collider, igual que
            // los escalones que sostiene.
            // Se ancla al ÚLTIMO escalón colocado, no a un índice fijo: si el corte del techo
            // o el del tile recortan el tramo, la zanca lo sigue en vez de quedarse flotando
            // por delante o por detrás de él. El clamp final es lo que hace que ese
            // seguimiento no la empuje fuera del tile: sin él, el margen depende de que
            // GhostRun y el corte del bucle sigan cuadrando, y son dos constantes que se
            // pueden retocar por separado.
            if (ghostSteps > 0)
            {
                float stringerZ = Mathf.Min(ghostZ + GhostRun * ghostSteps,
                    TileSize * 0.5f - StringerThickness * 0.5f);
                Slab(root.transform, mat, tint, "GhostStringer",
                    new Vector3(0f, (GhostFlightBottomY + GridConstants.LayerHeight) * 0.5f,
                        stringerZ),
                    new Vector3(Width, GridConstants.LayerHeight - GhostFlightBottomY,
                        StringerThickness),
                    collider: false);
            }

            return root;
        }

        private static void Slab(Transform parent, Material mat, Color tint, string name,
            Vector3 localPos, Vector3 scale, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;

            if (!collider)
            {
                var col = go.GetComponent<Collider>();
                if (col != null)
                {
                    // Mismo criterio que PlaceholderFactory.Finish: Object.Destroy a secas
                    // lanza fuera de Play Mode y los tests EditMode recorren esta rama.
                    if (Application.isPlaying) Object.Destroy(col);
                    else Object.DestroyImmediate(col);
                }
            }

            if (mat != null)
            {
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor(LayerVisualMaterials.BaseColorId, tint);
                r.SetPropertyBlock(mpb);
            }
        }
    }
}
