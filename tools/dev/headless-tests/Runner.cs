using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

// Corre por reflexion las clases de test que no dependen de UnityEngine. Mismo contrato que el
// Test Runner: SetUp antes de cada caso, TearDown despues (tambien si el caso falla), un
// TestCase = un caso.
internal static class Runner
{
    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    private static int Main()
    {
        Type[] classes = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "BackroomsSurvival.Tests")
            // Las clases de cierre que genera el compilador viven en el mismo namespace y no son
            // tests. Sin este filtro salen treinta y cinco lineas de ruido por ejecucion.
            .Where(t => !t.Name.StartsWith("<") &&
                        t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null)
            .OrderBy(t => t.Name)
            .ToArray();

        foreach (Type type in classes) RunClass(type);

        Console.WriteLine();
        foreach (string failure in Failures) Console.WriteLine("FALLO  " + failure);
        Console.WriteLine();
        Console.WriteLine($"RESULTADO: {_passed} pasan, {Failures.Count} fallan, {classes.Length} clases");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void RunClass(Type type)
    {
        MethodInfo setUp = type.GetMethods().FirstOrDefault(m => m.GetCustomAttribute<SetUpAttribute>() != null);
        MethodInfo tearDown = type.GetMethods().FirstOrDefault(m => m.GetCustomAttribute<TearDownAttribute>() != null);

        int before = _passed;
        int failedBefore = Failures.Count;

        foreach (MethodInfo method in type.GetMethods())
        {
            var cases = method.GetCustomAttributes<TestCaseAttribute>().ToArray();
            bool plain = method.GetCustomAttribute<TestAttribute>() != null;
            if (!plain && cases.Length == 0) continue;

            if (cases.Length > 0)
            {
                foreach (TestCaseAttribute testCase in cases)
                {
                    // `[TestCase(null)]` con un solo argumento ata el null AL PROPIO params, asi
                    // que Arguments llega null y no como { null }.
                    object[] raw = testCase.Arguments ?? new object[] { null };
                    Invoke(type, setUp, tearDown, method, Coerce(method, raw));
                }
            }
            else
            {
                Invoke(type, setUp, tearDown, method, Array.Empty<object>());
            }
        }

        Console.WriteLine($"{type.Name}: {_passed - before} pasan, {Failures.Count - failedBefore} fallan");
    }

    /// Los atributos guardan los literales tal cual; un `[TestCase(7778)]` sobre un parametro
    /// `int` ya llega bien, pero un null sobre `string` y los enum piden conversion explicita.
    private static object[] Coerce(MethodInfo method, object[] arguments)
    {
        ParameterInfo[] parameters = method.GetParameters();
        var coerced = new object[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            object value = arguments[i];
            Type wanted = i < parameters.Length ? parameters[i].ParameterType : typeof(object);
            if (value != null && wanted.IsEnum) value = Enum.ToObject(wanted, value);
            else if (value != null && wanted != value.GetType() && wanted.IsValueType)
                value = Convert.ChangeType(value, wanted);
            coerced[i] = value;
        }

        return coerced;
    }

    private static void Invoke(Type type, MethodInfo setUp, MethodInfo tearDown, MethodInfo method,
        object[] arguments)
    {
        string label = type.Name + "." + method.Name +
                       (arguments.Length == 0 ? "" : "(" + string.Join(", ", arguments.Select(a => a ?? "null")) + ")");
        object instance = Activator.CreateInstance(type);

        try
        {
            setUp?.Invoke(instance, null);
            method.Invoke(instance, arguments);
            _passed++;
        }
        catch (TargetInvocationException e)
        {
            Failures.Add(label + " — " + (e.InnerException?.Message ?? e.Message));
        }
        catch (Exception e)
        {
            Failures.Add(label + " — " + e.Message);
        }
        finally
        {
            try { tearDown?.Invoke(instance, null); }
            catch (Exception) { /* un TearDown que falla no puede tapar el fallo real */ }
        }
    }
}
