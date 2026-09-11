# CA-A-IA v0.1.0

Primer instalador público de **CA-A-IA** (firma CA): programación asistida por
múltiples proveedores de IA en Windows, con agentes autónomos, planificación
previa, verificación y autocorrección.

## Descarga

- **CA-A-IA-Setup-0.1.0.exe** (abajo, en Assets): doble clic y listo. Sin
  UAC: se instala por usuario en `%LocalAppData%\Programs\CA-A-IA`.
- Accesos: escritorio, menú Inicio → CA-A-IA y `Win+R` → `CA-A-IA`.

## Requisitos

- Windows 10 (1809+) u 11, x64.
- Sin .NET que instalar: el paquete es self-contained.
- Para modelos en la nube, tu API key (Ajustes): `OPENCODE_ZEN_API_KEY` u
  `OPENROUTER_API_KEY`. Sin key, funciona con modelos locales (Ollama/LM Studio)
  y el flujo llega hasta el plan con guía de configuración.

## Novedades

- Chat con burbujas, historial persistente (SQLite) y segmentación por
  conversación; motor del agente con estados, reintentos, auditoría final
  acotada y convergente; paneles Plan/Tareas/Archivos/Salida en vivo.
- Actualizar conserva tus datos (`%LocalAppData%\CA-A-IA`) y no duplica accesos.
- Al desinstalar, tus datos se conservan a propósito.

## Nota de firma

El instalador incluye metadatos del editor **CA**. La firma Authenticode con
certificado llegará en una próxima versión: Windows SmartScreen puede mostrar
el aviso habitual de editor desconocido en esta primera release.
