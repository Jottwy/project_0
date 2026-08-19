using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// El componente en el que se convierte un <see cref="RoomDefinition.Marker"/> de tipo
    /// <see cref="RoomDefinition.MarkerKind.Prop"/> o <see cref="RoomDefinition.MarkerKind.Spawn"/>
    /// al hornear la sala — ver <c>RoomAuthoringWindow.SaveGeneratedRoom</c>.
    ///
    /// Un marcador de luz no necesita esto: se convierte en un <see cref="Light"/> de verdad, que
    /// ya es su propio punto encontrable. Prop y Spawn no tienen un componente de motor
    /// equivalente, así que llevan este en su lugar — lo único que aporta es el sitio y la
    /// etiqueta; quien la resuelve (el sistema de props del mundo, el de aparición de
    /// jugadores...) es cosa de fuera de esta sala.
    /// </summary>
    public sealed class RoomMarker : MonoBehaviour
    {
        public RoomDefinition.MarkerKind kind;

        /// <summary>Se llama <c>label</c> y no <c>tag</c> a propósito: <c>Component</c> ya trae
        /// su propio <c>tag</c> (el sistema de tags de Unity), y llamar igual a este lo taparía
        /// en silencio — confusión garantizada la primera vez que alguien escriba
        /// <c>marker.tag</c> esperando esto y lea el otro.</summary>
        public string label = "";
    }
}
