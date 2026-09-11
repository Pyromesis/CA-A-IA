# AgentArchitecture — CA-A-IA Fase 0

## 1. Principio: planificar antes de actuar

El flujo `Understand → Analyze → Clarify → Plan → Review → Approve → Execute → Test →
Repair → Verify → Advance → FinalAudit → Done` NO es una secuencia de `if/else`: es la tabla
declarativa `AgentTransitions` + el bucle de `AgentExecutionEngine`. Añadir un estado nuevo es
añadir una fila, no reescribir el motor.

## 2. Máquina de estados (`Agent/StateMachine/`)

- 19 estados (`Domain/Enums/AgentState.cs`): los 18 del prompt + `Unknown` (valor 0, nunca válido
  como destino; evita el bug del enum por defecto).
- `AgentStateMachine`: thread-safe (`lock`), historial de `StateTransition`, evento `Transitioned`
  (UI + logs), hidratação `Restore()` desde checkpoints.
- Regla global: desde cualquier estado NO terminal → `Paused`/`Cancelled` (pausa cooperativa).
- Terminales: `Completed`, `Cancelled`, `Failed`. Salidas: `Completed→Idle`, `Failed→Recovering/Idle`,
  `Paused→Recovering/Cancelled/Idle`.
- Ramas de fallo: `Executing→AnalyzingFailure→Repairing→Retesting→VerifyingTask`, o
  `AnalyzingFailure→AdvancingTask` cuando la política dice que no es reparable (escalado).

## 3. Motor (`Agent/Execution/AgentExecutionEngine.cs`)

Un motor por sesión (vía `AgentEngineFactory`). Responsabilidades implementadas en Fase 0:

- Bucle principal sobre tareas `Pending` con dependencias completadas (`AgentTask.IsReady`).
- **Las tareas `Failed` no se re-eligen solas** (evita reintentos infinitos); solo el repair loop
  puede reiniciarlas dentro del mismo intento lógico.
- Timeout por operación + timeout global + `CancellationToken` enlazado (pausa/cancelación/global).
- Checkpoint en CADA transición (`ICheckpointStore`) + `Heartbeat` periódico de sesión.
- `Testing→VerifyingTask→AdvancingTask` por tarea; `FinalVerification` con auditor
  (`LlmPlanVerifier`: juicio semántico del modelo + suelo mecánico de `FinalPlanAuditor`,
  veredicto = unión de gaps) y posible ampliación del plan (`Plan.ExtendAfterAudit`, rev+1).
- Un fallo de checkpoint se registra y NO tumba la ejecución.

Ejecución real por tarea: `ITaskExecutor` = `LlmTaskExecutor`
(ver `docs/ToolArchitecture.md` §5). El motor además se testea con ejecutores guionizados.

## 4. Planificación

`IPlanner` = `LlmPlanner` (pide al modelo descomposición JSON con `response_format`,
valida tareas/preguntas/límites, tolera cercas markdown) con fallback a `HeuristicPlanner`
(estructura + preguntas, cero tareas inventadas) ante cualquier fallo. El invariante
`sin tareas → no aprobable` sigue siendo la compuerta. Testeado: parseo, cercas y doble fallback.

## 5. Larga duración (§7)

`AgentSession` + `Checkpoint` + log de ejecución append-only permiten: cerrar la app, reiniciar
Windows y reanudar (`ResumeSession` → `Recovering` → `PreparingExecution`). Test de integración
`Stores_SurviveContainerRebuild` lo demuestra con dos contenedores sobre el mismo `DataPath`.
Watchdog: timer que vigila `_lastProgressUtc`; sin transiciones durante `StallDetectionSeconds`
→ evento + `Failed` + cancelación cooperativa con checkpoint conservado (`IsStalledAt` testeado).

## 6. Memoria (`Agent/Memory/AgentMemory.cs` + `IMemoryStore`)

Scopes `Session/Task/Project/Decision/Error/Verification` con TTL por entrada
(errores 7 días, notas de tarea 24 h, decisiones/verificación sin expiración). La recuperación es
explícita y acotada para no contaminar el contexto.
