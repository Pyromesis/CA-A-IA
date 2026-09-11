# CA-A-IA v0.1.5

Multi-agente: varios agentes trabajan en paralelo.

## Nuevo

- **Tareas independientes en paralelo** (hasta 3 agentes a la vez, configurable
  con `MaxParallelAgents` en `appsettings.json`): lo que antes tardaba la suma
  ahora tarda el máximo. Las dependencias se siguen respetando (una tarea
  espera a sus previas) y cada fallo se repara en su propio carril.
- La pestaña **Tareas** muestra el avance simultáneo en vivo.

## Instalación

- **CA-A-IA-Setup-0.1.5.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
