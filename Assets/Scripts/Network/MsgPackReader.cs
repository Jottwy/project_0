using System;
using System.Collections.Generic;
using System.Text;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// MessagePack decoder over a shared byte buffer, with two APIs:
    ///
    ///  - <see cref="ReadValue"/>: materializes a generic object tree (Dictionary&lt;string,object&gt;
    ///    for maps, object[] for arrays, string/long/double/bool/null/byte[] for scalars). Used only
    ///    for <c>GameEventMsg.data</c> (low-frequency discrete events) — IPCParse.L/F/S/Vec3 consume
    ///    the resulting tree.
    ///  - Token API (ReadMapHeader/ReadArrayHeader/ReadKey/ReadInt/ReadFloat/.../Skip): walks the
    ///    same bytes without allocating a box or container per field. Every Parse(MsgPackReader) on
    ///    the hot path (10 Hz world state, chunk streaming) uses this instead.
    ///
    /// Both APIs share <see cref="_pos"/> and are bounds-checked against a LOGICAL length
    /// (<see cref="_len"/>) that can be smaller than the backing array — <see cref="Reset"/> lets a
    /// caller reuse one instance across frames with a buffer that only grows, never shrinks
    /// (IPCClient's per-connection frame buffer). Without that check, a malformed/truncated frame
    /// on a reused oversized buffer would silently read stale bytes from a PREVIOUS message instead
    /// of throwing — this way it throws just like the old per-message `new byte[len]` did.
    /// </summary>
    public sealed class MsgPackReader
    {
        private byte[] _data;
        private int _pos;
        private int _len;
        // Reused across ReadF32/ReadF64 calls on THIS instance — avoids a byte[] alloc per float,
        // which otherwise would have undone most of the token API's zero-alloc point (every
        // position/rotation/health/... field is msgpack float32 on the wire). Never shared across
        // instances/threads; each reader (one per IPC connection) owns its own.
        private readonly byte[] _scratch8 = new byte[8];

        public MsgPackReader(byte[] data) { _data = data; _pos = 0; _len = data?.Length ?? 0; }

        /// <summary>Rebind to a (possibly reused, possibly larger) buffer and rewind the cursor.
        /// <paramref name="length"/> is the logical frame length — bytes past it are never read,
        /// even if <paramref name="data"/>.Length is larger (a grown-on-demand connection buffer).</summary>
        public void Reset(byte[] data, int length)
        {
            _data = data;
            _pos = 0;
            _len = length;
        }

        public object ReadValue()
        {
            byte c = NextByte();

            if (c <= 0x7f) return (long)c;            // positive fixint
            if (c >= 0xe0) return (long)(sbyte)c;     // negative fixint
            if (c >= 0x80 && c <= 0x8f) return ReadMap(c & 0x0f);
            if (c >= 0x90 && c <= 0x9f) return ReadArray(c & 0x0f);
            if (c >= 0xa0 && c <= 0xbf) return ReadStr(c & 0x1f);

            switch (c)
            {
                case 0xc0: return null;
                case 0xc2: return false;
                case 0xc3: return true;
                case 0xcc: return (long)ReadU8();
                case 0xcd: return (long)ReadU16();
                case 0xce: return (long)ReadU32();
                case 0xcf: return (long)ReadU64();
                case 0xd0: return (long)(sbyte)ReadU8();
                case 0xd1: return (long)(short)ReadU16();
                case 0xd2: return (long)(int)ReadU32();
                case 0xd3: return (long)ReadU64();
                case 0xca: return (double)ReadF32();
                case 0xcb: return ReadF64();
                case 0xd9: return ReadStr(ReadU8());
                case 0xda: return ReadStr(ReadU16());
                case 0xdb: return ReadStr((int)ReadU32());
                case 0xdc: return ReadArray(ReadU16());
                case 0xdd: return ReadArray((int)ReadU32());
                case 0xde: return ReadMap(ReadU16());
                case 0xdf: return ReadMap((int)ReadU32());
                case 0xc4: return ReadBin(ReadU8());
                case 0xc5: return ReadBin(ReadU16());
                case 0xc6: return ReadBin((int)ReadU32());
                default: throw new Exception($"Unsupported MessagePack type 0x{c:x2}");
            }
        }

        private Dictionary<string, object> ReadMap(int n)
        {
            var d = new Dictionary<string, object>(n);
            for (int i = 0; i < n; i++)
            {
                object k = ReadValue();
                object v = ReadValue();
                d[k?.ToString() ?? "null"] = v;
            }
            return d;
        }

        private object[] ReadArray(int n)
        {
            var a = new object[n];
            for (int i = 0; i < n; i++) a[i] = ReadValue();
            return a;
        }

        private string ReadStr(int n)
        {
            CheckBounds(n);
            string s = Encoding.UTF8.GetString(_data, _pos, n);
            _pos += n;
            return s;
        }

        private byte[] ReadBin(int n)
        {
            CheckBounds(n);
            var b = new byte[n];
            Array.Copy(_data, _pos, b, 0, n);
            _pos += n;
            return b;
        }

        private byte ReadU8() => NextByte();

        private ushort ReadU16()
        {
            CheckBounds(2);
            ushort v = (ushort)((_data[_pos] << 8) | _data[_pos + 1]);
            _pos += 2;
            return v;
        }

        private uint ReadU32()
        {
            CheckBounds(4);
            uint v = ((uint)_data[_pos] << 24) | ((uint)_data[_pos + 1] << 16) | ((uint)_data[_pos + 2] << 8) | _data[_pos + 3];
            _pos += 4;
            return v;
        }

        private ulong ReadU64()
        {
            CheckBounds(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | _data[_pos + i];
            _pos += 8;
            return v;
        }

        private float ReadF32()
        {
            CheckBounds(4);
            _scratch8[0] = _data[_pos]; _scratch8[1] = _data[_pos + 1];
            _scratch8[2] = _data[_pos + 2]; _scratch8[3] = _data[_pos + 3];
            _pos += 4;
            if (BitConverter.IsLittleEndian)
            {
                byte t = _scratch8[0]; _scratch8[0] = _scratch8[3]; _scratch8[3] = t;
                t = _scratch8[1]; _scratch8[1] = _scratch8[2]; _scratch8[2] = t;
            }
            return BitConverter.ToSingle(_scratch8, 0);
        }

        private double ReadF64()
        {
            CheckBounds(8);
            for (int i = 0; i < 8; i++) _scratch8[i] = _data[_pos + i];
            _pos += 8;
            if (BitConverter.IsLittleEndian) Array.Reverse(_scratch8);
            return BitConverter.ToDouble(_scratch8, 0);
        }

        // ───────────────────────── Token API (zero-alloc) ─────────────────────────
        //
        // Usage pattern per Parse(MsgPackReader): read the map header, loop `n` times reading a
        // key span + dispatching on it, Skip() anything unrecognized (forward-compat — a newer
        // backend field must never throw). Order-independent by construction, EXCEPT the outer
        // {"type": ..., ...fields} envelope: serde's internally-tagged enum codegen always
        // serializes the tag field first (it controls emission order; only the DEserialize side
        // needs to tolerate arbitrary order), so IPCClient.Dispatch reads "type" as the first key
        // and hands the remaining pair count to the matched Parse(reader, remainingPairs).

        /// <summary>Map header token. Returns the pair count, or -1 if the value is nil.</summary>
        public int ReadMapHeader()
        {
            byte c = NextByte();
            if (c == 0xc0) return -1;
            if (c >= 0x80 && c <= 0x8f) return c & 0x0f;
            if (c == 0xde) return ReadU16();
            if (c == 0xdf) return (int)ReadU32();
            throw new Exception($"Expected map header, got 0x{c:x2}");
        }

        /// <summary>Array header token. Returns the element count, or -1 if the value is nil.</summary>
        public int ReadArrayHeader()
        {
            byte c = NextByte();
            if (c == 0xc0) return -1;
            if (c >= 0x90 && c <= 0x9f) return c & 0x0f;
            if (c == 0xdc) return ReadU16();
            if (c == 0xdd) return (int)ReadU32();
            throw new Exception($"Expected array header, got 0x{c:x2}");
        }

        /// <summary>
        /// Reads a map key as a span over the underlying buffer — zero allocation. A non-string
        /// key (malformed/unexpected wire) is consumed whole (Skip()) and an empty span returned;
        /// callers never match it and Skip() the paired value in turn.
        /// </summary>
        public ReadOnlySpan<byte> ReadKey()
        {
            byte c = NextByte();
            int n;
            if (c >= 0xa0 && c <= 0xbf) n = c & 0x1f;
            else if (c == 0xd9) n = NextByte();
            else if (c == 0xda) n = ReadU16();
            else if (c == 0xdb) n = (int)ReadU32();
            else { SkipValue(c); return ReadOnlySpan<byte>.Empty; }

            CheckBounds(n);
            var span = new ReadOnlySpan<byte>(_data, _pos, n);
            _pos += n;
            return span;
        }

        /// <summary>ASCII-key comparison against a raw key span — no allocation on either side.</summary>
        public static bool Is(ReadOnlySpan<byte> key, string ascii)
        {
            if (key.Length != ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
                if (key[i] != (byte)ascii[i]) return false;
            return true;
        }

        /// <summary>Reads an int-family or float-family value as long, matching IPCParse.ToLong's
        /// int↔double tolerance (a field the backend sends as f64 still decodes).</summary>
        public long ReadInt()
        {
            byte c = NextByte();
            if (c <= 0x7f) return c;
            if (c >= 0xe0) return (sbyte)c;
            switch (c)
            {
                case 0xc0: return 0L; // nil ⇒ default, mirrors IPCParse.Get returning null
                case 0xcc: return ReadU8();
                case 0xcd: return ReadU16();
                case 0xce: return ReadU32();
                case 0xcf: return (long)ReadU64();
                case 0xd0: return (sbyte)ReadU8();
                case 0xd1: return (short)ReadU16();
                case 0xd2: return (int)ReadU32();
                case 0xd3: return (long)ReadU64();
                case 0xca: return (long)ReadF32();
                case 0xcb: return (long)ReadF64();
                default: throw new Exception($"Expected int-like value, got 0x{c:x2}");
            }
        }

        /// <summary>Reads a float-family or int-family value as float, matching IPCParse.ToFloat.</summary>
        public float ReadFloat()
        {
            byte c = NextByte();
            if (c <= 0x7f) return c;
            if (c >= 0xe0) return (sbyte)c;
            switch (c)
            {
                case 0xc0: return 0f;
                case 0xcc: return ReadU8();
                case 0xcd: return ReadU16();
                case 0xce: return ReadU32();
                case 0xcf: return ReadU64();
                case 0xd0: return (sbyte)ReadU8();
                case 0xd1: return (short)ReadU16();
                case 0xd2: return (int)ReadU32();
                case 0xd3: return (long)ReadU64();
                case 0xca: return ReadF32();
                case 0xcb: return (float)ReadF64();
                default: throw new Exception($"Expected float-like value, got 0x{c:x2}");
            }
        }

        /// <summary>Reads a bool value. Anything else (including nil) reads as false, matching
        /// IPCParse.B's `Get(d, key) is bool b &amp;&amp; b` fallback.</summary>
        public bool ReadBool()
        {
            byte c = NextByte();
            if (c == 0xc3) return true;
            if (c == 0xc2) return false;
            SkipValue(c);
            return false;
        }

        /// <summary>Reads a string value fresh (no caching). Matches IPCParse.S's "" fallback
        /// for nil/non-string.</summary>
        public string ReadString()
        {
            byte c = NextByte();
            int n;
            if (c >= 0xa0 && c <= 0xbf) n = c & 0x1f;
            else if (c == 0xd9) n = NextByte();
            else if (c == 0xda) n = ReadU16();
            else if (c == 0xdb) n = (int)ReadU32();
            else { SkipValue(c); return ""; }

            CheckBounds(n);
            string s = Encoding.UTF8.GetString(_data, _pos, n);
            _pos += n;
            return s;
        }

        /// <summary>Typed counterpart of <see cref="MsgPackWriter.WriteBin"/>: bin8/16/32 → byte[].
        /// ADR-046 Fase 0, the read half of the same gap.
        ///
        /// Any other type — including nil, and including a value from a newer backend this client
        /// does not understand — is CONSUMED via <see cref="SkipValue"/> and reported as an empty
        /// array. Returning early without consuming would leave the cursor mid-value and corrupt
        /// every remaining field of the frame; that misalignment is precisely the failure the
        /// ReadIntArray2 sentinel was added to catch.</summary>
        public byte[] ReadBin()
        {
            byte c = NextByte();
            int n;
            if (c == 0xc4) n = NextByte();
            else if (c == 0xc5) n = ReadU16();
            else if (c == 0xc6) n = (int)ReadU32();
            else { SkipValue(c); return Array.Empty<byte>(); }

            if (n <= 0) return Array.Empty<byte>();
            CheckBounds(n);
            var b = new byte[n];
            Array.Copy(_data, _pos, b, 0, n);
            _pos += n;
            return b;
        }

        // ── C5: bounded string interning ──────────────────────────────────────
        //
        // Applied by callers to exactly 5 low-cardinality fields (state/animation/entity_type/
        // item_type/kind — a handful of repeated values decoded once per entity per snapshot).
        // Deliberately NOT applied to name/source or anything else of open cardinality: a cache
        // over those is a slow leak wearing a performance-fix costume — bad hit rate, unbounded
        // growth. Per-INSTANCE (one MsgPackReader per IPC connection, never static/shared), hard
        // cap, stops caching once full (never evicts/grows past it). No explicit clear-on-
        // disconnect call exists because none is needed: IPCClient.ReadFrames declares its
        // `reader` as a local, so it (and this cache) is discarded when that call returns
        // (connection drop) and a fresh instance is built on the next reconnect. Comparison is
        // exact byte-for-byte against the original UTF-8 bytes — never assumes ASCII, never hashes.
        private struct CachedStr { public byte[] Bytes; public string Value; }
        private readonly List<CachedStr> _stringCache = new List<CachedStr>();
        private const int StringCacheCapacity = 64;

        /// <summary>Reads a string value, reusing a cached managed string instance when the
        /// decoded bytes exactly match one already seen on THIS reader. Same nil/non-string
        /// fallback as <see cref="ReadString"/> ("").</summary>
        public string ReadStringCached()
        {
            byte c = NextByte();
            int n;
            if (c >= 0xa0 && c <= 0xbf) n = c & 0x1f;
            else if (c == 0xd9) n = NextByte();
            else if (c == 0xda) n = ReadU16();
            else if (c == 0xdb) n = (int)ReadU32();
            else { SkipValue(c); return ""; }

            CheckBounds(n);
            int start = _pos;
            for (int i = 0; i < _stringCache.Count; i++)
            {
                var entry = _stringCache[i];
                if (entry.Bytes.Length == n && BytesEqual(entry.Bytes, _data, start, n))
                {
                    _pos += n;
                    return entry.Value;
                }
            }

            string s = Encoding.UTF8.GetString(_data, start, n);
            _pos += n;
            if (_stringCache.Count < StringCacheCapacity)
            {
                var bytes = new byte[n];
                Array.Copy(_data, start, bytes, 0, n);
                _stringCache.Add(new CachedStr { Bytes = bytes, Value = s });
            }
            return s;
        }

        private static bool BytesEqual(byte[] a, byte[] data, int offset, int n)
        {
            for (int i = 0; i < n; i++)
                if (a[i] != data[offset + i]) return false;
            return true;
        }

        /// <summary>Fills <paramref name="dest"/> from a msgpack array, zeroing slots past what
        /// the wire carried — mirrors IPCParse.FillIntArray.</summary>
        public void ReadIntArrayInto(int[] dest)
        {
            int n = ReadArrayHeader();
            if (n < 0) n = 0;
            for (int i = 0; i < dest.Length; i++)
                dest[i] = i < n ? (int)ReadInt() : 0;
            for (int i = dest.Length; i < n; i++)
                Skip(); // wire had more slots than dest — consume and discard, never truncate the frame
        }

        /// <summary>Reads a 3-float array as Vector3, or Vector3.zero for nil/short arrays —
        /// mirrors IPCParse.Vec3.</summary>
        public UnityEngine.Vector3 ReadVec3()
        {
            int n = ReadArrayHeader();
            if (n < 3)
            {
                for (int i = 0; i < n; i++) Skip();
                return UnityEngine.Vector3.zero;
            }
            float x = ReadFloat(), y = ReadFloat(), z = ReadFloat();
            for (int i = 3; i < n; i++) Skip();
            return new UnityEngine.Vector3(x, y, z);
        }

        /// <summary>2-int array, defaulting to [0,0] for nil/short arrays — mirrors IPCParse.IntArray2.</summary>
        public int[] ReadIntArray2()
        {
            int n = ReadArrayHeader();
            if (n < 2)
            {
                for (int i = 0; i < n; i++) Skip();
                return new[] { 0, 0 };
            }
            var a = new[] { (int)ReadInt(), (int)ReadInt() };
            for (int i = 2; i < n; i++) Skip();
            return a;
        }

        /// <summary>Variable-length int array — mirrors IPCParse.IntArray.</summary>
        public int[] ReadIntArray()
        {
            int n = ReadArrayHeader();
            if (n <= 0) return Array.Empty<int>();
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = (int)ReadInt();
            return a;
        }

        /// <summary>Variable-length string array — mirrors IPCParse.StringArray.</summary>
        public string[] ReadStringArray()
        {
            int n = ReadArrayHeader();
            if (n <= 0) return Array.Empty<string>();
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = ReadString();
            return a;
        }

        /// <summary>Byte array with the same clamp-to-[0,255] as IPCParse.ByteArray (the backend
        /// never sends an out-of-range value here; the clamp is defensive, kept for parity).</summary>
        public byte[] ReadByteArrayValues()
        {
            int n = ReadArrayHeader();
            if (n <= 0) return Array.Empty<byte>();
            var a = new byte[n];
            for (int i = 0; i < n; i++)
            {
                long v = ReadInt();
                a[i] = (byte)(v < 0 ? 0 : (v > byte.MaxValue ? byte.MaxValue : v));
            }
            return a;
        }

        /// <summary>ushort array with the same clamp as IPCParse.UShortArray.</summary>
        public ushort[] ReadUShortArrayValues()
        {
            int n = ReadArrayHeader();
            if (n <= 0) return Array.Empty<ushort>();
            var a = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                long v = ReadInt();
                a[i] = (ushort)(v < 0 ? 0 : (v > ushort.MaxValue ? ushort.MaxValue : v));
            }
            return a;
        }

        /// <summary>Consumes exactly one value of any type — map/array recurse into their
        /// elements, everything else advances past its own bytes. Never throws on a type this
        /// client doesn't otherwise handle (ext/fixext included) — forward-compat for unknown
        /// fields from a newer backend.</summary>
        public void Skip() => SkipValue(NextByte());

        private void SkipValue(byte c)
        {
            if (c <= 0x7f || c >= 0xe0) return; // fixint (positive/negative)
            if (c >= 0x80 && c <= 0x8f) { SkipMapBody(c & 0x0f); return; }
            if (c >= 0x90 && c <= 0x9f) { SkipArrayBody(c & 0x0f); return; }
            if (c >= 0xa0 && c <= 0xbf) { Advance(c & 0x1f); return; }

            switch (c)
            {
                case 0xc0: case 0xc2: case 0xc3: return; // nil, false, true
                case 0xcc: case 0xd0: Advance(1); return;
                case 0xcd: case 0xd1: case 0xd4: Advance(2); return; // + fixext1 (1 type + 1 data)
                case 0xd5: Advance(3); return; // fixext2 (1 type + 2 data) — NOT the same size as uint32/int32/float32 below
                case 0xce: case 0xd2: case 0xca: Advance(4); return;
                case 0xcf: case 0xd3: case 0xcb: Advance(8); return;
                case 0xd6: Advance(5); return; // fixext4
                case 0xd7: Advance(9); return; // fixext8
                case 0xd8: Advance(17); return; // fixext16
                case 0xd9: Advance(NextByte()); return;
                case 0xda: Advance(ReadU16()); return;
                case 0xdb: Advance((int)ReadU32()); return;
                case 0xdc: SkipArrayBody(ReadU16()); return;
                case 0xdd: SkipArrayBody((int)ReadU32()); return;
                case 0xde: SkipMapBody(ReadU16()); return;
                case 0xdf: SkipMapBody((int)ReadU32()); return;
                case 0xc4: Advance(NextByte()); return;
                case 0xc5: Advance(ReadU16()); return;
                case 0xc6: Advance((int)ReadU32()); return;
                case 0xc7: { int len = NextByte(); Advance(1 + len); return; } // ext8: type byte + payload
                case 0xc8: { int len = ReadU16(); Advance(1 + len); return; } // ext16
                case 0xc9: { int len = (int)ReadU32(); Advance(1 + len); return; } // ext32
                default: throw new Exception($"Unsupported MessagePack type 0x{c:x2}");
            }
        }

        private void SkipMapBody(int n) { for (int i = 0; i < n; i++) { Skip(); Skip(); } }
        private void SkipArrayBody(int n) { for (int i = 0; i < n; i++) Skip(); }

        private byte NextByte()
        {
            CheckBounds(1);
            return _data[_pos++];
        }

        private void Advance(int n)
        {
            CheckBounds(n);
            _pos += n;
        }

        /// <summary>Guards every read against the LOGICAL frame length, not just the backing
        /// array's physical length — see the class doc comment for why this matters once
        /// <see cref="Reset"/> lets the array outlive the frame it currently holds.</summary>
        private void CheckBounds(int n)
        {
            if (_pos + n > _len)
                throw new Exception($"MessagePack frame truncated: need {n} byte(s) at {_pos}, logical length {_len}");
        }
    }
}
