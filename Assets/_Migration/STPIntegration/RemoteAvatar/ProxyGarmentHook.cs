using System;
using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Net;
using BackroomsSurvival.Wearables;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-149 enm. 7 + ADR-022 enm.: la ropa del jugador remoto con su prenda de encima y su rotura. Lee de la vista de
    /// <see cref="RemotePlayerManager"/> lo que llega en la pose (<c>outer</c>, <c>garmentDamage</c>, <c>garmentCuts</c>, índices
    /// [Head, Torso, Legs, Feet, Outer]) y hace lo mismo que el muñeco del inventario: enciende la prenda de encima sobre lo
    /// del torso, pasa la rotura de cada prenda a su material (shader <c>Backrooms/Garment Lit</c>) y compone la máscara de
    /// piel (<see cref="GarmentSkinComposer"/>). La ropa base la sigue poniendo <see cref="ProxyClothingHook"/>; esto va en
    /// LateUpdate, detrás de él. Cableado por <c>RemoteAvatarPrefabBuilder</c>; con change-detection y re-armado en OnEnable
    /// (pool). Robapieles y facelings llegan con todo a cero: ropa sana, nada encima.
    /// </summary>
    public sealed class ProxyGarmentHook : MonoBehaviour
    {
        private static readonly int ZonesA = Shader.PropertyToID("_GarmentZonesA");
        private static readonly int ZonesB = Shader.PropertyToID("_GarmentZonesB");
        private static readonly int ZonesC = Shader.PropertyToID("_GarmentZonesC");
        private static readonly int ZonesD = Shader.PropertyToID("_GarmentZonesD");
        private static readonly BodyPoint[] SkinPoints = { BodyPoint.Torso, BodyPoint.Legs, BodyPoint.Feet };

        [Tooltip("Renderers de prenda del proxy; el mismo índice en _itemIds, _points y _masks (los rellena el builder).")]
        [SerializeField] private SkinnedMeshRenderer[] _renderers = Array.Empty<SkinnedMeshRenderer>();
        [SerializeField] private int[] _itemIds = Array.Empty<int>();
        [SerializeField] private int[] _points = Array.Empty<int>();
        [SerializeField] private Texture2D[] _masks = Array.Empty<Texture2D>();

        [Header("Prenda de encima")]
        [SerializeField] private SkinnedMeshRenderer[] _outerRenderers = Array.Empty<SkinnedMeshRenderer>();
        [SerializeField] private int[] _outerIds = Array.Empty<int>();

        [Header("Piel por los agujeros")]
        [SerializeField] private SkinnedMeshRenderer _body;
        [SerializeField] private Texture2D _zoneMap;
        [SerializeField] private Shader _composeShader;
        [SerializeField] private int _maskSize = 512;

        private readonly float[] _codes = new float[GarmentVisualState.Slots];
        private readonly Vector4[] _packed = new Vector4[4];
        private readonly bool[] _reveal = new bool[GarmentVisualState.Slots];
        private readonly RenderTexture[] _skinMasks = new RenderTexture[BodyPointUtility.TotalBodyPoints];
        private readonly GarmentState _scratch = new();
        private readonly GarmentState _scratchInner = new();
        private readonly GarmentState _scratchOuter = new();
        private readonly Dictionary<int, GarmentZonesData> _data = new();
        private RemotePlayerManager _manager;
        private MaterialPropertyBlock _block;
        private Material _compose;
        private int _lastKey;
        private bool _armed;

        private void OnEnable() => _armed = false;

        private void OnDestroy() => GarmentSkinComposer.Release(_skinMasks, ref _compose);

        private void LateUpdate()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view)) return;
            Apply(view.equipment, view.outer, view.garmentDamage, view.garmentCuts);
        }

        /// <summary>Aplica un estado de ropa. Público para verlo sin red (capturas, tests de editor).</summary>
        public void Apply(int[] equipment, int outer, uint[] damage, ushort[] cuts)
        {
            int show = BackroomsOuterClothing.OuterIndex(outer, _outerIds);
            for (int i = 0; i < _outerRenderers.Length; i++)
                if (_outerRenderers[i] != null && _outerRenderers[i].gameObject.activeSelf != (i == show))
                    _outerRenderers[i].gameObject.SetActive(i == show);

            int key = Key(equipment, outer, damage, cuts);
            if (_armed && key == _lastKey) return;
            _armed = true;
            _lastKey = key;
            _block ??= new MaterialPropertyBlock();

            for (int i = 0; i < _renderers.Length && i < _itemIds.Length; i++)
            {
                var renderer = _renderers[i];
                if (renderer == null) continue;
                if (TryState(_itemIds[i], equipment, outer, damage, cuts, _scratch, out var data))
                    GarmentVisualState.ZoneCodes(_scratch, data, _codes);
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

            foreach (var point in SkinPoints)
            {
                int inner = ActiveIndex((int)point);
                int outerIndex = point == BodyPoint.Torso ? ActiveIndex(BackroomsGarmentDamageVisuals.OuterPoint) : -1;
                GarmentZonesData innerData = null, outerData = null;
                bool hasInner = inner >= 0 && TryState(_itemIds[inner], equipment, outer, damage, cuts, _scratchInner, out innerData);
                bool hasOuter = outerIndex >= 0 && TryState(_itemIds[outerIndex], equipment, outer, damage, cuts, _scratchOuter, out outerData);
                GarmentVisualState.SkinReveal(hasInner ? _scratchInner : null, hasInner ? innerData : null,
                    hasOuter ? _scratchOuter : null, hasOuter ? outerData : null, _reveal);
                GarmentSkinComposer.Apply(_body, point, MaskOf(inner), MaskOf(outerIndex), _reveal, _zoneMap, _composeShader,
                    ref _compose, _skinMasks, _maskSize);
            }
        }

        private bool TryState(int itemId, int[] equipment, int outer, uint[] damage, ushort[] cuts, GarmentState scratch,
            out GarmentZonesData data)
        {
            data = Data(itemId);
            int slot = GarmentVisualState.WireSlot(itemId, equipment, outer);
            if (data == null || slot < 0) return false;
            GarmentVisualState.StateFromWire(scratch, At(damage, slot), At(cuts, slot));
            return true;
        }

        private GarmentZonesData Data(int itemId)
        {
            if (itemId == 0) return null;
            if (_data.TryGetValue(itemId, out var cached)) return cached;
            GarmentZonesData data = null;
            if (ItemDefinition.TryGetWithId(itemId, out var definition)) definition.TryGetDataOfType(out data);
            _data[itemId] = data;
            return data;
        }

        private int ActiveIndex(int point)
        {
            for (int i = 0; i < _renderers.Length && i < _points.Length; i++)
                if (_points[i] == point && _renderers[i] != null && _renderers[i].gameObject.activeSelf)
                    return i;
            return -1;
        }

        private Texture2D MaskOf(int index) => index >= 0 && index < _masks.Length ? _masks[index] : null;

        private int Key(int[] equipment, int outer, uint[] damage, ushort[] cuts)
        {
            var hash = new HashCode();
            hash.Add(outer);
            if (equipment != null) foreach (int id in equipment) hash.Add(id);
            if (damage != null) foreach (uint d in damage) hash.Add(d);
            if (cuts != null) foreach (ushort c in cuts) hash.Add(c);
            foreach (var renderer in _renderers) hash.Add(renderer != null && renderer.gameObject.activeSelf);
            return hash.ToHashCode();
        }

        private static uint At(uint[] values, int i) => values != null && i < values.Length ? values[i] : 0u;

        private static ushort At(ushort[] values, int i) => values != null && i < values.Length ? values[i] : (ushort)0;
    }
}
