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
    /// ext/fixext y las cabeceras de 32 bits (map32/array32), que MsgPackWriter no puede
    /// producir sin frames poco prácticos para un test. (La familia bin SÍ pasó a ser emitible
    /// desde ADR-046 Fase 0; los tests de Skip que la cubren con bytes a mano se dejan como
    /// están, porque prueban el decoder contra un emisor ajeno y eso sigue siendo lo correcto.)
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
            // Mismo molde que ReadVec3DefaultsToZeroForNilOrShortArrays: envoltorio + los casos
            // cortos DENTRO. La versión anterior escribía solo la cabecera del envoltorio (un
            // byte, 0x91) y nada más, así que el ReadIntArray2 se salía del frame y el test
            // moría por excepción sin llegar al assert — nunca ejercitó lo que dice ejercitar.
            var w = new MsgPackWriter();
            w.WriteArrayHeader(3);
            w.WriteNil();               // nil ⇒ [0,0]
            w.WriteArrayHeader(1);      // array corto (1 de 2) ⇒ [0,0], y hay que consumir su int
            w.WriteInt(7);
            w.WriteInt(0x2A);           // centinela: solo se lee si el cursor quedó alineado

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            CollectionAssert.AreEqual(new[] { 0, 0 }, r.ReadIntArray2(), "nil ⇒ [0,0]");
            CollectionAssert.AreEqual(new[] { 0, 0 }, r.ReadIntArray2(), "array de 1 ⇒ [0,0]");
            // La propiedad que de verdad importa: un array corto tiene que CONSUMIR sus elementos
            // antes de devolver el default. Si no, el cursor queda desalineado y todo lo que se
            // lea después en ese frame sale corrupto en silencio — la clase de fallo que no da la
            // cara en un test de valor de retorno. Misma técnica que
            // SkipConsumesExactlyOneValueForEveryOpcodeIncludingExt.
            Assert.AreEqual(0x2AL, r.ReadInt(), "el array corto dejó el cursor desalineado");
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

        // ── C5: interning acotado de strings ──

        /// <summary>
        /// La propiedad que de verdad importa del interning: NO cambia ni un valor decodificado.
        /// Se lee la misma secuencia dos veces — una con ReadString (sin caché) y otra con
        /// ReadStringCached — y las cadenas resultantes tienen que ser iguales una a una.
        /// </summary>
        [Test]
        public void ReadStringCachedDecodesTheSameValuesAsReadString()
        {
            string[] values = { "idle", "walk", "idle", "run", "walk", "idle", "", "café ☕" };

            var w = new MsgPackWriter();
            w.WriteArrayHeader(values.Length);
            foreach (var s in values) w.WriteString(s);
            byte[] frame = w.ToArray();

            var plain = new MsgPackReader(frame);
            plain.ReadArrayHeader();
            var cached = new MsgPackReader(frame);
            cached.ReadArrayHeader();

            for (int i = 0; i < values.Length; i++)
            {
                string expected = plain.ReadString();
                Assert.AreEqual(expected, cached.ReadStringCached(), $"índice {i}");
                Assert.AreEqual(values[i], expected, $"índice {i}: el fixture debe round-tripear");
            }
        }

        /// <summary>
        /// El ahorro real: un valor repetido devuelve la MISMA instancia de string, así que un
        /// snapshot con N entidades en el mismo estado no aloca N cadenas. ReferenceEquals es el
        /// aserto correcto aquí — AreEqual pasaría igual sin caché y no probaría nada.
        /// </summary>
        [Test]
        public void ReadStringCachedReturnsTheSameInstanceForRepeatedValues()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(3);
            w.WriteString("idle");
            w.WriteString("walk");
            w.WriteString("idle");

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            string first = r.ReadStringCached();
            string other = r.ReadStringCached();
            string repeat = r.ReadStringCached();

            Assert.AreSame(first, repeat, "un valor repetido debe reutilizar la instancia cacheada");
            Assert.AreNotSame(first, other, "valores distintos no deben colapsar a la misma instancia");
        }

        /// <summary>
        /// La caché es POR INSTANCIA (una por conexión IPC), nunca estática: dos readers no
        /// comparten instancias. Es lo que garantiza que al caer la conexión la caché se va con
        /// el reader — IPCClient.ReadFrames declara el suyo como local.
        /// </summary>
        [Test]
        public void StringCacheIsPerReaderInstanceNotShared()
        {
            var w = new MsgPackWriter();
            w.WriteString("idle");
            byte[] frame = w.ToArray();

            string a = new MsgPackReader(frame).ReadStringCached();
            string b = new MsgPackReader(frame).ReadStringCached();

            Assert.AreEqual(a, b);
            Assert.AreNotSame(a, b, "dos readers distintos no comparten caché (no es estática)");
        }

        /// <summary>
        /// Tope duro: pasado el límite deja de cachear, NUNCA crece ni desaloja. Con más valores
        /// únicos que capacidad, los que entran siguen sirviéndose de caché y los que se quedan
        /// fuera se decodifican frescos — pero todos con el valor correcto, que es lo innegociable.
        /// </summary>
        [Test]
        public void StringCacheStopsGrowingPastItsCapacityAndStillDecodesCorrectly()
        {
            const int unique = 200; // muy por encima de StringCacheCapacity (64)
            var w = new MsgPackWriter();
            w.WriteArrayHeader(unique * 2);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < unique; i++)
                    w.WriteString($"value_{i}");

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < unique; i++)
                    Assert.AreEqual($"value_{i}", r.ReadStringCached(), $"pase {pass}, índice {i}");
        }

        /// <summary>
        /// Prefijo compartido: "walk" no debe servirse como acierto de "walking" ni al revés. La
        /// comparación es exacta byte a byte incluida la longitud.
        /// </summary>
        [Test]
        public void ReadStringCachedDoesNotConfuseValuesSharingAPrefix()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(4);
            w.WriteString("walk");
            w.WriteString("walking");
            w.WriteString("walking");
            w.WriteString("walk");

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            Assert.AreEqual("walk", r.ReadStringCached());
            Assert.AreEqual("walking", r.ReadStringCached());
            Assert.AreEqual("walking", r.ReadStringCached());
            Assert.AreEqual("walk", r.ReadStringCached());
        }

        /// <summary>Mismo fallback que ReadString: nil/no-string ⇒ "" y el valor consumido entero.</summary>
        [Test]
        public void ReadStringCachedFallsBackToEmptyForNilAndNonString()
        {
            var w = new MsgPackWriter();
            w.WriteArrayHeader(3);
            w.WriteNil();
            w.WriteInt(42);
            w.WriteString("ok");

            var r = new MsgPackReader(w.ToArray());
            r.ReadArrayHeader();
            Assert.AreEqual("", r.ReadStringCached(), "nil ⇒ \"\"");
            Assert.AreEqual("", r.ReadStringCached(), "int ⇒ \"\", consumido entero");
            Assert.AreEqual("ok", r.ReadStringCached(), "el valor siguiente debe seguir alineado");
        }

        /// <summary>
        /// La caché sobrevive a Reset() (mismo reader, frame siguiente de la MISMA conexión) y
        /// sigue devolviendo el valor correcto pese a que el buffer subyacente cambió de
        /// contenido — la caché guarda copia propia de los bytes, no un puntero al buffer.
        /// </summary>
        [Test]
        public void StringCacheSurvivesResetAndDoesNotAliasTheFrameBuffer()
        {
            var w1 = new MsgPackWriter();
            w1.WriteString("idle");
            byte[] frame1 = w1.ToArray();

            var w2 = new MsgPackWriter();
            w2.WriteString("idle");
            byte[] frame2 = w2.ToArray();

            var reader = new MsgPackReader(null);
            reader.Reset(frame1, frame1.Length);
            string first = reader.ReadStringCached();

            reader.Reset(frame2, frame2.Length);
            string second = reader.ReadStringCached();

            Assert.AreEqual("idle", second);
            Assert.AreSame(first, second, "el acierto de caché debe cruzar frames de la misma conexión");
        }

        // ── ADR-046 Fase 0: la familia bin, escribible por fin ────────────────────────
        //
        // Cada caso lleva CENTINELA: un entero escrito DETRÁS del payload y leído DESPUÉS.
        // Sin él, un ReadBin que devuelve los bytes correctos pero deja el cursor descolocado
        // pasa el test y corrompe todos los campos siguientes del frame. Verificado con
        // mutación (`_pos += n` → `_pos += n - 1`): sin centinela el mutante sobrevive.

        private static byte[] BinPattern(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)((i * 31 + 7) & 0xff);
            return b;
        }

        [TestCase(0, 0xc4)]
        [TestCase(1, 0xc4)]
        [TestCase(120, 0xc4)] // el tamaño real de una trama de voz: 2 × 20 ms de Opus a ~24 kbps
        [TestCase(255, 0xc4)]
        [TestCase(256, 0xc5)]
        [TestCase(65535, 0xc5)]
        [TestCase(65536, 0xc6)]
        public void WriteBinRoundTripsAtEveryWidthBoundary(int len, int expectedTag)
        {
            byte[] payload = BinPattern(len);

            var w = new MsgPackWriter();
            w.WriteBin(payload);
            w.WriteInt(0x2A2A);
            byte[] frame = w.ToArray();

            Assert.AreEqual(expectedTag, frame[0], $"len={len} debe elegir el ancho de cabecera 0x{expectedTag:x2}");

            var r = new MsgPackReader(frame);
            Assert.AreEqual(payload, r.ReadBin(), $"payload de {len} B no sobrevivió el round-trip");
            Assert.AreEqual(0x2A2A, r.ReadInt(), "CENTINELA: ReadBin dejó el cursor descolocado");
        }

        /// <summary>
        /// La ruta genérica (ReadValue → el árbol de GameEventMsg.data) camina EL MISMO cursor,
        /// así que necesita su propio centinela: una mutación del ReadBin privado sobrevive a
        /// cualquier aserción que solo mire los bytes devueltos.
        /// </summary>
        [Test]
        public void ReadValueTreeDecodesBinAndLeavesTheCursorAligned()
        {
            byte[] payload = BinPattern(300);

            var w = new MsgPackWriter();
            w.WriteMapHeader(1);
            w.WriteString("data");
            w.WriteBin(payload);
            w.WriteInt(0x3B3B);

            var r = new MsgPackReader(w.ToArray());
            var tree = (Dictionary<string, object>)r.ReadValue();
            Assert.AreEqual(payload, tree["data"] as byte[], "el árbol genérico debe entregar byte[]");
            Assert.AreEqual(0x3B3B, r.ReadInt(), "CENTINELA: la ruta genérica dejó el cursor descolocado");
        }

        /// <summary>
        /// Un payload nulo o vacío emite un bin8 VACÍO, nunca nil: `rmp_serde` decodifica
        /// `Vec&lt;u8&gt;` desde un bin vacío y NO desde nil, así que emitir nil convertiría
        /// "este hablante no mandó audio en este frame" en un error de decodificación.
        /// </summary>
        [Test]
        public void WriteBinEmitsEmptyBinForNullNeverNil()
        {
            var w = new MsgPackWriter();
            w.WriteBin(null);
            w.WriteInt(7);
            byte[] frame = w.ToArray();

            Assert.AreEqual(0xc4, frame[0], "debe ser bin8");
            Assert.AreEqual(0x00, frame[1], "de longitud cero");
            Assert.AreNotEqual(0xc0, frame[0], "nil rompería la deserialización de Vec<u8>");

            var r = new MsgPackReader(frame);
            Assert.IsEmpty(r.ReadBin());
            Assert.AreEqual(7, r.ReadInt(), "CENTINELA");
        }

        [Test]
        public void WriteBinSliceEmitsExactlyTheRequestedWindow()
        {
            byte[] source = BinPattern(64);

            var w = new MsgPackWriter();
            w.WriteBin(source, 8, 16);
            w.WriteInt(9);

            var r = new MsgPackReader(w.ToArray());
            byte[] slice = r.ReadBin();
            Assert.AreEqual(16, slice.Length);
            for (int i = 0; i < 16; i++)
                Assert.AreEqual(source[8 + i], slice[i], $"byte {i} de la ventana");
            Assert.AreEqual(9, r.ReadInt(), "CENTINELA");
        }

        [Test]
        public void WriteBinRejectsARangePastTheEndInsteadOfEmittingAdjacentMemory()
        {
            var w = new MsgPackWriter();
            Assert.Throws<ArgumentOutOfRangeException>(() => w.WriteBin(BinPattern(64), 60, 16));
        }

        /// <summary>
        /// Contrapartida obligatoria: un campo que el emisor codificó como OTRA cosa no puede
        /// dejar el frame ilegible. ReadBin consume el valor entero y devuelve vacío, así que
        /// los campos posteriores siguen decodificando — la misma tolerancia hacia delante que
        /// da el `else r.Skip()` de IPCMessages.
        /// </summary>
        [Test]
        public void ReadBinConsumesAWrongTypedValueWholeAndKeepsTheFrameReadable()
        {
            var ws = new MsgPackWriter();
            ws.WriteString("not binary");
            ws.WriteInt(1234);
            var rs = new MsgPackReader(ws.ToArray());
            Assert.IsEmpty(rs.ReadBin(), "un string donde se esperaba bin → vacío");
            Assert.AreEqual(1234, rs.ReadInt(), "el valor siguiente debe seguir decodificando");

            var wm = new MsgPackWriter();
            wm.WriteMapHeader(1);
            wm.WriteString("k");
            wm.WriteInt(5);
            wm.WriteInt(4321);
            var rm = new MsgPackReader(wm.ToArray());
            Assert.IsEmpty(rm.ReadBin(), "un map donde se esperaba bin → vacío");
            Assert.AreEqual(4321, rm.ReadInt(), "el cuerpo del map debe consumirse ENTERO");
        }

        [Test]
        public void ReadBinOnATruncatedFrameIsCaughtByTheBoundsCheck()
        {
            var w = new MsgPackWriter();
            w.WriteBin(BinPattern(40));
            byte[] full = w.ToArray();
            var cut = new byte[full.Length - 10];
            Array.Copy(full, cut, cut.Length);

            var r = new MsgPackReader(cut);
            Assert.Throws<Exception>(() => r.ReadBin(), "la cabecera promete más bytes de los que hay");
        }
    }
}
