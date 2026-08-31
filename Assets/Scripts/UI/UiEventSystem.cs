using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// EL ÚNICO SITIO QUE CREA UN EventSystem.
    ///
    /// # Qué estaba mal, y por qué el guard no lo cazaba
    ///
    /// Había TRES `EnsureEventSystem` —<c>JoinSessionUI</c>, <c>BackroomsGraphicsSettings</c> y
    /// <c>VoiceSettingsUI</c>— y los tres guardaban con <c>FindFirstObjectByType&lt;EventSystem&gt;()</c>,
    /// que **excluye los objetos inactivos**. Basta con que el EventSystem horneado de la escena
    /// esté en un objeto apagado en el instante en que corre cualquiera de ellos para que el guard
    /// conteste «no hay ninguno» y monte un segundo.
    ///
    /// El síntoma es «There are 2 event systems in the scene», que uGUI reemite **por frame y con
    /// stack trace** desde <c>EventSystem.Update</c>. Medido en el Profiler, el diagnóstico que se
    /// escribió para perseguirlo costaba 1,09 ms por frame él solo, barriendo la escena entera.
    ///
    /// Aquí se busca INCLUYENDO inactivos, que es la única forma de que el guard responda a la
    /// pregunta que se le hace de verdad —«¿existe ya uno?»— y no a «¿hay uno encendido ahora
    /// mismo?», que es otra pregunta y con otra respuesta.
    /// </summary>
    public static class UiEventSystem
    {
        /// <summary>
        /// Deja la escena con exactamente UN EventSystem, con el módulo de entrada que corresponde
        /// al backend de input compilado.
        ///
        /// Idempotente: llamarlo desde cinco pantallas distintas en el mismo frame sigue dejando uno.
        /// </summary>
        public static void Ensure()
        {
            EventSystem existing = EventSystem.current;
            if (existing == null)
                existing = UnityEngine.Object.FindAnyObjectByType<EventSystem>(
                    FindObjectsInactive.Include);

            if (existing != null)
            {
                EnsureInputModule(existing.gameObject);
                return;
            }

            // SCENE-SCOPED a propósito, NO DontDestroyOnLoad: uno creado en MainMenu —que no trae
            // ninguno horneado— sobrevivía a la carga de STP_Showcase, que sí lo trae, y los dos
            // coexistían. Cada escena que necesite uno vuelve a pedirlo.
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            EnsureInputModule(go);
            // Se avisa al CREAR y no al encontrar: crear es raro y explica de dónde salió el objeto;
            // encontrar pasa en cada pantalla y sería ruido.
            Debug.Log("[UI] EventSystem creado (ámbito de escena): la escena no traía ninguno.");
        }

        /// <summary>
        /// EL MÓDULO IMPORTA Y NO ES COSMÉTICO.
        ///
        /// <c>StandaloneInputModule</c> es el módulo del Input Manager LEGACY, y este proyecto está
        /// en <c>activeInputHandler: 1</c> —solo el Input System nuevo—. Consecuencias, las dos
        /// verificadas: el módulo no puede leer input, así que el EventSystem que lo lleve no sirve
        /// para nada; y uGUI emite un warning POR FRAME con stack trace, que fue uno de los tres
        /// surtidores del Editor.log de 8 GB que tumbó el editor el 2026-08-13.
        ///
        /// <c>VoiceSettingsUI</c> seguía creando uno legacy después de que
        /// <c>BackroomsGraphicsSettings</c> documentara este mismo fallo y lo arreglara en su copia:
        /// es lo que pasa cuando la misma función vive en tres sitios y solo se arregla en uno.
        /// </summary>
        private static void EnsureInputModule(GameObject go)
        {
#if ENABLE_INPUT_SYSTEM
            if (go.GetComponent<InputSystemUIInputModule>() != null) return;

            // Fuera el legacy ANTES de añadir el nuevo: dos módulos en el mismo EventSystem se
            // pelean por el puntero y el warning por frame vuelve por otra puerta.
            foreach (BaseInputModule stale in go.GetComponents<BaseInputModule>())
                UnityEngine.Object.Destroy(stale);

            go.AddComponent<InputSystemUIInputModule>();
#else
            if (go.GetComponent<BaseInputModule>() == null)
                go.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
