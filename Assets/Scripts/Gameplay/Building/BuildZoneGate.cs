using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.BuildingSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// ADR-081 fase 1 — cierra el MODO CONSTRUCCIÓN cuando el jugador está fuera de una zona
    /// construible, y se lo dice.
    ///
    /// POR QUÉ CIERRA EL MODO Y NO TIÑE EL FANTASMA. Lo natural sería poner la vista previa en
    /// <c>InPlacementDenied</c>, que es lo que ya hace el vendor cuando la superficie no vale. No se
    /// puede sin editar código del vendor: <c>BuildingPiece.SetState</c> es <c>protected</c> y las dos
    /// subclases que el jugador usa de verdad — <see cref="FreeBuildingPiece"/> y
    /// <see cref="GroupBuildingPiece"/> — son <c>sealed</c>, así que no hay punto de extensión desde
    /// fuera. Editar STP está prohibido en este proyecto (un reimport del `.unitypackage` se lleva el
    /// parche por delante, en silencio). Nuestras dos piezas propias
    /// (<see cref="GridWallBuildingPiece"/>, <see cref="GridPanelBuildingPiece"/>) sí podrían teñirse,
    /// pero gatear la mitad de las piezas de una manera y la otra mitad de otra es peor que una regla
    /// única.
    ///
    /// ESTO NO ES AUTORIDAD: es feedback (ver <see cref="BuildPermission"/>). El host rechaza igual.
    ///
    /// Solo actúa cuando HAY una vista previa activa, y eso es lo que lo hace silencioso: sin pieza
    /// en la mano no hay comprobación, no hay mensaje y no hay coste. El mensaje sale una vez por
    /// cancelación, no una vez por frame, porque cancelar deja al controlador sin pieza.
    ///
    /// Se auto-instala como <see cref="StpBuildingPlacementWatcher"/> — nada que cablear en escena, y
    /// borrar el fichero lo desinstala entero.
    /// </summary>
    public sealed class BuildZoneGate : MonoBehaviour
    {
        private static BuildZoneGate _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[BuildZoneGate]");
            _instance = go.AddComponent<BuildZoneGate>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            // HasInstance, no `Instance != null`: el getter del MonoSingleton del vendor ESCRIBE UN
            // ERROR con stack trace cada vez que se lee sin estar puesto, así que polearlo desde el
            // menú principal llena el log. Misma guarda que StpBuildingPlacementWatcher.
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            if (character == null)
                return;

            if (!character.TryGetCC(out IBuildControllerCC controller) || controller.BuildingPiece == null)
                return;

            if (BuildPermission.CanBuildAt(character.transform.position))
                return;

            controller.SetBuildingPiece(null);
            MessageDispatcher.Instance.Dispatch(character, MsgType.Error, BuildPermission.DeniedMessage);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
