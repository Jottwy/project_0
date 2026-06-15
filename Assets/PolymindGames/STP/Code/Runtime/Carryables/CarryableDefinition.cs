using PolymindGames.ProceduralMotion;
using System.Diagnostics;
using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PolymindGames.WieldableSystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Building/Carryable Definition", fileName = "Carryable_")]
    public sealed class CarryableDefinition : DataDefinition<CarryableDefinition>
    {
        [SerializeField, SpritePreview]
        private Sprite _icon;
        
        [SerializeField, NotNull, PrefabObjectOnly]
        [Tooltip("Corresponding pickup for this carryable.")]
        private CarryablePickup _pickup;

        [SerializeField, Title("Dropping")]
        private Vector3 _dropForce;

        [SerializeField, Range(0f, 1f)]
        private float _dropTorque = 0.25f;
        
        [SerializeField, SpaceArea]
#if UNITY_EDITOR
        [EditorButton(nameof(GetOffsetsFromTransforms), null, ButtonActivityType.OnPlayMode)]
        [EditorButton(nameof(RefreshVisuals), null, ButtonActivityType.OnPlayMode)]
#endif
        private WieldableCarrySettings _wieldableSettings;

        public override Sprite Icon => _icon;
        public CarryablePickup Pickup => _pickup;
        public Vector3 DropForce => _dropForce;
        public float DropTorque => _dropTorque;
        public int MaxCarryCount => _wieldableSettings.Offsets.Length;
        public WieldableCarrySettings WieldableSettings => _wieldableSettings;

        #region Editor
#if UNITY_EDITOR
        [Conditional("UNITY_EDITOR")]
        private void GetOffsetsFromTransforms()
        {
            var wieldableCarryable = GameMode.Instance.LocalPlayer.GetCC<IWieldablesControllerCC>()
                .gameObject.GetComponentInChildren<WieldableCarryable>();

            Undo.RecordObject(this, "carryable");
            
            var offsets = _wieldableSettings.Offsets;
            for (int i = 0; i < wieldableCarryable.CarryCount; i++)
            {
                var (position, rotation) = wieldableCarryable.GetOffsetsAtIndex(i);
                offsets[i].PositionOffset = position;
                offsets[i].RotationOffset = rotation;
            }
            
            EditorUtility.SetDirty(this);
        }
        
        [Conditional("UNITY_EDITOR")]
        private void RefreshVisuals()
        { 
            var wieldableCarryable = GameMode.Instance.LocalPlayer.GetCC<IWieldablesControllerCC>()
                .gameObject.GetComponentInChildren<WieldableCarryable>();
            
            wieldableCarryable.RefreshVisuals();
        }
#endif
        #endregion
    }
    
    [Serializable]
    public sealed class WieldableCarrySettings
    {
        [Serializable]
        public struct Offset
        {
            public Vector3 PositionOffset;
            public Vector3 RotationOffset;
        }
        
        public enum Socket
        {
            RightHand,
            LeftHand
        }
        
        [SerializeField, NotNull]
        [Tooltip("The animation override clips.")]
        private AnimatorOverrideController _animator;

        [SerializeField, NotNull]
        private MotionProfile _motion;
        
        [SerializeField]
        private Socket _socket;

        [SerializeField]
        private Vector3 _positionOffset;

        [SerializeField]
        private Vector3 _rotationOffset;
        
        [SerializeField, SpaceArea]
        [ReorderableList(ListStyle.Lined, ElementLabel = "Offset")]
        private Offset[] _offsets;
        
        public AnimatorOverrideController Animator => _animator;
        public MotionProfile Motion => _motion;
        public Socket TargetSocket => _socket;
        public Vector3 PositionOffset => _positionOffset;
        public Vector3 RotationOffset => _rotationOffset;
        public Offset[] Offsets => _offsets;
    }
}