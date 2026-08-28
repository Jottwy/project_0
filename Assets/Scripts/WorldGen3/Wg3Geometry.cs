using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Una columna. Cuadrada y con giro; una redonda se aproxima con giro y se acepta
    /// que colisione como prisma — la colisión de una columna redonda exacta cuesta más de lo que
    /// vale (R25: primaria y secundaria cuestan colisión, la decoración no).</summary>
    [Serializable]
    public struct Wg3Pillar
    {
        public Vector2 position;
        public float size;
        public float yawDegrees;

        public Wg3Pillar(float x, float z, float size, float yawDegrees = 0f)
        {
            position = new Vector2(x, z);
            this.size = size;
            this.yawDegrees = yawDegrees;
        }
    }

    /// <summary>Volumen macizo interior. Un bloque ancho y profundo es un núcleo (L4); uno
    /// estrecho y largo es una pared parcial (L13).</summary>
    [Serializable]
    public struct Wg3Block
    {
        public Vector2 position;
        public float sizeX;
        public float sizeZ;
        public float baseY;
        public float height;
        public float yawDegrees;

        public Wg3Block(float x, float z, float sizeX, float sizeZ,
            float height, float baseY = 0f, float yawDegrees = 0f)
        {
            position = new Vector2(x, z);
            this.sizeX = sizeX;
            this.sizeZ = sizeZ;
            this.baseY = baseY;
            this.height = height;
            this.yawDegrees = yawDegrees;
        }
    }

    /// <summary>Un tramo de escalera, escalón a escalón. `yawDegrees` da la dirección de subida
    /// (0 = hacia +Z).</summary>
    [Serializable]
    public struct Wg3StairRun
    {
        public Vector2 position;
        public float yawDegrees;
        public float width;
        public int steps;
        public float rise;
        public float run;

        /// <summary>
        /// ADR-097 enmienda 1 — `run` NO BAJA DE LA CELDA DEL RÁSTER (0,50 m), y por eso el
        /// defecto es 0,60 y no 0,29.
        ///
        /// El rasterizado del servidor es conservador: cada celda se queda con el peldaño MÁS ALTO
        /// de los que toca. Con huella de 0,29 m una celda de 0,50 abarca hasta tres peldaños y los
        /// funde en un escalón de 36 cm — por encima de los 0,275 del `m_StepOffset` del jugador,
        /// así que las dos piezas verticales del catálogo eran INFRANQUEABLES en la colisión aunque
        /// el cliente las dibujara subibles. Medido pieza a pieza: `room_stair` pedía 0,38 m y
        /// `cor_ramp` 0,36.
        ///
        /// Con 0,60 cada celda toca dos peldaños consecutivos como mucho, así que el salto entre
        /// celdas vecinas es exactamente un `rise`. La regla general, que vale para todo WG3: la
        /// geometría más fina que la celda del ráster CAMBIA DE SIGNIFICADO al pasar al servidor,
        /// no de precisión.
        /// </summary>
        public Wg3StairRun(float x, float z, float yawDegrees, float width,
            int steps, float rise = 0.18f, float run = 0.60f)
        {
            position = new Vector2(x, z);
            this.yawDegrees = yawDegrees;
            this.width = width;
            this.steps = steps;
            this.rise = rise;
            this.run = run;
        }

        public float TopHeight() => rise * steps;
        public float Length() => run * steps;
    }

    /// <summary>Para qué sirve un volumen. Decide quién lo mira: la colisión ignora
    /// <see cref="Decoration"/>, la malla lo dibuja todo.</summary>
    public enum Wg3VolumeKind
    {
        Floor = 0,
        Ceiling = 1,
        Wall = 2,
        Pillar = 3,
        Block = 4,
        Step = 5,

        /// <summary>Solo se ve. Rodapiés, molduras, marcos. REGLA R25 y L14 al revés: si costase
        /// colisión, un rodapié de 12 cm frenaría al jugador contra la pared.</summary>
        Decoration = 6
    }

    /// <summary>Una caja con giro en Y. Es la unidad de la "chuleta": lo único que en F2 tendrá
    /// que entender Rust de la geometría de una pieza.</summary>
    [Serializable]
    public struct Wg3Volume
    {
        public Vector3 center;
        public Vector3 size;
        public float yawDegrees;
        public Wg3VolumeKind kind;

        public bool IsSolid => kind != Wg3VolumeKind.Decoration;
    }

    /// <summary>
    /// LA FUENTE ÚNICA (regla R2, y aquí es literal).
    ///
    /// `RoomColliderBuilder` y `RoomMeshBuilder` de WG2 leen los dos el mismo modelo, pero por
    /// separado: son dos recorridos distintos que PUEDEN divergir, y su propia cabecera admite que
    /// si divergen "se vería una puerta donde hay muro o al revés".
    ///
    /// Aquí no hay dos recorridos. Esta clase produce UNA lista de volúmenes, y de ahí salen tanto
    /// la colisión (los sólidos, tal cual) como la malla (todos, triangulados). Divergir es
    /// imposible porque no hay dos fuentes que puedan separarse.
    ///
    /// Lo que sí se separa —y es una decisión, no un descuido— es la DECORACIÓN: el rodapié se
    /// dibuja y no colisiona. Esa es la línea de R25 escrita como un enum en vez de como una
    /// intención, y es lo que hace que el criterio de cierre de F0 signifique algo: si chocas con
    /// una columna pero NO con el rodapié, las dos mitades del sistema están de acuerdo y además
    /// saben en qué no tienen que estarlo.
    /// </summary>
    public static class Wg3Geometry
    {
        /// <summary>Grosor de losa de suelo y techo.</summary>
        public const float SlabThickness = 0.12f;

        /// <summary>Altura del rodapié. REGLA R31: es lo que unifica las referencias visuales —el
        /// zócalo corriendo sin interrupción por pasillos y salas— y lo primero que delata una
        /// junta modular si no casa entre dos piezas.</summary>
        public const float SkirtingHeight = 0.14f;

        /// <summary>Cuánto sobresale el rodapié de la pared.</summary>
        public const float SkirtingProud = 0.022f;

        public static List<Wg3Volume> Build(Wg3Piece piece)
        {
            var volumes = new List<Wg3Volume>(32);
            if (piece == null) return volumes;

            // PIEZA AUTORADA: sus volúmenes ya salieron de un modelo dibujado a mano y se hornearon.
            // Interceptar aquí —y no en el exportador— es lo que mantiene a esta clase como fuente
            // única: malla, colisión y manifiesto siguen leyendo del mismo sitio, venga la pieza de
            // un dibujo o de los campos de abajo. Si el corte estuviera en el exportador, la malla
            // del cliente se seguiría construyendo de `blocks`/`pillars` y volveríamos a tener dos
            // recorridos que pueden divergir, que es exactamente lo que R2 prohíbe.
            if (piece.bakedVolumes != null && piece.bakedVolumes.Length > 0)
            {
                volumes.AddRange(piece.bakedVolumes);
                return volumes;
            }

            float w = piece.sizeX, d = piece.sizeZ, h = piece.heightMeters;
            float t = Mathf.Max(0.02f, piece.wallThickness);

            // Suelo y techo, huella completa. El suelo cuelga por debajo de y=0 para que la cara
            // pisable quede EXACTAMENTE en 0 y dos piezas contiguas no dejen un escalón de losa.
            Add(volumes, Wg3VolumeKind.Floor,
                new Vector3(w * 0.5f, -SlabThickness * 0.5f, d * 0.5f),
                new Vector3(w, SlabThickness, d));
            Add(volumes, Wg3VolumeKind.Ceiling,
                new Vector3(w * 0.5f, h + SlabThickness * 0.5f, d * 0.5f),
                new Vector3(w, SlabThickness, d));

            for (int side = 0; side < 4; side++)
                BuildSide(volumes, piece, side, w, d, h, t);

            foreach (Wg3Pillar p in piece.pillars)
                Add(volumes, Wg3VolumeKind.Pillar,
                    new Vector3(p.position.x, h * 0.5f, p.position.y),
                    new Vector3(p.size, h, p.size), p.yawDegrees);

            foreach (Wg3Block b in piece.blocks)
                Add(volumes, Wg3VolumeKind.Block,
                    new Vector3(b.position.x, b.baseY + b.height * 0.5f, b.position.y),
                    new Vector3(b.sizeX, b.height, b.sizeZ), b.yawDegrees);

            foreach (Wg3StairRun s in piece.stairs)
                BuildStair(volumes, s);

            return volumes;
        }

        /// <summary>
        /// La pared de un lado, partida por sus bocas.
        ///
        /// Es el punto donde "el vano existe" deja de ser una afirmación: si este recorrido se
        /// equivoca, la colisión tapa una puerta que se ve abierta, y ese es el peor fallo posible
        /// porque el jugador ve una cosa y el juego hace otra.
        /// </summary>
        private static void BuildSide(List<Wg3Volume> volumes, Wg3Piece piece, int side,
            float w, float d, float h, float t)
        {
            bool horizontal = (side == 0 || side == 2);
            float length = horizontal ? w : d;

            // Cortes ordenados. Se ordenan aquí y no se presume el orden del autorado: dos bocas
            // declaradas al revés dejarían un tramo de longitud negativa.
            var cuts = new List<Vector2>();
            foreach (Wg3Socket s in piece.sockets)
            {
                if (s.side != side) continue;
                cuts.Add(new Vector2(s.offset - s.width * 0.5f, s.offset + s.width * 0.5f));
            }
            cuts.Sort((a, b) => a.x.CompareTo(b.x));

            float cursor = 0f;
            for (int i = 0; i < cuts.Count; i++)
            {
                if (cuts[i].x > cursor) EmitWall(volumes, side, cursor, cuts[i].x, w, d, h, t);
                cursor = Mathf.Max(cursor, cuts[i].y);
            }
            if (cursor < length) EmitWall(volumes, side, cursor, length, w, d, h, t);
        }

        /// <summary>Un tramo de pared entre dos offsets del lado, más su rodapié. Los offsets
        /// recorren el perímetro en el mismo sentido horario que los sockets (ver
        /// <see cref="Wg3Socket"/>), así que aquí se convierten a metros de mundo local.</summary>
        private static void EmitWall(List<Wg3Volume> volumes, int side, float from, float to,
            float w, float d, float h, float t)
        {
            float mid = (from + to) * 0.5f;
            float len = to - from;
            if (len <= 1e-3f) return;

            Vector3 centre;
            Vector3 size;
            Vector3 skirtCentre;
            Vector3 skirtSize;

            switch (side)
            {
                case 0: // N, z = d. El offset corre en +X.
                    centre = new Vector3(mid, h * 0.5f, d - t * 0.5f);
                    size = new Vector3(len, h, t);
                    skirtCentre = new Vector3(mid, SkirtingHeight * 0.5f, d - t - SkirtingProud * 0.5f);
                    skirtSize = new Vector3(len, SkirtingHeight, SkirtingProud);
                    break;
                case 1: // E, x = w. El offset corre en −Z desde z = d.
                    centre = new Vector3(w - t * 0.5f, h * 0.5f, d - mid);
                    size = new Vector3(t, h, len);
                    skirtCentre = new Vector3(w - t - SkirtingProud * 0.5f, SkirtingHeight * 0.5f, d - mid);
                    skirtSize = new Vector3(SkirtingProud, SkirtingHeight, len);
                    break;
                case 2: // S, z = 0. El offset corre en −X desde x = w.
                    centre = new Vector3(w - mid, h * 0.5f, t * 0.5f);
                    size = new Vector3(len, h, t);
                    skirtCentre = new Vector3(w - mid, SkirtingHeight * 0.5f, t + SkirtingProud * 0.5f);
                    skirtSize = new Vector3(len, SkirtingHeight, SkirtingProud);
                    break;
                default: // O, x = 0. El offset corre en +Z.
                    centre = new Vector3(t * 0.5f, h * 0.5f, mid);
                    size = new Vector3(t, h, len);
                    skirtCentre = new Vector3(t + SkirtingProud * 0.5f, SkirtingHeight * 0.5f, mid);
                    skirtSize = new Vector3(SkirtingProud, SkirtingHeight, len);
                    break;
            }

            Add(volumes, Wg3VolumeKind.Wall, centre, size);
            Add(volumes, Wg3VolumeKind.Decoration, skirtCentre, skirtSize);
        }

        /// <summary>Escalera como pila de cajas. Cada escalón es un volumen sólido desde el suelo:
        /// una rampa inclinada sería una caja menos, pero un jugador con `CharacterController` sube
        /// escalones y resbala por cajas giradas.</summary>
        private static void BuildStair(List<Wg3Volume> volumes, Wg3StairRun s)
        {
            if (s.steps < 1 || s.width <= 0f) return;

            float yaw = s.yawDegrees * Mathf.Deg2Rad;
            float fx = Mathf.Sin(yaw), fz = Mathf.Cos(yaw);

            for (int i = 0; i < s.steps; i++)
            {
                float top = s.rise * (i + 1);
                float along = s.run * (i + 0.5f);
                Add(volumes, Wg3VolumeKind.Step,
                    new Vector3(s.position.x + fx * along, top * 0.5f, s.position.y + fz * along),
                    new Vector3(s.width, top, s.run), s.yawDegrees);
            }
        }

        private static void Add(List<Wg3Volume> into, Wg3VolumeKind kind,
            Vector3 centre, Vector3 size, float yaw = 0f)
        {
            into.Add(new Wg3Volume { center = centre, size = size, yawDegrees = yaw, kind = kind });
        }

        /// <summary>Los volúmenes de una pieza YA COLOCADA, en coordenadas de mundo. Aplica el giro
        /// de la colocación y traslada al origen en esquina mínima.</summary>
        public static List<Wg3Volume> BuildPlaced(Wg3Placement placement)
        {
            List<Wg3Volume> local = Build(placement.piece);
            float w = placement.piece.sizeX, d = placement.piece.sizeZ;
            int r = placement.rotation & 3;

            for (int i = 0; i < local.Count; i++)
            {
                Wg3Volume v = local[i];
                Vector2 p = RotateLocal(new Vector2(v.center.x, v.center.z), r, w, d);

                // ADR-097 — la Y de la colocación se suma aquí y en ningún otro sitio. Como malla y
                // colisión salen las dos de esta lista, una pieza elevada sube entera: no hay forma
                // de que se dibuje arriba y bloquee abajo.
                v.center = new Vector3(placement.originX + p.x,
                                       placement.originY + v.center.y,
                                       placement.originZ + p.y);

                // El giro va SOLO al yaw. Intercambiar además X y Z aplicaría la rotación dos
                // veces: una caja de 4 × 1 girada 90° sigue midiendo 4 × 1 en su propio eje, y es
                // el yaw el que la pone atravesada en el mundo.
                v.yawDegrees += r * 90f;
                local[i] = v;
            }
            return local;
        }

        /// <summary>El mismo giro horario que <see cref="Wg3Placement.WorldPoint"/>. Duplicado a
        /// propósito en vez de compartido: son dos usos con vidas distintas —uno es el contrato de
        /// composición, el otro el de geometría— y hay un test que los ata.
        ///
        /// PÚBLICO desde la rebanada 2 del catálogo autorado: el ensamblador coloca la malla
        /// autorada con ESTA función y no con una copia suya. Una segunda implementación del mismo
        /// giro es una que puede desviarse, y el síntoma sería la malla en un sitio y su colisión en
        /// otro — que es el peor fallo posible del cliente y además no se ve en una captura.</summary>
        public static Vector2 RotateLocal(Vector2 p, int r, float w, float d)
        {
            switch (r & 3)
            {
                case 0: return p;
                case 1: return new Vector2(p.y, w - p.x);
                case 2: return new Vector2(w - p.x, d - p.y);
                default: return new Vector2(d - p.y, p.x);
            }
        }
    }
}
