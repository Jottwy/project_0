using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Standard Recoil-Manager")]
    public class FirearmStandardRecoilManager : FirearmRecoilManagerBehaviour
    {
        [SerializeField, Title("Recoil")]
        private AnimationCurve _recoilStrengthOverTime = AnimationCurve.Constant(0f, 1f, 1f);
        
        [NewLabel("Head Recoil (Camera)"), SpaceArea]
        [ReferencePicker(typeof(IFirearmRecoil), TypeGrouping.ByFlatName)]
        [SerializeReference, ReorderableList(elementLabel: "Recoil")]
        private IFirearmRecoil[] _headRecoilSettings = Array.Empty<IFirearmRecoil>();
        
        [NewLabel("Hands Recoil (Wieldable)")]
        [ReferencePicker(typeof(IFirearmRecoil), TypeGrouping.ByFlatName)]
        [SerializeReference, ReorderableList(elementLabel: "Recoil")]
        private IFirearmRecoil[] _handsRecoilSettings = Array.Empty<IFirearmRecoil>();
        
        public override void ApplyRecoil(float accuracy, float recoilProgress, bool isAiming)
        {
            float recoilMultiplier = Firearm.Trigger.TriggerCharge * RecoilMultiplier * _recoilStrengthOverTime.Evaluate(recoilProgress);

            foreach (var recoil in _headRecoilSettings)
                recoil.ApplyRecoil(recoilMultiplier, recoilProgress, isAiming);
            
            foreach (var recoil in _handsRecoilSettings)
                recoil.ApplyRecoil(recoilMultiplier, recoilProgress, isAiming);
        }
        
        protected override void OnEnable()
        {
            base.OnEnable();
            InitializeRecoil();
        }

        private void InitializeRecoil()
        {
            var motion = Wieldable.Motion;
            var character = Wieldable.Character;
            
            var headMotionMixer = motion.HeadComponents.Mixer;
            foreach (var handsRecoil in _headRecoilSettings)
                handsRecoil.Initialize(headMotionMixer, character);
            
            var handsMotionMixer = motion.HandsComponents.Mixer;
            foreach (var handsRecoil in _handsRecoilSettings)
                handsRecoil.Initialize(handsMotionMixer, character);
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying && Wieldable != null)
                InitializeRecoil();
        }
#endif
        #endregion
    }
}