using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// El Almond Water en la mano. Beber ya funciona hoy desde el inventario, por el Drink action
    /// compartido que lee el <c>ConsumeData</c> del propio item (ADR-030) — nada de eso pasa por
    /// aquí. Esta clase es sólo el <see cref="Wieldable"/> que <c>WieldableItem</c> exige para que
    /// el item se pueda EQUIPAR desde el cinturón; la animación de beber es la siguiente pasada.
    /// </summary>
    [AddComponentMenu("Backrooms/Almond Water")]
    public sealed class AlmondWaterWieldable : Wieldable
    {
    }
}
