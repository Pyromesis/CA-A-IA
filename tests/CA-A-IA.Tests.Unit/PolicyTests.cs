// CA-A-IA · Fase 0 — Tests de políticas: reparación, permisos y registro de proveedores.

using CaAIA.Agent.Policies;
using CaAIA.Domain.AI;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Security;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.AI;
using CaAIA.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Unit;

public sealed class PolicyTests
{
    [Theory]
    [InlineData(FailureCategory.CodeError, true)]
    [InlineData(FailureCategory.TestFailure, true)]
    [InlineData(FailureCategory.BuildFailure, true)]
    [InlineData(FailureCategory.ToolFailure, true)]
    [InlineData(FailureCategory.NetworkFailure, true)]
    [InlineData(FailureCategory.ProviderFailure, true)]
    [InlineData(FailureCategory.DependencyFailure, false)]
    [InlineData(FailureCategory.EnvironmentFailure, false)]
    [InlineData(FailureCategory.PermissionFailure, false)]
    [InlineData(FailureCategory.Unknown, false)]
    public void RepairPolicy_RepairsOwnCode_EscalatesEnvironment(FailureCategory category, bool expected)
    {
        var policy = new DefaultRepairPolicy();
        Assert.Equal(expected, policy.ShouldRepair(category, attempts: 0, maxAttempts: 3));
        Assert.False(policy.ShouldRepair(FailureCategory.CodeError, attempts: 3, maxAttempts: 3));
    }

    [Fact]
    public void PermissionService_DeniesWritesOutsideScope()
    {
        var service = new ToolPermissionService();
        var scope = new ExecutionScope(
            AllowedPaths: new[] { @"C:\work" },
            DeniedPaths: Array.Empty<string>(),
            GrantedPermissions: ToolPermission.Read | ToolPermission.Write,
            RequireConfirmationForWrite: false,
            RequireConfirmationForExecute: true);
        var definition = new ToolDefinition("WriteFile", "WriteFile", "Writes.", ToolKind.FileSystem,
            ToolPermission.Write, Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(30));

        var outside = new ToolInvocation(Guid.NewGuid(), "WriteFile",
            """{"path":"C:\\other\\evil.txt"}""",
            new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        var decision = service.Authorize(outside, definition, scope);
        Assert.False(decision.Allowed);

        var inside = outside with { ArgumentsJson = """{"path":"C:\\work\\ok.txt"}""" };
        var ok = service.Authorize(inside, definition, scope);
        Assert.True(ok.Allowed);
    }

    [Fact]
    public void PermissionService_DeniesMissingGrants_AndFlagsConfirmation()
    {
        var service = new ToolPermissionService();
        var readOnly = ExecutionScope.ReadOnlyWorkspace(@"C:\work");
        var definition = new ToolDefinition("Exec", "Exec", "Runs.", ToolKind.Command,
            ToolPermission.Execute, Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(30));
        var invocation = new ToolInvocation(Guid.NewGuid(), "Exec", "{}",
            new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(service.Authorize(invocation, definition, readOnly).Allowed);

        var confirmScope = readOnly with { GrantedPermissions = ToolPermission.Read | ToolPermission.Execute };
        var needsConfirm = service.Authorize(
            invocation with { ArgumentsJson = """{"path":"C:\\work\\run.ps1"}""" }, definition, confirmScope);
        Assert.True(needsConfirm.Allowed);
        Assert.True(needsConfirm.RequiresUserConfirmation);
    }

    [Fact]
    public void AuthorizationLevels_MapToScopes()
    {
        var denied = Array.Empty<string>();

        var l1 = ExecutionScope.FromAuthorizationLevel(@"C:\work", denied, AuthorizationLevel.ConfirmChanges, false);
        Assert.True(l1.RequireConfirmationForWrite);
        Assert.True(l1.RequireConfirmationForExecute);
        Assert.NotEqual(ToolPermission.None, l1.GrantedPermissions & ToolPermission.Execute);

        var l2 = ExecutionScope.FromAuthorizationLevel(@"C:\work", denied, AuthorizationLevel.EditWithoutPcControl, false);
        Assert.False(l2.RequireConfirmationForWrite);
        Assert.Equal(ToolPermission.None, l2.GrantedPermissions & ToolPermission.Execute);
        Assert.NotEqual(ToolPermission.None, l2.GrantedPermissions & ToolPermission.Write);

        var l3 = ExecutionScope.FromAuthorizationLevel(@"C:\work", denied, AuthorizationLevel.FullControl, false);
        Assert.False(l3.RequireConfirmationForWrite);
        Assert.False(l3.RequireConfirmationForExecute);
        Assert.NotEqual(ToolPermission.None, l3.GrantedPermissions & ToolPermission.Execute);
        Assert.NotEqual(ToolPermission.None, l3.GrantedPermissions & ToolPermission.Write);
    }

    [Fact]
    public void AuthorizationLevels_EnforcePcControl()
    {
        var service = new ToolPermissionService();
        var correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var execDef = new ToolDefinition("ExecuteCommand", "ExecuteCommand", "Runs.", ToolKind.Command,
            ToolPermission.Execute, Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(30));
        var writeDef = new ToolDefinition("WriteFile", "WriteFile", "Writes.", ToolKind.FileSystem,
            ToolPermission.Write, Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(30));
        var denied = Array.Empty<string>();

        // Nivel 1: escritura y ejecución permitidas pero con confirmación.
        var l1 = ExecutionScope.FromAuthorizationLevel(@"C:\work", denied, AuthorizationLevel.ConfirmChanges, false);
        var l1Write = service.Authorize(
            new ToolInvocation(Guid.NewGuid(), "WriteFile", """{"path":"C:\\work\\a.txt"}""", correlation),
            writeDef, l1);
        Assert.True(l1Write.Allowed);
        Assert.True(l1Write.RequiresUserConfirmation);
        var l1Exec = service.Authorize(
            new ToolInvocation(Guid.NewGuid(), "ExecuteCommand", """{"workdir":"C:\\work"}""", correlation),
            execDef, l1);
        Assert.True(l1Exec.Allowed);
        Assert.True(l1Exec.RequiresUserConfirmation);

        // Nivel 2: escribe sin confirmar; ejecutar (control del PC) denegado.
        var l2 = ExecutionScope.FromAuthorizationLevel(@"C:\work", denied, AuthorizationLevel.EditWithoutPcControl, false);
        var l2Write = service.Authorize(
            new ToolInvocation(Guid.NewGuid(), "WriteFile", """{"path":"C:\\work\\a.txt"}""", correlation),
            writeDef, l2);
        Assert.True(l2Write.Allowed);
        Assert.False(l2Write.RequiresUserConfirmation);
        var l2Exec = service.Authorize(
            new ToolInvocation(Guid.NewGuid(), "ExecuteCommand", """{"workdir":"C:\\work"}""", correlation),
            execDef, l2);
        Assert.False(l2Exec.Allowed);

        // Nivel 3: escribe y ejecuta sin confirmar.
        var l3 = ExecutionScope.FromAuthorizationLevel(@"C:\work", denied, AuthorizationLevel.FullControl, false);
        var l3Exec = service.Authorize(
            new ToolInvocation(Guid.NewGuid(), "ExecuteCommand", """{"workdir":"C:\\work"}""", correlation),
            execDef, l3);
        Assert.True(l3Exec.Allowed);
        Assert.False(l3Exec.RequiresUserConfirmation);
    }

    [Fact]
    public void ProviderRegistry_RegisterGetRemove_Works()
    {
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var fake = new FakeProvider("fake", ProviderCapabilities.Tools);
        registry.Register(fake);
        Assert.Same(fake, registry.Get("FAKE")); // case-insensitive
        Assert.Single(registry.GetAll());
        Assert.Single(registry.GetProvidersSupporting(ProviderCapabilities.Tools));
        Assert.Empty(registry.GetProvidersSupporting(ProviderCapabilities.Vision));
        Assert.Throws<InvalidOperationException>(() => registry.Register(fake));
        Assert.True(registry.Remove("fake"));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("fake"));
    }

    [Fact]
    public async Task ResilientProvider_RetriesTransient_ThenSucceeds()
    {
        var fake = new FakeProvider("fake", ProviderCapabilities.None, failTimes: 2);
        var resilient = new ResilientAIProvider(fake, 3, NullLogger<ResilientAIProvider>.Instance);
        var response = await resilient.CompleteAsync(
            new AIRequest
            {
                ModelId = "m",
                Correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            }, CancellationToken.None);
        Assert.Equal("ok", response.Content);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task ResilientProvider_DoesNotRetryAuth()
    {
        var fake = new AlwaysFailProvider(AIErrorKind.Authentication, isRetryable: false);
        var resilient = new ResilientAIProvider(fake, 3, NullLogger<ResilientAIProvider>.Instance);
        await Assert.ThrowsAsync<AIProviderException>(() => resilient.CompleteAsync(
            new AIRequest
            {
                ModelId = "m",
                Correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            }, CancellationToken.None));
        Assert.Equal(1, fake.Calls);
    }

    // ---- Doubles ----

    private sealed class FakeProvider : IAIProvider
    {
        private int _failuresLeft;
        public int Calls { get; private set; }
        public FakeProvider(string id, ProviderCapabilities caps, int failTimes = 0)
        {
            Id = id;
            Capabilities = caps;
            _failuresLeft = failTimes;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public ProviderCapabilities Capabilities { get; }

        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_failuresLeft-- > 0)
            {
                throw new AIProviderException(Id, AIErrorKind.Network, "transient", isRetryable: true,
                    retryAfter: TimeSpan.Zero);
            }

            return Task.FromResult(new AIResponse("ok", Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1)));
        }

        public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<AIStreamChunk>();

        public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(
                new[] { new AIModel("m", "M", Id, Capabilities: Capabilities) });

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class AlwaysFailProvider : IAIProvider
    {
        private readonly AIErrorKind _kind;
        private readonly bool _retryable;
        public int Calls { get; private set; }
        public AlwaysFailProvider(AIErrorKind kind, bool isRetryable) { _kind = kind; _retryable = isRetryable; }
        public string Id => "always-fail";
        public string DisplayName => Id;
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AIProviderException(Id, _kind, "fatal", isRetryable: _retryable);
        }

        public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<AIStreamChunk>();
        public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(Array.Empty<AIModel>());
        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
