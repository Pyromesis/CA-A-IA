# ProviderArchitecture — CA-A-IA Fase 0

## 1. El proveedor es una dependencia intercambiable

```text
Agent → IAIProvider → IProviderRegistry → { OpenCode | OpenRouter | Local | futuros }
```

Todo el sistema programa contra `Domain/AI/IAIProvider.cs`. Ningún `using` de SDKs de proveedor
existe fuera de `Infrastructure/Providers/`. Test de arquitectura
`ProviderImplementations_LiveOnlyInInfrastructure` lo impone.

## 2. Contrato (`IAIProvider`)

Cubre lo exigido en §10: `CompleteAsync` + `StreamAsync` (`IAsyncEnumerable<AIStreamChunk>`,
cancelable entre chunks) + `GetModelsAsync` + `CheckHealthAsync`, con `ProviderCapabilities`
(flags: Streaming, Tools, Vision, StructuredOutput, Reasoning, Embeddings, LongContext,
CodeExecution) para selección y degradación. Errores tipados (`AIProviderException` + `AIErrorKind`)
con `IsRetryable` explícito: auth/modelo/contexto NO reintentan; red/timeout/rate-limit sí.

Tipos normalizados: `AIRequest` (mensajes, tool calls/results, system, temperatura, schema de
salida estructurada, timeout, correlación), `AIResponse` (+ `TokenUsage`), `AIModel`.

## 3. Registro (`Infrastructure/AI/ProviderRegistry.cs`)

Thread-safe, `case-insensitive`, filtrado por capacidades. El alta es **adaptador + 1 línea en DI**
(`AddInfrastructure`) + nombre en `CaAIA:Providers:EnabledProviders`. `ProviderRegistrationTask`
(hija de `IStartupTask`) hace el registro efectivo al arrancar.

## 4. Adaptadores (los tres reales)

| Adaptador | Id | Estado |
|---|---|---|
| `Providers/OpenCode/OpenCodeProvider.cs` | `opencode` | **Real y verificado E2E contra servidor real** (v1.18.30): gestiona `opencode serve` como proceso hijo (binario en PATH —solo ejecutables/`.cmd`/`.bat`/`.ps1` con wrap a shell, nunca el shim sh—, puerto libre, espera de `/global/health` con auth Basic `OPENCODE_SERVER_USERNAME/PASSWORD`, reutiliza servidor existente, parada al liberar) y habla su API según la spec OpenAPI (`POST /session`, `POST /session/{id}/message` → `{info, parts[]}`, `GET /config/providers`; completion real con modelo free verificada + mapeo de `info.error` a excepción). Sin binario: indisponible con mensaje accionable (apunta a `opencode-zen`) |
| `Providers/Zen/OpenCodeZenProvider.cs` | `opencode-zen` | **Real**: la API de modelos de OpenCode (`https://opencode.ai/zen/v1`, OpenAI-compatible según models.dev, key `OPENCODE_API_KEY` en secret store). Catálogo enriquecido vía `ModelsDevCatalog` (bloque `opencode` de models.dev, caché local 24 h): gratuidad (coste 0/0), contexto y capacidades; fallback al `/models` de Zen. **Routing por familia** (tabla de endpoints en opencode.ai/docs/zen): `muse-*`/`gpt-*`/`grok-*` → Responses API (`OpenAIResponsesClient`, sin tools); resto → chat completions |
| `Providers/OpenRouter/OpenRouterProvider.cs` | `openrouter` | **Real**: delega en `OpenAICompatibleClient` (chat + SSE + tools + `GET /models`); key desde `ISecretStore`; `CheckHealth` propaga errores de auth en lugar de mentir |
| `Providers/Local/LocalModelProvider.cs` | `local` | **Real** con **autodetección**: prueba Ollama (`:11434`) y LM Studio (`:1234`) y usa el primero con vida (timeout corto, sin colgarse); modelos marcados gratis; sin servidor, catálogo vacío y mensaje con pasos de instalación. Habilitado por defecto |

Notas honestas del adaptador OpenCode: ejecuta **sus** herramientas en **su** workspace
(`OpenCode:Directory`), por eso `Capabilities = None` (no acepta nuestras `ToolDefinition`;
el ejecutor lo trata como respuesta final) y `StreamAsync` emite la respuesta completa como
único delta. Sin binario: indisponible limpio (`CheckHealth=false`, catálogos vacíos), nunca
crash. Parseo defensivo de `parts[]`/`providers[]` con tests (texto concatenado, tokens si
vienen, formas desconocidas ignoradas).

Registro envuelve cada adaptador en `ResilientAIProvider` (timeout + reintentos + `Retry-After`).

**Esfuerzo de razonamiento** (Default/Minimal/Low/Medium/High/Xhigh, como el picker de OpenCode):
persistido en ajustes, propagado a planner/executor/verifier vía `AIRequest.ReasoningEffort`,
y solo enviado como `variant` al servidor OpenCode (único adaptador con soporte verificado
en su spec).

## 5. Resiliencia (`Infrastructure/AI/ResilientAIProvider.cs`)

Decorador aplicable a cualquier adaptador: timeout por intento (del `AIRequest`), reintentos solo
ante `IsRetryable`, backoff exponencial con jitter respetando `RetryAfter` (rate limits).
Testeado con dobles (2 fallos transient → éxito; fallo auth → 1 intento).

## 6. Claves y modelos gratis (decisiones verificadas)

- **Zen exige cuenta+key incluso para los gratis** (opencode.ai/docs/zen: sign in + billing + API key; el 400 sin key lo confirma). Ni opencode CLI los usa sin login. Por eso la app trae pestaña **Ajustes** (guardar/probar keys) en vez de fingir acceso anónimo.
- **Free tier de Zen por API directa: bloqueado por política** ("can only be used in OpenCode", verificado con key real). Vía válida: CLI local logueado (`opencode auth login`) + proveedor `opencode`.
- **Pollinations descartado**: su API (antes anónima) hoy devuelve 402 Payment Required (verificado). No se integra lo que no funciona.
- **Sin key de verdad**: solo modelos locales (Ollama/LM Studio, autodetectados) o variables `OPENCODE_ZEN_API_KEY` / `OPENROUTER_API_KEY` como alternativa al diálogo.

## 7. Cliente HTTP (`Infrastructure/AI/OpenAICompatibleClient.cs`)

Implementa el estándar de facto OpenAI sin SDKs: `POST {base}/chat/completions` (tools con
esquema generado desde `ToolDefinition`, `response_format: json_object`, `temperature`,
`max_tokens`), SSE (`stream:true`, deltas de contenido + acumulación de `tool_calls` por
fragmentos, tolerante a líneas corruptas), `GET {base}/models` (`context_length` → `LongContext`
desde 100k; error → lista vacía, nunca excepción). Mapeo de errores: 401/403/404 no reintentan;
429/5xx/timeout/red sí; `Retry-After` (segundos o fecha) alimenta el backoff.

Decisión: `HttpClient` directo (uno por proveedor singleton, `Timeout.Infinite` + CTS por
petición, `PooledConnectionLifetime` 5 min). Sin `IHttpClientFactory` (paquete no disponible
offline; el patrón actual es correcto para una app de escritorio con pocos clientes
long-lived). Testeado con `HttpMessageHandler` simulado: parsing, esquemas, errores, modelos y SSE.
