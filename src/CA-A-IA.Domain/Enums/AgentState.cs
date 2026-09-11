// CA-A-IA · Fase 0 — Estados explícitos del agente (máquina de estados persistible).
// Ver docs/AgentArchitecture.md y CaAIA.Agent.StateMachine.AgentStateMachine.

namespace CaAIA.Domain.Enums;

/// <summary>
/// Estado del ciclo de vida del agente. Toda transición válida está declarada en
/// <c>CaAIA.Agent.StateMachine.AgentTransitions</c>; ninguna transición fuera de la tabla es legal.
/// </summary>
public enum AgentState
{
    Unknown = 0,

    /// <summary>Sin trabajo activo. Único estado inicial tras arrancar o recuperar sin sesión.</summary>
    Idle = 1,

    /// <summary>Interpretando la solicitud del usuario (intención, alcance, restricciones).</summary>
    Understanding = 2,

    /// <summary>Inspeccionando el proyecto (archivos, build, git) para fundamentar el plan.</summary>
    InspectingProject = 3,

    /// <summary>Bloqueado esperando respuestas del usuario. Persiste hasta reanudación.</summary>
    ClarificationRequired = 4,

    /// <summary>Construyendo el <see cref="Planning.Plan"/> con tareas, dependencias y criterios.</summary>
    Planning = 5,

    /// <summary>Plan generado pendiente de revisión/aprobación (usuario o política).</summary>
    PlanReview = 6,

    /// <summary>Preparando ejecución: resolución de proveedor/modelo, herramientas, checkpoints.</summary>
    PreparingExecution = 7,

    /// <summary>Ejecutando la tarea actual del plan.</summary>
    Executing = 8,

    /// <summary>Compilando / ejecutando pruebas de la tarea actual.</summary>
    Testing = 9,

    /// <summary>Analizando un fallo para clasificarlo (<see cref="FailureCategory"/>).</summary>
    AnalyzingFailure = 10,

    /// <summary>Aplicando una reparación generada a partir del análisis.</summary>
    Repairing = 11,

    /// <summary>Re-ejecutando pruebas tras una reparación.</summary>
    Retesting = 12,

    /// <summary>Verificando la tarea actual contra sus criterios de aceptación.</summary>
    VerifyingTask = 13,

    /// <summary>Avanzando a la siguiente tarea lista del plan (resolución de dependencias).</summary>
    AdvancingTask = 14,

    /// <summary>Auditoría final: request vs plan vs implementación vs tests.</summary>
    FinalVerification = 15,

    /// <summary>Trabajo terminado y auditado.</summary>
    Completed = 16,

    /// <summary>Pausado por el usuario o por política. Reanudable desde checkpoint.</summary>
    Paused = 17,

    /// <summary>Cancelado por el usuario. Terminal; conserva historial y checkpoints.</summary>
    Cancelled = 18,

    /// <summary>Fallo no recuperable automáticamente. Terminal hasta intervención.</summary>
    Failed = 19,

    /// <summary>Recuperando sesión/estado tras reinicio, crash o error inesperado.</summary>
    Recovering = 20,
}
