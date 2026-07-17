# Neon Rush

Endless runner para móvil (Android-first, iOS después). Estudio: **MoonCat Studio**.
Unity 6 (6000.5.2f1) · URP · C# · arquitectura limpia con capas forzadas por el compilador.

Corredor de neón procedural con economía completa, monetización, retención y LiveOps —
construido *feel-first*: primero un bucle que se siente bien, después todo lo que gana dinero.

---

## Estado actual

**Jugable de punta a punta**, con 201 tests EditMode en verde. Todo el código es de producción;
lo único pendiente son tres SDK que dependen de tus cuentas (ver más abajo).

| Sistema | Estado | Notas |
| --- | --- | --- |
| Runner jugable | ✅ | Swipe, carriles, salto, deslizamiento, pista procedural, colisión AABB manual |
| Game feel | ✅ | Buffer de entrada, caída rápida, FOV por velocidad, inclinación, cámara con lag |
| Economía | ✅ | Wallet con valores ofuscados, libro mayor grifo/sumidero, tope anti-overflow |
| Persistencia | ✅ | Escritura atómica + backup + firma HMAC; guarda en pausa (Android mata sin avisar) |
| Anuncios | ✅ | Gobernador de frecuencia (gracia, doble puerta, escudo de carrera corta); revivir/doblar |
| Tienda + IAP | ✅ | Validación de recibos **en servidor** antes de conceder; solo cosméticos, nunca poder |
| Remote Config | ✅ | Cada número de balance clampeado a rango seguro (un push hostil no puede brickear) |
| Analytics | ✅ | Taxonomía centralizada, embudo grifo/sumidero, señal de compra-bloqueada |
| Recompensas diarias | ✅ | Ciclo de 7 días, anti-manipulación de reloj, racha persistida |
| Misiones | ✅ | 3/día deterministas por fecha UTC, dirigidas por el bus de eventos |
| Menú principal | ✅ | Ritual explícito de reclamo diario, panel de misiones, tap-para-jugar |
| Pase visual | ✅ | Bloom, niebla al horizonte, skyline procedural, estela, monedas flotantes |

### Bloqueado en ti (SDK atados a tus cuentas)

Los puertos están definidos y el juego corre con adaptadores locales/simulados. Cada SDK es
**un fichero de adaptador** cuando lo instales:

- **Firebase** (Auth, Firestore, Analytics, Messaging, Remote Config, Crashlytics) — para cloud
  save, validación de recibos en servidor, config remota real y analytics reales.
- **Google AdMob** — para `IAdsService` real (ahora: `NullAdsService` / `SimulatedAdsService`).
- **Unity IAP / Google Play Billing + StoreKit** — para `IIapService` real (ahora: simulado).

---

## Arquitectura

Capas con dependencias hacia dentro, **forzadas por assembly definitions** (no aspiracional —
una violación no compila):

```
Presentation   MonoBehaviours: UI, VFX, mundo, input          (referencia hacia dentro)
Application    casos de uso, máquina de estados, sesión de carrera
Domain         entidades + reglas + puertos    ← C# puro, SIN UnityEngine
Infrastructure adaptadores: Firebase, AdMob, IAP, save, time   (implementa los puertos)
Composition    GameBootstrap: el único que conoce los tipos concretos
```

`Domain` y buena parte de `Application` no tocan el motor — por eso sus tests corren en
milisegundos en CI, sin editor, sin GPU, sin licencia. Ver [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

---

## Puesta en marcha

1. Abre el proyecto en Unity Hub (necesita **6000.5.2f1** + módulo *Android Build Support*).
2. Primera vez: `Neon Rush/Setup/Configure Project` y `Neon Rush/Setup/Build Game Scene` (menú del editor).
3. Abre `Assets/_Project/Scenes/Game.unity` y dale a Play. En el editor se juega con flechas/WASD/espacio.

**Probar en el móvil:** ver [docs/PLAYTEST_ON_PHONE.md](docs/PLAYTEST_ON_PHONE.md).
Para compilar: `Neon Rush/Build/Android Dev APK`.

**Tests:** `Window → General → Test Runner → EditMode → Run All`, o en headless:
```
Unity.exe -batchmode -nographics -projectPath . -runTests -testPlatform EditMode
```

---

## Convenciones

- **Git:** el desarrollador realiza TODAS las operaciones de git manualmente. Los mensajes de
  commit siguen Conventional Commits.
- **Sin valores hardcodeados de balance:** todo lo tuneable pasa por Remote Config con un default
  compilado que ya produce un juego completo y balanceado offline.
- **El cliente es no confiable:** moneda autoritativa en servidor, recibos validados en servidor.
- **60 FPS en gama baja** es la restricción vinculante: cero asignaciones por frame en carrera,
  nada de `Instantiate` durante una partida (todo pooleado).
