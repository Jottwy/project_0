using System;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// Capacidad de una prenda o mochila (ADR-147 PROPUESTA, enfoque A; INVENTORY-ROADMAP D2 y D6 enmendado):
    /// cuántos huecos da, cuántos kg aguanta ella sola (impide guardar) y cuánto mueve los tramos de carga.
    /// Dato de DEFINICIÓN, igual para todas las instancias del objeto.
    /// </summary>
    [Serializable]
    public sealed class WearableCapacityData : ItemData
    {
        [SerializeField, Range(0, 27)]
        private int _slots;

        [SerializeField, Range(0f, 100f)]
        private float _maxKg;

        [SerializeField, Range(-50f, 50f)]
        private float _carryBonusPct;

        public WearableCapacityData() { }

        public WearableCapacityData(int slots, float maxKg, float carryBonusPct)
        {
            _slots = slots;
            _maxKg = maxKg;
            _carryBonusPct = carryBonusPct;
        }

        public int Slots => _slots;
        public float MaxKg => _maxKg;
        public float CarryBonusPct => _carryBonusPct;
    }
}
