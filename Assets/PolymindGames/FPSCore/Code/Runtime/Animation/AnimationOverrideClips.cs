using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames
{
    [Serializable]
    public sealed class AnimationOverrideClips
    {
        [Serializable]
        public struct AnimationClipPair
        {
            public AnimationClip Original;
            public AnimationClip Override;
        }

        [SerializeField]
        private RuntimeAnimatorController _controller;

        [SerializeField]
        private AnimationClipPair[] _clips;

        [SerializeField]
        private AnimatorParameterTrigger[] _defaultParameters;

        private AnimatorOverrideController _overrideController;

        
        public RuntimeAnimatorController Controller 
        {
            get => _controller;
            set => _controller = value;
        }

        public AnimationClipPair[] Clips => _clips;

        public AnimatorOverrideController OverrideController
        {
            get
            {
                if (_overrideController == null)
                {
                    _overrideController = new AnimatorOverrideController(_controller);
                    var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();

                    for (int i = 0; i < _clips.Length; i++)
                        overrides.Add(new KeyValuePair<AnimationClip, AnimationClip>(_clips[i].Original, _clips[i].Override));

                    _overrideController.ApplyOverrides(overrides);
                }

                return _overrideController;
            }
        }

        public AnimatorParameterTrigger[] DefaultParameters => _defaultParameters;
    }
}