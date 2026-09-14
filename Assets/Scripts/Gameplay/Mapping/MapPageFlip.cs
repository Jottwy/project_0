using UnityEngine;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.5 de MAPPING-PROTOTYPE — una hoja 3D de verdad que pasa sobre el lomo del libro al cambiar de hoja.
    /// </summary>
    /// <remarks>
    /// Malla de tira (<see cref="Columns"/> columnas) en el espacio local del panel de la página (unidades de Canvas,
    /// X a la derecha, Y arriba, la cara que se ve mira a −Z). Cada frame se recalcula en CPU: cada columna gira sobre
    /// la anterior y el borde libre va con retraso, así que la hoja se CURVA en vez de girar como una tabla.
    ///
    /// Dos submallas: el anverso lleva una copia de la hoja que se va y el reverso es papel. Material Unlit de URP y
    /// opaco: escribe profundidad y tapa la hoja nueva del Canvas (que prueba profundidad) mientras pasa. El Canvas
    /// del libro no usa <c>BR_UIWarp</c>, así que una malla sin warp en la misma capa cae en el mismo sitio.
    /// </remarks>
    public sealed class MapPageFlip : MonoBehaviour
    {
        public const int Columns = 24;
        public const float Seconds = 0.45f;
        /// <summary>Hasta dónde gira: más allá se metería en la página izquierda, que no es coplanaria.</summary>
        private const float MaxAngle = Mathf.PI * 0.92f;
        /// <summary>Cuánto se levanta del papel para no pelearse en profundidad con el Canvas.</summary>
        private const float LiftUnits = 1.5f;

        private RectTransform _page;
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Material _front;
        private Material _back;
        private Vector3[] _vertices;
        private float _started = -1f;

        public bool Playing => _started >= 0f;

        public static MapPageFlip Create(RectTransform page, Color paper)
        {
            var go = new GameObject("PageFlip");
            go.layer = page.gameObject.layer;
            go.transform.SetParent(page, false);
            var flip = go.AddComponent<MapPageFlip>();
            flip.Build(page, paper);
            return flip;
        }

        private void Build(RectTransform page, Color paper)
        {
            _page = page;
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            _front = new Material(unlit);
            // URP/Unlit usa _BaseColor y _BaseMap; Material.color escribiría _Color, que no existe.
            _back = new Material(unlit);
            _back.SetColor("_BaseColor", paper);

            int count = (Columns + 1) * 2;
            _vertices = new Vector3[count];
            var uvs = new Vector2[count];
            for (int c = 0; c <= Columns; c++)
            {
                float u = c / (float)Columns;
                uvs[2 * c] = new Vector2(u, 0f);
                uvs[2 * c + 1] = new Vector2(u, 1f);
            }

            var front = new int[Columns * 6];
            var back = new int[Columns * 6];
            for (int c = 0; c < Columns; c++)
            {
                int bl = 2 * c, tl = bl + 1, br = bl + 2, tr = bl + 3;
                // Vista desde −Z con X a la derecha e Y arriba: bl → tl → tr es horario, cara visible.
                front[6 * c] = bl; front[6 * c + 1] = tl; front[6 * c + 2] = tr;
                front[6 * c + 3] = bl; front[6 * c + 4] = tr; front[6 * c + 5] = br;
                back[6 * c] = bl; back[6 * c + 1] = tr; back[6 * c + 2] = tl;
                back[6 * c + 3] = bl; back[6 * c + 4] = br; back[6 * c + 5] = tr;
            }

            _mesh = new Mesh { name = "MapPageFlip" };
            _mesh.MarkDynamic();
            Deform(0f);
            _mesh.vertices = _vertices;
            _mesh.uv = uvs;
            _mesh.subMeshCount = 2;
            _mesh.SetTriangles(front, 0);
            _mesh.SetTriangles(back, 1);
            _mesh.RecalculateBounds();

            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterials = new[] { _front, _back };
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = false;
        }

        /// <summary>
        /// Pasa una hoja con <paramref name="page"/> en el anverso. <paramref name="forward"/> (a una hoja de número
        /// mayor): la hoja que SE VA sale de la página y pasa a la izquierda. Hacia atrás: la hoja que LLEGA viene de la
        /// izquierda y se posa, recorriendo la misma curva al revés.
        /// </summary>
        public void Play(Texture page, bool forward)
        {
            _front.SetTexture("_BaseMap", page);
            _forward = forward;
            _started = Time.unscaledTime;
            _renderer.enabled = true;
            Deform(forward ? 0f : 1f);
            Upload();
        }

        private bool _forward = true;

        /// <summary>Herramienta de capturas en editor: deja quieta la hoja del último <see cref="Play"/> en <paramref name="progress"/>.</summary>
        public void PoseForCapture(float progress)
        {
            _started = -1f;
            // Con 1 el paso ha terminado: la hoja ya no se ve (igual que al acabar Play).
            _renderer.enabled = progress < 1f;
            float eased = progress * progress * (3f - 2f * progress);
            Deform(_forward ? eased : 1f - eased);
            Upload();
        }

        private void LateUpdate()
        {
            if (_started < 0f) return;
            float p = (Time.unscaledTime - _started) / Seconds;
            if (p >= 1f)
            {
                _started = -1f;
                _renderer.enabled = false;
                return;
            }

            // Arranca despacio, acelera en el aire y se posa.
            float eased = p * p * (3f - 2f * p);
            Deform(_forward ? eased : 1f - eased);
            Upload();
        }

        private void Upload()
        {
            _mesh.vertices = _vertices;
            _mesh.RecalculateBounds();
        }

        private void Deform(float progress)
        {
            Rect rect = _page.rect;
            float segment = rect.width / Columns;
            float x = rect.xMin;
            float z = -LiftUnits;
            for (int c = 0; c <= Columns; c++)
            {
                if (c > 0)
                {
                    float u = c / (float)Columns;
                    // El borde libre va con retraso: la hoja se curva. Con progress = 1 todas llegan a MaxAngle.
                    float angle = MaxAngle * Mathf.Clamp01(progress * 1.4f - u * 0.4f);
                    x += segment * Mathf.Cos(angle);
                    z -= segment * Mathf.Sin(angle);
                }

                _vertices[2 * c] = new Vector3(x, rect.yMin, z);
                _vertices[2 * c + 1] = new Vector3(x, rect.yMax, z);
            }
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_front != null) Destroy(_front);
            if (_back != null) Destroy(_back);
        }
    }
}
