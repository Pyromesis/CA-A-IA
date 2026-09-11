# CA-A-IA — Fase 0: Fundación, arquitectura y diseño técnico

Plataforma nativa de Windows para **programación asistida por múltiples proveedores de IA**,
con agentes autónomos de larga duración, planificación previa, verificación y autocorrección.

> Estado: **núcleo funcional end-to-end**. Desde el Chat: sesión → plan (modelo o
> heurístico) → preguntas respondibles → aprobar y ejecutar con el motor (estados,
> reintentos, auditoría final) narrado en vivo. Workspace, proveedor y modelo compartidos
> entre pestañas y persistidos (SQLite). Sin API key, el flujo llega hasta el plan con
> guía honesta de configuración (`OPENCODE_ZEN_API_KEY` u `OPENROUTER_API_KEY`).
> Ver `TODO(FUTURE_PHASE)` para lo explícitamente pendiente (MSIX sin tooling en este entorno).

## Stack

- .NET 10 · C# (latest) · WinUI 3 · **Windows App SDK 2.4.0** · XAML · MVVM (`CommunityToolkit.Mvvm`)
- DI (`Microsoft.Extensions.DependencyInjection` + Hosting), `async/await` + `CancellationToken` en todo
- Tests: xUnit (`tests/`: Unit / Integration / Architecture)

## Estructura

```text
CA-A-IA.slnx
Directory.Build.props / Directory.Packages.props / .editorconfig / global.json
docs/            Architecture, Agent, Provider, Tool, Security, Persistence, Testing
src/
  CA-A-IA.Domain/          Entidades, ValueObjects, Enums, Interfaces (sin dependencias)
  CA-A-IA.Application/     DTOs, Commands/Queries, UseCases, Orchestration, Services, DI
  CA-A-IA.Infrastructure/  Providers (OpenCode/OpenRouter/Local), Tools, FS, Git, Persistencia, Logging, Config, EventBus
  CA-A-IA.Agent/           StateMachine, ExecutionEngine, Planner, Verifier, Repair, Context, Memory, Policies
  CA-A-IA.Presentation/    WinUI 3: Shell, Views, ViewModels, Navigation, CompositionRoot
tests/
  CA-A-IA.Tests.Unit / CA-A-IA.Tests.Integration / CA-A-IA.Tests.Architecture
```

Dirección de dependencias permitida (verificada por tests de arquitectura):

```text
Presentation → Application, Agent, Infrastructure, Domain   (Composition Root)
Infrastructure → Application, Domain
Agent → Application, Domain        (NUNCA Infrastructure ni Presentation)
Application → Domain
Domain → (nada; solo BCL)
```

## Compilar y probar

```powershell
dotnet build CA-A-IA.slnx
dotnet test CA-A-IA.slnx
```

La app WinUI (`CA-A-IA.Presentation`) se ejecuta en Windows con build unpackaged/MSIX según perfil.
Fase 0 incluye el shell navegable (Sidebar + paneles) conectado a ViewModels con datos de diseño;
el motor del agente está presente como máquina de estados persistible, sin ejecución real
(`TODO(FUTURE_PHASE)`).

## Flujo del agente (diseño Fase 0)

`Understand → Analyze → Clarify → Plan → Review → Approve → Execute → Test → Repair →
Verify → Advance → FinalAudit → Done`, implementado como máquina de estados explícita y
persistible (`AgentState`, `AgentStateMachine`), NO como `if/else` en una clase gigante.

## Documentación

Ver `docs/`: `Architecture.md`, `AgentArchitecture.md`, `ProviderArchitecture.md`,
`ToolArchitecture.md`, `SecurityArchitecture.md`, `PersistenceArchitecture.md`, `TestingStrategy.md`.
