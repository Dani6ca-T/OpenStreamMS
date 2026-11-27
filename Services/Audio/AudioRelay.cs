using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenStreamMS.Services.Seats;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Services.Audio;

/// <summary>
/// Audio de una sesión compuesta: su Sunshine corre en la sesión de Windows de un solo miembro (el
/// del teclado) y solo capturaría el sonido de esa. Cada otro miembro manda el suyo por una tubería
/// (<c>--audio-relay send</c>, captura loopback de su "Remote Audio") a un proceso en la sesión del
/// teclado (<c>--audio-relay play</c>) que lo reproduce allí, donde Sunshine lo captura ya mezclado.
/// <para>
/// Protocolo: "OSAR", frecuencia, canales y bits (negativo = PCM entero, positivo = float) como
/// int32; luego bloques de int32 longitud + muestras. El servidor es el reproductor; el nombre de la
/// tubería es aleatorio y la abre cualquier usuario autenticado (el miembro usa otro usuario).
/// </para>
/// </summary>
internal static class AudioRelay
{
    private static readonly byte[] Magic = "OSAR"u8.ToArray();
    private static volatile bool _stopping;

    /// <summary><c>--audio-relay send|play &lt;pipeName&gt;</c>. Devuelve el código de salida.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 3 || args[1] is not ("send" or "play") || string.IsNullOrWhiteSpace(args[2]))
            return 2;
        Console.CancelKeyPress += (_, e) => { _stopping = true; e.Cancel = true; };
        try
        {
            if (args[1] == "send") Send(args[2]);
            else Play(args[2]);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("audioRelay.operationFailed", args[1], args[2], ex.Message));
            return 1;
        }
    }

    // ── Miembro: captura y envía ─────────────────────────────────────────────

    private static void Send(string pipeName)
    {
        while (!_stopping)
        {
            MMDevice? dev = DefaultOutput();
            if (dev is null) { Thread.Sleep(2000); continue; }   // aún sin "Remote Audio"

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
            try { pipe.Connect(2000); }
            catch (TimeoutException) { continue; }

            using var cap = new LowLatencyLoopback(dev, 10);
            var f = cap.WaveFormat;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
            var header = new byte[16];
            Magic.CopyTo(header, 0);
            BitConverter.TryWriteBytes(header.AsSpan(4), f.SampleRate);
            BitConverter.TryWriteBytes(header.AsSpan(8), f.Channels);
            BitConverter.TryWriteBytes(header.AsSpan(12), isFloat ? f.BitsPerSample : -f.BitsPerSample);
            pipe.Write(header);

            var broken = new ManualResetEventSlim();
            var len = new byte[4];
            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0 || broken.IsSet) return;
                try
                {
                    BitConverter.TryWriteBytes(len, e.BytesRecorded);
                    pipe.Write(len);
                    pipe.Write(e.Buffer, 0, e.BytesRecorded);
                }
                catch (IOException) { broken.Set(); }
                catch (ObjectDisposedException) { broken.Set(); }
            };
            cap.RecordingStopped += (_, _) => broken.Set();
            cap.StartRecording();
            Logger.Log(BackendText.Get("audioRelay.sending", System.Diagnostics.Process.GetCurrentProcess().SessionId, f));
            while (!_stopping && !broken.Wait(500)) { }
            try { cap.StopRecording(); } catch { }
        }
    }

    // ── Sesión del teclado: recibe y reproduce ───────────────────────────────

    private static void Play(string pipeName)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                                                  PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                                                  PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!_stopping)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                             PipeOptions.None, 0, 0, security);
            pipe.WaitForConnection();
            try { Receive(pipe); }
            catch (IOException) { /* el miembro se desconectó: se espera a que vuelva */ }
        }
    }

    private static void Receive(NamedPipeServerStream pipe)
    {
        var header = new byte[16];
        pipe.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic)) throw new IOException(BackendText.Get("audioRelay.invalidHeader"));
        int rate = BitConverter.ToInt32(header, 4), channels = BitConverter.ToInt32(header, 8), bits = BitConverter.ToInt32(header, 12);
        var wf = bits > 0 ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, -bits, channels);

        MMDevice? dev = DefaultOutput() ?? throw new IOException(BackendText.Get("audioRelay.noOutputDevice"));
        var buffer = new SeatJitterBuffer(wf);
        using var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 10);
        output.Init(buffer);
        output.Play();
        Logger.Log(BackendText.Get("audioRelay.playing", dev.FriendlyName, wf));

        var len = new byte[4];
        while (!_stopping)
        {
            pipe.ReadExactly(len);
            int n = BitConverter.ToInt32(len);
            if (n <= 0 || n > 1 << 20) throw new IOException(BackendText.Get("audioRelay.invalidBlock"));
            var data = new byte[n];
            pipe.ReadExactly(data);
            buffer.Add(data);
        }
    }

    private static MMDevice? DefaultOutput()
    {
        try
        {
            using var mm = new MMDeviceEnumerator();
            return mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (COMException) { return null; }   // ELEMENT_NOT_FOUND: sin dispositivo de reproducción
    }
}
