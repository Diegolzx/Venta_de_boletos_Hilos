# Sistema de Venta Distribuida de Boletos de Autobús

## Descripción

Sistema distribuido en C# (.NET) que simula la venta de boletos de autobús con **40 asientos**, utilizando sockets TCP para comunicación en red local (WiFi).

```
[Cliente T1] <───\
[Cliente T2] <────>─── [Servidor Central] (40 asientos)
[Cliente T3] <────>
[Cliente T4] <───/
```

---

## Requisitos

- **.NET SDK 6.0 o superior** instalado en todas las máquinas.
- Todas las laptops deben estar **conectadas a la misma red WiFi**.
- El **firewall** debe permitir conexiones TCP en el **puerto 5000**.

---

## Estructura del Proyecto

```
Venta_Boletos/
├── Servidor/
│   └── Servidor/
│       ├── Servidor.csproj
│       ├── Program.cs         ← Servidor TCP (acepta clientes, despacha comandos)
│       └── Autobus.cs         ← Clase con estado compartido y sincronización (lock)
├── Cliente/
│   └── Cliente/
│       ├── Cliente.csproj
│       └── Program.cs         ← Cliente interactivo (menú de consola)
└── README.md
```

---

## Instrucciones para Ejecutar

### Paso 1: Iniciar el Servidor (en UNA laptop)

```bash
cd Servidor/Servidor
dotnet run
```

El servidor mostrará su **IP local** y el **puerto 5000**. Anota la IP.

**Ejemplo de salida:**
```
╔══════════════════════════════════════════════════╗
║   SERVIDOR DE VENTA DE BOLETOS DE AUTOBÚS       ║
║ IP Local: 192.168.1.100                         ║
║ Puerto:   5000                                  ║
╚══════════════════════════════════════════════════╝
```

### Paso 2: Iniciar Clientes (en OTRAS laptops)

```bash
cd Cliente/Cliente
dotnet run -- 192.168.1.100
```

Reemplaza `192.168.1.100` con la **IP que mostró el servidor**.

**Para probar en la misma máquina** (abre múltiples terminales):
```bash
cd Cliente/Cliente
dotnet run
```
(Conecta automáticamente a `127.0.0.1:5000`)

### Paso 3: Usar el Menú del Cliente

```
╔══════════════════════════════════════════════════╗
║  MENÚ DE OPCIONES (Terminal T1)                  ║
╠══════════════════════════════════════════════════╣
║  1. Comprar asiento aleatorio                    ║
║  2. Comprar asiento específico                   ║
║  3. Ver asientos vendidos                        ║
║  4. Salir                                        ║
╚══════════════════════════════════════════════════╝
```

### Paso 4: Cerrar el Servidor

Presiona **Ctrl+C** en la terminal del servidor para ver el resumen final de ventas.

---

## Configurar el Firewall (Windows)

Si los clientes no pueden conectarse, ejecuta **como Administrador** en la laptop del servidor:

```powershell
netsh advfirewall firewall add rule name="Venta Boletos Servidor" dir=in action=allow protocol=tcp localport=5000
```

Para eliminarlo después:
```powershell
netsh advfirewall firewall delete rule name="Venta Boletos Servidor"
```

---

## Protocolo de Comunicación

| Comando Cliente       | Respuesta Servidor | Significado              |
|-----------------------|-------------------|--------------------------|
| `VENDER_RANDOM`       | `OK:n`            | Se vendió asiento n      |
|                       | `NO_QUEDAN`       | No hay asientos libres   |
| `VENDER_ESPECIFICO:n` | `OK:n`            | Venta exitosa            |
|                       | `OCUPADO`         | Asiento ya vendido       |
|                       | `INVALIDO`        | Número fuera de rango    |
| `RESUMEN`             | `VENDIDOS:[1,3]`  | Lista de vendidos        |
| `SALIR`               | `ADIÓS`           | Cierra conexión          |

Todos los mensajes terminan en `\n`.

---

## Explicación Técnica

### 1. Exclusión Mutua (`lock`)
dotnet run --project Servidor/Servidor/Servidor.csproj
```csharp
private readonly object lockObj = new object();

lock (lockObj)
{
    // Solo UN hilo a la vez puede ejecutar este bloque.
    // Verificar si asiento está libre + marcarlo como vendido
    // se ejecuta como operación ATÓMICA.
}
```

El `lock` en C# usa el mecanismo de **Monitor** del CLR. Cuando un hilo entra al bloque `lock`, adquiere el monitor del objeto. Cualquier otro hilo que intente entrar a un bloque `lock` con el **mismo objeto** quedará **bloqueado** hasta que el primero salga.

### 2. Condición de Carrera (Race Condition)

**Sin lock (PROBLEMA):**
```
Hilo A: Lee asiento 5 → libre ✓
Hilo B: Lee asiento 5 → libre ✓    ← ¡AÚN NO SE MARCÓ VENDIDO!
Hilo A: Marca asiento 5 como vendido
Hilo B: Marca asiento 5 como vendido → ¡DOBLE VENTA! 💥
```

**Con lock (SOLUCIÓN):**
```
Hilo A: Adquiere lock → Lee asiento 5 → libre → Marca vendido → Libera lock
Hilo B: Espera lock... → Adquiere lock → Lee asiento 5 → OCUPADO → Libera lock
```

### 3. Prioridad de Terminales

```
T1 > T2 > T3 > T4
(menor ID = mayor prioridad)
```

Cuando dos terminales solicitan el mismo asiento dentro de una **ventana temporal de 100ms**, el servidor otorga el asiento a la terminal con **menor ID** (mayor prioridad). Las demás reciben `OCUPADO`.

Esto se implementa dentro del `lock`:
1. Se registra la solicitud con timestamp.
2. Se revisan solicitudes dentro de la ventana temporal.
3. Gana el menor `terminalId`.

### 4. Manejo de Errores

**Servidor:**
- Cada cliente se atiende en su propio `Thread`.
- Si un cliente se desconecta abruptamente, el `catch (IOException)` captura el error.
- El servidor sigue funcionando para los demás clientes.
- El bloque `finally` garantiza la limpieza de recursos (`Close()`).

**Cliente:**
- Si `ReadLine()` retorna `null`, significa que el servidor cerró la conexión.
- Se muestra un mensaje descriptivo y se termina limpiamente.
- `SocketException` se captura si no se puede conectar al servidor.

---

## Diagrama de Hilos en el Servidor

```
Hilo Principal
│
├── Bucle: AcceptTcpClient() [BLOQUEA esperando conexión]
│   │
│   ├── Conexión T1 → Thread "Terminal-T1" → ManejarCliente()
│   │                                         ├── ReadLine() [BLOQUEA]
│   │                                         ├── autobus.VenderAsiento() ← lock
│   │                                         └── WriteLine() [respuesta]
│   │
│   ├── Conexión T2 → Thread "Terminal-T2" → ManejarCliente()
│   │                                         ├── ReadLine()
│   │                                         ├── autobus.VenderAsiento() ← lock (espera si T1 está dentro)
│   │                                         └── WriteLine()
│   │
│   ├── Conexión T3 → Thread "Terminal-T3" → ManejarCliente()
│   └── Conexión T4 → Thread "Terminal-T4" → ManejarCliente()
│
└── Ctrl+C → ManejarCierre() → ObtenerResumenFinal() → Exit
```

---

## Tecnologías Utilizadas

| Componente       | Tecnología          |
|------------------|---------------------|
| Red              | `TcpListener`, `TcpClient` |
| Hilos            | `System.Threading.Thread` |
| Sincronización   | `lock (Monitor)`    |
| E/S              | `StreamReader`, `StreamWriter` |
| Serialización    | Protocolo texto simple (`\n`) |
