#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Corre UNA clase de tests EditMode por reflexión y escupe el resultado en consola, sin pasar
    /// por el Test Runner de Unity.
    ///
    /// EXISTE PORQUE EL RUNNER DEL EDITOR NO CONTESTA (`docs/STATE.md`, 06-09): la petición se
    /// queda en "started" y el log dice `RunFailed` en `RestoreSceneSetupTask` antes de ejecutar
    /// un solo test. Un test que no corre no falla: no existe, que es peor — y estas
    /// comprobaciones son justo las que cierran fallos silenciosos (un item sin etiqueta, un
    /// gastador heredado que vacía la linterna en un segundo).
    ///
    /// NO SUSTITUYE AL RUNNER y no pretende hacerlo: no hay `[SetUp]`, ni `[TearDown]`, ni
    /// parámetros, ni `UnityTest` con corrutinas. Ejecuta los métodos `[Test]` públicos y sin
    /// argumentos de una clase, cuenta verdes y rojos, e imprime el mensaje de la aserción que
    /// falló. Para lo que hace falta el día que el runner reviva, sigue haciendo falta el runner.
    ///
    /// Es genérico a propósito —el nombre de la clase se pide por diálogo— porque el runner no está
    /// roto sólo para la linterna.
    /// </summary>
    public static class BackroomsEditModeFixtureRunner
    {
        private const string LastFixtureKey = "backrooms.fixturerunner.last";

        /// <summary>
        /// SIN DIÁLOGO a propósito: este menú se dispara también desde el runner de ficheros, que
        /// no tiene manos para contestar una ventana modal. La clase se cambia con el otro menú.
        /// </summary>
        [MenuItem("Backrooms/Tests/Correr la clase guardada", false, 400)]
        public static void RunSavedFixture()
        {
            // Un fichero manda sobre la preferencia: es la única forma de elegir la clase desde
            // fuera del editor (el runner de sesión no puede contestar al selector de fichero).
            // Se consume al leerlo para que no se quede pegado a la siguiente sesión.
            const string overridePath = "Temp/claude_fixture.txt";
            string name = EditorPrefs.GetString(LastFixtureKey, "CrankFlashlightItemTests");
            if (System.IO.File.Exists(overridePath))
            {
                string fromFile = System.IO.File.ReadAllText(overridePath).Trim();
                if (fromFile.Length > 0) name = fromFile;
                try { System.IO.File.Delete(overridePath); } catch { /* se releerá la próxima vez */ }
            }
            Run(name);
        }

        [MenuItem("Backrooms/Tests/Elegir la clase…", false, 401)]
        public static void ChooseFixture()
        {
            // Sin campo de texto en la API de diálogos del editor: se pide por el selector de
            // fichero, que es feo pero no obliga a escribir una ventana propia para esto.
            string path = EditorUtility.OpenFilePanel("Elige el .cs de la clase de tests",
                "Assets/Tests/EditMode", "cs");
            if (string.IsNullOrEmpty(path))
                return;

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            EditorPrefs.SetString(LastFixtureKey, name);
            Run(name);
        }

        /// <summary>
        /// Corre la clase por nombre. Público para poder dispararlo desde un runner de ficheros
        /// sin pasar por el diálogo.
        /// </summary>
        public static void Run(string fixtureName)
        {
            Type fixture = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                // Un assembly con dependencias a medias tira aquí, y tirar por uno dejaría sin
                // buscar todos los demás.
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var candidate in types)
                {
                    if (candidate == null || candidate.Name != fixtureName) continue;
                    fixture = candidate;
                    break;
                }
                if (fixture != null) break;
            }

            if (fixture == null)
            {
                Debug.LogError($"[FixtureRunner] No hay ninguna clase '{fixtureName}' cargada. " +
                               "¿Compiló el assembly de tests?");
                return;
            }

            object instance;
            try
            {
                instance = Activator.CreateInstance(fixture);
            }
            catch (Exception e)
            {
                Debug.LogError($"[FixtureRunner] '{fixtureName}' no se puede instanciar: {e.Message}");
                return;
            }

            int passed = 0, failed = 0;
            var report = new StringBuilder();

            foreach (var method in fixture.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetParameters().Length != 0) continue;

                bool isTest = false;
                foreach (var attribute in method.GetCustomAttributes(false))
                {
                    if (attribute.GetType().Name != "TestAttribute") continue;
                    isTest = true;
                    break;
                }
                if (!isTest) continue;

                try
                {
                    method.Invoke(instance, null);
                    passed++;
                    report.Append("  OK   ").Append(method.Name).Append('\n');
                }
                catch (TargetInvocationException e)
                {
                    failed++;
                    // El de dentro es el que trae el mensaje de la aserción; el envoltorio de
                    // reflexión no dice nada útil.
                    var inner = e.InnerException ?? e;
                    report.Append("  FALLA ").Append(method.Name).Append(": ")
                          .Append(inner.Message).Append('\n');
                }
            }

            string summary = $"[FixtureRunner] {fixtureName}: {passed} verdes, {failed} rojos\n{report}";
            if (failed > 0) Debug.LogError(summary);
            else Debug.Log(summary);
        }
    }
}
#endif
