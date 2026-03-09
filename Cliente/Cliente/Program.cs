// ============================================================================
// CLIENTE TERMINAL - Sistema de Venta Distribuida de Boletos de Autobús
// ============================================================================
//
// Este cliente:
//   1. Se conecta al servidor central por TCP (IP + puerto 5000).
//   2. AUTOMÁTICAMENTE compra un boleto cada 5 segundos.
//   3. Consulta asientos disponibles antes de cada compra para evitar
//      intentar comprar asientos ya vendidos.
//   4. Si dos terminales quieren el mismo asiento, el servidor resuelve
//      por PRIORIDAD (T1 > T2 > T3 > T4).
//   5. Se detiene cuando el autobús está LLENO (40/40 vendidos).
//
// PROTOCOLO:
//   VENDER_RANDOM         → El servidor asigna un asiento aleatorio disponible
//   VENDER_ESPECIFICO:n   → Intenta comprar el asiento número n
//   RESUMEN               → Obtiene lista de asientos vendidos
//   SALIR                 → Cierra la conexión
//
// USO:
//   dotnet run                        → Conecta a 127.0.0.1:5000
//   dotnet run -- 192.168.1.100       → Conecta a IP específica, puerto 5000
//   dotnet run -- 192.168.1.100 5000  → Conecta a IP y puerto específicos
//
// SIMULTANEIDAD:
//   Cada terminal compra automáticamente cada 5 segundos.
//   Cuando 2+ terminales eligen el mismo asiento en la ventana de 100ms,
//   el servidor da prioridad: T1 > T2 > T3 > T4.
//   La terminal que pierde recibe "OCUPADO" y reintenta con otro asiento.
//
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Cliente
{
    class Program
    {
        // Generador de números aleatorios para elegir asientos
        private static Random random = new Random();

        static void Main(string[] args)
        {
            // ─── Configurar IP y puerto del servidor ───
            string ipServidor = "127.0.0.1"; // Por defecto: misma máquina
            int puerto = 5000;

            if (args.Length >= 1)
            {
                ipServidor = args[0];
            }
            else
            {
                // Si no se pasó IP como argumento, pedirla interactivamente
                Console.WriteLine("╔══════════════════════════════════════════════════╗");
                Console.WriteLine("║   CLIENTE - VENTA DE BOLETOS DE AUTOBÚS         ║");
                Console.WriteLine("║   (Modo automático cada 5 segundos)             ║");
                Console.WriteLine("╚══════════════════════════════════════════════════╝");
                Console.WriteLine();
                Console.Write("  Ingrese la IP del servidor (o Enter para 127.0.0.1): ");
                string entrada = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(entrada))
                {
                    ipServidor = entrada.Trim();
                }
                Console.WriteLine();
            }
            if (args.Length >= 2)
            {
                int.TryParse(args[1], out puerto);
            }

            Console.WriteLine("╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║   CLIENTE - VENTA DE BOLETOS DE AUTOBÚS         ║");
            Console.WriteLine("║   MODO AUTOMÁTICO (compra cada 5 segundos)      ║");
            Console.WriteLine("╠══════════════════════════════════════════════════╣");
            Console.WriteLine("║ Conectando a: {0}:{1,-24}║", ipServidor, puerto);
            Console.WriteLine("╚══════════════════════════════════════════════════╝");
            Console.WriteLine();

            // ═══════════════════════════════════════════════════════
            // CONEXIÓN AL SERVIDOR
            // ═══════════════════════════════════════════════════════
            TcpClient cliente = null;
            StreamReader lector = null;
            StreamWriter escritor = null;

            try
            {
                cliente = new TcpClient();
                cliente.Connect(ipServidor, puerto);

                NetworkStream stream = cliente.GetStream();
                lector = new StreamReader(stream);
                escritor = new StreamWriter(stream);
                escritor.AutoFlush = true;

                // ─── Recibir mensajes de bienvenida ───
                string bienvenida = lector.ReadLine();
                string terminalInfo = lector.ReadLine();

                if (bienvenida == null || terminalInfo == null)
                {
                    Console.WriteLine("[ERROR] El servidor cerró la conexión inesperadamente.");
                    return;
                }

                // Extraer el ID de terminal asignado por el servidor
                string terminalId = "?";
                if (terminalInfo.StartsWith("TERMINAL_ID:"))
                {
                    terminalId = terminalInfo.Substring("TERMINAL_ID:".Length);
                }

                Console.WriteLine("  ╔═══════════════════════════════════════════════╗");
                Console.WriteLine("  ║  Conectado exitosamente al servidor.          ║");
                Console.WriteLine("  ║  Terminal asignada: T{0}                        ║", terminalId);
                Console.WriteLine("  ║  Prioridad: T1 > T2 > T3 > T4                ║");
                Console.WriteLine("  ║                                               ║");
                Console.WriteLine("  ║  Comprando automáticamente cada 5 segundos... ║");
                Console.WriteLine("  ║  Presione Ctrl+C para detener.                ║");
                Console.WriteLine("  ╚═══════════════════════════════════════════════╝");
                Console.WriteLine();

                // ═══════════════════════════════════════════════════════
                // BUCLE AUTOMÁTICO DE COMPRA
                //
                // Cada 5 segundos:
                //   1. Pide RESUMEN al servidor para saber qué asientos están libres.
                //   2. Elige un asiento aleatorio de los DISPONIBLES.
                //   3. Envía VENDER_ESPECIFICO:n al servidor.
                //   4. Si el servidor responde OCUPADO (otra terminal con mayor 
                //      prioridad lo compró), intenta con VENDER_RANDOM.
                //   5. Muestra el estado actualizado del autobús.
                //   6. Si NO_QUEDAN asientos, se detiene.
                //
                // SIMULTANEIDAD: Como las 4 terminales hacen esto al mismo
                // tiempo, es muy probable que 2+ terminales elijan el mismo
                // asiento. El servidor resuelve esto con PRIORIDAD (lock + 
                // ventana temporal de 100ms).
                // ═══════════════════════════════════════════════════════
                int compraNumero = 0;
                bool autobusLleno = false;

                while (!autobusLleno)
                {
                    compraNumero++;
                    Console.WriteLine("══════════════════════════════════════════════════");
                    Console.WriteLine("  [T{0}] Intento de compra #{1} - {2}",
                        terminalId, compraNumero, DateTime.Now.ToString("HH:mm:ss.fff"));
                    Console.WriteLine("══════════════════════════════════════════════════");

                    // ─── PASO 1: Consultar asientos disponibles ───
                    // Pedimos el RESUMEN para saber qué asientos están libres.
                    // Así evitamos intentar comprar uno que ya sabemos vendido.
                    escritor.WriteLine("RESUMEN");
                    string resumen = lector.ReadLine();
                    if (resumen == null)
                    {
                        Console.WriteLine("  [ERROR] Servidor desconectado.");
                        break;
                    }

                    // Parsear la lista de vendidos para obtener los libres
                    List<int> asientosLibres = ObtenerAsientosLibres(resumen);

                    if (asientosLibres.Count == 0)
                    {
                        Console.WriteLine("  ¡AUTOBÚS COMPLETAMENTE LLENO! (40/40)");
                        autobusLleno = true;
                        break;
                    }

                    Console.WriteLine("  Asientos disponibles: {0}", asientosLibres.Count);

                    // ─── PASO 2: Elegir asiento aleatorio de los disponibles ───
                    // Se elige aleatoriamente entre los que SABEMOS que están libres.
                    // NOTA: Otra terminal podría comprarlo en paralelo, por eso el
                    // servidor tiene el mecanismo de prioridad.
                    int asientoElegido = asientosLibres[random.Next(asientosLibres.Count)];
                    Console.WriteLine("  Intentando comprar asiento #{0}...", asientoElegido);

                    // ─── PASO 3: Enviar solicitud de compra ───
                    escritor.WriteLine("VENDER_ESPECIFICO:" + asientoElegido);
                    string respuesta = lector.ReadLine();
                    if (respuesta == null)
                    {
                        Console.WriteLine("  [ERROR] Servidor desconectado.");
                        break;
                    }

                    // ─── PASO 4: Procesar respuesta ───
                    if (respuesta.StartsWith("OK:"))
                    {
                        string asiento = respuesta.Substring(3);
                        Console.WriteLine("  ╔═══════════════════════════════════════╗");
                        Console.WriteLine("  ║  ¡VENTA EXITOSA!                     ║");
                        Console.WriteLine("  ║  Asiento #{0,-3} comprado por T{1}       ║", asiento, terminalId);
                        Console.WriteLine("  ╚═══════════════════════════════════════╝");
                    }
                    else if (respuesta == "OCUPADO")
                    {
                        // Otra terminal con mayor prioridad compró el mismo asiento.
                        // Esto demuestra la SIMULTANEIDAD y PRIORIDAD del sistema.
                        Console.WriteLine("  [CONFLICTO] Asiento #{0} fue tomado por otra terminal", asientoElegido);
                        Console.WriteLine("              (terminal con mayor prioridad ganó)");
                        Console.WriteLine("  Buscando siguiente asiento disponible...");

                        // Intentar con VENDER_RANDOM para obtener cualquier asiento libre
                        escritor.WriteLine("VENDER_RANDOM");
                        respuesta = lector.ReadLine();
                        if (respuesta == null)
                        {
                            Console.WriteLine("  [ERROR] Servidor desconectado.");
                            break;
                        }

                        if (respuesta.StartsWith("OK:"))
                        {
                            string asiento = respuesta.Substring(3);
                            Console.WriteLine("  ╔═══════════════════════════════════════╗");
                            Console.WriteLine("  ║  ¡VENTA EXITOSA (alternativo)!        ║");
                            Console.WriteLine("  ║  Asiento #{0,-3} comprado por T{1}       ║", asiento, terminalId);
                            Console.WriteLine("  ╚═══════════════════════════════════════╝");
                        }
                        else if (respuesta == "NO_QUEDAN")
                        {
                            Console.WriteLine("  ¡AUTOBÚS COMPLETAMENTE LLENO! (40/40)");
                            autobusLleno = true;
                        }
                    }
                    else if (respuesta == "NO_QUEDAN")
                    {
                        Console.WriteLine("  ¡AUTOBÚS COMPLETAMENTE LLENO! (40/40)");
                        autobusLleno = true;
                    }

                    // ─── PASO 5: Mostrar estado actual del autobús ───
                    escritor.WriteLine("RESUMEN");
                    string estadoActual = lector.ReadLine();
                    if (estadoActual != null)
                    {
                        MostrarResumen(estadoActual, terminalId);
                    }

                    if (!autobusLleno)
                    {
                        // ─── PASO 6: Esperar 5 segundos antes del siguiente intento ───
                        Console.WriteLine("  Esperando 5 segundos para la siguiente compra...");
                        Console.WriteLine();
                        Thread.Sleep(5000);
                    }
                }

                // ─── FIN: Enviar SALIR al servidor ───
                Console.WriteLine();
                Console.WriteLine("══════════════════════════════════════════════════");
                Console.WriteLine("  [T{0}] Proceso de compra automática finalizado.", terminalId);
                Console.WriteLine("══════════════════════════════════════════════════");

                escritor.WriteLine("SALIR");
                string despedida = lector.ReadLine();
                if (despedida != null)
                {
                    Console.WriteLine("  Servidor dice: {0}", despedida);
                }
            }
            catch (SocketException ex)
            {
                // ─── ERROR DE CONEXIÓN ───
                Console.WriteLine("\n  [ERROR] No se pudo conectar al servidor:");
                Console.WriteLine("  {0}", ex.Message);
                Console.WriteLine("\n  Verifique que:");
                Console.WriteLine("  - El servidor esté ejecutándose.");
                Console.WriteLine("  - La IP ({0}) sea correcta.", ipServidor);
                Console.WriteLine("  - El puerto ({0}) sea correcto.", puerto);
                Console.WriteLine("  - Ambas máquinas estén en la misma red WiFi.");
                Console.WriteLine("  - El firewall permita conexiones en el puerto.");
            }
            catch (IOException)
            {
                // ─── DESCONEXIÓN DEL SERVIDOR ───
                Console.WriteLine("\n  [ERROR] Se perdió la conexión con el servidor.");
                Console.WriteLine("  El servidor puede haberse cerrado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n  [ERROR] Error inesperado: {0}", ex.Message);
            }
            finally
            {
                // ─── LIMPIEZA ───
                try
                {
                    if (lector != null) lector.Close();
                    if (escritor != null) escritor.Close();
                    if (cliente != null) cliente.Close();
                }
                catch (Exception) { }

                Console.WriteLine("  Conexión cerrada.");
                Console.WriteLine("  Presione Enter para salir...");
                Console.ReadLine();
            }
        }

        // =====================================================================
        // MÉTODOS AUXILIARES
        // =====================================================================

        /// <summary>
        /// Parsea la respuesta VENDIDOS:[...] del servidor y devuelve
        /// la lista de asientos que están LIBRES (no vendidos).
        /// </summary>
        /// <param name="resumen">Respuesta del servidor con formato VENDIDOS:[1,3,...]</param>
        /// <returns>Lista de números de asientos libres (1-40)</returns>
        private static List<int> ObtenerAsientosLibres(string resumen)
        {
            // Determinar qué asientos están vendidos
            bool[] vendidos = new bool[40];

            if (resumen.StartsWith("VENDIDOS:[") && resumen.EndsWith("]"))
            {
                string lista = resumen.Substring("VENDIDOS:[".Length,
                    resumen.Length - "VENDIDOS:[".Length - 1);

                if (!string.IsNullOrEmpty(lista))
                {
                    string[] numeros = lista.Split(',');
                    foreach (string numStr in numeros)
                    {
                        int num;
                        if (int.TryParse(numStr.Trim(), out num) && num >= 1 && num <= 40)
                        {
                            vendidos[num - 1] = true;
                        }
                    }
                }
            }

            // Construir lista de asientos libres
            List<int> libres = new List<int>();
            for (int i = 0; i < 40; i++)
            {
                if (!vendidos[i])
                {
                    libres.Add(i + 1);
                }
            }

            return libres;
        }

        /// <summary>
        /// Muestra el estado del autobús con formato visual.
        /// [XX] = vendido, [ .] = libre
        /// </summary>
        /// <param name="respuesta">Respuesta del servidor con la lista de vendidos</param>
        /// <param name="terminalId">ID de esta terminal</param>
        private static void MostrarResumen(string respuesta, string terminalId)
        {
            // Parsear la lista de vendidos
            bool[] vendidos = new bool[40];
            int totalVendidos = 0;

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

            Console.WriteLine();
            Console.WriteLine("  ┌─── ESTADO DEL AUTOBÚS (T{0}) ───────────────────┐", terminalId);
            Console.Write("  │  ");
            for (int i = 0; i < 40; i++)
            {
                if (vendidos[i])
                {
                    Console.Write("[{0:D2}]", i + 1); // Vendido
                }
                else
                {
                    Console.Write("[ .]");             // Libre
                }

                if ((i + 1) % 10 == 0 && i < 39)
                {
                    Console.WriteLine();
                    Console.Write("  │  ");
                }
            }
            Console.WriteLine();
            Console.WriteLine("  │  [##] = Vendido    [ .] = Libre");
            Console.WriteLine("  │  Vendidos: {0}/40    Libres: {1}/40",
                totalVendidos, 40 - totalVendidos);
            Console.WriteLine("  └─────────────────────────────────────────────────┘");
            Console.WriteLine();
        }
    }
}
