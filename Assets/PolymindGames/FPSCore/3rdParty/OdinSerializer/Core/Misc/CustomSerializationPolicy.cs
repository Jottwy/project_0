//-----------------------------------------------------------------------
// <copyright file="CustomSerializationPolicy.cs" company="Sirenix IVS">
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
    using System.Reflection;

    /// <summary>
    /// Helper class for quickly and easily implementing the <see cref="ISerializationPolicy"/> interface.
    /// </summary>
    public sealed class CustomSerializationPolicy : ISerializationPolicy
    {
        private readonly string m_ID;
        private readonly bool m_AllowNonSerializableTypes;
        private readonly Func<MemberInfo, bool> m_ShouldSerializeFunc;

        /// <summary>
        /// Initializes a new instance of the <see cref="CustomSerializationPolicy"/> class.
        /// </summary>
        /// <param name="id">The policy ID.</param>
        /// <param name="allowNonSerializableTypes">if set to <c>true</c> non serializable types will be allowed.</param>
        /// <param name="shouldSerializeFunc">The delegate to use for determining whether members should be serialized.</param>
        /// <exception cref="System.ArgumentNullException">
        /// The id argument or the shouldSerializeFunc argument was null.
        /// </exception>
        public CustomSerializationPolicy(string id, bool allowNonSerializableTypes, Func<MemberInfo, bool> shouldSerializeFunc)
        {
            m_ID = id ?? throw new ArgumentNullException(nameof(id));
            m_AllowNonSerializableTypes = allowNonSerializableTypes;
            m_ShouldSerializeFunc = shouldSerializeFunc ?? throw new ArgumentNullException(nameof(shouldSerializeFunc));
        }

        /// <summary>
        /// Gets the identifier of the policy. This can be stored in the serialization metadata, so the policy used to serialize it can be recovered without knowing the policy at runtime. This ID should preferably be unique.
        /// </summary>
        /// <value>
        /// The identifier of the policy.
        /// </value>
        public string ID => m_ID;

        /// <summary>
        /// Gets a value indicating whether to allow non serializable types. (Types which are not decorated with <see cref="System.SerializableAttribute" />.)
        /// </summary>
        /// <value>
        /// <c>true</c> if serializable types are allowed; otherwise, <c>false</c>.
        /// </value>
        public bool AllowNonSerializableTypes => m_AllowNonSerializableTypes;

        /// <summary>
        /// Gets a value indicating whether a given <see cref="MemberInfo" /> should be serialized or not.
        /// </summary>
        /// <param name="member">The member to check.</param>
        /// <returns>
        ///   <c>true</c> if the given member should be serialized, otherwise, <c>false</c>.
        /// </returns>
        public bool ShouldSerializeMember(MemberInfo member)
        {
            return m_ShouldSerializeFunc(member);
        }
    }
}