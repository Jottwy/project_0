#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Editor validator for Resources/Structures/default_structures.json.
    ///
    /// The superstructure JSON is hand-edited by designers (see docs/STRUCTURES.md).
    /// This menu item lint-checks it BEFORE it reaches the runtime generator, which
    /// silently ignores malformed entries. Every problem is reported to the console
    /// with the offending structure id and field.
    ///
    /// Read-only: never writes back to the JSON. Does not touch the generator.
    ///
    /// Note: Unity's JsonUtility cannot deserialize jagged arrays (string[][]),
    /// which is exactly the `pattern` shape, so we parse with a tiny self-contained
    /// JSON reader (MiniJson below) instead of JsonUtility.
    /// </summary>
    public static class StructureValidator
    {
        private const string ResourcePath = "Structures/default_structures";

        // Characters accepted in a pattern cell (W C O P T), the vocabulary documented in
        // docs/STRUCTURES.md. The runtime consumer (`ProceduralWorldGenerator.TryPlaceStructures`)
        // was retired with the client-side generator; the JSON and `StructureDefinition` stay as
        // documentation-as-code (DEBT-ROADMAP §1), so this lint only guards that authored contract.
        private static readonly HashSet<string> ValidCells =
            new HashSet<string> { "W", "C", "O", "P", "T" };

        // Tile grid of the authored patterns: 10×10 tiles of 5 m per 50 m chunk (the legacy
        // grid the structures were written against, not the 2.5 m grid_gen tiles).
        private const int ChunkTiles = 10;

        // Layers are 0..3 → up to 4 stacked layers.
        private const int MinLayersTall = 1;
        private const int MaxLayersTall = 4;

        [MenuItem("Backrooms/Validate Structures")]
        public static void Validate()
        {
            var ta = Resources.Load<TextAsset>(ResourcePath);
            if (ta == null)
            {
                Debug.LogError(
                    $"[StructureValidator] Resources/{ResourcePath}.json not found.");
                return;
            }

            object root;
            try
            {
                root = MiniJson.Parse(ta.text);
            }
            catch (Exception e)
            {
                Debug.LogError($"[StructureValidator] JSON parse error: {e.Message}");
                return;
            }

            if (!(root is Dictionary<string, object> obj) ||
                !obj.TryGetValue("structures", out var sObj) ||
                !(sObj is List<object> structures))
            {
                Debug.LogError(
                    "[StructureValidator] Root must be an object with a \"structures\" array.");
                return;
            }

            int errorCount = 0;
            var seenIds = new HashSet<string>();

            for (int i = 0; i < structures.Count; i++)
            {
                if (!(structures[i] is Dictionary<string, object> s))
                {
                    Report(ref errorCount, $"#{i}", "(root)",
                        "entry is not a JSON object.");
                    continue;
                }

                // ── id (unique, non-empty) ─────────────────────────────
                string id = GetString(s, "id");
                string label = string.IsNullOrEmpty(id) ? $"#{i}" : id;
                if (string.IsNullOrEmpty(id))
                {
                    Report(ref errorCount, label, "id", "missing or empty.");
                }
                else if (!seenIds.Add(id))
                {
                    Report(ref errorCount, label, "id", $"duplicate id \"{id}\".");
                }

                // ── probability (0.0 .. 1.0) ───────────────────────────
                if (!TryGetNumber(s, "probability", out double prob))
                {
                    Report(ref errorCount, label, "probability",
                        "missing or not a number.");
                }
                else if (prob < 0.0 || prob > 1.0)
                {
                    Report(ref errorCount, label, "probability",
                        $"{prob.ToString(CultureInfo.InvariantCulture)} is outside [0.0, 1.0].");
                }

                // ── layers_tall (1 .. 4) ───────────────────────────────
                if (!TryGetNumber(s, "layers_tall", out double layersD))
                {
                    Report(ref errorCount, label, "layers_tall",
                        "missing or not a number.");
                }
                else
                {
                    int layers = (int)layersD;
                    if (layersD != layers)
                        Report(ref errorCount, label, "layers_tall",
                            $"{layersD.ToString(CultureInfo.InvariantCulture)} is not an integer.");
                    else if (layers < MinLayersTall || layers > MaxLayersTall)
                        Report(ref errorCount, label, "layers_tall",
                            $"{layers} is outside [{MinLayersTall}, {MaxLayersTall}].");
                }

                // ── pattern (rectangular, valid chars, fits 10×10) ─────
                ValidatePattern(s, label, ref errorCount);
            }

            if (errorCount == 0)
                Debug.Log($"[StructureValidator] All structures valid. " +
                          $"({structures.Count} checked)");
            else
                Debug.LogError($"[StructureValidator] {errorCount} error(s) found " +
                               $"across {structures.Count} structure(s).");
        }

        private static void ValidatePattern(
            Dictionary<string, object> s, string label, ref int errorCount)
        {
            if (!s.TryGetValue("pattern", out var pObj) || !(pObj is List<object> rows))
            {
                Report(ref errorCount, label, "pattern",
                    "missing or not an array of rows.");
                return;
            }
            if (rows.Count == 0)
            {
                Report(ref errorCount, label, "pattern", "is empty.");
                return;
            }

            int width = -1;
            for (int r = 0; r < rows.Count; r++)
            {
                if (!(rows[r] is List<object> row))
                {
                    Report(ref errorCount, label, "pattern",
                        $"row {r} is not an array.");
                    continue;
                }

                if (width < 0) width = row.Count;
                else if (row.Count != width)
                    Report(ref errorCount, label, "pattern",
                        $"row {r} width {row.Count} ≠ row 0 width {width} (not rectangular).");

                for (int c = 0; c < row.Count; c++)
                {
                    string cell = row[c] as string;
                    if (cell == null || !ValidCells.Contains(cell))
                        Report(ref errorCount, label, "pattern",
                            $"cell [{r}][{c}] = \"{cell}\" is not one of W C O P T.");
                }
            }

            int rowsN = rows.Count;
            int colsN = Mathf.Max(0, width);
            if (rowsN > ChunkTiles || colsN > ChunkTiles)
                Report(ref errorCount, label, "pattern",
                    $"size {colsN}×{rowsN} does not fit in a {ChunkTiles}×{ChunkTiles} chunk.");
        }

        // ─── Reporting ─────────────────────────────────────────────────

        private static void Report(ref int count, string id, string field, string msg)
        {
            count++;
            Debug.LogError($"[StructureValidator] \"{id}\" → {field}: {msg}");
        }

        // ─── Typed accessors over the MiniJson object graph ────────────

        private static string GetString(Dictionary<string, object> o, string key)
            => o.TryGetValue(key, out var v) ? v as string : null;

        private static bool TryGetNumber(
            Dictionary<string, object> o, string key, out double value)
        {
            value = 0;
            if (!o.TryGetValue(key, out var v) || !(v is double d)) return false;
            value = d;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────
        // MiniJson — tiny recursive-descent JSON reader.
        // Produces: Dictionary<string,object> / List<object> / string /
        // double / bool / null. Enough to validate the structure library;
        // not a general-purpose serializer.
        // ─────────────────────────────────────────────────────────────────
        private static class MiniJson
        {
            public static object Parse(string json)
            {
                int i = 0;
                object result = ParseValue(json, ref i);
                SkipWhitespace(json, ref i);
                if (i != json.Length)
                    throw new FormatException($"trailing characters at index {i}.");
                return result;
            }

            private static object ParseValue(string s, ref int i)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end of input.");
                char c = s[i];
                switch (c)
                {
                    case '{': return ParseObject(s, ref i);
                    case '[': return ParseArray(s, ref i);
                    case '"': return ParseString(s, ref i);
                    case 't':
                    case 'f': return ParseBool(s, ref i);
                    case 'n': Expect(s, ref i, "null"); return null;
                    default:  return ParseNumber(s, ref i);
                }
            }

            private static Dictionary<string, object> ParseObject(string s, ref int i)
            {
                var dict = new Dictionary<string, object>();
                i++; // '{'
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return dict; }
                while (true)
                {
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != '"')
                        throw new FormatException($"expected key string at index {i}.");
                    string key = ParseString(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != ':')
                        throw new FormatException($"expected ':' at index {i}.");
                    i++;
                    dict[key] = ParseValue(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length)
                        throw new FormatException("unterminated object.");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; break; }
                    throw new FormatException($"expected ',' or '}}' at index {i}.");
                }
                return dict;
            }

            private static List<object> ParseArray(string s, ref int i)
            {
                var list = new List<object>();
                i++; // '['
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref i));
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length)
                        throw new FormatException("unterminated array.");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; break; }
                    throw new FormatException($"expected ',' or ']' at index {i}.");
                }
                return list;
            }

            private static string ParseString(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++; // opening quote
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (i >= s.Length) break;
                        char e = s[i++];
                        switch (e)
                        {
                            case '"':  sb.Append('"');  break;
                            case '\\': sb.Append('\\'); break;
                            case '/':  sb.Append('/');  break;
                            case 'b':  sb.Append('\b'); break;
                            case 'f':  sb.Append('\f'); break;
                            case 'n':  sb.Append('\n'); break;
                            case 'r':  sb.Append('\r'); break;
                            case 't':  sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > s.Length)
                                    throw new FormatException("bad \\u escape.");
                                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                                break;
                            default:
                                throw new FormatException($"bad escape \\{e}.");
                        }
                    }
                    else sb.Append(c);
                }
                throw new FormatException("unterminated string.");
            }

            private static object ParseNumber(string s, ref int i)
            {
                int start = i;
                while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
                string token = s.Substring(start, i - start);
                if (double.TryParse(token, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double d))
                    return d;
                throw new FormatException($"invalid number \"{token}\".");
            }

            private static bool ParseBool(string s, ref int i)
            {
                if (s[i] == 't') { Expect(s, ref i, "true");  return true; }
                Expect(s, ref i, "false");
                return false;
            }

            private static void Expect(string s, ref int i, string literal)
            {
                if (i + literal.Length > s.Length ||
                    s.Substring(i, literal.Length) != literal)
                    throw new FormatException($"expected \"{literal}\" at index {i}.");
                i += literal.Length;
            }

            private static void SkipWhitespace(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }
        }
    }
}
#endif
