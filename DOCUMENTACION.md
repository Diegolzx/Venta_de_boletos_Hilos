# Documentación Técnica: Sistema de Venta Distribuida de Boletos de Autobús

## Índice

1. [Introducción](#1-introducción)
2. [Arquitectura del Sistema](#2-arquitectura-del-sistema)
3. [Estructura del Proyecto](#3-estructura-del-proyecto)
4. [Protocolo de Comunicación](#4-protocolo-de-comunicación)
5. [Servidor (`Servidor/Program.cs`)](#5-servidor-servidorprogramcs)
   - 5.1 Inicialización y escucha TCP
   - 5.2 Aceptación de clientes con hilos
   - 5.3 Manejo de comandos por cliente
   - 5.4 Cierre limpio del servidor
6. [Clase Autobus (`Servidor/Autobus.cs`)](#6-clase-autobus-servidorautobucs)
   - 6.1 Estado compartido
   - 6.2 Exclusión mutua con `lock`
   - 6.3 Condición de carrera (Race Condition)
   - 6.4 Venta con prioridad de terminales
   - 6.5 Venta de asiento aleatorio
   - 6.6 Consulta de resumen
7. [Cliente (`Cliente/Program.cs`)](#7-cliente-clienteprogramcs)
   - 7.1 Conexión al servidor
   - 7.2 Bucle automático de compra
   - 7.3 Manejo de conflictos y prioridad
   - 7.4 Visualización del autobús
8. [Aplicación de Hilos (Threads)](#8-aplicación-de-hilos-threads)
9. [Sincronización y Exclusión Mutua](#9-sincronización-y-exclusión-mutua)
10. [Diagrama de Flujo del Sistema](#10-diagrama-de-flujo-del-sistema)
11. [Conceptos Clave Aplicados](#11-conceptos-clave-aplicados)

---

## 1. Introducción

Este proyecto implementa un **sistema distribuido de venta de boletos de autobús** desarrollado en **C# con .NET 9.0**. El sistema simula un escenario real donde múltiples terminales de venta (clientes) compiten por vender los **40 asientos** de un autobús, comunicándose con un servidor central a través de **sockets TCP** en una red local WiFi.

El objetivo principal del proyecto es demostrar la aplicación práctica de:

- **Hilos (Threads)**: Para atender múltiples clientes de forma concurrente.
- **Exclusión mutua (`lock`)**: Para evitar condiciones de carrera al vender asientos.
- **Comunicación por sockets TCP**: Para conectar clientes y servidor en red.
- **Prioridad de procesos**: Para resolver conflictos cuando dos terminales quieren el mismo asiento.

---

## 2. Arquitectura del Sistema

El sistema sigue una arquitectura **cliente-servidor**:

```
Red WiFi Local
┌─────────────┐           ┌──────────────────────────────┐
│ Cliente T1  │◄─── TCP ──►                              │
├─────────────┤           │   SERVIDOR CENTRAL           │
│ Cliente T2  │◄─── TCP ──►   Puerto 5000                │
├─────────────┤           │                              │
│ Cliente T3  │◄─── TCP ──►   ┌────────────────────────┐ │
├─────────────┤           │   │  Autobus (40 asientos) │ │
│ Cliente T4  │◄─── TCP ──►   │  lock (exclusión mutua)│ │
└─────────────┘           │   └────────────────────────┘ │
                          └──────────────────────────────┘
```

- **Servidor Central**: Corre en una laptop, escucha en el puerto 5000, mantiene el estado del autobús y garantiza la exclusión mutua.
- **Clientes (Terminales T1–T4)**: Cada uno corre en una laptop diferente, se conecta al servidor y compra boletos automáticamente cada 5 segundos.

---

## 3. Estructura del Proyecto

```
Venta_Boletos/
├── Venta_Boletos.sln          ← Archivo de solución de Visual Studio
├── Servidor/
│   └── Servidor/
│       ├── Servidor.csproj    ← Proyecto del servidor
│       ├── Program.cs         ← Lógica principal del servidor TCP
│       └── Autobus.cs         ← Clase con estado compartido y sincronización
├── Cliente/
│   └── Cliente/
│       ├── Cliente.csproj     ← Proyecto del cliente
│       └── Program.cs         ← Lógica del cliente automático
├── Publicar/                  ← Ejecutables publicados
│   ├── Cliente/
│   └── Servidor/
└── README.md                  ← Instrucciones de uso
```

---

## 4. Protocolo de Comunicación

El servidor y los clientes se comunican mediante un protocolo de texto simple sobre TCP. Cada mensaje es una línea de texto terminada en `\n`.

| Comando del cliente       | Respuesta del servidor | Descripción                              |
|---------------------------|------------------------|------------------------------------------|
| `VENDER_RANDOM`           | `OK:n` o `NO_QUEDAN`  | Vende el siguiente asiento disponible    |
| `VENDER_ESPECIFICO:n`     | `OK:n`, `OCUPADO` o `INVALIDO` | Intenta vender el asiento número `n` |
| `RESUMEN`                 | `VENDIDOS:[1,3,5,...]` | Lista de asientos vendidos               |
| `SALIR`                   | `ADIÓS`                | Cierra la conexión                       |

**Ejemplo de conversación TCP:**
```
Cliente → Servidor:  RESUMEN
Servidor → Cliente:  VENDIDOS:[1,3,5]
Cliente → Servidor:  VENDER_ESPECIFICO:7
Servidor → Cliente:  OK:7
Cliente → Servidor:  SALIR
Servidor → Cliente:  ADIÓS
```

---

## 5. Servidor (`Servidor/Program.cs`)

### 5.1 Inicialización y escucha TCP

El servidor crea un `TcpListener` que escucha en **todas las interfaces de red** (`IPAddress.Any`) para que clientes de otras laptops en la red WiFi puedan conectarse.

```csharp
// Instancia compartida del autobús (40 asientos)
private static Autobus autobus = new Autobus();

// Listener TCP en puerto 5000
private static TcpListener servidor = null;

// Contador de terminales conectadas (para asignar IDs)
private static int contadorTerminales = 0;
private static readonly object contadorLock = new object();

static void Main(string[] args)
{
    int puerto = 5000;

    // Escucha en TODAS las interfaces (0.0.0.0)
    servidor = new TcpListener(IPAddress.Any, puerto);
    servidor.Start();
    // ...
}
```

**Puntos clave:**
- `IPAddress.Any` permite que el servidor sea accesible desde cualquier interfaz de red (WiFi, Ethernet, etc.).
- La instancia `autobus` es **compartida entre todos los hilos**. Es el recurso que se debe proteger con exclusión mutua.
- `contadorTerminales` se usa para asignar un ID único a cada terminal que se conecta.

---

### 5.2 Aceptación de clientes con hilos (Thread por cliente)

Esta es la parte más importante en cuanto a **aplicación de hilos**. Por cada cliente que se conecta, el servidor crea un **hilo independiente** para atenderlo:

```csharp
while (ejecutando)
{
    // AcceptTcpClient() BLOQUEA hasta que un cliente se conecte
    TcpClient cliente = servidor.AcceptTcpClient();

    // Asignar ID de terminal (1, 2, 3, 4...)
    int terminalId;
    lock (contadorLock)
    {
        contadorTerminales++;
        terminalId = contadorTerminales;
    }

    // Crear hilo para este cliente
    Thread hiloCliente = new Thread(() => ManejarCliente(cliente, terminalId));
    hiloCliente.IsBackground = true; // Se cierra con el proceso principal
    hiloCliente.Name = "Terminal-T" + terminalId;
    hiloCliente.Start();
}
```

**¿Por qué se usa un hilo por cliente?**

Sin hilos, el servidor solo podría atender un cliente a la vez. `AcceptTcpClient()` es una operación **bloqueante**: el servidor se detiene completamente hasta que llegue un cliente. Si atendiera al cliente en el mismo hilo, no podría aceptar más conexiones hasta terminar con el primero.

Con la creación de un `Thread` por cada cliente:
1. El **hilo principal** queda libre para seguir aceptando nuevas conexiones.
2. Cada **hilo hijo** se encarga exclusivamente de la comunicación con su cliente.
3. Todos los hilos se ejecutan **concurrentemente**, permitiendo que 4 terminales operen al mismo tiempo.

**`IsBackground = true`**: Marca el hilo como hilo de fondo. Esto garantiza que cuando el proceso principal termine (Ctrl+C), los hilos de los clientes también se cierren automáticamente, sin dejar el proceso colgado.

**`lock (contadorLock)`**: Incluso el simple acto de incrementar un contador necesita protección, ya que dos conexiones simultáneas podrían obtener el mismo ID si ambos hilos leen el mismo valor de `contadorTerminales` antes de incrementarlo.

---

### 5.3 Manejo de comandos por cliente

El método `ManejarCliente` se ejecuta en el hilo dedicado a cada cliente. Lee comandos línea por línea y los despacha al método correspondiente de la clase `Autobus`:

```csharp
private static void ManejarCliente(TcpClient cliente, int terminalId)
{
    StreamReader lector = null;
    StreamWriter escritor = null;

    try
    {
        NetworkStream stream = cliente.GetStream();
        lector = new StreamReader(stream);
        escritor = new StreamWriter(stream);
        escritor.AutoFlush = true;

        // Enviar mensaje de bienvenida
        escritor.WriteLine("BIENVENIDO_SERVIDOR");
        escritor.WriteLine("TERMINAL_ID:" + terminalId);

        // Bucle de comandos
        string comando;
        while ((comando = lector.ReadLine()) != null)
        {
            comando = comando.Trim();
            if (string.IsNullOrEmpty(comando)) continue;

            string respuesta;

            // Despachar comando
            if (comando == "VENDER_RANDOM")
            {
                respuesta = autobus.VenderSiguienteDisponible(terminalId);
            }
            else if (comando.StartsWith("VENDER_ESPECIFICO:"))
            {
                string parteNumero = comando.Substring("VENDER_ESPECIFICO:".Length);
                int numero;
                if (int.TryParse(parteNumero, out numero))
                {
                    respuesta = autobus.VenderAsientoEspecifico(numero, terminalId);
                }
                else
                {
                    respuesta = "INVALIDO";
                }
            }
            else if (comando == "RESUMEN")
            {
                respuesta = autobus.ObtenerVendidos();
            }
            else if (comando == "SALIR")
            {
                escritor.WriteLine("ADIÓS");
                break;
            }
            else
            {
                respuesta = "COMANDO_DESCONOCIDO";
            }

            escritor.WriteLine(respuesta);
        }
    }
    catch (IOException ex)
    {
        // Desconexión inesperada del cliente
        // Solo afecta a ESTE cliente, el servidor sigue funcionando
        Console.WriteLine("[T{0}] Desconexión inesperada: {1}", terminalId, ex.Message);
    }
    finally
    {
        // Limpieza de recursos
        try
        {
            if (lector != null) lector.Close();
            if (escritor != null) escritor.Close();
            if (cliente != null) cliente.Close();
        }
        catch (Exception) { }
    }
}
```

**Puntos clave:**

- **`StreamReader` / `StreamWriter`**: Encapsulan el `NetworkStream` del socket TCP para facilitar la lectura y escritura de líneas de texto.
- **`AutoFlush = true`**: Cada `WriteLine` envía inmediatamente los datos al cliente, sin esperar a llenar un buffer.
- **`lector.ReadLine()`**: Lee hasta encontrar un `\n`. Si el cliente se desconecta, retorna `null` y el bucle termina.
- **Aislamiento de errores**: Si un cliente se desconecta repentinamente, la excepción `IOException` solo mata **este hilo**, no el servidor ni los demás clientes.

---

### 5.4 Cierre limpio del servidor

Al presionar `Ctrl+C`, el servidor muestra un resumen final de todas las ventas antes de terminar:

```csharp
Console.CancelKeyPress += new ConsoleCancelEventHandler(ManejarCierre);

private static void ManejarCierre(object sender, ConsoleCancelEventArgs args)
{
    args.Cancel = true; // Evitar cierre abrupto
    ejecutando = false;

    Console.WriteLine(autobus.ObtenerResumenFinal());

    try { if (servidor != null) servidor.Stop(); }
    catch (Exception) { }

    Environment.Exit(0);
}
```

`args.Cancel = true` intercepta la señal de cierre para que el proceso no muera inmediatamente —se da tiempo de mostrar el resumen antes de salir.

---

## 6. Clase Autobus (`Servidor/Autobus.cs`)

Esta clase es el **corazón del sistema** en cuanto a concurrencia. Almacena el estado compartido del autobús y protege todas las operaciones con `lock` para evitar condiciones de carrera.

### 6.1 Estado compartido

```csharp
public class Autobus
{
    // Array de 40 posiciones: true = vendido, false = libre
    private readonly bool[] asientosVendidos = new bool[40];

    // Contador de asientos vendidos
    private int totalVendidos = 0;

    // Qué terminal vendió cada asiento (clave: asiento 1-40, valor: terminalId)
    private readonly Dictionary<int, int> asientoTerminal = new Dictionary<int, int>();

    // Objeto de sincronización
    private readonly object lockObj = new object();

    // Solicitudes pendientes para prioridad
    private readonly Dictionary<int, List<SolicitudVenta>> solicitudesPendientes
        = new Dictionary<int, List<SolicitudVenta>>();

    // Ventana temporal para considerar solicitudes "simultáneas"
    private const int VENTANA_PRIORIDAD_MS = 100;
}
```

Todos estos campos son **compartidos entre todos los hilos** del servidor (cada hilo de un cliente invoca métodos de esta misma instancia). Sin protección, las lecturas y escrituras concurrentes podrían corromperse.

---

### 6.2 Exclusión mutua con `lock`

El mecanismo fundamental de sincronización es el `lock` de C#:

```csharp
private readonly object lockObj = new object();

lock (lockObj)
{
    // Solo UN hilo a la vez puede ejecutar este bloque.
    // Los demás hilos ESPERAN hasta que el primer hilo salga.
}
```

**¿Cómo funciona el `lock`?**

1. Cuando un hilo llega a `lock(lockObj)`, intenta adquirir el "monitor" del objeto `lockObj`.
2. Si ningún otro hilo lo tiene, entra al bloque inmediatamente.
3. Si otro hilo ya lo tiene, el hilo actual **se bloquea** y espera.
4. Cuando el hilo que está dentro sale del bloque `lock`, libera el monitor.
5. Uno de los hilos en espera adquiere el monitor y entra.

Esto garantiza que la verificación "¿está libre?" y la acción "marcar como vendido" ocurran como una **operación atómica indivisible**.

---

### 6.3 Condición de carrera (Race Condition)

**¿Qué es una condición de carrera?**

Es un error que ocurre cuando dos o más hilos acceden a un recurso compartido sin sincronización, y el resultado depende del orden impredecible de ejecución.

**Ejemplo concreto sin `lock` (ERROR):**

```
Tiempo   Hilo A (T1)                    Hilo B (T2)
──────   ──────────────                  ──────────────
  t1     lee asiento 5 → libre ✓
  t2                                     lee asiento 5 → libre ✓  (¡aún no se marcó!)
  t3     marca asiento 5 vendido ✓
  t4                                     marca asiento 5 vendido ✓  ← ¡DOBLE VENTA!
```

Ambos hilos leyeron que el asiento 5 estaba libre porque la lectura de B ocurrió antes de la escritura de A. Resultado: **dos terminales creen que compraron el mismo asiento**.

**Con `lock` (CORRECTO):**

```
Tiempo   Hilo A (T1)                    Hilo B (T2)
──────   ──────────────                  ──────────────
  t1     ENTRA al lock
  t2     lee asiento 5 → libre ✓        ESPERA (lock ocupado)
  t3     marca asiento 5 vendido ✓      ESPERA (lock ocupado)
  t4     SALE del lock
  t5                                     ENTRA al lock
  t6                                     lee asiento 5 → VENDIDO ✗
  t7                                     retorna "OCUPADO"
  t8                                     SALE del lock
```

Hilo B espera a que Hilo A termine. Cuando B entra, lee el estado **ya actualizado** y sabe que el asiento está vendido.

---

### 6.4 Venta con prioridad de terminales

Cuando dos terminales solicitan el mismo asiento dentro de una ventana temporal de 100ms, gana la terminal con **menor ID** (mayor prioridad): T1 > T2 > T3 > T4.

```csharp
private class SolicitudVenta
{
    public int TerminalId { get; set; }
    public DateTime Timestamp { get; set; }
    public ManualResetEvent Evento { get; set; }
    public string Resultado { get; set; }
}

public string VenderAsientoEspecifico(int numero, int terminalId)
{
    if (numero < 1 || numero > 40)
        return "INVALIDO";

    lock (lockObj)
    {
        int indice = numero - 1;

        // Si ya está vendido, no importa la prioridad
        if (asientosVendidos[indice])
            return "OCUPADO";

        // Registrar solicitud para este asiento
        if (!solicitudesPendientes.ContainsKey(numero))
            solicitudesPendientes[numero] = new List<SolicitudVenta>();

        solicitudesPendientes[numero].Add(new SolicitudVenta
        {
            TerminalId = terminalId,
            Timestamp = DateTime.Now
        });

        // Limpiar solicitudes antiguas (fuera de la ventana temporal)
        DateTime ahora = DateTime.Now;
        solicitudesPendientes[numero].RemoveAll(
            s => (ahora - s.Timestamp).TotalMilliseconds > VENTANA_PRIORIDAD_MS
                 && s.TerminalId != terminalId);

        // LÓGICA DE PRIORIDAD: gana el menor ID
        int menorTerminal = terminalId;
        foreach (SolicitudVenta solicitud in solicitudesPendientes[numero])
        {
            if (solicitud.TerminalId < menorTerminal)
                menorTerminal = solicitud.TerminalId;
        }

        // Si esta terminal NO tiene la mayor prioridad, pierde
        if (menorTerminal != terminalId)
            return "OCUPADO";

        // VENTA EXITOSA
        asientosVendidos[indice] = true;
        totalVendidos++;
        asientoTerminal[numero] = terminalId;
        solicitudesPendientes.Remove(numero);

        return "OK:" + numero;
    }
}
```

**Mecanismo de prioridad paso a paso:**

1. La terminal registra su solicitud con un `Timestamp`.
2. Dentro del `lock`, se buscan otras solicitudes para el **mismo asiento** que hayan llegado dentro de los últimos 100ms.
3. Si hay otra terminal con ID más bajo (mayor prioridad), la solicitud actual es rechazada con `"OCUPADO"`.
4. Si la terminal actual tiene prioridad máxima entre las solicitudes recientes, la venta se ejecuta.

---

### 6.5 Venta de asiento aleatorio (`VenderSiguienteDisponible`)

Busca linealmente el primer asiento libre y lo vende:

```csharp
public string VenderSiguienteDisponible(int terminalId)
{
    lock (lockObj)
    {
        for (int i = 0; i < 40; i++)
        {
            if (!asientosVendidos[i])
            {
                asientosVendidos[i] = true;
                totalVendidos++;
                int numero = i + 1;
                asientoTerminal[numero] = terminalId;
                return "OK:" + numero;
            }
        }
        return "NO_QUEDAN";
    }
}
```

Todo el recorrido y la venta ocurren dentro del `lock`, garantizando que dos hilos no obtengan el mismo "siguiente disponible".

---

### 6.6 Consulta de resumen

Incluso las **lecturas** están protegidas con `lock` para garantizar consistencia:

```csharp
public string ObtenerVendidos()
{
    lock (lockObj)
    {
        List<string> vendidos = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            if (asientosVendidos[i])
                vendidos.Add((i + 1).ToString());
        }
        return "VENDIDOS:[" + string.Join(",", vendidos) + "]";
    }
}
```

**¿Por qué usar `lock` en lecturas?** Sin el `lock`, un hilo podría estar leyendo la lista mientras otro hilo está en medio de una venta. Podría leer un estado inconsistente (por ejemplo, `totalVendidos` ya incrementado pero el asiento aún no marcado como vendido).

---

## 7. Cliente (`Cliente/Program.cs`)

### 7.1 Conexión al servidor

El cliente se conecta al servidor usando `TcpClient` y establece los streams de lectura/escritura:

```csharp
TcpClient cliente = new TcpClient();
cliente.Connect(ipServidor, puerto);

NetworkStream stream = cliente.GetStream();
StreamReader lector = new StreamReader(stream);
StreamWriter escritor = new StreamWriter(stream);
escritor.AutoFlush = true;

// Recibir mensajes de bienvenida del servidor
string bienvenida = lector.ReadLine();  // "BIENVENIDO_SERVIDOR"
string terminalInfo = lector.ReadLine(); // "TERMINAL_ID:1"

// Extraer ID de terminal
string terminalId = "?";
if (terminalInfo.StartsWith("TERMINAL_ID:"))
    terminalId = terminalInfo.Substring("TERMINAL_ID:".Length);
```

El servidor asigna automáticamente el ID de terminal en orden de conexión. La primera terminal que se conecta es T1 (máxima prioridad), la segunda es T2, etc.

---

### 7.2 Bucle automático de compra

El cliente opera de forma completamente automática: cada **5 segundos** intenta comprar un boleto sin intervención del usuario.

```csharp
int compraNumero = 0;
bool autobusLleno = false;

while (!autobusLleno)
{
    compraNumero++;

    // PASO 1: Consultar asientos disponibles
    escritor.WriteLine("RESUMEN");
    string resumen = lector.ReadLine();

    List<int> asientosLibres = ObtenerAsientosLibres(resumen);
    if (asientosLibres.Count == 0)
    {
        autobusLleno = true;
        break;
    }

    // PASO 2: Elegir asiento aleatorio de los disponibles
    int asientoElegido = asientosLibres[random.Next(asientosLibres.Count)];

    // PASO 3: Enviar solicitud de compra
    escritor.WriteLine("VENDER_ESPECIFICO:" + asientoElegido);
    string respuesta = lector.ReadLine();

    // PASO 4: Procesar respuesta
    if (respuesta.StartsWith("OK:"))
    {
        // ¡Compra exitosa!
    }
    else if (respuesta == "OCUPADO")
    {
        // Otra terminal con mayor prioridad lo compró
        // Intentar con VENDER_RANDOM como respaldo
        escritor.WriteLine("VENDER_RANDOM");
        respuesta = lector.ReadLine();
    }

    // PASO 5: Mostrar estado actual del autobús
    escritor.WriteLine("RESUMEN");
    string estadoActual = lector.ReadLine();
    MostrarResumen(estadoActual, terminalId);

    // PASO 6: Esperar 5 segundos
    if (!autobusLleno)
        Thread.Sleep(5000);
}
```

**El ciclo de cada compra:**

1. **Consultar estado**: Pide `RESUMEN` para saber qué asientos están libres y evitar intentar comprar uno ya vendido.
2. **Elegir aleatoriamente**: Selecciona un asiento al azar de los disponibles.
3. **Enviar solicitud**: Pide al servidor `VENDER_ESPECIFICO:n`.
4. **Manejar conflictos**: Si el servidor responde `OCUPADO` (otra terminal ganó por prioridad), intenta con `VENDER_RANDOM` como respaldo.
5. **Mostrar estado visual**: Imprime el mapa del autobús en consola.
6. **Esperar**: `Thread.Sleep(5000)` pausa 5 segundos antes del siguiente intento.

---

### 7.3 Manejo de conflictos y prioridad

Cuando el servidor responde `OCUPADO`, significa que otra terminal con mayor prioridad compró el mismo asiento en la ventana de 100ms. El cliente maneja esto con un mecanismo de respaldo:

```csharp
if (respuesta == "OCUPADO")
{
    Console.WriteLine("  [CONFLICTO] Asiento #{0} fue tomado por otra terminal",
        asientoElegido);
    Console.WriteLine("              (terminal con mayor prioridad ganó)");

    // Intentar con VENDER_RANDOM para obtener cualquier asiento libre
    escritor.WriteLine("VENDER_RANDOM");
    respuesta = lector.ReadLine();

    if (respuesta.StartsWith("OK:"))
    {
        string asiento = respuesta.Substring(3);
        Console.WriteLine("  ¡VENTA EXITOSA (alternativo)! Asiento #{0}", asiento);
    }
    else if (respuesta == "NO_QUEDAN")
    {
        autobusLleno = true;
    }
}
```

Esto asegura que un cliente **nunca se queda sin comprar** si hay asientos disponibles. Si su primera opción fue tomada, obtiene cualquier otro asiento libre.

---

### 7.4 Visualización del autobús

El cliente muestra un mapa visual del autobús en la consola:

```csharp
private static void MostrarResumen(string respuesta, string terminalId)
{
    bool[] vendidos = new bool[40];
    int totalVendidos = 0;

    // Parsear la respuesta VENDIDOS:[1,3,5,...] del servidor
    if (respuesta.StartsWith("VENDIDOS:[") && respuesta.EndsWith("]"))
    {
        string lista = respuesta.Substring("VENDIDOS:[".Length,
            respuesta.Length - "VENDIDOS:[".Length - 1);

        if (!string.IsNullOrEmpty(lista))
        {
            string[] numeros = lista.Split(',');
            foreach (string numStr in numeros)
            {
                int num;
                if (int.TryParse(numStr.Trim(), out num) && num >= 1 && num <= 40)
                {
                    vendidos[num - 1] = true;
                    totalVendidos++;
                }
            }
        }
    }

    // Mostrar mapa visual: [XX] = vendido, [ .] = libre
    for (int i = 0; i < 40; i++)
    {
        if (vendidos[i])
            Console.Write("[{0:D2}]", i + 1);  // Vendido
        else
            Console.Write("[ .]");              // Libre

        if ((i + 1) % 10 == 0 && i < 39)
            Console.WriteLine();
    }
}
```

**Salida de ejemplo:**
```
  ┌─── ESTADO DEL AUTOBÚS (T1) ───────────────────┐
  │  [01][02][ .][ .][05][ .][ .][08][ .][ .]
  │  [ .][ .][13][ .][ .][ .][ .][ .][ .][ .]
  │  [ .][ .][ .][ .][ .][ .][ .][ .][ .][ .]
  │  [ .][ .][ .][ .][ .][ .][ .][ .][ .][ .]
  │  [##] = Vendido    [ .] = Libre
  │  Vendidos: 5/40    Libres: 35/40
  └─────────────────────────────────────────────────┘
```

---

## 8. Aplicación de Hilos (Threads)

### ¿Dónde se usan los hilos?

| Componente | Hilo | Función |
|------------|------|---------|
| Servidor - Hilo principal | `Main()` | Acepta conexiones TCP en un bucle |
| Servidor - Hilo por cliente | `Thread hiloCliente` | Atiende los comandos de UN cliente |
| Cliente | Hilo principal | Ejecuta el bucle de compra automática |
| Cliente | `Thread.Sleep(5000)` | Pausa entre compras (usa el hilo actual) |

### Creación de hilos en el servidor

```csharp
// Por cada cliente se crea un hilo dedicado
Thread hiloCliente = new Thread(() => ManejarCliente(cliente, terminalId));
hiloCliente.IsBackground = true;
hiloCliente.Name = "Terminal-T" + terminalId;
hiloCliente.Start();
```

- **`new Thread(() => ...)`**: Crea un nuevo hilo con una lambda que ejecuta `ManejarCliente`.
- **`IsBackground = true`**: Hilo de fondo que se cierra automáticamente al terminar el proceso.
- **`hiloCliente.Start()`**: Inicia la ejecución concurrente del hilo.

### Diagrama de hilos en ejecución

```
Servidor (Proceso)
├── Hilo Principal ─────── AcceptTcpClient() → bucle infinito
├── Hilo "Terminal-T1" ── ManejarCliente(cliente1, 1) → lee/escribe
├── Hilo "Terminal-T2" ── ManejarCliente(cliente2, 2) → lee/escribe
├── Hilo "Terminal-T3" ── ManejarCliente(cliente3, 3) → lee/escribe
└── Hilo "Terminal-T4" ── ManejarCliente(cliente4, 4) → lee/escribe
```

Todos los hilos acceden a la misma instancia de `Autobus`. El `lock` dentro de `Autobus` serializa los accesos para evitar corrupciones.

---

## 9. Sincronización y Exclusión Mutua

### Resumen de mecanismos de sincronización usados

| Mecanismo | Ubicación | Propósito |
|-----------|-----------|-----------|
| `lock(lockObj)` | `Autobus.VenderAsientoEspecifico()` | Evita doble venta del mismo asiento |
| `lock(lockObj)` | `Autobus.VenderSiguienteDisponible()` | Evita que dos hilos obtengan el mismo "siguiente" |
| `lock(lockObj)` | `Autobus.ObtenerVendidos()` | Lectura consistente del estado |
| `lock(lockObj)` | `Autobus.ObtenerResumenFinal()` | Resumen consistente al cierre |
| `lock(contadorLock)` | `Program.Main()` (servidor) | Asignación única de IDs de terminal |

### ¿Qué pasaría sin exclusión mutua?

1. **Doble venta**: Dos terminales compran el mismo asiento.
2. **Contador inconsistente**: `totalVendidos` podría no coincidir con los asientos realmente marcados.
3. **ID duplicados**: Dos clientes podrían recibir el mismo `terminalId`.
4. **Lectura sucia**: Un cliente podría obtener un resumen parcialmente actualizado.

---

## 10. Diagrama de Flujo del Sistema

### Flujo general

```
┌──────────────────┐
│  INICIAR SERVIDOR│
│  (puerto 5000)   │
└────────┬─────────┘
         ▼
┌──────────────────┐     ┌──────────────────────────────┐
│ Esperar conexión │◄────│  CLIENTE se conecta por TCP  │
│ (AcceptTcpClient)│     └──────────────────────────────┘
└────────┬─────────┘
         ▼
┌──────────────────┐     ┌──────────────────────────────┐
│ Crear HILO nuevo │────►│  ManejarCliente(cliente, id) │
│ para el cliente  │     │  (ejecuta en hilo propio)    │
└────────┬─────────┘     └──────────┬───────────────────┘
         │                          ▼
         │               ┌──────────────────────────────┐
         │               │  Leer comando del cliente    │
         │               │  (VENDER, RESUMEN, SALIR)    │
         │               └──────────┬───────────────────┘
         │                          ▼
         │               ┌──────────────────────────────┐
         │               │  Ejecutar en Autobus con     │
         │               │  LOCK (exclusión mutua)      │
         │               └──────────┬───────────────────┘
         │                          ▼
         │               ┌──────────────────────────────┐
         │               │  Enviar respuesta al cliente │
         │               └──────────┬───────────────────┘
         │                          │
         ▼                          ▼
  Volver a esperar          Volver a leer comando
  otra conexión             (bucle hasta SALIR)
```

### Flujo del cliente (automático)

```
┌───────────────────┐
│ Conectar a IP:5000│
└────────┬──────────┘
         ▼
┌───────────────────┐
│ Recibir TERMINAL_ │
│ ID del servidor   │
└────────┬──────────┘
         ▼
┌───────────────────┐◄──────────────────────────┐
│ Pedir RESUMEN     │                           │
└────────┬──────────┘                           │
         ▼                                      │
    ¿Hay asientos?──── NO ──► FIN               │
         │ SÍ                                   │
         ▼                                      │
┌───────────────────┐                           │
│ Elegir asiento    │                           │
│ aleatorio libre   │                           │
└────────┬──────────┘                           │
         ▼                                      │
┌───────────────────┐                           │
│ VENDER_ESPECIFICO │                           │
└────────┬──────────┘                           │
         ▼                                      │
    ¿Respuesta?                                 │
    ├── OK:n     ► Compra exitosa               │
    ├── OCUPADO  ► Intentar VENDER_RANDOM       │
    └── NO_QUEDAN ► FIN                         │
         │                                      │
         ▼                                      │
┌───────────────────┐                           │
│ Mostrar mapa del  │                           │
│ autobús           │                           │
└────────┬──────────┘                           │
         ▼                                      │
┌───────────────────┐                           │
│ Thread.Sleep(5000)│───────────────────────────┘
│ (esperar 5 seg)   │
└───────────────────┘
```

---

## 11. Conceptos Clave Aplicados

### 11.1 Programación Concurrente
El servidor atiende múltiples clientes **simultáneamente** mediante la creación de un hilo (`Thread`) por cada conexión TCP. Esto permite que hasta 4 terminales operen al mismo tiempo sin bloquearse entre sí.

### 11.2 Exclusión Mutua (Mutual Exclusion)
El recurso compartido (los 40 asientos del autobús) está protegido con `lock` de C#. Solo un hilo a la vez puede verificar y modificar el estado de los asientos, eliminando las condiciones de carrera.

### 11.3 Condición de Carrera (Race Condition)
Sin `lock`, dos hilos podrían leer que un asiento está libre y ambos venderlo → **doble venta**. El `lock` serializa las operaciones, haciendo que la lectura + escritura sea atómica.

### 11.4 Sección Crítica (Critical Section)
Es el bloque de código dentro de `lock(lockObj) { ... }` donde se accede al recurso compartido. Solo un hilo a la vez puede estar dentro de la sección crítica.

### 11.5 Comunicación por Sockets TCP
La comunicación entre clientes y servidor se realiza mediante sockets TCP, garantizando la entrega confiable y ordenada de mensajes a través de la red WiFi.

### 11.6 Prioridad de Procesos
Cuando dos terminales compiten por el mismo asiento en una ventana de 100ms, gana la de menor ID (T1 > T2 > T3 > T4). Esto demuestra el concepto de **prioridad en la planificación de procesos**.

### 11.7 Hilos de Fondo (Background Threads)
Los hilos de clientes se configuran como `IsBackground = true`, lo que garantiza que se terminen automáticamente cuando el proceso principal cierra, evitando que el servidor quede colgado.

### 11.8 Manejo de Recursos (Patrón try/finally)
Todos los streams y sockets se cierran en bloques `finally`, garantizando la liberación de recursos de red incluso si ocurre una excepción inesperada.

---

> **Nota**: Este sistema fue desarrollado como proyecto educativo para demostrar conceptos de programación concurrente, sincronización e hilos en un escenario práctico de venta de boletos distribuida.
