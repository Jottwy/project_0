using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace PolymindGames.Editor
{
	using UnityEditor;
	
	[CustomEditor(typeof(RagdollCreationWizard))]
	public sealed class RagdollCreationWizardEditor : EditorWizardBaseEditor
	{
		public override void DrawCustomInspector()
		{
			DrawBaseInspector();
			GUILayout.FlexibleSpace();
			EditorGUILayout.HelpBox("Drag all bones from the hierarchy into their slots.\nMake sure your character is in T-Stand.\n", MessageType.Info);
			DrawResetAndCreateButtons();
		}
	}

	public sealed class RagdollCreationWizard : EditorWizardBase
	{
		public sealed class BoneData
		{
			public string Name;

			public Transform Anchor;
			public CharacterJoint Joint;
			public BoneData Parent;

			public float MinLimit;
			public float MaxLimit;
			public float SwingLimit;

			public Vector3 Axis;
			public Vector3 NormalAxis;

			public float RadiusScale;
			public Type ColliderType;

			public readonly List<BoneData> Children = new();
			public float Density;
			public float TotalMass;
		}
		
		[SerializeField]
		private Animator _animator;

		[SerializeField]
		private PhysicsMaterial _fleshMaterial;

		[Header("Bones")]
		[SerializeField, SceneObjectOnly]
		private Transform _root;

		[SerializeField]
		private Transform _leftHips;

		[SerializeField]
		private Transform _leftKnee;

		[SerializeField]
		private Transform _leftFoot;

		[SerializeField]
		private Transform _rightHips;

		[SerializeField]
		private Transform _rightKnee;

		[SerializeField]
		private Transform _rightFoot;

		[SerializeField]
		private Transform _leftArm;

		[SerializeField]
		private Transform _leftElbow;

		[SerializeField]
		private Transform _rightArm;

		[SerializeField]
		private Transform _rightElbow;

		[SerializeField]
		private Transform _middleSpine;

		[SerializeField]
		private Transform _head;

		[Header("Joint Settings")]
		[SerializeField]
		private bool _enableProjection = true;

		[Header("Ragdoll Settings")]
		[SerializeField]
		private float _totalMass = 60f;

		[SerializeField]
		private bool _flipForward; 

		private readonly Dictionary<Transform, BoneData> _map = new();
		
		private Vector3 _right = Vector3.right;
		private Vector3 _up = Vector3.up;
		private Vector3 _forward = Vector3.forward;
		
		private Vector3 _worldRight = Vector3.right;
		private Vector3 _worldUp = Vector3.up;
		private Vector3 _worldForward = Vector3.forward;   

		private List<BoneData> _bones;
		private BoneData _rootBone;


		public override string ValidateSettings()
		{
			PrepareBones();
			
			_map.Clear();
			foreach (BoneData bone in _bones)
			{
				if (bone.Anchor)
				{
					if (_map.TryGetValue(bone.Anchor, out var oldBone))
						return $"{bone.Name} and {oldBone.Name} may not be assigned to the same bone.";

					_map.Add(bone.Anchor, bone);
				}
			}
			
			foreach (BoneData bone in _bones)
			{
				if (bone.Anchor == null)
					return $"{bone.Name} has not been assigned yet.\n";
			}
			
			
			return string.Empty;
		}

		public override void CreateAsset()
		{
			Cleanup();
			BuildCapsules();	
			AddBreastColliders();
			AddHeadCollider();
			
			BuildBodies ();
			BuildJoints ();
			CalculateMass();

			AddRagdollComponent();

			_animator = null;
			_root = null;
		}

		public override void Update()
		{        
			CalculateAxes();
		}
		
		private void OnEnable()
		{
			Selection.selectionChanged += OnSelectionChanged;
		}
		
		private void OnDestroy()
		{
			Selection.selectionChanged -= OnSelectionChanged;
		}

		private void OnSelectionChanged()
		{
			_rootBone = null;
			_bones = null;

			if (Selection.activeGameObject == null)
			{
				_animator = null;

				_root = null;

				_leftHips = null;
				_leftKnee = null;
				_leftFoot = null;

				_rightHips = null;
				_rightKnee = null;
				_rightFoot = null;

				_leftArm = null;
				_leftElbow = null;

				_rightArm = null;
				_rightElbow = null;

				_middleSpine = null;

				_head = null;
			}

			// Repaint();
		}
		
		private static void DecomposeVector(out Vector3 normal, out Vector3 tangent, Vector3 outwardDir, Vector3 outwardNormal)
		{
			outwardNormal = outwardNormal.normalized;
			normal = outwardNormal * Vector3.Dot(outwardDir, outwardNormal);
			tangent = outwardDir - normal;
		}
		
		private void CalculateAxes()
		{
			if (_head != null && _root != null)
				_up = CalculateDirectionAxis(_root.InverseTransformPoint(_head.position));
			if (_rightElbow != null && _root != null)
			{
				DecomposeVector(out _, out var removed, _root.InverseTransformPoint(_rightElbow.position), _up);
				_right = CalculateDirectionAxis(removed);
			}
			
			_forward = Vector3.Cross(_right, _up);
			if (_flipForward)
				_forward = -_forward;	
		}

		private void PrepareBones()
		{
			if (Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<Animator>() != null)
				_animator = Selection.activeGameObject.GetComponent<Animator>();
			
			if (_animator != null) 
			{			
				try
				{
					_root = _animator.GetBoneTransform (HumanBodyBones.Hips);
					
					_leftHips = _animator.GetBoneTransform (HumanBodyBones.LeftUpperLeg);
					_leftKnee = _animator.GetBoneTransform (HumanBodyBones.LeftLowerLeg);
					_leftFoot = _animator.GetBoneTransform (HumanBodyBones.LeftFoot);
					
					_rightHips = _animator.GetBoneTransform (HumanBodyBones.RightUpperLeg);
					_rightKnee = _animator.GetBoneTransform (HumanBodyBones.RightLowerLeg);
					_rightFoot = _animator.GetBoneTransform (HumanBodyBones.RightFoot);
					
					_leftArm = _animator.GetBoneTransform (HumanBodyBones.LeftUpperArm);
					_leftElbow = _animator.GetBoneTransform (HumanBodyBones.LeftLowerArm);
					
					_rightArm = _animator.GetBoneTransform (HumanBodyBones.RightUpperArm);
					_rightElbow = _animator.GetBoneTransform (HumanBodyBones.RightLowerArm);
					
					_middleSpine = _animator.GetBoneTransform (HumanBodyBones.Chest);

					_head = _animator.GetBoneTransform (HumanBodyBones.Head);

					EditorUtility.SetDirty(this);
				}
				catch
				{
					// ignored
				}
			}

			if (_root)
			{
				_worldRight = _root.TransformDirection(_right);
				_worldUp = _root.TransformDirection(_up);
				_worldForward = _root.TransformDirection(_forward);
			}
			
			_bones = new List<BoneData>();
			
			_rootBone = new BoneData
			{
				Name = "Root",
				Anchor = _root,
				Parent = null,
				Density = 2.5F
			};
			
			_bones.Add (_rootBone);
			
			AddMirroredJoint ("Hips", _leftHips, _rightHips, "Root", _worldRight, _worldForward, -20, 70, 30, typeof(CapsuleCollider), 0.3F, 1.5f);
			AddMirroredJoint ("Knee", _leftKnee, _rightKnee, "Hips", _worldRight, _worldForward, -80, 0, 0, typeof(CapsuleCollider), 0.25F, 1.5f);
			
			AddJoint ("Middle Spine", _middleSpine, "Root", _worldRight, _worldForward, -20, 20, 10, null, 1f, 2.5f);
			
			AddMirroredJoint ("Arm", _leftArm, _rightArm, "Middle Spine", _worldUp, _worldForward, -70f, 10f, 50f, typeof(CapsuleCollider), 0.25f, 1f);
			AddMirroredJoint ("Elbow", _leftElbow, _rightElbow, "Arm", _worldForward, _worldUp, -90f, 0f, 0f, typeof(CapsuleCollider), 0.2f, 1f);
			
			AddJoint ("Head", _head, "Middle Spine", _worldRight, _worldForward, -40, 25, 25, null, 1, 1.0f);
		}

		private BoneData FindBone(string boneName)
		{
			return _bones.FirstOrDefault(bone => bone.Name == boneName);
		}
		
		private void AddMirroredJoint(string jointName, Transform leftAnchor, Transform rightAnchor, string parent, Vector3 worldTwistAxis, Vector3 worldSwingAxis, float minLimit, float maxLimit, float swingLimit, Type colliderType, float radiusScale, float density)
		{
			AddJoint ("Left " + jointName, leftAnchor, parent, worldTwistAxis, worldSwingAxis, minLimit, maxLimit, swingLimit, colliderType, radiusScale, density);
			AddJoint ("Right " + jointName, rightAnchor, parent, worldTwistAxis, worldSwingAxis, minLimit, maxLimit, swingLimit, colliderType, radiusScale, density);
		}	
		
		private void AddJoint(string jointName, Transform anchor, string parent, Vector3 worldTwistAxis, Vector3 worldSwingAxis, float minLimit, float maxLimit, float swingLimit, Type colliderType, float radiusScale, float density)
		{
			BoneData bone = new BoneData
			{
				Name = jointName,
				Anchor = anchor,
				Axis = worldTwistAxis,
				NormalAxis = worldSwingAxis,
				MinLimit = minLimit,
				MaxLimit = maxLimit,
				SwingLimit = swingLimit,
				Density = density,
				ColliderType = colliderType,
				RadiusScale = radiusScale
			};

			if (FindBone (parent) != null)
				bone.Parent = FindBone (parent);
			else if (jointName.StartsWith ("Left"))
				bone.Parent = FindBone ("Left " + parent);
			else if (jointName.StartsWith ("Right"))
				bone.Parent = FindBone ("Right "+ parent);		
			
			bone.Parent.Children.Add(bone);
			_bones.Add (bone);
		}
		
		private void BuildCapsules()
		{
			foreach (BoneData bone in _bones)
			{
				if (bone.ColliderType != typeof (CapsuleCollider))
					continue;
				
				int direction;
				float distance;
				if (bone.Children.Count == 1)
				{
					BoneData childBone = bone.Children[0];
					Vector3 endPoint = childBone.Anchor.position;
					CalculateDirection (bone.Anchor.InverseTransformPoint(endPoint), out direction, out distance);
				}
				else
				{
					Vector3 endPoint = bone.Anchor.position - bone.Parent.Anchor.position + bone.Anchor.position;
					CalculateDirection (bone.Anchor.InverseTransformPoint(endPoint), out direction, out distance);
					
					if (bone.Anchor.GetComponentsInChildren(typeof(Transform)).Length > 1)
					{
						Bounds bounds = new Bounds();
						foreach (Transform child in bone.Anchor.GetComponentsInChildren<Transform>())
							bounds.Encapsulate(bone.Anchor.InverseTransformPoint(child.position));
						
						distance = distance > 0 ? bounds.max[direction] : bounds.min[direction];
					}
				}
				
				CapsuleCollider collider = Undo.AddComponent<CapsuleCollider>(bone.Anchor.gameObject);
				collider.direction = direction;
				
				Vector3 center = Vector3.zero;
				center[direction] = distance * 0.5F;
				collider.center = center;
				collider.height = Mathf.Abs (distance);
				collider.radius = Mathf.Abs (distance * bone.RadiusScale);
			}
		}
		
		private void Cleanup()
		{
			var ragdoll = _animator.GetComponentInChildren<CharacterRagdoll>();
			if (ragdoll != null)
				Undo.DestroyObjectImmediate(ragdoll);
			
			foreach (BoneData bone in _bones)
			{
				if (!bone.Anchor)
					continue;
				
				var hitboxes = bone.Anchor.GetComponentsInChildren<CharacterHitbox>();
				foreach (CharacterHitbox hitbox in hitboxes)
					Undo.DestroyObjectImmediate(hitbox);
				
				var impactHandlers = bone.Anchor.GetComponentsInChildren<RigidbodyImpactHandler>();
				foreach (var impactHandler in impactHandlers)
					Undo.DestroyObjectImmediate(impactHandler);
				
				var joints = bone.Anchor.GetComponentsInChildren<Joint>();
				foreach (Joint joint in joints)
					Undo.DestroyObjectImmediate(joint);
				
				var bodies = bone.Anchor.GetComponentsInChildren<Rigidbody>();
				foreach (Rigidbody body in bodies)
					Undo.DestroyObjectImmediate(body);

				var colliders = bone.Anchor.GetComponentsInChildren<Collider>();
				foreach (Collider collider in colliders)
				{
					if (collider.transform != _leftFoot.transform && collider.transform != _rightFoot)
						Undo.DestroyObjectImmediate(collider);
				}
			}
		}
		
		private void BuildBodies()
		{
			foreach(BoneData bone in _bones)
			{
				Undo.AddComponent<Rigidbody>(bone.Anchor.gameObject);
				bone.Anchor.GetComponent<Rigidbody>().mass = bone.Density;
			}
		}
		
		private void BuildJoints()
		{
			foreach (BoneData bone in _bones)
			{
				if (bone.Parent == null)
					continue;
				
				CharacterJoint joint = Undo.AddComponent<CharacterJoint>(bone.Anchor.gameObject);
				bone.Joint = joint;

				joint.axis = CalculateDirectionAxis (bone.Anchor.InverseTransformDirection(bone.Axis));
				joint.swingAxis = CalculateDirectionAxis (bone.Anchor.InverseTransformDirection(bone.NormalAxis));
				joint.anchor = Vector3.zero;
				joint.connectedBody = bone.Parent.Anchor.GetComponent<Rigidbody>();
					
				SoftJointLimit limit = new SoftJointLimit
				{
					limit = bone.MinLimit
				};

				joint.lowTwistLimit = limit;
				
				limit.limit = bone.MaxLimit;
				joint.highTwistLimit = limit;
				
				limit.limit = bone.SwingLimit;
				joint.swing1Limit = limit;
				
				limit.limit = 0;
				joint.swing2Limit = limit;
	            joint.enableProjection = _enableProjection;
			}
		}
		
		private static void CalculateMassRecursively(BoneData bone)
		{
			float mass = bone.Anchor.GetComponent<Rigidbody>().mass;
			foreach (BoneData child in bone.Children)
			{
				CalculateMassRecursively (child);
				mass += child.TotalMass;
			}

			bone.TotalMass = mass;
		}
		
		private void CalculateMass()
		{
			CalculateMassRecursively (_rootBone);

			float massScale = _totalMass / _rootBone.TotalMass;
	        foreach (BoneData bone in _bones)
				bone.Anchor.GetComponent<Rigidbody>().mass *= massScale;
			
			CalculateMassRecursively(_rootBone);
		}
		
		private void AddRagdollComponent()
		{
			var ragdoll = Undo.AddComponent<CharacterRagdoll>(_animator.gameObject);
			var pelvis = _root.GetComponent<Rigidbody>();
			
			var bones = ragdoll.GetComponentsInChildren<CharacterJoint>()
				.Select(joint => joint.GetComponent<Rigidbody>())
				.Append(pelvis)
				.ToArray();

			ragdoll.Animator = _animator;
			ragdoll.Bones = bones;

			foreach (var bone in bones)
			{
				Undo.AddComponent<CharacterHitbox>(bone.gameObject);
				Undo.AddComponent<RigidbodyImpactHandler>(bone.gameObject);
			}

			foreach (var col in ragdoll.GetComponentsInChildren<Collider>())
				col.sharedMaterial = _fleshMaterial;
		}

		private static void CalculateDirection (Vector3 point, out int direction, out float distance)
		{
			direction = 0;
			if (Mathf.Abs(point[1]) > Mathf.Abs(point[0]))
				direction = 1;
			if (Mathf.Abs(point[2]) >Mathf.Abs(point[direction]))
				direction = 2;

			distance = point[direction];
		}
		
		private static Vector3 CalculateDirectionAxis(Vector3 point)
		{
			CalculateDirection (point, out int direction, out float distance);
			Vector3 axis = Vector3.zero;
			if (distance > 0)
				axis[direction] = 1f;
			else
				axis[direction] = -1f;
			
			return axis;
		}
		
		private static int SmallestComponent(Vector3 point)
		{
			int direction = 0;
			if (Mathf.Abs(point[1]) < Mathf.Abs(point[0]))
				direction = 1;
			if (Mathf.Abs(point[2]) < Mathf.Abs(point[direction]))
				direction = 2;
			return direction;
		}
		
		private static int LargestComponent(Vector3 point)
		{
			int direction = 0;
			if (Mathf.Abs(point[1]) > Mathf.Abs(point[0]))
				direction = 1;
			if (Mathf.Abs(point[2]) > Mathf.Abs(point[direction]))
				direction = 2;
			return direction;
		}
		
		private Bounds Clip(Bounds bounds, Transform relativeTo, Transform clipTransform, bool below)
		{
			int axis = LargestComponent(bounds.size);
			
			if (Vector3.Dot (_worldUp, relativeTo.TransformPoint(bounds.max)) > Vector3.Dot (_worldUp, relativeTo.TransformPoint(bounds.min)) == below)
			{
				Vector3 min = bounds.min;
				min[axis] = relativeTo.InverseTransformPoint(clipTransform.position)[axis];
				bounds.min = min;
			}
			else
			{
				Vector3 max = bounds.max;
				max[axis] = relativeTo.InverseTransformPoint(clipTransform.position)[axis];
				bounds.max = max;
			}

			return bounds;
		}
		
		private Bounds GetBreastBounds (Transform relativeTo)
		{
			Bounds bounds = new Bounds ();
			bounds.Encapsulate (relativeTo.InverseTransformPoint (_leftHips.position));
			bounds.Encapsulate (relativeTo.InverseTransformPoint (_rightHips.position));
			bounds.Encapsulate (relativeTo.InverseTransformPoint (_leftArm.position));
			bounds.Encapsulate (relativeTo.InverseTransformPoint (_rightArm.position));
			Vector3 size = bounds.size;
			size[SmallestComponent (bounds.size)] = size[LargestComponent (bounds.size)] / 2.0F;
			bounds.size = size;

			return bounds;		
		}
		
		private void AddBreastColliders()
		{
			if (_middleSpine != null && _root != null)
			{
				Bounds bounds;
				BoxCollider box;

				bounds = Clip (GetBreastBounds (_root), _root, _middleSpine, false);
				box = Undo.AddComponent<BoxCollider>(_root.gameObject);
				box.center = bounds.center;
				box.size = bounds.size;
				
				bounds = Clip (GetBreastBounds (_middleSpine), _middleSpine, _middleSpine, true);
				box = Undo.AddComponent<BoxCollider>(_middleSpine.gameObject);
				box.center = bounds.center;
				box.size = bounds.size;
			}
			else
			{
				Bounds bounds = new Bounds ();
				bounds.Encapsulate (_root.InverseTransformPoint (_leftHips.position));
				bounds.Encapsulate (_root.InverseTransformPoint (_rightHips.position));
				bounds.Encapsulate (_root.InverseTransformPoint (_leftArm.position));
				bounds.Encapsulate (_root.InverseTransformPoint (_rightArm.position));
				
				Vector3 size = bounds.size;
				size[SmallestComponent (bounds.size)] = size[LargestComponent (bounds.size)] / 2.0F;

				BoxCollider box = Undo.AddComponent<BoxCollider>(_root.gameObject);
				box.center = bounds.center;
				box.size = size;
			}
		}
		
		private void AddHeadCollider()
		{
			if (_head.GetComponent<Collider>())
				Destroy (_head.GetComponent<Collider>());
			
			float radius = Vector3.Distance(_rightArm.transform.position ,_leftArm.transform.position);
			radius /= 4f;

			SphereCollider sphere = Undo.AddComponent<SphereCollider>(_head.gameObject);
			sphere.radius = radius;
			Vector3 center = Vector3.zero;

			CalculateDirection (_head.InverseTransformPoint(_root.position), out int direction, out float distance);

			if (distance > 0)
				center[direction] = -radius;
			else
				center[direction] = radius;
			
			sphere.center = center;
		}
	}
}