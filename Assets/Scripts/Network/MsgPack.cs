using System;
using System.Collections.Generic;
using System.Text;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Minimal, dependency-free MessagePack encoder. Matches the wire format the
    /// Rust backend expects (rmp_serde::to_vec_named → maps keyed by field name).
    /// Only the subset needed for IPC is implemented.
    /// </summary>
    public sealed class MsgPackWriter
    {
        private readonly List<byte> _buf = new List<byte>(256);

        public byte[] ToArray() => _buf.ToArray();
        public int Count => _buf.Count;

        public void WriteMapHeader(int n)
        {
            if (n < 16) _buf.Add((byte)(0x80 | n));
            else if (n <= 0xffff) { _buf.Add(0xde); WriteU16((ushort)n); }
            else { _buf.Add(0xdf); WriteU32((uint)n); }
        }

        public void WriteArrayHeader(int n)
        {
            if (n < 16) _buf.Add((byte)(0x90 | n));
            else if (n <= 0xffff) { _buf.Add(0xdc); WriteU16((ushort)n); }
            else { _buf.Add(0xdd); WriteU32((uint)n); }
        }

        public void WriteString(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            int n = bytes.Length;
            if (n < 32) _buf.Add((byte)(0xa0 | n));
            else if (n <= 0xff) { _buf.Add(0xd9); _buf.Add((byte)n); }
            else if (n <= 0xffff) { _buf.Add(0xda); WriteU16((ushort)n); }
            else { _buf.Add(0xdb); WriteU32((uint)n); }
            _buf.AddRange(bytes);
        }

        public void WriteFloat(float f)
        {
            _buf.Add(0xca); // float32
            byte[] b = BitConverter.GetBytes(f);
            if (BitConverter.IsLittleEndian) Array.Reverse(b); // MessagePack is big-endian
            _buf.AddRange(b);
        }

        public void WriteBool(bool v) => _buf.Add(v ? (byte)0xc3 : (byte)0xc2);

        public void WriteNil() => _buf.Add(0xc0);

        public void WriteInt(long n)
        {
            if (n >= 0)
            {
                if (n < 128) _buf.Add((byte)n);
                else if (n <= 0xff) { _buf.Add(0xcc); _buf.Add((byte)n); }
                else if (n <= 0xffff) { _buf.Add(0xcd); WriteU16((ushort)n); }
                else if (n <= 0xffffffffL) { _buf.Add(0xce); WriteU32((uint)n); }
                else { _buf.Add(0xcf); WriteU64((ulong)n); }
            }
            else
            {
                if (n >= -32) _buf.Add((byte)n); // negative fixint
                else if (n >= -128) { _buf.Add(0xd0); _buf.Add((byte)(sbyte)n); }
                else if (n >= -32768) { _buf.Add(0xd1); WriteU16((ushort)(short)n); }
                else if (n >= -2147483648L) { _buf.Add(0xd2); WriteU32((uint)(int)n); }
                else { _buf.Add(0xd3); WriteU64((ulong)n); }
            }
        }

        private void WriteU16(ushort v) { _buf.Add((byte)(v >> 8)); _buf.Add((byte)v); }
        private void WriteU32(uint v) { _buf.Add((byte)(v >> 24)); _buf.Add((byte)(v >> 16)); _buf.Add((byte)(v >> 8)); _buf.Add((byte)v); }
        private void WriteU64(ulong v) { for (int i = 7; i >= 0; i--) _buf.Add((byte)(v >> (i * 8))); }
    }

    /// <summary>
    /// Minimal MessagePack decoder. Produces a generic object tree:
    /// Dictionary&lt;string,object&gt; for maps, object[] for arrays, plus
    /// string / long / double / bool / null / byte[]. Sufficient to read every
    /// message the backend sends.
    /// </summary>
    public sealed class MsgPackReader
    {
        private readonly byte[] _data;
        private int _pos;

        public MsgPackReader(byte[] data) { _data = data; _pos = 0; }

        public object ReadValue()
        {
            byte c = _data[_pos++];

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
            string s = Encoding.UTF8.GetString(_data, _pos, n);
            _pos += n;
            return s;
        }

        private byte[] ReadBin(int n)
        {
            var b = new byte[n];
            Array.Copy(_data, _pos, b, 0, n);
            _pos += n;
            return b;
        }

        private byte ReadU8() => _data[_pos++];

        private ushort ReadU16()
        {
            ushort v = (ushort)((_data[_pos] << 8) | _data[_pos + 1]);
            _pos += 2;
            return v;
        }

        private uint ReadU32()
        {
            uint v = ((uint)_data[_pos] << 24) | ((uint)_data[_pos + 1] << 16) | ((uint)_data[_pos + 2] << 8) | _data[_pos + 3];
            _pos += 4;
            return v;
        }

        private ulong ReadU64()
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | _data[_pos + i];
            _pos += 8;
            return v;
        }

        private float ReadF32()
        {
            byte[] b = { _data[_pos], _data[_pos + 1], _data[_pos + 2], _data[_pos + 3] };
            _pos += 4;
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToSingle(b, 0);
        }

        private double ReadF64()
        {
            byte[] b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = _data[_pos + i];
            _pos += 8;
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToDouble(b, 0);
        }
    }
}
