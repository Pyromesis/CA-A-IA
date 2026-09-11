# CA-A-IA v0.1.7

Autonomía fiable: ojos, acciones deterministas y atajos globales.

## Nuevo

- **Ojos para el agente**: `UiActiveWindow` (proceso + título al frente) y
  `UiWaitWindow` (esperar a que aparezca algo en vez de adivinar tiempos).
- **Acciones fiables**: `UiOpenApp` (abre o enfoca si ya está abierto, nunca
  duplica) y `UiOpenUrl` (abre páginas directamente en el navegador).
- La IA ahora **verifica cada paso** y corrige si abrió lo incorrecto.
- **Atajos globales** (funcionan en cualquier app): **Ctrl+J** pausa al agente,
  **Ctrl+K** lo reanuda.
- Ritmo humano algo más ágil (teclear y mover siguen viéndose naturales).

## Instalación

- **CA-A-IA-Setup-0.1.7.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
