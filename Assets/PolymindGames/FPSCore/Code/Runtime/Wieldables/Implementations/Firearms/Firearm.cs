using PolymindGames.Options;
using UnityEngine.Events;
using UnityEngine;
using System.Linq;
using System;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents a firearm that can be wielded, fired, aimed and reloaded by the player.
    /// </summary>
    [AddComponentMenu("Polymind Games/Wieldables/Firearms/Firearm")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class Firearm : Wieldable, IFirearm, IUseInputHandler, IAimInputHandler, IReloadInputHandler
    {
        [SerializeField, Range(0f, 1f), NewLabel("Hip Fire Accuracy")]
        [Tooltip("Modifier for accuracy when firing from the hip.")]
        private float _hipAccuracyMod = 0.9f;

        private bool _requiresEject;
        private bool _isShooting;

        private const float RecoilRecoveryMultiplier = 5f;
        private const float ActionCooldown = 0.3f;

        protected override void OnCharacterChanged(ICharacter character)
        {
            if (character == null || !character.TryGetCC(out _characterAccuracy))
                _characterAccuracy = new NullAccuracyHandler();
        }

        private void Shoot()
        {
            // Check if the firearm has enough ammo in the magazine.
            bool hasEnoughAmmo = GameplayOptions.Instance.InfiniteMagazineAmmo
                                 || _magazine.TryUseAmmo(_fireSystem.AmmoPerShot);

            if (!hasEnoughAmmo)
                return;

            _isShooting = true;
            _fireSystem.Fire(Accuracy, _impactEffect);
            _shootInaccuracy += _aimHandler.IsAiming ? _recoilManager.AimAccuracyKick : _recoilManager.HipfireAccuracyKick;

            _recoilProgressUpdateTimer = Time.fixedTime + _recoilManager.RecoilRecoveryDelay;
            _recoilManager.ApplyRecoil(Accuracy, _recoilProgress, IsAiming);
            _recoilProgress = Mathf.Clamp01(_recoilProgress + 1 / (float)Mathf.Clamp(_magazine.Capacity, 0, 30));

            _barrelEffect.TriggerFireEffect();

            if (GameplayOptions.Instance.ManualShellEjection && _shellEjector.EjectionDuration > 0.01f)
                _requiresEject = true;
            else
                _shellEjector.Eject();

            ReloadBlocker.AddDurationBlocker(ActionCooldown);
        }

        private void Start()
        {
            AimBlocker.OnBlocked += ForceEndAiming;
            UseBlocker.OnBlocked += ForceReleaseTrigger;
            ReloadBlocker.OnBlocked += ForceCancelReload;

            void ForceReleaseTrigger() => _trigger.ReleaseTrigger();
            void ForceEndAiming() => EndAim();
            void ForceCancelReload() => EndReload();
        }

        private void FixedUpdate()
        {
            float deltaTime = Time.fixedDeltaTime;
            float currentTime = Time.fixedTime;
            UpdateAccuracy(deltaTime);

            if (_recoilProgressUpdateTimer < currentTime)
            {
                UpdateRecoilRecovery(deltaTime);

                if (_isShooting)
                {
                    _barrelEffect.TriggerFireStopEffect();
                    _isShooting = false;
                }
            }
        }
        
        #region Components
        private UnityAction[] _componentChangedCallbacks;

        private static readonly int _componentTypeCount = Enum.GetValues(typeof(FirearmComponentType)).Cast<FirearmComponentType>().ToArray().Length;
        
        /// <inheritdoc/>
        public void AddChangedListener(FirearmComponentType type, UnityAction callback)
        {
            _componentChangedCallbacks ??= new UnityAction[_componentTypeCount];

            int index = (int)type;
            _componentChangedCallbacks[index] += callback;
        }

        /// <inheritdoc/>
        public void RemoveChangedListener(FirearmComponentType type, UnityAction callback)
        {
            _componentChangedCallbacks ??= new UnityAction[_componentTypeCount];

            int index = (int)type;
            _componentChangedCallbacks[index] -= callback;
        }

        private void RaiseComponentChangedEvent(FirearmComponentType type)
            => _componentChangedCallbacks?[(int)type]?.Invoke();

        private IFirearmAimHandler _aimHandler = DefaultFirearmAimHandler.Instance;
        
        /// <inheritdoc/>
        public IFirearmAimHandler AimHandler
        {
            get => _aimHandler;
            set
            {
                if (value != _aimHandler)
                {
                    if (_aimHandler.IsAiming)
                        _aimHandler.StopAiming();

                    _aimHandler.Detach();

                    _aimHandler = value ?? DefaultFirearmAimHandler.Instance;
                    _aimHandler.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.AimHandler);
                }
            }
        }

        private IFirearmTrigger _trigger = DefaultFirearmTrigger.Instance;
        
        /// <inheritdoc/>
        public IFirearmTrigger Trigger
        {
            get => _trigger;
            set
            {
                if (_trigger != value)
                {
                    bool wasHeld = _trigger.IsTriggerHeld;
                    _trigger.Shoot -= Shoot;
                    _trigger.Detach();

                    _trigger = value ?? DefaultFirearmTrigger.Instance;

                    _trigger.Shoot += Shoot;
                    _trigger.Attach();

                    if (wasHeld)
                        _trigger.HoldTrigger();

                    RaiseComponentChangedEvent(FirearmComponentType.Trigger);
                }
            }
        }

        private IFirearmFireSystem _fireSystem = DefaultFirearmFireSystem.Instance;
        
        /// <inheritdoc/>
        public IFirearmFireSystem FireSystem
        {
            get => _fireSystem;
            set
            {
                if (value != _fireSystem)
                {
                    _fireSystem.Detach();
                    _fireSystem = value ?? DefaultFirearmFireSystem.Instance;
                    _fireSystem.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.FireSystem);
                }
            }
        }

        private IFirearmAmmoProvider _ammoProvider = DefaultFirearmAmmoProvider.Instance;
        
        /// <inheritdoc/>
        public IFirearmAmmoProvider AmmoProvider
        {
            get => _ammoProvider;
            set
            {
                if (value != _ammoProvider)
                {
                    bool wasNull = _ammoProvider is DefaultFirearmAmmoProvider;

                    if (!wasNull)
                    {
                        int added = _ammoProvider.AddAmmo(_magazine.CurrentAmmoCount);
                        _magazine.ForceSetAmmo(_magazine.CurrentAmmoCount - added);
                        _shellEjector.ResetShells();
                    }

                    _ammoProvider.Detach();
                    _ammoProvider = value ?? DefaultFirearmAmmoProvider.Instance;
                    _ammoProvider.Attach();
                    
                    if (!wasNull)
                        Reload(WieldableInputPhase.Start);

                    RaiseComponentChangedEvent(FirearmComponentType.AmmoProvider);
                }
            }
        }

        private IFirearmReloadableMagazine _magazine = DefaultReloadableFirearmMagazine.Instance;
        
        /// <inheritdoc/>
        public IFirearmReloadableMagazine ReloadableMagazine
        {
            get => _magazine;
            set
            {
                if (value != _magazine)
                {
                    int prevAmmoCount = ReloadableMagazine.CurrentAmmoCount;
                    _magazine.Detach();
                    _magazine = value ?? DefaultReloadableFirearmMagazine.Instance;
                    _magazine.ForceSetAmmo(prevAmmoCount);
                    _magazine.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.ReloadableMagazine);
                }
            }
        }

        private IFirearmRecoilManager _recoilManager = DefaultFirearmRecoilManager.Instance;
        
        /// <inheritdoc/>
        public IFirearmRecoilManager RecoilManager
        {
            get => _recoilManager;
            set
            {
                if (value != _recoilManager)
                {
                    _recoilManager.Detach();
                    _recoilManager = value ?? DefaultFirearmRecoilManager.Instance;
                    _recoilManager.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.RecoilManager);
                }
            }
        }

        private IFirearmImpactEffect _impactEffect = DefaultFirearmImpactEffect.Instance;
        
        /// <inheritdoc/>
        public IFirearmImpactEffect ImpactEffect
        {
            get => _impactEffect;
            set
            {
                if (value != _impactEffect)
                {
                    _impactEffect.Detach();
                    _impactEffect = value ?? DefaultFirearmImpactEffect.Instance;
                    _impactEffect.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.ImpactEffect);
                }
            }
        }

        private IFirearmShellEjector _shellEjector = DefaultFirearmShellEjector.Instance;
        
        /// <inheritdoc/>
        public IFirearmShellEjector ShellEjector
        {
            get => _shellEjector;
            set
            {
                if (value != _shellEjector)
                {
                    _shellEjector.Detach();
                    _shellEjector = value ?? DefaultFirearmShellEjector.Instance;
                    _shellEjector.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.ShellEjector);
                }
            }
        }

        private IFirearmDryFireFeedback _dryFireFeedback = DefaultFirearmDryFireFeedback.Instance;
        
        /// <inheritdoc/>
        public IFirearmDryFireFeedback DryFireFeedback
        {
            get => _dryFireFeedback;
            set
            {
                if (value != _dryFireFeedback)
                {
                    _dryFireFeedback.Detach();
                    _dryFireFeedback = value ?? DefaultFirearmDryFireFeedback.Instance;
                    _dryFireFeedback.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.DryFireFeedback);
                }
            }
        }

        private IFirearmBarrelEffect _barrelEffect = DefaultFirearmBarrelEffect.Instance;
        
        /// <inheritdoc/>
        public IFirearmBarrelEffect BarrelEffect
        {
            get => _barrelEffect;
            set
            {
                if (value != _barrelEffect)
                {
                    _barrelEffect.Detach();
                    _barrelEffect = value ?? DefaultFirearmBarrelEffect.Instance;
                    _barrelEffect.Attach();

                    RaiseComponentChangedEvent(FirearmComponentType.BarrelEffect);
                }
            }
        }
        #endregion

        #region Input Handling
        
        /// <inheritdoc/>
        public ActionBlockHandler UseBlocker { get; } = new();
        
        /// <inheritdoc/>
        public ActionBlockHandler AimBlocker { get; } = new();
        
        /// <inheritdoc/>
        public ActionBlockHandler ReloadBlocker { get; } = new();
        
        /// <inheritdoc/>
        public bool IsReloading => _magazine.IsReloading;
        
        /// <inheritdoc/>
        public bool IsUsing => _trigger.IsTriggerHeld;
        
        /// <inheritdoc/>
        public bool IsAiming => _aimHandler.IsAiming;

        /// <inheritdoc/>
        public bool Use(WieldableInputPhase inputPhase)
        {
            return inputPhase switch
            {
                WieldableInputPhase.Start => StartUse(),
                WieldableInputPhase.Hold => HoldUse(),
                WieldableInputPhase.End => EndUse(),
                _ => false
            };
        }
        
        /// <inheritdoc/>
        public bool Aim(WieldableInputPhase inputPhase) => inputPhase switch
        {
            WieldableInputPhase.Start => StartAim(),
            WieldableInputPhase.End => EndAim(),
            _ => false
        };

        /// <inheritdoc/>
        public bool Reload(WieldableInputPhase inputPhase) => inputPhase switch
        {
            WieldableInputPhase.Start => StartReload(),
            WieldableInputPhase.End => EndReload(),
            _ => false
        };

        private bool StartUse()
        {
            // Use the input to manually eject the shell (only if manual eject is required).
            if (_requiresEject)
            {
                _shellEjector.Eject();
                UseBlocker.AddDurationBlocker(_shellEjector.EjectionDuration);
                _requiresEject = false;
                return true;
            }

            // Use the input to cancel the reload if active.
            if (IsReloading && EndReload())
                return true;

            // If the magazine is empty, try to reload or dry fire.
            if (_magazine.IsMagazineEmpty() && !IsReloading)
            {
                if (GameplayOptions.Instance.AutoReloadOnDry && StartReload())
                    return true;

                _dryFireFeedback.TriggerDryFireFeedback();
                return true;
            }

            return HoldUse();
        }

        private bool HoldUse()
        {
            if (!UseBlocker.IsBlocked && !IsReloading)
            {
                _trigger.HoldTrigger();
                return _trigger.IsTriggerHeld;
            }
                
            _trigger.ReleaseTrigger();
            return false;
        }

        private bool EndUse()
        {
            _trigger.ReleaseTrigger();
            return true;
        }

        private bool StartAim()
        {
            if (AimBlocker.IsBlocked)
                return false;

            if (IsReloading && !GameplayOptions.Instance.CanAimWhileReloading)
                return false;

            return _aimHandler.StartAiming();
        }

        private bool EndAim()
        {
            if (_aimHandler.StopAiming())
            {
                AimBlocker.AddDurationBlocker(ActionCooldown);
                return true;
            }

            return false;
        }

        private bool StartReload()
        {
            if (ReloadBlocker.IsBlocked)
                return false;
            
            var ammo = GameplayOptions.Instance.InfiniteStorageAmmo ? DefaultFirearmAmmoProvider.Instance : AmmoProvider;
            if (_magazine.TryBeginReload(ammo))
            {
                UseBlocker.AddDurationBlocker(ActionCooldown);

                if (IsAiming && !GameplayOptions.Instance.CanAimWhileReloading)
                    EndAim();

                return true;
            }

            return false;
        }

        private bool EndReload()
        {
            if (!_magazine.IsMagazineEmpty() && _magazine.TryCancelReload(AmmoProvider, out var endDuration))
            {
                if (endDuration == 0f)
                    return false;
                    
                UseBlocker.AddDurationBlocker(endDuration + 0.05f);
                return true;
            }

            return false;
        }
        #endregion

        #region Accuracy
        private IAccuracyHandlerCC _characterAccuracy;
        private float _recoilProgressUpdateTimer;
        private float _shootInaccuracy;
        private float _baseAccuracy = 1f;
        private float _recoilProgress;

        /// <inheritdoc/>
        public override bool IsCrosshairActive()
            => !UseBlocker.IsBlocked && !ReloadableMagazine.IsMagazineEmpty() && !IsReloading;

        private void UpdateAccuracy(float deltaTime)
        {
            _baseAccuracy = _characterAccuracy.GetAccuracyMod() * (AimHandler.IsAiming ? AimHandler.FireAccuracyModifier : _hipAccuracyMod);
            float targetAccuracy = Mathf.Clamp01(_baseAccuracy - _shootInaccuracy);

            float accuracyRecoverDelta = deltaTime * (AimHandler.IsAiming ? _recoilManager.AimAccuracyRecoveryRate : _recoilManager.HipfireAccuracyRecoveryRate);
            _shootInaccuracy = Mathf.Clamp01(_shootInaccuracy - accuracyRecoverDelta);

            Accuracy = targetAccuracy;
        }

        private void UpdateRecoilRecovery(float deltaTime)
        {
            float recoverDelta = deltaTime * _recoilManager.RecoilRecoveryRate;
            _recoilProgress = Mathf.Clamp01(_recoilProgress - Mathf.Max(recoverDelta * _recoilProgress * RecoilRecoveryMultiplier, recoverDelta));
        }
        #endregion
        
        #region Editor
#if UNITY_EDITOR
        protected override void DrawDebugGUI()
        {
            using (new UnityEditor.EditorGUI.IndentLevelScope())
            {
                GUILayout.Label($"Is Aiming: {AimHandler.IsAiming}");
                GUILayout.Label($"Is Reloading: {ReloadableMagazine.IsReloading}");
                GUILayout.Label($"Ammo In Magazine: {ReloadableMagazine.CurrentAmmoCount}");
                GUILayout.Label($"Is Magazine Empty: {ReloadableMagazine.IsMagazineEmpty()}");
                GUILayout.Label($"Is Magazine Full: {ReloadableMagazine.IsMagazineFull()}");
                GUILayout.Label($"Is Trigger Held: {Trigger.IsTriggerHeld}");
                GUILayout.Label($"Accuracy: {Math.Round(Accuracy, 2)}");
                GUILayout.Label($"Recoil Progress: {_recoilProgress}");
                GUILayout.Label($"Speed Multiplier: {Math.Round(SpeedModifier.EvaluateValue(), 2)}");
                GUILayout.Label($"Is Use Input Blocked: {UseBlocker.IsBlocked}");
                GUILayout.Label($"Is Aim Input Blocked: {AimBlocker.IsBlocked}");
                GUILayout.Label($"Is Reload Input Blocked: {ReloadBlocker.IsBlocked}");
            }
        }
#endif
        #endregion
    }
}