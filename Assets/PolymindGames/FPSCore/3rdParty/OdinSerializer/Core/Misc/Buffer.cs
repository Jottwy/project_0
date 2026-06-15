//-----------------------------------------------------------------------
// <copyright file="Buffer.cs" company="Sirenix IVS">
// Copyright (c) 2018 Sirenix IVS
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//-----------------------------------------------------------------------

namespace PolymindGames.OdinSerializer
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Provides a way of claiming and releasing cached array buffers.
    /// </summary>
    /// <typeparam name="T">The element type of the array to buffer.</typeparam>
    /// <seealso cref="System.IDisposable" />
    public sealed class Buffer<T> : IDisposable
    {
        private static readonly object s_Lock = new();
        private static readonly List<Buffer<T>> s_FreeBuffers = new();

        private readonly int m_Count;
        private readonly T[] m_Array;
        private volatile bool m_IsFree;

        private Buffer(int count)
        {
            m_Array = new T[count];
            m_Count = count;
            m_IsFree = false; // Always start as non-free
        }

        /// <summary>
        /// Gets the total element count of the buffered array. This will always be a power of two.
        /// </summary>
        /// <value>
        /// The total element count of the buffered array.
        /// </value>
        /// <exception cref="System.InvalidOperationException">Cannot access a buffer while it is freed.</exception>
        public int Count
        {
            get
            {
                if (m_IsFree)
                {
                    throw new InvalidOperationException("Cannot access a buffer while it is freed.");
                }

                return m_Count;
            }
        }

        /// <summary>
        /// Gets the buffered array.
        /// </summary>
        /// <value>
        /// The buffered array.
        /// </value>
        /// <exception cref="System.InvalidOperationException">Cannot access a buffer while it is freed.</exception>
        public T[] Array
        {
            get
            {
                if (m_IsFree)
                {
                    throw new InvalidOperationException("Cannot access a buffer while it is freed.");
                }

                return m_Array;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this buffer is free.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this buffer is free; otherwise, <c>false</c>.
        /// </value>
        public bool IsFree => m_IsFree;

        /// <summary>
        /// Claims a buffer with the specified minimum capacity. Note: buffers always have a capacity equal to or larger than 256.
        /// </summary>
        /// <param name="minimumCapacity">The minimum capacity.</param>
        /// <returns>A buffer which has a capacity equal to or larger than the specified minimum capacity.</returns>
        /// <exception cref="System.ArgumentException">Requested size of buffer must be larger than 0.</exception>
        public static Buffer<T> Claim(int minimumCapacity)
        {
            if (minimumCapacity < 0)
            {
                throw new ArgumentException("Requested size of buffer must be larger than or equal to 0.");
            }

            if (minimumCapacity < 256)
            {
                minimumCapacity = 256; // Minimum buffer size
            }

            Buffer<T> result = null;

            lock (s_Lock)
            {
                // Search for a free buffer of sufficient size
                for (int i = 0; i < s_FreeBuffers.Count; i++)
                {
                    var buffer = s_FreeBuffers[i];

                    if (buffer != null && buffer.m_Count >= minimumCapacity)
                    {
                        result = buffer;
                        result.m_IsFree = false;
                        s_FreeBuffers[i] = null;
                        break;
                    }
                }
            }

            if (result == null)
            {
                // Allocate new buffer
                result = new Buffer<T>(NextPowerOfTwo(minimumCapacity));
            }

            return result;
        }

        /// <summary>
        /// Frees the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to free.</param>
        /// <exception cref="System.ArgumentNullException">The buffer argument is null.</exception>
        public static void Free(Buffer<T> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (buffer.m_IsFree == false)
            {
                lock (s_Lock)
                {
                    if (buffer.m_IsFree == false)
                    {
                        buffer.m_IsFree = true;

                        bool added = false;

                        for (int i = 0; i < s_FreeBuffers.Count; i++)
                        {
                            if (s_FreeBuffers[i] == null)
                            {
                                s_FreeBuffers[i] = buffer;
                                added = true;
                                break;
                            }
                        }

                        if (!added)
                        {
                            s_FreeBuffers.Add(buffer);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Frees this buffer.
        /// </summary>
        public void Free()
        {
            Free(this);
        }

        /// <summary>
        /// Frees this buffer.
        /// </summary>
        public void Dispose()
        {
            Free(this);
        }

        private static int NextPowerOfTwo(int v)
        {
            // Engage bit hax
            // http://stackoverflow.com/questions/466204/rounding-up-to-nearest-power-of-2
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return v;
        }
    }
}