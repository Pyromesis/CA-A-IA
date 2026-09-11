# PersistenceArchitecture — CA-A-IA

## Decisión: SQLite (WAL) como store primario, con migración única desde JSON

`IPlanStore`, `IAgentSessionStore`, `ICheckpointStore`, `IMemoryStore` e `IEventStore` (Domain)
se implementan sobre **un único fichero** `{DataPath}/ca-a-ia.db` (`SQLiteConnectionFactory`):

- WAL + `foreign_keys` + `busy_timeout=5000`; conexiones baratas por operación, escrituras
  serializadas con `SemaphoreSlim` (nada de `SQLITE_BUSY` entre hilos).
- Documentos JSON en columna `data` + columnas de consulta (`session_id`, `is_active`,
  `scope`, `type`, timestamps ISO-8601): el modelo de dominio evoluciona sin migraciones
  de esquema; los índices cubren los accesos (por sesión, activos, expiración).
- Tablas: `sessions`, `plans`, `checkpoints`, `execution_logs`, `memories`, `events`,
  `settings`, `chat_messages` (historial del chat: `session_id`, `role`, `text`, `created_utc`;
  el Chat restaura los últimos 200 al arrancar y conserva 1000).

## Propiedades garantizadas (testeadas)

- Transaccional por escritura (`INSERT … ON CONFLICT`); crash → último commit intacto.
- Recuperación entre reinicios: `Stores_SurviveContainerRebuild` (sesión + log sobreviven a un
  contenedor nuevo con el mismo `DataPath`), ahora sobre SQLite sin cambiar un solo aserto.
- Checkpoints en cada transición → reanudación (`ResumeSession` → último checkpoint).
- Memoria persistente con TTL (`SqliteMemoryStore`: sobrevive a reinicios, expiración y
  `PruneExpired` testeados).
- Documentos corruptos: `Deserialize` devuelve `null` y se saltan (nunca tumban lecturas).

## Migración legacy (`Persistence/Migration/LegacyJsonImporter.cs`)

Si la BD está vacía y existe layout JSON de la Fase 0 (`sessions/`, `plans/`, `checkpoints/`,
`logs/*.jsonl`), se importa una vez al arrancar (`IStartupTask`) y los originales se
**preservan**. Idempotente (segunda ejecución = 0). Test `LegacyMigration_ImportsOnce_ThenNoops`.

## Eventos

- `IEventBus` en caliente (`InMemoryEventBus`, pub/sub thread-safe, handlers aislados).
- `RecordingEventBus` lo decora persistiendo en `IEventStore` lo que lleve sesión
  (fire-and-forget aislado + `FlushAsync` para tests/apagado).
- `IEventStore.ReadAsync(sessionId, max)` devuelve historial cronológico (replay para UI
  y auditoría). Test `Events_PersistAndReplay_InOrder`.
