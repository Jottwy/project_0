//-----------------------------------------------------------------------
// <copyright file="ColorBlockFormatter.cs" company="Sirenix IVS">
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

[assembly: RegisterFormatterLocator(typeof(ColorBlockFormatterLocator))]

namespace PolymindGames.OdinSerializer
{
    using System;
    using System.Reflection;
    using UnityEngine;

    public sealed class ColorBlockFormatterLocator : IFormatterLocator
    {
        public bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, out IFormatter formatter)
        {
            if (step == FormatterLocationStep.BeforeRegisteredFormatters && type.FullName == "UnityEngine.UI.ColorBlock")
            {
                var formatterType = typeof(ColorBlockFormatter<>).MakeGenericType(type);
                formatter = (IFormatter)Activator.CreateInstance(formatterType);
                return true;
            }

            formatter = null;
            return false;
        }
    }

    /// <summary>
    /// Custom formatter for the <see cref="ColorBlock"/> type.
    /// </summary>
    /// <seealso cref="MinimalBaseFormatter{UnityEngine.UI.ColorBlock}" />
    public sealed class ColorBlockFormatter<T> : MinimalBaseFormatter<T>
    {
        private static readonly Serializer<float> s_FloatSerializer = Serializer.Get<float>();
        private static readonly Serializer<Color> s_ColorSerializer = Serializer.Get<Color>();

        private static readonly PropertyInfo s_NormalColor = typeof(T).GetProperty("normalColor");
        private static readonly PropertyInfo s_HighlightedColor = typeof(T).GetProperty("highlightedColor");
        private static readonly PropertyInfo s_PressedColor = typeof(T).GetProperty("pressedColor");
        private static readonly PropertyInfo s_DisabledColor = typeof(T).GetProperty("disabledColor");
        private static readonly PropertyInfo s_ColorMultiplier = typeof(T).GetProperty("colorMultiplier");
        private static readonly PropertyInfo s_FadeDuration = typeof(T).GetProperty("fadeDuration");
        
        /// <summary>
        /// Reads into the specified value using the specified reader.
        /// </summary>
        /// <param name="value">The value to read into.</param>
        /// <param name="reader">The reader to use.</param>
        protected override void Read(ref T value, IDataReader reader)
        {
            object boxed = value;

            s_NormalColor.SetValue(boxed, s_ColorSerializer.ReadValue(reader), null);
            s_HighlightedColor.SetValue(boxed, s_ColorSerializer.ReadValue(reader), null);
            s_PressedColor.SetValue(boxed, s_ColorSerializer.ReadValue(reader), null);
            s_DisabledColor.SetValue(boxed, s_ColorSerializer.ReadValue(reader), null);
            s_ColorMultiplier.SetValue(boxed, s_FloatSerializer.ReadValue(reader), null);
            s_FadeDuration.SetValue(boxed, s_FloatSerializer.ReadValue(reader), null);

            value = (T)boxed;
        }

        /// <summary>
        /// Writes from the specified value using the specified writer.
        /// </summary>
        /// <param name="value">The value to write from.</param>
        /// <param name="writer">The writer to use.</param>
        protected override void Write(ref T value, IDataWriter writer)
        {
            s_ColorSerializer.WriteValue((Color)s_NormalColor.GetValue(value, null), writer);
            s_ColorSerializer.WriteValue((Color)s_HighlightedColor.GetValue(value, null), writer);
            s_ColorSerializer.WriteValue((Color)s_PressedColor.GetValue(value, null), writer);
            s_ColorSerializer.WriteValue((Color)s_DisabledColor.GetValue(value, null), writer);
            s_FloatSerializer.WriteValue((float)s_ColorMultiplier.GetValue(value, null), writer);
            s_FloatSerializer.WriteValue((float)s_FadeDuration.GetValue(value, null), writer);
        }
    }
}