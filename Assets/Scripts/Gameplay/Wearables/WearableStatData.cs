using System;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// Modificadores de stats de una prenda puesta (INVENTORY-ROADMAP, Joel 2026-09-13: «zapatos que te den bonus de
    /// velocidad»). Dato de DEFINICIÓN, igual para todas las instancias. Empieza solo con velocidad; cada stat nuevo es
    /// un campo más aquí y una suma más en <see cref="BackroomsWornStats"/>.
    /// </summary>
    [Serializable]
    public sealed class WearableStatData : ItemData
    {
        // Porcentaje sobre andar y correr: +8 = un 8 % más rápido.
        [SerializeField, Range(-50f, 50f)]
        private float _speedPct;

        public WearableStatData() { }

        public WearableStatData(float speedPct) => _speedPct = speedPct;

        public float SpeedPct => _speedPct;
    }
}
