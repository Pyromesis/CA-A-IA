// CA-A-IA — Test de regresión: el grafo eager de construcción desde MainWindow no puede
// volver a MainWindow (fue el bug de las ~190 ventanas ocultas: ciclo
// MainWindow→…→DialogConfirmation→MainWindow; Func<> perezoso lo rompe y este test lo vigila).

using System.Reflection;

namespace CaAIA.Tests.Architecture;

public sealed class CompositionCycleTests
{
    [Fact]
    public void MainWindowGraph_HasNoConstructionCycle()
    {
        var assemblies = new[]
        {
            typeof(Domain.Enums.AgentState).Assembly,
            typeof(Application.DTOs.SessionDto).Assembly,
            typeof(Agent.StateMachine.AgentStateMachine).Assembly,
            typeof(Infrastructure.AI.ProviderRegistry).Assembly,
            LoadPresentation(),
        };
        var ours = assemblies.ToDictionary(a => a.FullName!, a => a);
        var types = assemblies.SelectMany(SafeTypes)
            .Where(t => t.IsClass && !t.IsAbstract && !t.Name.Contains('<'))
            .ToList();
        var implementers = types
            .SelectMany(t => t.GetInterfaces(), (t, i) => (Impl: t, Iface: i))
            .GroupBy(x => x.Iface)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Impl).ToList());

        var mainWindow = types.FirstOrDefault(t => t.Name == "MainWindow")
            ?? throw new InvalidOperationException("MainWindow not found.");
        if (FindCycleTo(mainWindow, mainWindow, ours, implementers, new List<string>(), new HashSet<Type>()) is { } path)
        {
            Assert.Fail("Construction cycle to MainWindow: " + string.Join(" -> ", path));
        }
    }

    [Fact]
    public void UiServices_NeverTakeWindowEagerly()
    {
        // Regresión del bug de las ~190 ventanas ocultas: el ciclo real iba por llamada a
        // método (MainWindow.Navigate→…→DialogConfirmation→MainWindow), invisible al análisis
        // de constructores. Regla estructural: Window/MainWindow solo tras Func<>/Lazy<>.
        var assemblies = new[]
        {
            typeof(Domain.Enums.AgentState).Assembly,
            typeof(Application.DTOs.SessionDto).Assembly,
            typeof(Agent.StateMachine.AgentStateMachine).Assembly,
            typeof(Infrastructure.AI.ProviderRegistry).Assembly,
            LoadPresentation(),
        };
        var violations = new List<string>();
        foreach (var type in assemblies.SelectMany(SafeTypes).Where(t => t.IsClass && !t.IsAbstract))
        {
            ConstructorInfo[] ctors;
            try
            {
                ctors = type.GetConstructors();
            }
            catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException)
            {
                continue;
            }

            foreach (var param in ctors.SelectMany(c => c.GetParameters()))
            {
                var pt = param.ParameterType;
                if (pt.IsGenericType && (pt.GetGenericTypeDefinition() == typeof(Func<>)
                    || pt.GetGenericTypeDefinition() == typeof(Lazy<>)))
                {
                    continue; // perezoso: no construye en el grafo eager
                }

                if (IsWindowType(pt))
                {
                    violations.Add($"{type.FullName}({param.Name}: {pt.Name})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Eager Window dependencies (use Func<Window>): " + string.Join("; ", violations));
    }

    private static bool IsWindowType(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.Name == "MainWindow" || (t.Name == "Window" && (t.Namespace ?? string.Empty).Contains("UI.Xaml")))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string>? FindCycleTo(
        Type current, Type target,
        Dictionary<string, Assembly> ours,
        Dictionary<Type, List<Type>> implementers,
        List<string> path, HashSet<Type> visiting)
    {
        path.Add(current.Name);
        if (current == target && path.Count > 1)
        {
            return new List<string>(path);
        }

        if (!visiting.Add(current))
        {
            path.RemoveAt(path.Count - 1);
            return null; // decoradores (IEventBus->RecordingEventBus->IEventBus): no es ciclo de construcción
        }

        foreach (var next in EagerDependencies(current, ours, implementers))
        {
            if (FindCycleTo(next, target, ours, implementers, path, visiting) is { } found)
            {
                return found;
            }
        }

        visiting.Remove(current);
        path.RemoveAt(path.Count - 1);
        return null;
    }

    private static IEnumerable<Type> EagerDependencies(
        Type type, Dictionary<string, Assembly> ours, Dictionary<Type, List<Type>> implementers)
    {
        ConstructorInfo? ctor;
        try
        {
            ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException)
        {
            yield break;
        }

        if (ctor is null)
        {
            yield break;
        }

        foreach (var param in ctor.GetParameters())
        {
            var pt = param.ParameterType;
            if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Func<>))
            {
                continue; // perezoso: rompe el ciclo en runtime
            }

            if (pt.Assembly.FullName is string full && ours.ContainsKey(full) && pt.IsClass && !pt.IsAbstract)
            {
                yield return pt;
            }
            else if (pt.IsInterface && implementers.TryGetValue(pt, out var impls))
            {
                foreach (var impl in impls)
                {
                    yield return impl;
                }
            }
        }
    }

    private static Assembly LoadPresentation()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "CA-A-IA.Presentation");
        if (loaded is not null)
        {
            return loaded;
        }

        var dir = Path.GetDirectoryName(typeof(CompositionCycleTests).Assembly.Location)!;
        return Assembly.LoadFrom(Path.Combine(dir, "CA-A-IA.Presentation.dll"));
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
