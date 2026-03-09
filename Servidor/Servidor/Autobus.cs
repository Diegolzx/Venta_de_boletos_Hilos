// ============================================================================
// Clase Autobus - Estado compartido del autobús con 40 asientos
// Implementa exclusión mutua con lock para evitar condiciones de carrera.
// Implementa prioridad de terminales: T1 > T2 > T3 > T4
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace Servidor
{
    /// <summary>
    /// Representa el autobús con 40 asientos.
    /// Todos los métodos que modifican estado están protegidos con lock
    /// para garantizar exclusión mutua y evitar doble venta (condición de carrera).
    /// </summary>
    public class Autobus
    {
        // =====================================================================
        // ESTADO COMPARTIDO
        // Array de 40 posiciones: true = vendido, false = libre
        // Índice 0 = asiento 1, índice 39 = asiento 40
        // =====================================================================
        private readonly bool[] asientosVendidos = new bool[40];

        // Contador de asientos vendidos
        private int totalVendidos = 0;

        // Almacena qué terminal vendió cada asiento (para prioridad)
        // Clave: número de asiento (1-40), Valor: terminalId
        private readonly Dictionary<int, int> asientoTerminal = new Dictionary<int, int>();

        // =====================================================================
        // OBJETO DE SINCRONIZACIÓN (lock)
        // 
        // Este objeto actúa como "monitor". Cuando un hilo entra en un bloque
        // lock(lockObj), ningún otro hilo puede entrar a NINGÚN bloque lock
        // que use el mismo objeto hasta que el primero salga.
        //
        // Esto previene CONDICIONES DE CARRERA:
        //   - Sin lock, dos hilos podrían leer que el asiento 5 está libre,
        //     y ambos lo marcarían como vendido → DOBLE VENTA (error grave).
        //   - Con lock, solo un hilo a la vez puede verificar y modificar
        //     el estado del asiento → se garantiza EXCLUSIÓN MUTUA.
        // =====================================================================
        private readonly object lockObj = new object();

        // =====================================================================
        // Cola de prioridad para manejar solicitudes simultáneas
        // Cuando dos terminales intentan el mismo asiento al "mismo tiempo",
        // el lock serializa las peticiones. Usamos un mecanismo de ventana
        // temporal para dar prioridad a la terminal de mayor rango.
        // =====================================================================
        
        // Diccionario de solicitudes pendientes por asiento
        // Clave: número de asiento, Valor: lista de (terminalId, timestamp)
        private readonly Dictionary<int, List<SolicitudVenta>> solicitudesPendientes 
            = new Dictionary<int, List<SolicitudVenta>>();

        // Ventana temporal en milisegundos para considerar solicitudes "simultáneas"
        private const int VENTANA_PRIORIDAD_MS = 100;

        /// <summary>
        /// Estructura para representar una solicitud de venta pendiente.
        /// </summary>
        private class SolicitudVenta
        {
            public int TerminalId { get; set; }
            public DateTime Timestamp { get; set; }
            public ManualResetEvent Evento { get; set; }
            public string Resultado { get; set; }
        }

        // =====================================================================
        // MÉTODO: VenderAsientoEspecifico
        // 
        // Implementa la venta de un asiento específico con prioridad.
        // PRIORIDAD: T1 > T2 > T3 > T4 (menor número = mayor prioridad)
        //
        // El mecanismo de prioridad funciona así:
        //   1. Un hilo registra su solicitud para un asiento.
        //   2. Espera un breve periodo (ventana de prioridad).
        //   3. Dentro del lock, revisa si hay otras solicitudes para el 
        //      mismo asiento dentro de la ventana temporal.
        //   4. Si las hay, gana la terminal con menor ID (mayor prioridad).
        //   5. Las demás reciben "OCUPADO".
        // =====================================================================
        
        /// <summary>
        /// Vende un asiento específico verificando prioridad de terminal.
        /// </summary>
        /// <param name="numero">Número de asiento (1-40)</param>
        /// <param name="terminalId">ID de la terminal (1=mayor prioridad, 4=menor)</param>
        /// <returns>
        /// "OK:n" si la venta fue exitosa,
        /// "OCUPADO" si el asiento ya fue vendido,
        /// "INVALIDO" si el número está fuera de rango
        /// </returns>
        public string VenderAsientoEspecifico(int numero, int terminalId)
        {
            // ─── Validación fuera del lock (no modifica estado) ───
            if (numero < 1 || numero > 40)
            {
                return "INVALIDO";
            }

            // ─── SECCIÓN CRÍTICA ───
            // Todo lo que está dentro de lock(lockObj) se ejecuta de manera
            // ATÓMICA respecto a otros hilos que también usen lock(lockObj).
            // Esto significa que:
            //   1. Solo UN hilo a la vez ejecuta este bloque.
            //   2. La verificación (asiento libre?) y la modificación (marcar vendido)
            //      ocurren como una operación indivisible.
            //   3. No existe posibilidad de que dos hilos vendan el mismo asiento.
            lock (lockObj)
            {
                int indice = numero - 1;

                // Si ya está vendido, no importa la prioridad
                if (asientosVendidos[indice])
                {
                    return "OCUPADO";
                }

                // Registrar solicitud para este asiento
                if (!solicitudesPendientes.ContainsKey(numero))
                {
                    solicitudesPendientes[numero] = new List<SolicitudVenta>();
                }

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

                // ─── LÓGICA DE PRIORIDAD ───
                // Si hay múltiples solicitudes dentro de la ventana temporal,
                // gana la terminal con el ID más bajo (mayor prioridad).
                // T1(id=1) > T2(id=2) > T3(id=3) > T4(id=4)
                int menorTerminal = terminalId;
                foreach (SolicitudVenta solicitud in solicitudesPendientes[numero])
                {
                    if (solicitud.TerminalId < menorTerminal)
                    {
                        menorTerminal = solicitud.TerminalId;
                    }
                }

                // Si esta terminal NO tiene la mayor prioridad, pierde
                if (menorTerminal != terminalId)
                {
                    Console.WriteLine("[PRIORIDAD] Terminal T{0} pierde asiento {1} contra T{2}",
                        terminalId, numero, menorTerminal);
                    return "OCUPADO";
                }

                // ─── VENTA EXITOSA ───
                // Marcar asiento como vendido
                asientosVendidos[indice] = true;
                totalVendidos++;
                asientoTerminal[numero] = terminalId;

                // Limpiar solicitudes de este asiento (ya fue vendido)
                solicitudesPendientes.Remove(numero);

                Console.WriteLine("[VENTA] Asiento {0} vendido a Terminal T{1} (Total: {2}/40)",
                    numero, terminalId, totalVendidos);

                return "OK:" + numero;
            }
            // ─── FIN SECCIÓN CRÍTICA ───
        }

        // =====================================================================
        // MÉTODO: VenderSiguienteDisponible
        //
        // Busca el primer asiento libre (de 1 a 40) y lo vende.
        // También está protegido con lock para evitar que dos hilos
        // obtengan el mismo asiento "siguiente disponible".
        // =====================================================================
        
        /// <summary>
        /// Vende el siguiente asiento disponible (el primero libre de 1 a 40).
        /// </summary>
        /// <param name="terminalId">ID de la terminal que solicita</param>
        /// <returns>
        /// "OK:n" si se encontró y vendió un asiento libre,
        /// "NO_QUEDAN" si todos están vendidos
        /// </returns>
        public string VenderSiguienteDisponible(int terminalId)
        {
            // ─── SECCIÓN CRÍTICA ───
            lock (lockObj)
            {
                // Recorrer asientos del 1 al 40
                for (int i = 0; i < 40; i++)
                {
                    if (!asientosVendidos[i])
                    {
                        // Encontró asiento libre → vender
                        asientosVendidos[i] = true;
                        totalVendidos++;
                        int numero = i + 1;
                        asientoTerminal[numero] = terminalId;

                        Console.WriteLine("[VENTA] Asiento {0} vendido a Terminal T{1} (Total: {2}/40)",
                            numero, terminalId, totalVendidos);

                        return "OK:" + numero;
                    }
                }

                // No hay asientos libres
                Console.WriteLine("[INFO] Terminal T{0} intentó comprar pero NO QUEDAN asientos", terminalId);
                return "NO_QUEDAN";
            }
            // ─── FIN SECCIÓN CRÍTICA ───
        }

        // =====================================================================
        // MÉTODO: ObtenerVendidos
        //
        // Devuelve la lista de asientos vendidos en formato:
        // VENDIDOS:[1,3,5,10]
        // También protegido con lock para leer estado consistente.
        // =====================================================================
        
        /// <summary>
        /// Obtiene la lista de asientos vendidos.
        /// </summary>
        /// <returns>Cadena con formato "VENDIDOS:[1,3,5,...]"</returns>
        public string ObtenerVendidos()
        {
            // ─── SECCIÓN CRÍTICA ───
            // Aunque solo leemos, usamos lock para garantizar una lectura
            // consistente (que no leamos mientras otro hilo está modificando).
            lock (lockObj)
            {
                List<string> vendidos = new List<string>();
                for (int i = 0; i < 40; i++)
                {
                    if (asientosVendidos[i])
                    {
                        vendidos.Add((i + 1).ToString());
                    }
                }

                return "VENDIDOS:[" + string.Join(",", vendidos) + "]";
            }
            // ─── FIN SECCIÓN CRÍTICA ───
        }

        // =====================================================================
        // MÉTODO: ObtenerResumenFinal
        //
        // Genera un resumen detallado para mostrar al cerrar el servidor.
        // =====================================================================
        
        /// <summary>
        /// Genera el resumen final con detalle de ventas por terminal.
        /// </summary>
        /// <returns>Cadena con el resumen completo</returns>
        public string ObtenerResumenFinal()
        {
            lock (lockObj)
            {
                string resumen = "\n";
                resumen += "╔══════════════════════════════════════════════════╗\n";
                resumen += "║          RESUMEN FINAL DE VENTAS                ║\n";
                resumen += "╠══════════════════════════════════════════════════╣\n";

                // Mostrar estado de cada asiento
                resumen += "║ Estado de asientos:                             ║\n";
                resumen += "║ ";
                for (int i = 0; i < 40; i++)
                {
                    int num = i + 1;
                    if (asientosVendidos[i])
                    {
                        string terminal = asientoTerminal.ContainsKey(num) 
                            ? "T" + asientoTerminal[num] 
                            : "??";
                        resumen += string.Format("[{0:D2}:{1}]", num, terminal);
                    }
                    else
                    {
                        resumen += string.Format("[{0:D2}:--]", num);
                    }

                    if ((i + 1) % 10 == 0 && i < 39)
                    {
                        resumen += "\n║ ";
                    }
                }
                resumen += "\n";

                // Vendidos por terminal
                resumen += "╠══════════════════════════════════════════════════╣\n";
                for (int t = 1; t <= 4; t++)
                {
                    int count = 0;
                    List<string> asientos = new List<string>();
                    foreach (var par in asientoTerminal)
                    {
                        if (par.Value == t)
                        {
                            count++;
                            asientos.Add(par.Key.ToString());
                        }
                    }
                    resumen += string.Format("║ Terminal T{0}: {1} asientos → [{2}]\n",
                        t, count, string.Join(",", asientos));
                }

                resumen += "╠══════════════════════════════════════════════════╣\n";
                resumen += string.Format("║ TOTAL VENDIDOS: {0}/40\n", totalVendidos);
                resumen += string.Format("║ TOTAL LIBRES:   {0}/40\n", 40 - totalVendidos);
                resumen += "╚══════════════════════════════════════════════════╝\n";

                return resumen;
            }
        }
    }
}
