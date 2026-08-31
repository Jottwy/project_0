// Shim mínimo de NUnit para poder EJECUTAR los ficheros de test reales sin Unity.
//
// El editor de Unity está tomado por otra sesión y no se mata (regla de convivencia), así que la
// suite EditMode no se puede lanzar. Esto compila los MISMOS ficheros de test, sin tocarlos, y los
// corre por reflexión. No sustituye a la suite —no cubre lo que depende de UnityEngine— pero
// convierte "compila" en "pasa", que es otra cosa.
using System;
using System.Collections;
using System.Collections.Generic;

namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class TestCaseAttribute : Attribute
    {
        public object[] Arguments { get; }
        public TestCaseAttribute(params object[] arguments) { Arguments = arguments; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SetUpAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TearDownAttribute : Attribute { }

    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static class Assert
    {
        private static void Fail(string message) => throw new AssertionException(message);

        private static string Show(object value) => value == null ? "<null>" : "'" + value + "'";

        public static void AreEqual(object expected, object actual, string message = null)
        {
            if (expected is int && actual is int)
            {
                if ((int)expected == (int)actual) return;
            }
            else if (Equals(expected, actual))
            {
                return;
            }

            Fail($"esperado {Show(expected)}, obtenido {Show(actual)}. {message}");
        }

        public static void AreNotEqual(object expected, object actual, string message = null)
        {
            if (!Equals(expected, actual)) return;
            Fail($"no debia ser {Show(expected)}. {message}");
        }

        public static void AreSame(object expected, object actual, string message = null)
        {
            if (ReferenceEquals(expected, actual)) return;
            Fail($"esperaba la MISMA instancia. {message}");
        }

        public static void IsTrue(bool condition, string message = null)
        {
            if (!condition) Fail($"esperaba true. {message}");
        }

        public static void IsFalse(bool condition, string message = null)
        {
            if (condition) Fail($"esperaba false. {message}");
        }

        public static void IsNull(object value, string message = null)
        {
            if (value != null) Fail($"esperaba null, obtenido {Show(value)}. {message}");
        }

        public static void IsNotNull(object value, string message = null)
        {
            if (value == null) Fail($"esperaba algo, obtenido null. {message}");
        }

        public static void Less(long actual, long limit, string message = null)
        {
            if (actual >= limit) Fail($"{actual} no es menor que {limit}. {message}");
        }

        public static void GreaterOrEqual(long actual, long limit, string message = null)
        {
            if (actual < limit) Fail($"{actual} no llega a {limit}. {message}");
        }
    }

    public static class StringAssert
    {
        public static void Contains(string expected, string actual, string message = null)
        {
            if (actual != null && actual.Contains(expected)) return;
            throw new AssertionException($"'{actual}' no contiene '{expected}'. {message}");
        }

        public static void DoesNotContain(string expected, string actual, string message = null)
        {
            if (actual == null || !actual.Contains(expected)) return;
            throw new AssertionException($"'{actual}' contiene '{expected}' y no debia. {message}");
        }

        public static void StartsWith(string expected, string actual, string message = null)
        {
            if (actual != null && actual.StartsWith(expected, StringComparison.Ordinal)) return;
            throw new AssertionException($"'{actual}' no empieza por '{expected}'. {message}");
        }
    }

    public static class CollectionAssert
    {
        public static void IsEmpty(IEnumerable collection, string message = null)
        {
            foreach (object item in collection)
                throw new AssertionException($"la coleccion tenia {item}. {message}");
        }

        public static void AllItemsAreUnique(IEnumerable collection, string message = null)
        {
            var seen = new List<object>();
            foreach (object item in collection)
            {
                if (seen.Contains(item)) throw new AssertionException($"repetido: {item}. {message}");
                seen.Add(item);
            }
        }
    }
}
