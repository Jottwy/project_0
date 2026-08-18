using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.BuildingSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// Enmienda a ADR-081 — dibuja el borde de cada territorio reclamado, para que el radio de 25 m
    /// deje de ser un número invisible que solo se descubre chocándose con él.
    ///
    /// SOLO MIENTRAS HAY UNA PIEZA EN LA MANO (decisión de Joel): es cuando el borde importa, y deja
    /// el mundo limpio el resto del tiempo — que en un juego cuyo tono es el vacío no es un detalle
    /// menor. Guardar la pieza apaga los círculos en el mismo frame.
    ///
    /// El color dice de quién es: **tuyo** o **de otro**. Se deriva del mismo sitio que la regla del
    /// host, la lista replicada de marcadores, así que un círculo no puede mentir sobre dónde te
    /// dejarán construir — es la MISMA fuente, no una copia.
    ///
    /// Cero assets: `LineRenderer` + material de <see cref="MaterialHelper"/>, que se apoya en los
    /// `.mat` ya commiteados de `Resources/BackroomsRuntimeMaterials` (y por eso no lo puede podar el
    /// stripping de shaders de un build, que es lo que le pasaría a un `Shader.Find` a pelo).
    ///
    /// DEUDA DE NOMBRE, asumida: desde la enmienda 3 dibuja un CUADRADO (el bloque de rejilla), no un
    /// círculo. El nombre se conserva porque es el que citan la enmienda 2 de ADR-081 y el commit que
    /// lo introdujo, y un ADR no se reescribe; renombrarlo dejaría esas referencias apuntando a nada.
    /// </summary>
    public sealed class ClaimCircleRenderer : MonoBehaviour
    {
        /// <summary>Cuatro esquinas: el claim es una HABITACIÓN rectangular, no un disco, así que el
        /// borde son cuatro segmentos y no una curva. Dibujar el borde real y no una aproximación es
        /// lo que permite al jugador contar tiles con la vista.</summary>
        private const int Segments = 4;

        /// <summary>Levantado del suelo para no pelearse en z-fighting con la losa.</summary>
        private const float GroundOffset = 0.05f;

        private const float LineWidth = 0.12f;

        /// <summary>Cuántos círculos se dibujan a la vez. Un claim ocupa medio chunk, así que a la
        /// distancia de streaming no puede haber muchos más a la vista; el tope existe para que un
        /// mundo muy construido no crezca sin límite, no porque se espere alcanzarlo.</summary>
        private const int MaxCircles = 12;

        private static readonly Color OwnColour = new Color(0.35f, 0.9f, 0.55f, 0.7f);
        private static readonly Color OtherColour = new Color(0.95f, 0.3f, 0.25f, 0.7f);

        private static ClaimCircleRenderer _instance;

        private readonly List<LineRenderer> _circles = new List<LineRenderer>();
        private Material _ownMaterial;
        private Material _otherMaterial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[ClaimCircleRenderer]");
            _instance = go.AddComponent<ClaimCircleRenderer>();
            DontDestroyOnLoad(go);
        }

        private void LateUpdate()
        {
            if (!IsBuilding())
            {
                HideFrom(0);
                return;
            }

            if (!IPCClient.TryGetInstance(out var ipc) || ipc.LatestState == null)
            {
                HideFrom(0);
                return;
            }

            ushort self = BuildPermission.LocalPeerId();
            var buildings = ipc.LatestState.stpBuildings;
            int drawn = 0;

            for (int i = 0; i < buildings.Count && drawn < MaxCircles; i++)
            {
                var b = buildings[i];
                if (b.defId != BuildPermission.ClaimMarkerDefId)
                    continue;

                Draw(drawn, b.position, b.ownerId == self && self != 0);
                drawn++;
            }

            HideFrom(drawn);
        }

        private static bool IsBuilding()
        {
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            return character != null
                   && character.TryGetCC(out IBuildControllerCC controller)
                   && controller.BuildingPiece != null;
        }

        private void Draw(int index, Vector3 markerPos, bool own)
        {
            var line = Circle(index);
            line.gameObject.SetActive(true);
            line.sharedMaterial = own ? OwnMaterial() : OtherMaterial();

            // El borde REAL de la habitación tallada, tal cual lo mandó el backend: el marcador
            // puede estar en cualquier punto de la sala y el rectángulo dibujado sigue siendo el que
            // aplica el host. Si el chunk aún no ha llegado no se dibuja nada — mejor sin borde que
            // con uno inventado.
            if (!BuildRoomRegistry.TryGetRoomOrigin(markerPos, out var origin))
            {
                line.gameObject.SetActive(false);
                return;
            }
            float side = BuildRoomRegistry.RoomSizeMeters;

            // El marcador se planta con su pivote en el suelo, así que su propia Y ES la del suelo —
            // no hace falta raycast. En un suelo con desnivel el borde cortaría la geometría, y eso
            // es aceptable: la regla del host también es una casilla plana en XZ.
            float y = markerPos.y + GroundOffset;
            line.SetPosition(0, new Vector3(origin.x, y, origin.z));
            line.SetPosition(1, new Vector3(origin.x + side, y, origin.z));
            line.SetPosition(2, new Vector3(origin.x + side, y, origin.z + side));
            line.SetPosition(3, new Vector3(origin.x, y, origin.z + side));
        }

        private LineRenderer Circle(int index)
        {
            while (_circles.Count <= index)
            {
                var go = new GameObject($"ClaimCircle_{_circles.Count}");
                go.transform.SetParent(transform, false);

                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.loop = true;
                line.positionCount = Segments;
                line.widthMultiplier = LineWidth;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.alignment = LineAlignment.View;
                line.textureMode = LineTextureMode.Stretch;

                _circles.Add(line);
            }

            return _circles[index];
        }

        private void HideFrom(int index)
        {
            for (int i = index; i < _circles.Count; i++)
            {
                if (_circles[i].gameObject.activeSelf)
                    _circles[i].gameObject.SetActive(false);
            }
        }

        private Material OwnMaterial() => _ownMaterial ??= MaterialHelper.MakeTransparent(OwnColour);

        private Material OtherMaterial() => _otherMaterial ??= MaterialHelper.MakeTransparent(OtherColour);

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
