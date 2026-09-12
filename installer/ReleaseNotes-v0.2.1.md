# CA-A-IA v0.2.1

Historial limpio, ratón preciso, botón Seguir y red de memoria.

## Nuevo

- **Pestaña Memoria**: la red neuronal del conocimiento (.md de la bóveda +
  lecciones): nodos movibles por color (lección ámbar, global azul, nota
  gris), aristas por `[[enlaces]]` y vista previa al tocar cada archivo.
- **Botón Seguir** en Chat y Autonomía: si la IA se atasca pensando, reencola
  lo a medias con tu instrucción y continúa **sin finalizar** (usa lo que
  escribas, o "sigue" por defecto).

## Corregido

- **Nueva conversación** abre vista limpia (antes se veía el chat viejo arriba).
- **Abrir conversación** muestra solo esa (sin separadores ni restos de otras).
- **Precisión del ratón**: closed-loop (verifica y corrige hasta 2 veces) +
  la sensibilidad de Windows visible en `UiGetScreen`. El movimiento absoluto
  ya era inmune a la sensibilidad por diseño.
- Cero warnings en compilación y tests.

## Instalación

- **CA-A-IA-Setup-0.2.1.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
