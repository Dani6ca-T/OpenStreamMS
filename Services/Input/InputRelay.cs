using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenStreamMS.Services.Input;

/// <summary>
/// Teclado y ratón de una sesión compuesta en otro miembro: su Sunshine corre en la sesión de
/// Windows del miembro del teclado y mete la entrada allí (SendInput solo llega a la sesión propia).
/// Cuando desde el HUD se pasa el teclado y el ratón a otro miembro (<c>osms-input.txt</c>), Sunshine
/// manda cada evento por la tubería <c>osms-input-&lt;sesión&gt;</c> a este proceso, que corre como
/// SYSTEM en la sesión del miembro y lo inyecta allí (también en el escritorio seguro del UAC).
/// <para>
/// Protocolo: por cada lote, "OSIN" y el número de eventos (uint32) y luego esas estructuras INPUT
/// tal cual. Solo SYSTEM puede abrir la tubería, y Sunshine comprueba que quien la sirve es un
/// proceso de SYSTEM de esa sesión (si otro la creara antes, se quedaría con las pulsaciones).
/// </para>
/// </summary>
internal static class InputRelay
{
    private const uint Magic = 0x4E49534F;   // "OSIN"
    private const int MaxBatch = 4096;

    /// <summary><c>--input-relay</c>. Devuelve el código de salida.</summary>
    public static int Run()
    {
        if (!WindowsIdentity.GetCurrent().IsSystem) return 2;
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint session) || session == 0) return 2;
        var name = $"osms-input-{session}";

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                                                  PipeAccessRights.FullControl, AccessControlType.Allow));
        Logger.Log($"[InputRelay] Teclado y ratón de la compuesta para la sesión de Windows {session} ({name}).");

        int failures = 0;
        while (true)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                                 PipeOptions.FirstPipeInstance, 0, 0, security);
                failures = 0;
                pipe.WaitForConnection();
                try { Receive(pipe); }
                catch (EndOfStreamException) { /* Sunshine cerró: vuelve a otro */ }
                catch (IOException) { }
            }
            catch (Exception ex)
            {
                // Otro proceso ya tiene ese nombre (otro relé de esta sesión): se reintenta un rato.
                if (++failures >= 30)
                {
                    Logger.Warning($"[InputRelay] No se pudo crear {name}: {ex.Message}");
                    return 3;
                }
                Thread.Sleep(1000);
            }
        }
    }

    private static void Receive(NamedPipeServerStream pipe)
    {
        int size = Marshal.SizeOf<INPUT>();
        var header = new byte[8];
        var buffer = new byte[size * 16];
        var inputs = new INPUT[16];
        IntPtr desktop = IntPtr.Zero;
        try
        {
            while (true)
            {
                pipe.ReadExactly(header);
                if (BitConverter.ToUInt32(header, 0) != Magic) throw new EndOfStreamException("cabecera no válida");
                int count = (int)BitConverter.ToUInt32(header, 4);
                if (count is <= 0 or > MaxBatch) throw new EndOfStreamException("lote no válido");
                if (buffer.Length < count * size) buffer = new byte[count * size];
                if (inputs.Length < count) inputs = new INPUT[count];
                pipe.ReadExactly(buffer, 0, count * size);
                MemoryMarshal.Cast<byte, INPUT>(buffer.AsSpan(0, count * size)).CopyTo(inputs);

                if (SendInput((uint)count, inputs, size) == count) continue;
                // El escritorio de entrada cambió (UAC, bloqueo): se engancha al nuevo y se reintenta.
                SyncDesktop(ref desktop);
                SendInput((uint)count, inputs, size);
            }
        }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }
    }

    private static void SyncDesktop(ref IntPtr current)
    {
        var desk = OpenInputDesktop(0, false, GENERIC_ALL);
        if (desk == IntPtr.Zero) return;
        if (SetThreadDesktop(desk))
        {
            if (current != IntPtr.Zero) CloseDesktop(current);
            current = desk;
        }
        else CloseDesktop(desk);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint GENERIC_ALL = 0x10000000;

    /// <summary>INPUT de 64 bits: tipo, relleno y la unión (MOUSEINPUT es la mayor: 32 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint Type;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
