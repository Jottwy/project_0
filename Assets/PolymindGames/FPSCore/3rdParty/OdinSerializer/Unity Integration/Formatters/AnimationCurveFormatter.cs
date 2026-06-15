//-----------------------------------------------------------------------
// <copyright file="AnimationCurveFormatter.cs" company="Sirenix IVS">
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

[assembly: RegisterFormatter(typeof(AnimationCurveFormatter))]

namespace PolymindGames.OdinSerializer
{
    using UnityEngine;

    /// <summary>
    /// Custom formatter for the <see cref="AnimationCurve"/> type.
    /// </summary>
    /// <seealso cref="MinimalBaseFormatter{UnityEngine.AnimationCurve}" />
    public sealed class AnimationCurveFormatter : MinimalBaseFormatter<AnimationCurve>
    {
        private static readonly Serializer<Keyframe[]> s_KeyframeSerializer = Serializer.Get<Keyframe[]>();
        private static readonly Serializer<WrapMode> s_WrapModeSerializer = Serializer.Get<WrapMode>();

        /// <summary>
        /// Returns null.
        /// </summary>
        /// <returns>
        /// A null value.
        /// </returns>
        protected override AnimationCurve GetUninitializedObject()
        {
            return null;
        }

        /// <summary>
        /// Reads into the specified value using the specified reader.
        /// </summary>
        /// <param name="value">The value to read into.</param>
        /// <param name="reader">The reader to use.</param>
        protected override void Read(ref AnimationCurve value, IDataReader reader)
        {
            var keys = s_KeyframeSerializer.ReadValue(reader);

            value = new AnimationCurve(keys);
            value.preWrapMode = s_WrapModeSerializer.ReadValue(reader);
            value.postWrapMode = s_WrapModeSerializer.ReadValue(reader);
        }

        /// <summary>
        /// Writes from the specified value using the specified writer.
        /// </summary>
        /// <param name="value">The value to write from.</param>
        /// <param name="writer">The writer to use.</param>
        protected override void Write(ref AnimationCurve value, IDataWriter writer)
        {
            s_KeyframeSerializer.WriteValue(value.keys, writer);
            s_WrapModeSerializer.WriteValue(value.preWrapMode, writer);
            s_WrapModeSerializer.WriteValue(value.postWrapMode, writer);
        }
    }
}