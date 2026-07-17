# Probar Neon Rush en tu teléfono Android

El APK de desarrollo se genera en `Builds/NeonRush-dev.apk`. Es una build **de desarrollo**
(sideload, no de tienda): sirve para jugar y ver el aspecto en un dispositivo real, y permite las
compras simuladas en el móvil. No es la build de release.

Tienes dos caminos. El **A (USB)** es el más rápido si tienes cable; el **B (copiar el archivo)**
funciona sin cable.

---

## Camino A — Instalar por USB (recomendado)

Una sola vez, en el teléfono:

1. **Ajustes → Acerca del teléfono → toca "Número de compilación" 7 veces.** Esto activa las
   *Opciones de desarrollador*.
2. **Ajustes → Sistema → Opciones de desarrollador → activa "Depuración por USB".**
3. Conecta el teléfono al PC por USB. En el teléfono saldrá "¿Permitir depuración USB?" → **Permitir**
   (marca "siempre" para no repetirlo).

Luego, con el APK ya compilado, ejecuta el instalador (te lo dejo preparado):

```
Neon Rush → menú del editor:  Neon Rush/Build/Android Dev APK   (para (re)compilar)
```

Y para instalarlo directamente, desde una terminal en la carpeta del proyecto:

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" install -r Builds\NeonRush-dev.apk
```

`-r` reinstala sobre una versión previa conservando tus datos de guardado. Cuando termine, verás
`Success` y el icono de **Neon Rush** aparecerá en el cajón de apps.

---

## Camino B — Copiar el archivo (sin cable)

1. Copia `Builds/NeonRush-dev.apk` al teléfono: por Google Drive, Telegram (mándatelo a ti
   mismo), correo, o un cable como memoria USB.
2. En el teléfono, abre el archivo con el explorador de archivos.
3. Android pedirá permitir "instalar apps desconocidas" para esa app (explorador/navegador) →
   acéptalo. Es normal para cualquier APK fuera de la tienda.
4. Instalar → Abrir.

---

## Qué esperar

- **Arranca en el menú**: título NEON RUSH, tus saldos, misiones del día, botón dorado de
  recompensa diaria. Toca en cualquier parte para correr.
- **Controles táctiles**: desliza izquierda/derecha para cambiar de carril, arriba para saltar,
  abajo para deslizarte.
- Es una build de desarrollo, así que verás una pequeña marca de agua de Unity y el rendimiento
  es ligeramente inferior al de una build de release — no te fíes del FPS exacto, pero sí del
  *aspecto* (bloom, niebla, skyline) y del *tacto* de los controles.

## Si algo falla

- **"App no instalada"**: casi siempre es una versión previa firmada con otra clave. Desinstala
  la anterior primero, o usa `adb install -r`.
- **Pantalla negra al abrir**: mándame el log. Con el teléfono conectado por USB:
  ```
  adb logcat -s Unity:V > logcat.txt
  ```
  y pásame ese archivo.
- **Va a tirones**: es esperable en una build *Development*. La build de release (IL2CPP sin
  profiler ni marca de agua) rinde bastante mejor.
