# Neon Rush — set de modelos 3D

27 modelos, 6.256 triángulos en total, 16 materiales compartidos, 9 texturas PBR.
Todo generado proceduralmente; el código fuente está en `Source/` y es reproducible
con `python3 build.py <destino>`.

Este set sustituye al greybox que `PrimitiveFactory` y `NeonMaterials` construyen en
runtime. **Ningún archivo de gameplay ha sido modificado** — son assets más una guía.

---

## Convenciones (léelas antes de tocar nada)

| | |
|---|---|
| Unidades | 1 unidad = 1 metro |
| Arriba | +Y |
| Delante | **-Z en glTF** → +Z en Unity tras el flip del importador |
| Escala de importación | **1.0**. No toques el Scale Factor |
| Materiales | Compartidos por nombre; 16 cubren los 27 modelos |

### El flip de Z: el detalle que rompe todo si se ignora

glTF es right-handed, Unity es left-handed. Todos los importadores (glTFast,
UnityGLTF) resuelven la diferencia **negando Z al importar**.

Consecuencia práctica: en Unity el jugador está en z = 0 y la pista crece hacia +Z,
así que un obstáculo que se acerca le muestra su cara **-Z**. Esa cara es la que aquí
está autorada en **+Z**.

Por eso toda la señalética de los obstáculos (chevrones, flechas, rejilla) vive en +Z,
y el núcleo emisivo de los personajes está en su espalda (+Z), que es lo único que la
cámara de persecución ve durante toda la partida. Invertir esto no produce un fallo
sutil: deja al jugador mirando una losa negra sin información, con 250 ms para adivinar.

`Source/scene.py` aplica ese mismo flip antes de renderizar, así que
`Preview/scene.png` es lo que realmente verás en Unity, no una aproximación.

---

## Inventario

### Characters — 4 modelos · pivote entre los pies, y = 0

| Modelo | Función | Tris | Dimensiones |
|---|---|---:|---|
| `CHR_Runner_Default` | Personaje inicial. Sustituye al cubo cian de `GameBootstrap` | 772 | 0.74 × 1.59 × 0.39 |
| `CHR_Runner_Nova` | `char_nova` (2.500 monedas) | 808 | 0.74 × 1.67 × 0.39 |
| `CHR_Runner_Nova_Void` | Skin `skin_nova_void` (350 gemas) | 808 | 0.74 × 1.67 × 0.39 |
| `CHR_Runner_Rook` | `char_rook` (7.500 monedas) | 916 | 0.89 × 1.59 × 0.39 |

Los cuatro caben en la caja 0.8 × 1.6 × 0.8 de `PlayerMotor.Bounds`. Rook es más
ancho de hombros pero **su huella de colisión es idéntica**: los cosméticos no tocan
el balance, igual que impone `StoreItem.cs`.

`Nova_Void` comparte malla exacta con `Nova`. Una skin que cambia la silueta se lee
como un cambio de hitbox. En runtime puedes resolverlo con un swap de material sobre
la malla de Nova en lugar de cargar una segunda malla.

### Obstacles — 3 modelos · pivote en el centro de la caja

| Modelo | Función | Tris | Dimensiones | Colocar en |
|---|---|---:|---|---|
| `OBS_LowJump_Barrier` | `ObstacleKind.LowJump` — solo salto | 164 | 1.80 × 0.70 × 1.20 | CentreY 0.35 |
| `OBS_HighSlide_Gate` | `ObstacleKind.HighSlide` — solo deslizamiento | 200 | 1.80 × 1.00 × 1.20 | CentreY 1.60 |
| `OBS_FullBlock_Wall` | `ObstacleKind.FullBlock` — solo cambio de carril | 144 | 1.80 × 1.60 × 1.20 | CentreY 0.80 |

Las tres dimensiones son **exactas**, no aproximadas, y el build las verifica en cada
ejecución. El docstring de `ObstacleArchetype` ya dice por qué: "un modelo que se sale
de estas dimensiones produce muertes contra un hitbox que el jugador no puede ver".
`build.py` además comprueba las tres invariantes de juego:

- `LowJump` no supera 0.70 m — un salto correcto no puede rozarlo.
- `HighSlide` no baja de 1.10 m — un deslizamiento correcto no puede rozarlo.
- Ninguno excede 1.80 m de ancho — no invaden el carril contiguo.

La señalética es direccional a propósito: `^` para saltar, `v` para deslizar, flechas
laterales para esquivar. A 30 m/s una flecha se parsea más rápido que un convenio de
color que el jugador tiene que haber aprendido antes.

### Pickups — 4 modelos · pivote en el centro

| Modelo | Función | Tris | Dimensiones |
|---|---|---:|---|
| `PU_Coin` | Moneda. Sustituye `PrimitiveFactory.Coin` | 156 | 0.70 × 0.70 × 0.06 |
| `PU_Magnet` | `PowerUpType.Magnet` | 168 | 0.46 × 0.59 × 0.46 |
| `PU_Shield` | `PowerUpType.Shield` | 64 | 0.68 × 0.59 × 0.46 |
| `PU_DoubleScore` | `PowerUpType.DoubleScore` | 172 | 0.61 × 0.61 × 0.47 |

Los tres power-ups se distinguen **por silueta**, no por color: `TrackStreamer` los
hace girar sobre tres ejes, y mientras giran el color se lava dentro del bloom y lo
único que sobrevive es el contorno. Herradura, cristal hexagonal y tableta octogonal
son inconfundibles incluso monocromos.

La moneda tiene el eje del disco en Z, igual que la rotación de 90° en X que
`PrimitiveFactory.Coin` ya aplica. El bisel del canto no es decoración: un cilindro
plano desaparece de canto y la moneda parece parpadear en lugar de flotar.

### Track — 3 modelos

| Modelo | Función | Tris | Dimensiones | Pivote |
|---|---|---:|---|---|
| `TRK_RoadSlab_30m` | Un chunk de calzada | 36 | 9.00 × 0.30 × 30.00 | Centro |
| `TRK_LaneMarker_30m` | Separador de carril | 12 | 0.08 × 0.02 × 30.00 | Centro |
| `TRK_EdgeRail_30m` | Raíl de neón en el borde (**nuevo**) | 24 | 0.14 × 0.50 × 30.00 | Base |

Ancho 9.00 m = `LaneWidth 2.6 × 3 + 1.2`. Largo 30 m = `ChunkLength`. Están modelados
exactamente a un chunk para que se encadenen sin costura y **sin escalado**.

Los marcadores son tira continua, no discontinua: a 30 m/s una línea discontinua hace
aliasing y se convierte en un estrobo en un panel de 60 Hz, y su función es ser una
referencia estable que el ojo pueda seguir.

### Environment — 6 modelos · pivote en la base

| Modelo | Función | Tris | Dimensiones |
|---|---|---:|---|
| `ENV_Building_Low` | Bloque bajo del skyline | 36 | 3.23 × 9.15 × 3.35 |
| `ENV_Building_Mid` | Bloque medio | 36 | 4.07 × 15.15 × 3.75 |
| `ENV_Building_Tall` | Bloque alto, el que rompe la niebla | 36 | 4.91 × 24.16 × 4.15 |
| `ENV_NeonStrip_Crown` | Banda de cornisa suelta | 12 | 3.15 × 0.15 × 3.35 |
| `ENV_Billboard_Sign` | Valla publicitaria (**nuevo**) | 108 | 3.60 × 6.17 × 0.22 |
| `ENV_ArchGate` | Arco sobre la pista (**nuevo**) | 84 | 10.60 × 6.05 × 0.67 |

Tres alturas fijas sustituyen la altura aleatoria continua del greybox. Un set fijo
comparte malla y material y por tanto se GPU-instancia; una escala aleatoria continua
no puede batchear y metería docenas de draw calls en decorado que nadie mira. Varía el
skyline **mezclando y rotando 90°**, nunca escalando: una escala no uniforme estira la
textura de fachada y rompe la proporción de la cornisa.

El arco tiene un trabajo de gameplay, no solo decorativo: `StageService` avanza la
partida por etapas y ahora mismo nada en el mundo marca la transición. Su gálibo es de
5.2 m, muy por encima del techo de 2.1 m de cualquier obstáculo, así que es imposible
confundirlo con algo que haya que esquivar.

### Store — 7 modelos

| Modelo | Función | Tris | Dimensiones |
|---|---|---:|---|
| `STR_Board_Pulse` | `board_pulse` (4.000 monedas) | 128 | 0.47 × 0.14 × 1.58 |
| `STR_Board_Prism` | `board_prism` (500 gemas) | 136 | 0.47 × 0.22 × 1.58 |
| `STR_Gem` | Moneda premium (`CurrencyType.Gems`) | 48 | 0.48 × 0.62 × 0.48 |
| `STR_CoinStack` | Icono de los packs `gems_100`…`gems_5000` | 624 | 0.72 × 0.25 × 0.74 |
| `STR_Chest_Bundle` | `bundle_starter` y `StarterPackOfferScreen` | 136 | 0.84 × 1.00 × 0.71 |
| `STR_Crown_Vip` | `vip_monthly` y `VipScreen` | 88 | 0.70 × 0.34 × 0.70 |
| `STR_Badge_NoAds` | `no_ads` (`ItemKind.AdRemoval`) | 340 | 0.68 × 0.68 × 0.19 |

`STR_CoinStack` reutiliza el perfil exacto de `PU_Coin`, así que el icono de la tienda
y lo que se recoge en carrera son visiblemente el mismo objeto.

`Prism` lleva un cristal en la punta y no solo un recoloreado: un artículo a precio de
gemas que solo cambia de tono se lee como mal negocio al lado de uno a precio de monedas.

---

## Materiales

16 materiales para 27 modelos. Los colores salen literalmente de `NeonMaterials.cs` y
`TrackStreamer.cs`, así que el arte no puede desviarse del greybox que sustituye.

| Material | Uso | Emisión |
|---|---|---:|
| `MAT_Neon_Cyan` | Jugador, Magnet, acentos | 1.6 |
| `MAT_Neon_Magenta` | Peligro: toda la señalética de obstáculos | 1.6 |
| `MAT_Neon_Gold` | Monedas, DoubleScore, corona | 1.6 |
| `MAT_Neon_Violet` | Marcadores de carril, raíles | 1.4 |
| `MAT_Neon_Blue` | Shield | 2.2 |
| `MAT_Neon_Pink` / `MAT_Neon_Lilac` | Acentos del skyline | 1.8 |
| `MAT_Neon_White` | Puntos calientes | 1.2 |
| `MAT_Dark_Asphalt` / `MAT_Dark_Chassis` | Estructura no emisiva | 0 |
| `MAT_Metal_Steel` | Placas, blindaje | 0 |
| `MAT_Dark_Void` | Base de la skin Void | 0.35 |
| `MAT_Glass_Tint` | Cristales translúcidos | 0.9 |
| `MAT_Road_Panel` / `MAT_Facade_Lit` / `MAT_Coin_Face` | Los tres texturizados | var. |

### Emisión por encima de 1.0

glTF limita `emissiveFactor` a [0,1], pero el bloom de `NeonAtmosphere` tiene el
threshold en 0.9 y el proyecto usa valores de hasta 2.2. Los valores reales están en
`MODEL_MANIFEST.json` bajo `materials[].emissionStrength`; aplícalos del lado de Unity
al construir los materiales URP, exactamente como hace hoy `NeonMaterials.Get(colour, emission)`.

Sin ese paso los modelos se ven correctos pero apagados: es la diferencia entre
"cubos de colores" y "neón".

---

## Texturas

9 PNG, 512² salvo la moneda a 256². Empaquetado ORM estándar de glTF
(oclusión=R, rugosidad=G, metalicidad=B), así que los importadores las conectan solas.

- `TEX_Road_*` — asfalto con juntas de panel y motas. Las juntas dan referencia de
  velocidad sin competir con los marcadores de carril.
- `TEX_Facade_*` — rejilla de ventanas encendidas al ~35%. Una fachada totalmente
  iluminada parece una hoja de cálculo; una encendida a parches parece una ciudad de
  noche vista desde 200 m, que es la única distancia desde la que se ve.
- `TEX_CoinFace_*` — anillo de canto y glifo de rayo, radial para que no se vea
  costura al flotar.

---

## Presupuesto de rendimiento

`build.py` corta el build si se excede:

| Categoría | Presupuesto | Máximo real |
|---|---:|---:|
| Characters | 1.800 | 916 |
| Obstacles | 900 | 200 |
| Pickups | 500 | 172 |
| Track | 200 | 36 |
| Environment | 400 | 108 |
| Store | 1.200 | 624 |

Todo está entre el 10 % y el 52 % de su presupuesto. La restricción vinculante del
proyecto son 60 FPS en gama baja, y lo caro ahí no es el conteo de triángulos sino los
draw calls: por eso hay 16 materiales y no 27, por eso el skyline son tres mallas
fijas, y por eso los chaflanes están solo donde el jugador mira.

Ninguna malla proyecta sombra por diseño, en coherencia con
`PrimitiveFactory.ConfigureRenderer`.

---

## Integración en Unity

### 1. Instalar un importador de glTF

Unity no lee glTF de serie. Package Manager → *Add package by name*:

```
com.atteneder.gltfast
```

(Alternativa: `com.unity.cloud.gltfast` en versiones recientes, o UnityGLTF.)

### 2. Ajustes de importación

Para cada `.glb`, en el inspector:

- **Scale Factor 1.0** — ya están en metros.
- **Generate Colliders: off** — el proyecto no usa el motor de física; la colisión es
  un AABB a mano en `CollisionSystem`. Dejar colliders puestos haría que Unity
  actualice la broadphase de cientos de objetos móviles cada frame para nada, que es
  exactamente lo que `PrimitiveFactory.StripCollider` existe para evitar.
- **Cast Shadows: off**, **Receive Shadows: off**, Light/Reflection probes off.
- **Read/Write: off**.

### 3. Sustituir la fábrica de primitivas

El camino de menor riesgo es un `IMeshProvider` detrás del cual `PrimitiveFactory`
queda como fallback. Nada fuera de `NeonMaterials`/`PrimitiveFactory` sabe hoy qué
aspecto tiene un obstáculo, así que el cambio queda contenido en `Presentation/Visuals`:

```csharp
public interface IMeshProvider
{
    GameObject Obstacle(ObstacleKind kind);
    GameObject Coin();
    GameObject PowerUp(PowerUpType type);
    GameObject Player(string characterId);
}
```

Puntos de enganche concretos:

| Dónde | Qué hace hoy | Qué pasa a hacer |
|---|---|---|
| `TrackStreamer` L134 | `PrimitiveFactory.Cube("Obstacle", …)` | Instancia el prefab por `ObstacleKind` |
| `TrackStreamer` L139 | `PrimitiveFactory.Coin(…)` | Instancia `PU_Coin` |
| `TrackStreamer` L150 | `PrimitiveFactory.Cube("PowerUp", …)` | Instancia por `PowerUpType` |
| `TrackStreamer` L596 | Cubo de calzada | `TRK_RoadSlab_30m` |
| `TrackStreamer` L610 | Cubos de marcador | `TRK_LaneMarker_30m` |
| `TrackStreamer` L667 | Cubos de edificio | Los tres prefabs de `ENV_Building_*` |
| `GameBootstrap` L441 | Cubo del jugador | `CHR_Runner_*` según el personaje equipado |

**Dos cosas que no se pueden romper:**

1. **El pooling.** `GameObjectPool` prellena instancias en el arranque y el proyecto
   prohíbe `Instantiate` durante una partida. Los prefabs entran en el pool igual que
   los primitivos; lo que cambia es la factoría, no el ciclo de vida.

2. **La escala de los obstáculos.** Hoy `TrackStreamer` L583 hace
   `localScale = new Vector3(archetype.Width, archetype.Height, archetype.Depth)`
   sobre un cubo unitario. Estos modelos **ya vienen a tamaño real**, así que esa línea
   debe pasar a `Vector3.one`. Si se deja, un obstáculo de 1.8 m se escala otra vez por
   1.8 y acabas con una pared de 3.24 m. `CollisionSystem` lee la escala del transform,
   así que el hitbox se iría con él y el bug sería consistente, invisible en tests, y
   solo visible como "el juego me mató desde lejos".

### 4. Pivote del jugador

`GameBootstrap` L452 crea un `PlayerPivot` hijo y desplaza la malla media altura hacia
arriba para que el padre quede a ras de suelo. Los personajes ya tienen el pivote entre
los pies, así que ese hijo y su ajuste de media altura se pueden eliminar.

---

## Sobre FBX

No se incluye, y es deliberado. GLB cubre Unity por completo vía glTFast, y Blender,
Maya y 3ds Max abren glTF de forma nativa desde hace años. Un FBX aquí sería un tercer
formato que mantener sincronizado sin que nadie lo necesite.

Si lo necesitas para una herramienta concreta, la conversión es una línea:

```bash
blender -b -P convert.py   # bpy.ops.import_scene.gltf() → bpy.ops.export_scene.fbx()
```

---

## Estructura

```
Art/
├── Models/
│   ├── Characters/   *.glb  +  gltf/*.gltf, *.bin
│   ├── Obstacles/
│   ├── Pickups/
│   ├── Track/
│   ├── Environment/
│   └── Store/
├── Textures/         9 PNG PBR
├── Preview/          contact sheets + scene.png (previsualización en espacio Unity)
├── Source/           los generadores; `python3 build.py <destino>` reconstruye todo
└── MODEL_MANIFEST.json
```

`MODEL_MANIFEST.json` es la fuente de verdad legible por máquina: por modelo lleva
función, pivote, triángulos, bounds exactos, materiales y bytes; y el resultado de la
validación. Si alguna vez toca regenerar el arte, ese fichero es lo que se compara.

---

## Qué se ha inferido

Tres modelos no tienen equivalente en el greybox y son adiciones razonadas, no relleno:

- **`TRK_EdgeRail_30m`** — la calzada del greybox simplemente termina en su borde y el
  ojo no tiene contra qué medir la velocidad lateral. Un raíl encendido en la periferia
  es la señal de velocidad más barata que existe: 2 draw calls por chunk.
- **`ENV_Billboard_Sign`** — el skyline es todo bloques verticales y se lee como un
  peine repetido. La valla es el único elemento horizontal y rompe ese ritmo.
- **`ENV_ArchGate`** — marca las transiciones de `StageService`, que hoy no tienen
  ninguna representación en el mundo.

Y una simplificación que conviene conocer: los edificios pasaron de altura aleatoria
continua a tres alturas fijas. Se pierde variedad y se gana batching. Si el skyline
acaba resultando repetitivo, la respuesta es añadir una cuarta y una quinta malla, no
volver a escalar aleatoriamente.
