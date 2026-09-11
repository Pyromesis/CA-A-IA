// CA-A-IA · Fase 0 — Tests de arquitectura: dirección de dependencias entre capas,
// ubicación de adaptadores y base WinUI real de Presentation. Sin paquetes externos:
// reflexión sobre metadatos de assemblies (no se ejecuta UI).

using System.Reflection;

namespace CaAIA.Tests.Architecture;

public sealed class LayerDependencyTests
{
    private static Assembly Domain => typeof(Domain.Enums.AgentState).Assembly;
    private static Assembly Application => typeof(Application.DTOs.SessionDto).Assembly;
    private static Assembly Agent => typeof(Agent.StateMachine.AgentStateMachine).Assembly;
    private static Assembly Infrastructure => typeof(Infrastructure.AI.ProviderRegistry).Assembly;

    private static Assembly Presentation
    {
        get
        {
            // Carga por metadatos: no se instancia ningún tipo WinUI.
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "CA-A-IA.Presentation");
            if (loaded is not null)
            {
                return loaded;
            }

            var dir = Path.GetDirectoryName(typeof(LayerDependencyTests).Assembly.Location)!;
            return Assembly.LoadFrom(Path.Combine(dir, "CA-A-IA.Presentation.dll"));
        }
    }

    private static IReadOnlySet<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToHashSet();

    private static bool References(Assembly from, Assembly to) =>
        ReferencesOf(from).Contains(to.GetName().Name ?? string.Empty);

    private static void AssertReferencesOnly(Assembly from, params Assembly[] allowed)
    {
        var allowedNames = allowed.Select(a => a.GetName().Name).ToHashSet();
        var offenders = ReferencesOf(from)
            .Where(r => r.StartsWith("CA-A-IA.", StringComparison.Ordinal) && !allowedNames.Contains(r))
            .ToList();
        Assert.True(offenders.Count == 0,
            $"{from.GetName().Name} references forbidden assemblies: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Domain_ReferencesNoLayers()
    {
        var offenders = ReferencesOf(Domain)
            .Where(r => r.StartsWith("CA-A-IA.", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Application_ReferencesOnlyDomain() =>
        AssertReferencesOnly(Application, Domain);

    [Fact]
    public void Agent_ReferencesOnlyDomainAndApplication() =>
        AssertReferencesOnly(Agent, Domain, Application);

    [Fact]
    public void Infrastructure_ReferencesOnlyDomainAndApplication() =>
        AssertReferencesOnly(Infrastructure, Domain, Application);

    [Fact]
    public void Presentation_ReferencesWindowsAppSDK()
    {
        var refs = ReferencesOf(Presentation);
        // El SDK expone WinUI vía el ref Microsoft.WinUI (paquete Microsoft.WindowsAppSDK).
        Assert.Contains(refs, r => r == "Microsoft.WinUI" || r == "Microsoft.WindowsAppSDK");
    }

    [Fact]
    public void Presentation_DoesNotBypassLayers()
    {
        // Composition Root: puede referenciarlas todas, pero NUNCA a los tests.
        var offenders = ReferencesOf(Presentation)
            .Where(r => r.StartsWith("CA-A-IA.Tests", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);
        Assert.True(References(Presentation, Domain));
        Assert.True(References(Presentation, Application));
        Assert.True(References(Presentation, Agent));
        Assert.True(References(Presentation, Infrastructure));
    }

    [Fact]
    public void ProviderImplementations_LiveOnlyInInfrastructure()
    {
        var providerType = typeof(Domain.AI.IAIProvider);
        var implementers = new[] { Domain, Application, Agent, Infrastructure, Presentation }
            .SelectMany(a => SafeTypes(a))
            .Where(t => !t.IsInterface && !t.IsAbstract && providerType.IsAssignableFrom(t))
            .Where(t => !t.Name.Contains("Resilient", StringComparison.Ordinal)) // decorador, también en Infra
            .ToList();
        Assert.NotEmpty(implementers);
        foreach (var impl in implementers)
        {
            Assert.Equal(Infrastructure.FullName, impl.Assembly.FullName);
        }
    }

    [Fact]
    public void StateMachineImplementation_LivesInAgent()
    {
        var machineType = typeof(Domain.Execution.IAgentStateMachine);
        var impl = new[] { Domain, Application, Agent, Infrastructure }
            .SelectMany(SafeTypes)
            .FirstOrDefault(t => !t.IsInterface && !t.IsAbstract && machineType.IsAssignableFrom(t));
        Assert.NotNull(impl);
        Assert.Equal(Agent.FullName, impl.Assembly.FullName);
    }

    [Fact]
    public void NoGodClasses_NoTypeWithTooManyPublicMethods()
    {
        // SOLID: ninguna clase pública con más de 25 MÉTODOS reales. Se excluyen accessors de
        // propiedades, operadores y miembros sintetizados de records (una entidad rica en
        // invariantes pequeñas como Plan/AgentTask no es una god-class).
        var offenders = new[] { Domain, Application, Agent, Infrastructure }
            .SelectMany(SafeTypes)
            .Where(t => t.IsClass && t.IsPublic && !t.IsAbstract)
            .Select(t => (Type: t, Count: t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Count(m => !m.IsSpecialName && m.Name is not ("Deconstruct" or "PrintMembers" or "ToString" or "GetHashCode" or "Equals"))))
            .Where(x => x.Count > 25)
            .Select(x => $"{x.Type.FullName} ({x.Count})")
            .ToList();
        Assert.True(offenders.Count == 0, "God classes: " + string.Join("; ", offenders));
    }

    [Fact]
    public void ViewModels_DoNotReferenceProvidersOrStores()
    {
        // MVVM: los ViewModels orquestan Application (coordinador/DTOs) y observan el bus;
        // nunca referencian adaptadores de Infrastructure (providers, stores, git, DPAPI).
        var viewModels = SafeTypes(Presentation)
            .Where(t => t.IsClass && (t.Namespace ?? string.Empty).Contains("ViewModels", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(viewModels);

        var violations = new List<string>();
        foreach (var vm in viewModels)
        {
            foreach (var memberType in MemberTypes(vm))
            {
                if (memberType.Assembly == Infrastructure)
                {
                    violations.Add($"{vm.Name} -> {memberType.FullName}");
                    continue;
                }

                var ns = memberType.Namespace ?? string.Empty;
                if (ns.StartsWith("CaAIA.Infrastructure", StringComparison.Ordinal))
                {
                    violations.Add($"{vm.Name} -> {memberType.FullName}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "ViewModels referencing Infrastructure: " + string.Join("; ", violations));
    }

    private static IEnumerable<Type> MemberTypes(Type type)
    {
        var probe = new List<Type>();
        try
        {
            if (type.BaseType is not null)
            {
                probe.Add(type.BaseType);
            }

            probe.AddRange(type.GetInterfaces());
            probe.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(f => f.FieldType));
            probe.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(p => p.PropertyType));
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName && (m.Name.StartsWith("get_", StringComparison.Ordinal) || m.Name.StartsWith("set_", StringComparison.Ordinal)))
                {
                    continue; // ya cubiertos por propiedades
                }

                probe.Add(m.ReturnType);
                probe.AddRange(m.GetParameters().Select(p => p.ParameterType));
            }
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or MissingMethodException)
        {
            // Entorno sin runtime WinUI completo: se inspecciona lo cargable (evidencia positiva sigue valiendo).
        }

        return probe.Where(t => t.Assembly != typeof(object).Assembly);
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
