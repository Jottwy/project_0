using System.Collections.Generic;
using PolymindGames.InventorySystem;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-072 — leer y aplicar las propiedades de instancia de un <see cref="Item"/> (desgaste,
    /// munición cargada, progreso de cocinado).
    ///
    /// Existe porque el mismo par leer/aplicar hace falta en cinco sitios —el reporte de
    /// inventario, el snapshot de muerte, el cadáver que se materializa, el rollback de una toma
    /// rechazada y el reconcile contra el roster— y cada copia era una oportunidad de que uno
    /// aceptara lo que otro descarta. La forma canónica del dato la fija ADR-045 Fase 3
    /// (<c>ItemPropertyValue {id, value}</c>), y aquí no se inventa ninguna otra.
    /// </summary>
    public static class ItemProps
    {
        /// <summary>
        /// Las propiedades que ESTA instancia tiene ahora mismo, o <c>null</c> si no tiene ninguna
        /// (el caso de casi todos los items — devolver null y no una lista vacía deja al llamante
        /// saltarse el trabajo con una comparación).
        ///
        /// El rodeo por <c>GetPropertyGenerators()</c> no es capricho: los VALORES viven en el
        /// <see cref="Item"/> pero solo se alcanzan de uno en uno con <c>TryGetProperty</c>, que no
        /// expone el conjunto; los IDS que pueden existir salen de la definición. Cruzar los dos es
        /// la forma documentada de enumerar "las propiedades reales de esta instancia" sin
        /// inventarse un modelo paralelo.
        /// </summary>
        public static List<ItemPropertyValue> Read(Item item)
        {
            var generators = item?.Definition?.GetPropertyGenerators();
            if (generators == null || generators.Length == 0)
                return null;

            List<ItemPropertyValue> props = null;
            for (int i = 0; i < generators.Length; i++)
            {
                int propId = generators[i].Property.Id;
                if (item.TryGetProperty(propId, out var prop))
                    (props ??= new List<ItemPropertyValue>(generators.Length))
                        .Add(new ItemPropertyValue { id = propId, value = prop.Double });
            }
            return props;
        }

        /// <summary>
        /// Escribe esos valores sobre un item recién construido, que nace con los de fábrica.
        ///
        /// Un id que este item no reconoce se IGNORA en vez de fallar: es el mismo contrato de
        /// degradación que el resto del proyecto aplica a un `item_id` desconocido, y cubre el caso
        /// real de una definición que cambió entre versiones y ya no tiene esa propiedad.
        /// </summary>
        public static void Apply(Item item, IReadOnlyList<ItemPropertyValue> props)
        {
            if (item == null || props == null)
                return;

            for (int i = 0; i < props.Count; i++)
            {
                if (item.TryGetProperty(props[i].id, out var prop))
                    prop.Double = props[i].value;
            }
        }

        /// <summary>Un item de esa definición con estas propiedades ya puestas — el idioma que
        /// repiten los tres sitios que reconstruyen un item venido del backend.</summary>
        public static Item Build(ItemDefinition def, IReadOnlyList<ItemPropertyValue> props)
        {
            var item = new Item(def);
            Apply(item, props);
            return item;
        }
    }
}
