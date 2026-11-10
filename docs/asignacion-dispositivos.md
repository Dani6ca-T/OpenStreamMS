# Asignar dispositivos a una sesión: qué funciona en Windows

Investigación de noviembre de 2026 para asignar a un puesto cualquier dispositivo
(mando, USB genérico…) de forma fiable. Las pruebas se hicieron en Windows 11 Pro 26300
con `tests/DeviceSessionProbe` (ver abajo) sobre un HID USB de fabricante, comprobando
desde la consola (sesión 1, usuario normal), desde la sesión de un puesto (sesión 2,
su usuario `osms-…`) y como SYSTEM en la sesión 0.

## Resultados

### `DEVPKEY_Device_SessionId`: solo oculta, no protege

| `SessionId` | Consola | Puesto | SYSTEM |
|---|---|---|---|
| Sin fijar | lo ve y lo abre | lo ve y lo abre | lo ve y lo abre |
| `= 2` | **no lo ve** (`CM_Locate_DevNode` → CR 13, ni SetupDi ni interfaces) **pero lo abre** por su ruta | lo ve y lo abre | lo ve y lo abre |

- Es solo enumeración: un proceso que ya conoce la ruta del interfaz lo abre sin problema.
- **Windows la borra cada vez que el dispositivo arranca** (reinicio, deshabilitar/habilitar,
  reconexión). Fijada con el dispositivo deshabilitado tampoco sobrevive al habilitarlo:
  habría que volver a fijarla en cada arranque, con una ventana en la que se ve.
- Sirve como complemento (que los juegos de la consola no listen el mando del puesto), no
  como aislamiento.

### Descriptor de seguridad del dispositivo (`SPDRP_SECURITY_SDS`): aísla de verdad

| ACL del nodo y sus hijos | Consola | Puesto | SYSTEM |
|---|---|---|---|
| `D:P(A;;GA;;;SY)(A;;GA;;;<SID del puesto>)` + reinicio | **acceso denegado (5)** | abre | abre |
| `D:P(A;;GA;;;SY)(A;;GA;;;<SID de la consola>)` + reinicio | abre | **acceso denegado (5)** | abre |
| Cambiada **sin reiniciar** | sigue la ACL anterior | sigue la ACL anterior | abre |
| Quitada + reinicio | abre | abre | abre |

- La aplica el gestor de E/S al abrir, para cualquier clase de dispositivo y sin drivers.
- Persiste en el registro (sobrevive a reinicios y reconexiones): hay que anotarla para
  quitarla si el servicio se cae con un puesto encendido.
- Solo cuenta al crear el objeto de dispositivo: **cambiarla exige reiniciar el nodo**, que
  además cierra los handles que tuviera abiertos la otra sesión.
- Es por usuario, no por sesión. Los puestos usan cuentas propias (`osms-…`), así que basta
  con el SID del usuario. Con el mismo usuario en dos sesiones habría que usar el SID de
  inicio de sesión (`S-1-5-5-x-y`), que es único por sesión.
- **No cubre lo que abre un servicio del sistema** en nombre de la sesión: audio
  (audiosrv), cámaras (FrameServer), unidades montadas (el volumen lo monta el sistema y la
  letra de unidad es global), GameInput/Windows.Gaming.Input. Para esos hace falta otro
  camino (audio por puesto, como ya se hace; letras de unidad por sesión; redirección USB).

### Otras vías (sin probar en hardware)

- **HidHide con jail de sesión** (`instancia!N`, driver 1.4.181+): lo que ya usa
  OpenStreamMS para los mandos. Funciona igual que la ACL (decide al abrir, no revoca
  handles), pero solo en las clases que filtra HidHide (HID, XUSB, Xbox One) y depende de un
  driver de terceros.
- **Redirección USB de RDP (URBDRC)**: los componentes de servidor están en Windows 11 Pro,
  pero no está documentado que Pro funcione como servidor, y el cliente FreeRDP en Windows
  depende de UsbDk. Queda para dispositivos que necesiten su driver dentro de la sesión.
- **Filtro UMDF propio** que deniegue aperturas por sesión: viable, pero la ACL da lo mismo
  sin driver.

## Decisión

1. **Mandos**: HidHide como hasta ahora; si no está o no tiene jail de sesión, ACL por
   usuario del puesto como respaldo.
2. **Otros dispositivos USB** asignados a un puesto: ACL por usuario del puesto, aplicada al
   encender (y al conectarse en un puerto de un puesto encendido) y quitada al apagar, con
   reinicio del nodo y registro en disco para restaurar tras una caída.
3. **Almacenamiento, cámaras y audio USB**: avisar en el panel de que la ACL no los aísla
   (los abre un servicio del sistema) hasta tener un mecanismo propio para cada uno.

## Herramienta de prueba

`tests/DeviceSessionProbe` (no forma parte del servicio):

```powershell
dotnet build tests/DeviceSessionProbe -c Release -o <carpeta>\probe
<carpeta>\probe\DeviceSessionProbe.exe list VID_     # nodos, clase, SessionId y puerto
<carpeta>\probe\DeviceSessionProbe.exe probe <id>    # qué ve y qué abre esta sesión
```

`acl-test.ps1` y `session-id-test.ps1` repiten las pruebas de arriba; corren como SYSTEM
(tarea programada) porque fijan propiedades, reinician el dispositivo y lanzan la
comprobación con el token del usuario de cada sesión. Usar un dispositivo de prueba que no
sea el teclado ni el ratón de la consola: se reinicia varias veces.
