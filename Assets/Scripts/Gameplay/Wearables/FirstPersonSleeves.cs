using System;
using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// ADR-149 R4c: la manga de la prenda que llevas, en TUS brazos de primera persona, con su rotura.
    ///
    /// Los brazos 1P son parte de cada wieldable (mismo problema que <c>FirstPersonBandageHook</c>) y el reloj ni siquiera es
    /// el wieldable activo: cuelga de <c>WieldablesRoot</c>. Así que esto no mira el arma activa: repasa las mallas
    /// <c>LeftArm</c>/<c>RightArm</c> que cuelgan de <c>WieldablesRoot</c> y a cada una le pone al lado una funda
    /// (<see cref="SleeveRegistry"/>) que comparte sus huesos. La funda se ve si su brazo se ve y si lo puesto (encima
    /// primero, luego el torso) tiene manga larga con material de primera persona. Por un agujero se ve el brazo, que está
    /// debajo. Las fundas cuelgan del wieldable y se destruyen con él.
    /// </summary>
    public sealed class FirstPersonSleeves : MonoBehaviour
    {
        private const string LeftArmName = "LeftArm";
        private const string RightArmName = "RightArm";
        private const string SleeveName = "BR_Sleeve";
        private const float RescanSeconds = 0.5f;

        private static readonly int ZonesA = Shader.PropertyToID("_GarmentZonesA");
        private static readonly int ZonesB = Shader.PropertyToID("_GarmentZonesB");
        private static readonly int ZonesC = Shader.PropertyToID("_GarmentZonesC");
        private static readonly int ZonesD = Shader.PropertyToID("_GarmentZonesD");

        private static FirstPersonSleeves _instance;

        private readonly List<(SkinnedMeshRenderer Arm, SkinnedMeshRenderer Sleeve)> _pairs = new();
        private readonly List<SkinnedMeshRenderer> _scan = new();
        private readonly float[] _codes = new float[GarmentVisualState.Slots];
        private readonly Vector4[] _packed = new Vector4[4];
        private SleeveRegistry _registry;
        private ICharacter _character;
        private IWieldablesControllerCC _controller;
        private IItemContainer _outer;
        private IItemContainer _torso;
        private MaterialPropertyBlock _block;
        private float _nextScan;
        private int _lastKey;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("[FirstPersonSleeves]");
            _instance = go.AddComponent<FirstPersonSleeves>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            _registry = Resources.Load<SleeveRegistry>(SleeveRegistry.ResourcesPath);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void LateUpdate()
        {
            if (_registry == null || !Resolve()) return;

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanSeconds;
                Rescan();
            }

            var garment = WornLongSleeve(out var material);
            var hash = new HashCode();
            hash.Add(garment);
            hash.Add(GarmentState.Version);
            hash.Add(_pairs.Count);
            int key = hash.ToHashCode();
            bool dirty = key != _lastKey;
            _lastKey = key;
            if (dirty)
            {
                if (garment != null && garment.Definition.TryGetDataOfType(out GarmentZonesData data))
                    GarmentVisualState.ZoneCodes(GarmentState.Of(garment), data, _codes);
                else
                    Array.Clear(_codes, 0, _codes.Length);
                GarmentVisualState.Pack(_codes, _packed);
            }

            _block ??= new MaterialPropertyBlock();
            for (int i = _pairs.Count - 1; i >= 0; i--)
            {
                var (arm, sleeve) = _pairs[i];
                if (arm == null || sleeve == null)
                {
                    _pairs.RemoveAt(i);
                    continue;
                }
                bool visible = material != null && arm.enabled && arm.gameObject.activeInHierarchy;
                if (sleeve.gameObject.activeSelf != visible) sleeve.gameObject.SetActive(visible);
                if (!visible) continue;
                if (sleeve.sharedMaterial != material) sleeve.sharedMaterial = material;
                if (!dirty) continue;
                sleeve.GetPropertyBlock(_block);
                _block.SetVector(ZonesA, _packed[0]);
                _block.SetVector(ZonesB, _packed[1]);
                _block.SetVector(ZonesC, _packed[2]);
                _block.SetVector(ZonesD, _packed[3]);
                sleeve.SetPropertyBlock(_block);
            }
        }

        /// <summary>La prenda puesta cuya manga se ve: la de encima si tiene manga larga y material 1P, si no la del torso.</summary>
        private Item WornLongSleeve(out Material material)
        {
            var item = LongSleeveIn(_outer, out material);
            return item != null ? item : LongSleeveIn(_torso, out material);
        }

        private Item LongSleeveIn(IItemContainer container, out Material material)
        {
            material = null;
            var item = FirstItem(container);
            if (item == null || !HasLongSleeves(item)) return null;
            material = _registry.MaterialFor(item.Id);
            return material != null ? item : null;
        }

        /// <summary>Manga larga = la prenda cubre algún antebrazo.</summary>
        public static bool HasLongSleeves(GarmentZonesData data)
            => data != null && (data.IndexOf(BodyZone.ForearmL) >= 0 || data.IndexOf(BodyZone.ForearmR) >= 0);

        private static bool HasLongSleeves(Item item) => item.Definition.TryGetDataOfType(out GarmentZonesData data) && HasLongSleeves(data);

        private static Item FirstItem(IItemContainer container)
        {
            if (container == null || container.SlotsCount == 0) return null;
            var stack = container.GetItemAtIndex(0);
            return stack.HasItem() ? stack.Item : null;
        }

        private void Rescan()
        {
            var root = _controller.WieldablesRoot;
            if (root == null) return;
            root.GetComponentsInChildren(true, _scan);
            foreach (var arm in _scan)
            {
                if (arm.name != LeftArmName && arm.name != RightArmName) continue;
                if (HasPair(arm)) continue;
                var mesh = _registry.SleeveFor(arm.sharedMesh);
                if (mesh == null) continue;
                _pairs.Add((arm, Attach(arm, mesh)));
            }
        }

        private bool HasPair(SkinnedMeshRenderer arm)
        {
            foreach (var pair in _pairs)
                if (pair.Arm == arm) return true;
            return false;
        }

        /// <summary>La funda al lado de su brazo: mismos huesos, misma capa, sin sombra (como los brazos).</summary>
        private static SkinnedMeshRenderer Attach(SkinnedMeshRenderer arm, Mesh mesh)
        {
            var existing = arm.transform.parent != null ? arm.transform.parent.Find($"{SleeveName}_{arm.name}") : null;
            if (existing != null && existing.TryGetComponent(out SkinnedMeshRenderer found)) return found;

            var go = new GameObject($"{SleeveName}_{arm.name}") { layer = arm.gameObject.layer };
            go.transform.SetParent(arm.transform.parent, false);
            go.transform.SetLocalPositionAndRotation(arm.transform.localPosition, arm.transform.localRotation);
            go.transform.localScale = arm.transform.localScale;
            var sleeve = go.AddComponent<SkinnedMeshRenderer>();
            sleeve.sharedMesh = mesh;
            sleeve.bones = arm.bones;
            sleeve.rootBone = arm.rootBone;
            sleeve.localBounds = arm.localBounds;
            sleeve.updateWhenOffscreen = true;
            sleeve.shadowCastingMode = ShadowCastingMode.Off;
            sleeve.receiveShadows = arm.receiveShadows;
            go.SetActive(false);
            return sleeve;
        }

        private bool Resolve()
        {
            if (_character is UnityEngine.Object characterObject && characterObject == null)
            {
                _character = null;
                _controller = null;
                _pairs.Clear();
            }
            if (_character == null)
            {
                _character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
                if (_character == null) return false;
                _controller = _character.GetCC<IWieldablesControllerCC>();
                var inventory = _character.Inventory;
                _outer = inventory?.FindContainer(ItemContainerFilters.WithName("Outer"));
                _torso = inventory?.FindContainer(ItemContainerFilters.WithTag(ItemConstants.TorsoEquipmentTag));
                _nextScan = 0f;
            }
            return _controller != null;
        }
    }
}
