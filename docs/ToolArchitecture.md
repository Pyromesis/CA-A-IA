# ToolArchitecture — CA-A-IA

## 1. Contrato (`Domain/Tools/ITool.cs`)

`ITool` = `Definition` (id, nombre, descripción para el modelo, `ToolKind`, permisos requeridos,
parámetros, timeout) + `ExecuteAsync(ToolInvocation, CancellationToken)` → `ToolResult`
(éxito / denegación de permiso / fallo con `FailureCategory` + duración). Cada invocación lleva
`InvocationId` + `CorrelationContext`: toda ejecución es auditable de extremo a extremo.

## 2. Catálogo (`Infrastructure/Tools/ToolRegistry.cs`)

Registro thread-safe con `ListDefinitionsFor(granted)`: el modelo solo VE las herramientas que el
scope permite (reducción de superficie de ataque por construcción, no solo por validación).

Implementadas y testeadas: `ReadFile` y `ListDirectory` (solo lectura), más **`WriteFile`**
(exige directorio padre existente, tope 500k chars), **`EditFile`** (reemplazo exacto de 1
ocurrencia; 0 o 2+ → fallo sin tocar nada) y **`ExecuteCommand`** (`command` + `args` +
`workdir` obligatorio dentro del scope, sin shell, timeout 5–1800 s, salida truncada a 20k,
exit≠0 → `ToolFailure` con la salida como evidencia).

## 3. Permisos (`Infrastructure/Security/ToolPermissionService.cs`)

Orden de evaluación: 1) permisos de la herramienta ⊆ permisos del scope; 2) rutas de
escritura/ejecución (incluido `workdir`) dentro de `AllowedPaths` y fuera de `DeniedPaths`;
3) confirmación de usuario si el scope la exige. Testeado: escritura fuera del workspace
denegada, lectura permitida, confirmación señalada, `workdir` fuera de scope denegado.

## 4. Ejecución segura de procesos (`Infrastructure/Process/`)

Salida asíncrona (sin deadlocks de buffer), timeout con muerte del árbol
(`Kill(entireProcessTree)`), cancelación cooperativa. `ProcessGitService`
(status/diff/log/commit) ya lo usa. `ICommandRunner` (Domain, `Process/`) es el puente que
consumen las herramientas y el Agent sin conocer procesos (`CommandRunnerAdapter`).

## 5. Bucle agéntico (`Agent/Execution/LlmTaskExecutor.cs`)

Por tarea: contexto del proyecto → provider con tools visibles según scope → autorizar cada
llamada (`IToolPermissionService`) → confirmación humana si el scope la exige
(`IUserConfirmation`: diálogo WinUI en la app, denegación en headless) → ejecutar → devolver
resultados al modelo → hasta `MaxToolIterations` o resumen final. Clasifica fallos
(red/proveedor/entorno) para el repair loop del motor. Testeado con proveedor guionizado:
lectura→resumen, escritura con confirmación allow/deny, sin modelo → `EnvironmentFailure`.
