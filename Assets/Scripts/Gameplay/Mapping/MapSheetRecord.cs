using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>Una capa tal como se guarda: clave, boli y tramos (ADR-154 D2).</summary>
    public sealed class MapSheetRecordLayer
    {
        public int Key;
        public uint Argb;
        /// <summary>Grosor en centésimas de píxel: todo el registro son enteros, idénticos en C# y en Rust.</summary>
        public int WidthCentiPx;
        public readonly List<MapRun> Runs = new List<MapRun>();
    }

    /// <summary>
    /// ADR-154 D2 — una hoja de mapa como FOTO guardable: zona, semilla, capas con sus tramos, flechas y marcas. C# puro.
    /// </summary>
    /// <remarks>
    /// **Todo son enteros.** Las posiciones locales de flechas y marcas van en centésimas de celda y el grosor en
    /// centésimas de píxel: así el JSON sale byte a byte igual aquí y en el backend de Rust, y el golden común
    /// (<c>tools/dev/fixtures/map_sheet_record.golden.json</c>) sirve de oráculo a los dos codecs.
    ///
    /// JSON compacto, con las claves siempre en el mismo orden y sin espacios. Los tramos van como arrays planos
    /// <c>[vertical, line, from, to, old, draw_index]</c>. Ni el codec ni <see cref="FromSheet"/> reordenan capas ni
    /// tramos: el orden es parte de la foto.
    /// </remarks>
    public sealed class MapSheetRecord
    {
        public const int MaxLayers = 32;
        public const int MaxRuns = 1500;
        public const int MaxLinks = 16;
        public const int MaxMarks = 64;
        public const int MaxLabelChars = 64;

        public ulong Id;
        public uint Rev;
        public bool HasZone;
        public MapZone Zone;
        public uint Seed;
        public bool Clean;
        public string Label = "";
        public readonly List<MapSheetRecordLayer> Layers = new List<MapSheetRecordLayer>();
        public readonly List<MapLink> Links = new List<MapLink>();
        public readonly List<MapMark> Marks = new List<MapMark>();

        /// <summary>Los topes del ADR. Devuelve false con el motivo; nunca recorta.</summary>
        public bool Validate(out string reason)
        {
            reason = null;
            if (Layers.Count > MaxLayers) reason = $"capas {Layers.Count} > {MaxLayers}";
            else if (Links.Count > MaxLinks) reason = $"enlaces {Links.Count} > {MaxLinks}";
            else if (Marks.Count > MaxMarks) reason = $"marcas {Marks.Count} > {MaxMarks}";
            else if ((Label ?? "").Length > MaxLabelChars) reason = $"etiqueta de {Label.Length} caracteres > {MaxLabelChars}";
            else
            {
                int runs = 0;
                int previousKey = -1;
                foreach (MapSheetRecordLayer layer in Layers)
                {
                    if (layer.Key <= previousKey) { reason = $"clave de capa {layer.Key} no creciente"; break; }
                    previousKey = layer.Key;
                    foreach (MapRun run in layer.Runs)
                    {
                        if (run.To <= run.From) { reason = $"tramo vacío {run.From}..{run.To}"; break; }
                        runs++;
                    }

                    if (reason != null) break;
                }

                if (reason == null && runs > MaxRuns) reason = $"tramos {runs} > {MaxRuns}";
            }

            return reason == null;
        }

        public int RunCount
        {
            get
            {
                int count = 0;
                foreach (MapSheetRecordLayer layer in Layers) count += layer.Runs.Count;
                return count;
            }
        }

        /// <summary>Lo guardable de <paramref name="sheet"/>. Los trazos no se guardan: se rehacen desde los tramos.</summary>
        public static MapSheetRecord FromSheet(MapSheet sheet, ulong id, uint rev)
        {
            var record = new MapSheetRecord
            {
                Id = id,
                Rev = rev,
                HasZone = sheet.HasZone,
                Zone = sheet.Zone,
                Seed = unchecked((uint)sheet.Seed),
                Clean = sheet.Clean,
            };

            foreach (MapSheetLayer layer in sheet.Layers)
            {
                var saved = new MapSheetRecordLayer
                {
                    Key = layer.Key,
                    Argb = layer.Argb,
                    WidthCentiPx = ToCenti(layer.WidthPx),
                };
                saved.Runs.AddRange(layer.Runs);
                record.Layers.Add(saved);
            }

            foreach (MapLink link in sheet.Links)
                record.Links.Add(new MapLink(link.To, link.Side, FromCenti(ToCenti(link.LocalX)), FromCenti(ToCenti(link.LocalZ))));
            foreach (MapMark mark in sheet.Marks)
                record.Marks.Add(new MapMark(mark.Kind, FromCenti(ToCenti(mark.LocalX)), FromCenti(ToCenti(mark.LocalZ)), mark.Argb));
            return record;
        }

        /// <summary>Rehace la hoja: capas con su clave, tramos, flechas, marcas, y trazos y aristas desde los tramos.</summary>
        public MapSheet ToSheet(int cellsPerChunk)
        {
            // MapSheet.Id es de 32 bits: en P1 los ids del host caben de sobra (tope global de 2000 hojas).
            var sheet = new MapSheet(unchecked((int)Id), unchecked((int)Seed), Clean);
            if (HasZone) sheet.AssignZone(Zone);
            foreach (MapSheetRecordLayer layer in Layers)
            {
                MapSheetLayer target = sheet.AddLayer(layer.Key, layer.Argb, FromCenti(layer.WidthCentiPx));
                target.Runs.AddRange(layer.Runs);
            }

            sheet.Links.AddRange(Links);
            sheet.Marks.AddRange(Marks);
            MapSheetStrokeBuilder.Redraw(sheet, cellsPerChunk);
            return sheet;
        }

        private static int ToCenti(float value) => (int)Math.Round(value * 100.0, MidpointRounding.AwayFromZero);

        private static float FromCenti(int value) => value / 100f;

        // ── JSON ──────────────────────────────────────────────────────────────────────────────────────────────

        public string ToJson()
        {
            var json = new StringBuilder(256 + RunCount * 20);
            json.Append("{\"id\":").Append(Id.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"rev\":").Append(Rev.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"zone\":");
            if (HasZone) AppendZone(json, Zone);
            else json.Append("null");
            json.Append(",\"seed\":").Append(Seed.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"clean\":").Append(Clean ? "true" : "false");
            json.Append(",\"label\":");
            AppendString(json, Label ?? "");

            json.Append(",\"layers\":[");
            for (int l = 0; l < Layers.Count; l++)
            {
                MapSheetRecordLayer layer = Layers[l];
                if (l > 0) json.Append(',');
                json.Append("{\"layer_key\":").Append(Int(layer.Key));
                json.Append(",\"pen_argb\":").Append(layer.Argb.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"width_cpx\":").Append(Int(layer.WidthCentiPx));
                json.Append(",\"runs\":[");
                for (int r = 0; r < layer.Runs.Count; r++)
                {
                    MapRun run = layer.Runs[r];
                    if (r > 0) json.Append(',');
                    json.Append('[').Append(run.Vertical ? '1' : '0')
                        .Append(',').Append(Int(run.Line))
                        .Append(',').Append(Int(run.From))
                        .Append(',').Append(Int(run.To))
                        .Append(',').Append(run.Old ? '1' : '0')
                        .Append(',').Append(Int(run.DrawIndex)).Append(']');
                }

                json.Append("]}");
            }

            json.Append("],\"links\":[");
            for (int i = 0; i < Links.Count; i++)
            {
                MapLink link = Links[i];
                if (i > 0) json.Append(',');
                json.Append("{\"to\":");
                AppendZone(json, link.To);
                json.Append(",\"side\":").Append(Int((int)link.Side));
                json.Append(",\"x_cc\":").Append(Int(ToCenti(link.LocalX)));
                json.Append(",\"z_cc\":").Append(Int(ToCenti(link.LocalZ))).Append('}');
            }

            json.Append("],\"marks\":[");
            for (int i = 0; i < Marks.Count; i++)
            {
                MapMark mark = Marks[i];
                if (i > 0) json.Append(',');
                json.Append("{\"kind\":").Append(Int((int)mark.Kind));
                json.Append(",\"x_cc\":").Append(Int(ToCenti(mark.LocalX)));
                json.Append(",\"z_cc\":").Append(Int(ToCenti(mark.LocalZ)));
                json.Append(",\"argb\":").Append(mark.Argb.ToString(CultureInfo.InvariantCulture)).Append('}');
            }

            json.Append("]}");
            return json.ToString();
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static void AppendZone(StringBuilder json, MapZone zone) =>
            json.Append("{\"chunk_x\":").Append(Int(zone.ChunkX))
                .Append(",\"chunk_z\":").Append(Int(zone.ChunkZ))
                .Append(",\"storey\":").Append(Int(zone.Storey)).Append('}');

        /// <summary>Como <c>serde_json</c>: escapa comillas, barra y controles; el resto va tal cual en UTF-8.</summary>
        private static void AppendString(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    case '\b': json.Append("\\b"); break;
                    case '\f': json.Append("\\f"); break;
                    default:
                        if (c < 0x20) json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else json.Append(c);
                        break;
                }
            }

            json.Append('"');
        }

        /// <summary>Lee un registro. Lanza <see cref="FormatException"/> si el JSON no tiene la forma del ADR.</summary>
        public static MapSheetRecord FromJson(string text)
        {
            var reader = new JsonReader(text);
            object root = reader.ReadDocument();
            if (!(root is Dictionary<string, object> obj)) throw new FormatException("el registro no es un objeto");

            var record = new MapSheetRecord
            {
                Id = checked((ulong)GetLong(obj, "id")),
                Rev = checked((uint)GetLong(obj, "rev")),
                Seed = checked((uint)GetLong(obj, "seed")),
                Clean = GetBool(obj, "clean"),
                Label = GetString(obj, "label"),
            };

            object zone = Get(obj, "zone");
            if (zone != null)
            {
                record.HasZone = true;
                record.Zone = ReadZone(zone);
            }

            foreach (object item in GetArray(obj, "layers"))
            {
                Dictionary<string, object> layerObj = AsObject(item, "capa");
                var layer = new MapSheetRecordLayer
                {
                    Key = checked((int)GetLong(layerObj, "layer_key")),
                    Argb = checked((uint)GetLong(layerObj, "pen_argb")),
                    WidthCentiPx = checked((int)GetLong(layerObj, "width_cpx")),
                };
                foreach (object runItem in GetArray(layerObj, "runs"))
                {
                    if (!(runItem is List<object> values) || values.Count != 6)
                        throw new FormatException("un tramo son 6 enteros");
                    layer.Runs.Add(new MapRun(ToLong(values[0]) != 0, checked((int)ToLong(values[1])),
                        checked((int)ToLong(values[2])), checked((int)ToLong(values[3])), ToLong(values[4]) != 0,
                        checked((int)ToLong(values[5]))));
                }

                record.Layers.Add(layer);
            }

            foreach (object item in GetArray(obj, "links"))
            {
                Dictionary<string, object> linkObj = AsObject(item, "enlace");
                record.Links.Add(new MapLink(ReadZone(Get(linkObj, "to")), (MapLinkSide)checked((byte)GetLong(linkObj, "side")),
                    FromCenti(checked((int)GetLong(linkObj, "x_cc"))), FromCenti(checked((int)GetLong(linkObj, "z_cc")))));
            }

            foreach (object item in GetArray(obj, "marks"))
            {
                Dictionary<string, object> markObj = AsObject(item, "marca");
                record.Marks.Add(new MapMark((MapMarkKind)checked((byte)GetLong(markObj, "kind")),
                    FromCenti(checked((int)GetLong(markObj, "x_cc"))), FromCenti(checked((int)GetLong(markObj, "z_cc"))),
                    checked((uint)GetLong(markObj, "argb"))));
            }

            return record;
        }

        private static MapZone ReadZone(object value)
        {
            Dictionary<string, object> zone = AsObject(value, "zona");
            return new MapZone(checked((int)GetLong(zone, "chunk_x")), checked((int)GetLong(zone, "chunk_z")),
                checked((int)GetLong(zone, "storey")));
        }

        private static Dictionary<string, object> AsObject(object value, string what) =>
            value as Dictionary<string, object> ?? throw new FormatException($"{what}: se esperaba un objeto");

        private static object Get(Dictionary<string, object> obj, string key) =>
            obj.TryGetValue(key, out object value) ? value : throw new FormatException($"falta \"{key}\"");

        private static long GetLong(Dictionary<string, object> obj, string key) => ToLong(Get(obj, key));

        private static long ToLong(object value) =>
            value is long number ? number : throw new FormatException("se esperaba un entero");

        private static bool GetBool(Dictionary<string, object> obj, string key) =>
            Get(obj, key) is bool flag ? flag : throw new FormatException($"\"{key}\" no es booleano");

        private static string GetString(Dictionary<string, object> obj, string key) =>
            Get(obj, key) as string ?? throw new FormatException($"\"{key}\" no es texto");

        private static List<object> GetArray(Dictionary<string, object> obj, string key) =>
            Get(obj, key) as List<object> ?? throw new FormatException($"\"{key}\" no es una lista");

        /// <summary>Lector JSON mínimo: objetos, listas, enteros, booleanos, null y texto. Sin decimales: el registro no los usa.</summary>
        private sealed class JsonReader
        {
            private readonly string _text;
            private int _pos;

            public JsonReader(string text)
            {
                _text = text ?? throw new FormatException("texto nulo");
            }

            public object ReadDocument()
            {
                object value = ReadValue();
                SkipWhitespace();
                if (_pos != _text.Length) throw new FormatException($"sobra texto en {_pos}");
                return value;
            }

            private object ReadValue()
            {
                SkipWhitespace();
                if (_pos >= _text.Length) throw new FormatException("fin inesperado");
                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadInteger();
                        throw new FormatException($"carácter inesperado '{c}' en {_pos}");
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                var obj = new Dictionary<string, object>();
                _pos++;
                SkipWhitespace();
                if (Peek() == '}') { _pos++; return obj; }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw new FormatException($"se esperaba una clave en {_pos}");
                    string key = ReadString();
                    SkipWhitespace();
                    if (Next() != ':') throw new FormatException($"se esperaba ':' en {_pos}");
                    obj[key] = ReadValue();
                    SkipWhitespace();
                    char c = Next();
                    if (c == '}') return obj;
                    if (c != ',') throw new FormatException($"se esperaba ',' o '}}' en {_pos}");
                }
            }

            private List<object> ReadArray()
            {
                var list = new List<object>();
                _pos++;
                SkipWhitespace();
                if (Peek() == ']') { _pos++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWhitespace();
                    char c = Next();
                    if (c == ']') return list;
                    if (c != ',') throw new FormatException($"se esperaba ',' o ']' en {_pos}");
                }
            }

            private string ReadString()
            {
                var value = new StringBuilder();
                _pos++;
                while (true)
                {
                    char c = Next();
                    if (c == '"') return value.ToString();
                    if (c != '\\') { value.Append(c); continue; }
                    char escape = Next();
                    switch (escape)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length) throw new FormatException("\\u incompleto");
                            value.Append((char)int.Parse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _pos += 4;
                            break;
                        default: throw new FormatException($"escape \\{escape} desconocido");
                    }
                }
            }

            private long ReadInteger()
            {
                int start = _pos;
                if (Peek() == '-') _pos++;
                while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') _pos++;
                if (_pos < _text.Length && (_text[_pos] == '.' || _text[_pos] == 'e' || _text[_pos] == 'E'))
                    throw new FormatException($"el registro solo lleva enteros (posición {start})");
                if (!long.TryParse(_text.Substring(start, _pos - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                    throw new FormatException($"entero no válido en {start}");
                return value;
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw new FormatException($"se esperaba {word} en {_pos}");
                _pos += word.Length;
            }

            private char Peek() => _pos < _text.Length ? _text[_pos] : throw new FormatException("fin inesperado");

            private char Next() => _pos < _text.Length ? _text[_pos++] : throw new FormatException("fin inesperado");

            private void SkipWhitespace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
            }
        }
    }
}
