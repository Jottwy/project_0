using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// Cabeceo absoluto de la cabeza de los proxies (pasada 1a del plan de animación 3P, 2026-09-14).
    ///
    /// Se prueba la cuenta sobre una cadena de Transforms montada a mano, con la postura del agachado
    /// medida en el arnés (pecho 77° hacia delante, cabeza 70°). Queda fuera, y se declara: la
    /// elección del Animator que pinta y la calibración en Awake sobre el prefab real — eso lo cubre la
    /// captura del arnés de poses en Play.
    /// </summary>
    public sealed class ProxyHeadPitchSolverTests
    {
        private GameObject _root;
        private Transform _chest, _neck, _head;
        private Vector3 _headLook;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("root");
            _chest = Bone("chest", _root.transform, new Vector3(0f, 1.3f, 0f));
            _neck = Bone("neck", _chest, new Vector3(0f, 0.2f, 0f));
            _head = Bone("head", _neck, new Vector3(0f, 0.1f, 0f));
            // Ejes del hueso autorados «raros» a propósito, como en un rig importado: la medida no puede
            // depender de que el hueso mire hacia +Z.
            _head.localRotation = Quaternion.Euler(90f, 0f, -90f);
            _headLook = ProxyHeadPitchSolver.LocalLookAxis(_head.rotation, _root.transform.forward);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        private static Transform Bone(string name, Transform parent, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }

        private float HeadPitch() => ProxyHeadPitchSolver.MeasurePitch(
            _head.rotation, _headLook, _root.transform.forward, _root.transform.right);

        private void CrouchPose()
        {
            Vector3 right = _root.transform.right;
            _chest.rotation = Quaternion.AngleAxis(77f, right) * _chest.rotation;
            _head.rotation = Quaternion.AngleAxis(-7f, right) * _head.rotation;
        }

        [Test]
        public void BindPoseLooksStraightAhead()
        {
            Assert.AreEqual(0f, HeadPitch(), 0.01f);
        }

        [Test]
        public void PositivePitchIsLookingDown()
        {
            _head.rotation = Quaternion.AngleAxis(30f, _root.transform.right) * _head.rotation;
            Assert.AreEqual(30f, HeadPitch(), 0.01f);
        }

        [Test]
        public void TheCrouchPoseLooksAtTheFloorBeforeAiming()
        {
            CrouchPose();
            Assert.AreEqual(70f, HeadPitch(), 0.5f);
        }

        [TestCase(0f)]
        [TestCase(-60f)]
        [TestCase(60f)]
        [TestCase(89f)]
        public void CrouchedHeadEndsAtTheNetworkPitch(float target)
        {
            CrouchPose();
            ProxyHeadPitchSolver.AimHead(_root.transform, _chest, _neck, _head, _headLook, target);
            Assert.AreEqual(target, HeadPitch(), 0.5f);
        }

        [Test]
        public void WorksWithTheAvatarTurnedAndTheHeadRolled()
        {
            _root.transform.rotation = Quaternion.Euler(0f, 137f, 0f);
            CrouchPose();
            _head.rotation = Quaternion.AngleAxis(-14f, _root.transform.forward) * _head.rotation;
            ProxyHeadPitchSolver.AimHead(_root.transform, _chest, _neck, _head, _headLook, 0f);
            Assert.AreEqual(0f, HeadPitch(), 0.5f);
        }

        [Test]
        public void MissingChestAndNeckStillAimTheHead()
        {
            CrouchPose();
            ProxyHeadPitchSolver.AimHead(_root.transform, null, null, _head, _headLook, 0f);
            Assert.AreEqual(0f, HeadPitch(), 0.5f);
        }

        [TestCase(0f)]
        [TestCase(20f)]
        [TestCase(-70f)]
        [TestCase(-150f)]
        public void TheSplitAddsUpAndTheChestIsCapped(float error)
        {
            var s = ProxyHeadPitchSolver.Distribute(error);
            Assert.AreEqual(error, s.Chest + s.Neck + s.Head, 0.001f);
            Assert.LessOrEqual(Mathf.Abs(s.Chest), ProxyHeadPitchSolver.ChestCapDegrees + 0.001f);
        }

        [Test]
        public void TheChestTakesPartOfTheLookSoTheNeckIsNotAHinge()
        {
            var s = ProxyHeadPitchSolver.Distribute(-60f);
            Assert.Less(s.Chest, 0f);
            Assert.Greater(Mathf.Abs(s.Head), Mathf.Abs(s.Neck));
        }
    }
}
