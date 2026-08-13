using PolymindGames.UserInterface;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using System;
using System.Linq;

namespace PolymindGames
{
    public enum GameDifficulty
    {
        Easy,
        Standard,
        Difficult,
        Expert
    }

    /// <summary>
    /// Base class representing a game mode with functionality for initializing player and UI,
    /// managing spawn points, and handling the scene camera during player spawning.
    /// </summary>
    [SelectionBase]
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public abstract class GameMode : MonoSingleton<GameMode>
    {
        [SerializeField]
        private GameObject _sceneCamera;

        [SerializeField, PrefabObjectOnly, SpaceArea]
        private Player _playerPrefab;

        [SerializeField, PrefabObjectOnly]
        private PlayerUI _playerUIPrefab;

        [SerializeField, SpaceArea]
        [Help("Initial spawn point used only at the start of the game. For respawns, the spawn points list will be used.")]
        private Transform _initialSpawnPoint;

        [SerializeField]
        private SelectionType _spawnPointSelectionType;

        [SerializeField, Range(0f, 1f)]
        private float _spawnRotationRandomness = 0.15f;

        [SerializeField]
        [ReorderableList(ListStyle.Lined, HasLabels = false, Foldable = true)]
        private Transform[] _spawnPoints = Array.Empty<Transform>();

        private PlayerUI _localPlayerUI;
        private Player _localPlayer;
        private int _lastSpawnPoint;

        /// <summary>
        /// The current player instance in the game mode.
        /// </summary>
        public Player LocalPlayer
        {
            get => _localPlayer;
#if XWIZARD_GAMES_STP_MP // Temporary Hack
            set => _localPlayer = value;
#endif
        }

        /// <summary>
        /// The current Player UI instance in the game mode.
        /// </summary>
        public PlayerUI LocalPlayerUI
        {
            get => _localPlayerUI;
#if XWIZARD_GAMES_STP_MP
            set => _localPlayerUI = value;
#endif
        }

        private IEnumerator Start()
        {
            _localPlayer = Player.AllPlayers.FirstOrDefault();

            if (_localPlayer == null)
            {
                Player.PlayerCreated += DestroySceneCamera;
                yield return null;
                // Wait for the boot gate (e.g. IPC connection) before spawning.
                // GameBootGate defaults to ready, so standalone scenes spawn
                // immediately; the networking layer feeds it in scenes that need to
                // wait for a connection. NO offline timeout on purpose: it used to
                // spawn the player after 10 s with no backend — a player in an empty
                // world (nothing streams chunks) that breaks the whole session. If
                // the session never starts, waiting (with the Host/Join UI up) is
                // the correct outcome.
                while (!GameBootGate.IsReady())
                    yield return null;
                _localPlayer = Player.AllPlayers.FirstOrDefault() ?? SpawnPlayer();
            }
            else
            {
                DestroySceneCamera(_localPlayer);
            }
            
            yield return null;

            _localPlayerUI = PlayerUI.Instance ?? SpawnPlayerUI();

            if (_localPlayerUI != null)
                _localPlayerUI.AttachToCharacter(_localPlayer);

            OnPlayerInitialized(_localPlayer, _localPlayerUI);

            Player.PlayerCreated -= DestroySceneCamera;
        }

        protected virtual void OnEnable()
        {
            UnityUtility.LockCursor();
        }

        protected virtual void OnDisable()
        {
            UnityUtility.UnlockCursor();
        }

        /// <summary>
        /// Called after the player and UI have been initialized. Can be overridden in derived classes for additional setup.
        /// </summary>
        /// <param name="player">The initialized player instance.</param>
        /// <param name="playerUI">The initialized player UI instance.</param>
        protected virtual void OnPlayerInitialized(Player player, PlayerUI playerUI) { }

        private T FindObjectsInRoot<T>(List<GameObject> rootObjects) where T : Component
        {
            foreach (var rootObject in rootObjects)
            {
                if (rootObject.TryGetComponent(out T component))
                    return component;
            }

            return null;
        }

        private Player SpawnPlayer()
        {
            if (_playerPrefab == null)
                throw new NullReferenceException("The Player prefab is not assigned. Please assign one in the inspector.");

            return Instantiate(_playerPrefab);
        }

        private PlayerUI SpawnPlayerUI()
        {
            if (_playerUIPrefab == null)
            {
                Debug.LogWarning("The Player UI prefab is not assigned. Consider assigning one in the inspector.", gameObject);
                return null;
            }

            return Instantiate(_playerUIPrefab);
        }

        private void DestroySceneCamera(Player player)
        {
            if (_sceneCamera != null)
                Destroy(_sceneCamera.gameObject);
        }

        /// <summary>
        /// Returns a random spawn point for the player, either the initial spawn or a random one from the spawn points list.
        /// </summary>
        /// <param name="first">If true, uses the initial spawn point (if set).</param>
        /// <returns>A tuple containing the spawn position and rotation with randomization.</returns>
        public (Vector3 position, Quaternion rotation) GetRandomSpawnPoint(bool first)
        {
            Transform spawnPoint;

            if (first && _initialSpawnPoint != null)
            {
                spawnPoint = _initialSpawnPoint;
            }
            else
            {
                spawnPoint = _spawnPoints.Length > 0
                    ? _spawnPoints.Select(ref _lastSpawnPoint, _spawnPointSelectionType)
                    : transform;
            }

            Vector3 position = spawnPoint.position;
            float yAngle = UnityEngine.Random.Range(0f, 360f);
            Quaternion randomRotation = Quaternion.Euler(0f, yAngle, 0f);
            Quaternion rotation = Quaternion.Lerp(spawnPoint.rotation, randomRotation, _spawnRotationRandomness);

            return (position, rotation);
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_sceneCamera == null)
            {
                var cam = GetComponentInChildren<Camera>(true);
                if (cam != null)
                    _sceneCamera = cam.gameObject;
            }
        }

        private void OnDrawGizmos()
        {
            if (_spawnPoints.Length == 0)
            {
                SnapSpawnPointToGround(transform);
                DrawSpawnPointGizmo(transform);
            }
            else
            {
                foreach (var spawnPoint in _spawnPoints)
                {
                    if (spawnPoint != null)
                    {
                        SnapSpawnPointToGround(spawnPoint);
                        DrawSpawnPointGizmo(spawnPoint);
                    }
                }
            }
        }

        private static void SnapSpawnPointToGround(Transform spawnPoint)
        {
            // Snaps the spawn point position to the ground.
            if (Physics.Raycast(spawnPoint.position + Vector3.up * 0.25f, Vector3.down, out RaycastHit hit, 10f)
                || Physics.Raycast(spawnPoint.position + Vector3.up, Vector3.down, out hit, 10f))
            {
                spawnPoint.position = hit.point;
            }
        }

        private static void DrawSpawnPointGizmo(Transform spawnPoint)
        {
            Color prevColor = Gizmos.color;
            Gizmos.color = new Color(0.1f, 0.9f, 0.1f, 0.35f);

            const float GizmoWidth = 0.5f;
            const float GizmoHeight = 1.8f;

            Vector3 position = spawnPoint.position;
            Gizmos.DrawCube(new Vector3(position.x, position.y + GizmoHeight / 2, position.z), new Vector3(GizmoWidth, GizmoHeight, GizmoWidth));

            Gizmos.color = prevColor;
        }
#endif
        #endregion
    }
}