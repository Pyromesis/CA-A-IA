# CA-A-IA v0.1.6

Nueva pestaña **Autonomía**: el agente controla tu PC como lo harías tú.

## Nuevo

- **Pestaña Autonomía** (entre Chat e Historial): pide en lenguaje natural
  («abre el navegador y busca…») y mira cómo mueve el ratón, hace clic,
  desplaza, escribe y pulsa teclas, narrado en vivo en el chat.
- **Movimiento humanizado**: curva ease-in-out, jitter de mano, velocidad
  variable y micro-pausas antes de actuar. Nada de saltos de robot.
- **Carpeta temporal por pedido** (`%TEMP%\CA-A-IA-autonomy\…`, limpieza
  automática de +7 días) como workspace del agente.
- **Seguridad por niveles**: sin Nivel 2 (sin control del PC); en Nivel 1 cada
  clic/tecla pide tu confirmación; Nivel 3 actúa sin pedir.
- La IA recibe encuadre de autonomía (primero `UiGetScreen`, coordenadas de
  elementos reales) y las burbujas de chat se comparten entre pestañas.

## Instalación

- **CA-A-IA-Setup-0.1.6.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
