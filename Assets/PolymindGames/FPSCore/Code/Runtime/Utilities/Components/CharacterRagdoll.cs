using UnityEngine;

namespace PolymindGames
{
	public sealed class CharacterRagdoll : MonoBehaviour 
	{
		[SerializeField]
		private bool _enableOnStart;

		[SerializeField, NotNull]
		private Animator _animator;
		
		[SerializeField, SpaceArea]
		[ReorderableList(HasLabels = false)]
		private Rigidbody[] _bones;

		public Rigidbody[] Bones
		{
			get => _bones;
			set => _bones = value;
		}

		public Animator Animator
		{
			get => _animator;
			set => _animator = value;
		}
		
		public void EnableRagdoll()
		{
			_animator.enabled = false;
			foreach(var bone in _bones)
			{
				bone.isKinematic = false;
				bone.gameObject.layer = LayerConstants.DynamicObject;
			}
		}

		public void DisableRagdoll()
		{
			_animator.enabled = true;
			_animator.Rebind();
			foreach(var bone in _bones)
			{
				bone.isKinematic = true;
				bone.gameObject.layer = LayerConstants.Hitbox;
			}
		}

		private void Awake()
		{
			if (_enableOnStart)
			{
				EnableRagdoll();
			}
			else
			{
				DisableRagdoll();
			}
		}
	}
}
