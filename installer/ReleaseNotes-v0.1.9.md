# CA-A-IA v0.1.9

Memoria por proyecto estilo Obsidian + internet para la IA.

## Nuevo

- **Bóveda de conocimiento** (`.ca-a-ia/*.md` en cada proyecto): la IA la
  consulta antes de decidir (`VaultSearch`, `VaultRead`), recorre su red
  (`VaultGraph`: enlaces `[[wiki]]` + backlinks) y guarda decisiones y
  aprendizajes (`VaultWrite`). Ver `docs/ToolArchitecture.md`.
- **Internet**: `WebSearch` (sin keys) para lo que no sepa + `WebFetch` (página
  como texto truncado). Solo https, sin localhost/privadas, descargas y
  tiempos acotados. En Nivel 1 cada acceso web pide confirmación; nunca mete
  secretos en las búsquedas.

## Instalación

- **CA-A-IA-Setup-0.1.9.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
