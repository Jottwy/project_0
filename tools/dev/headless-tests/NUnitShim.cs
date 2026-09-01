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

    /// <summary>Marca de clase de pruebas. El shim no la usa para nada -- el runner descubre por
    /// los metodos [Test] -- pero tiene que EXISTIR para que un fichero de test real compile sin
    /// tocarlo, que es la regla de este arnes.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TestFixtureAttribute : Attribute { }

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

        public static void Greater(long actual, long limit, string message = null)
        {
            if (actual <= limit) Fail($"{actual} no supera a {limit}. {message}");
        }

        // Sobrecargas en coma flotante: sin ellas un test que compara floats no compila, y
        // reescribir el test para el arnes seria falsear lo que el arnes dice medir.
        public static void Less(double actual, double limit, string message = null)
        {
            if (actual >= limit) Fail($"{actual} no es menor que {limit}. {message}");
        }

        public static void GreaterOrEqual(double actual, double limit, string message = null)
        {
            if (actual < limit) Fail($"{actual} no llega a {limit}. {message}");
        }

        public static void Greater(double actual, double limit, string message = null)
        {
            if (actual <= limit) Fail($"{actual} no supera a {limit}. {message}");
        }

        public static void IsNotEmpty(IEnumerable collection, string message = null)
        {
            foreach (object unused in collection) return;
            Fail($"la coleccion estaba vacia. {message}");
        }

        /// <summary>El unico constraint que usan estos ficheros: `Is.EqualTo(x).Within(t)`.</summary>
        public static void That(double actual, WithinConstraint constraint, string message = null)
        {
            if (Math.Abs(actual - constraint.Expected) <= constraint.Tolerance) return;
            Fail($"{actual} no esta a {constraint.Tolerance} de {constraint.Expected}. {message}");
        }
    }

    /// <summary>`Is.EqualTo(x).Within(t)`, lo minimo para que un test real compile sin cambiarlo.</summary>
    public sealed class WithinConstraint
    {
        public double Expected;
        public double Tolerance;
        public WithinConstraint Within(double tolerance) { Tolerance = tolerance; return this; }
    }

    public static class Is
    {
        public static WithinConstraint EqualTo(double expected)
            => new WithinConstraint { Expected = expected, Tolerance = 0d };
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

        private static List<object> ToList(IEnumerable collection)
        {
            var list = new List<object>();
            if (collection != null)
                foreach (object item in collection) list.Add(item);
            return list;
        }

        public static void AreEqual(IEnumerable expected, IEnumerable actual, string message = null)
        {
            var a = ToList(expected);
            var b = ToList(actual);
            if (a.Count == b.Count)
            {
                bool same = true;
                for (int i = 0; i < a.Count; i++)
                    if (!Equals(a[i], b[i])) { same = false; break; }
                if (same) return;
            }
            throw new AssertionException($"colecciones distintas ({a.Count} vs {b.Count}). {message}");
        }

        public static void AreNotEqual(IEnumerable expected, IEnumerable actual, string message = null)
        {
            var a = ToList(expected);
            var b = ToList(actual);
            if (a.Count != b.Count) return;
            for (int i = 0; i < a.Count; i++)
                if (!Equals(a[i], b[i])) return;
            throw new AssertionException($"las dos colecciones son iguales y no debian. {message}");
        }

        public static void DoesNotContain(IEnumerable collection, object unexpected, string message = null)
        {
            foreach (object item in ToList(collection))
                if (Equals(item, unexpected))
                    throw new AssertionException($"la coleccion contiene {unexpected}. {message}");
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
