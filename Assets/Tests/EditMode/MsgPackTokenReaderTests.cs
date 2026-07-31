using System;
using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Ruta IPC sin boxing — cierre de docs/STATE.md ("el boxing sistemático del decoder
    /// msgpack ... es el mayor coste real de la ruta de parseo"). Cubre la nueva API de
    /// tokens de <see cref="MsgPackReader"/> (ReadMapHeader/ReadArrayHeader/ReadKey/ReadInt/
    /// ReadFloat/ReadBool/ReadString/Skip/...), que camina los mismos bytes que
    /// <see cref="MsgPackReader.ReadValue"/> sin materializar un Dictionary/object[]/box por
    /// campo. ReadValue() en sí NO cambia de comportamiento — solo gana bounds-check contra
    /// una longitud lógica reseteable — y se prueba aquí solo en el camino de reutilización
    /// (Reset), que es lo nuevo.
    ///
    /// Todo round-trip real: MsgPackWriter para lo que sabe emitir; bytes crudos a mano para
    /// bin/ext/fixext y las cabeceras de 32 bits (map32/array32), que MsgPackWriter no puede
    /// producir sin frames poco prácticos para un test.
    /// </summary>
    [TestFixture]
    public class MsgPackTokenReaderTests
    {
        // ── Enteros: fixint, u8/u16/u32/u64, i8/i16/i32/i64, y el límite de cada uno ──

        [Test]
        public void ReadIntRoundTripsEveryIntegerOpcodeBoundary()
        {
            long[] values =
            {
                0, 127, 128, 255, 256, 65535, 65536, 4294967295L, 4294967296L,
                -1, -32, -33, -128, -129, -32768, -32769, -2147483648L, -2147483649L,
            };

            var w = new MsgPackWriter();
            w.WriteArrayHeader(values.Length);
            foreach (var v in values) w.WriteInt(v);

            var r = new MsgPackReader(w.ToArray());
            Assert.AreEqual(values.Length, r.ReadArrayHeader());
            foreach (var v in values)
                Assert.AreEqual(v, r.ReadInt(), $"valor {v} no sobrevivió el round-trip");
        }

        [Test]
        public void ReadFloatRoundTripsFloat32()
        {
            float[] values = { 0f, 1f, -1f, 3.14159f, -273.15f, 1e10f };

            var w = new MsgPackWriter();
            w.WriteArrayHeader(values.Length);
            foreach (var v in values) w.WriteFloat(v);

            var r = new MsgPackReader(w.ToArray());
            Assert.AreEqual(values.Length, r.ReadArrayHeader());
            foreach (var v in values)
                Assert.AreEqual(v, r.ReadFloat(), 0.0001f);
        }

        /// <summary>
        /// IPCParse.ToLong/ToFloat toleran el cruce int↔float (un campo que el backend manda
        /// como f64 igual decodifica). ReadInt/ReadFloat deben conservar esa tolerancia,
        /// incluida la rama 0xcb (float64) que MsgPackWriter no puede emitir (no tiene
        /// WriteDouble) — construida a mano.
        /// </summary>
        [Test]
        public void ReadIntAndReadFloatToleranceAcrossTheOtherFamily()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(1);
            w.WriteFloat(42f);
            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            Assert.AreEqual(42L, r.ReadInt(), "ReadInt debe tolerar un valor float32");

            var w2 = new MsgPackWriter();
            w2.WriteArrayHeader(1);
            w2.WriteInt(7);
            var r2 = new MsgPackReader(w2.ToArray());
            r2.ReadArrayHeader();
            Assert.AreEqual(7f, r2.ReadFloat(), "ReadFloat debe tolerar un valor entero");

            // 0xcb float64, a mano: MsgPackWriter no emite ese opcode.
            byte[] f64Bytes = BitConverter.GetBytes(9.0d);
            if (BitConverter.IsLittleEndian) Array.Reverse(f64Bytes);
            var raw = new List<byte> { 0xcb };
            raw.AddRange(f64Bytes);
            var r3 = new MsgPackReader(raw.ToArray());
            Assert.AreEqual(9L, r3.ReadInt(), "ReadInt debe tolerar la rama 0xcb (float64)");

            raw = new List<byte> { 0xcb };
            raw.AddRange(f64Bytes);
            var r4 = new MsgPackReader(raw.ToArray());
            Assert.AreEqual(9f, r4.ReadFloat(), "ReadFloat debe tolerar la rama 0xcb (float64)");
        }

        // ── Strings: fixstr, str8, str16 ──

        [Test]
        public void ReadStringRoundTripsFixstrStr8Str16()
        {
            string[] values = { "hola", new string('b', 100), new string('c', 300) };

            var w = new MsgPackWriter();
            w.WriteArrayHeader(values.Length);
            foreach (var s in values) w.WriteString(s);

            var r = new MsgPackReader(w.ToArray());
            Assert.AreEqual(values.Length, r.ReadArrayHeader());
            foreach (var s in values)
                Assert.AreEqual(s, r.ReadString());
        }

        // ── Cabeceras de mapa/array: fixmap/fixarray, map16/array16 reales; map32/array32 a mano ──

        [Test]
        public void ReadMapHeaderAndArrayHeaderDecode16BitCountsWithRealPayload()
        {
            const int n = 20; // >= 16 ⇒ cruza a map16/array16 en MsgPackWriter
            var w = new MsgPackWriter();
            w.WriteMapHeader(n);
            for (int i = 0; i < n; i++) { w.WriteInt(i); w.WriteInt(i * 10); }

            var r = new MsgPackReader(w.ToArray());
            Assert.AreEqual(n, r.ReadMapHeader());
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(i, r.ReadInt());
                Assert.AreEqual(i * 10, r.ReadInt());
            }

            var w2 = new MsgPackWriter();
            w2.WriteArrayHeader(n);
            for (int i = 0; i < n; i++) w2.WriteInt(i);

            var r2 = new MsgPackReader(w2.ToArray());
            Assert.AreEqual(n, r2.ReadArrayHeader());
            for (int i = 0; i < n; i++) Assert.AreEqual(i, r2.ReadInt());
        }

        [Test]
        public void ReadMapHeaderAndArrayHeaderDecode32BitCountsFromRawBytes()
        {
            // Solo la cabecera: ReadMapHeader/ReadArrayHeader no iteran el contenido, así que
            // no hace falta escribir 70000 pares reales para probar la rama 0xdf/0xdd.
            var mapBytes = new byte[] { 0xdf, 0x00, 0x01, 0x11, 0x70 }; // 0x00011170 = 70000
            Assert.AreEqual(70000, new MsgPackReader(mapBytes).ReadMapHeader());

            var arrayBytes = new byte[] { 0xdd, 0x00, 0x01, 0x11, 0x70 };
            Assert.AreEqual(70000, new MsgPackReader(arrayBytes).ReadArrayHeader());
        }

        [Test]
        public void NilMapAndArrayHeadersReturnMinusOne()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(2);
            w.WriteNil();
            w.WriteNil();

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            Assert.AreEqual(-1, r.ReadMapHeader(), "un valor nil en vez de mapa debe leer -1");
            Assert.AreEqual(-1, r.ReadArrayHeader(), "un valor nil en vez de array debe leer -1");
        }

        // ── Claves: span cero-alloc + comparación ASCII ──

        [Test]
        public void ReadKeyReturnsSpanOverBufferAndIsMatchesExactly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(1);
            w.WriteString("hello");
            w.WriteInt(5);

            var r = new MsgPackReader(w.ToArray());
            Assert.AreEqual(1, r.ReadMapHeader());
            var key = r.ReadKey();

            Assert.IsTrue(MsgPackReader.Is(key, "hello"));
            Assert.IsFalse(MsgPackReader.Is(key, "nope"), "contenido distinto no debe matchear");
            Assert.IsFalse(MsgPackReader.Is(key, "hell"), "longitud distinta no debe matchear");
            Assert.AreEqual(5L, r.ReadInt(), "el valor debe seguir leíble tras comparar la clave");
        }

        // ── nil / bool ──

        [Test]
        public void NilReadsAsDefaultAcrossAllScalarAccessors()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(4);
            w.WriteNil(); w.WriteNil(); w.WriteNil(); w.WriteNil();

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            Assert.AreEqual(0L, r.ReadInt(), "nil ⇒ ReadInt default 0");
            Assert.AreEqual(0f, r.ReadFloat(), "nil ⇒ ReadFloat default 0");
            Assert.IsFalse(r.ReadBool(), "nil ⇒ ReadBool default false");
            Assert.AreEqual("", r.ReadString(), "nil ⇒ ReadString default \"\"");
        }

        [Test]
        public void BoolRoundTrips()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(2);
            w.WriteBool(true);
            w.WriteBool(false);

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            Assert.IsTrue(r.ReadBool());
            Assert.IsFalse(r.ReadBool());
        }

        // ── Skip(): cada tipo msgpack, incluidos bin/ext/fixext que MsgPackWriter no emite ──

        /// <summary>
        /// Construye a mano un array con UNA instancia de cada opcode msgpack relevante,
        /// seguido de un centinela. Skip() n veces debe dejar el cursor exactamente sobre el
        /// centinela — ni corto (bytes de más sin consumir) ni largo (se comió el centinela).
        /// </summary>
        [Test]
        public void SkipConsumesExactlyOneValueForEveryOpcodeIncludingExt()
        {
            var b = new List<byte>();
            int elementCount = 0;

            void Elem(params byte[] bytes) { b.AddRange(bytes); elementCount++; }

            Elem(0x05);                                   // positive fixint
            Elem(0xff);                                   // negative fixint (-1)
            Elem(0xcc, 0x80);                              // uint8
            Elem(0xcd, 0x01, 0x00);                        // uint16
            Elem(0xce, 0x00, 0x01, 0x00, 0x00);            // uint32
            Elem(0xcf, 0, 0, 0, 0, 0, 0, 0, 1);            // uint64
            Elem(0xd0, 0x80);                              // int8
            Elem(0xd1, 0xff, 0x00);                        // int16
            Elem(0xd2, 0xff, 0xff, 0xff, 0x00);            // int32
            Elem(0xd3, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00); // int64
            Elem(0xca, 0x42, 0x28, 0x00, 0x00);            // float32 (42.0)
            Elem(0xcb, 0x40, 0x45, 0, 0, 0, 0, 0, 0);       // float64 (42.0)
            Elem(0xa3, (byte)'a', (byte)'b', (byte)'c');   // fixstr "abc"
            Elem(new byte[] { 0xd9, 0x02, (byte)'x', (byte)'y' });                 // str8
            Elem(Concat(new byte[] { 0xda, 0x00, 0x02 }, new byte[] { 1, 2 }));    // str16
            Elem(Concat(new byte[] { 0xdb, 0x00, 0x00, 0x00, 0x02 }, new byte[] { 1, 2 })); // str32
            Elem(Concat(new byte[] { 0xc4, 0x02 }, new byte[] { 9, 9 }));          // bin8
            Elem(Concat(new byte[] { 0xc5, 0x00, 0x02 }, new byte[] { 9, 9 }));    // bin16
            Elem(Concat(new byte[] { 0xc6, 0x00, 0x00, 0x00, 0x02 }, new byte[] { 9, 9 })); // bin32
            Elem(0x91, 0x01);                              // fixarray [1]
            Elem(0x81, 0x01, 0x02);                        // fixmap {1:2}
            Elem(0xc0);                                    // nil
            Elem(0xc2);                                    // false
            Elem(0xc3);                                    // true
            Elem(Concat(new byte[] { 0xd4, 0x01 }, new byte[] { 1 }));             // fixext1
            Elem(Concat(new byte[] { 0xd5, 0x01 }, new byte[] { 1, 2 }));          // fixext2
            Elem(Concat(new byte[] { 0xd6, 0x01 }, new byte[4]));                  // fixext4
            Elem(Concat(new byte[] { 0xd7, 0x01 }, new byte[8]));                  // fixext8
            Elem(Concat(new byte[] { 0xd8, 0x01 }, new byte[16]));                 // fixext16
            Elem(Concat(new byte[] { 0xc7, 0x02, 0x01 }, new byte[] { 9, 9 }));    // ext8
            Elem(Concat(new byte[] { 0xc8, 0x00, 0x02, 0x01 }, new byte[] { 9, 9 })); // ext16
            Elem(Concat(new byte[] { 0xc9, 0x00, 0x00, 0x00, 0x02, 0x01 }, new byte[] { 9, 9 })); // ext32

            var frame = new List<byte>();
            frame.Add((byte)(0x90 | elementCount)); // fixarray header (elementCount < 16? NO — see below)
            // elementCount aquí es 28 > 15: fixarray de 4 bits no alcanza. Usar array16 real.
            frame.Clear();
            frame.Add(0xdc); frame.Add((byte)(elementCount >> 8)); frame.Add((byte)elementCount);
            frame.AddRange(b);
            frame.Add(123); // centinela: fixint 123

            var r = new MsgPackReader(frame.ToArray());
            int n = r.ReadArrayHeader();
            Assert.AreEqual(elementCount, n, "cabecera de array debe reportar el conteo real construido");
            for (int i = 0; i < n; i++) r.Skip();

            Assert.AreEqual(123L, r.ReadInt(), "tras N Skip() el cursor debe caer justo sobre el centinela");
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Array.Copy(a, 0, r, 0, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        // ── Arrays tipados: defaults, clamps y descarte del sobrante ──

        [Test]
        public void ReadIntArrayIntoZerosMissingSlotsAndDiscardsExtras()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(2);
            w.WriteInt(11);
            w.WriteInt(22);

            var dest = new int[4]; // el wire trae menos de lo que pide dest ⇒ cola a cero
            new MsgPackReader(w.ToArray()).ReadIntArrayInto(dest);
            CollectionAssert.AreEqual(new[] { 11, 22, 0, 0 }, dest);

            var w2 = new MsgPackWriter();
            w2.WriteArrayHeader(4);
            w2.WriteInt(1); w2.WriteInt(2); w2.WriteInt(3); w2.WriteInt(4);

            var dest2 = new int[2]; // el wire trae más de lo que cabe en dest ⇒ se descarta, no trunca el frame
            var r2 = new MsgPackReader(w2.ToArray());
            r2.ReadIntArrayInto(dest2);
            CollectionAssert.AreEqual(new[] { 1, 2 }, dest2);
        }

        [Test]
        public void ReadVec3DefaultsToZeroForNilOrShortArrays()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(2);
            w.WriteNil();
            w.WriteArrayHeader(2); // solo 2 de 3 componentes
            w.WriteFloat(1f); w.WriteFloat(2f);

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            Assert.AreEqual(Vector3.zero, r.ReadVec3(), "nil ⇒ Vector3.zero");
            Assert.AreEqual(Vector3.zero, r.ReadVec3(), "array corto (2 de 3) ⇒ Vector3.zero, no crash");
        }

        [Test]
        public void ReadIntArray2DefaultsToZeroZeroForShortArrays()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(1);

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            CollectionAssert.AreEqual(new[] { 0, 0 }, r.ReadIntArray2());
        }

        [Test]
        public void ByteAndUShortArraysClampOutOfRangeValues()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(2);
            w.WriteInt(-5);
            w.WriteInt(9999);

            var r = new MsgPackReader(w.ToArray());
            CollectionAssert.AreEqual(new byte[] { 0, 255 }, r.ReadByteArrayValues());

            var w2 = new MsgPackWriter();
            w2.WriteArrayHeader(2);
            w2.WriteInt(-1);
            w2.WriteInt(100000);

            var r2 = new MsgPackReader(w2.ToArray());
            CollectionAssert.AreEqual(new ushort[] { 0, 65535 }, r2.ReadUShortArrayValues());
        }

        // ── Reset(): la propiedad de seguridad central de este barrido ──

        /// <summary>
        /// El caso que justifica CheckBounds contra una longitud LÓGICA en vez de
        /// data.Length: IPCClient reutiliza un buffer que solo crece. Un frame más corto que
        /// el anterior deja bytes de un mensaje VIEJO físicamente presentes justo después del
        /// límite lógico. Sin este guard, leer de más ahí devolvería basura silenciosa en vez
        /// de lanzar — exactamente el bug que el `new byte[len]` por-mensaje de antes evitaba
        /// gratis (y que este rediseño tiene que seguir evitando).
        /// </summary>
        [Test]
        public void ResetRejectsReadsPastLogicalLengthEvenWithStaleBytesPresent()
        {
            var shared = new byte[64];

            var w1 = new MsgPackWriter();
            w1.WriteArrayHeader(2);
            w1.WriteInt(999);
            w1.WriteInt(888);
            byte[] frame1 = w1.ToArray();
            Array.Copy(frame1, shared, frame1.Length);

            var reader = new MsgPackReader(null);
            reader.Reset(shared, frame1.Length);
            Assert.AreEqual(2, reader.ReadArrayHeader());
            Assert.AreEqual(999L, reader.ReadInt());
            Assert.AreEqual(888L, reader.ReadInt());

            // Frame 2, más corto, reutiliza el MISMO array físico — solo pisa sus propios
            // bytes; el resto de `shared` sigue conteniendo la cola de frame1.
            var w2 = new MsgPackWriter();
            w2.WriteArrayHeader(1);
            w2.WriteInt(42);
            byte[] frame2 = w2.ToArray();
            Assert.Less(frame2.Length, frame1.Length, "el fixture exige que frame2 sea más corto que frame1");
            Array.Copy(frame2, shared, frame2.Length);

            reader.Reset(shared, frame2.Length);
            Assert.AreEqual(1, reader.ReadArrayHeader());
            Assert.AreEqual(42L, reader.ReadInt());

            Assert.Throws<Exception>(() => reader.ReadInt(),
                "leer más allá de la longitud lógica debe lanzar, aunque el array físico tenga bytes de frame1 ahí");
        }

        [Test]
        public void ReadValueStillWorksThroughAReusedResetInstance()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(1);
            w.WriteString("reason");
            w.WriteString("timeout");
            byte[] frame = w.ToArray();

            var reader = new MsgPackReader(null);
            reader.Reset(frame, frame.Length);

            var decoded = reader.ReadValue() as Dictionary<string, object>;
            Assert.IsNotNull(decoded);
            Assert.AreEqual("timeout", decoded["reason"]);
        }

        [Test]
        public void TruncatedFrameThrowsInsteadOfReadingOutOfBounds()
        {
            // 0xcd (uint16) anuncia 2 bytes de payload pero el frame solo trae 1.
            var truncated = new byte[] { 0xcd, 0x01 };
            var r = new MsgPackReader(truncated);
            Assert.Throws<Exception>(() => r.ReadInt());
        }
    }
}
