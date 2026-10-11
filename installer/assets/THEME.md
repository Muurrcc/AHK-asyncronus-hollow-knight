# Asyncronus - tema visual del instalador

Direccion: oscuridad silenciosa, niebla y hueso. Sobrio, mucho vacio, un unico punto de luz (el "destello de alma"). Sin arte ni logos de Team Cherry: la mascara es una forma propia (ovoide con dos puntas, ojos en ranura) y su eco azul desfasado simboliza el segundo jugador / asincronia.

## Paleta
| Rol | Hex | Uso |
|---|---|---|
| Vacio | `#06080C` | fondo mas profundo, ojos de la mascara |
| Cueva | `#0E141D` | fondo base oscuro |
| Niebla | `#22303E` | halos, paneles secundarios |
| Azul pizarra palido | `#8FA3B5` | bordes, eco, texto secundario, enlaces |
| Blanco hueso | `#E8E2D0` | mascara, titulos sobre oscuro |
| Papel | `#ECE8DC` | fondo claro de dialogos (texto por defecto legible) |
| Alma | `#CFE8F5` | unico acento: destellos, foco, barra de progreso |
| Tinta | `#344252` | texto sobre fondo claro |

Contraste: hueso sobre vacio ~14:1; tinta `#344252` sobre papel ~8:1; `#8FA3B5` solo para texto secundario sobre oscuro (~7:1).

## Variantes de imagenes (WixUI)
- `banner.bmp` / `dialog.bmp`: **claras** (fondo papel). Funcionan con el texto negro por defecto de WixUI sin tocar estilos. Recomendadas por defecto. En `dialog.bmp` el panel izquierdo (164 px) es oscuro con la mascara; el resto es papel.
- `banner-dark.bmp` / `dialog-dark.bmp`: totalmente oscuras. Solo si el integrador usa un tema/estilos con texto hueso `#E8E2D0` (UI personalizada o WixUI con dialogos propios).
- Variables: `WixUIBannerBmp` = banner.bmp, `WixUIDialogBmp` = dialog.bmp. Icono: `asyncronus.ico` (16-256 px), PNG de referencia `asyncronus-256.png`.
- El texto de WixUI se superpone a la izquierda del banner: la zona izquierda esta despejada; la mascara vive en el extremo derecho.

## Tipografia (incluida en Windows)
1. **Constantia** (preferida: serif romana de buen contraste, cercana a la inscripcion tipo Trajan en tono sobrio)
2. **Palatino Linotype**
3. **Georgia** (fallback universal)
- Titulos: serif, MAYUSCULAS pequenas con espaciado amplio (si el motor lo permite) o capitalizada normal.
- Cuerpo/UI: Segoe UI 9-10 pt (legibilidad en listas, rutas y errores). No mezclar mas de dos familias.

## Textos propuestos (es-ES)
**Bienvenida**
- Titulo: Asyncronus
- Cuerpo: Este asistente instalara Asyncronus, el modo multijugador para Hollow Knight. Cierra el juego antes de continuar.

**Licencia**
- Titulo: Condiciones de uso
- Cuerpo: Lee y acepta las condiciones para continuar. Asyncronus es un proyecto de fans y no esta afiliado a Team Cherry.
- Casilla: Acepto las condiciones

**Carpeta**
- Titulo: Carpeta de instalacion
- Cuerpo: Elige la carpeta de Hollow Knight donde se colocara el mod.
- Error: No se encontro Hollow Knight en esa carpeta.

**Progreso**
- Titulo: Instalando
- Cuerpo: Colocando los archivos del mod. Puede tardar un momento.
- Estados: Copiando archivos... / Registrando... / Casi listo.

**Final**
- Titulo: Instalacion completada
- Cuerpo: Asyncronus esta listo. Inicia Hollow Knight para jugar.
- Casilla: Abrir Hollow Knight al cerrar
- Error: La instalacion no pudo completarse. Cierra el juego e intentalo de nuevo.

## Reglas de tono
Frases cortas, segunda persona (tu), sin signos de exclamacion, sin jerga tecnica ni humor. Una accion por pantalla.

## Regenerar
`python make-assets.py` (Pillow) regenera todo; `python verify-assets.py` valida 24-bit, 493x58, 493x312 y tamanos del .ico.
