using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-028 Fase D fix #2b — freezes a corpse's ragdoll bones (isKinematic = true) once they've
    /// settled, bounding how far the visual body can drift from the frozen death_pos. Paired with
    /// the server-side CORPSE_LOOT_MAX_DISTANCE widen (Fase D fix #2a, corpse.rs): the combination
    /// gives normal settling headroom (a) while capping the pathological case — a ragdoll rolling
    /// down a slope or a flight of stairs indefinitely — at the settle timeout (b), rather than
    /// requiring an unbounded server-side radius to compensate.
    ///
    /// Settle heuristic: every FixedUpdate, track the FASTEST bone. Once every bone stays below
    /// _velocityThreshold for _settleDuration seconds continuously, freeze. A hard _maxDuration cap
    /// freezes regardless (a bone stuck oscillating just under/over the threshold, or genuinely
    /// still sliding after a long fall, must not drift forever).
    ///
    /// Purely cosmetic — freezing does not touch the loot interaction collider (a separate
    /// component on the Pelvis bone, CorpseSpawner.WireLoot) or any server state.
    /// </summary>
    public sealed class CorpseRagdollFreezer : MonoBehaviour
    {
        [SerializeField] private float _velocityThreshold = 0.08f;
        [SerializeField] private float _settleDuration = 1.75f;
        [SerializeField] private float _maxDuration = 5f;

        private Rigidbody[] _bones;
        private float _belowThresholdSince = -1f;
        private float _spawnTime;
        private bool _frozen;

        public void Initialize(Rigidbody[] bones)
        {
            _bones = bones;
            _spawnTime = Time.time;
        }

        private void FixedUpdate()
        {
            if (_frozen || _bones == null || _bones.Length == 0)
                return;

            if (Time.time - _spawnTime >= _maxDuration)
            {
                Freeze();
                return;
            }

            float maxSpeed = 0f;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null)
                    continue;
                float speed = _bones[i].linearVelocity.sqrMagnitude;
                if (speed > maxSpeed)
                    maxSpeed = speed;
            }
            maxSpeed = Mathf.Sqrt(maxSpeed);

            if (maxSpeed <= _velocityThreshold)
            {
                if (_belowThresholdSince < 0f)
                    _belowThresholdSince = Time.time;
                else if (Time.time - _belowThresholdSince >= _settleDuration)
                    Freeze();
            }
            else
            {
                _belowThresholdSince = -1f;
            }
        }

        private void Freeze()
        {
            _frozen = true;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null)
                    _bones[i].isKinematic = true;
            }
        }
    }
}
