# Sesiones compuestas (de 2 a 4 sesiones en un stream)

Una **sesión compuesta** junta en un solo stream las pantallas de 2 a 4 sesiones RDP, cada una en
su hueco. Es una sesión más del panel: tiene su propio Sunshine, con su nombre, su puerto y su
emparejamiento, así que en Moonlight o en la TV aparece como un equipo más. Al conectarte se ven
todas a la vez.

Las sesiones que la forman siguen siendo normales: se pueden usar solas cuando la compuesta está
parada y pueden estar en varias compuestas, aunque solo en una en marcha a la vez.

## Cómo se usa

1. En el panel, **+ Sesión compuesta**.
2. Ponle un nombre y elige una plantilla:
   - dos, una al lado de otra;
   - dos, una encima de otra;
   - cuatro (2×2);
   - una grande a la izquierda y dos a la derecha;
   - una grande arriba y dos abajo.
3. En la rejilla, elige qué sesión va en cada hueco. Si eliges una que ya estaba en otro hueco,
   se intercambian. Los huecos que queden vacíos salen en negro.
4. Elige de qué sesión salen **el teclado, el ratón y el audio**, y la resolución y los fps del stream.
5. Arráncala con su botón de encendido y conéctate a ella desde Moonlight o la TV (botón **Conectar**).

Para cambiarla, párala y usa **Editar la composición** en su menú ⋯.

## Qué pasa al arrancarla y al pararla

| Momento | Qué hace |
|---|---|
| Arrancar | Arranca las sesiones paradas, pone en pausa el Sunshine propio de cada una, las reconecta al tamaño de su hueco y lanza el Sunshine de la compuesta en la sesión de Windows del teclado |
| En marcha | Si la sesión del teclado se para, se para la compuesta. Si otra se para, su hueco sale en negro; si vuelve a arrancar, se pone otra vez en su hueco |
| Parar | Cada sesión vuelve a su tamaño y a su propio Sunshine. Las que arrancó la compuesta se paran |

Mientras una sesión está en una compuesta en marcha, su tarjeta lleva la etiqueta **En «nombre»**
con un mapa de su hueco. Su propio stream está en pausa.

## Decisiones de diseño

| Tema | Decisión | Motivo |
|---|---|---|
| Qué es | Un tipo de sesión (`Composite` con sus huecos) y no un servicio aparte | Hereda puertos, Sunshine propio, emparejamiento, tarjeta, arrancar y parar, arranque con el servicio y logs |
| Composición | El Sunshine de la compuesta (`capture = remoteidd`) abre los fotogramas RemoteIdd de cada sesión y los copia en un lienzo (`CopySubresourceRegion`) | Sin ventana presentadora ni shaders; solo se copia lo que cambia |
| Control | `osms-mosaic.txt` junto al `sunshine.conf` de la compuesta; Sunshine lo relee cada 250 ms | Un miembro que cambia de sesión de Windows no corta el stream |
| Dónde corre su Sunshine | En la sesión de Windows de la sesión del teclado | Sunshine inyecta teclado y ratón y captura el audio de la sesión en la que corre |
| Lienzo | La resolución de la compuesta; cada sesión se reconecta al tamaño de su hueco | Sin escalado: cada sesión dibuja a su tamaño real |
| Mandos | El mando N que crea su Sunshine va a la sesión del hueco N (si ese hueco no tiene sesión en marcha, a la del teclado) | El orden de los huecos es el de la rejilla |
| Huecos | Partes de la cuadrícula de 2×2 de los puestos (`SeatScreenArea`); no se pueden pisar | Sunshine y el servicio reparten el lienzo con las mismas celdas |

## Fichero de control

`sessions\<id de la compuesta>\Sunshine\config\osms-mosaic.txt`, lo escribe el servicio:

```
# OpenStreamMS mosaic: <Windows session id> <part>
canvas 1920 1080
7 Left
9 TopRight
12 BottomRight
```

Un hueco cuya sesión está parada no se escribe y sale en negro. Con un tamaño impar, la mitad
izquierda o de arriba se queda el lado menor, igual que en la pantalla dividida de los puestos.

## API

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/api/composites/templates` | Plantillas y sus huecos en orden |
| POST | `/api/composites` | Crea una compuesta: `name`, `slots` (`[{session, area}]`), `input`, `width`, `height`, `frameRate`, `enabled` |
| PUT | `/api/composites/{id}` | Edita una compuesta parada (mismo cuerpo) |

Arrancar, parar, reiniciar, borrar, logs y emparejamiento son los de cualquier sesión
(`/api/sessions/{id}/...`). `GET /api/sessions` incluye en la compuesta `composite` y
`compositeInput`, y en cada miembro en uso, `compositeOf`.

## Requisitos y límites

- Las sesiones tienen que ser RDP con captura **RemoteIdd**, en la misma GPU. Sin HDR.
- No se puede arrancar si alguna de sus sesiones tiene un cliente conectado, está en otra compuesta
  en marcha o la usa un puesto local.
- No se puede borrar una sesión que está en una compuesta: primero hay que quitarla de ella.
- Teclado, ratón y audio son los de una sola sesión.

## Archivos

| Archivo | Qué hace |
|---|---|
| `Core/Api/StreamSessionService.Composite.cs` | Crear, editar, arrancar, parar y vigilar las compuestas |
| `Core/Api/CompositeEndpoints.cs` | API REST |
| `Services/Mosaic/MosaicLayout.cs` | Plantillas, huecos sin pisarse, tamaños y contenido de `osms-mosaic.txt` |
| `Services/Gamepad/GamepadSessionIsolation.cs` | Mando N de la compuesta → sesión del hueco N |
| `Core/wwwroot/composites.js` | Editor en rejilla y lo que enseñan las tarjetas |
| Sunshine-Web-RTC `src/platform/windows/display_remoteidd.cpp` | Lectura del fichero y lienzo |
