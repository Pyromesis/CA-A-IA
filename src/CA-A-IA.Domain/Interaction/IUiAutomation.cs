// CA-A-IA — Automatización de la UI como un humano (ratón + teclado reales).
// El agente la usa desde la pestaña Autonomía; cada herramienta se autoriza
// contra el scope (Nivel 1 confirma cada acción, Nivel 3 no pide).

namespace CaAIA.Domain.Interaction;

/// <summary>Tamaño del monitor principal (el modelo necesita cotas para coordenadas).</summary>
public sealed record ScreenSize(int Width, int Height);

/// <summary>Ventana en primer plano: proceso + título (para verificar qué se abrió).</summary>
public sealed record ActiveWindow(string ProcessName, string Title);

/// <summary>
/// Ratón y teclado reales del PC. Implementación en Infrastructure (SendInput).
/// Todo es cancelable y acotado; las coordenadas se recortan a la pantalla.
/// </summary>
public interface IUiAutomation
{
    ScreenSize GetScreenSize();
    (int X, int Y) GetMousePosition();

    /// <summary>Velocidad del puntero de Windows 1-20 (informativo: el movimiento
    /// absoluto no la usa, pero sirve para diagnosticar).</summary>
    int GetMouseSpeed();
    Task MoveMouseAsync(int x, int y, bool humanize, CancellationToken cancellationToken);
    Task ClickAsync(string button, bool doubleClick, CancellationToken cancellationToken);
    Task ScrollAsync(int deltaX, int deltaY, CancellationToken cancellationToken);
    Task TypeTextAsync(string text, CancellationToken cancellationToken);
    Task PressKeyAsync(string key, IReadOnlyList<string> modifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Abre una app por nombre (brave, notepad…): si ya corre, la trae al frente
    /// en vez de duplicarla. Devuelve proceso + título para verificar.
    /// </summary>
    Task<ActiveWindow> OpenAppAsync(string name, CancellationToken cancellationToken);

    /// <summary>Abre una URL (navegador por defecto o el indicado). Valida http(s).</summary>
    Task OpenUrlAsync(string url, string? browser, CancellationToken cancellationToken);

    /// <summary>Ventana en primer plano ahora mismo (ojos del agente).</summary>
    ActiveWindow GetActiveWindow();
    /// <summary>
    /// Captura el monitor principal (reducida, PNG) en la carpeta indicada.
    /// Devuelve la ruta. Rápida (~100-300 ms) para ver casi en tiempo real.
    /// </summary>
    Task<string> CaptureScreenshotAsync(string directory, CancellationToken cancellationToken);

    /// <summary>Espera hasta que el primer plano contenga el texto (proceso o
    /// título) o se acabe el tiempo. Para sincronizar en vez de adivinar.</summary>
    Task<bool> WaitForActiveWindowAsync(string text, int timeoutSeconds, CancellationToken cancellationToken);
}
