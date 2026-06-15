//-----------------------------------------------------------------------
// <copyright file="SerializationUtility.cs" company="Sirenix IVS">
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
    using System.IO;
    using Utilities;

    /// <summary>
    /// Provides an array of utility wrapper methods for easy serialization and deserialization of objects of any type.
    /// </summary>
    public static class SerializationUtility
    {
        private static IDataWriter GetCachedWriter(out IDisposable cache, DataFormat format, Stream stream, SerializationContext context)
        {
            IDataWriter writer;

            if (format == DataFormat.Binary)
            {
                var binaryCache = Cache<BinaryDataWriter>.Claim();
                var binaryWriter = binaryCache.Value;

                binaryWriter.Stream = stream;
                binaryWriter.Context = context;
                binaryWriter.PrepareNewSerializationSession();

                writer = binaryWriter;
                cache = binaryCache;
            }
            else if (format == DataFormat.Json)
            {
                var jsonCache = Cache<JsonDataWriter>.Claim();
                var jsonWriter = jsonCache.Value;

                jsonWriter.Stream = stream;
                jsonWriter.Context = context;
                jsonWriter.PrepareNewSerializationSession();

                writer = jsonWriter;
                cache = jsonCache;
            }
            else if (format == DataFormat.Nodes)
            {
                throw new InvalidOperationException("Cannot automatically create a writer for the format '" + DataFormat.Nodes + "', because it does not use a stream.");
            }
            else
            {
                throw new NotImplementedException(format.ToString());
            }

            return writer;
        }

        private static IDataReader GetCachedReader(out IDisposable cache, DataFormat format, Stream stream, DeserializationContext context)
        {
            IDataReader reader;

            if (format == DataFormat.Binary)
            {
                var binaryCache = Cache<BinaryDataReader>.Claim();
                var binaryReader = binaryCache.Value;

                binaryReader.Stream = stream;
                binaryReader.Context = context;
                binaryReader.PrepareNewSerializationSession();

                reader = binaryReader;
                cache = binaryCache;
            }
            else if (format == DataFormat.Json)
            {
                var jsonCache = Cache<JsonDataReader>.Claim();
                var jsonReader = jsonCache.Value;

                jsonReader.Stream = stream;
                jsonReader.Context = context;
                jsonReader.PrepareNewSerializationSession();

                reader = jsonReader;
                cache = jsonCache;
            }
            else if (format == DataFormat.Nodes)
            {
                throw new InvalidOperationException("Cannot automatically create a reader for the format '" + DataFormat.Nodes + "', because it does not use a stream.");
            }
            else
            {
                throw new NotImplementedException(format.ToString());
            }

            return reader;
        }

        /// <summary>
        /// Serializes the given value using the given writer.
        /// </summary>
        /// <typeparam name="T">The type of the value to serialize.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="writer">The writer to use.</param>
        public static void SerializeValue<T>(T value, IDataWriter writer)
        {
            Serializer.Get<T>().WriteValue(value, writer);
            writer.FlushToStream();
        }

        /// <summary>
        /// Serializes the given value to a given stream in the specified format.
        /// </summary>
        /// <typeparam name="T">The type of the value to serialize.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="stream">The stream to serialize to.</param>
        /// <param name="format">The format to serialize in.</param>
        /// <param name="context">The context.</param>
        public static void SerializeValue<T>(T value, Stream stream, DataFormat format, SerializationContext context = null)
        {
            var writer = GetCachedWriter(out var cache, format, stream, context);

            try
            {
                if (context != null)
                {
                    SerializeValue(value, writer);
                }
                else
                {
                    using (var con = Cache<SerializationContext>.Claim())
                    {
                        writer.Context = con;
                        SerializeValue(value, writer);
                    }
                }
            }
            finally
            {
                cache.Dispose();
            }
        }

        /// <summary>
        /// Serializes the given value using the specified format, and returns the result as a byte array.
        /// </summary>
        /// <typeparam name="T">The type of the value to serialize.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="format">The format to use.</param>
        /// <param name="context">The context to use.</param>
        /// <returns>A byte array containing the serialized value.</returns>
        public static byte[] SerializeValue<T>(T value, DataFormat format, SerializationContext context = null)
        {
            using (var stream = CachedMemoryStream.Claim())
            {
                SerializeValue(value, stream.Value.MemoryStream, format, context);
                return stream.Value.MemoryStream.ToArray();
            }
        }

        /// <summary>
        /// Deserializes a value from the given reader.
        /// </summary>
        /// <typeparam name="T">The type to deserialize.</typeparam>
        /// <param name="reader">The reader to use.</param>
        /// <returns>The deserialized value.</returns>
        public static T DeserializeValue<T>(IDataReader reader)
        {
            return Serializer.Get<T>().ReadValue(reader);
        }

        /// <summary>
        /// Deserializes a value of a given type from the given stream in the given format.
        /// </summary>
        /// <typeparam name="T">The type to deserialize.</typeparam>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="format">The format to read.</param>
        /// <param name="context">The context.</param>
        /// <returns>
        /// The deserialized value.
        /// </returns>
        public static T DeserializeValue<T>(Stream stream, DataFormat format, DeserializationContext context = null)
        {
            var reader = GetCachedReader(out var cache, format, stream, context);

            try
            {
                if (context != null)
                {
                    return DeserializeValue<T>(reader);
                }
                else
                {
                    using (var con = Cache<DeserializationContext>.Claim())
                    {
                        reader.Context = con;
                        return DeserializeValue<T>(reader);
                    }
                }
            }
            finally
            {
                cache.Dispose();
            }
        }

        /// <summary>
        /// Deserializes a value of a given type from the given byte array in the given format.
        /// </summary>
        /// <typeparam name="T">The type to deserialize.</typeparam>
        /// <param name="bytes">The bytes to deserialize from.</param>
        /// <param name="format">The format to read.</param>
        /// <param name="context">The context to use.</param>
        /// <returns>
        /// The deserialized value.
        /// </returns>
        public static T DeserializeValue<T>(byte[] bytes, DataFormat format, DeserializationContext context = null)
        {
            using (var stream = CachedMemoryStream.Claim(bytes))
            {
                return DeserializeValue<T>(stream.Value.MemoryStream, format, context);
            }
        }

        /// <summary>
        /// Creates a deep copy of an object. Returns null if null. All Unity objects references will remain the same - they will not get copied.
        /// </summary>
        public static object CreateCopy(object obj)
        {
            if (obj == null)
                return null;

            if (obj is string)
                return obj;

            var type = obj.GetType();

            if (type.IsValueType)
                return obj;

            if (type.InheritsFrom(typeof(UnityEngine.Object)))
                return obj;

            using (var stream = CachedMemoryStream.Claim())
            using (var serContext = Cache<SerializationContext>.Claim())
            using (var desContext = Cache<DeserializationContext>.Claim())
            {
                serContext.Value.Config.SerializationPolicy = SerializationPolicies.Everything;
                desContext.Value.Config.SerializationPolicy = SerializationPolicies.Everything;

                SerializeValue(obj, stream.Value.MemoryStream, DataFormat.Binary, serContext);
                stream.Value.MemoryStream.Position = 0;
                return DeserializeValue<object>(stream.Value.MemoryStream, DataFormat.Binary, desContext);
            }
        }
    }
}