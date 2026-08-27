using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Monta UNA pieza autorada en la escena, al arrancar.
    ///
    /// POR QUÉ AL ARRANCAR Y NO AL CREAR LA ESCENA: <see cref="Wg3SceneAssembler"/> marca todo lo
    /// que monta con <c>HideFlags.DontSave</c> —las mallas se crean en tiempo de ejecución y no son
    /// assets— así que una escena montada en el editor se guarda VACÍA. La primera versión de esta
    /// escena hizo justo eso: al entrar a Play quedaban el jugador y el sondeo flotando en el vacío,
    /// el jugador cayó 191 m y el veredicto acusó al suelo de no sostener cuando no había suelo.
    ///
    /// Es el mismo patrón que <see cref="Wg3TestWorld"/>, que reconstruye su mundo en cada arranque.
    /// La diferencia es que aquí no hay composición: una pieza, en el origen, sin girar.
    /// </summary>
    public sealed class Wg3SinglePieceWorld : MonoBehaviour
    {
        public Wg3PieceAsset piece;
        public Wg3Materials materials;
        public bool spawnLights = true;

        private readonly List<Mesh> _meshes = new List<Mesh>();

        /// <summary>La colocación montada, por si alguien necesita sus bocas en coordenadas de
        /// mundo. Null hasta que <see cref="Generate"/> corre.</summary>
        public Wg3Placement Placement { get; private set; }

        /// <summary>En Awake y no en Start: el sondeo coloca al jugador en su propio Start, y si la
        /// geometría no existiera todavía lo dejaría cayendo desde el primer fotograma.</summary>
        private void Awake() => Generate();

        private void OnDestroy() => Wg3SceneAssembler.Clear(transform, _meshes);

        public void Generate()
        {
            Wg3SceneAssembler.Clear(transform, _meshes);
            if (piece == null || !piece.IsBaked)
            {
                Debug.LogError("[WG3] no hay pieza horneada que montar.", this);
                return;
            }

            Wg3Piece model = piece.ToPiece();
            Placement = new Wg3Placement
            {
                piece = model,
                rotation = 0,
                originX = 0f,
                originZ = 0f,
                socketState = new byte[model.sockets.Length]
            };

            var world = new Wg3World();
            world.placements.Add(Placement);
            Wg3SceneAssembler.Assemble(world, transform, materials, _meshes, spawnLights);
        }
    }
}
