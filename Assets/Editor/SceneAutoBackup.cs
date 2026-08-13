using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Copia de seguridad periódica de las escenas abiertas, fuera de <c>Assets/</c>.
    ///
    /// POR QUÉ EXISTE. El 2026-08-13 el editor crasheó en <c>ScriptingDomainUnload</c> y se
    /// llevó por delante una tarde de cambios de iluminación sin guardar. El backup de
    /// recuperación de Unity (<c>Temp/__Backupscenes/0.backup</c>) NO sirvió: se sobrescribe
    /// al reabrir el proyecto, y para cuando se fue a mirar ya se había pisado dos minutos
    /// después del reinicio. Este editor acumula 5 crashes en 11 días, así que no es un
    /// accidente irrepetible.
    ///
    /// GUARDA UNA COPIA, NO LA ESCENA. Es deliberado: un auto-save de verdad consolidaría en
    /// disco cambios que quizá se querían descartar, y eso destruye trabajo de otra manera.
    /// <c>SaveScene(..., saveAsCopy: true)</c> escribe aparte y NO limpia el flag de sucio,
    /// así que Ctrl+S sigue significando exactamente lo que significaba.
    ///
    /// Las copias van a <c>&lt;proyecto&gt;/SceneBackups/</c> — FUERA de Assets, o Unity las
    /// importaría como escenas de verdad y aparecerían en el Build Settings y en las
    /// búsquedas. El .gitignore las excluye.
    ///
    /// Recuperar una: cerrar Unity si crasheó, copiar el .unity de SceneBackups sobre el
    /// original (o abrirlo con File ▸ Open Scene navegando fuera del proyecto), comparar y
    /// quedarse con lo que interese.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneAutoBackup
    {
        private const string EnabledPref  = "Backrooms.SceneAutoBackup.Enabled";
        private const string MenuPath     = "Backrooms/Auto Backup/Enabled";
        private const string OpenMenuPath = "Backrooms/Auto Backup/Open backup folder";
        private const string FolderName   = "SceneBackups";

        /// <summary>Minutos entre copias. 5 acota la pérdida a un intervalo que se rehace.</summary>
        private const double IntervalMinutes = 5.0;

        /// <summary>
        /// Copias rotatorias por escena. No es solo tope de disco: si el crash corrompe la
        /// escena, la copia MÁS RECIENTE puede haber capturado ya el estado malo, y hay que
        /// poder retroceder. Con 6 slots hay media hora de historia.
        /// </summary>
        private const int Slots = 6;

        private static double _nextRun;
        private static int    _slot;

        static SceneAutoBackup()
        {
            _nextRun = EditorApplication.timeSinceStartup + IntervalMinutes * 60.0;
            EditorApplication.update += Tick;
        }

        private static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true); // ON por defecto: la pérdida ya ocurrió
            set => EditorPrefs.SetBool(EnabledPref, value);
        }

        [MenuItem(MenuPath)]
        private static void ToggleEnabled() => Enabled = !Enabled;

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        [MenuItem(OpenMenuPath)]
        private static void OpenFolder()
        {
            var dir = BackupRoot();
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        private static string BackupRoot() =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, FolderName);

        private static void Tick()
        {
            if (!Enabled) return;
            if (EditorApplication.timeSinceStartup < _nextRun) return;
            _nextRun = EditorApplication.timeSinceStartup + IntervalMinutes * 60.0;

            // En Play la escena de disco no es la que se está editando, y guardar mientras
            // se compila o se importa pilla el estado a medias. Se salta el turno entero.
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
                return;

            BackupOnce();
        }

        private static void BackupOnce()
        {
            string root;
            try
            {
                root = BackupRoot();
                Directory.CreateDirectory(root);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneAutoBackup] no se pudo crear la carpeta: {e.Message}");
                return;
            }

            int saved = 0;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                // Sin ruta = escena nunca guardada; SaveScene la trataría como "guardar como"
                // y abriría un diálogo modal en mitad del trabajo. No es asunto de un backup.
                if (!scene.isLoaded || !scene.isDirty || string.IsNullOrEmpty(scene.path)) continue;

                if (TrySaveCopy(scene, root)) saved++;
            }

            if (saved > 0)
            {
                _slot = (_slot + 1) % Slots;
                Debug.Log($"[SceneAutoBackup] {saved} escena(s) copiadas a {FolderName}/ " +
                          $"(slot {_slot}/{Slots}). Ctrl+S sigue sin usarse: son copias.");
            }
        }

        private static bool TrySaveCopy(Scene scene, string root)
        {
            var path = Path.Combine(root, $"{scene.name}_slot{_slot}.unity");
            try
            {
                // saveAsCopy: escribe aparte y deja la escena marcada como sucia, que es todo
                // el punto — el usuario sigue decidiendo cuándo guarda de verdad.
                return EditorSceneManager.SaveScene(scene, path, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneAutoBackup] fallo copiando '{scene.name}': {e.Message}");
                return false;
            }
        }
    }
}
