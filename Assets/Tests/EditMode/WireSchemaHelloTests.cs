using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-061 — the IPC version gate: the backend's `hello` frame and the decision taken on it.
    ///
    /// `hello` is root-tagged, so these tests play IPCClient.Dispatch's part by hand (read the
    /// map header, match "type", pass n-1 as remainingPairs), same discipline as
    /// IPCMessagesParityTests.
    ///
    /// Declared gap, same shape as IPCFrameGoldenTests': Dispatch and HandleHello need a live
    /// socket and a SessionEndHandler, so what they do with a mismatch (LogError + synthetic
    /// session_ended + drop later frames) is NOT covered here. The decision itself lives in
    /// WireSchema precisely so it is reachable without either — that part is covered below, and
    /// the end-to-end path is verified by running a client against a deliberately bumped
    /// constant (see the ADR's verification section).
    /// </summary>
    [TestFixture]
    public class WireSchemaHelloTests
    {
        /// <summary>Encodes a `hello` frame body and hands back a reader positioned exactly where
        /// HelloMsg.Parse expects it — past the header and the "type" pair.</summary>
        private static HelloMsg ParseHello(params (string key, uint value)[] extraPairs)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(1 + extraPairs.Length);
            w.WriteString("type"); w.WriteString("hello");
            foreach (var (key, value) in extraPairs)
            {
                w.WriteString(key); w.WriteInt(value);
            }

            var r = new MsgPackReader(w.ToArray());
            int n = r.ReadMapHeader();
            Assert.IsTrue(MsgPackReader.Is(r.ReadKey(), "type"));
            r.ReadString();
            return HelloMsg.Parse(r, n - 1);
        }

        [Test]
        public void HelloMsg_SchemaVersionDecodesThroughTaggedEnvelope()
        {
            var msg = ParseHello(("schema_version", 26u));
            Assert.AreEqual(26u, msg.schemaVersion);
        }

        [Test]
        public void HelloMsg_MissingSchemaVersionDefaultsToZero_WhichIsIncompatible()
        {
            // The one place in the decoder where the silent default must FAIL the gate rather
            // than degrade: a hello without the key cannot be allowed to read as a match.
            var msg = ParseHello();
            Assert.AreEqual(0u, msg.schemaVersion);
            Assert.IsFalse(WireSchema.IsCompatible(msg.schemaVersion));
        }

        [Test]
        public void HelloMsg_UnknownExtraKeysAreSkipped()
        {
            // The additive contract still holds for hello itself: a future backend may add
            // fields here without this client failing to read the version.
            var msg = ParseHello(("schema_version", 26u), ("future_field", 999u));
            Assert.AreEqual(26u, msg.schemaVersion);
        }

        [Test]
        public void WireSchema_ExactMatchIsCompatible()
        {
            Assert.IsTrue(WireSchema.IsCompatible(WireSchema.Expected));
            // Exact equality, not a floor — deployment is lockstep (ADR-047), so even a NEWER
            // backend is a desynced build, not a peer to tolerate.
            Assert.IsFalse(WireSchema.IsCompatible(WireSchema.Expected + 1));
            Assert.IsFalse(WireSchema.IsCompatible(WireSchema.Expected - 1));
        }

        [Test]
        public void WireSchema_MismatchReasonNamesBothVersions()
        {
            // SessionEndHandler.ReadReason surfaces this string; it has to say which build to
            // rebuild, so both numbers must appear.
            string reason = WireSchema.MismatchReason(99u);
            StringAssert.Contains("99", reason);
            StringAssert.Contains(WireSchema.Expected.ToString(), reason);
            StringAssert.StartsWith("wire_schema_mismatch", reason);
        }
    }
}
