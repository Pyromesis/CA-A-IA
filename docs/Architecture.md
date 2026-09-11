# Architecture — CA-A-IA Fase 0

Decisiones reales tomadas en esta fase, no plantillas.

## 1. Capas y dirección de dependencias

```text
Presentation → Application, Agent, Infrastructure, Domain   (solo Composition Root)
Infrastructure → Application, Domain
Agent → Application, Domain          (NUNCA Infrastructure / Presentation)
Application → Domain
Domain → BCL únicamente
```

Verificado automáticamente por `CA-A-IA.Tests.Architecture` (referencias de assembly por
reflexión + ubicación de implementaciones + ausencia de god-classes + ViewModels sin
referencias a Infrastructure). Si una fase futura invierte una flecha, el build de tests falla.

**Por qué Agent no referencia Infrastructure:** el motor debe funcionar con cualquier proveedor,
persistencia o herramienta que cumpla los contratos de Domain. Los tests del motor usan dobles
sin infraestructura real precisamente gracias a este corte.

**Por qué los contratos viven en Domain y no en Application:** `IAIProvider`, `ITool`,
`IAgentStateMachine`, `IEventBus`, stores y `IGitService` son el lenguaje ubicuo del sistema
(plan, tarea, checkpoint, tool call); Application añade orquestación (casos de uso, coordinador).

## 2. Desviaciones justificadas respecto al esquema del prompt

1. **`WorkspaceContextBuilder` vive en Infrastructure, no en Agent.** Lee el sistema de ficheros
   (`WorkspaceReader`); ponerlo en Agent habría obligado a Agent → Infrastructure. El contrato
   `IContextBuilder` sigue en Domain y Agent lo consume sin conocer la implementación.
2. **Sin proyecto `Shared`/kernel separado:** Domain ES el kernel. Añadir otro proyecto solo
   movería el problema de dependencias.
3. **CQRS ligero sin mediador:** comandos/queries + handlers explícitos registrados en DI.
   MediatR habría añadido una dependencia externa para un despacho que en Fase 0 son ~10 mensajes.
4. **`ResilientAIProvider` (decorador) en Infrastructure:** reintentos con backoff + timeout por
   intento aplicables a CUALQUIER proveedor sin tocar el motor.
5. **`IStartupTask`:** el auto-registro de providers/tools en sus registros necesita correr tras
   construir el contenedor; este hook evita que `App.xaml.cs` conozca adaptadores concretos.

## 3. Convenciones

- Namespaces raíz `CaAIA.*` (los nombres de assembly conservan `CA-A-IA.*` por legibilidad del
  `.slnx`; `RootNamespace` evita identificadores C# inválidos con guiones).
- `async/await` + `CancellationToken` en TODA operación de E/S o potencialmente larga.
- Escrituras de persistencia atómicas (tmp + move) y `SemaphoreSlim` por store.
- Secretos jamás en configuración: `ISecretStore` (DPAPI CurrentUser).
- `TODO(FUTURE_PHASE)` marca integraciones pendientes sin fingir que existen.
- `TreatWarningsAsErrors=false` en Fase 0 (XAML generado + WinRT emiten avisos propios);
  endurecer en Fase 1.

## 4. Mapa de archivos clave

| Decisión | Dónde |
|---|---|
| Tabla de transiciones | `Agent/StateMachine/AgentTransitions.cs` |
| Motor + checkpoints | `Agent/Execution/AgentExecutionEngine.cs` |
| Contratos IA | `Domain/AI/IAIProvider.cs` |
| Contratos tools + permisos | `Domain/Tools/ITool.cs`, `Domain/Security/Security.cs` |
| Composition Root | `Presentation/App.xaml.cs` |
| Persistencia | `Infrastructure/Persistence/File*.cs` |
| Auditoría final | `Agent/Verification/FinalPlanAuditor.cs` |
