// CA-A-IA — Tests de endurecimiento (auditoría: path scope, permisos, git, red, backoff).

using CaAIA.Domain.Correlation;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Security;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.AI;
using CaAIA.Infrastructure.FileSystem;
using CaAIA.Infrastructure.Git;
using CaAIA.Infrastructure.Providers.OpenCode;
using CaAIA.Infrastructure.Security;

namespace CaAIA.Tests.Unit;

public sealed class SecurityHardeningTests
{
    private static ExecutionScope Scope(string workspace) => new(
        AllowedPaths: new[] { workspace },
        DeniedPaths: Array.Empty<string>(),
        GrantedPermissions: ToolPermission.Read | ToolPermission.Write | ToolPermission.Execute,
        RequireConfirmationForWrite: false,
        RequireConfirmationForExecute: false);

    private static CorrelationContext Ctx() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void IsPathAllowed_BlocksTraversal_AndSiblingPrefix()
    {
        var scope = Scope(@"C:\work");
        Assert.False(scope.IsPathAllowed(@"C:\work\..\other\evil.txt"));
        Assert.False(scope.IsPathAllowed(@"C:\work-evil\ok.txt"));
        Assert.False(scope.IsPathAllowed(@"C:\other\evil.txt"));
        Assert.True(scope.IsPathAllowed(@"C:\work\sub\ok.txt"));
        Assert.True(scope.IsPathAllowed(@"C:\WORK\sub\ok.txt"));
        Assert.True(scope.IsPathAllowed(@"C:\work"));
        Assert.False(scope.IsPathAllowed(string.Empty));
    }

    [Fact]
    public void IsPathAllowed_RejectsUnc_AndHonorsDenied()
    {
        var scope = new ExecutionScope(
            new[] { @"C:\work" },
            new[] { @"C:\work\secret" },
            ToolPermission.Read | ToolPermission.Write,
            RequireConfirmationForWrite: false,
            RequireConfirmationForExecute: false);
        Assert.False(scope.IsPathAllowed(@"\\server\share\file.txt"));
        Assert.False(scope.IsPathAllowed(@"C:\work\secret\keys.txt"));
        Assert.True(scope.IsPathAllowed(@"C:\work\secret-evil\ok.txt")); // hermano, no hijo
        Assert.True(scope.IsPathAllowed(@"C:\work\public\ok.txt"));
    }

    [Fact]
    public void PermissionService_DeniesReadOutsideScope()
    {
        var service = new ToolPermissionService();
        var scope = Scope(@"C:\work");
        var readDef = new ToolDefinition("ReadFile", "ReadFile", "Reads.", ToolKind.FileSystem,
            ToolPermission.Read, Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(30));

        var outside = new ToolInvocation(Guid.NewGuid(), "ReadFile",
            """{"path":"C:\\Windows\\secret.txt"}""", Ctx());
        Assert.False(service.Authorize(outside, readDef, scope).Allowed);

        // …pero la lectura dentro del scope no pide confirmación.
        var inside = outside with { ArgumentsJson = """{"path":"C:\\work\\code.cs"}""" };
        var ok = service.Authorize(inside, readDef, scope);
        Assert.True(ok.Allowed);
        Assert.False(ok.RequiresUserConfirmation);
    }

    [Fact]
    public void PermissionService_DeniesExecuteWithAbsoluteArgOutsideScope()
    {
        var service = new ToolPermissionService();
        var scope = Scope(@"C:\work");
        var execDef = new ToolDefinition("ExecuteCommand", "ExecuteCommand", "Runs.", ToolKind.Command,
            ToolPermission.Execute, Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(30));

        var evil = new ToolInvocation(Guid.NewGuid(), "ExecuteCommand",
            """{"command":"git","args":["C:\\Windows\\secret.txt"],"workdir":"C:\\work"}""", Ctx());
        Assert.False(service.Authorize(evil, execDef, scope).Allowed);

        var benign = evil with
        {
            ArgumentsJson = """{"command":"git","args":["status"],"workdir":"C:\\work"}""",
        };
        Assert.True(service.Authorize(benign, execDef, scope).Allowed);
    }

    [Fact]
    public void GitRefValidation_RejectsOptionInjection()
    {
        Assert.Throws<ArgumentException>(() => ProcessGitService.ValidateRefName("--help"));
        Assert.Throws<ArgumentException>(() => ProcessGitService.ValidateRefName("-D"));
        Assert.Throws<ArgumentException>(() => ProcessGitService.ValidateRefName("a..b"));
        Assert.Throws<ArgumentException>(() => ProcessGitService.ValidateRefName("bad name!"));
        Assert.Throws<ArgumentException>(() => ProcessGitService.ValidateRefName(""));

        // Nombres legítimos pasan.
        ProcessGitService.ValidateRefName("feature/login-2");
        ProcessGitService.ValidateRefName("v1.2+rc");
    }

    [Fact]
    public void Backoff_ClampsRetryAfter()
    {
        Assert.Equal(TimeSpan.Zero, ResilientAIProvider.Backoff(1, TimeSpan.FromSeconds(-5)));
        Assert.Equal(TimeSpan.FromSeconds(60), ResilientAIProvider.Backoff(1, TimeSpan.FromHours(3)));
        var normal = ResilientAIProvider.Backoff(1, null);
        Assert.InRange(normal.TotalMilliseconds, 500, 30_000 + 250);
    }

    [Fact]
    public void OpenCodeBaseUrl_RejectsNonLoopbackHttp()
    {
        Assert.Throws<ArgumentException>(() => OpenCodeServerClient.ValidateBaseUrl("http://192.168.1.10:3000"));
        Assert.Throws<ArgumentException>(() => OpenCodeServerClient.ValidateBaseUrl("ftp://127.0.0.1/x"));
        Assert.Throws<ArgumentException>(() => OpenCodeServerClient.ValidateBaseUrl("not-a-url"));

        OpenCodeServerClient.ValidateBaseUrl("http://127.0.0.1:4096");
        OpenCodeServerClient.ValidateBaseUrl("http://localhost:4096");
        OpenCodeServerClient.ValidateBaseUrl("https://example.com:443");
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData("prod.env", true)]
    [InlineData("secrets.dat", true)]
    [InlineData("ca-a-ia.db", true)]
    [InlineData("backup.DB", true)]
    [InlineData("deploy.pem", true)]
    [InlineData("id_rsa.key", true)]
    [InlineData("appsettings.json", false)]
    [InlineData("program.cs", false)]
    public void WorkspaceReader_SkipsSecretFiles(string fileName, bool expected)
    {
        Assert.Equal(expected, WorkspaceReader.IsSecretFile(fileName));
    }

    [Fact]
    public void WorkspaceReader_DiscoversTxtFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ca-a-ia-discover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "perro.txt"), "guau");
            File.WriteAllText(Path.Combine(dir, "notas.md"), "# hola");
            var reader = new WorkspaceReader();
            var found = reader.DiscoverRelevantFiles(dir);
            Assert.Contains(found, f => f.EndsWith("perro.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(found, f => f.EndsWith("notas.md", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
