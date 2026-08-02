using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-046 — captura de micrófono por WinMM (`waveIn`), como alternativa a
    /// <c>UnityEngine.Microphone</c> cuando ésta devuelve silencio.
    ///
    /// POR QUÉ EXISTE. Una interfaz de audio expone sus entradas como un endpoint ESTÉREO
    /// ("Analogue 1 + 2"). `Microphone.Start` **no admite pedir número de canales**: Unity negocia
    /// solo, con ese endpoint abre en mono y el driver le entrega ceros. Medido: pico exactamente
    /// 0,0000 con el buffer avanzando a la cadencia correcta, mientras la propia prueba de Windows
    /// sobre el mismo aparato daba 99 % de volumen. No es el micrófono ni la ganancia: es que la
    /// API de Unity no puede expresar lo que ese dispositivo necesita.
    ///
    /// `winmm.dll` es de Windows, no una librería de terceros: **no se vendoriza ni un archivo** y
    /// la decisión de ADR-046 de no depender de binarios por plataforma sigue intacta. El precio
    /// es que esto es código SOLO para Windows, y por eso <see cref="IsSupported"/> existe y la
    /// ruta de `Microphone` se conserva para todo lo demás.
    ///
    /// SIN CALLBACK NATIVO, a propósito. `waveInOpen` admite una función de callback, y marshalar
    /// un delegado que el driver invoca desde su propio hilo es la vía corta a un crash difícil de
    /// reproducir en Unity. Aquí se abre con `CALLBACK_NULL` y se SONDEA la bandera `WHDR_DONE` de
    /// cada buffer desde el hilo principal: más simple, y el coste es mirar ocho enteros por frame.
    /// </summary>
    public sealed class WinMmCapture : IDisposable
    {
        public static bool IsSupported =>
            Application.platform == RuntimePlatform.WindowsPlayer ||
            Application.platform == RuntimePlatform.WindowsEditor;

        /// <summary>Buffers en circulación. Ocho de 20 ms dan 160 ms de colchón: suficiente para
        /// un hipo del hilo principal sin que el driver se quede sin sitio donde escribir.</summary>
        private const int BufferCount = 8;

        private const int WaveFormatPcm = 1;
        private const uint CallbackNull = 0x00000000;
        private const uint WhdrDone = 0x00000001;
        private const uint WaveMapper = 0xFFFFFFFF;

        public bool IsRunning { get; private set; }
        public int Channels { get; private set; }
        public int SampleRate { get; private set; }

        /// <summary>Nombre real del dispositivo abierto, tal como lo reporta el driver.</summary>
        public string DeviceName { get; private set; } = "";

        private IntPtr _handle;
        private IntPtr[] _headers;
        private IntPtr[] _buffers;
        private int _bufferBytes;
        private int _next;   // siguiente buffer a examinar, en orden circular

        /// <summary>
        /// Abre el dispositivo cuyo nombre EMPIECE por <paramref name="deviceHint"/>.
        ///
        /// Coincidencia por prefijo y no exacta: `waveInGetDevCaps` trunca el nombre a 31
        /// caracteres, así que "Analogue 1 + 2 (Focusrite USB Audio)" llega recortado y una
        /// comparación exacta contra la lista de Unity no casaría nunca.
        /// </summary>
        public bool Start(string deviceHint, int sampleRate, int channels)
        {
            if (!IsSupported) return false;
            Stop();

            uint deviceId = ResolveDevice(deviceHint, out string resolvedName);
            var format = new WaveFormatEx
            {
                wFormatTag = WaveFormatPcm,
                nChannels = (ushort)Mathf.Clamp(channels, 1, 8),
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 16,
                cbSize = 0,
            };
            format.nBlockAlign = (ushort)(format.nChannels * format.wBitsPerSample / 8);
            format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

            int result = waveInOpen(out _handle, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull);
            if (result != 0)
            {
                Debug.LogWarning($"[WinMm] waveInOpen fallo ({result}) para '{deviceHint}' " +
                                 $"a {sampleRate} Hz {channels} canales.");
                _handle = IntPtr.Zero;
                return false;
            }

            Channels = format.nChannels;
            SampleRate = sampleRate;
            DeviceName = resolvedName;

            // 20 ms por buffer, en la unidad que el driver entiende: muestras × canales × 2 bytes.
            _bufferBytes = sampleRate / 50 * format.nBlockAlign;
            _headers = new IntPtr[BufferCount];
            _buffers = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(_bufferBytes);
                var hdr = new WaveHdr { lpData = _buffers[i], dwBufferLength = (uint)_bufferBytes };
                _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
                Marshal.StructureToPtr(hdr, _headers[i], false);

                if (waveInPrepareHeader(_handle, _headers[i], (uint)Marshal.SizeOf<WaveHdr>()) != 0 ||
                    waveInAddBuffer(_handle, _headers[i], (uint)Marshal.SizeOf<WaveHdr>()) != 0)
                {
                    Debug.LogWarning("[WinMm] No se pudo preparar el buffer " + i);
                    Stop();
                    return false;
                }
            }

            if (waveInStart(_handle) != 0)
            {
                Debug.LogWarning("[WinMm] waveInStart fallo.");
                Stop();
                return false;
            }

            _next = 0;
            IsRunning = true;
            Debug.Log($"[WinMm] Capturando '{DeviceName}' a {SampleRate} Hz, {Channels} canales, " +
                      $"{BufferCount} buffers de {_bufferBytes} B.");
            return true;
        }

        /// <summary>
        /// Vuelca a <paramref name="dest"/> las muestras INTERCALADAS que el driver haya
        /// completado, y devuelve cuántas escribió. Cero significa "aún no hay una trama entera",
        /// que es normal y no un error.
        ///
        /// Cada buffer consumido se REENCOLA inmediatamente: si no, el driver se queda sin sitio a
        /// los 160 ms y la captura se para en silencio.
        /// </summary>
        public int Read(short[] dest, int maxSamples)
        {
            if (!IsRunning || dest == null) return 0;
            int written = 0;

            for (int scanned = 0; scanned < BufferCount; scanned++)
            {
                var hdr = Marshal.PtrToStructure<WaveHdr>(_headers[_next]);
                if ((hdr.dwFlags & WhdrDone) == 0) break; // el resto tampoco estará listo

                int samples = (int)hdr.dwBytesRecorded / 2;
                if (samples > 0 && written + samples <= maxSamples)
                {
                    Marshal.Copy(hdr.lpData, dest, written, samples);
                    written += samples;
                }
                else if (samples > 0)
                {
                    break; // no cabe: se deja el buffer para la próxima llamada, sin reencolar
                }

                hdr.dwFlags &= ~WhdrDone;
                hdr.dwBytesRecorded = 0;
                Marshal.StructureToPtr(hdr, _headers[_next], false);
                waveInAddBuffer(_handle, _headers[_next], (uint)Marshal.SizeOf<WaveHdr>());

                _next = (_next + 1) % BufferCount;
            }
            return written;
        }

        public void Stop()
        {
            IsRunning = false;
            if (_handle != IntPtr.Zero)
            {
                // `waveInReset` marca todos los buffers como hechos y los devuelve; sin él,
                // `waveInUnprepareHeader` falla sobre los que siguen en la cola del driver.
                waveInReset(_handle);
                if (_headers != null)
                {
                    for (int i = 0; i < _headers.Length; i++)
                    {
                        if (_headers[i] == IntPtr.Zero) continue;
                        waveInUnprepareHeader(_handle, _headers[i], (uint)Marshal.SizeOf<WaveHdr>());
                        Marshal.FreeHGlobal(_headers[i]);
                        _headers[i] = IntPtr.Zero;
                    }
                }
                waveInClose(_handle);
                _handle = IntPtr.Zero;
            }

            if (_buffers != null)
            {
                for (int i = 0; i < _buffers.Length; i++)
                {
                    if (_buffers[i] == IntPtr.Zero) continue;
                    Marshal.FreeHGlobal(_buffers[i]);
                    _buffers[i] = IntPtr.Zero;
                }
            }
            _headers = null;
            _buffers = null;
        }

        public void Dispose() => Stop();

        private static uint ResolveDevice(string hint, out string resolved)
        {
            resolved = "(predeterminado)";
            if (string.IsNullOrEmpty(hint)) return WaveMapper;

            uint count = waveInGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                var caps = new WaveInCaps();
                if (waveInGetDevCapsW(i, ref caps, (uint)Marshal.SizeOf<WaveInCaps>()) != 0) continue;
                string name = caps.szPname ?? "";
                if (string.IsNullOrEmpty(name)) continue;

                // El nombre del driver viene truncado a 31 caracteres: se compara por prefijo en
                // los dos sentidos para que case tanto si el corto está dentro del largo como al revés.
                if (hint.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(hint, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = name;
                    return i;
                }
            }
            Debug.LogWarning($"[WinMm] No encontre '{hint}' entre los {count} dispositivos de " +
                             "waveIn; uso el predeterminado del sistema.");
            return WaveMapper;
        }

        // ── P/Invoke ─────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WaveInCaps
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveInGetDevCapsW(uint deviceId, ref WaveInCaps caps, uint size);
        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr handle, uint deviceId, ref WaveFormatEx format,
            IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);
    }
}
