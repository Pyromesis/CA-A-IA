# CA-A-IA v0.1.2

Visibilidad total del agente + fiabilidad + árbol de archivos.

## Nuevo

- **Archivos como árbol**: carpetas y archivos anidados, con icono, tamaño y
  recuento, ordenados (carpetas primero).
- **El Chat muestra lo que hace la IA**: con el proveedor OpenCode se narran
  en vivo lecturas, ediciones, creaciones y comandos (incluido control del PC:
  "Ejecutando …" y "▶ Ejecutó …"). Al terminar, burbuja con lo que se escribió.
- **Barra de estado coherente**: la sesión muestra el estado real del motor
  (adiós al "Executing … Idle" contradictorio).
- El filtro **Gratis** del catálogo también se guarda entre sesiones.

## Corregido

- El timeout de tarea (10 min) ya no escapa como excepción ni envenena el
  motor: se convierte en fallo clasificable y el siguiente Run funciona.
- Reintentar tras fallo/cancelación pasa por `Idle` (transición legal).
- Los errores de autenticación/cuota del proveedor ya no se reintentan a
  ciegas: escalan como problema de configuración con su mensaje.
- El watchdog ya no falla tareas lentas pero vivas (turnos largos en OpenCode);
  el timeout de operación sigue acotando de verdad.

## Instalación

- **CA-A-IA-Setup-0.1.2.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
