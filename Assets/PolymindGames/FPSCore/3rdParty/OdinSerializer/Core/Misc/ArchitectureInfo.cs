//-----------------------------------------------------------------------
// <copyright file="ArchitectureInfo.cs" company="Sirenix IVS">
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
    using UnityEngine;

    /// <summary>
    /// This class gathers info about the current architecture for the purpose of determining
    /// the unaligned read/write capabilities that we have to work with.
    /// </summary>
    public static unsafe class ArchitectureInfo
    {
        public static bool ArchitectureSupportsUnalignedFloat32Reads;

        /// <summary>
        /// This will be false on some ARM architectures, such as ARMv7.
        /// In these cases, we will have to perform slower but safer int-by-int read/writes of data.
        /// <para />
        /// Since this value will never change at runtime, performance hits from checking this 
        /// everywhere should hopefully be negligible, since branch prediction from speculative
        /// execution will always predict it correctly.
        /// </summary>
        public static bool ArchitectureSupportsAllUnalignedReadWrites;

        static ArchitectureInfo()
        {
#if UNITY_EDITOR
            ArchitectureSupportsUnalignedFloat32Reads = true;
            ArchitectureSupportsAllUnalignedReadWrites = true;
#else
            // At runtime, we are going to be very pessimistic and assume the
            // worst until we get more info about the platform we are on.
            ArchitectureSupportsUnalignedFloat32Reads = false;
            ArchitectureSupportsAllUnalignedReadWrites = false;

            Debug.Log("Odin Serializer ArchitectureInfo initialization with defaults (all unaligned read/writes disabled).");
#endif
        }

        internal static void SetRuntimePlatform(RuntimePlatform platform)
        {
            // Experience indicates that unaligned read/write support is pretty spotty and sometimes causes subtle bugs even when it appears to work,
            // so to be safe, we only enable it for platforms where we are certain that it will work.

            switch (platform)
            {
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.PS4:
                case RuntimePlatform.XboxOne:
                case RuntimePlatform.WebGLPlayer:
                case RuntimePlatform.WSAPlayerX64:
                case RuntimePlatform.WSAPlayerX86:
                    
                    try
                    {
                        // Try to perform some unaligned float reads.
                        // If this throws an exception, the current
                        // architecture does not support doing this.

                        // Note that there are cases where this is supported
                        // but other unaligned read/writes are not, usually 
                        // 64-bit read/writes. However, testing indicates 
                        // that these read/writes cause hard crashes and not
                        // NullReferenceExceptions, and so we cannot test for
                        // them but must instead look at the architecture.

                        byte[] testArray = new byte[8];

                        fixed (byte* test = testArray)
                        {
                            // Even if test is weirdly aligned in the stack, trying four differently aligned 
                            // reads will definitely have an unaligned read or two in there.

                            // If all of these reads work, we are safe. We do it this way instead of just having one read,
                            // because as far as I have been able to determine, there are no guarantees about the alignment 
                            // of local stack memory.

                            for (int i = 0; i < 4; i++)
                            {
                                float value = *(float*)(test + i);
                            }

                            ArchitectureSupportsUnalignedFloat32Reads = true;
                        }
                    }
                    catch (NullReferenceException)
                    {
                        ArchitectureSupportsUnalignedFloat32Reads = false;
                    }

                    if (ArchitectureSupportsUnalignedFloat32Reads)
                    {
                        Debug.Log("Odin Serializer detected whitelisted runtime platform " + platform + " and memory read test succeeded; enabling all unaligned memory read/writes.");
                        ArchitectureSupportsAllUnalignedReadWrites = true;
                    }
                    else
                    {
                        Debug.Log("Odin Serializer detected whitelisted runtime platform " + platform + " and memory read test failed; disabling all unaligned memory read/writes.");
                    }
                    break;
                default:
                    ArchitectureSupportsUnalignedFloat32Reads = false;
                    ArchitectureSupportsAllUnalignedReadWrites = false;
                    Debug.Log("Odin Serializer detected non-white-listed runtime platform " + platform + "; disabling all unaligned memory read/writes.");
                    break;
            }
        }
    }
}