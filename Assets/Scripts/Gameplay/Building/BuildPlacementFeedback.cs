using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.BuildingSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// Enmienda a ADR-081 — el fantasma se pinta de ROJO donde la regla de territorio va a rechazar
    /// la colocación, en vez de cerrarse el modo construcción de golpe.
    ///
    /// SUSTITUYE a `BuildZoneGate`, y el motivo por el que aquello existía resultó ser FALSO: la
    /// decisión 4 del ADR daba por imposible teñir el fantasma sin editar el vendor, mirando
    /// `BuildingPiece.SetState` (que sí es `protected`) y parando ahí. El tinte no lo hace el estado:
    /// lo hace <see cref="MaterialEffect.EnableEffect"/>, que es PÚBLICO, con el
    /// `MaterialEffectConfig` que expone `BuildingManager.PlacementDeniedMaterialEffect`, también
    /// público. O sea que se pinta con el MISMO rojo del vendor y sin tocar una línea suya.
    ///
    /// Se mide contra la posición del FANTASMA, no la del jugador: es la posición que valida el host,
    /// y es lo que hace que el color cambie en vivo al barrer la mira sobre el borde del claim.
    ///
    /// SIGUE SIN SER AUTORIDAD (ver <see cref="BuildPermission"/>): el tinte es cosmético y el vendor
    /// deja pulsar igual. Quien impide de verdad la colocación son `StpBuildingPlacementWatcher`
    /// (no la manda) y el host (la rechaza). Rojo significa "esto se va a rechazar", no "el botón
    /// está muerto".
    ///
    /// LÍMITE CONOCIDO, y es el precio de no tocar el vendor: cuando nuestra regla dice que SÍ, hay
    /// que devolver el tinte a "permitido", y ahí se pisa el veredicto propio del vendor. Si el
    /// vendor estaba denegando por su cuenta —sin superficie bajo la mira, o solape con otra pieza—
    /// el fantasma se verá verde aunque su click vaya a fallar con el sonido de colocación inválida.
    /// Leer ese veredicto exigiría un accesor que el vendor no expone; duplicar su raycast sería
    /// reimplementarlo y quedaría desincronizado al primer reimport.
    /// </summary>
    public sealed class BuildPlacementFeedback : MonoBehaviour
    {
        private static BuildPlacementFeedback _instance;

        private BuildingPiece _tinted;
        private bool _lastAllowed = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[BuildPlacementFeedback]");
            _instance = go.AddComponent<BuildPlacementFeedback>();
            DontDestroyOnLoad(go);
        }

        // LateUpdate, no Update: CharacterBuildController corre en LateUpdate con
        // [DefaultExecutionOrder(AfterDefault1)] y es ahí donde reposiciona el fantasma y fija su
        // estado. Midiendo en Update se leería la pose del frame ANTERIOR, que en pleno barrido de
        // mira es justo un frame de color equivocado en el borde del claim.
        private void LateUpdate()
        {
            // HasInstance, no `Instance != null`: el getter del MonoSingleton del vendor ESCRIBE UN
            // ERROR con stack trace cada vez que se lee sin estar puesto. Misma guarda que
            // StpBuildingPlacementWatcher.
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            var piece = character != null && character.TryGetCC(out IBuildControllerCC controller)
                ? controller.BuildingPiece
                : null;

            if (piece == null)
            {
                _tinted = null;
                return;
            }

            var definition = piece.Definition;
            int defId = definition != null ? definition.Id : 0;
            bool allowed = BuildPermission.CanPlaceAt(piece.transform.position, defId);

            // Solo al cambiar de veredicto o de pieza. `MaterialEffect.SetEffect` ya sale temprano si
            // el efecto es el mismo, así que esto no es por coste: es para no pisar CADA frame el
            // tinte del vendor y dejarle repintar lo suyo mientras nuestra regla no tenga nada que
            // decir (ver el límite conocido de arriba).
            if (ReferenceEquals(piece, _tinted) && allowed == _lastAllowed)
                return;

            _tinted = piece;
            _lastAllowed = allowed;
            Tint(piece, allowed);
        }

        private static void Tint(BuildingPiece piece, bool allowed)
        {
            if (!piece.TryGetComponent(out MaterialEffect effect) || BuildingManager.Instance == null)
                return;

            effect.EnableEffect(allowed
                ? BuildingManager.Instance.PlacementAllowedMaterialEffect
                : BuildingManager.Instance.PlacementDeniedMaterialEffect);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
