#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// El historial de la herramienta: Ctrl+Z deshace lo que se ha tocado EN LA SALA.
    ///
    /// Antes no había historial ninguno. El único paso que llegaba a la pila de Unity era la
    /// creación del objeto de previsualización (un <c>Undo.RegisterCreatedObjectUndo</c> al
    /// crearlo), así que el primer Ctrl+Z borraba la sala entera y los ajustes hechos con los
    /// mandos no se deshacían nunca. Ese registro se ha quitado: la previsualización es un objeto
    /// de usar y tirar, y no tiene por qué ocupar un sitio en el deshacer del proyecto.
    ///
    /// El historial es PROPIO y no el de Unity porque el modelo (<see cref="RoomDefinition"/>) es
    /// una clase normal, no un objeto de Unity: el sistema de deshacer del editor no sabe
    /// fotografiarla. Lo que sí sabe hacer es JSON — el mismo camino que ya usa el pool para
    /// guardarla —, así que cada paso del historial es una foto en JSON de la sala.
    ///
    /// Solo actúa mientras esta ventana tiene el foco. Fuera de ella, Ctrl+Z es el de siempre.
    /// </summary>
    public sealed partial class RoomAuthoringWindow
    {
        /// <summary>Cuántos pasos atrás se guardan. Cada uno es la sala entera en JSON — unos pocos
        /// kB — así que el tope va por no acumular sin límite en una sesión larga, no por tamaño.</summary>
        private const int UndoDepth = 64;

        private readonly List<string> _undoStack = new List<string>();
        private readonly List<string> _redoStack = new List<string>();

        /// <summary>La sala tal y como estaba ANTES de la interacción en curso.
        ///
        /// Se toma al empezar (botón pulsado, tecla bajada) y no a cada cambio: arrastrar un
        /// deslizador cambia la sala en cada frame, y guardar uno por frame llenaría el historial
        /// de sesenta pasos idénticos por cada arrastre. Así un arrastre entero es UN paso.</summary>
        private string _undoBaseline;

        /// <summary>Prepara el punto al que volverá el próximo Ctrl+Z.</summary>
        private void CaptureUndoBaseline() => _undoBaseline = JsonUtility.ToJson(_def);

        /// <summary>
        /// Cierra la interacción en curso: si la sala ha cambiado respecto a la foto de antes, ese
        /// estado anterior pasa a ser un paso del historial.
        ///
        /// Se compara el JSON en vez de fiarse de que haya habido interacción: pulsar un botón que
        /// no cambia nada, o arrastrar un deslizador y devolverlo a su sitio, no son pasos que
        /// nadie quiera deshacer.
        /// </summary>
        private void CommitUndo()
        {
            if (_undoBaseline == null) return;

            string now = JsonUtility.ToJson(_def);
            if (now == _undoBaseline) return;

            _undoStack.Add(_undoBaseline);
            if (_undoStack.Count > UndoDepth) _undoStack.RemoveAt(0);

            // Cualquier cambio nuevo invalida lo que hubiera por delante: es lo que hace todo el
            // mundo, y la alternativa (un árbol de historias) no la espera nadie.
            _redoStack.Clear();
            _undoBaseline = now;
        }

        /// <summary>
        /// Intercepta Ctrl+Z y Ctrl+Y mientras la ventana tiene el foco.
        ///
        /// Se hace por los comandos "Undo"/"Redo" y no leyendo la tecla a mano porque es así como
        /// el editor los reparte: la combinación real depende del sistema (en Mac es Cmd+Z) y de
        /// lo que el usuario tenga configurado. Hay que responder a los DOS eventos: el de
        /// validación dice "esto lo atiendo yo" y sin él el editor ni llega a mandar el segundo.
        /// </summary>
        private void HandleUndoShortcuts()
        {
            var e = Event.current;
            if (e == null) return;
            bool undo = e.commandName == "Undo", redo = e.commandName == "Redo";
            if (!undo && !redo) return;

            if (e.type == EventType.ValidateCommand)
            {
                e.Use();
                return;
            }
            if (e.type != EventType.ExecuteCommand) return;

            var from = undo ? _undoStack : _redoStack;
            var to = undo ? _redoStack : _undoStack;
            if (from.Count > 0)
            {
                to.Add(JsonUtility.ToJson(_def));
                string json = from[from.Count - 1];
                from.RemoveAt(from.Count - 1);

                // Sobre el objeto que ya hay, no uno nuevo: la ventana y las vistas previas
                // guardan referencias a él, y cambiarlo por otro las dejaría apuntando a la sala
                // vieja.
                JsonUtility.FromJsonOverwrite(json, _def);

                _undoBaseline = json;
                _foldouts.Clear();          // los índices se han movido: el plegado ya no vale
                RebuildIfLive();
                Repaint();
            }
            e.Use();
        }

        /// <summary>
        /// Abre un paso de historial cuando empieza una interacción y lo cierra cuando termina.
        ///
        /// Va al principio del pintado, antes de que ningún control haya podido tocar la sala: si
        /// se tomara la foto después, ya tendría el cambio dentro y el paso no serviría de nada.
        /// </summary>
        private void TrackUndoInteraction()
        {
            switch (Event.current.type)
            {
                case EventType.MouseDown:
                case EventType.KeyDown:
                case EventType.DragPerform:
                    CaptureUndoBaseline();
                    break;
                case EventType.MouseUp:
                case EventType.KeyUp:
                    CommitUndo();
                    break;
            }
        }
    }
}
#endif
