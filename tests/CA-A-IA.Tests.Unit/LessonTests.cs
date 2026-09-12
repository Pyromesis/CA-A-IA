// CA-A-IA — Tests del aprendizaje continuo (intake, guardado, recuerdo).

using CaAIA.Application.Configuration;
using CaAIA.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Unit;

public sealed class LessonTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"ca-a-ia-lesson-{Guid.NewGuid():N}");
    private readonly string _ws = Path.Combine(Path.GetTempPath(), $"ca-a-ia-lesson-ws-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_ws, recursive: true); } catch (Exception) { }
    }

    private LessonStore Store() => new(
        Options.Create(new CaAIAOptions
        {
            Persistence = new PersistenceSettings { DataPath = _dataPath },
        }),
        NullLogger<LessonStore>.Instance);

    [Theory]
    [InlineData("recuerda: no usar paints", LessonScope.Project, "no usar paints")]
    [InlineData("RECUERDA: Xyz ab", LessonScope.Project, "Xyz ab")]
    [InlineData("recuerda siempre: pulsar Tab antes", LessonScope.Global, "pulsar Tab antes")]
    [InlineData("remember: check twice", LessonScope.Project, "check twice")]
    [InlineData("nota: abcdef", LessonScope.Project, "abcdef")]
    public void Intake_DetectsMarkers(string text, LessonScope scope, string expected)
    {
        Assert.True(LessonIntake.TryExtract(text, out var actualScope, out var lesson));
        Assert.Equal(scope, actualScope);
        Assert.Equal(expected, lesson);
    }

    [Theory]
    [InlineData("hola mundo")]
    [InlineData("recuerda:")]
    [InlineData("recuerda: ab")]
    [InlineData("")]
    public void Intake_IgnoresNonLessons(string text)
    {
        Assert.False(LessonIntake.TryExtract(text, out _, out _));
    }

    [Fact]
    public async Task Record_And_Recall_ProjectLesson()
    {
        Directory.CreateDirectory(_ws);
        var store = Store();
        await store.RecordLessonAsync(_ws, LessonScope.Project,
            "No abrir WhatsApp para buscar en YouTube: usar el navegador.", "usuario", CancellationToken.None);

        var vaultFile = Path.Combine(VaultNotes.VaultDir(_ws), "lecciones.md");
        Assert.True(File.Exists(vaultFile));

        var hits = await store.RecallRelevantAsync(_ws, "youtube navegador", 3, CancellationToken.None);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase));

        // Duplicado (igual o contenido): no se guarda dos veces.
        await store.RecordLessonAsync(_ws, LessonScope.Project,
            "no abrir whatsapp para buscar en youtube: usar el navegador", "usuario", CancellationToken.None);
        var lines = await File.ReadAllLinesAsync(vaultFile);
        Assert.Single(lines, l => l.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Record_GlobalLesson_RecalledWithoutWorkspace()
    {
        var store = Store();
        await store.RecordLessonAsync(null, LessonScope.Global,
            "Confirmar siempre antes de cerrar apps ajenas.", "usuario", CancellationToken.None);

        var hits = await store.RecallRelevantAsync(null, "cerrar apps confirmar", 3, CancellationToken.None);
        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task Record_WithoutWorkspace_ProjectScope_DoesNothing()
    {
        var store = Store();
        await store.RecordLessonAsync(null, LessonScope.Project, "algo que aprender hoy", "usuario",
            CancellationToken.None);
        Assert.False(Directory.Exists(_ws));
    }

    [Fact]
    public void ParseLessonLine_StripsBrackets()
    {
        Assert.Equal("hacer x", LessonStore.ParseLessonLine("- [2026-09-11] [usuario] hacer x"));
        Assert.Equal(string.Empty, LessonStore.ParseLessonLine("# título"));
    }
}
