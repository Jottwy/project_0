using System.Reflection;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Los watchers de STP sondean al jugador local desde <c>Update</c>. La forma obvia de hacerlo
    /// —leer <c>GameMode.Instance</c> y comparar con null— es una trampa del vendor: el getter de
    /// <c>PolymindGames.MonoSingleton&lt;T&gt;</c> emite un <c>Debug.LogError</c> CON TRAZA DE PILA
    /// cada vez que se lee mientras no hay instancia. En el menú principal no hay <c>GameMode</c>,
    /// así que el sondeo escribía errores sin parar: 2 478 780 líneas de <c>Player.log</c> en unos
    /// minutos, medido sobre el standalone. Con el log así, ningún otro diagnóstico se puede leer.
    ///
    /// Estos tests no llevan assert de "no se ha logueado": el Test Framework ya tumba cualquier
    /// test con un log de Error no declarado (ver el comentario de <c>NetworkInitializerTests</c>).
    /// Basta con ejecutar <c>Update</c> sin <c>GameMode</c> en escena — la situación del menú.
    /// VERIFICADO QUE NO ES VACUO: revirtiendo el guard de uno solo de los watchers, su test se
    /// pone rojo con "Unhandled log message: '[Error] No instance of PolymindGames.GameMode'".
    ///
    /// Se invoca <c>Update</c> por reflexión a propósito: es privado, y hacerlo público solo para
    /// el test cambiaría la superficie de una clase de producción para nada. Por la misma razón se
    /// fuerzan por reflexión las guardas de temporizador/escena que, de otro modo, harían que
    /// <c>Update</c> ni siquiera llegase a la línea del singleton — un test que no alcanza la línea
    /// que dice cubrir es peor que no tenerlo.
    /// </summary>
    [TestFixture]
    public class StpWatcherSingletonGuardTests
    {
        private static void SetPrivate(object target, string field, object value)
        {
            FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(f, Is.Not.Null, $"{target.GetType().Name} ya no tiene el campo {field}");
            f.SetValue(target, value);
        }

        private static void PumpUpdate(MonoBehaviour behaviour)
        {
            MethodInfo update = behaviour.GetType()
                .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(update, Is.Not.Null,
                $"{behaviour.GetType().Name} ya no tiene Update(): si el sondeo se movió a otro " +
                "sitio, este guard tiene que seguirlo hasta allí, no borrarse.");

            // Varios frames: el fallo original era por-frame, y un guard que solo acierte la
            // primera vez (por ejemplo cacheando mal) tiene que caer igual.
            for (int i = 0; i < 3; i++)
                update.Invoke(behaviour, null);
        }

        [Test]
        public void ThePlacementWatcherDoesNotTouchTheSingletonWhenThereIsNoGameMode()
        {
            var go = new GameObject("TestPlacementWatcher");
            try
            {
                PumpUpdate(go.AddComponent<StpBuildingPlacementWatcher>());
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TheMaterialWatcherDoesNotTouchTheSingletonWhenThereIsNoGameMode()
        {
            var go = new GameObject("TestMaterialWatcher");
            try
            {
                PumpUpdate(go.AddComponent<StpBuildMaterialWatcher>());
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Éste era la fuente viva del spam que quedaba tras arreglar los dos de arriba: escanea por
        /// temporizador y, a diferencia de <see cref="StpNativeDropWatcher"/>, NO está limitado a la
        /// escena de gameplay, así que corría en el menú (623 errores medidos en 75 s).
        /// </summary>
        [Test]
        public void TheCarryableDropWatcherDoesNotTouchTheSingletonWhenThereIsNoGameMode()
        {
            var go = new GameObject("TestCarryableDropWatcher");
            try
            {
                var watcher = go.AddComponent<StpCarryableDropWatcher>();
                // Sin esto, el warmup y el temporizador de escaneo devuelven antes de llegar
                // siquiera a la línea del singleton, y el test pasaría sin probar nada.
                SetPrivate(watcher, "_warmedUp", true);
                SetPrivate(watcher, "_nextScan", 0f);
                PumpUpdate(watcher);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Este no llegó a spamear porque su guard de escena lo salvaba, pero tenía el mismo patrón:
        /// en la escena de gameplay sin <c>GameMode</c> (carga, teardown) habría logueado por escaneo.
        /// El test neutraliza ese guard apuntando <c>gameplayScene</c> a la escena activa del runner.
        /// </summary>
        [Test]
        public void TheNativeDropWatcherDoesNotTouchTheSingletonWhenThereIsNoGameMode()
        {
            var go = new GameObject("TestNativeDropWatcher");
            try
            {
                var watcher = go.AddComponent<StpNativeDropWatcher>();
                watcher.gameplayScene = SceneManager.GetActiveScene().name;
                SetPrivate(watcher, "_warmedUp", true);
                SetPrivate(watcher, "_nextScan", 0f);
                PumpUpdate(watcher);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
