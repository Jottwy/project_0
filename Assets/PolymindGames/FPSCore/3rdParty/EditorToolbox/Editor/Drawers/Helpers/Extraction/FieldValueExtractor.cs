using System.Reflection;
using System;

namespace Toolbox.Editor.Drawers
{
    public class FieldValueExtractor : IValueExtractor
    {
        public bool TryGetValue(string source, object declaringObject, out object value)
        {
            value = default(object);
            if (string.IsNullOrEmpty(source))
                return false;

            var type = declaringObject.GetType();

            var info = type.GetField(source, ReflectionUtility.allBindings);
            if (info == null)
            {
                info = GetPrivateField(type, source);
                if (info == null)
                    return false;
            }

            value = info.GetValue(declaringObject);
            return true;
        }
        
        private static FieldInfo GetPrivateField(Type t, string source)
        {
            const BindingFlags FLAGS = BindingFlags.Instance |
                                       BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;

            int iterrations = 0;

            FieldInfo fi;
            while ((fi = t.GetField(source, FLAGS)) == null && (t = t.BaseType) != null && iterrations < 12)
                iterrations++;
            return fi;
        }
    }
}