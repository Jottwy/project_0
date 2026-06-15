using UnityEngine;

namespace PolymindGames.Demo
{
    public sealed class TeleportPlayerBehaviour : MonoBehaviour
    {
        [SerializeField, ReorderableList(HasLabels = false)]
        private Transform[] _teleportPoints;


        public void TeleportPlayer(ICharacter character)
        {
            Transform teleportPoint = _teleportPoints.SelectRandom();
            character.SetPositionAndRotation(teleportPoint.position, teleportPoint.rotation);
        }
    }
}