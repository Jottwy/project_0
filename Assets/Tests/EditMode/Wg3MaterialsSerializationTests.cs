using System;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Del 08-29 al 09-13 <c>[System.Serializable]</c> decoró por error a <c>Wg3HumBatch</c>: se insertó
    /// una clase entre el atributo y <see cref="Wg3Materials"/>. Nadie lo vio porque el mundo no salía
    /// en rosa —<c>Wg3ChunkStreamer</c> caía a los materiales de WG2—, pero el juego llevaba dos semanas
    /// sin los materiales de WG3 que el prefab <c>GridTestWorld</c> tiene asignados.
    /// </summary>
    [TestFixture]
    public class Wg3MaterialsSerializationTests
    {
        [Test]
        public void Wg3MaterialsIsSerializableSoScenesAndPrefabsKeepTheirAssignment()
        {
            Assert.IsTrue(Attribute.IsDefined(typeof(Wg3Materials), typeof(SerializableAttribute)),
                "sin [Serializable], GridTestWorld.wg3Materials y Wg3ChunkStreamer.materials no se cargan");
        }
    }
}
