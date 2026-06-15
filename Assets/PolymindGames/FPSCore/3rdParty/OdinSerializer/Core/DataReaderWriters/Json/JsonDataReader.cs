//-----------------------------------------------------------------------
// <copyright file="JsonDataReader.cs" company="Sirenix IVS">
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
    using System.Globalization;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Reads json data from a stream that has been written by a <see cref="JsonDataWriter"/>.
    /// </summary>
    /// <seealso cref="BaseDataReader" />
    public sealed class JsonDataReader : BaseDataReader
    {
        private JsonTextReader m_Reader;
        private EntryType? m_PeekedEntryType;
        private string m_PeekedEntryName;
        private string m_PeekedEntryContent;
        private readonly Dictionary<int, Type> m_SeenTypes = new(16);

        private readonly Dictionary<Type, Delegate> m_PrimitiveArrayReaders;

        public JsonDataReader() : this(null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonDataReader" /> class.
        /// </summary>
        /// <param name="stream">The base stream of the reader.</param>
        /// <param name="context">The deserialization context to use.</param>
        public JsonDataReader(Stream stream, DeserializationContext context) : base(stream, context)
        {
            m_PrimitiveArrayReaders = new Dictionary<Type, Delegate>()
            {
                { typeof(char), (Func<char>)(() => {
                    ReadChar(out char v); return v; }) },
                { typeof(sbyte), (Func<sbyte>)(() => {
                    ReadSByte(out sbyte v); return v; }) },
                { typeof(short), (Func<short>)(() => {
                    ReadInt16(out short v); return v; }) },
                { typeof(int), (Func<int>)(() => {
                    ReadInt32(out int v); return v; })  },
                { typeof(long), (Func<long>)(() => {
                    ReadInt64(out long v); return v; })  },
                { typeof(byte), (Func<byte>)(() => {
                    ReadByte(out byte v); return v; })  },
                { typeof(ushort), (Func<ushort>)(() => {
                    ReadUInt16(out ushort v); return v; })  },
                { typeof(uint),   (Func<uint>)(() => {
                    ReadUInt32(out uint v); return v; })  },
                { typeof(ulong),  (Func<ulong>)(() => {
                    ReadUInt64(out ulong v); return v; })  },
                { typeof(decimal),   (Func<decimal>)(() => {
                    ReadDecimal(out decimal v); return v; })  },
                { typeof(bool),  (Func<bool>)(() => {
                    ReadBoolean(out bool v); return v; })  },
                { typeof(float),  (Func<float>)(() => {
                    ReadSingle(out float v); return v; })  },
                { typeof(double),  (Func<double>)(() => {
                    ReadDouble(out double v); return v; })  },
                { typeof(Guid),  (Func<Guid>)(() => {
                    ReadGuid(out var v); return v; })  }
            };
        }

#if !UNITY_EDITOR // This causes a warning when using source in Unity
#pragma warning disable IDE0009 // Member access should be qualified.
#endif

        /// <summary>
        /// Gets or sets the base stream of the reader.
        /// </summary>
        /// <value>
        /// The base stream of the reader.
        /// </value>
        public override Stream Stream
        {
            get => base.Stream;

            set
            {
                base.Stream = value;
                m_Reader = new JsonTextReader(base.Stream, Context);
            }
        }

#if !UNITY_EDITOR
#pragma warning restore IDE0009 // Member access should be qualified.
#endif

        /// <summary>
        /// Disposes all resources kept by the data reader, except the stream, which can be reused later.
        /// </summary>
        public override void Dispose()
        {
            m_Reader.Dispose();
        }

        /// <summary>
        /// Peeks ahead and returns the type of the next entry in the stream.
        /// </summary>
        /// <param name="name">The name of the next entry, if it has one.</param>
        /// <returns>
        /// The type of the next entry.
        /// </returns>
        public override EntryType PeekEntry(out string name)
        {
            if (m_PeekedEntryType != null)
            {
                name = m_PeekedEntryName;
                return m_PeekedEntryType.Value;
            }

            m_Reader.ReadToNextEntry(out name, out m_PeekedEntryContent, out var entry);
            m_PeekedEntryName = name;
            m_PeekedEntryType = entry;

            return entry;
        }

        /// <summary>
        /// Tries to enter a node. This will succeed if the next entry is an <see cref="EntryType.StartOfNode" />.
        /// <para />
        /// This call MUST (eventually) be followed by a corresponding call to <see cref="IDataReader.ExitNode(DeserializationContext)" /><para />
        /// This call will change the values of the <see cref="IDataReader.IsInArrayNode" />, <see cref="IDataReader.CurrentNodeName" />, <see cref="IDataReader.CurrentNodeId" /> and <see cref="IDataReader.CurrentNodeDepth" /> properties to the correct values for the current node.
        /// </summary>
        /// <param name="type">The type of the node. This value will be null if there was no metadata, or if the reader's serialization binder failed to resolve the type name.</param>
        /// <returns>
        ///   <c>true</c> if entering a node succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool EnterNode(out Type type)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.StartOfNode)
            {
                string nodeName = m_PeekedEntryName;
                int id = -1;

                ReadToNextEntry();

                if (m_PeekedEntryName == JsonConfig.ID_SIG)
                {
                    if (int.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out id) == false)
                    {
                        Context.Config.DebugContext.LogError("Failed to parse id: " + m_PeekedEntryContent);
                        id = -1;
                    }

                    ReadToNextEntry();
                }

                if (m_PeekedEntryName == JsonConfig.TYPE_SIG && m_PeekedEntryContent != null && m_PeekedEntryContent.Length > 0)
                {
                    if (m_PeekedEntryType == EntryType.Integer)
                    {

                        if (ReadInt32(out int typeID))
                        {
                            if (m_SeenTypes.TryGetValue(typeID, out type) == false)
                            {
                                Context.Config.DebugContext.LogError("Missing type id for node with reference id " + id + ": " + typeID);
                            }
                        }
                        else
                        {
                            Context.Config.DebugContext.LogError("Failed to read type id for node with reference id " + id);
                            type = null;
                        }
                    }
                    else
                    {
                        int typeNameStartIndex = 1;
                        int typeID = -1;
                        int idSplitIndex = m_PeekedEntryContent.IndexOf('|');

                        if (idSplitIndex >= 0)
                        {
                            typeNameStartIndex = idSplitIndex + 1;
                            string idStr = m_PeekedEntryContent.Substring(1, idSplitIndex - 1);

                            if (int.TryParse(idStr, NumberStyles.Any, CultureInfo.InvariantCulture, out typeID) == false)
                            {
                                typeID = -1;
                            }
                        }

                        type = Context.Binder.BindToType(m_PeekedEntryContent.Substring(typeNameStartIndex, m_PeekedEntryContent.Length - (1 + typeNameStartIndex)), Context.Config.DebugContext);

                        if (typeID >= 0)
                        {
                            m_SeenTypes[typeID] = type;
                        }

                        m_PeekedEntryType = null;
                    }
                }
                else
                {
                    type = null;
                }

                PushNode(nodeName, id, type);
                return true;
            }
            else
            {
                SkipEntry();
                type = null;
                return false;
            }
        }

        /// <summary>
        /// Exits the current node. This method will keep skipping entries using <see cref="IDataReader.SkipEntry(DeserializationContext)" /> until an <see cref="EntryType.EndOfNode" /> is reached, or the end of the stream is reached.
        /// <para />
        /// This call MUST have been preceded by a corresponding call to <see cref="IDataReader.EnterNode(out Type)" />.
        /// <para />
        /// This call will change the values of the <see cref="IDataReader.IsInArrayNode" />, <see cref="IDataReader.CurrentNodeName" />, <see cref="IDataReader.CurrentNodeId" /> and <see cref="IDataReader.CurrentNodeDepth" /> to the correct values for the node that was prior to the current node.
        /// </summary>
        /// <returns>
        ///   <c>true</c> if the method exited a node, <c>false</c> if it reached the end of the stream.
        /// </returns>
        public override bool ExitNode()
        {
            PeekEntry();

            // Read to next end of node
            while (m_PeekedEntryType != EntryType.EndOfNode && m_PeekedEntryType != EntryType.EndOfStream)
            {
                if (m_PeekedEntryType == EntryType.EndOfArray)
                {
                    Context.Config.DebugContext.LogError("Data layout mismatch; skipping past array boundary when exiting node.");
                    m_PeekedEntryType = null;
                    //this.MarkEntryConsumed();
                }

                SkipEntry();
            }

            if (m_PeekedEntryType == EntryType.EndOfNode)
            {
                m_PeekedEntryType = null;
                PopNode(CurrentNodeName);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Tries to enters an array node. This will succeed if the next entry is an <see cref="EntryType.StartOfArray" />.
        /// <para />
        /// This call MUST (eventually) be followed by a corresponding call to <see cref="IDataReader.ExitArray(DeserializationContext)" /><para />
        /// This call will change the values of the <see cref="IDataReader.IsInArrayNode" />, <see cref="IDataReader.CurrentNodeName" />, <see cref="IDataReader.CurrentNodeId" /> and <see cref="IDataReader.CurrentNodeDepth" /> properties to the correct values for the current array node.
        /// </summary>
        /// <param name="length">The length of the array that was entered.</param>
        /// <returns>
        ///   <c>true</c> if an array was entered, otherwise <c>false</c>
        /// </returns>
        public override bool EnterArray(out long length)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.StartOfArray)
            {
                PushArray();

                if (m_PeekedEntryName != JsonConfig.REGULAR_ARRAY_LENGTH_SIG)
                {
                    Context.Config.DebugContext.LogError("Array entry wasn't preceded by an array length entry!");
                    length = 0; // No array content for you!
                    return true;
                }
                else
                {

                    if (int.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out int intLength) == false)
                    {
                        Context.Config.DebugContext.LogError("Failed to parse array length: " + m_PeekedEntryContent);
                        length = 0; // No array content for you!
                        return true;
                    }

                    length = intLength;

                    ReadToNextEntry();

                    if (m_PeekedEntryName != JsonConfig.REGULAR_ARRAY_CONTENT_SIG)
                    {
                        Context.Config.DebugContext.LogError("Failed to find regular array content entry after array length entry!");
                        length = 0; // No array content for you!
                        return true;
                    }

                    m_PeekedEntryType = null;
                    return true;
                }
            }
            else
            {
                SkipEntry();
                length = 0;
                return false;
            }
        }

        /// <summary>
        /// Exits the closest array. This method will keep skipping entries using <see cref="IDataReader.SkipEntry(DeserializationContext)" /> until an <see cref="EntryType.EndOfArray" /> is reached, or the end of the stream is reached.
        /// <para />
        /// This call MUST have been preceded by a corresponding call to <see cref="IDataReader.EnterArray(out long)" />.
        /// <para />
        /// This call will change the values of the <see cref="IDataReader.IsInArrayNode" />, <see cref="IDataReader.CurrentNodeName" />, <see cref="IDataReader.CurrentNodeId" /> and <see cref="IDataReader.CurrentNodeDepth" /> to the correct values for the node that was prior to the exited array node.
        /// </summary>
        /// <returns>
        ///   <c>true</c> if the method exited an array, <c>false</c> if it reached the end of the stream.
        /// </returns>
        public override bool ExitArray()
        {
            PeekEntry();

            // Read to next end of array
            while (m_PeekedEntryType != EntryType.EndOfArray && m_PeekedEntryType != EntryType.EndOfStream)
            {
                if (m_PeekedEntryType == EntryType.EndOfNode)
                {
                    Context.Config.DebugContext.LogError("Data layout mismatch; skipping past node boundary when exiting array.");
                    m_PeekedEntryType = null;
                    //this.MarkEntryConsumed();
                }

                SkipEntry();
            }

            if (m_PeekedEntryType == EntryType.EndOfArray)
            {
                m_PeekedEntryType = null;
                PopArray();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads a primitive array value. This call will succeed if the next entry is an <see cref="EntryType.PrimitiveArray" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <typeparam name="T">The element type of the primitive array. Valid element types can be determined using <see cref="FormatterUtilities.IsPrimitiveArrayType(Type)" />.</typeparam>
        /// <param name="array">The resulting primitive array.</param>
        /// <returns>
        ///   <c>true</c> if reading a primitive array succeeded, otherwise <c>false</c>
        /// </returns>
        /// <exception cref="System.ArgumentException">Type  + typeof(T).Name +  is not a valid primitive array type.</exception>
        public override bool ReadPrimitiveArray<T>(out T[] array)
        {
            if (FormatterUtilities.IsPrimitiveArrayType(typeof(T)) == false)
            {
                throw new ArgumentException("Type " + typeof(T).Name + " is not a valid primitive array type.");
            }

            PeekEntry();

            if (m_PeekedEntryType == EntryType.PrimitiveArray)
            {
                PushArray();

                if (m_PeekedEntryName != JsonConfig.PRIMITIVE_ARRAY_LENGTH_SIG)
                {
                    Context.Config.DebugContext.LogError("Array entry wasn't preceded by an array length entry!");
                    array = null; // No array content for you!
                    return false;
                }
                else
                {

                    if (int.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out int intLength) == false)
                    {
                        Context.Config.DebugContext.LogError("Failed to parse array length: " + m_PeekedEntryContent);
                        array = null; // No array content for you!
                        return false;
                    }

                    ReadToNextEntry();

                    if (m_PeekedEntryName != JsonConfig.PRIMITIVE_ARRAY_CONTENT_SIG)
                    {
                        Context.Config.DebugContext.LogError("Failed to find primitive array content entry after array length entry!");
                        array = null; // No array content for you!
                        return false;
                    }

                    m_PeekedEntryType = null;

                    Func<T> reader = (Func<T>)m_PrimitiveArrayReaders[typeof(T)];
                    array = new T[intLength];

                    for (int i = 0; i < intLength; i++)
                    {
                        array[i] = reader();
                    }

                    ExitArray();
                    return true;
                }
            }
            else
            {
                SkipEntry();
                array = null;
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="bool" /> value. This call will succeed if the next entry is an <see cref="EntryType.Boolean" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadBoolean(out bool value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.Boolean)
            {
                try
                {
                    value = m_PeekedEntryContent == "true";
                    return true;
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = default(bool);
                return false;
            }
        }

        /// <summary>
        /// Reads an internal reference id. This call will succeed if the next entry is an <see cref="EntryType.InternalReference" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="id">The internal reference id.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadInternalReference(out int id)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.InternalReference)
            {
                try
                {
                    return ReadAnyIntReference(out id);
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                id = -1;
                return false;
            }
        }

        /// <summary>
        /// Reads an external reference index. This call will succeed if the next entry is an <see cref="EntryType.ExternalReferenceByIndex" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="index">The external reference index.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadExternalReference(out int index)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.ExternalReferenceByIndex)
            {
                try
                {
                    return ReadAnyIntReference(out index);
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                index = -1;
                return false;
            }
        }

        /// <summary>
        /// Reads an external reference guid. This call will succeed if the next entry is an <see cref="EntryType.ExternalReferenceByGuid" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="guid">The external reference guid.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadExternalReference(out Guid guid)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.ExternalReferenceByGuid)
            {
                var guidStr = m_PeekedEntryContent;

                if (guidStr.StartsWith(JsonConfig.EXTERNAL_GUID_REF_SIG))
                {
                    guidStr = guidStr.Substring(JsonConfig.EXTERNAL_GUID_REF_SIG.Length + 1);
                }

                try
                {
                    guid = new Guid(guidStr);
                    return true;
                }
                catch (FormatException)
                {
                    guid = Guid.Empty;
                    return false;
                }
                catch (OverflowException)
                {
                    guid = Guid.Empty;
                    return false;
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                guid = Guid.Empty;
                return false;
            }
        }

        /// <summary>
        /// Reads an external reference string. This call will succeed if the next entry is an <see cref="EntryType.ExternalReferenceByString" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="id">The external reference string.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadExternalReference(out string id)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.ExternalReferenceByString)
            {
                id = m_PeekedEntryContent;

                if (id.StartsWith(JsonConfig.EXTERNAL_STRING_REF_SIG_OLD))
                {
                    id = id.Substring(JsonConfig.EXTERNAL_STRING_REF_SIG_OLD.Length + 1);
                }
                else if (id.StartsWith(JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED))
                {
                    id = id.Substring(JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED.Length + 2, id.Length - (JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED.Length + 3));
                }

                MarkEntryConsumed();
                return true;
            }
            else
            {
                SkipEntry();
                id = null;
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="char" /> value. This call will succeed if the next entry is an <see cref="EntryType.String" />.
        /// <para />
        /// If the string of the entry is longer than 1 character, the first character of the string will be taken as the result.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadChar(out char value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.String)
            {
                try
                {
                    value = m_PeekedEntryContent[1];
                    return true;
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = default(char);
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="string" /> value. This call will succeed if the next entry is an <see cref="EntryType.String" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadString(out string value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.String)
            {
                try
                {
                    value = m_PeekedEntryContent.Substring(1, m_PeekedEntryContent.Length - 2);
                    return true;
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = null;
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="Guid" /> value. This call will succeed if the next entry is an <see cref="EntryType.Guid" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadGuid(out Guid value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.Guid)
            {
                try
                {
                    try
                    {
                        value = new Guid(m_PeekedEntryContent);
                        return true;
                    }
                    //// These exceptions can safely be swallowed - it just means the parse failed
                    catch (FormatException)
                    {
                        value = Guid.Empty;
                        return false;
                    }
                    catch (OverflowException)
                    {
                        value = Guid.Empty;
                        return false;
                    }
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = Guid.Empty;
                return false;
            }
        }

        /// <summary>
        /// Reads an <see cref="sbyte" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="sbyte.MinValue" /> or larger than <see cref="sbyte.MaxValue" />, the result will be default(<see cref="sbyte" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadSByte(out sbyte value)
        {

            if (ReadInt64(out long longValue))
            {
                checked
                {
                    try
                    {
                        value = (sbyte)longValue;
                    }
                    catch (OverflowException)
                    {
                        value = default(sbyte);
                    }
                }

                return true;
            }

            value = default(sbyte);
            return false;
        }

        /// <summary>
        /// Reads a <see cref="short" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="short.MinValue" /> or larger than <see cref="short.MaxValue" />, the result will be default(<see cref="short" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadInt16(out short value)
        {

            if (ReadInt64(out long longValue))
            {
                checked
                {
                    try
                    {
                        value = (short)longValue;
                    }
                    catch (OverflowException)
                    {
                        value = default(short);
                    }
                }

                return true;
            }

            value = default(short);
            return false;
        }

        /// <summary>
        /// Reads an <see cref="int" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="int.MinValue" /> or larger than <see cref="int.MaxValue" />, the result will be default(<see cref="int" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadInt32(out int value)
        {

            if (ReadInt64(out long longValue))
            {
                checked
                {
                    try
                    {
                        value = (int)longValue;
                    }
                    catch (OverflowException)
                    {
                        value = default(int);
                    }
                }

                return true;
            }

            value = default(int);
            return false;
        }

        /// <summary>
        /// Reads a <see cref="long" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="long.MinValue" /> or larger than <see cref="long.MaxValue" />, the result will be default(<see cref="long" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadInt64(out long value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.Integer)
            {
                try
                {
                    if (long.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }
                    else
                    {
                        Context.Config.DebugContext.LogError("Failed to parse long from: " + m_PeekedEntryContent);
                        return false;
                    }
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = default(long);
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="byte" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="byte.MinValue" /> or larger than <see cref="byte.MaxValue" />, the result will be default(<see cref="byte" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadByte(out byte value)
        {

            if (ReadUInt64(out ulong ulongValue))
            {
                checked
                {
                    try
                    {
                        value = (byte)ulongValue;
                    }
                    catch (OverflowException)
                    {
                        value = default(byte);
                    }
                }

                return true;
            }

            value = default(byte);
            return false;
        }

        /// <summary>
        /// Reads an <see cref="ushort" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="ushort.MinValue" /> or larger than <see cref="ushort.MaxValue" />, the result will be default(<see cref="ushort" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadUInt16(out ushort value)
        {

            if (ReadUInt64(out ulong ulongValue))
            {
                checked
                {
                    try
                    {
                        value = (ushort)ulongValue;
                    }
                    catch (OverflowException)
                    {
                        value = default(ushort);
                    }
                }

                return true;
            }

            value = default(ushort);
            return false;
        }

        /// <summary>
        /// Reads an <see cref="uint" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="uint.MinValue" /> or larger than <see cref="uint.MaxValue" />, the result will be default(<see cref="uint" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadUInt32(out uint value)
        {

            if (ReadUInt64(out ulong ulongValue))
            {
                checked
                {
                    try
                    {
                        value = (uint)ulongValue;
                    }
                    catch (OverflowException)
                    {
                        value = default(uint);
                    }
                }

                return true;
            }

            value = default(uint);
            return false;
        }

        /// <summary>
        /// Reads an <see cref="ulong" /> value. This call will succeed if the next entry is an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the value of the stored integer is smaller than <see cref="ulong.MinValue" /> or larger than <see cref="ulong.MaxValue" />, the result will be default(<see cref="ulong" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadUInt64(out ulong value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.Integer)
            {
                try
                {
                    if (ulong.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }
                    else
                    {
                        Context.Config.DebugContext.LogError("Failed to parse ulong from: " + m_PeekedEntryContent);
                        return false;
                    }
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = default(ulong);
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="decimal" /> value. This call will succeed if the next entry is an <see cref="EntryType.FloatingPoint" /> or an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the stored integer or floating point value is smaller than <see cref="decimal.MinValue" /> or larger than <see cref="decimal.MaxValue" />, the result will be default(<see cref="decimal" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadDecimal(out decimal value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.FloatingPoint || m_PeekedEntryType == EntryType.Integer)
            {
                try
                {
                    if (decimal.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }
                    else
                    {
                        Context.Config.DebugContext.LogError("Failed to parse decimal from: " + m_PeekedEntryContent);
                        return false;
                    }
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = default(decimal);
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="float" /> value. This call will succeed if the next entry is an <see cref="EntryType.FloatingPoint" /> or an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the stored integer or floating point value is smaller than <see cref="float.MinValue" /> or larger than <see cref="float.MaxValue" />, the result will be default(<see cref="float" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadSingle(out float value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.FloatingPoint || m_PeekedEntryType == EntryType.Integer)
            {
                try
                {
                    if (float.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }
                    else
                    {
                        Context.Config.DebugContext.LogError("Failed to parse float from: " + m_PeekedEntryContent);
                        return false;
                    }
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = default(float);
                return false;
            }
        }

        /// <summary>
        /// Reads a <see cref="double" /> value. This call will succeed if the next entry is an <see cref="EntryType.FloatingPoint" /> or an <see cref="EntryType.Integer" />.
        /// <para />
        /// If the stored integer or floating point value is smaller than <see cref="double.MinValue" /> or larger than <see cref="double.MaxValue" />, the result will be default(<see cref="double" />).
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <param name="value">The value that has been read.</param>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadDouble(out double value)
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.FloatingPoint || m_PeekedEntryType == EntryType.Integer)
            {
                try
                {
                    if (double.TryParse(m_PeekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }
                    else
                    {
                        Context.Config.DebugContext.LogError("Failed to parse double from: " + m_PeekedEntryContent);
                        return false;
                    }
                }
                finally
                {
                    MarkEntryConsumed();
                }
            }
            else
            {
                SkipEntry();
                value = default(double);
                return false;
            }
        }

        /// <summary>
        /// Reads a <c>null</c> value. This call will succeed if the next entry is an <see cref="EntryType.Null" />.
        /// <para />
        /// If the call fails (and returns <c>false</c>), it will skip the current entry value, unless that entry is an <see cref="EntryType.EndOfNode" /> or an <see cref="EntryType.EndOfArray" />.
        /// </summary>
        /// <returns>
        ///   <c>true</c> if reading the value succeeded, otherwise <c>false</c>
        /// </returns>
        public override bool ReadNull()
        {
            PeekEntry();

            if (m_PeekedEntryType == EntryType.Null)
            {
                MarkEntryConsumed();
                return true;
            }
            else
            {
                SkipEntry();
                return false;
            }
        }

        /// <summary>
        /// Tells the reader that a new serialization session is about to begin, and that it should clear all cached values left over from any prior serialization sessions.
        /// This method is only relevant when the same reader is used to deserialize several different, unrelated values.
        /// </summary>
        public override void PrepareNewSerializationSession()
        {
            base.PrepareNewSerializationSession();
            m_PeekedEntryType = null;
            m_PeekedEntryContent = null;
            m_PeekedEntryName = null;
            m_SeenTypes.Clear();
            m_Reader.Reset();
        }

        public override string GetDataDump()
        {
            if (!Stream.CanSeek)
            {
                return "Json data stream cannot seek; cannot dump data.";
            }

            var oldPosition = Stream.Position;

            var bytes = new byte[Stream.Length];

            Stream.Position = 0;
            Stream.Read(bytes, 0, bytes.Length);

            Stream.Position = oldPosition;

            return "Json: " + Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Peeks the current entry.
        /// </summary>
        /// <returns>The peeked entry.</returns>
        protected override EntryType PeekEntry()
        {
            string name;
            return PeekEntry(out name);
        }

        /// <summary>
        /// Consumes the current entry, and reads to the next one.
        /// </summary>
        /// <returns>The next entry.</returns>
        protected override EntryType ReadToNextEntry()
        {
            m_PeekedEntryType = null;
            string name;
            return PeekEntry(out name);
        }

        private void MarkEntryConsumed()
        {
            // After a common read, we cannot skip EndOfArray and EndOfNode entries (meaning the read has failed),
            //   as only the ExitArray and ExitNode methods are allowed to exit nodes and arrays
            if (m_PeekedEntryType != EntryType.EndOfArray && m_PeekedEntryType != EntryType.EndOfNode)
            {
                m_PeekedEntryType = null;
            }
        }

        private bool ReadAnyIntReference(out int value)
        {
            int separatorIndex = -1;

            for (int i = 0; i < m_PeekedEntryContent.Length; i++)
            {
                if (m_PeekedEntryContent[i] == ':')
                {
                    separatorIndex = i;
                    break;
                }
            }

            if (separatorIndex == -1 || separatorIndex == m_PeekedEntryContent.Length - 1)
            {
                Context.Config.DebugContext.LogError("Failed to parse id from: " + m_PeekedEntryContent);
            }

            string idStr = m_PeekedEntryContent.Substring(separatorIndex + 1);

            if (int.TryParse(idStr, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
            else
            {
                Context.Config.DebugContext.LogError("Failed to parse id: " + idStr);
            }

            value = -1;
            return false;
        }
    }
}