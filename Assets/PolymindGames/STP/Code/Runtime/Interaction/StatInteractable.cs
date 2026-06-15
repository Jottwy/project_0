using System.Text;
using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(IHoverableInteractable), typeof(Poolable))]
    public class StatInteractable : MonoBehaviour
    {
        [SerializeField]
        private string _consumableName;
        
        [Title("Stat Changes")]
        [SerializeField, MinMaxSlider(-100, 100)]
        private Vector2Int _healthChange = new(0, 0);
        
        [SerializeField, MinMaxSlider(-100, 100)]
        private Vector2Int _hungerChange = new(15, 20);

        [SerializeField, MinMaxSlider(-100, 100)]
        private Vector2Int _thirstChange = new(5, 10);

        [SerializeField, Title("Audio")]
        [Tooltip("Audio that will be played after a character consumes this.")]
        private AudioData _consumeAudio = new(null);

        private void Awake()
        {
            var stringBuilder = new StringBuilder();

            if (_healthChange != Vector2Int.zero)
                stringBuilder.Append($"Health: {_healthChange.x} - ({_healthChange.y}\n");
            
            if (_hungerChange != Vector2Int.zero)
                stringBuilder.Append($"Hunger: {_hungerChange.x} - {_hungerChange.y}\n");
            
            if (_thirstChange != Vector2Int.zero)
                stringBuilder.Append($"Thirst: {_thirstChange.x} - {_thirstChange.y}");
            
            var interactable = GetComponent<IHoverableInteractable>();
            interactable.Description = stringBuilder.ToString();
            interactable.Title = _consumableName;
            interactable.Interacted += Consume;
        }
        
        private void Consume(IInteractable consumable, ICharacter character)
        {
            if (TryConsume(character))
            {
                var poolable = GetComponent<Poolable>();
                poolable.Release();
            }
        }
        
        private bool TryConsume(ICharacter character)
        {
            bool consumed = false;

            float healthChange = _healthChange.GetRandomFromRange();

            if (healthChange < 0f)
                character.HealthManager.ReceiveDamage(healthChange, new DamageArgs(DamageType.Undefined, null, character.transform.position, Vector3.zero));
            else
                character.HealthManager.RestoreHealth(healthChange);
            
            if (character.TryGetCC(out IHungerManagerCC hungerManager) && hungerManager.MaxHunger - hungerManager.Hunger > 1f)
            {
                hungerManager.Hunger += _hungerChange.GetRandomFromRange();
                consumed = true;
            }

            if (character.TryGetCC(out IThirstManagerCC thirstManager) && thirstManager.MaxThirst - thirstManager.Thirst > 1f)
            {
                thirstManager.Thirst += _thirstChange.GetRandomFromRange();
                consumed = true;
            }

            if (consumed)
                AudioManager.Instance.PlayClip2D(_consumeAudio);

            return consumed;
        }
    }
}