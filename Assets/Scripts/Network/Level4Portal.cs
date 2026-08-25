using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-093 — el hueco de una puerta del Level 4 enseña lo que hay al otro lado de su gemela.
    ///
    /// CÓMO. Una cámara colocada en la gemela con la MISMA transformación relativa que la del
    /// jugador respecto a esta puerta, renderizando a una render texture que el quad del hueco
    /// muestrea en coordenadas de pantalla (`Backrooms/Portal`). Como la imagen ya viene en el
    /// encuadre correcto, el quad sólo la recorta: te asomas y la vista se mueve contigo, que es
    /// lo que separa un portal de una televisión colgada.
    ///
    /// La matriz es `gemela.localToWorld * esta.worldToLocal`, general: hoy las dos puertas miran
    /// igual y eso la reduce a una traslación, pero escrita así sigue siendo correcta el día que
    /// una rote.
    ///
    /// POR QUÉ HACE FALTA FIJAR CHUNKS. Con `viewRadius` = 1 el cliente tiene 9 chunks alrededor
    /// del jugador y nada más; la gemela está a 10 km, así que sin
    /// <see cref="ChunkStreamer.SetPinned"/> la cámara renderizaría el vacío. Se fija
    /// al ACERCARSE (no al abrir) para que la geometría ya esté cuando la puerta se abra, y se
    /// suelta al alejarse.
    ///
    /// LÍMITE CONOCIDO Y ACEPTADO (decisión de Joel): el otro lado sale SIN CRIATURAS. El backend
    /// sólo transmite entidades cerca del jugador, así que el vestíbulo se ve vacío aunque tenga
    /// facelings dentro. Arreglarlo es transmisión de entidades a distancia — trabajo de red, no
    /// de render.
    /// </summary>
    [RequireComponent(typeof(Level4DoorTrigger))]
    public sealed class Level4Portal : MonoBehaviour
    {
        /// Resolución del render del portal. Deliberadamente baja: es una ventana de 1,6 × 2,4 m
        /// vista de lejos, y esto es un segundo render del mundo por frame.
        private const int RtSize = 512;
        /// A qué distancia se empieza a fijar el otro lado. Más que el alcance de uso, para que
        /// los chunks lleguen ANTES de que el jugador pueda abrir la puerta.
        private const float PinRadiusM = 18f;
        /// Radio en chunks del islote fijado al otro lado. 1 = 3×3, lo mismo que ve el jugador.
        private const int PinChunkRadius = 1;
        private const float ChunkSide = GridConstants.ChunkCells * GridConstants.CellSize;

        private Level4DoorTrigger _door;
        private Level4Portal _twin;

        private Camera _cam;
        private RenderTexture _rt;
        private Renderer _surface;
        private Material _mat;
        private bool _pinning;

        /// Todos los portales vivos. La cámara de CUALQUIERA de ellos tiene que renderizar sin
        /// ver ninguna superficie de portal, o un portal se ve a sí mismo a través del otro y la
        /// imagen se realimenta.
        private static readonly List<Level4Portal> _all = new List<Level4Portal>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _all.Clear();

        public void Bind(Level4Portal twin, float width, float height)
        {
            _twin = twin;
            if (_surface == null)
                BuildSurface(width, height);
        }

        private void Awake()
        {
            _door = GetComponent<Level4DoorTrigger>();
            _all.Add(this);
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            _all.Remove(this);
            if (_rt != null)
                _rt.Release();
            if (_pinning)
                ChunkStreamer.UnpinAll();
        }

        private void BuildSurface(float width, float height)
        {
            // Resources.Load y no Shader.Find: `Find` sólo ve shaders que el build haya incluido
            // (por "Always Included" o por estar referenciados desde un material serializado), y
            // este no lo referencia ningún asset — se instancia entero por código. Bajo
            // Resources/ la carga es determinista en editor y en build por igual, que es el mismo
            // criterio con el que ChunkLootManager carga su ZoneLootTable.
            var shader = Resources.Load<Shader>("Shaders/BR_Portal");
            if (shader == null)
            {
                // Sin shader no hay portal, y un quad magenta en mitad del vano es peor que
                // ninguno. Se dice y se sigue: la puerta funciona igual sin la vista.
                Debug.LogWarning("[Level4Portal] shader 'Backrooms/Portal' no encontrado — " +
                                 "el hueco se queda sin vista, la puerta sigue funcionando");
                return;
            }

            _rt = new RenderTexture(RtSize, RtSize, 24, RenderTextureFormat.Default)
            {
                name = $"Level4PortalRT_{name}",
            };
            _rt.Create();

            _mat = new Material(shader);
            _mat.SetTexture("_MainTex", _rt);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "PortalSurface";
            quad.transform.SetParent(transform, false);
            // Centrado en el vano, muy pegado al plano de la puerta pero no EN él: exactamente en
            // el plano, el z-fighting con la hoja cerrada es visible desde ciertos ángulos.
            quad.transform.localPosition = new Vector3(0f, height * 0.5f, 0.01f);
            quad.transform.localScale = new Vector3(width, height, 1f);
            var col = quad.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            _surface = quad.GetComponent<Renderer>();
            _surface.sharedMaterial = _mat;
            _surface.enabled = false; // sólo se enciende con la puerta abierta

            var camGo = new GameObject($"PortalCam_{name}");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.enabled = false; // se enciende sólo cuando hay algo que enseñar
            var data = camGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
        }

        private void LateUpdate()
        {
            var playerCam = Camera.main;
            if (playerCam == null || _twin == null)
                return;

            float dist = Vector3.Distance(playerCam.transform.position, transform.position);
            UpdatePinning(dist <= PinRadiusM);

            bool show = _door != null && _door.IsOpen && dist <= PinRadiusM;
            if (_surface != null)
                _surface.enabled = show;
            if (_cam == null)
                return;
            _cam.enabled = show;
            if (!show)
                return;

            // La cámara gemela: misma pose relativa respecto a la OTRA puerta que la del jugador
            // respecto a ésta.
            Matrix4x4 m = _twin.transform.localToWorldMatrix * transform.worldToLocalMatrix;
            _cam.transform.SetPositionAndRotation(
                m.MultiplyPoint(playerCam.transform.position),
                m.rotation * playerCam.transform.rotation);
            _cam.fieldOfView = playerCam.fieldOfView;
            // El plano lejano tiene que cubrir el islote fijado; el cercano se queda corto para
            // no recortar lo que hay pegado al otro lado del vano.
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = Mathf.Max(playerCam.farClipPlane, ChunkSide * (PinChunkRadius + 2));
        }

        /// Fija (o suelta) el islote de chunks alrededor de la gemela. El conjunto se construye
        /// entre TODOS los portales que lo pidan, porque `SetPinned` reemplaza — si cada uno
        /// llamara con lo suyo, el segundo borraría lo del primero.
        private void UpdatePinning(bool wanted)
        {
            if (wanted == _pinning)
                return;
            _pinning = wanted;
            RepinAll();
        }

        private static void RepinAll()
        {
            var keys = new List<(int, int, int)>();
            foreach (var p in _all)
            {
                if (!p._pinning || p._twin == null)
                    continue;
                Vector3 at = p._twin.transform.position;
                int cx = Mathf.FloorToInt(at.x / ChunkSide);
                int cz = Mathf.FloorToInt(at.z / ChunkSide);
                int layer = Mathf.Clamp(
                    Mathf.FloorToInt(at.y / GridConstants.LayerHeight), 0, 3);
                for (int dz = -PinChunkRadius; dz <= PinChunkRadius; dz++)
                    for (int dx = -PinChunkRadius; dx <= PinChunkRadius; dx++)
                        keys.Add((cx + dx, cz + dz, layer));
            }
            ChunkStreamer.SetPinned(keys);
            Debug.Log($"[Level4Portal] MPTRACE step=L4 event=portal_pin chunks={keys.Count}");
        }

        // Ninguna cámara de portal debe ver una superficie de portal — ni la suya ni la de la
        // gemela. Se apagan todas mientras renderiza y se restauran después; hacerlo con capas
        // exigiría reservar una layer del proyecto, que es un recurso escaso y compartido.
        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_cam == null || cam != _cam)
                return;
            foreach (var p in _all)
                if (p._surface != null)
                    p._surface.enabled = false;
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_cam == null || cam != _cam)
                return;
            foreach (var p in _all)
                if (p._surface != null)
                    p._surface.enabled = p._door != null && p._door.IsOpen;
        }
    }
}
