using UnityEngine;
using System;
using UnityEngine.Serialization;

namespace PolymindGames.InventorySystem
{
    [Serializable]
    public sealed class FuelData : ItemData
    {
        [FormerlySerializedAs("_fuel")]
        [SerializeField, MinMaxSlider(1, 1000)]
        private Vector2Int _fuelCapacityRange;

        public float FuelCapacity => _fuelCapacityRange.GetRandomFromRange();
    }
}