// CA-A-IA — Tests de hotkeys globales (registro/liberación sin romper).

using CaAIA.Infrastructure.Process;

namespace CaAIA.Tests.Unit;

public sealed class GlobalHotkeysTests
{
    [Fact]
    public void StartStop_IsSafe()
    {
        // Humo: registrar/liberar no debe lanzar (el resultado depende de si el
        // SO concede el combo en esta máquina; ambas ramas son válidas).
        using var hotkeys = new GlobalHotkeys();
        try
        {
            hotkeys.Start(
                () => Task.CompletedTask,
                () => Task.CompletedTask);
        }
        finally
        {
            // Doble stop + dispose: siempre seguro (haya o no registrado).
            hotkeys.Stop();
            hotkeys.Stop();
        }
    }
}
