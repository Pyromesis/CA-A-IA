# CA-A-IA v0.1.8

La IA ahora VE la pantalla + atajos nuevos.

## Nuevo

- **Visión en casi tiempo real**: `UiScreenshot` captura la pantalla (reducida,
  ~100-300 ms) y la imagen se adjunta sola al siguiente mensaje al modelo
  (requiere modelo con visión). La IA verifica con sus ojos tras cada clic.
- **Atajos globales nuevos**: **Mayús izq.+A** pausa y **Mayús izq.+S** reanuda,
  en cualquier app (hook de bajo nivel que distingue el shift izquierdo; no
  traga teclas: escribir mayúsculas funciona igual).
- Observación sin fricción: pantalla, ventana activa y capturas ya no piden
  confirmación (solo mueven información); cada clic/tecla sigue pidiéndola
  en Nivel 1.

## Instalación

- **CA-A-IA-Setup-0.1.8.exe** (abajo, en Assets): actualiza sin duplicar
  accesos y conserva tus datos (`%LocalAppData%\CA-A-IA`).
