# Generates license.rtf (notice shown in the installer, Spanish UI text) from the text below.
text = """Este asistente instalará Asyncronus, el modo multijugador para Hollow Knight. Cierra el juego antes de continuar.

Lee y acepta las condiciones para continuar. Asyncronus es un proyecto de fans y no está afiliado a Team Cherry.

1. Es un proyecto de la comunidad. No está afiliado ni respaldado por Team Cherry. Hollow Knight es propiedad de Team Cherry; necesitas tu propia copia legítima del juego.

2. Se modifica el fichero hollow_knight_Data\\Managed\\Assembly-CSharp.dll (se instala la Modding API para Unity 6) y se añaden varias librerías en esa misma carpeta. Antes de modificarlo se guarda una copia en hollow_knight_Data\\Managed-vanilla-backup.

3. El mod se instala en Managed\\Mods\\Asyncronus. No se tocan tus partidas guardadas ni los demás mods de la carpeta Mods.

4. Para desinstalar: Configuración > Aplicaciones > Asyncronus. Se restauran los ficheros originales y se quita la carpeta del mod. También puedes usar «Verificar integridad de los archivos del juego» en Steam.

5. Cierra Hollow Knight antes de instalar o desinstalar.

6. El software se ofrece «tal cual», sin garantías. Incluye componentes de terceros con licencia MIT (MonoMod, Mono.Cecil, unityscenerepacker, Modding API de Hollow Knight, BouncyCastle); consulta THIRD-PARTY-NOTICES.md.
"""

def esc(s):
    out = []
    for ch in s:
        o = ord(ch)
        if ch == "\\":
            out.append("\\\\")
        elif ch in "{}":
            out.append("\\" + ch)
        elif ch == "\n":
            out.append("\\par\n")
        elif o > 127:
            out.append("\\'%02x" % ch.encode("cp1252")[0])
        else:
            out.append(ch)
    return "".join(out)

open("license.rtf", "w", encoding="ascii").write(
    "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss Segoe UI;}}\\f0\\fs20\n" + esc(text) + "}")
