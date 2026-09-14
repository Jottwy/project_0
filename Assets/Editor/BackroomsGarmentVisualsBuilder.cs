using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Wearables;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-149 R4a/R4b: la ropa del muñeco del inventario.
    /// R4a: nuestras prendas se ven. No hay mallas propias todavía: cada prenda reusa la malla de un donante del vendor (la
    /// camisa para la chaqueta, el pantalón militar, las botas) duplicada con un material propio teñido, y se añade a la lista
    /// de ropa del <see cref="CharacterClothing"/> del muñeco con la máscara de piel de su donante. La chaqueta, que va en
    /// <c>Outer</c> (hueco que el vendor no mira), la pone <see cref="BackroomsOuterClothing"/>.
    /// R4b: TODA la ropa del muñeco (la del vendor y la nuestra) pasa al shader <c>Backrooms/Garment Lit</c> con una copia
    /// de su malla que lleva la zona del cuerpo de cada vértice en uv3, y <see cref="BackroomsGarmentDamageVisuals"/> le
    /// pasa la rotura. Idempotente: se relanza con la variante. No toca assets del vendor: copias y overrides en la variante.
    /// </summary>
    public static class BackroomsGarmentVisualsBuilder
    {
        public const string MaterialFolder = "Assets/Art/Garments/Materials";
        public const string MeshFolder = "Assets/Art/Garments/Meshes";
        public const string ShaderName = "Backrooms/Garment Lit";
        public const string SkinComposeShaderName = "Hidden/Backrooms/Skin Mask Compose";
        public const string BodyZoneMapPath = "Assets/Art/Garments/BR_BodyZoneMap.asset";

        /// <summary>Metros que se hincha la prenda de encima para no pisar la de debajo.</summary>
        public const float OuterInflate = 0.004f;

        /// <summary>
        /// Metros que se adelanta hacia la cámara la prenda de encima al dibujarse: donde la de dentro la atraviesa (la
        /// chaqueta reusa una malla más ceñida que la camiseta) gana la de encima, sin engordar la silueta.
        /// </summary>
        public const float OuterViewBias = 0.025f;

        public readonly struct Look
        {
            public readonly string Garment, Donor;
            public readonly BodyPoint Point;
            public readonly Color Tint;
            public readonly bool Outer;

            public Look(string garment, string donor, BodyPoint point, Color tint, bool outer = false)
            {
                Garment = garment; Donor = donor; Point = point; Tint = tint; Outer = outer;
            }

            public string RendererName => Garment.Replace(" ", string.Empty);
            public string MaterialPath => $"{MaterialFolder}/{RendererName}.mat";
        }

        public static readonly Look[] Looks =
        {
            new Look("BR_Work Jacket", "Shirt", BodyPoint.Torso, new Color(0.46f, 0.38f, 0.26f), outer: true),
            new Look("BR_Work Trousers", "CargoPants", BodyPoint.Legs, new Color(0.42f, 0.45f, 0.52f)),
            new Look("BR_Work Boots", "Boots", BodyPoint.Feet, new Color(0.55f, 0.45f, 0.35f)),
            new Look("BR_Running Shoes", "Boots", BodyPoint.Feet, new Color(0.85f, 0.87f, 0.9f)),
        };

        /// <summary>Viste el muñeco del inventario de la variante abierta. Devuelve cuántas prendas nuestras quedaron cableadas.</summary>
        public static int WirePreview(GameObject root)
        {
            var preview = root.GetComponentInChildren<CharacterPreviewUI>(true);
            var clothing = preview != null ? preview.GetComponentInChildren<CharacterClothing>(true) : null;
            if (clothing == null) clothing = root.GetComponentInChildren<CharacterClothing>(true);
            if (clothing == null)
            {
                Debug.LogWarning("[GarmentVisuals] la variante no trae CharacterClothing en el muñeco");
                return 0;
            }

            var outers = new List<(SkinnedMeshRenderer Renderer, int Id, Texture2D Mask)>();
            int wired = WireLooks(clothing, outers);

            var outer = clothing.GetComponent<BackroomsOuterClothing>();
            if (outer == null) outer = clothing.gameObject.AddComponent<BackroomsOuterClothing>();
            var os = new SerializedObject(outer);
            os.FindProperty("_outerContainer").stringValue = BackroomsBackpackPrototypeCreator.OuterContainer;
            var outerRenderers = os.FindProperty("_outerRenderers");
            outerRenderers.arraySize = outers.Count;
            for (int i = 0; i < outers.Count; i++) outerRenderers.GetArrayElementAtIndex(i).objectReferenceValue = outers[i].Renderer;
            SetInts(os.FindProperty("_outerIds"), outers.ConvertAll(o => o.Id));
            os.ApplyModifiedPropertiesWithoutUndo();

            WireDamage(clothing, new SerializedObject(clothing).FindProperty("_clothing"), outers);
            // R4c: las mangas de primera persona usan los materiales de arriba.
            BackroomsSleeveBuilder.Build();
            return wired;
        }

        /// <summary>
        /// Avatar remoto (ADR-149 R4a, sin wire): pantalón y calzado de trabajo, que ya viajan en <c>equipment</c>, entran en el
        /// guardarropa del proxy. Sin la prenda de encima ni la rotura (piden wire: borrador de ADR-149 enm. 7) y sin componentes
        /// de UI, que en el proxy no tienen inventario.
        /// </summary>
        public static int WireRemoteAvatar(CharacterClothing clothing) => clothing != null ? WireLooks(clothing, null) : 0;

        /// <summary>
        /// Nuestras prendas en la lista de ropa de un <see cref="CharacterClothing"/>, con malla de donante y material teñido.
        /// Con <paramref name="outers"/> la de encima se prepara aparte (va sobre el torso); sin él, se omite.
        /// </summary>
        private static int WireLooks(CharacterClothing clothing, List<(SkinnedMeshRenderer Renderer, int Id, Texture2D Mask)> outers)
        {
            var so = new SerializedObject(clothing);
            var lists = so.FindProperty("_clothing");
            int wired = 0;
            foreach (var look in Looks)
            {
                if (look.Outer && outers == null) continue;
                var definition = LoadGarment(look.Garment);
                if (definition == null) continue;
                var items = lists.GetArrayElementAtIndex((int)look.Point).FindPropertyRelative("Items");
                if (!TryFindEntry(items, look.Donor, out var donorEntry))
                {
                    Debug.LogWarning($"[GarmentVisuals] sin donante '{look.Donor}' en {look.Point}");
                    continue;
                }
                var donor = (SkinnedMeshRenderer)donorEntry.FindPropertyRelative("Renderer").objectReferenceValue;
                var renderer = EnsureRenderer(donor, look);
                var mask = donorEntry.FindPropertyRelative("OpacityMask").objectReferenceValue as Texture2D;
                if (look.Outer)
                {
                    // R4b tanda 2: la de encima se dibuja ADEMÁS de lo del torso. Fuera de la lista del vendor, que pinta una sola.
                    int stale = FindEntryIndex(items, look.RendererName);
                    if (stale >= 0) items.DeleteArrayElementAtIndex(stale);
                    outers.Add((renderer, definition.Id, mask));
                    wired++;
                    continue;
                }
                if (!TryFindEntry(items, look.RendererName, out var entry))
                {
                    items.InsertArrayElementAtIndex(items.arraySize);
                    entry = items.GetArrayElementAtIndex(items.arraySize - 1);
                }
                entry.FindPropertyRelative("Item").FindPropertyRelative("_value").intValue = definition.Id;
                entry.FindPropertyRelative("Renderer").objectReferenceValue = renderer;
                entry.FindPropertyRelative("OpacityMask").objectReferenceValue = mask;
                wired++;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.RecordPrefabInstancePropertyModifications(clothing);
            return wired;
        }

        // ── R4b: shader de rotura y zona por vértice en toda la ropa del muñeco ──

        private static void WireDamage(CharacterClothing clothing, SerializedProperty lists,
            List<(SkinnedMeshRenderer Renderer, int Id, Texture2D Mask)> outers)
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null) Debug.LogError($"[GarmentVisuals] no existe el shader {ShaderName}: la rotura no se verá");

            var renderers = new List<SkinnedMeshRenderer>();
            var ids = new List<int>();
            var points = new List<int>();
            var masks = new List<Texture2D>();
            var baked = new Dictionary<Mesh, string>();
            for (int p = 0; p < lists.arraySize; p++)
            {
                var items = lists.GetArrayElementAtIndex(p).FindPropertyRelative("Items");
                for (int i = 0; i < items.arraySize; i++)
                {
                    var entry = items.GetArrayElementAtIndex(i);
                    if (!(entry.FindPropertyRelative("Renderer").objectReferenceValue is SkinnedMeshRenderer renderer)) continue;
                    if (renderers.Contains(renderer)) continue;
                    string meshPath = EnsureZoneMesh(renderer, baked);
                    if (meshPath != null) renderer.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    if (shader != null) renderer.sharedMaterial = EnsureGarmentMaterial(renderer.sharedMaterial, renderer.name, shader);
                    renderers.Add(renderer);
                    ids.Add(entry.FindPropertyRelative("Item").FindPropertyRelative("_value").intValue);
                    points.Add(p);
                    masks.Add(entry.FindPropertyRelative("OpacityMask").objectReferenceValue as Texture2D);
                }
            }
            foreach (var outer in outers)
            {
                var renderer = outer.Renderer;
                if (renderer == null || renderers.Contains(renderer)) continue;
                string meshPath = EnsureZoneMesh(renderer, baked);
                if (meshPath != null) renderer.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (shader != null) renderer.sharedMaterial = EnsureGarmentMaterial(renderer.sharedMaterial, renderer.name, shader);
                if (renderer.sharedMaterial != null)
                {
                    renderer.sharedMaterial.SetFloat("_GarmentInflate", OuterInflate);
                    renderer.sharedMaterial.SetFloat("_GarmentViewBias", OuterViewBias);
                    EditorUtility.SetDirty(renderer.sharedMaterial);
                    // Guardar ya: el material se había quedado sin la propiedad en disco aunque el valor se pusiera.
                    AssetDatabase.SaveAssetIfDirty(renderer.sharedMaterial);
                }
                renderers.Add(renderer);
                ids.Add(outer.Id);
                points.Add(BackroomsGarmentDamageVisuals.OuterPoint);
                masks.Add(outer.Mask);
            }

            // Trampa conocida: la malla ya cargada se sigue dibujando con búferes viejos si no se reimporta.
            AssetDatabase.SaveAssets();
            foreach (var path in baked.Values) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            foreach (var renderer in renderers)
            {
                if (renderer.sharedMesh == null) continue;
                string path = AssetDatabase.GetAssetPath(renderer.sharedMesh);
                if (path.StartsWith(MeshFolder)) renderer.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }

            var visuals = clothing.GetComponent<BackroomsGarmentDamageVisuals>();
            if (visuals == null) visuals = clothing.gameObject.AddComponent<BackroomsGarmentDamageVisuals>();
            var vs = new SerializedObject(visuals);
            var rs = vs.FindProperty("_renderers");
            rs.arraySize = renderers.Count;
            for (int i = 0; i < renderers.Count; i++) rs.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
            SetInts(vs.FindProperty("_itemIds"), ids);
            SetInts(vs.FindProperty("_points"), points);
            var maskArray = vs.FindProperty("_masks");
            maskArray.arraySize = masks.Count;
            for (int i = 0; i < masks.Count; i++) maskArray.GetArrayElementAtIndex(i).objectReferenceValue = masks[i];
            var body = new SerializedObject(clothing).FindProperty("_bodyRenderer").objectReferenceValue as SkinnedMeshRenderer;
            vs.FindProperty("_body").objectReferenceValue = body;
            vs.FindProperty("_zoneMap").objectReferenceValue = body != null ? EnsureBodyZoneMap(body) : null;
            var compose = Shader.Find(SkinComposeShaderName);
            if (compose == null) Debug.LogError($"[GarmentVisuals] no existe {SkinComposeShaderName}: la piel no se verá por los agujeros");
            vs.FindProperty("_composeShader").objectReferenceValue = compose;
            vs.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[GarmentVisuals] rotura cableada en {renderers.Count} prendas del muñeco, {baked.Count} mallas horneadas");
        }

        /// <summary>
        /// Copia de la malla con, en cada vértice, la zona del cuerpo en uv3.x (la del hueso con más peso, o la de su primer
        /// padre con zona) y en uv4 sus metros alrededor del eje de esa zona en la pose de reposo. Devuelve la ruta de la copia.
        /// Una copia ya horneada se vuelve a hornear sobre sí misma (conserva vértices y pesos): el horneado es determinista.
        /// </summary>
        private static string EnsureZoneMesh(SkinnedMeshRenderer renderer, Dictionary<Mesh, string> baked)
        {
            var source = renderer.sharedMesh;
            if (source == null) return null;
            if (baked.TryGetValue(source, out string done)) return done;
            string sourcePath = AssetDatabase.GetAssetPath(source);
            bool inPlace = sourcePath.StartsWith(MeshFolder);

            var weights = source.boneWeights;
            var bones = renderer.bones;
            if (weights.Length != source.vertexCount)
            {
                Debug.LogWarning($"[GarmentVisuals] {renderer.name}: la malla no trae pesos por vértice, se queda sin zonas");
                return null;
            }
            var zones = new List<Vector2>(weights.Length);
            var zoneOf = new BodyZone[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                int bone = GarmentVisualState.DominantBone(weights[i]);
                zoneOf[i] = bone >= 0 && bone < bones.Length ? GarmentVisualState.ZoneFromBone(bones[bone]) : BodyZone.Chest;
                zones.Add(new Vector2((int)zoneOf[i], 0f));
            }
            var coords = new Vector2[weights.Length];
            RestFrame(source, bones, out var up, out var forward, out var axes);
            var front = new float[weights.Length];
            GarmentVisualState.ProjectZones(source.vertices, zoneOf, up, forward, coords, front, axes);
            for (int i = 0; i < zones.Count; i++) zones[i] = new Vector2((int)zoneOf[i], front[i]);

            if (inPlace)
            {
                source.SetUVs(3, zones);
                source.SetUVs(4, coords);
                EditorUtility.SetDirty(source);
                baked[source] = sourcePath;
                return sourcePath;
            }

            EnsureFolder(MeshFolder);
            string path = $"{MeshFolder}/{source.name}_Zones.asset";
            var copy = Object.Instantiate(source);
            copy.name = $"{source.name}_Zones";
            copy.SetUVs(3, zones);
            copy.SetUVs(4, coords);
            MeshUtility.SetMeshCompression(copy, ModelImporterMeshCompression.Off);
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(copy, existing);
                Object.DestroyImmediate(copy);
                EditorUtility.SetDirty(existing);
            }
            else
            {
                AssetDatabase.CreateAsset(copy, path);
            }
            baked[source] = path;
            return path;
        }

        /// <summary>
        /// R4b tanda 2: mapa de zonas del cuerpo en el espacio UV de su malla (R8, texel = zona + 1; 0 fuera). Con él la máscara
        /// de piel se abre solo en la zona rota. Cada triángulo se rasteriza con la zona del vértice que más pesa en el texel, y
        /// se dilata dos texels para cubrir las costuras de UV.
        /// </summary>
        private static Texture2D EnsureBodyZoneMap(SkinnedMeshRenderer body)
        {
            var mesh = body.sharedMesh;
            if (mesh == null) return null;
            const int size = 512;
            var uv = mesh.uv;
            var tris = mesh.triangles;
            var weights = mesh.boneWeights;
            var bones = body.bones;
            if (uv.Length != mesh.vertexCount || weights.Length != mesh.vertexCount)
            {
                Debug.LogWarning("[GarmentVisuals] el cuerpo no trae UV o pesos por vértice: sin mapa de zonas");
                return null;
            }
            var zoneOf = new byte[mesh.vertexCount];
            for (int i = 0; i < zoneOf.Length; i++)
            {
                int bone = GarmentVisualState.DominantBone(weights[i]);
                var zone = bone >= 0 && bone < bones.Length ? GarmentVisualState.ZoneFromBone(bones[bone]) : BodyZone.Chest;
                zoneOf[i] = (byte)((int)zone + 1);
            }

            var pixels = new byte[size * size];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                Vector2 a = uv[i0] * size, b = uv[i1] * size, c = uv[i2] * size;
                float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                if (Mathf.Abs(area) < 1e-8f) continue;
                int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, size - 1);
                int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, size - 1);
                int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, size - 1);
                int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, size - 1);
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / area;
                    float w1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / area;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -0.01f || w1 < -0.01f || w2 < -0.01f) continue;
                    pixels[y * size + x] = w0 >= w1 && w0 >= w2 ? zoneOf[i0] : (w1 >= w2 ? zoneOf[i1] : zoneOf[i2]);
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                var copy = (byte[])pixels.Clone();
                for (int y = 1; y < size - 1; y++)
                for (int x = 1; x < size - 1; x++)
                {
                    int i = y * size + x;
                    if (copy[i] != 0) continue;
                    byte near = copy[i - 1] != 0 ? copy[i - 1] : copy[i + 1] != 0 ? copy[i + 1] : copy[i - size] != 0 ? copy[i - size] : copy[i + size];
                    if (near != 0) pixels[i] = near;
                }
            }

            EnsureFolder(System.IO.Path.GetDirectoryName(BodyZoneMapPath)?.Replace('\\', '/'));
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(BodyZoneMapPath);
            bool create = texture == null || texture.width != size || texture.format != TextureFormat.R8;
            if (create)
            {
                if (texture != null) AssetDatabase.DeleteAsset(BodyZoneMapPath);
                texture = new Texture2D(size, size, TextureFormat.R8, false, true) { name = "BR_BodyZoneMap" };
            }
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
            if (create) AssetDatabase.CreateAsset(texture, BodyZoneMapPath);
            EditorUtility.SetDirty(texture);
            return texture;
        }

        /// <summary>
        /// Arriba y al frente del personaje en el espacio de la malla, sacados del esqueleto en reposo: de la pelvis a la
        /// cabeza, y del pie a los dedos del pie. Sin esos huesos, los ejes del mundo.
        /// </summary>
        private static void RestFrame(Mesh mesh, Transform[] bones, out Vector3 up, out Vector3 forward, out Vector3[] axes)
        {
            up = Vector3.up;
            forward = Vector3.forward;
            axes = new Vector3[BodyZones.Count];
            var bindposes = mesh.bindposes;
            bool TryBone(string name, out Vector3 position)
            {
                for (int i = 0; i < bones.Length && i < bindposes.Length; i++)
                    if (bones[i] != null && bones[i].name == name)
                    {
                        position = bindposes[i].inverse.MultiplyPoint3x4(Vector3.zero);
                        return true;
                    }
                position = Vector3.zero;
                return false;
            }
            if (TryBone("Pelvis", out var pelvis) && TryBone("Head", out var head)) up = (head - pelvis).normalized;
            if (TryBone("Foot.L", out var foot) && TryBone("Toes.L", out var toes)) forward = toes - foot;
            else if (TryBone("Foot.R", out foot) && TryBone("Toes.R", out toes)) forward = toes - foot;

            // Eje de cada miembro: del hueso a su hijo, en reposo (apunta hacia fuera del cuerpo).
            var limbs = new (BodyZone Zone, string From, string To)[]
            {
                (BodyZone.UpperArmL, "UpperArm.L", "LowerArm.L"), (BodyZone.UpperArmR, "UpperArm.R", "LowerArm.R"),
                (BodyZone.ForearmL, "LowerArm.L", "Hand.L"), (BodyZone.ForearmR, "LowerArm.R", "Hand.R"),
                (BodyZone.ThighL, "UpperLeg.L", "LowerLeg.L"), (BodyZone.ThighR, "UpperLeg.R", "LowerLeg.R"),
                (BodyZone.ShinL, "LowerLeg.L", "Foot.L"), (BodyZone.ShinR, "LowerLeg.R", "Foot.R"),
            };
            foreach (var (zone, from, to) in limbs)
                if (TryBone(from, out var a) && TryBone(to, out var c))
                    axes[(int)zone] = c - a;
        }

        /// <summary>El material de la prenda con el shader de rotura. El del vendor se copia (Vendor_*), el nuestro se cambia.</summary>
        private static Material EnsureGarmentMaterial(Material current, string rendererName, Shader shader)
        {
            if (current == null) return null;
            string currentPath = AssetDatabase.GetAssetPath(current);
            Material material;
            if (currentPath.StartsWith(MaterialFolder))
            {
                material = current;
            }
            else
            {
                EnsureFolder(MaterialFolder);
                string path = $"{MaterialFolder}/Vendor_{rendererName}.mat";
                material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(current) { name = $"Vendor_{rendererName}" };
                    AssetDatabase.CreateAsset(material, path);
                }
                else
                {
                    material.CopyPropertiesFromMaterial(current);
                }
            }
            if (material.shader != shader) material.shader = shader;
            SetKeyword(material, "_NORMALMAP", material.GetTexture("_BumpMap") != null);
            SetKeyword(material, "_METALLICSPECGLOSSMAP", material.GetTexture("_MetallicGlossMap") != null);
            SetKeyword(material, "_OCCLUSIONMAP", material.GetTexture("_OcclusionMap") != null);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void SetKeyword(Material material, string keyword, bool on)
        {
            if (on) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        // ── R4a ──

        private static ItemDefinition LoadGarment(string asset)
        {
            foreach (var garment in BackroomsBackpackPrototypeCreator.Garments)
                if (garment.Asset == asset)
                    return AssetDatabase.LoadAssetAtPath<ItemDefinition>(garment.Path);
            Debug.LogWarning($"[GarmentVisuals] prenda desconocida {asset}");
            return null;
        }

        private static int FindEntryIndex(SerializedProperty items, string rendererName)
        {
            for (int i = 0; i < items.arraySize; i++)
            {
                var renderer = items.GetArrayElementAtIndex(i).FindPropertyRelative("Renderer").objectReferenceValue;
                if (renderer != null && renderer.name == rendererName) return i;
            }
            return -1;
        }

        private static bool TryFindEntry(SerializedProperty items, string rendererName, out SerializedProperty entry)
        {
            for (int i = 0; i < items.arraySize; i++)
            {
                entry = items.GetArrayElementAtIndex(i);
                var renderer = entry.FindPropertyRelative("Renderer").objectReferenceValue;
                if (renderer != null && renderer.name == rendererName) return true;
            }
            entry = null;
            return false;
        }

        /// <summary>Un duplicado apagado del donante, en el mismo esqueleto, con su material teñido.</summary>
        private static SkinnedMeshRenderer EnsureRenderer(SkinnedMeshRenderer donor, Look look)
        {
            var parent = donor.transform.parent;
            var existing = parent.Find(look.RendererName);
            SkinnedMeshRenderer renderer;
            if (existing != null && existing.TryGetComponent(out renderer))
            {
                // Mismo esqueleto, malla y límites que el donante, por si el vendor los cambió.
                renderer.sharedMesh = donor.sharedMesh;
                renderer.bones = donor.bones;
                renderer.rootBone = donor.rootBone;
                renderer.localBounds = donor.localBounds;
            }
            else
            {
                // Instantiate conserva las referencias a huesos de fuera de lo clonado: comparte el esqueleto.
                var go = Object.Instantiate(donor.gameObject, parent, false);
                go.name = look.RendererName;
                renderer = go.GetComponent<SkinnedMeshRenderer>();
            }
            renderer.transform.SetLocalPositionAndRotation(donor.transform.localPosition, donor.transform.localRotation);
            renderer.transform.localScale = donor.transform.localScale;
            renderer.sharedMaterial = EnsureMaterial(donor.sharedMaterial, look);
            renderer.gameObject.SetActive(false);
            return renderer;
        }

        private static Material EnsureMaterial(Material donor, Look look)
        {
            EnsureFolder(MaterialFolder);
            var material = AssetDatabase.LoadAssetAtPath<Material>(look.MaterialPath);
            if (material == null)
            {
                material = new Material(donor) { name = look.RendererName };
                AssetDatabase.CreateAsset(material, look.MaterialPath);
            }
            // Siempre el shader de rotura, venga de donde venga el donante: el material es compartido por el muñeco y el avatar
            // remoto, y el donante del proxy es un material del vendor (URP/Lit). Sin datos de zona la prenda sale sana.
            var garmentShader = Shader.Find(ShaderName);
            material.shader = garmentShader != null ? garmentShader : donor.shader;
            material.CopyPropertiesFromMaterial(donor);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", look.Tint);
            // La de encima se hincha; la copia del donante lo habría dejado a 0.
            material.SetFloat("_GarmentInflate", look.Outer ? OuterInflate : 0f);
            material.SetFloat("_GarmentViewBias", look.Outer ? OuterViewBias : 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void SetInts(SerializedProperty array, List<int> values)
        {
            array.arraySize = values.Count;
            for (int i = 0; i < values.Count; i++) array.GetArrayElementAtIndex(i).intValue = values[i];
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
