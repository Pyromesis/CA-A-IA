// CA-A-IA — Automatización de la UI como un humano (ratón + teclado reales).
// El agente la usa desde la pestaña Autonomía; cada herramienta se autoriza
// contra el scope (Nivel 1 confirma cada acción, Nivel 3 no pide).

namespace CaAIA.Domain.Interaction;

/// <summary>Tamaño del monitor principal (el modelo necesita cotas para coordenadas).</summary>
public sealed record ScreenSize(int Width, int Height);

/// <summary>
/// Ratón y teclado reales del PC. Implementación en Infrastructure (SendInput).
/// Todo es cancelable y acotado; las coordenadas se recortan a la pantalla.
/// </summary>
public interface IUiAutomation
{
    ScreenSize GetScreenSize();
    (int X, int Y) GetMousePosition();
    Task MoveMouseAsync(int x, int y, bool humanize, CancellationToken cancellationToken);
    Task ClickAsync(string button, bool doubleClick, CancellationToken cancellationToken);
    Task ScrollAsync(int deltaX, int deltaY, CancellationToken cancellationToken);
    Task TypeTextAsync(string text, CancellationToken cancellationToken);
    Task PressKeyAsync(string key, IReadOnlyList<string> modifiers, CancellationToken cancellationToken);
}
