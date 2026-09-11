// CA-A-IA — Tests del mapeo evento→frase de actividad (feed en vivo del Chat).

using CaAIA.Application.Services;
using CaAIA.Domain.Git;

namespace CaAIA.Tests.Unit;

public sealed class AgentActivityTests
{
    [Theory]
    [InlineData("ReadFile", "{\"path\":\"C:\\\\w\\\\Program.cs\"}", "Leyendo Program.cs…")]
    [InlineData("ListDirectory", "{\"path\":\"C:\\\\w\"}", "Explorando w…")]
    [InlineData("SearchText", "{\"pattern\":\"TODO\"}", "Buscando 'TODO'…")]
    [InlineData("SearchFiles", "{\"name\":\"Program\"}", "Buscando archivos 'Program'…")]
    [InlineData("WriteFile", "{\"path\":\"C:\\\\w\\\\a.txt\"}", "Escribiendo a.txt…")]
    [InlineData("EditFile", "{\"path\":\"C:\\\\w\\\\a.txt\"}", "Editando a.txt…")]
    [InlineData("ExecuteCommand", "{\"command\":\"dotnet\",\"args\":[\"build\"]}", "Compilando…")]
    [InlineData("ExecuteCommand", "{\"command\":\"dotnet\",\"args\":[\"test\"]}", "Pasando pruebas…")]
    [InlineData("ExecuteCommand", "{\"command\":\"git\",\"args\":[\"status\"]}", "Consultando git…")]
    [InlineData("ExecuteCommand", "{\"command\":\"dir\"}", "Ejecutando dir…")]
    [InlineData("OpenCode", "{}", "Trabajando en OpenCode…")]
    [InlineData("Whatever", "{}", "Ejecutando Whatever…")]
    public void ForToolStarted_MapsKnownTools(string toolId, string args, string expected) =>
        Assert.Equal(expected, AgentActivityText.ForToolStarted(toolId, args));

    [Fact]
    public void ForToolStarted_BadJson_DoesNotThrow() =>
        Assert.NotNull(AgentActivityText.ForToolStarted("ReadFile", "not-json{{"));

    [Theory]
    [InlineData("WriteFile", "{\"path\":\"C:\\\\w\\\\a.txt\"}", true, null, "✎ Escribió C:\\w\\a.txt")]
    [InlineData("EditFile", "{\"path\":\"C:\\\\w\\\\a.txt\"}", true, null, "✎ Editó C:\\w\\a.txt")]
    [InlineData("ExecuteCommand", "{\"command\":\"dotnet\",\"args\":[\"test\"]}", true, null, "▶ Ejecutó dotnet test")]
    [InlineData("WriteFile", "{\"path\":\"C:\\\\w\\\\a.txt\"}", false, "denied", "⚠ No se pudo escribir C:\\w\\a.txt: denied")]
    [InlineData("EditFile", "{}", false, "x", "⚠ No se pudo editar : x")]
    [InlineData("ReadFile", "{\"path\":\"C:\\\\w\\\\a.txt\"}", true, null, null)]
    [InlineData("SearchText", "{}", true, null, null)]
    [InlineData("Whatever", "{}", true, null, null)]
    [InlineData("Whatever", "{}", false, "x", null)]
    public void ForToolCompleted_NarratesMutations(
        string toolId, string args, bool ok, string? error, string? expected) =>
        Assert.Equal(expected, AgentActivityText.ForToolCompleted(toolId, args, ok, error));

    [Theory]
    [InlineData("Executing", "Trabajando…")]
    [InlineData("Testing", "Probando…")]
    [InlineData("Retesting", "Probando…")]
    [InlineData("VerifyingTask", "Verificando…")]
    [InlineData("AnalyzingFailure", "Analizando el fallo…")]
    [InlineData("Repairing", "Solucionando…")]
    [InlineData("FinalVerification", "Auditoría final…")]
    [InlineData("Completed", null)]
    [InlineData(null, null)]
    [InlineData("Idle", null)]
    public void ForState_MapsEngineStates(string? state, string? expected) =>
        Assert.Equal(expected, AgentActivityText.ForState(state));

    [Fact]
    public void ForFileChanges_Empty_ReturnsNull() =>
        Assert.Null(AgentActivityText.ForFileChanges(Array.Empty<GitChange>()));

    [Fact]
    public void ForFileChanges_ListsFilesWithStatus()
    {
        var text = AgentActivityText.ForFileChanges(new[]
        {
            new GitChange(@"C:\w\Program.cs", "M"),
            new GitChange(@"C:\w\new.txt", "??"),
        });
        Assert.NotNull(text);
        Assert.Contains("2", text);
        Assert.Contains("Program.cs", text);
        Assert.Contains("new.txt", text);
    }

    [Fact]
    public void ForFileChanges_TruncatesLongLists()
    {
        var changes = Enumerable.Range(0, 20)
            .Select(i => new GitChange($"C:\\w\\f{i}.txt", "M"))
            .ToList();
        var text = AgentActivityText.ForFileChanges(changes);
        Assert.NotNull(text);
        Assert.Contains("+", text);
        Assert.DoesNotContain("f19.txt", text);
    }
}
