# SecurityArchitecture — CA-A-IA Fase 0

Postura: **el agente es NO confiable** (puede cometer errores). La seguridad es validación en
profundidad, no confianza.

## 1. Secretos

- API keys y tokens SOLO en `ISecretStore` → `FallbackSecretStore` con primario
  **Credential Manager** (`CA-A-IA/<key>`, P/Invoke a `advapi32`, persistencia local-machine)
  y secundario **DPAPI** (fichero de la Fase 0, para no perder secretos existentes; se
  consolida en la bóveda al reescribir). Nunca en `appsettings.json`, variables de
  entorno persistentes, logs o telemetría. `OpenRouterOptions` guarda el NOMBRE del secreto,
  no el valor. Testeado: roundtrip real en la bóveda (clave única + limpieza) y consolidación.
- User-secrets de .NET solo para desarrollo local (fuera del repo).
- La pestaña **Ajustes** permite pegar las keys (Zen/OpenRouter) y probarlas con un modelo
  gratis; el Chat guía allí cuando falta autenticación. Alternativa sin UI: variables de
  entorno `OPENCODE_ZEN_API_KEY` / `OPENROUTER_API_KEY`.

## 2. Scopes de ejecución (`Domain/Security/Security.cs`)

`ExecutionScope(AllowedPaths, DeniedPaths, GrantedPermissions, RequireConfirmationForWrite,
RequireConfirmationForExecute)` + `ReadOnlyWorkspace()` por defecto. `IsPathAllowed`: denegados
vetan primero, permitidos acotan después.

### 2.1 Niveles de autorización (Ajustes, persistidos en `auth.level`)
`ExecutionScope.FromAuthorizationLevel` mapea el nivel elegido a permisos/confirmación
(las rutas se respetan en los 3): **Nivel 1** `ConfirmChanges` = escritura y ejecución
siempre con confirmación; **Nivel 2** `EditWithoutPcControl` = escribe sin pedir pero sin
`Execute` (ni se ofrece `ExecuteCommand` al modelo: no puede controlar el PC);
**Nivel 3** `FullControl` = escribe y ejecuta sin pedir. Default: Nivel 1.

## 3. Puertas por operación

| Operación | Puerta Fase 0 | Puerta futura |
|---|---|---|
| Leer fichero | scope + timeout | presupuestos por token |
| Escribir/ejecutar | `ToolPermissionService` + `IUserConfirmation` (`RequireConfirmationForWrite/Execute`) | diálogo WinUI (`DialogConfirmation`) en la app; denegación automática (`DenyAllConfirmation`) en headless/tests |
| Instalar paquetes | denegado por defecto (`AllowPackageInstall=false`) | allowlist + confirmación |
| Red | permiso `Network` por herramienta | egress allowlist por proveedor |
| Git destructivo | no implementado | confirmación + stash previo |

## 4. Clasificación de fallos como medida de seguridad

`DefaultRepairPolicy` NO reintenta `Permission/Environment/Dependency/Unknown`: ante un veto del
sistema el agente escala en lugar de insistir (evita bucles que fuercen accesos o destrocen
código por un problema externo). Test `Run_NonRepairableFailure_EscalatesPlanToFailed`.

## 5. Pendiente explícito (no deuda oculta)

Credential Manager (secretos compartibles), sandboxing de comandos (lista de binarios + cwd
fijo + sin elevación), firma de checkpoints, auditoría de `PermissionDecision` en el log
append-only. Todo marcado `TODO(FUTURE_PHASE)` en el código correspondiente.
