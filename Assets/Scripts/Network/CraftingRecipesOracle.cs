using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PolymindGames.InventorySystem;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-064 enm. 1 (E1.6) — el ORÁCULO común C#↔Rust de las recetas de crafteo:
    /// <c>docs/data/crafting-recipes.json</c>. Esta clase produce su texto EXACTO desde los
    /// <c>CraftingData</c> reales del catálogo; el menú <c>Backrooms/Create Craft Assets</c> lo
    /// escribe a disco, el test de EditMode compara el fichero con lo que esto genera, y el test
    /// de cargo (<c>crafting::spec</c>) compara su tabla con el fichero. Si una punta cambia sin la
    /// otra, una de las dos suites cae.
    ///
    /// Vive en el ensamblado de runtime, sin <c>UnityEditor</c>, porque <c>EditModeTests</c> no
    /// referencia <c>Assembly-CSharp-Editor</c> (igual que <c>ChunkLootRoll</c>: lógica pura que
    /// la suite puede llamar). Sólo lee <c>ItemDefinition.Definitions</c>.
    ///
    /// Formato = <c>json.dumps(indent=2, ensure_ascii=False)</c> con el que se acuñó el fichero:
    /// dos espacios, <c>"clave": valor</c>, un elemento por línea, salto final, LF. Recetas
    /// ordenadas por <c>item_id</c> (i32) como la tabla Rust; sólo entran las craftables
    /// (<c>IsCraftable</c>: blueprint no vacío y amount > 0).
    /// </summary>
    public static class CraftingRecipesOracle
    {
        /// <summary>Ruta relativa a la raíz del proyecto (la carpeta que contiene `Assets/`).</summary>
        public const string RelativePath = "docs/data/crafting-recipes.json";

        public const string Comment =
            "ADR-064 enm. 1 E1.6 - oraculo comun C#<->Rust de las recetas craftables (CraftingData.IsCraftable). " +
            "Lo REGENERA CraftingRecipesOracleTests (EditMode) y lo LEE crafting::spec (cargo test). No editar a mano.";

        public readonly struct Row
        {
            public readonly int ItemId;
            public readonly string Name;
            public readonly int Amount;
            public readonly int Level;
            public readonly CraftRequirement[] Blueprint;

            public Row(int itemId, string name, int amount, int level, CraftRequirement[] blueprint)
            {
                ItemId = itemId;
                Name = name;
                Amount = amount;
                Level = level;
                Blueprint = blueprint;
            }
        }

        /// <summary>Las recetas craftables del catálogo cargado, ordenadas por id.</summary>
        public static List<Row> CollectRows()
        {
            var rows = new List<Row>();
            foreach (var def in ItemDefinition.Definitions)
            {
                if (def == null || !def.TryGetDataOfType<CraftingData>(out var data) || !data.IsCraftable)
                    continue;
                rows.Add(new Row(def.Id, def.name, data.CraftAmount, data.CraftLevel, data.Blueprint));
            }
            rows.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
            return rows;
        }

        public static string BuildJson() => BuildJson(CollectRows());

        public static string BuildJson(IReadOnlyList<Row> rows)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"_comment\": \"").Append(Comment).Append("\",\n");
            sb.Append("  \"recipes\": [\n");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append("    {\n");
                sb.Append("      \"item_id\": ").Append(Int(r.ItemId)).Append(",\n");
                sb.Append("      \"name\": \"").Append(r.Name).Append("\",\n");
                sb.Append("      \"amount\": ").Append(Int(r.Amount)).Append(",\n");
                sb.Append("      \"level\": ").Append(Int(r.Level)).Append(",\n");
                sb.Append("      \"ingredients\": [\n");
                for (int j = 0; j < r.Blueprint.Length; j++)
                {
                    sb.Append("        {\n");
                    sb.Append("          \"item_id\": ").Append(Int(r.Blueprint[j].Item.Id)).Append(",\n");
                    sb.Append("          \"count\": ").Append(Int(r.Blueprint[j].Amount)).Append("\n");
                    sb.Append("        }").Append(j + 1 < r.Blueprint.Length ? "," : "").Append("\n");
                }
                sb.Append("      ]\n");
                sb.Append("    }").Append(i + 1 < rows.Count ? "," : "").Append("\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
