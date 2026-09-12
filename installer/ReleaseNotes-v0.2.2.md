# CA-A-IA v0.2.2

Archivos como árbol de verdad + autonomía sin pestañas duplicadas.

## Nuevo

- **Archivos en árbol expandible**: carpetas que se pliegan/despliegan con
  indentación, icono por tipo y tamaño/recuento. Abierto por defecto hasta el
  primer nivel: el contenido se ve sin clicks (antes solo salía la carpeta).

## Corregido

- La IA **no abre el mismo sitio dos veces**: si ya hay pestaña, cambia a ella
  (Ctrl+Tab) en vez de `UiOpenUrl` de nuevo (orden en el prompt + aviso en el
  resultado).
- Cero warnings en compilación y tests.

## Instalación

- **CA-A-IA-Setup-0.2.2.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
