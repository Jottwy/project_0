using System;
using BackroomsSurvival.Gameplay.Body;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// ADR-149 R4b: la rotura de la ropa se ve.
    /// Tanda 1: cada renderer de prenda del muñeco (vendor y nuestras, con el shader <c>Backrooms/Garment Lit</c>) sabe qué
    /// prenda pinta; se busca esa prenda puesta y su estado por zonas (<see cref="GarmentState"/>) va al material por
    /// <see cref="MaterialPropertyBlock"/>.
    /// Tanda 2 (Joel: «debe mostrar la prenda de debajo o el cuerpo»): la piel bajo la ropa la oculta la máscara del vendor
    /// (<c>_OpacityMask_Torso/Legs/Feet</c> del material del cuerpo). Aquí se compone otra: la de la capa de dentro junto
    /// con la de encima, y destapada en cada zona donde la prenda MÁS INTERIOR que la cubre está agujereada
    /// (<see cref="GarmentVisualState.SkinReveal"/>). Un agujero en la chaqueta sobre una camiseta sana enseña la camiseta.
    /// Solo se recalcula cuando cambia algo: la versión de las prendas, lo que se lleva puesto o qué renderer está encendido.
    /// </summary>
    public sealed class BackroomsGarmentDamageVisuals : CharacterUIBehaviour
    {
        /// <summary>En <see cref="_points"/>: la prenda de encima (no es una parte del cuerpo del vendor).</summary>
        public const int OuterPoint = -1;

        private static readonly int ZonesA = Shader.PropertyToID("_GarmentZonesA");
        private static readonly int ZonesB = Shader.PropertyToID("_GarmentZonesB");
        private static readonly int ZonesC = Shader.PropertyToID("_GarmentZonesC");
        private static readonly int ZonesD = Shader.PropertyToID("_GarmentZonesD");
        private static readonly BodyPoint[] SkinPoints = { BodyPoint.Torso, BodyPoint.Legs, BodyPoint.Feet };

        [Tooltip("Renderers de prenda; el mismo índice en _itemIds, _points y _masks dice qué prenda pinta, en qué parte y qué piel tapa (los rellena el builder).")]
        [SerializeField] private SkinnedMeshRenderer[] _renderers = Array.Empty<SkinnedMeshRenderer>();
        [SerializeField] private int[] _itemIds = Array.Empty<int>();
        [SerializeField] private int[] _points = Array.Empty<int>();
        [SerializeField] private Texture2D[] _masks = Array.Empty<Texture2D>();
        [SerializeField] private string[] _wornContainers = { "Outer", "Torso", "Legs", "Feet", "Head" };

        [Header("Piel por los agujeros")]
        [SerializeField] private SkinnedMeshRenderer _body;
        [SerializeField] private Texture2D _zoneMap;
        [SerializeField] private Shader _composeShader;
        [SerializeField] private int _maskSize = 512;

        private readonly float[] _codes = new float[GarmentVisualState.Slots];
        private readonly Vector4[] _packed = new Vector4[4];
        private readonly bool[] _reveal = new bool[GarmentVisualState.Slots];
        private readonly RenderTexture[] _skinMasks = new RenderTexture[BodyPointUtility.TotalBodyPoints];
        private IItemContainer[] _containers = Array.Empty<IItemContainer>();
        private MaterialPropertyBlock _block;
        private Material _compose;
        private int _lastKey;
        private bool _dirty = true;

        protected override void OnCharacterAttached(ICharacter character)
        {
            var inventory = character.Inventory;
            if (inventory == null) return;
            _containers = new IItemContainer[_wornContainers.Length];
            for (int i = 0; i < _wornContainers.Length; i++)
                _containers[i] = inventory.FindContainer(ItemContainerFilters.WithName(_wornContainers[i]));
            _dirty = true;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            _containers = Array.Empty<IItemContainer>();
            _dirty = true;
        }

        private void OnEnable() => _dirty = true;

        protected override void OnDestroy()
        {
            base.OnDestroy();
            GarmentSkinComposer.Release(_skinMasks, ref _compose);
        }

        private void LateUpdate()
        {
            if (_containers.Length == 0) return;
            int key = Key();
            if (!_dirty && key == _lastKey) return;
            _lastKey = key;
            _dirty = false;
            _block ??= new MaterialPropertyBlock();

            for (int i = 0; i < _renderers.Length && i < _itemIds.Length; i++)
            {
                var renderer = _renderers[i];
                if (renderer == null) continue;
                var worn = FindWorn(_itemIds[i]);
                if (worn != null && worn.Definition.TryGetDataOfType(out GarmentZonesData data))
                    GarmentVisualState.ZoneCodes(GarmentState.Of(worn), data, _codes);
                else
                    Array.Clear(_codes, 0, _codes.Length);
                GarmentVisualState.Pack(_codes, _packed);
                renderer.GetPropertyBlock(_block);
                _block.SetVector(ZonesA, _packed[0]);
                _block.SetVector(ZonesB, _packed[1]);
                _block.SetVector(ZonesC, _packed[2]);
                _block.SetVector(ZonesD, _packed[3]);
                renderer.SetPropertyBlock(_block);
            }

            ApplySkin();
        }

        private void ApplySkin()
        {
            if (_body == null || _zoneMap == null || _composeShader == null) return;
            foreach (var point in SkinPoints)
            {
                int inner = ActiveIndex((int)point);
                int outer = point == BodyPoint.Torso ? ActiveIndex(OuterPoint) : -1;
                var innerMask = MaskOf(inner);
                var outerMask = MaskOf(outer);
                // Sin máscara, el vendor no oculta piel en esa parte: no hay nada que destapar.
                if (innerMask == null && outerMask == null) continue;

                Garment(inner, out var innerState, out var innerData);
                Garment(outer, out var outerState, out var outerData);
                GarmentVisualState.SkinReveal(innerState, innerData, outerState, outerData, _reveal);
                GarmentSkinComposer.Apply(_body, point, innerMask, outerMask, _reveal, _zoneMap, _composeShader, ref _compose,
                    _skinMasks, _maskSize);
            }
        }

        private int ActiveIndex(int point)
        {
            for (int i = 0; i < _renderers.Length && i < _points.Length; i++)
                if (_points[i] == point && _renderers[i] != null && _renderers[i].gameObject.activeSelf)
                    return i;
            return -1;
        }

        private Texture2D MaskOf(int index) => index >= 0 && index < _masks.Length ? _masks[index] : null;

        private void Garment(int index, out GarmentState state, out GarmentZonesData data)
        {
            state = null;
            data = null;
            if (index < 0 || index >= _itemIds.Length) return;
            var worn = FindWorn(_itemIds[index]);
            if (worn == null || !worn.Definition.TryGetDataOfType(out data)) return;
            state = GarmentState.Of(worn);
        }

        private int Key()
        {
            var hash = new HashCode();
            hash.Add(GarmentState.Version);
            foreach (var container in _containers)
                hash.Add(Worn(container));
            foreach (var renderer in _renderers)
                hash.Add(renderer != null && renderer.gameObject.activeSelf);
            return hash.ToHashCode();
        }

        private Item FindWorn(int itemId)
        {
            foreach (var container in _containers)
            {
                var item = Worn(container);
                if (item != null && item.Id == itemId) return item;
            }
            return null;
        }

        private static Item Worn(IItemContainer container)
        {
            if (container == null || container.SlotsCount == 0) return null;
            var stack = container.GetItemAtIndex(0);
            return stack.HasItem() ? stack.Item : null;
        }
    }
}
