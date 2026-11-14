// Driver.cpp — OsmsPad: mandos virtuales de OpenStreamMS con VHF (Virtual HID Framework).
//
// Un único dispositivo raíz (ROOT\OSMSPAD). Este driver UMDF es un filtro encima de la
// función de VHF (vhf.sys, de Windows) y expone una interfaz privada (OsmsPadIoctl.h): cada
// IOCTL_OSMSPAD_CREATE crea un hijo HID con VhfCreate, que Windows enumera como un mando más.
// No hay driver de kernel propio, y por eso basta firmarlo como RemoteIdd (certificado de un
// solo uso en Root y TrustedPublisher), sin testsigning.
//
// Ideas tomadas de libvirtualgamepad (Copyright (c) 2026 Chase Payne, MIT), adaptadas:
//   - filtro sobre VHF con un destino de E/S local abierto al arrancar (PrepareHardware);
//   - cada mando es de quien lo creó: cerrar el handle borra sus mandos, así un Sunshine que
//     se cae no deja mandos huérfanos;
//   - el ID de hardware HID\VID_045E&PID_0B12&IG_00 hace que Windows le ponga xinputhid.
// Propio de OpenStreamMS: la sesión de Windows del mando va en su ID de instancia
// (OSMS_S<sesión>_P<n>), para que el servicio lo aísle en esa sesión sin adivinar nada.
//
// Concurrencia, sin más estados que los imprescindibles:
//   - opLock (wait lock) ordena crear, alimentar, borrar, cerrar handles y apagar;
//   - fbLock (spin lock) protege solo la vibración: los callbacks de VHF toman fbLock y nunca
//     opLock, así VhfDelete(Wait=TRUE) bajo opLock no se puede bloquear contra ellos.
#include <windows.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <initguid.h>
#include <vhf.h>
#include <stdio.h>

#include "OsmsPadIoctl.h"
#include "XboxSeries.h"

namespace {

struct Device;

struct Pad {
    Device* Parent;
    UINT32 Index;
    bool Used;
    VHFHANDLE Vhf;
    WDFFILEOBJECT Owner;
    UINT32 Session;
    wchar_t InstanceId[48];
    // Bajo fbLock:
    OsmsPadFeedback Feedback;
    bool FeedbackPending;
};

struct Device {
    WDFDEVICE Handle;
    WDFWAITLOCK OpLock;
    WDFSPINLOCK FbLock;
    WDFQUEUE FeedbackQueue;
    WDFIOTARGET VhfTarget;
    HANDLE VhfFile;
    bool Started;
    Pad Pads[OSMSPAD_MAX_PADS];
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(Device, GetDevice)

// Base del ContainerID de cada mando: los dos últimos bytes son la sesión y el número, para que
// Windows no agrupe varios mandos como si fueran un solo aparato.
constexpr GUID kContainerBase = { 0x3f6d2a91, 0x5c4e, 0x4b8a, { 0x9e, 0x17, 0x62, 0xd0, 0xa4, 0x3b, 0x00, 0x00 } };

void Log(const wchar_t* format, ...)
{
    wchar_t text[256];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(text, _TRUNCATE, format, args);
    va_end(args);
    OutputDebugStringW(text);
}

// ── VHF ──────────────────────────────────────────────────────────────────────

NTSTATUS EnsureVhfTarget(Device* dev)
{
    if (dev->VhfFile) return STATUS_SUCCESS;
    if (!dev->Started || !dev->VhfTarget) return STATUS_DEVICE_NOT_READY;
    // Abrir por fichero sin nombre: el destino es la parte de modo kernel de esta misma pila,
    // es decir vhf.sys. VHF en UMDF exige ese handle en VHF_CONFIG.FileHandle.
    WDF_IO_TARGET_OPEN_PARAMS params;
    WDF_IO_TARGET_OPEN_PARAMS_INIT_OPEN_BY_FILE(&params, nullptr);
    NTSTATUS st = WdfIoTargetOpen(dev->VhfTarget, &params);
    if (!NT_SUCCESS(st)) return st;
    dev->VhfFile = WdfIoTargetWdmGetTargetFileHandle(dev->VhfTarget);
    if (!dev->VhfFile)
    {
        WdfIoTargetClose(dev->VhfTarget);
        return STATUS_DEVICE_NOT_READY;
    }
    return STATUS_SUCCESS;
}

// El juego pide vibración: informe de salida 3. Se guarda y, si el dueño tiene una petición
// IOCTL_OSMSPAD_FEEDBACK esperando, se completa con ella.
VOID EvtVhfWriteReport(PVOID ctx, VHFOPERATIONHANDLE op, PVOID, PHID_XFER_PACKET packet)
{
    auto* pad = static_cast<Pad*>(ctx);
    Device* dev = pad->Parent;
    OsmsPadFeedback fb{};
    NTSTATUS st = STATUS_INVALID_PARAMETER;
    if (packet && osmspad::DecodeXboxRumble(packet->reportBuffer, packet->reportBufferLen, &fb))
    {
        fb.Pad = pad->Index;
        st = STATUS_SUCCESS;
        WDFREQUEST request = nullptr;
        WdfSpinLockAcquire(dev->FbLock);
        pad->Feedback = fb;
        pad->FeedbackPending = true;
        if (pad->Owner &&
            NT_SUCCESS(WdfIoQueueRetrieveRequestByFileObject(dev->FeedbackQueue, pad->Owner, &request)))
            pad->FeedbackPending = false;
        WdfSpinLockRelease(dev->FbLock);
        if (request)
        {
            void* out = nullptr;
            NTSTATUS rs = WdfRequestRetrieveOutputBuffer(request, sizeof(OsmsPadFeedback), &out, nullptr);
            if (NT_SUCCESS(rs)) memcpy(out, &fb, sizeof fb);
            WdfRequestCompleteWithInformation(request, rs, NT_SUCCESS(rs) ? sizeof fb : 0);
        }
    }
    VhfAsyncOperationComplete(op, st);
}

// Borra el hijo HID. Con opLock tomado; VhfDelete(TRUE) espera a los callbacks en curso.
void DestroyPad(Pad& pad)
{
    if (!pad.Used) return;
    if (pad.Vhf) VhfDelete(pad.Vhf, TRUE);
    Device* dev = pad.Parent;
    WdfSpinLockAcquire(dev->FbLock);
    pad.Vhf = nullptr;
    pad.Owner = nullptr;
    pad.FeedbackPending = false;
    WdfSpinLockRelease(dev->FbLock);
    pad.Used = false;
    Log(L"OsmsPad: mando %u (%s) borrado\n", pad.Index, pad.InstanceId);
}

void DestroyAll(Device* dev, WDFFILEOBJECT owner)
{
    for (auto& pad : dev->Pads)
        if (pad.Used && (!owner || pad.Owner == owner)) DestroyPad(pad);
}

// ── Sesión de quien llama ────────────────────────────────────────────────────

struct SessionQuery { DWORD Session; bool Ok; };

VOID EvtImpersonate(WDFREQUEST, PVOID ctx)
{
    auto* q = static_cast<SessionQuery*>(ctx);
    HANDLE token = nullptr;
    if (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token))
    {
        DWORD size = 0;
        q->Ok = GetTokenInformation(token, TokenSessionId, &q->Session, sizeof q->Session, &size) != FALSE;
        CloseHandle(token);
    }
}

// ── IOCTL ────────────────────────────────────────────────────────────────────

NTSTATUS CreatePad(Device* dev, WDFREQUEST request, WDFFILEOBJECT owner, size_t* info)
{
    OsmsPadCreateIn* in = nullptr;
    OsmsPadCreateOut* out = nullptr;
    NTSTATUS st = WdfRequestRetrieveInputBuffer(request, sizeof *in, reinterpret_cast<void**>(&in), nullptr);
    if (!NT_SUCCESS(st)) return st;
    if (in->Version != OSMSPAD_PROTOCOL_VERSION) return STATUS_REVISION_MISMATCH;
    if (in->Profile != OSMSPAD_PROFILE_XBOX_SERIES) return STATUS_NOT_SUPPORTED;
    const OsmsPadCreateIn req = *in;   // METHOD_BUFFERED: la salida pisa la entrada

    UINT32 session = req.TargetSession;
    if (session == OSMSPAD_SESSION_CALLER)
    {
        SessionQuery q{};
        st = WdfRequestImpersonate(request, SecurityImpersonation, EvtImpersonate, &q);
        if (!NT_SUCCESS(st) || !q.Ok) return NT_SUCCESS(st) ? STATUS_ACCESS_DENIED : st;
        session = q.Session;
    }

    st = WdfRequestRetrieveOutputBuffer(request, sizeof *out, reinterpret_cast<void**>(&out), nullptr);
    if (!NT_SUCCESS(st)) return st;

    st = EnsureVhfTarget(dev);
    if (!NT_SUCCESS(st)) return st;

    Pad* pad = nullptr;
    for (auto& p : dev->Pads)
        if (!p.Used) { pad = &p; break; }
    if (!pad) return STATUS_INSUFFICIENT_RESOURCES;

    swprintf_s(pad->InstanceId, L"OSMS_S%u_P%u", session, pad->Index);
    size_t descriptorSize = 0, idsBytes = 0;
    const UINT8* descriptor = osmspad::XboxSeriesDescriptor(&descriptorSize);
    const wchar_t* ids = osmspad::XboxSeriesHardwareIds(&idsBytes);

    VHF_CONFIG cfg;
    VHF_CONFIG_INIT(&cfg, dev->VhfFile, static_cast<USHORT>(descriptorSize), const_cast<PUCHAR>(descriptor));
    cfg.VhfClientContext = pad;
    cfg.EvtVhfAsyncOperationWriteReport = EvtVhfWriteReport;
    cfg.VendorID = osmspad::kXboxVendorId;
    cfg.ProductID = osmspad::kXboxSeriesProductId;
    cfg.VersionNumber = osmspad::kXboxSeriesVersion;
    cfg.ContainerID = kContainerBase;
    cfg.ContainerID.Data4[6] = static_cast<UCHAR>(session);
    cfg.ContainerID.Data4[7] = static_cast<UCHAR>(pad->Index);
    cfg.InstanceID = pad->InstanceId;
    cfg.InstanceIDLength = static_cast<USHORT>((wcslen(pad->InstanceId) + 1) * sizeof(wchar_t));
    cfg.HardwareIDs = const_cast<PWSTR>(ids);
    cfg.HardwareIDsLength = static_cast<USHORT>(idsBytes);

    // Todo listo antes de VhfStart: VHF puede llamar a EvtVhfWriteReport antes de que vuelva.
    WdfSpinLockAcquire(dev->FbLock);
    pad->Owner = owner;
    pad->FeedbackPending = false;
    WdfSpinLockRelease(dev->FbLock);
    pad->Session = session;
    pad->Used = true;

    VHFHANDLE vhf = nullptr;
    st = VhfCreate(&cfg, &vhf);
    if (NT_SUCCESS(st))
    {
        pad->Vhf = vhf;
        st = VhfStart(vhf);
    }
    if (!NT_SUCCESS(st))
    {
        Log(L"OsmsPad: no se pudo crear el mando %s (0x%08X)\n", pad->InstanceId, st);
        DestroyPad(*pad);
        return st;
    }

    // Estado inicial (todo suelto, sticks al centro): sin él, XInput tarda en ver el mando.
    OsmsPadInput idle{};
    idle.Pad = pad->Index;
    osmspad::XboxInputReport report = osmspad::EncodeXboxInput(idle);
    HID_XFER_PACKET packet{ reinterpret_cast<PUCHAR>(&report), sizeof report, osmspad::kXboxInputReportId };
    (void)VhfReadReportSubmit(pad->Vhf, &packet);

    memset(out, 0, sizeof *out);
    out->Pad = pad->Index;
    out->Session = session;
    wcscpy_s(out->InstanceId, pad->InstanceId);
    *info = sizeof *out;
    Log(L"OsmsPad: mando %u creado (%s)\n", pad->Index, pad->InstanceId);
    return STATUS_SUCCESS;
}

Pad* OwnedPad(Device* dev, UINT32 index, WDFFILEOBJECT owner)
{
    if (index >= OSMSPAD_MAX_PADS) return nullptr;
    Pad& pad = dev->Pads[index];
    return pad.Used && pad.Owner == owner ? &pad : nullptr;
}

NTSTATUS SubmitInput(Device* dev, WDFREQUEST request, WDFFILEOBJECT owner)
{
    OsmsPadInput* in = nullptr;
    NTSTATUS st = WdfRequestRetrieveInputBuffer(request, sizeof *in, reinterpret_cast<void**>(&in), nullptr);
    if (!NT_SUCCESS(st)) return st;
    Pad* pad = OwnedPad(dev, in->Pad, owner);
    if (!pad || !pad->Vhf) return STATUS_INVALID_HANDLE;
    osmspad::XboxInputReport report = osmspad::EncodeXboxInput(*in);
    HID_XFER_PACKET packet{ reinterpret_cast<PUCHAR>(&report), sizeof report, osmspad::kXboxInputReportId };
    // Sin EvtVhfReadyForNextReadReport, VHF copia el informe y lo encola. Si su cola está llena
    // (nadie lee el mando) se pierde, y da igual: el siguiente lleva el estado completo.
    (void)VhfReadReportSubmit(pad->Vhf, &packet);
    return STATUS_SUCCESS;
}

NTSTATUS DestroyOne(Device* dev, WDFREQUEST request, WDFFILEOBJECT owner)
{
    OsmsPadDestroyIn* in = nullptr;
    NTSTATUS st = WdfRequestRetrieveInputBuffer(request, sizeof *in, reinterpret_cast<void**>(&in), nullptr);
    if (!NT_SUCCESS(st)) return st;
    Pad* pad = OwnedPad(dev, in->Pad, owner);
    if (!pad) return STATUS_INVALID_HANDLE;
    DestroyPad(*pad);
    return STATUS_SUCCESS;
}

// Vibración: si ya hay una pendiente para alguno de sus mandos sale enseguida; si no, la
// petición espera en FeedbackQueue (el cliente la manda overlapped y la repone al volver).
// Devuelve true si la petición se completó aquí o se quedó en la cola.
bool QueueFeedback(Device* dev, WDFREQUEST request, WDFFILEOBJECT owner)
{
    void* out = nullptr;
    NTSTATUS st = WdfRequestRetrieveOutputBuffer(request, sizeof(OsmsPadFeedback), &out, nullptr);
    if (!NT_SUCCESS(st))
    {
        WdfRequestComplete(request, st);
        return true;
    }
    OsmsPadFeedback fb{};
    bool ready = false;
    WdfSpinLockAcquire(dev->FbLock);
    for (auto& pad : dev->Pads)
        if (pad.Used && pad.Owner == owner && pad.FeedbackPending)
        {
            fb = pad.Feedback;
            pad.FeedbackPending = false;
            ready = true;
            break;
        }
    if (!ready) st = WdfRequestForwardToIoQueue(request, dev->FeedbackQueue);
    WdfSpinLockRelease(dev->FbLock);
    if (ready)
    {
        memcpy(out, &fb, sizeof fb);
        WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, sizeof fb);
    }
    else if (!NT_SUCCESS(st))
        WdfRequestComplete(request, st);
    return true;
}

VOID EvtIoDeviceControl(WDFQUEUE queue, WDFREQUEST request, size_t, size_t, ULONG code)
{
    Device* dev = GetDevice(WdfIoQueueGetDevice(queue));
    WDFFILEOBJECT owner = WdfRequestGetFileObject(request);
    if (!owner)
    {
        WdfRequestComplete(request, STATUS_INVALID_HANDLE);
        return;
    }
    if (code == IOCTL_OSMSPAD_FEEDBACK)
    {
        QueueFeedback(dev, request, owner);
        return;
    }

    NTSTATUS st = STATUS_INVALID_DEVICE_REQUEST;
    size_t info = 0;
    WdfWaitLockAcquire(dev->OpLock, nullptr);
    switch (code)
    {
    case IOCTL_OSMSPAD_CREATE:  st = CreatePad(dev, request, owner, &info); break;
    case IOCTL_OSMSPAD_INPUT:   st = SubmitInput(dev, request, owner); break;
    case IOCTL_OSMSPAD_DESTROY: st = DestroyOne(dev, request, owner); break;
    default: break;
    }
    WdfWaitLockRelease(dev->OpLock);
    WdfRequestCompleteWithInformation(request, st, info);
}

// ── Ficheros, PnP y energía ─────────────────────────────────────────────────

VOID EvtFileCreate(WDFDEVICE, WDFREQUEST request, WDFFILEOBJECT)
{
    WdfRequestComplete(request, STATUS_SUCCESS);
}

// Cerrar el handle (o que el proceso muera) borra sus mandos. Las peticiones de vibración que
// esperaban en la cola manual las cancela WDF.
VOID EvtFileCleanup(WDFFILEOBJECT file)
{
    if (!file) return;
    Device* dev = GetDevice(WdfFileObjectGetDevice(file));
    WdfWaitLockAcquire(dev->OpLock, nullptr);
    DestroyAll(dev, file);
    WdfWaitLockRelease(dev->OpLock);
}

// El destino local solo se puede abrir con el dispositivo arrancado (en DeviceAdd daría
// STATUS_DEVICE_NOT_READY). Si falla aquí, se reintenta al crear el primer mando.
NTSTATUS EvtPrepareHardware(WDFDEVICE device, WDFCMRESLIST, WDFCMRESLIST)
{
    Device* dev = GetDevice(device);
    WdfWaitLockAcquire(dev->OpLock, nullptr);
    dev->Started = true;
    (void)EnsureVhfTarget(dev);
    WdfWaitLockRelease(dev->OpLock);
    return STATUS_SUCCESS;
}

NTSTATUS EvtReleaseHardware(WDFDEVICE device, WDFCMRESLIST)
{
    Device* dev = GetDevice(device);
    WdfWaitLockAcquire(dev->OpLock, nullptr);
    DestroyAll(dev, nullptr);
    dev->Started = false;
    if (dev->VhfFile)
    {
        WdfIoTargetClose(dev->VhfTarget);
        dev->VhfFile = nullptr;
    }
    WdfWaitLockRelease(dev->OpLock);
    return STATUS_SUCCESS;
}

// Suspensión, hibernación, apagado o deshabilitar: fuera mandos. Sunshine los vuelve a crear
// cuando el cliente se reconecta.
NTSTATUS EvtD0Exit(WDFDEVICE device, WDF_POWER_DEVICE_STATE)
{
    Device* dev = GetDevice(device);
    WdfWaitLockAcquire(dev->OpLock, nullptr);
    DestroyAll(dev, nullptr);
    WdfWaitLockRelease(dev->OpLock);
    return STATUS_SUCCESS;
}

NTSTATUS EvtDeviceAdd(WDFDRIVER, PWDFDEVICE_INIT init)
{
    // Filtro encima de vhf.sys: VHF sigue siendo el dueño de la energía de la pila.
    WdfFdoInitSetFilter(init);

    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, EvtFileCreate, WDF_NO_EVENT_CALLBACK, EvtFileCleanup);
    fileConfig.AutoForwardCleanupClose = WdfFalse;   // los handles son nuestros; VHF no los ve
    WdfDeviceInitSetFileObjectConfig(init, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDevicePrepareHardware = EvtPrepareHardware;
    pnp.EvtDeviceReleaseHardware = EvtReleaseHardware;
    pnp.EvtDeviceD0Exit = EvtD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(init, &pnp);

    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, Device);
    WDFDEVICE device = nullptr;
    NTSTATUS st = WdfDeviceCreate(&init, &attrs, &device);
    if (!NT_SUCCESS(st)) return st;

    Device* dev = GetDevice(device);
    memset(dev, 0, sizeof *dev);
    dev->Handle = device;
    for (UINT32 i = 0; i < OSMSPAD_MAX_PADS; ++i)
    {
        dev->Pads[i].Parent = dev;
        dev->Pads[i].Index = i;
    }

    WDF_OBJECT_ATTRIBUTES child;
    WDF_OBJECT_ATTRIBUTES_INIT(&child);
    child.ParentObject = device;
    if (!NT_SUCCESS(st = WdfWaitLockCreate(&child, &dev->OpLock))) return st;
    if (!NT_SUCCESS(st = WdfSpinLockCreate(&child, &dev->FbLock))) return st;
    if (!NT_SUCCESS(st = WdfIoTargetCreate(device, &child, &dev->VhfTarget))) return st;
    if (!NT_SUCCESS(st = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_OSMSPAD, nullptr))) return st;

    // Colas sin gestión de energía: por encima del dueño de la energía (vhf.sys) WDF lo exige.
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.PowerManaged = WdfFalse;
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;
    if (!NT_SUCCESS(st = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, nullptr))) return st;

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    queueConfig.PowerManaged = WdfFalse;
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &dev->FeedbackQueue);
}

}  // namespace

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject, PUNICODE_STRING registryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(driverObject, registryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}
