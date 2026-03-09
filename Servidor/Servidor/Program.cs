// ============================================================================
// SERVIDOR CENTRAL - Sistema de Venta Distribuida de Boletos de Autobús
// ============================================================================
//
// Este servidor:
//   1. Escucha en el puerto 5000 (accesible en red local WiFi).
//   2. Acepta múltiples clientes simultáneamente (un Thread por cliente).
//   3. Mantiene el estado de 40 asientos mediante la clase Autobus.
//   4. Garantiza exclusión mutua con lock para evitar doble venta.
//   5. Implementa prioridad de terminales (T1 > T2 > T3 > T4).
//   6. Al cerrar con Ctrl+C, muestra resumen final de ventas.
//
// PROTOCOLO (texto simple, líneas terminadas en \n):
//   Cliente envía → Servidor responde
//   VENDER_RANDOM              → OK:n | NO_QUEDAN
//   VENDER_ESPECIFICO:n        → OK:n | OCUPADO | INVALIDO
//   RESUMEN                    → VENDIDOS:[1,3,5,...]
//   SALIR                      → ADIÓS
//   (otro)                     → COMANDO_DESCONOCIDO
//
// EXCLUSIÓN MUTUA:
//   Sin sincronización, dos hilos podrían leer que un asiento está libre
//   y ambos lo venderían → CONDICIÓN DE CARRERA → DOBLE VENTA.
//   El lock garantiza que solo un hilo a la vez puede verificar + modificar.
//
// CONDICIÓN DE CARRERA (explicación):
//   Hilo A: lee asiento 5 → libre ✓
//   Hilo B: lee asiento 5 → libre ✓  (aún no se marcó vendido)
//   Hilo A: marca asiento 5 vendido
//   Hilo B: marca asiento 5 vendido → ¡DOBLE VENTA!
//   Con lock, Hilo B espera a que Hilo A termine antes de leer.
//
// ============================================================================

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Servidor
{
    class Program
    {
        // Instancia compartida del autobús (40 asientos)
        private static Autobus autobus = new Autobus();

        // Listener TCP en puerto 5000
        private static TcpListener servidor = null;

        // Contador de terminales conectadas (para asignar IDs)
        private static int contadorTerminales = 0;
        private static readonly object contadorLock = new object();

        // Flag para controlar el cierre limpio
        private static bool ejecutando = true;

        static void Main(string[] args)
        {
            // ─── Configurar puerto (por defecto 5000) ───
            int puerto = 5000;
            if (args.Length > 0)
            {
                int.TryParse(args[0], out puerto);
            }

            // ─── Manejar Ctrl+C para cierre limpio ───
            // Al presionar Ctrl+C, se ejecuta el resumen final antes de cerrar.
            Console.CancelKeyPress += new ConsoleCancelEventHandler(ManejarCierre);

            // ─── Obtener IP local para mostrar al usuario ───
            string ipLocal = ObtenerIPLocal();

            try
            {
                // ═══════════════════════════════════════════════════════
                // INICIAR SERVIDOR TCP
                // Escucha en TODAS las interfaces (0.0.0.0) para que
                // clientes de otras laptops en la red WiFi puedan conectarse.
                // ═══════════════════════════════════════════════════════
                servidor = new TcpListener(IPAddress.Any, puerto);
                servidor.Start();

                Console.WriteLine("╔══════════════════════════════════════════════════╗");
                Console.WriteLine("║   SERVIDOR DE VENTA DE BOLETOS DE AUTOBÚS       ║");
                Console.WriteLine("╠══════════════════════════════════════════════════╣");
                Console.WriteLine("║ Estado: ACTIVO                                  ║");
                Console.WriteLine("║ IP Local: {0,-39}║", ipLocal);
                Console.WriteLine("║ Puerto:   {0,-39}║", puerto);
                Console.WriteLine("║ Asientos: 40 disponibles                        ║");
                Console.WriteLine("║                                                 ║");
                Console.WriteLine("║ Los clientes deben conectarse a:                ║");
                Console.WriteLine("║   IP: {0,-43}║", ipLocal);
                Console.WriteLine("║   Puerto: {0,-39}║", puerto);
                Console.WriteLine("║                                                 ║");
                Console.WriteLine("║ Presione Ctrl+C para cerrar y ver resumen.      ║");
                Console.WriteLine("╚══════════════════════════════════════════════════╝");
                Console.WriteLine();

                // ═══════════════════════════════════════════════════════
                // BUCLE PRINCIPAL: Aceptar clientes
                // Por cada cliente que se conecta, se crea un Thread
                // independiente para atenderlo sin bloquear al resto.
                // ═══════════════════════════════════════════════════════
                while (ejecutando)
                {
                    try
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

                        Console.WriteLine("[CONEXIÓN] Terminal T{0} conectada desde {1}",
                            terminalId, ((IPEndPoint)cliente.Client.RemoteEndPoint).Address);

                        // ─── Crear hilo para este cliente ───
                        // Cada cliente se atiende en su propio hilo.
                        // Esto permite atender múltiples clientes simultáneamente.
                        Thread hiloCliente = new Thread(() => ManejarCliente(cliente, terminalId));
                        hiloCliente.IsBackground = true; // Se cierra con el proceso principal
                        hiloCliente.Name = "Terminal-T" + terminalId;
                        hiloCliente.Start();
                    }
                    catch (SocketException)
                    {
                        // Se lanza cuando se detiene el listener (Ctrl+C)
                        if (!ejecutando) break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[ERROR] Error aceptando cliente: {0}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR FATAL] No se pudo iniciar el servidor: {0}", ex.Message);
                Console.WriteLine("Verifique que el puerto {0} no esté en uso.", puerto);
            }
        }

        // =====================================================================
        // MANEJADOR DE CLIENTE (ejecuta en su propio hilo)
        //
        // Cada cliente se comunica con el servidor mediante líneas de texto.
        // El manejador lee comandos, invoca métodos sincronizados del Autobus,
        // y envía la respuesta de vuelta al cliente.
        //
        // Si ocurre una excepción (cliente desconectado, error de red),
        // solo SE CIERRA ESTE CLIENTE sin afectar al servidor ni a otros.
        // =====================================================================

        /// <summary>
        /// Maneja la comunicación con un cliente individual.
        /// Se ejecuta en un Thread dedicado.
        /// </summary>
        /// <param name="cliente">TcpClient conectado</param>
        /// <param name="terminalId">ID asignado a esta terminal</param>
        private static void ManejarCliente(TcpClient cliente, int terminalId)
        {
            StreamReader lector = null;
            StreamWriter escritor = null;

            try
            {
                // Obtener streams de comunicación
                NetworkStream stream = cliente.GetStream();
                lector = new StreamReader(stream);
                escritor = new StreamWriter(stream);
                escritor.AutoFlush = true; // Enviar inmediatamente cada WriteLine

                // ─── Enviar mensaje de bienvenida ───
                escritor.WriteLine("BIENVENIDO_SERVIDOR");
                escritor.WriteLine("TERMINAL_ID:" + terminalId);

                Console.WriteLine("[T{0}] Sesión iniciada", terminalId);

                // ─── Bucle de comandos ───
                // Lee comandos línea por línea hasta que el cliente
                // envíe "SALIR" o se desconecte.
                string comando;
                while ((comando = lector.ReadLine()) != null)
                {
                    comando = comando.Trim();
                    if (string.IsNullOrEmpty(comando)) continue;

                    Console.WriteLine("[T{0}] Comando recibido: {1}", terminalId, comando);

                    string respuesta;

                    // ─── Despachar comando ───
                    if (comando == "VENDER_RANDOM")
                    {
                        // Vender siguiente asiento disponible
                        respuesta = autobus.VenderSiguienteDisponible(terminalId);
                    }
                    else if (comando.StartsWith("VENDER_ESPECIFICO:"))
                    {
                        // Extraer número de asiento del comando
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
                        // Obtener lista de asientos vendidos
                        respuesta = autobus.ObtenerVendidos();
                    }
                    else if (comando == "SALIR")
                    {
                        // Cliente pide desconexión
                        escritor.WriteLine("ADIÓS");
                        Console.WriteLine("[T{0}] Se desconectó voluntariamente", terminalId);
                        break;
                    }
                    else
                    {
                        // Comando no reconocido
                        respuesta = "COMANDO_DESCONOCIDO";
                    }

                    // Enviar respuesta al cliente
                    escritor.WriteLine(respuesta);
                    Console.WriteLine("[T{0}] Respuesta enviada: {1}", terminalId, respuesta);
                }
            }
            catch (IOException ex)
            {
                // ─── MANEJO DE ERRORES DE E/S ───
                // Esto ocurre cuando el cliente se desconecta abruptamente
                // (cerró la ventana, se cayó la red, etc.)
                // Solo afecta a ESTE cliente, el servidor sigue funcionando.
                Console.WriteLine("[T{0}] Desconexión inesperada: {1}", terminalId, ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[T{0}] Error: {1}", terminalId, ex.Message);
            }
            finally
            {
                // ─── LIMPIEZA ───
                // Cerrar streams y socket para liberar recursos.
                // El bloque finally garantiza que esto se ejecute
                // incluso si hubo una excepción.
                try
                {
                    if (lector != null) lector.Close();
                    if (escritor != null) escritor.Close();
                    if (cliente != null) cliente.Close();
                }
                catch (Exception) { /* Ignorar errores al cerrar */ }

                Console.WriteLine("[T{0}] Conexión cerrada", terminalId);
            }
        }

        // =====================================================================
        // CIERRE LIMPIO (Ctrl+C)
        // =====================================================================

        /// <summary>
        /// Maneja el evento Ctrl+C para cerrar el servidor limpiamente.
        /// Muestra el resumen final de ventas antes de terminar.
        /// </summary>
        private static void ManejarCierre(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true; // Evitar cierre abrupto para poder mostrar resumen
            ejecutando = false;

            Console.WriteLine("\n[SERVIDOR] Cerrando servidor...");

            // Mostrar resumen final
            Console.WriteLine(autobus.ObtenerResumenFinal());

            // Detener el listener para desbloquear AcceptTcpClient
            try
            {
                if (servidor != null) servidor.Stop();
            }
            catch (Exception) { }

            // Ahora sí, salir del proceso
            Environment.Exit(0);
        }

        // =====================================================================
        // UTILIDAD: Obtener IP local
        // =====================================================================

        /// <summary>
        /// Obtiene la dirección IP local de la máquina en la red WiFi.
        /// Esta es la IP que los clientes de otras laptops deben usar.
        /// </summary>
        private static string ObtenerIPLocal()
        {
            try
            {
                // Crear socket UDP temporal para determinar la IP local
                // que se usaría para comunicarse con la red
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530); // No envía datos realmente
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint.Address.ToString();
                }
            }
            catch
            {
                return "127.0.0.1";
            }
        }
    }
}
