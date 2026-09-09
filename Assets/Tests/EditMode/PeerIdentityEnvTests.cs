using System.Collections.Generic;
using BackroomsSurvival.Connectivity;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-136 D1/D2 — las dos variables de identidad del backend. Poca lógica y un solo sitio
    /// donde equivocarse en silencio: poner un 0 (el backend lo leería igual, pero «sin identidad»
    /// dejaría de ser una ausencia) o formatearlo con la cultura de la máquina.
    /// </summary>
    [TestFixture]
    public class PeerIdentityEnvTests
    {
        [Test]
        public void Host_pone_su_identidad_y_nadie_le_invito()
        {
            var env = new Dictionary<string, string>();
            PeerIdentityEnv.Apply(env, 76561198000000001UL, 0UL);

            Assert.AreEqual("76561198000000001", env[PeerIdentityEnv.IdentityKey]);
            Assert.IsFalse(env.ContainsKey(PeerIdentityEnv.InvitedByKey), "0 = nadie, y no se pone");
        }

        [Test]
        public void Invitado_pone_las_dos()
        {
            var env = new Dictionary<string, string>();
            PeerIdentityEnv.Apply(env, 76561198000000002UL, 76561198000000001UL);

            Assert.AreEqual("76561198000000002", env[PeerIdentityEnv.IdentityKey]);
            Assert.AreEqual("76561198000000001", env[PeerIdentityEnv.InvitedByKey]);
        }

        /// Build sin Steam: no hay identidad, y el backend tiene que ver la variable AUSENTE.
        [Test]
        public void Sin_identidad_no_se_pone_nada()
        {
            var env = new Dictionary<string, string> { ["NET_NAME"] = "Joel" };
            PeerIdentityEnv.Apply(env, 0UL, 0UL);

            Assert.AreEqual(1, env.Count);
            Assert.IsFalse(env.ContainsKey(PeerIdentityEnv.IdentityKey));
            Assert.IsFalse(env.ContainsKey(PeerIdentityEnv.InvitedByKey));
        }

        /// Un `SteamId` usa los 64 bits; un formato con separadores o notación científica
        /// dejaría al backend con un `bad_config` y la invitación caería al reparto normal.
        [Test]
        public void El_formato_es_decimal_plano_hasta_el_ultimo_bit()
        {
            var env = new Dictionary<string, string>();
            PeerIdentityEnv.Apply(env, ulong.MaxValue, 0UL);

            Assert.AreEqual("18446744073709551615", env[PeerIdentityEnv.IdentityKey]);
        }
    }
}
