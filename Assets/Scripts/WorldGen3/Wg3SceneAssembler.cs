using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Los cuatro materiales, en el orden de submalla de
    /// <see cref="Wg3MeshBuilder.SubMesh"/>.</summary>
    [System.Serializable]
    public sealed class Wg3Materials
    {
        public Material floor;
        public Material structure;
        public Material ceiling;
        public Material decoration;

        public Material[] AsArray() => new[] { floor, structure, ceiling, decoration };
    }

    /// <summary>
    /// Monta un <see cref="Wg3World"/> en la escena.
    ///
    /// Un GameObject por pieza: una malla con cuatro submallas y los <c>BoxCollider</c> de sus
    /// volúmenes sólidos. La decoración NO recibe collider — es el mismo dato que en
    /// <see cref="Wg3Geometry"/> decidió que el rodapié se ve y no frena, aquí llevado a la escena
    /// sin una sola condición nueva.
    ///
    /// FUGAS: las mallas se crean en tiempo de ejecución y hay que destruirlas a mano. Borrar solo
    /// los GameObjects hijos deja las mallas vivas y la memoria sube en cada regeneración — es
    /// exactamente la fuga que se documentó en <c>VerticalShaftChunk</c>, donde `Clear()` destruía
    /// los hijos y nunca los recursos. Por eso <see cref="Clear"/> existe y por eso lleva la lista.
    /// </summary>
    public static class Wg3SceneAssembler
    {
        /// <summary>Tolerancia bajo la cual un giro se considera nulo y el volumen puede compartir
        /// GameObject. Un <c>BoxCollider</c> no puede girar por su cuenta: solo gira su
        /// transform, así que cada caja con yaw necesita su propio hijo.</summary>
        private const float YawEpsilon = 0.01f;

        public static void Assemble(Wg3World world, Transform parent, Wg3Materials materials,
            List<Mesh> createdMeshes, bool addLights = true)
        {
            if (world == null || parent == null) return;
            Material[] mats = materials != null ? materials.AsArray() : null;

            for (int i = 0; i < world.placements.Count; i++)
            {
                Wg3Placement placement = world.placements[i];
                List<Wg3Volume> volumes = Wg3Geometry.BuildPlaced(placement);
                var origin = new Vector3(placement.originX, 0f, placement.originZ);

                var go = new GameObject($"{i:D3}_{placement.piece.id}_r{placement.rotation}");

                // DontSave, y no es cosmético: la malla se crea en tiempo de ejecución y no es un
                // asset. Sin esta marca, guardar la escena serializa cientos de objetos con una
                // referencia a malla que al reabrir ya no existe — el fichero engorda y la escena
                // se abre llena de objetos vacíos que parecen geometría perdida.
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(parent, false);
                go.transform.position = origin;

                Mesh mesh = Wg3MeshBuilder.Build(volumes, origin);
                mesh.name = $"wg3_{placement.piece.id}_{i}";
                mesh.hideFlags = HideFlags.DontSave;
                createdMeshes?.Add(mesh);

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                if (mats != null) renderer.sharedMaterials = mats;

                AddColliders(go, volumes, origin);

                if (addLights) AddCeilingLight(go, placement);
            }
        }

        private static void AddColliders(GameObject root, List<Wg3Volume> volumes, Vector3 origin)
        {
            for (int v = 0; v < volumes.Count; v++)
            {
                Wg3Volume vol = volumes[v];
                if (!vol.IsSolid) continue;

                float yaw = Mathf.Repeat(vol.yawDegrees, 90f);
                bool axisAligned = yaw < YawEpsilon || yaw > 90f - YawEpsilon;

                if (axisAligned)
                {
                    // Sin giro propio: todas las cajas caben como componentes del mismo objeto, lo
                    // que ahorra un GameObject por pared en un mundo que tiene cientos.
                    var box = root.AddComponent<BoxCollider>();
                    bool swapped = Mathf.Repeat(vol.yawDegrees, 180f) > 45f;
                    box.center = vol.center - origin;
                    box.size = swapped ? new Vector3(vol.size.z, vol.size.y, vol.size.x) : vol.size;
                }
                else
                {
                    var child = new GameObject($"col_{vol.kind}_{v}");
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = vol.center - origin;
                    child.transform.localRotation = Quaternion.Euler(0f, vol.yawDegrees, 0f);
                    child.AddComponent<BoxCollider>().size = vol.size;
                }
            }
        }

        /// <summary>
        /// Un plafón por pieza, en el centro del techo.
        ///
        /// PROVISIONAL Y ANOTADO COMO TAL: la REGLA R32 dice que la posición de la luz es
        /// ESTRUCTURA y va autorada en la pieza —en las referencias, el ritmo de los plafones es
        /// lo único que te dice cuánto falta para el final de un pasillo—. Uno al centro no da
        /// ese ritmo; da que se vea algo. Cuando la pieza declare sus luces, esto se borra.
        /// </summary>
        private static void AddCeilingLight(GameObject root, Wg3Placement placement)
        {
            var go = new GameObject("light");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(
                placement.SizeX * 0.5f, placement.piece.heightMeters - 0.25f, placement.SizeZ * 0.5f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.97f, 0.88f);
            light.intensity = 1.6f;
            light.range = Mathf.Max(placement.SizeX, placement.SizeZ) * 0.75f + 6f;
            light.shadows = LightShadows.None;
        }

        /// <summary>Borra la escena montada Y las mallas que creó. Lo segundo es lo que se olvida.</summary>
        public static void Clear(Transform parent, List<Mesh> createdMeshes)
        {
            if (parent != null)
            {
                for (int i = parent.childCount - 1; i >= 0; i--)
                {
                    GameObject child = parent.GetChild(i).gameObject;
                    if (Application.isPlaying) Object.Destroy(child);
                    else Object.DestroyImmediate(child);
                }
            }

            if (createdMeshes == null) return;
            for (int i = 0; i < createdMeshes.Count; i++)
            {
                if (createdMeshes[i] == null) continue;
                if (Application.isPlaying) Object.Destroy(createdMeshes[i]);
                else Object.DestroyImmediate(createdMeshes[i]);
            }
            createdMeshes.Clear();
        }
    }
}
