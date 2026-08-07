using System;
using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// C6 de la ruta IPC sin boxing — red de seguridad del lado SALIDA, y criterio de aceptación
    /// explícito de C7 (reescritura de <see cref="MsgPackWriter"/> sin allocs): los bytes que el
    /// writer emite tienen que salir **idénticos** antes y después. Aquí se congelan.
    ///
    /// Cobertura en dos ejes:
    ///  (a) FORMAS DE FRAME reales — una por cada shape distinta que emite IPCClient: el envelope
    ///      de entrada, el de acción con `data` map, el de acción con `data` NIL (byte distinto,
    ///      0xc0 vs 0x80 — la distinción que documenta SendActionFrame), request_chunk, ui_event,
    ///      y los payloads anidados más anchos (report_death_loot, pvp_hit_candidate,
    ///      set_stp_items). Incluye `player_input` con sus 19 campos, que es el único frame que
    ///      **cruza la frontera fixmap→map16** (0x80|n solo llega a n=15) — exactamente el borde
    ///      que el comentario de SendPlayerInput señala como fuente del bug de `crouch`.
    ///  (b) RAMAS DE CODIFICACIÓN — cada rama de WriteInt/WriteFloat/WriteString/WriteMapHeader/
    ///      WriteArrayHeader, con sus valores frontera.
    ///
    /// Los valores se generaron ejecutando el MsgPack.cs REAL headless (csc + dotnet de Unity,
    /// sin editor) y se verificaron a mano contra la spec msgpack — p.ej. `de0010` = map16 de 16
    /// entradas en player_input, `d3ffffffff7fffffff` = int64 −2147483649, `ca40490fd0` = 3.14159f.
    ///
    /// ALCANCE HONESTO: esto congela la salida de MsgPackWriter, que es lo que C7 toca. NO prueba
    /// que los métodos Send* de IPCClient llamen al writer en el orden correcto — sus cuerpos
    /// siguen inline detrás de un SendFrame privado que exige socket. Extraerlos a un seam
    /// testeable se evaluó y se descartó: mover 26 cuerpos de método a producción es un refactor
    /// mayor que el barrido que lo motiva (regla dura #3), y no lo pide C7, que no toca ni una
    /// línea de esos cuerpos. Queda declarado como hueco conocido.
    /// </summary>
    [TestFixture]
    public class IPCFrameGoldenTests
    {
        /// <summary>FNV-1a 64 — elegido sobre un hash cripto para no depender de
        /// System.Security.Cryptography y que la constante sea reproducible a mano.</summary>
        private static ulong Fnv1a64(byte[] data)
        {
            ulong h = 14695981039346656037UL;
            foreach (var b in data) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        private static string Hex(byte[] data)
        {
            var sb = new System.Text.StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Compara contra el hex congelado. Al fallar imprime el hex real, para que
        /// actualizar un golden legítimamente cambiado sea copiar-pegar y no arqueología.</summary>
        private static void AssertGolden(string name, MsgPackWriter w, int expectedLength, string expectedHex)
        {
            byte[] actual = w.ToArray();
            Assert.AreEqual(expectedLength, actual.Length,
                $"[{name}] cambió la LONGITUD del frame. Real: {Hex(actual)}");
            Assert.AreEqual(expectedHex, Hex(actual),
                $"[{name}] cambiaron los BYTES del frame — el writer dejó de ser byte-idéntico.");
        }

        /// <summary>Variante por hash para los goldens grandes, donde el hex completo sería
        /// ilegible en el fuente.</summary>
        private static void AssertGoldenHash(string name, MsgPackWriter w, int expectedLength, ulong expectedHash)
        {
            byte[] actual = w.ToArray();
            Assert.AreEqual(expectedLength, actual.Length,
                $"[{name}] cambió la LONGITUD del frame.");
            Assert.AreEqual(expectedHash, Fnv1a64(actual),
                $"[{name}] cambió el HASH del frame — el writer dejó de ser byte-idéntico. Real: {Hex(actual)}");
        }

        // ── (a) Formas de frame reales ──────────────────────────────────────

        [Test]
        public void InputFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("type"); w.WriteString("input");
            w.WriteString("movement"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(-1f);
            w.WriteString("look_delta"); w.WriteArrayHeader(2);
            w.WriteFloat(0.5f); w.WriteFloat(-0.25f);
            w.WriteString("sprint"); w.WriteBool(true);
            w.WriteString("actions"); w.WriteArrayHeader(2);
            w.WriteString("a"); w.WriteString("bb");

            AssertGolden("input", w, 81,
                "85a474797065a5696e707574a86d6f76656d656e7493ca3f800000ca00000000cabf800000" +
                "aa6c6f6f6b5f64656c746192ca3f000000cabe800000a6737072696e74c3a7616374696f6e" +
                "7392a161a26262");
        }

        /// <summary>
        /// El frame de pose a 30 Hz. 19 campos ⇒ cabecera map16 (`de 0013`), NO fixmap: es el
        /// único frame del cliente que cruza esa frontera, y desalinear ahí es exactamente el
        /// modo de fallo del bug histórico de `crouch` (rmp_serde descarta la cola en silencio).
        ///
        /// GOLDEN REGENERADO dos veces: ADR-042 (16 → 18, `light_on` + `fire_seq`) y ADR-044
        /// (18 → 19, `melee_seq`). STATE.md exige que romper un golden sea DELIBERADO Y DOCUMENTADO:
        /// lo está, en las "Consecuencias" de ambos ADRs. Cada valor se acuñó con un arnés headless
        /// contra el `MsgPack.cs` REAL del repo, y el arnés reprodujo primero los goldens ANTERIORES
        /// byte a byte (len 238 / 0x66588337895127b0 y len 258 / 0x30f307f1c05701f0) antes de emitir
        /// el nuevo — sin esa comprobación previa, una constante nueva sólo dice que el writer
        /// coincide consigo mismo.
        ///
        /// OJO con `buttons`: ADR-044 le da significado (bits de apuntar/recargar) pero aquí se deja
        /// en 0 A PROPÓSITO, para que el único delta contra el golden de ADR-042 sea el campo nuevo.
        /// Su fidelidad de valor la fija el round-trip de abajo, que es donde corresponde.
        /// </summary>
        [Test]
        public void PlayerInputFrameBytesAreStableAndUseMap16Header()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(19);
            w.WriteString("type"); w.WriteString("input");
            w.WriteString("movement"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("look_delta"); w.WriteArrayHeader(2);
            w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("sprint"); w.WriteBool(false);
            w.WriteString("actions"); w.WriteArrayHeader(0);
            w.WriteString("input_seq"); w.WriteInt(1234u);
            w.WriteString("client_tick"); w.WriteInt(5678u);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(10.5f); w.WriteFloat(1.8f); w.WriteFloat(-3.25f);
            w.WriteString("velocity"); w.WriteArrayHeader(3);
            w.WriteFloat(0.1f); w.WriteFloat(0f); w.WriteFloat(-0.2f);
            w.WriteString("move_state"); w.WriteInt(1);
            w.WriteString("look"); w.WriteArrayHeader(2);
            w.WriteFloat(-12.5f); w.WriteFloat(90f);
            w.WriteString("buttons"); w.WriteInt(0);
            w.WriteString("crouch"); w.WriteBool(true);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(11); w.WriteInt(-22); w.WriteInt(333); w.WriteInt(0);
            w.WriteString("held_item"); w.WriteInt(77);
            w.WriteString("hit_seq"); w.WriteInt(3);
            w.WriteString("light_on"); w.WriteBool(true); // ADR-042
            w.WriteString("fire_seq"); w.WriteInt(5);     // ADR-042
            w.WriteString("melee_seq"); w.WriteInt(6);    // ADR-044
            // ADR-049: el par de carga. `carry_def` NEGATIVO a propósito — los def_id se acuñan con
            // Random.Range sobre todo el rango de i32, así que un golden con un positivo pequeño no
            // fijaría la codificación de los negativos, que es la mitad de los ids reales.
            w.WriteString("carry_def"); w.WriteInt(-1208217892);
            w.WriteString("carry_count"); w.WriteInt(3);

            byte[] bytes = w.ToArray();
            Assert.AreEqual(0xde, bytes[0], "21 campos deben emitir cabecera map16 (0xde), no fixmap");
            Assert.AreEqual(0x00, bytes[1]);
            Assert.AreEqual(0x15, bytes[2], "el conteo del map16 debe ser 21");

            // ADR-049 declara esta regeneración: al pasar de 19 a 21 campos cambian longitud y hash,
            // y el cambio es deliberado, no una regresión colada. Los valores viejos eran
            // 269 / 0xff1bd09a3c966ee6; los nuevos se obtuvieron con un arnés headless contra el
            // MsgPackWriter REAL que primero reproduce el golden viejo byte a byte — sin esa
            // comprobación previa una constante nueva sólo diría que el writer coincide consigo mismo.
            AssertGoldenHash("player_input", w, 297, 0x962776e96f18d65eUL);
        }

        [Test]
        public void RequestChunkFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("type"); w.WriteString("request_chunk");
            w.WriteString("cx"); w.WriteInt(3);
            w.WriteString("cz"); w.WriteInt(-7);
            w.WriteString("layer"); w.WriteInt(2);

            AssertGolden("request_chunk", w, 35,
                "84a474797065ad726571756573745f6368756e6ba2637803a2637af9a56c6179657202");
        }

        /// <summary>
        /// SendAction: `data` es NIL (0xc0). Distinto byte que el mapa vacío (0x80) del test de
        /// abajo — la razón por la que SendAction NO puede usar SendActionFrame. Congelar ambos
        /// por separado es lo que impide que una "unificación" futura los colapse en silencio.
        /// </summary>
        [Test]
        public void ActionFrameWithNilDataBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("craft");
            w.WriteString("data"); w.WriteNil();

            byte[] bytes = w.ToArray();
            Assert.AreEqual(0xc0, bytes[bytes.Length - 1], "`data` debe ser nil (0xc0), no un mapa vacío");

            AssertGolden("action_nil_data", w, 37,
                "83a474797065a6616374696f6eab616374696f6e5f74797065a56372616674a464617461c0");
        }

        /// <summary>SendActionFrame con 0 campos: `data` es un MAPA VACÍO (0x80), no nil.</summary>
        [Test]
        public void ActionFrameWithEmptyMapDataBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("respawn_request");
            w.WriteString("data"); w.WriteMapHeader(0);

            byte[] bytes = w.ToArray();
            Assert.AreEqual(0x80, bytes[bytes.Length - 1], "`data` debe ser un mapa vacío (0x80), no nil");

            AssertGolden("action_empty_map_data", w, 47,
                "83a474797065a6616374696f6eab616374696f6e5f74797065af7265737061776e5f72657175" +
                "657374a46461746180");
        }

        [Test]
        public void UiEventFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(2);
            w.WriteString("type"); w.WriteString("ui_event");
            w.WriteString("event_type"); w.WriteString("pause");

            AssertGolden("ui_event", w, 32,
                "82a474797065a875695f6576656e74aa6576656e745f74797065a57061757365");
        }

        [Test]
        public void ReportDamageFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("report_damage");
            w.WriteString("data"); w.WriteMapHeader(2);
            w.WriteString("amount"); w.WriteFloat(12.5f);
            w.WriteString("cause"); w.WriteString("Fall");

            AssertGolden("report_damage", w, 68,
                "83a474797065a6616374696f6eab616374696f6e5f74797065ad7265706f72745f64616d6167" +
                "65a46461746182a6616d6f756e74ca41480000a56361757365a446616c6c");
        }

        /// <summary>ADR-045 Fase 1: no invoca IPCClient.SendSetIdentity — mismo hueco conocido y
        /// aceptado que el resto de la familia Send*, ver la nota al principio del fichero.</summary>
        [Test]
        public void SetIdentityFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("set_identity");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("key"); w.WriteString("uuid:abc-123");

            AssertGolden("set_identity", w, 61,
                "83a474797065a6616374696f6eab616374696f6e5f74797065ac7365745f6964656e74697479" +
                "a46461746181a36b6579ac757569643a6162632d313233");
        }

        /// <summary>Array anidado de mapas de 2 campos + item ids NEGATIVOS (DataIdReference).</summary>
        [Test]
        public void ReportDeathLootFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("report_death_loot");
            w.WriteString("data"); w.WriteMapHeader(3);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(1); w.WriteInt(-2); w.WriteInt(3); w.WriteInt(0);
            w.WriteString("held_item"); w.WriteInt(9);
            w.WriteString("items"); w.WriteArrayHeader(2);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(-500);
            w.WriteString("quantity"); w.WriteInt(3);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(70000);
            w.WriteString("quantity"); w.WriteInt(1);

            AssertGoldenHash("report_death_loot", w, 128, 0x8eb8c2501e9e468eUL);
        }

        /// <summary>El frame de acción más ancho: 9 campos de data, 3 vec3.</summary>
        [Test]
        public void PvpHitCandidateFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("pvp_hit_candidate");
            w.WriteString("data"); w.WriteMapHeader(9);
            w.WriteString("request_id"); w.WriteInt(4294967296L);
            w.WriteString("attacker_id"); w.WriteInt(1);
            w.WriteString("victim_id"); w.WriteInt(2);
            w.WriteString("weapon_id"); w.WriteInt(-33);
            w.WriteString("damage"); w.WriteFloat(35.75f);
            w.WriteString("origin"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("direction"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(1f);
            w.WriteString("client_tick"); w.WriteInt(999u);
            w.WriteString("hit_position"); w.WriteArrayHeader(3);
            w.WriteFloat(-1.5f); w.WriteFloat(0.25f); w.WriteFloat(4f);

            AssertGoldenHash("pvp_hit_candidate", w, 210, 0xf1af22fe6c752aebUL);
        }

        [Test]
        public void SetStpItemsFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("set_stp_items");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(2);
            for (int i = 0; i < 2; i++)
            {
                w.WriteMapHeader(5);
                w.WriteString("id"); w.WriteInt(100 + i);
                w.WriteString("def_id"); w.WriteInt(-7);
                w.WriteString("count"); w.WriteInt(255);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(i); w.WriteFloat(0.5f); w.WriteFloat(-i);
                w.WriteString("rotation"); w.WriteFloat(45f);
            }

            AssertGoldenHash("set_stp_items", w, 172, 0xe4f494bc72c77ff3UL);
        }

        [Test]
        public void WorldInteractFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("world_interact");
            w.WriteString("data"); w.WriteMapHeader(5);
            w.WriteString("request_id"); w.WriteInt(12345);
            w.WriteString("target_id"); w.WriteInt(678u);
            w.WriteString("target_kind"); w.WriteString("harvestable");
            w.WriteString("interaction_type"); w.WriteString("chop");
            w.WriteString("player_position"); w.WriteArrayHeader(3);
            w.WriteFloat(1.25f); w.WriteFloat(0f); w.WriteFloat(-2.5f);

            AssertGoldenHash("world_interact", w, 151, 0x6801e9d8be501146UL);
        }

        [Test]
        public void StpPlaceFrameBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_place");
            w.WriteString("data"); w.WriteMapHeader(6);
            w.WriteString("place_id"); w.WriteInt(-2147483649L);
            w.WriteString("def_id"); w.WriteInt(42);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("rotation"); w.WriteFloat(180f);
            w.WriteString("group_id"); w.WriteInt(65535u);
            w.WriteString("is_group"); w.WriteBool(true);

            AssertGoldenHash("stp_place", w, 128, 0x6bf568f26be752f1UL);
        }

        // ── (b) Ramas de codificación con sus valores frontera ───────────────

        /// <summary>
        /// Las 10 ramas de WriteInt (positive/negative fixint, uint8/16/32/64, int8/16/32/64) en
        /// sus valores frontera. Verificado a mano: 0x00, 0x7f, 0xcc80, 0xccff, 0xcd0100,
        /// 0xcdffff, 0xce00010000, 0xceffffffff, 0xcf00000001_00000000, 0xcf7fffffffffffffff,
        /// 0xff, 0xe0, 0xd0df, 0xd080, 0xd1ff7f, 0xd18000, 0xd2ffff7fff, 0xd280000000,
        /// 0xd3ffffffff7fffffff, 0xd38000000000000000.
        /// </summary>
        [Test]
        public void EveryWriteIntBranchBytesAreStable()
        {
            var w = new MsgPackWriter();
            long[] ints = {
                0, 127, 128, 255, 256, 65535, 65536, 4294967295L, 4294967296L, long.MaxValue,
                -1, -32, -33, -128, -129, -32768, -32769, -2147483648L, -2147483649L, long.MinValue,
            };
            w.WriteArrayHeader(ints.Length);
            foreach (var v in ints) w.WriteInt(v);

            AssertGolden("all_int_branches", w, 83,
                "dc0014007fcc80ccffcd0100cdffffce00010000ceffffffffcf0000000100000000" +
                "cf7fffffffffffffffffe0d0dfd080d1ff7fd18000d2ffff7fffd280000000" +
                "d3ffffffff7fffffffd38000000000000000");
        }

        /// <summary>Todo float sale como float32 big-endian (0xca + 4 bytes).</summary>
        [Test]
        public void EveryWriteFloatBranchBytesAreStable()
        {
            var w = new MsgPackWriter();
            float[] floats = { 0f, 1f, -1f, 0.5f, -0.25f, 3.14159f, -273.15f, float.MaxValue, float.MinValue };
            w.WriteArrayHeader(floats.Length);
            foreach (var v in floats) w.WriteFloat(v);

            AssertGolden("all_float_branches", w, 46,
                "99ca00000000ca3f800000cabf800000ca3f000000cabe800000ca40490fd0cac3889333" +
                "ca7f7fffffcaff7fffff");
        }

        /// <summary>fixstr/str8/str16 en sus fronteras exactas (31/32, 255/256) + UTF-8 multibyte
        /// (19 bytes para 10 caracteres — la longitud del header es en BYTES, no en chars).</summary>
        [Test]
        public void EveryWriteStringBranchBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(6);
            w.WriteString("");
            w.WriteString(new string('a', 31));   // fixstr máximo
            w.WriteString(new string('b', 32));   // str8 mínimo
            w.WriteString(new string('c', 255));  // str8 máximo
            w.WriteString(new string('d', 256));  // str16 mínimo
            w.WriteString("café ☕ 日本語");        // multibyte

            AssertGoldenHash("all_string_branches", w, 604, 0x9d9568fcd8af9e4aUL);
        }

        /// <summary>Fronteras fixmap→map16 y fixarray→array16, más bool/nil.</summary>
        [Test]
        public void EveryHeaderAndScalarBranchBytesAreStable()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(8);
            w.WriteMapHeader(0);
            w.WriteMapHeader(15);   // fixmap máximo
            w.WriteMapHeader(16);   // map16 mínimo
            w.WriteArrayHeader(15); // fixarray máximo
            w.WriteArrayHeader(16); // array16 mínimo
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteNil();

            AssertGolden("all_header_and_scalar_branches", w, 13, "98808fde00109fdc0010c3c2c0");
        }

        // ── Round-trip semántico: los bytes congelados siguen decodificando ──

        /// <summary>
        /// Complemento al congelado: un frame que sea byte-idéntico pero semánticamente roto
        /// pasaría los tests de arriba. Este cierra el círculo decodificando con el MISMO
        /// MsgPackReader que usa IPCClient y comprobando claves y valores.
        /// </summary>
        [Test]
        public void FrozenActionFrameStillDecodesToTheExpectedKeysAndValues()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("report_damage");
            w.WriteString("data"); w.WriteMapHeader(2);
            w.WriteString("amount"); w.WriteFloat(12.5f);
            w.WriteString("cause"); w.WriteString("Fall");

            var decoded = new MsgPackReader(w.ToArray()).ReadValue() as Dictionary<string, object>;
            Assert.IsNotNull(decoded);
            Assert.AreEqual("action", IPCParse.S(decoded, "type"));
            Assert.AreEqual("report_damage", IPCParse.S(decoded, "action_type"));

            var data = IPCParse.Get(decoded, "data") as Dictionary<string, object>;
            Assert.IsNotNull(data);
            Assert.AreEqual(12.5f, IPCParse.F(data, "amount"));
            Assert.AreEqual("Fall", IPCParse.S(data, "cause"));
        }

        /// <summary>
        /// El frame de pose completo sobrevive el round-trip con los 19 campos intactos — la
        /// regresión que el bug de `crouch` (cola descartada en silencio) habría disparado.
        /// `melee_seq` es AHORA el último par del mapa, así que es el que un desalineado de
        /// cabecera perdería primero: el centinela vive donde más duele (ADR-044).
        ///
        /// Aquí `buttons` SÍ va con valor (0b11, los dos bits de ADR-044 a la vez): un bitfield mal
        /// serializado suele sobrevivir con un solo bit puesto y fallar en cuanto hay dos.
        /// </summary>
        [Test]
        public void FrozenPlayerInputFrameRoundTripsAllNineteenFields()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(19);
            w.WriteString("type"); w.WriteString("input");
            w.WriteString("movement"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("look_delta"); w.WriteArrayHeader(2);
            w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("sprint"); w.WriteBool(false);
            w.WriteString("actions"); w.WriteArrayHeader(0);
            w.WriteString("input_seq"); w.WriteInt(1234u);
            w.WriteString("client_tick"); w.WriteInt(5678u);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(10.5f); w.WriteFloat(1.8f); w.WriteFloat(-3.25f);
            w.WriteString("velocity"); w.WriteArrayHeader(3);
            w.WriteFloat(0.1f); w.WriteFloat(0f); w.WriteFloat(-0.2f);
            w.WriteString("move_state"); w.WriteInt(1);
            w.WriteString("look"); w.WriteArrayHeader(2);
            w.WriteFloat(-12.5f); w.WriteFloat(90f);
            w.WriteString("buttons"); w.WriteInt(3); // ADR-044: los dos bits a la vez
            w.WriteString("crouch"); w.WriteBool(true);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(11); w.WriteInt(-22); w.WriteInt(333); w.WriteInt(0);
            w.WriteString("held_item"); w.WriteInt(77);
            w.WriteString("hit_seq"); w.WriteInt(3);
            w.WriteString("light_on"); w.WriteBool(true); // ADR-042
            w.WriteString("fire_seq"); w.WriteInt(5);     // ADR-042
            w.WriteString("melee_seq"); w.WriteInt(6);    // ADR-044

            var decoded = new MsgPackReader(w.ToArray()).ReadValue() as Dictionary<string, object>;
            Assert.IsNotNull(decoded);
            Assert.AreEqual(19, decoded.Count, "los 19 pares deben sobrevivir (ninguna cola descartada)");
            Assert.AreEqual("input", IPCParse.S(decoded, "type"));
            Assert.AreEqual(1234, IPCParse.L(decoded, "input_seq"));
            Assert.AreEqual(5678, IPCParse.L(decoded, "client_tick"));
            Assert.AreEqual(1, IPCParse.L(decoded, "move_state"));
            Assert.IsTrue(IPCParse.B(decoded, "crouch"), "crouch es el campo que el bug histórico perdía");
            Assert.AreEqual(77, IPCParse.L(decoded, "held_item"));
            Assert.AreEqual(3, IPCParse.L(decoded, "hit_seq"));
            Assert.IsTrue(IPCParse.B(decoded, "light_on"), "ADR-042: light_on debe sobrevivir");
            Assert.AreEqual(5, IPCParse.L(decoded, "fire_seq"), "ADR-042: fire_seq debe sobrevivir");
            Assert.AreEqual(3, IPCParse.L(decoded, "buttons"),
                "ADR-044: los DOS bits (apuntar + recargar) deben sobrevivir juntos");
            Assert.AreEqual(6, IPCParse.L(decoded, "melee_seq"),
                "ADR-044: melee_seq es el ÚLTIMO par — el primero que se pierde si la cabecera desalinea");
            Assert.AreEqual(new UnityEngine.Vector3(10.5f, 1.8f, -3.25f), IPCParse.Vec3(IPCParse.Get(decoded, "position")));

            var equipment = IPCParse.IntArray(IPCParse.Get(decoded, "equipment"));
            CollectionAssert.AreEqual(new[] { 11, -22, 333, 0 }, equipment);
        }
    }
}
