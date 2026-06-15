//-----------------------------------------------------------------------
// <copyright file="KeyframeFormatter.cs" company="Sirenix IVS">
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

using PolymindGames.OdinSerializer;

[assembly: RegisterFormatter(typeof(KeyframeFormatter))]

namespace PolymindGames.OdinSerializer
{
    using Utilities;
    using UnityEngine;

    /// <summary>
    /// Custom formatter for the <see cref="Keyframe"/> type.
    /// </summary>
    /// <seealso cref="MinimalBaseFormatter{UnityEngine.Keyframe}" />
    public sealed class KeyframeFormatter : MinimalBaseFormatter<Keyframe>
    {
        private static readonly Serializer<float> s_FloatSerializer = Serializer.Get<float>();
        private static readonly Serializer<int> s_IntSerializer = Serializer.Get<int>();

        private static readonly bool s_IsIn20181OrAbove;
        private static IFormatter<Keyframe> s_Formatter;

        static KeyframeFormatter()
        {
            s_IsIn20181OrAbove = typeof(Keyframe).GetProperty("weightedMode") != null;

            if (s_IsIn20181OrAbove)
            {
                if (EmitUtilities.CanEmit)
                {
                    s_Formatter = (IFormatter<Keyframe>)FormatterEmitter.GetEmittedFormatter(typeof(Keyframe), SerializationPolicies.Everything);
                }
                else
                {
                    s_Formatter = new ReflectionFormatter<Keyframe>(SerializationPolicies.Everything);
                }
            }
        }

        /// <summary>
        /// Reads into the specified value using the specified reader.
        /// </summary>
        /// <param name="value">The value to read into.</param>
        /// <param name="reader">The reader to use.</param>
        protected override void Read(ref Keyframe value, IDataReader reader)
        {
            EntryType first = reader.PeekEntry(out string name);

            if (first == EntryType.Integer && name == "ver")
            {
                if (s_Formatter == null)
                {
                    // We're deserializing 2018.1+ data in a lower version of Unity - so just give it a go
                    s_Formatter = new ReflectionFormatter<Keyframe>(SerializationPolicies.Everything);
                }

                int version;
                reader.ReadInt32(out version);

                // Only one version so far, so ignore it for now
                value = s_Formatter.Deserialize(reader);
            }
            else
            {
                // Legacy Keyframe deserialization code
                value.inTangent = s_FloatSerializer.ReadValue(reader);
                value.outTangent = s_FloatSerializer.ReadValue(reader);
                value.time = s_FloatSerializer.ReadValue(reader);
                value.value = s_FloatSerializer.ReadValue(reader);

#pragma warning disable 0618
                value.tangentMode = s_IntSerializer.ReadValue(reader);
#pragma warning restore 0618
            }
        }

        /// <summary>
        /// Writes from the specified value using the specified writer.
        /// </summary>
        /// <param name="value">The value to write from.</param>
        /// <param name="writer">The writer to use.</param>
        protected override void Write(ref Keyframe value, IDataWriter writer)
        {
            if (s_IsIn20181OrAbove)
            {
                writer.WriteInt32("ver", 1);
                s_Formatter.Serialize(value, writer);
            }
            else
            {
                // Legacy Keyframe serialization code
                s_FloatSerializer.WriteValue(value.inTangent, writer);
                s_FloatSerializer.WriteValue(value.outTangent, writer);
                s_FloatSerializer.WriteValue(value.time, writer);
                s_FloatSerializer.WriteValue(value.value, writer);

#pragma warning disable 0618
                s_IntSerializer.WriteValue(value.tangentMode, writer);
#pragma warning restore 0618
            }
        }
    }
}