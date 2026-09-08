using UnityEngine;

namespace BackroomsSurvival.Gameplay.Medical
{
    /// <summary>
    /// La venda que se ve: una banda de tela alrededor del antebrazo. Un solo constructor para las
    /// dos representaciones —los brazos de primera persona y el avatar remoto que ven los demás—
    /// porque si fueran dos, el día que cambie el grosor o el color cambiaría en uno solo.
    ///
    /// SE CONSTRUYE, NO SE IMPORTA, y es una decisión con motivo: un prefab que apunte a un import
    /// deja el objeto INVISIBLE en cualquier máquina que no tenga la carpeta de imports (que está
    /// en .gitignore). Una banda es un cilindro; mejor un cilindro que existe siempre que un asset
    /// que a veces no está. Si algún día hay arte de verdad, se deja el prefab en
    /// <c>Resources/BR_Bandage_Arm</c> y esto lo usa sin tocar una línea.
    ///
    /// EL EJE SALE DEL HUESO, no de una constante: la banda se orienta hacia el hijo del antebrazo
    /// (la mano), así que sirve igual para el esqueleto de los brazos del vendor
    /// (<c>Forearm.L</c> → <c>Hand.L</c>) que para el del cuerpo en tercera persona
    /// (<c>LowerArm.L</c> → <c>Hand.L</c>), que no comparten ni nombres ni ejes locales.
    /// </summary>
    public static class BandageVisual
    {
        /// <summary>Prefab opcional de arte. Si no existe, se construye la banda.</summary>
        public const string OptionalPrefabResourcePath = "BR_Bandage_Arm";

        /// <summary>
        /// Material de la banda, en Resources. Existir como ASSET no es un lujo: un material creado
        /// en memoria se PIERDE al guardar un prefab que lo referencie, así que el rollo de gasa que
        /// hornea el autor del item saldría con el rosa de "material perdido". El de runtime es sólo
        /// la red de seguridad para cuando el asset todavía no se ha creado.
        /// </summary>
        public const string MaterialResourcePath = "BR_Bandage_Material";

        /// <summary>Color de la gasa, en un sitio: lo usan el material de runtime y el que crea el
        /// autor del item, y si divergen la venda de la mano y la del brazo no serían la misma tela.</summary>
        public static readonly Color ClothColor = new Color(0.86f, 0.83f, 0.75f);

        /// <summary>Nada de brillo: es tela.</summary>
        public const float ClothSmoothness = 0.08f;

        /// <summary>
        /// Radio de la banda en los brazos de PRIMERA persona, en metros. Un pelo por encima del
        /// antebrazo para no coserse dentro de la piel — un z-fighting en un objeto que llevas en
        /// la cara se ve fatal.
        /// </summary>
        public const float DefaultRadius = 0.058f;

        /// <summary>
        /// El mismo radio para el avatar remoto, y es MENOR porque los dos brazos no miden lo mismo:
        /// los de primera persona son el viewmodel, deliberadamente gruesos para que se vean bien en
        /// pantalla, y el cuerpo en tercera persona es un humano a escala. Con el radio de 1P la
        /// venda sobresalía del antebrazo y parecía una escayola.
        ///
        /// MEDIDO SOBRE LA MALLA, y las dos veces que se estimó a ojo salió mal en direcciones
        /// opuestas: 0,058 dejaba una escayola y 0,030 se hundía dentro del brazo. Los vértices que
        /// mandan `LowerArm.*` en la franja donde la venda se pone dan mediana 0,0300 m y percentil
        /// 90 0,0431 m — por eso 0,030 desaparecía: era exactamente la mediana, o sea la mitad de
        /// la piel por fuera. 0,045 cubre el p90 con un margen de holgura.
        /// </summary>
        public const float ProxyRadius = 0.045f;

        /// <summary>Alto de la banda a lo largo del hueso, en metros: unas cuantas vueltas.</summary>
        public const float DefaultLength = 0.11f;

        /// <summary>Fracción del hueso, desde el codo hacia la mano, donde se centra la banda.</summary>
        public const float DefaultAlongBone = 0.55f;

        private static Mesh _bandMesh;
        private static Material _bandMaterial;
        private static GameObject _prefab;
        private static bool _prefabLoaded;

        /// <summary>
        /// Cuelga una venda del antebrazo dado y devuelve el objeto creado (nunca <c>null</c> salvo
        /// que el hueso lo sea). Nace APAGADO: quien lo pide decide cuándo se ve, para que un
        /// cambio de estado sea un <c>SetActive</c> y no un Instantiate a mitad de partida.
        /// </summary>
        public static GameObject Attach(Transform forearm, float radius = DefaultRadius,
                                        float length = DefaultLength, float alongBone = DefaultAlongBone)
        {
            if (forearm == null)
                return null;

            var go = InstantiateBody();
            go.name = "BR_Bandage";
            go.layer = forearm.gameObject.layer;
            go.transform.SetParent(forearm, false);

            Place(go.transform, forearm, radius, length, alongBone);
            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// Coloca la banda sobre el hueso. Separado de <see cref="Attach"/> porque los brazos de
        /// primera persona se reconstruyen con cada arma y hay que recolocar sin volver a crear.
        /// </summary>
        public static void Place(Transform bandage, Transform forearm, float radius, float length, float alongBone)
        {
            if (bandage == null || forearm == null)
                return;

            // Hacia dónde va el hueso: a su hijo. Sin hijo (una punta de cadena) se cae al eje Y
            // local, que es lo que usan la mayoría de los rigs para el largo del hueso.
            var direction = Vector3.up;
            if (forearm.childCount > 0)
            {
                var child = forearm.GetChild(0);
                var local = forearm.InverseTransformPoint(child.position);
                if (local.sqrMagnitude > 1e-6f)
                    direction = local.normalized;
            }

            // La escala del hueso NO es 1 en todos los rigs; el tamaño se pide en METROS DE MUNDO,
            // así que se divide por la escala acumulada o una venda saldría del tamaño del brazo.
            var lossy = forearm.lossyScale;
            float scale = Mathf.Max(0.0001f, (Mathf.Abs(lossy.x) + Mathf.Abs(lossy.y) + Mathf.Abs(lossy.z)) / 3f);

            float boneLength = ForearmLength(forearm);
            bandage.localPosition = direction * (boneLength * alongBone);
            bandage.localRotation = Quaternion.FromToRotation(Vector3.up, direction);
            // El cilindro primitivo mide 2 de alto y 1 de diámetro: de ahí el medio y el doble.
            bandage.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f) / scale;
        }

        /// <summary>Largo del hueso en su propio espacio local (0 si no tiene hijo del que medirlo).</summary>
        private static float ForearmLength(Transform forearm)
        {
            if (forearm.childCount == 0)
                return 0.25f;

            return forearm.InverseTransformPoint(forearm.GetChild(0).position).magnitude;
        }

        private static GameObject InstantiateBody()
        {
            var prefab = LoadOptionalPrefab();
            if (prefab != null)
                return Object.Instantiate(prefab);

            var go = new GameObject("BR_Bandage");
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BandMesh();

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = BandMaterial();
            // Una venda no proyecta sombra útil y en primera persona la suya cae sobre la cámara.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            return go;
        }

        private static GameObject LoadOptionalPrefab()
        {
            if (_prefabLoaded)
                return _prefab;

            _prefabLoaded = true;
            _prefab = Resources.Load<GameObject>(OptionalPrefabResourcePath);
            return _prefab;
        }

        /// <summary>
        /// La malla del cilindro primitivo de Unity, tomada prestada UNA vez. Se crea un primitivo
        /// y se destruye acto seguido: la malla es un recurso compartido del motor y sobrevive al
        /// objeto, así que esto no deja basura y ahorra escribir un generador de cilindros.
        /// </summary>
        private static Mesh BandMesh()
        {
            if (_bandMesh != null)
                return _bandMesh;

            var temp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _bandMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            // `Destroy` es ilegal fuera de Play y el autor del item corre en el EDITOR: sin este
            // reparto, hornear el prefab de la venda lanzaría "Destroy may not be called from edit
            // mode" y dejaría un cilindro suelto en la escena.
            if (Application.isPlaying)
                Object.Destroy(temp);
            else
                Object.DestroyImmediate(temp);
            return _bandMesh;
        }

        /// <summary>
        /// Material URP. Con un shader de Built-in saldría MAGENTA desde ADR-065, y sin material
        /// saldría el rosa de "shader perdido" — que es peor, porque parece un fallo de la venda y
        /// no del render. Gasa sucia: blanco roto, mate, nada de brillo.
        /// </summary>
        private static Material BandMaterial()
        {
            if (_bandMaterial != null)
                return _bandMaterial;

            // Primero el asset: es el único que sobrevive a guardar un prefab que lo use.
            _bandMaterial = Resources.Load<Material>(MaterialResourcePath);
            if (_bandMaterial != null)
                return _bandMaterial;

            _bandMaterial = Build();
            return _bandMaterial;
        }

        /// <summary>
        /// Construye el material de la gasa. Público porque el autor del item lo usa para GUARDARLO
        /// como asset: una sola receta, y así el material de Resources y el de emergencia son el
        /// mismo material y no dos telas distintas.
        /// </summary>
        public static Material Build()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                return null;

            var material = new Material(shader) { name = "BR_Bandage" };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", ClothColor);
            material.color = ClothColor;
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", ClothSmoothness);
            return material;
        }
    }
}
