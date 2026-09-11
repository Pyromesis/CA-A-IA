# TestingStrategy — CA-A-IA Fase 0

Tres proyectos xUnit, tres propósitos distintos:

## 1. `CA-A-IA.Tests.Unit` (rápidos, sin E/S real salvo temp)

- `StateMachineTests`: lifecycle completo, rama de reparación, transiciones ilegales (no mutan),
  regla global pausa/cancel, `Restore`, eventos.
- `PlanDomainTests`: invariantes (revisión → aprobación con preguntas respondidas y tareas;
  auditoría solo amplía en `Auditing`; dependencias; `MaxAttempts`).
- `PolicyTests`: matriz de `DefaultRepairPolicy`, denegación fuera de scope, registro de
  proveedores, reintentos del decorador resiliente con dobles.
- `ToolsAndAuditTests`: `ReadFile` real sobre temp, `ToolRegistry`, auditor final (gap detectado
  y caso satisfecho), memoria con expiración.
- `ProviderClientTests`: cliente OpenAI-compatible con handler simulado (parsing, esquema de
  tools, mapeo de errores 401/403/404/429/5xx + `Retry-After`, modelos con `LongContext`, SSE
  con tool calls fragmentados).
- `WriteToolsTests`: roundtrip Write→Read, Edit (1 ocurrencia ok / 0 y 2+ denegadas sin tocar),
  Execute (salida + exit code), `workdir` fuera de scope denegado.
- `LlmPlannerTests`: parseo del JSON del modelo, tolerancia a cercas markdown, doble fallback
  heurístico (fallo de proveedor y cero tareas).
- `LlmVerifierTests`: unión conservadora mecánico+semántico, fallback ante fallo y sin modelo.
- `SummarizerTests`: passthrough bajo presupuesto, resumen sobre presupuesto, truncado si falla.
- `ModelSelectionTests`: defaults desde config, Set + evento Changed.
- `OpenCodeTests`: parseo defensivo (answers, providers), split de modelo, flujo completo con
  HTTP simulado, salud sin binario, lifecycle (FindBinary/FreePort).

## 2. `CA-A-IA.Tests.Integration` (DI real + disco temporal)

- `CompositionTests`: el contenedor completo resuelve (coordinador, registro con
  opencode+openrouter+local, 5 tools), OpenCode stub honesto (`CheckHealth=false`, catálogo
  vacío), ciclo pausa/reanudación/cancelación, **supervivencia entre contenedores** (mismo `DataPath`).
- `EngineTests` (ejecutor guionizado + `InstantRepairPolicy` sin delays): happy path hasta
  `Completed` con checkpoint final, reparación transient, **escalado sin reintentos** ante
  `PermissionFailure`, **ampliación por auditoría real** (rev 1→2 → `Completed`), cancelación
  durante ejecución con parada graceful, **detección de stall** (`IsStalledAt`).
- `LlmExecutorTests` (proveedor guionizado + herramientas reales): lectura→resumen con el
  resultado alimentando la 2ª iteración, escritura con confirmación allow (escribe) y deny
  (bloquea, el modelo se adapta), sin modelo → `EnvironmentFailure`.
- `GitServiceTests` (repo temporal real): status/diff/log/commit + mensaje vacío rechazado,
  ramas (crear/listar/checkout) + checkout con working sucio denegado.
- `CoordinatorPlanTests`: flujo sesión→plan con fallback heurístico, respuesta que desbloquea
  y plan vacío no aprobable.
- `SecretsTests`: roundtrip real en Credential Manager (clave única + limpieza) y compuesto
  con fallback (lee secundario, consolida al escribir).
- `LocalDetectTests`: primer candidato vivo, todos caídos → null, cancelación que propaga,
  sin servidor → vacío rápido.
- `SqliteTests`: eventos (persist + replay ordenado), bus con `FlushAsync`, memoria persistente
  con TTL, migración legacy (importa una vez, segunda = 0).

## 3. `CA-A-IA.Tests.Architecture` (guardián de la arquitectura)

Reflexión sobre metadatos (Presentation se carga sin instanciar UI): dirección de dependencias por
assembly, implementaciones de `IAIProvider` solo en Infrastructure, `IAgentStateMachine` solo en
Agent, Presentation referencia WinUI (`Microsoft.WinUI`), **ViewModels sin tipos de
Infrastructure** (inspección de miembros), sin god-classes (>25 métodos públicos).

## Reglas

- Ningún test depende de red, credenciales ni del `DataPath` real del usuario (todo temporal).
- Tiempos acotados: sin `Task.Delay` reales en el camino feliz (reintentos con `RetryAfter=Zero`,
  `InstantRepairPolicy`); el único delay es la ventana de cancelación (500 ms).
- Cobertura objetivo Fase 1+: bucles de packages + mutation ligera en `AgentTransitions`.
