# CA-A-IA v0.1.1

Actualización automática dentro de la app + correcciones.

## Nuevo

- **Ajustes → Actualizaciones**: la app busca el último release al arrancar y
  permite **descargar e instalar** sin salir: descarga verificada (SHA-256
  oficial + tamaño), instalación silenciosa y reapertura automática.

## Corregido

- El chat mostraba los mensajes largos del agente en una sola línea cortada:
  ahora envuelven completos.
- Archivos detecta `.txt` y otras 25 extensiones de texto/código (antes una
  carpeta solo con `.txt` parecía vacía).
- Salida/Tareas/Plan escuchan eventos desde el arranque (antes Salida salía
  vacía si se abría tras ejecutar).
- Los ajustes (proveedor, carpeta, nivel…) y el historial ya se guardan al
  cerrar en vez de perderse.
- La auditoría final ya no genera tareas en bucle: evidencia real, sin
  duplicados y con rondas acotadas (falla honesto si no converge).

## Instalación

- **CA-A-IA-Setup-0.1.1.exe** (abajo, en Assets): actualiza la versión anterior
  sin duplicar accesos y conservando tus datos (`%LocalAppData%\CA-A-IA`).
