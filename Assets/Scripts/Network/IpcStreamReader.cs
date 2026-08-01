using System;
using System.IO;
using System.Net.Sockets;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// The blocking-read half of <see cref="IPCClient"/>'s network loop, split out so it can be
    /// exercised against a real socket without a MonoBehaviour or a live Unity player.
    ///
    /// This is the one piece of the IPC path whose failure mode is a HANG rather than a wrong
    /// value — if it stops honouring the running-flag, app teardown blocks on Thread.Join and the
    /// editor/player wedges on quit. That is why it lives in its own testable unit while the rest
    /// of the send/receive bodies stay inline in IPCClient.
    ///
    /// Public rather than internal purely so the EditMode suite can reach it: it sits in the same
    /// namespace as the already-public MsgPackReader/MsgPackWriter, and InternalsVisibleTo is not
    /// an option here because tools/dev/CompileCheckClient.sh builds each assembly under a
    /// `_check` suffix, so the friend name would never match under the project's own gate.
    /// Treat it as IPCClient's implementation detail regardless — nothing else should call it.
    /// </summary>
    public static class IpcStreamReader
    {
        /// <summary>Overall per-frame deadline: how long to wait for the bytes of one frame
        /// before declaring the connection dead.</summary>
        public const int ReadTimeoutMs = 5000;

        /// <summary>
        /// Socket-level wait slice. Replaces the old `DataAvailable` + Thread.Sleep(1) spin,
        /// which burned ~100 wakeups per inter-snapshot gap at 10 Hz just to find no data.
        /// Short enough that the loop re-checks the running flag promptly during shutdown even
        /// if closing the stream fails to unblock the read; long enough that idling is free.
        /// </summary>
        public const int ReadSliceMs = 250;

        /// <summary>
        /// Fills <paramref name="buf"/>[0..<paramref name="count"/>) from <paramref name="stream"/>.
        /// Returns false on timeout, socket error, stream close, or when
        /// <paramref name="isRunning"/> goes false — the caller treats any false as
        /// "connection over, reconnect".
        /// </summary>
        public static bool ReadExactly(Stream stream, byte[] buf, int count, Func<bool> isRunning)
        {
            int offset = 0;
            int start = Environment.TickCount;

            try { stream.ReadTimeout = ReadSliceMs; }
            catch (Exception) { return false; } // already closed/disposed, or not timeout-capable

            while (offset < count && isRunning())
            {
                int read;
                try
                {
                    read = stream.Read(buf, offset, count - offset);
                }
                catch (IOException e)
                {
                    // A read timeout is the expected idle case (no data this slice) — keep
                    // waiting until the FULL frame timeout so disconnect semantics stay exactly
                    // what they were before the spin was removed. Any other IO error is a real
                    // break: bail immediately instead of stalling for the rest of the 5 s.
                    if (!IsTimeout(e)) return false;

                    // Unchecked int delta, NOT `TickCount + timeout` compared against a later
                    // TickCount: Environment.TickCount is a 32-bit ms counter that wraps every
                    // ~24.9 days of OS uptime. The old form computed the deadline in int before
                    // widening to long, so near the wrap it overflowed to negative and every
                    // comparison read as "expired" — a spurious disconnect loop on any machine
                    // up that long. Subtracting two samples is correct across the wrap.
                    if (unchecked(Environment.TickCount - start) >= ReadTimeoutMs) return false;
                    continue;
                }
                catch (Exception) { return false; } // disposed by Shutdown, connection reset, ...

                if (read <= 0) return false; // orderly close by the peer
                offset += read;
                start = Environment.TickCount;
            }

            return offset == count;
        }

        /// <summary>
        /// True when an IOException is just this slice expiring with no data. Mono and .NET both
        /// wrap the socket error, but the exact nesting differs, so walk the chain rather than
        /// inspecting only InnerException.
        /// </summary>
        private static bool IsTimeout(Exception e)
        {
            for (Exception cur = e; cur != null; cur = cur.InnerException)
                if (cur is SocketException se)
                    return se.SocketErrorCode == SocketError.TimedOut;
            return false;
        }
    }
}
