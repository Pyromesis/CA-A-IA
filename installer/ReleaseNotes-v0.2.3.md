# CA-A-IA v0.2.3

Tres agentes en Autonomía: Actor rápido, Vigilante y Analista.

## Nuevo

- **Vigilante automático**: tras cada acción de ratón/teclado/navegador, captura
  sola la pantalla (~200 ms, sin gastar turnos ni pedir confirmación) y la
  adjunta al siguiente mensaje.
- **Analista en el mismo turno**: el modelo VE la captura y verifica antes de
  seguir (describe qué ve y si funcionó lo anterior). Sin llamadas extra.
- Usa un **modelo rápido y gratuito** en Autonomía para volar; el bucle es
  ver-actuar-verificar en cada paso.

## Instalación

- **CA-A-IA-Setup-0.2.3.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
