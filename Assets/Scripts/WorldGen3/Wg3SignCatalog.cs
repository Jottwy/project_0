using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// ADR-129 enm. 1 — los CARTELES: lo único del mundo con texto.
    ///
    /// Un cartel no es un prefab: es un quad con una celda de un atlas de decals
    /// (<c>Assets/Art/Signage/Resources/Wg3SignAtlas.png</c>, 8 × 8 celdas de 256). El servidor
    /// manda el ancla ya despegada de la superficie y la VARIANTE en <c>style</c>, y aquí se
    /// resuelve a tamaño, a celda del atlas y a malla.
    ///
    /// **Cada celda del atlas es cuadrada y ningún cartel lo es.** El dibujo se horneó a la
    /// proporción real y se estiró a la celda (<c>tools/dev/BakeSignAtlas.py</c>); el quad, que
    /// tiene la proporción real, deshace ese estirado. Por eso no hace falta una tabla de UV por
    /// variante: la única cuenta compartida con el horno es fila = <c>v / 8</c>, columna
    /// <c>v % 8</c>.
    /// </summary>
    public static class Wg3SignCatalog
    {
        /// <summary>Espejo de `SIGN_GARBLED_BASE`: sumado a una variante da su gemela con el texto
        /// estropeado, que es lo que se sirve al bajar (ADR-130 D4).</summary>
        public const byte GarbledBase = 32;
        // Espejo exacto de la tabla de `segment.rs`: primera variante de cada familia y cuántas hay.
        public const byte DoorPlate = 0, DoorPlateN = 8;
        public const byte CubicleTag = 8, CubicleTagN = 8;
        public const byte Exit = 16, ExitN = 4;
        public const byte Corkboard = 20, CorkboardN = 4;
        public const byte Calendar = 24, CalendarN = 4;

        public const int Cols = 8;
        public const int Cell = 256;
        public const int AtlasPx = Cols * Cell;
        /// <summary>Margen transparente de cada celda, en píxeles del atlas. **Espejo de `GUTTER`
        /// en el horno**: sin él, el filtrado bilineal trae al borde de una placa los píxeles
        /// verdes de la señal de salida que tiene al lado en el atlas.</summary>
        public const int Gutter = 4;

        /// <summary>ADR-129 enm. 1 — el tamaño del quad de una variante, en metros. **Espejo de
        /// `segment::sign_size_cm`**: el servidor mide el hueco con esta misma tabla.</summary>
        public static Vector2 SizeM(byte variant)
        {
            byte v = (byte)(variant % GarbledBase);
            if (v < CubicleTag) return new Vector2(0.30f, 0.12f);
            if (v < Exit) return new Vector2(0.24f, 0.09f);
            if (v < Corkboard) return new Vector2(0.40f, 0.15f);
            if (v < Calendar) return new Vector2(1.20f, 0.90f);
            if (v < Calendar + CalendarN) return new Vector2(0.30f, 0.42f);
            return new Vector2(0.30f, 0.12f);
        }

        /// <summary>La celda del atlas de una variante, en UV: fila <c>v / 8</c> desde ARRIBA
        /// (v de Unity crece hacia arriba), columna <c>v % 8</c>, menos el margen.</summary>
        public static Rect UvOf(byte variant)
        {
            int v = variant % (Cols * Cols);
            float step = 1f / Cols;
            float pad = Gutter / (float)AtlasPx;
            float u0 = (v % Cols) * step + pad;
            float v1 = 1f - (v / Cols) * step - pad;
            return new Rect(u0, v1 - step + 2f * pad, step - 2f * pad, step - 2f * pad);
        }

        private static readonly Dictionary<byte, Mesh> Meshes = new Dictionary<byte, Mesh>();
        private static Material _material;
        private static bool _warned;

        /// <summary>
        /// El material compartido de TODOS los carteles: URP Lit con recorte por alfa y el atlas de
        /// mapa base. Se crea en runtime y no se serializa —como el resto de WG3— y **no lleva
        /// ninguna luz**: un cartel es papel, y en un sótano sin corriente no se lee. Recorte y no
        /// transparencia porque un plano transparente a 1 cm de la pared se ordena mal contra ella
        /// desde según qué ángulo.
        /// </summary>
        public static Material Material()
        {
            if (_material != null) return _material;
            var tex = Resources.Load<Texture2D>("Wg3SignAtlas");
            if (tex == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[wg3] sin atlas de carteles (Resources/Wg3SignAtlas): " +
                        "ejecuta python tools/dev/BakeSignAtlas.py");
                }
                return null;
            }
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            _material = new Material(shader) { name = "Wg3Sign", hideFlags = HideFlags.DontSave };
            _material.SetTexture("_BaseMap", tex);
            _material.SetFloat("_AlphaClip", 1f);
            _material.SetFloat("_Cutoff", 0.5f);
            _material.SetFloat("_Smoothness", 0.15f);
            _material.SetFloat("_Metallic", 0f);
            _material.EnableKeyword("_ALPHATEST_ON");
            _material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            return _material;
        }

        /// <summary>
        /// El quad de una variante, cacheado: mira a +z en local (la convención de `yaw_deg`, con
        /// 0 = mira a +z), centrado en su ancla y con la celda del atlas ya en las UV. Una malla
        /// por variante y no un `MaterialPropertyBlock` por cartel: un MPB por cada uno de los
        /// cientos que hay en un radio 1 rompería el SRP Batcher, que es lo mismo que ya se dice de
        /// las luminarias en <c>Wg3SceneAssembler</c>.
        /// </summary>
        public static Mesh MeshOf(byte variant)
        {
            if (Meshes.TryGetValue(variant, out Mesh cached) && cached != null) return cached;
            Vector2 s = SizeM(variant);
            Rect uv = UvOf(variant);
            float hx = s.x * 0.5f, hy = s.y * 0.5f;
            var mesh = new Mesh { name = $"Wg3Sign_{variant}", hideFlags = HideFlags.DontSave };
            mesh.vertices = new[]
            {
                new Vector3(-hx, -hy, 0f), new Vector3(hx, -hy, 0f),
                new Vector3(hx, hy, 0f), new Vector3(-hx, hy, 0f),
            };
            // **La U va al revés que la X local, y no es un capricho.** Mirando de frente a un quad
            // cuya normal es +z, la cámara está en +z mirando a −z, y ahí su «derecha» es el −x del
            // mundo: el borde de la izquierda de la textura acaba a la derecha de la pantalla. Sin
            // este cambio el cartel sale en ESPEJO, que es exactamente como salió la primera
            // captura en juego («SOSRUCER SONAMUH»), y con las dos caras puestas no había forma de
            // notarlo desde el código.
            mesh.uv = new[]
            {
                new Vector2(uv.xMax, uv.yMin), new Vector2(uv.xMin, uv.yMin),
                new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax),
            };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            // Las dos caras sobre los mismos cuatro vértices. La de atrás queda DENTRO de la pared
            // a la que va pegado el cartel, así que no se ve nunca; está para que un giro mal
            // calculado por el emisor no se manifieste como un cartel invisible, que es el fallo
            // más caro de diagnosticar de todos (mirar a una pared vacía no dice nada).
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            Meshes[variant] = mesh;
            return mesh;
        }
    }
}
