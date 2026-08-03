using System;
using System.Text;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Minimal, dependency-free MessagePack encoder. Matches the wire format the
    /// Rust backend expects (rmp_serde::to_vec_named → maps keyed by field name).
    /// Only the subset needed for IPC is implemented.
    ///
    /// Allocation-free by construction: one grow-on-demand byte[] instead of a List&lt;byte&gt;,
    /// no throwaway array per float (a union struct reinterprets the bits) and no throwaway
    /// array per string (UTF-8 is encoded straight into the buffer). The previous shape cost
    /// ~40 allocations per SendPlayerInput, at 30 Hz.
    ///
    /// The buffer also reserves <see cref="FrameHeaderBytes"/> bytes at the FRONT for the
    /// 4-byte big-endian length prefix, so IPCClient can stamp the header in place and write
    /// the socket frame from this one buffer — no second array, no copy.
    /// CAREFUL: that reserve is invisible to the public surface. <see cref="Count"/> and
    /// <see cref="ToArray"/> report/return the message BODY only; only
    /// <see cref="StampFrameHeader"/> + <see cref="FrameBuffer"/> see the prefix.
    /// </summary>
    public sealed class MsgPackWriter
    {
        /// <summary>Bytes reserved at the head of the buffer for the length prefix.</summary>
        internal const int FrameHeaderBytes = 4;

        private byte[] _buf = new byte[256];
        private int _pos = FrameHeaderBytes;

        /// <summary>Length of the encoded message body (excludes the reserved length prefix).</summary>
        public int Count => _pos - FrameHeaderBytes;

        /// <summary>Copy of the message body. Allocates — used by tests and by any caller that
        /// needs a standalone array; the socket path uses <see cref="StampFrameHeader"/> instead.</summary>
        public byte[] ToArray()
        {
            var result = new byte[Count];
            Array.Copy(_buf, FrameHeaderBytes, result, 0, Count);
            return result;
        }

        /// <summary>Rewind for reuse across sends, keeping the grown buffer.</summary>
        public void Reset() => _pos = FrameHeaderBytes;

        /// <summary>Backing buffer, including the reserved 4-byte prefix at offset 0. Only valid
        /// up to the length <see cref="StampFrameHeader"/> returns.</summary>
        internal byte[] FrameBuffer => _buf;

        /// <summary>Writes the 4-byte big-endian body length into the reserved prefix and returns
        /// the TOTAL frame length (prefix + body) to hand to Stream.Write. Zero copies.</summary>
        internal int StampFrameHeader()
        {
            int len = Count;
            _buf[0] = (byte)(len >> 24);
            _buf[1] = (byte)(len >> 16);
            _buf[2] = (byte)(len >> 8);
            _buf[3] = (byte)len;
            return FrameHeaderBytes + len;
        }

        private void EnsureCapacity(int extra)
        {
            int needed = _pos + extra;
            if (needed <= _buf.Length) return;
            int cap = _buf.Length;
            while (cap < needed) cap <<= 1;
            Array.Resize(ref _buf, cap);
        }

        private void Add(byte b)
        {
            EnsureCapacity(1);
            _buf[_pos++] = b;
        }

        public void WriteMapHeader(int n)
        {
            if (n < 16) Add((byte)(0x80 | n));
            else if (n <= 0xffff) { Add(0xde); WriteU16((ushort)n); }
            else { Add(0xdf); WriteU32((uint)n); }
        }

        public void WriteArrayHeader(int n)
        {
            if (n < 16) Add((byte)(0x90 | n));
            else if (n <= 0xffff) { Add(0xdc); WriteU16((ushort)n); }
            else { Add(0xdd); WriteU32((uint)n); }
        }

        public void WriteString(string s)
        {
            s ??= string.Empty;
            // GetByteCount walks the string without allocating; the header must carry the BYTE
            // length (not the char count), so multibyte UTF-8 needs this pass before the header.
            int n = Encoding.UTF8.GetByteCount(s);
            if (n < 32) Add((byte)(0xa0 | n));
            else if (n <= 0xff) { Add(0xd9); Add((byte)n); }
            else if (n <= 0xffff) { Add(0xda); WriteU16((ushort)n); }
            else { Add(0xdb); WriteU32((uint)n); }

            EnsureCapacity(n);
            Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, _pos); // straight into the buffer
            _pos += n;
        }

        // Reinterprets a float's bits as uint with no allocation and no unsafe code.
        // BitConverter.SingleToInt32Bits would be cleaner but is not guaranteed on this
        // project's TargetFrameworkVersion (v4.7.1); this overlay works on every runtime.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float F;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint U;
        }

        public void WriteFloat(float f)
        {
            Add(0xca); // float32
            // WriteU32 emits MSB-first, i.e. big-endian as MessagePack requires — on both
            // little- and big-endian hosts, since the overlay yields the same bit pattern.
            var bits = new FloatBits { F = f };
            WriteU32(bits.U);
        }

        public void WriteBool(bool v) => Add(v ? (byte)0xc3 : (byte)0xc2);

        /// <summary>Opaque byte payload as the MessagePack bin family (0xc4/0xc5/0xc6), which is
        /// what <c>rmp_serde</c> expects for a <c>#[serde(with = "serde_bytes")] Vec&lt;u8&gt;</c>.
        ///
        /// ADR-046 Fase 0. Until now the writer had no way to emit binary at all, so a byte payload
        /// would have had to travel Base64'd inside a string: +33 % on the wire and a throwaway
        /// string per frame, at the voice cadence (25 Hz per speaker).
        ///
        /// A null or empty payload emits an EMPTY bin8, never nil: <c>Vec&lt;u8&gt;</c> deserializes
        /// from an empty bin and NOT from nil, so emitting nil would turn "this speaker sent no
        /// audio this frame" into a decode error on the backend.</summary>
        public void WriteBin(byte[] data) => WriteBin(data, 0, data?.Length ?? 0);

        /// <summary>Range overload — lets a caller emit a slice of a reused capture buffer without
        /// copying it into a right-sized array first.</summary>
        public void WriteBin(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0)
            {
                Add(0xc4);
                Add(0);
                return;
            }
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count), $"WriteBin range [{offset},{offset + count}) outside byte[{data.Length}]");

            if (count <= 0xff) { Add(0xc4); Add((byte)count); }
            else if (count <= 0xffff) { Add(0xc5); WriteU16((ushort)count); }
            else { Add(0xc6); WriteU32((uint)count); }

            EnsureCapacity(count);
            Array.Copy(data, offset, _buf, _pos, count);
            _pos += count;
        }

        public void WriteNil() => Add(0xc0);

        public void WriteInt(long n)
        {
            if (n >= 0)
            {
                if (n < 128) Add((byte)n);
                else if (n <= 0xff) { Add(0xcc); Add((byte)n); }
                else if (n <= 0xffff) { Add(0xcd); WriteU16((ushort)n); }
                else if (n <= 0xffffffffL) { Add(0xce); WriteU32((uint)n); }
                else { Add(0xcf); WriteU64((ulong)n); }
            }
            else
            {
                if (n >= -32) Add((byte)n); // negative fixint
                else if (n >= -128) { Add(0xd0); Add((byte)(sbyte)n); }
                else if (n >= -32768) { Add(0xd1); WriteU16((ushort)(short)n); }
                else if (n >= -2147483648L) { Add(0xd2); WriteU32((uint)(int)n); }
                else { Add(0xd3); WriteU64((ulong)n); }
            }
        }

        private void WriteU16(ushort v)
        {
            EnsureCapacity(2);
            _buf[_pos++] = (byte)(v >> 8);
            _buf[_pos++] = (byte)v;
        }

        private void WriteU32(uint v)
        {
            EnsureCapacity(4);
            _buf[_pos++] = (byte)(v >> 24);
            _buf[_pos++] = (byte)(v >> 16);
            _buf[_pos++] = (byte)(v >> 8);
            _buf[_pos++] = (byte)v;
        }

        private void WriteU64(ulong v)
        {
            EnsureCapacity(8);
            for (int i = 7; i >= 0; i--) _buf[_pos++] = (byte)(v >> (i * 8));
        }
    }
}
