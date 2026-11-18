# Virtual controller protocol notes (phase 4)

Facts for the in-app USB/IP server, the usbip-win2 attach path, the virtual
DualSense and Xbox 360 devices, and HidHide. These are working notes, not code.
Report layouts already covered in [protocol.md](protocol.md) (input state block,
output state block, feature reports `0x05`/`0x09`/`0x20`, checksums) are not
repeated here.

Anything marked **unverified** has to be confirmed on hardware or with a capture
before code depends on it.

## Clean-room note

Sources were read only to extract numbers, layouts and sequences. The reference
client (`DualSenseClient`, GPL) and the Linux kernel driver sources are GPL; no code,
method structure or comments from them may be copied. usbip-win2 is BSD-2-Clause,
HidHide and its NuGet wrapper are MIT, ViGEmBus is BSD-3-Clause; these were also
read for facts only.

Citation shorthands:

| Tag | Meaning |
|---|---|
| `DSC:` | local reference checkout `…\scratchpad\refs\DualSenseClient\source\` (path:line) |
| `UW:` | `https://github.com/vadimgrn/usbip-win2/blob/v.0.9.8.1/` (path:line) |
| `UW-master:` | same repository, branch `master` (unreleased changes) |
| `HH:` | `https://github.com/nefarius/HidHide/blob/master/` |
| `VG:` | `https://github.com/nefarius/ViGEmBus/blob/master/` (archived repo) |
| `KDOC` | https://www.kernel.org/doc/html/latest/usb/usbip_protocol.html |

---

## 1. USB/IP wire format

Protocol version `0x0111` (1.1.1). Every protocol field is big-endian. The 8-byte
USB setup packet inside `CMD_SUBMIT` is carried as raw USB bytes (little-endian
`wValue`/`wIndex`/`wLength`). Default TCP port 3240 (`UW:include/usbip/consts.h`,
`tcp_port`); any port works because the driver takes the port as a string.
Sources: KDOC; `UW:include/usbip/proto.h`; `UW:include/usbip/proto_op.h`.

### 1.1 op_common (8 bytes, starts every handshake message)

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | version, `0x0111` |
| 2 | 2 | code |
| 4 | 4 | status (requests: 0) |

| Code | Message |
|---|---|
| `0x8005` | OP_REQ_DEVLIST |
| `0x0005` | OP_REP_DEVLIST |
| `0x8003` | OP_REQ_IMPORT |
| `0x0003` | OP_REP_IMPORT |

Status values used by usbip-win2 (`UW:include/usbip/consts.h`, `op_status_t`):
0 OK, 1 not available, 2 device busy, 3 device error, 4 no such busid, 5 error.
The kernel doc only defines 0 = OK, 1 = error (KDOC).

### 1.2 Device record (`usbip_usb_device`, 312 bytes)

Offsets relative to the start of the record.

| Offset | Size | Field |
|---|---|---|
| 0x000 | 256 | path, NUL-padded (a virtual device can leave it all zero, `DSC:DualSenseClient.Emulation/UsbIp/UsbIpDeviceInfo.cs:133`) |
| 0x100 | 32 | busid, NUL-terminated, max 31 chars |
| 0x120 | 4 | busnum |
| 0x124 | 4 | devnum |
| 0x128 | 4 | speed (Linux `usb_device_speed`: 0 unknown, 1 low, 2 full, 3 high, 4 wireless, 5 super, 6 super-plus) |
| 0x12C | 2 | idVendor |
| 0x12E | 2 | idProduct |
| 0x130 | 2 | bcdDevice |
| 0x132 | 1 | bDeviceClass |
| 0x133 | 1 | bDeviceSubClass |
| 0x134 | 1 | bDeviceProtocol |
| 0x135 | 1 | bConfigurationValue |
| 0x136 | 1 | bNumConfigurations |
| 0x137 | 1 | bNumInterfaces |

usbip-win2 maps speed to UDE speed: low/full/high/super; wireless is treated as
high; unknown (0) is rejected (`UW:drivers/ude/device.cpp:29-46`).

### 1.3 Handshake messages

| Message | Layout | Total |
|---|---|---|
| OP_REQ_DEVLIST | op_common only (KDOC). usbip-win2's struct also declares a 4-byte reserved body (`UW:include/usbip/proto_op.h:70-73`), unverified whether it is sent | 8 (+4?) |
| OP_REP_DEVLIST | op_common, u32 device count at 0x08, then per device: 312-byte record followed by `bNumInterfaces` × 4-byte interface records (class, subclass, protocol, 0 pad). Connection is closed after this reply (KDOC) | variable |
| OP_REQ_IMPORT | op_common, 32-byte busid at 0x08 | 40 |
| OP_REP_IMPORT | op_common; if status 0, the 312-byte record at 0x08 (no interface records) | 8 or 320 |

### 1.4 URB header (48 bytes, shared by the four URB messages)

Basic part (0x00-0x13):

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0x00 | 4 | command | 1 CMD_SUBMIT, 2 CMD_UNLINK, 3 RET_SUBMIT, 4 RET_UNLINK |
| 0x04 | 4 | seqnum | echo exactly in the reply |
| 0x08 | 4 | devid | client: `(busnum << 16) \| devnum`; server replies: 0 |
| 0x0C | 4 | direction | 0 OUT, 1 IN; server replies: 0 |
| 0x10 | 4 | ep | endpoint *number* without direction bit; 0 in UNLINK and in replies |

CMD_SUBMIT (0x14-0x2F):

| Offset | Size | Field |
|---|---|---|
| 0x14 | 4 | transfer_flags (Linux URB flags) |
| 0x18 | 4 | transfer_buffer_length |
| 0x1C | 4 | start_frame (0 for non-ISO) |
| 0x20 | 4 | number_of_packets (`0xFFFFFFFF` for non-ISO) |
| 0x24 | 4 | interval (usbip-win2 sends the endpoint's bInterval, `UW:drivers/ude/proto.cpp:85`) |
| 0x28 | 8 | setup packet (zero for non-control) |
| 0x30 | n | OUT payload only, n = transfer_buffer_length |
| … | 16 × packets | ISO packet descriptors (offset, length, actual_length, status; all u32) |

RET_SUBMIT (0x14-0x2F):

| Offset | Size | Field |
|---|---|---|
| 0x14 | 4 | status (0 or negative errno: -32 EPIPE = stall, -104 ECONNRESET, -108 ESHUTDOWN, -2 ENOENT, -22 EINVAL) |
| 0x18 | 4 | actual_length |
| 0x1C | 4 | start_frame |
| 0x20 | 4 | number_of_packets (`0xFFFFFFFF` for non-ISO) |
| 0x24 | 4 | error_count |
| 0x28 | 8 | zero padding |
| 0x30 | n | IN payload only, n = actual_length |

CMD_UNLINK: 0x14 = seqnum of the SUBMIT to cancel, 0x18-0x2F zero.
RET_UNLINK: 0x14 = status, rest zero. Rules (KDOC; `UW:drivers/ude/wsk_receive.cpp:448-453`):

- URB still pending and cancelled: RET_UNLINK status `-104` (-ECONNRESET), and **no**
  RET_SUBMIT is sent for it afterwards.
- URB already answered: RET_UNLINK status 0.
- The RET_UNLINK carries the seqnum of the CMD_UNLINK itself, not the cancelled one.

### 1.5 usbip-win2 specifics on the wire

| Fact | Source |
|---|---|
| The seqnum's low bit encodes the transfer direction (1 = IN); the number is `seqnum >> 1`; seqnum with zero number part is invalid. The server only has to echo it. | `UW:drivers/ude/context.h:276-278` |
| The driver accepts only RET_SUBMIT and RET_UNLINK from the server; anything else is a protocol error. | `UW:drivers/ude/wsk_receive.cpp` (validate) |
| RET_SUBMIT `actual_length` must be ≥ 0 and ≤ the URB's buffer length; `number_of_packets` must be `0xFFFFFFFF` or 0..1024. | same |
| For ISO IN, the sum of descriptor `actual_length` must equal the header `actual_length`, or the URB fails. | `UW:drivers/ude/wsk_receive.cpp:175-228`; `DSC:DualSenseClient.Emulation/UsbIp/UsbIpServer.cs:1299-1305` |
| The direction in `transfer_flags` can be wrong; the endpoint descriptor (or the setup packet on EP0) decides direction. | `UW:drivers/ude/proto.cpp:27-30` |
| Endpoints are dynamic (`UdecxEndpointTypeDynamic`): the driver forwards every Windows control request, including all GET_DESCRIPTOR calls, over the wire. It does not cache descriptors itself. | `UW:drivers/ude/device.cpp:508` |
| The driver sets TCP keep-alive on its socket (registry-tunable `TCP_KEEPIDLE`/`TCP_KEEPCNT`/`TCP_KEEPINTVL`). | `UW:drivers/ude/vhci_ioctl.cpp:260-265` |

### 1.6 Attach sequence as usbip-win2 runs it

1. `PLUGIN_HARDWARE` IOCTL arrives (section 2). The driver resolves host and
   service with `getaddrinfo` (numeric service, any address family) and tries each
   result in turn (`UW:drivers/ude/vhci_ioctl.cpp:600-625`, `:500-510`). Pass the
   literal `127.0.0.1` so it never tries `::1` against an IPv4-only listener.
2. TCP connect.
3. Client sends **OP_REQ_IMPORT** directly. No OP_REQ_DEVLIST is sent during
   attach (`UW:drivers/ude/vhci_ioctl.cpp:117-143`). DEVLIST is only used by
   `usbip.exe list -r`.
4. Server answers OP_REP_IMPORT. The driver checks the version, the status, and that
   the busid in the reply equals the requested one (else protocol error, no retry)
   (`UW:drivers/ude/vhci_ioctl.cpp:145-174`).
5. The driver builds `devid = (busnum << 16) | devnum` from the reply
   (`UW:drivers/ude/vhci_ioctl.cpp:203`), creates the UDE device at the reported
   speed and plugs it into a free root-hub port (USB 2 or USB 3 port range by speed).
6. The IOCTL completes only after this (it is pending until then), returning the
   hub port number.
7. Windows enumerates over EP0 via CMD_SUBMIT: GET_DESCRIPTOR device, configuration,
   strings, SET_CONFIGURATION, then class drivers start (section 3.5).

### 1.7 busid / devid conventions

- busid: any string up to 31 bytes. The Linux convention is `<bus>-<port>` (for
  example `1-1`). DSClient registers devices as `<n>-1` starting at `1-1`, with
  busnum = n and devnum = 1 (`DSC:DualSenseClient.Emulation/EmulationServer.cs:17`,
  `:106-109`).
- One TCP connection serves exactly one imported device. Several virtual devices
  means several connections to the same listener, told apart by busid.

### 1.8 Disconnect behaviour

- Server closes the socket (clean stop **or crash**): the driver's receive path
  detaches and deletes the device, **with automatic reattach enabled**
  (`UW:drivers/ude/wsk_receive_events.cpp:110-117`). The device disappears from
  Windows. Pending URBs complete with errors.
- Reattach: after a detach with reattach, the driver waits 30 s, then retries
  connect + import with backoff ×1.5 up to 480 s, 20 attempts (about 2 h) by default
  (`UW:drivers/ude/persistent.cpp:533`; `UW:drivers/ude/usbip2_ude.inf:78-80`;
  `UW:drivers/ude/vhci.cpp:214-245`). Retries stop for ABI, version and protocol
  errors (`UW:drivers/ude/persistent.cpp:780-793`).
  **Consequence:** if Tactile crashes and restarts on the same port with the same
  busid, the driver can reattach the old virtual device by itself within ~30 s to
  8 min. See section 6.
- `PLUGOUT_HARDWARE` from user mode detaches **without** reattach
  (`UW:drivers/ude/vhci_ioctl.cpp:771-801`).
- `PLUGIN_HARDWARE_ONCE` only disables retries for the *initial* attach attempt. It
  is not remembered, so a later socket drop still starts reattach attempts
  (`UW:drivers/ude/vhci_ioctl.cpp:548-558`; `device_attributes` has no "once" field).

---

## 2. usbip-win2 VHCI userspace interface

Valid for **usbip-win2 0.9.8.1** (released 2026-09-27). Layouts changed in 0.9.8.0,
in 0.9.8.1 and again on `master` after the release. See 2.6.

### 2.1 Device interface

| Item | Value | Source |
|---|---|---|
| Interface GUID | `{B4030C06-DC5F-4FCC-87EB-E5515A0935C0}` (`GUID_DEVINTERFACE_USBIP_VHCI`) | `UW:include/usbip/vhci.h:60` |
| Hardware ID of the root device | `ROOT\USBIP_WIN2\UDE` | `UW:userspace/innosetup/setup.iss:62` |
| Device class | USB host controller (`{36FC9E60-C465-11CF-8056-444553540000}`) | `UW:drivers/ude/usbip2_ude.inf:8-9` |
| Path lookup | `CM_Get_Device_Interface_List(GUID, PRESENT)`; libusbip treats more than one path as an error | `UW:userspace/libusbip/src/vhci.cpp:150-180` |
| Example path | `\\?\ROOT#USB#0000#{b4030c06-…}` | `DSC:DualSenseClient.Emulation/Windows/VhciDeviceLocator.cs:8` |
| Open | `CreateFile` with `GENERIC_READ \| GENERIC_WRITE`, share read+write, `OPEN_EXISTING` | `UW:userspace/libusbip/src/vhci.cpp:262-280` |
| ACL | World read/write (any unprivileged process may attach; the source itself flags this as a security concern) | `UW:drivers/ude/vhci.cpp:615-642`; `UW:drivers/ude/vhci_ioctl.cpp:627-651` |
| Default ports | 30 USB 2.0 + 30 USB 3.0 hub ports (`NumberOfUsb20Ports`, `NumberOfUsb30Ports`) | `UW:drivers/ude/usbip2_ude.inf:64-67` |

### 2.2 IOCTL codes

All are `CTL_CODE(FILE_DEVICE_UNKNOWN, fn, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)`
(`UW:include/usbip/vhci.h:117-141`).

| Name | Function | Code |
|---|---|---|
| PLUGIN_HARDWARE | 0x800 | `0x0022E000` |
| PLUGOUT_HARDWARE | 0x801 | `0x0022E004` |
| GET_IMPORTED_DEVICES | 0x802 | `0x0022E008` |
| SET_PERSISTENT | 0x803 | `0x0022E00C` |
| GET_PERSISTENT | 0x804 | `0x0022E010` |
| STOP_ATTACH_ATTEMPTS | 0x805 | `0x0022E014` |
| PLUGIN_HARDWARE_ONCE | 0x806 | `0x0022E018` |
| PLUGOUT_HARDWARE_AND_REATTACH | 0x807 | `0x0022E01C` (driver-internal) |

The driver also answers `ReadFile` on the handle with `device_state` records
(state change events). State values: 0 unplugged, 1 connecting, 2 connected,
3 plugged, 4 disconnected, 5 unplugging (`UW:include/usbip/vhci.h:99`).

### 2.3 Struct layouts in 0.9.8.1 (x64)

Strings are NUL-terminated UTF-8. Natural MSVC packing. The structs use C++
inheritance, so each base part keeps its own padded size. That is why `serial`
starts at 1104 and not at 1101. DSClient's recorded size of 1124 for
`plugin_hardware` matches this (`DSC:DualSenseClient.Emulation/Windows/VhciStructures.cs:15-43`).

`imported_device_location` (1100 bytes incl. 3 pad): port i32 @0, location_hash u32 @4,
busid[32] @8, service[32] @40, host[1025] @72.

**plugin_hardware**, 1124 bytes (`UW:include/usbip/vhci.h:144-148`):

| Offset | Size | Field | Dir |
|---|---|---|---|
| 0 | 4 | size = 1124 | in |
| 4 | 4 | port (≥1 on success, 0 on error) | out |
| 8 | 4 | location_hash (hash of host, service, busid) | out (see note) |
| 12 | 32 | busid | in |
| 44 | 32 | service = TCP port as decimal string, for example `"3240"` | in |
| 76 | 1025 | host, for example `"127.0.0.1"` | in |
| 1101 | 3 | padding | |
| 1104 | 16 | serial override (empty = keep the device's own; ASCII alphanumeric only, else rejected) | in |
| 1120 | 1 | wsk_events (0 = default receive mode, 1 = WSK event receive) | in |
| 1121 | 3 | padding | |

Driver checks: input length == sizeof and `size` == length, else
`STATUS_INVALID_BUFFER_SIZE` / `USBIP_ERROR_ABI`. The driver reports only **8 bytes**
written (size + port), so `location_hash` is not returned through this IOCTL. The
call is synchronous for the caller but pends in the driver until the import
handshake and plug-in finish (`UW:drivers/ude/vhci_ioctl.cpp:740-766`;
`UW:userspace/libusbip/src/vhci.cpp:330-358`). Connect errors come back as Win32
errors; libusbip maps not-found to `WSAHOST_NOT_FOUND` and timeout to `WSAETIMEDOUT`.

**plugout_hardware**, 8 bytes: size u32 @0 = 8, port i32 @4.
`port` > 0 detaches that port (`STATUS_INVALID_PARAMETER` if out of range,
`STATUS_DEVICE_NOT_CONNECTED` if empty). `port` = -1 (`PORT_ALL`) detaches and deletes
all. `port` = -2 (`PORT_ALL_CLOSEONLY`) closes all sockets without plug-out
(`UW:include/usbip/vhci.h:155-160`; `UW:drivers/ude/vhci_ioctl.cpp:771-801`). No
output buffer is needed.

**stop_attach_attempts**, 1108 bytes: size @0, location (port @4, hash @8 out, busid
@12, service @44, host @76), count i32 @1104 (out: number of cancelled retry requests).
All three strings empty = stop **all** pending reattach attempts. All three set =
stop for that location. Partly set = invalid parameter
(`UW:drivers/ude/vhci_ioctl.cpp:694-736`).

**get_imported_devices**: input is just the u32 `size` field = 1136 (= sizeof the
struct with one element). Output = 4-byte header + N × 1132-byte `imported_device`.
`STATUS_BUFFER_TOO_SMALL` (Win32 `ERROR_INSUFFICIENT_BUFFER`) if too small. libusbip
retries with room for 4, 8, … 1024 devices (`UW:drivers/ude/vhci_ioctl.cpp:806-855`;
`UW:userspace/libusbip/src/vhci.cpp:286-330`).

`imported_device` (1132 bytes) = `imported_device_location` (1100: port @0, hash @4,
busid @8, service @40, host @72) + `imported_device_properties` at 1100: devid u32
@1100, speed i32 @1104, vendor u16 @1108, product u16 @1110, serial[16] @1112,
iserial u8 @1128, wsk_events u8 @1129, 2 pad.

`device_state` (ReadFile record): size u32 @0, imported_device @4 (1132), state
i32 @1136, source_id u32 @1140 → 1144 bytes. These offsets are computed from the
header and **unverified**. A wrong size is rejected with `USBIP_ERROR_ABI`, so
probing is safe.

### 2.4 Operations

| Task | How |
|---|---|
| Attach to our server | PLUGIN_HARDWARE with host `127.0.0.1`, service = listener port as string, busid = our busid. Remember the returned port. |
| Detach | PLUGOUT_HARDWARE with that port (no reattach afterwards). |
| List | GET_IMPORTED_DEVICES. Ours are the entries whose host is `127.0.0.1` and whose service equals our port (or any port we used before). |
| Cancel background retries | STOP_ATTACH_ATTEMPTS for our location, or with empty strings for all (all also affects other users' devices). |
| CLI fallback | `usbip.exe --tcp-port <port> attach -r <host> -b <busid>` prints "successfully attached to port N"; `usbip.exe detach -p N`; `usbip.exe detach --all=closeonly` (`DSC:DualSenseClient.Emulation/Windows/UsbIpExeAttachStrategy.cs:10-11`; `UW:README.md:125-145`; `UW:userspace/innosetup/setup.iss:281`). Install dir `C:\Program Files\USBip`, not on PATH (`DSC:…/UsbIpExeAttachStrategy.cs:218-219`). |

The handle is only a control channel. The attachment belongs to the driver once the
IOCTL succeeds, and closing the handle does not detach
(`DSC:DualSenseClient.Emulation/Windows/VhciIoctlAttachStrategy.cs:214-221`).

### 2.5 Detecting the installed version

| Method | Detail |
|---|---|
| Uninstall key | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1`, value `DisplayVersion` (Inno Setup AppId, `UW:userspace/innosetup/setup.iss:56`; winget manifest `ProductCode` and `DisplayVersion`) |
| Driver | DriverVersion of the device with hardware ID `ROOT\USBIP_WIN2\UDE` (SetupAPI/CM property), or file version of `usbip2_ude.sys` in the DriverStore |
| Behaviour probe | Send PLUGOUT_HARDWARE with an invalid port; an ABI mismatch returns `USBIP_ERROR_ABI` instead of invalid-parameter. **unverified** as a reliable probe |

Choose the struct layout **by version**, not by trial. Two releases share the
1120-byte size with different field offsets (2.6).

### 2.6 Layout history (risk)

| Release | `plugin_hardware` | Notes |
|---|---|---|
| 0.9.7.8 (2026-07-04) | no `wsk_events`, no `location_hash`; probably 1116 bytes (**unverified**) | `UW` tag `v.0.9.7.8` `include/usbip/vhci.h` |
| 0.9.8.0 (2026-09-07) | 1120: size @0, port @4, busid @8, service @40, host @72, serial @1100, wsk_events @1116 | tag `v.0.9.8.0` |
| **0.9.8.1** (2026-09-27) | **1124**: as in 2.3 (location_hash added, commit 6b3af1f2, 2026-09-22) | current release |
| master after 2026-10-18 | 1120 again but different: size @0, port @4, hash @8, busid @12, service @44, host @76, serial @1101, wsk_events @1117 (composition instead of inheritance, commit e653984c) | not released yet |

Function numbers 0x800-0x807 did not change across these versions.

---

## 3. Virtual DualSense (HID only)

### 3.1 Real device (USB, for reference)

Source: public `lsusb -v` dump
(https://discussion.fedoraproject.org/t/playstation-5-dualsense-controller-only-acting-as-a-mouse-via-its-touchpad/126984);
the bytes come from `DSC:DualSenseClient.Emulation/Devices/DualSense/DualSenseDescriptorSet.cs`
and `DSC:DualSenseClient.Emulation/Core/UsbAudioClass.cs`, cross-checked against that dump.

Device descriptor (18 bytes): `12 01 00 02 00 00 00 40 4C 05 E6 0C 00 01 01 02 00 01`

| Field | Value |
|---|---|
| bcdUSB | 2.00 (the reference notes 2.10 only with remote wake on, `DSC:…/DualSenseDescriptorSet.cs:76`) |
| class/sub/proto | 00/00/00 (no IAD) |
| bMaxPacketSize0 | 64 |
| VID/PID/bcdDevice | 054C / 0CE6 / 0x0100 (Edge: PID 0DF2) |
| iManufacturer 1 | "Sony Interactive Entertainment" |
| iProduct 2 | "DualSense Wireless Controller" |
| iSerialNumber | 0 (none) |
| Speed | the real interval values (ISO bInterval 4, interrupt bInterval 6) only make sense at **high speed**: 1 ms ISO, 4 ms interrupt (250 Hz). DSClient exports **full speed** (`DSC:DualSenseClient.Emulation/Core/VirtualUsbDevice.cs:128-130`) and rewrites intervals to full-speed frames |

Configuration descriptor: 227 bytes (`wTotalLength 0x00E3`), 4 interfaces, value 1,
bmAttributes `0xC0`, MaxPower `0xFA` (500 mA).

| If | Alt | Class | Endpoints |
|---|---|---|---|
| 0 | 0 | Audio Control (01/01/00) | none |
| 1 | 0, 1 | Audio Streaming (01/02/00) | alt 1: `0x01` ISO OUT adaptive (attr 0x09), 392 B, bInterval 4 (HS) |
| 2 | 0, 1 | Audio Streaming (01/02/00) | alt 1: `0x82` ISO IN async (attr 0x05), **196 B** real, bInterval 4 (HS) |
| 3 | 0 | HID (03/00/00) | `0x84` interrupt IN 64 B, `0x03` interrupt OUT 64 B, bInterval 6 (HS) |

Full 227-byte configuration as a real high-speed device presents it. The audio block
is the 186-byte DSClient block with the real mic size (`C4 00`) and the real
intervals (4/4/6/6) put back. DSClient serves `C0 00` and interval 1/1/2/2.

```
09 02 E3 00 04 01 00 C0 FA
-- audio control, interface 0 (73-byte class block)
09 04 00 00 00 01 01 00 00
0A 24 01 00 01 49 00 02 01 02
0C 24 02 01 01 01 06 04 33 00 00 00
0C 24 06 02 01 01 03 00 00 00 00 00
09 24 03 03 01 03 04 02 00
0C 24 02 04 02 04 03 02 03 00 00 00
09 24 06 05 04 01 03 00 00
09 24 03 06 01 01 01 05 00
-- speaker streaming, interface 1 (4 ch, 16 bit, 48 kHz)
09 04 01 00 00 01 02 00 00
09 04 01 01 01 01 02 00 00
07 24 01 01 01 01 00
0B 24 02 01 04 02 10 01 80 BB 00
09 05 01 09 88 01 04 00 00
07 25 01 00 00 00 00
-- microphone streaming, interface 2 (2 ch, 16 bit, 48 kHz)
09 04 02 00 00 01 02 00 00
09 04 02 01 01 01 02 00 00
07 24 01 06 01 01 00
0B 24 02 01 02 02 10 01 80 BB 00
09 05 82 05 C4 00 04 00 00
07 25 01 00 00 00 00
-- HID, interface 3
09 04 03 00 02 03 00 00 00
09 21 11 01 00 01 22 11 01
07 05 84 03 40 00 06
07 05 03 03 40 00 06
```

The interrupt and ISO interval bytes above are the high-speed values from the dump.
The rest of the audio block is the reference's transcription and was not compared
byte by byte against a real capture (**unverified**).

### 3.2 HID-only variant

What a HID-only virtual DualSense needs (DSClient's own phase-3 bring-up used
interface 0, `DSC:…/DualSenseDescriptorSet.cs:115-116`):

| Item | Value |
|---|---|
| Configuration | `09 02 29 00 01 01 00 C0 FA`: wTotalLength 41 (9 + 9 + 9 + 7 + 7), 1 interface |
| Interface | `09 04 00 00 02 03 00 00 00` (renumbered to 0) |
| HID descriptor | `09 21 11 01 00 01 22 11 01` (bcdHID 1.11, report descriptor length 273 = `0x0111`) |
| EP IN | `07 05 84 03 40 00 xx` |
| EP OUT | `07 05 03 03 40 00 xx` |
| Interval `xx` | exported as high speed (speed 3): 6 = 4 ms like the real pad, 4 = 1 ms. Exported as full speed (speed 2): value in ms, for example 4 (real rate) or 1. DSClient: full speed, 2 |
| Endpoint addresses | keep 0x84/0x03; numbers need not be contiguous |
| Composite | with 1 interface and class 00, Windows binds HID directly to the device (no usbccgp). Keep the interface at 0. If the number stays 3, Windows still loads usbccgp only for multi-interface devices (**unverified**) |
| Audio | without interfaces 0-2 the host sees no speaker or haptics endpoint. Games that drive haptics through the USB audio device will find nothing; rumble-emulation output (Flags A bit 0/1) still arrives in report `0x02` |

Note for SDL/Steam: they recognise the device by VID/PID plus the report descriptor,
not by the audio interfaces. Whether any game refuses a DualSense without audio is
**unverified**.

### 3.3 HID report descriptor (273 bytes, real, USB)

From `DSC:DualSenseClient.Emulation/Devices/DualSense/DualSenseDescriptorSet.cs:161-300`.
Byte count checked: 273, matching the real `wDescriptorLength`.

```
05 01 09 05 A1 01 85 01 09 30 09 31 09 32 09 35
09 33 09 34 15 00 26 FF 00 75 08 95 06 81 02 06
00 FF 09 20 95 01 81 02 05 01 09 39 15 00 25 07
35 00 46 3B 01 65 14 75 04 95 01 81 42 65 00 05
09 19 01 29 0F 15 00 25 01 75 01 95 0F 81 02 06
00 FF 09 21 95 0D 81 02 06 00 FF 09 22 15 00 26
FF 00 75 08 95 34 81 02 85 02 09 23 95 2F 91 02
85 05 09 33 95 28 B1 02 85 08 09 34 95 2F B1 02
85 09 09 24 95 13 B1 02 85 0A 09 25 95 1A B1 02
85 20 09 26 95 3F B1 02 85 21 09 27 95 04 B1 02
85 22 09 40 95 3F B1 02 85 80 09 28 95 3F B1 02
85 81 09 29 95 3F B1 02 85 82 09 2A 95 09 B1 02
85 83 09 2B 95 3F B1 02 85 84 09 2C 95 3F B1 02
85 85 09 2D 95 02 B1 02 85 A0 09 2E 95 01 B1 02
85 E0 09 2F 95 3F B1 02 85 F0 09 30 95 3F B1 02
85 F1 09 31 95 3F B1 02 85 F2 09 32 95 0F B1 02
85 F4 09 35 95 3F B1 02 85 F5 09 36 95 03 B1 02
C0
```

The DualSense Edge uses a different 389-byte descriptor
(`DSC:…/DualSenseEdgeDescriptorSet.cs`). It is out of scope for now.

Report sizes this descriptor gives Windows (including the ID byte):

| Report | Type | Size |
|---|---|---|
| `0x01` | input | 64 |
| `0x02` | output | **48** (47 + ID). Windows `WriteFile`/`HidD_SetOutputReport` uses this size, not the 63 bytes in protocol.md |
| Feature | `0x05` 41, `0x08` 48, `0x09` 20, `0x0A` 27, `0x20` 64, `0x21` 5, `0x22` 64, `0x80` 64, `0x81` 64, `0x82` 10, `0x83` 64, `0x84` 64, `0x85` 3, `0xA0` 2, `0xE0` 64, `0xF0` 64, `0xF1` 64, `0xF2` 16, `0xF4` 64, `0xF5` 4 | `DSC:…/DualSenseFeatureReports.cs:161-180` |

### 3.4 String descriptors

| Index | Content |
|---|---|
| 0 | LANGID list `04 03 09 04` (en-US) |
| 1 | "Sony Interactive Entertainment" (UTF-16LE, bLength = 2 + 2·chars) |
| 2 | "DualSense Wireless Controller" |
| 3 | not present on the retail pad (iSerialNumber 0). usbip-win2 can inject a serial through the `serial` field (2.3); doing that gives each virtual pad a stable Windows instance ID (**unverified** whether Windows uses it when iSerialNumber was 0) |

Sources: `DSC:DualSenseClient.Emulation/Devices/DualSense/DualSenseIdentity.cs`;
`DSC:DualSenseClient.Emulation/Core/VirtualUsbDevice.cs:459-476`.

### 3.5 Control requests to expect

| Request | bmRequestType / bRequest / wValue | Expected reply | Source |
|---|---|---|---|
| GET_DESCRIPTOR device | 80 06 0100 | 18 bytes (first read may use wLength 64 or 8) | USB 2.0 §9.4.3 |
| GET_DESCRIPTOR config | 80 06 0200 | first 9 bytes, then full wTotalLength | same |
| GET_DESCRIPTOR string | 80 06 03xx, wIndex 0409 | as 3.4; unknown index: stall | same |
| GET_DESCRIPTOR device qualifier (06) / BOS (0F) / other | 80 06 06xx / 0Fxx | stall is accepted (DSClient stalls every type except device, config, string and report; enumeration works) | `DSC:DualSenseClient.Emulation/Core/VirtualUsbDevice.cs:395-414` |
| MS OS string `0xEE` | 80 06 03EE | stall (Windows probes it once per VID/PID/REV) | **unverified** that it is sent over USB/IP |
| SET_ADDRESS | 00 05 | accept, no state (UDE may handle it locally; harmless either way) | `DSC:…/VirtualUsbDevice.cs:305-310` |
| SET_CONFIGURATION | 00 09 0001 | accept; start the interrupt IN pacing | |
| GET_STATUS | 80/81/82 00 | 2 zero bytes | `DSC:…/VirtualUsbDevice.cs:296-301,498-499` |
| SET/CLEAR_FEATURE | 00/02 03 / 01 | accept (endpoint halt) | `DSC:…/VirtualUsbDevice.cs:333-342` |
| SET_INTERFACE | 01 0B | only for audio alt settings | |
| GET_DESCRIPTOR HID report | **81** 06 2200, wIndex = interface | 273 bytes (interface recipient, seen on a real Windows enumeration) | `DSC:…/VirtualUsbDevice.cs:283-286` |
| SET_IDLE | 21 0A | accept (or stall; hidusb ignores a stall) | `DSC:DualSenseClient.Emulation/Core/VirtualHidDevice.cs:189-190` |
| GET_IDLE / GET/SET_PROTOCOL | A1 02 / A1 03 / 21 0B | 1 byte (0) / 1 (report protocol) / accept | `DSC:…/VirtualHidDevice.cs:186-196` |
| GET_REPORT feature | A1 01 03xx, wLength | report bytes starting with the ID; truncate to wLength | `DSC:…/VirtualHidDevice.cs:242-244` |
| SET_REPORT feature | 21 09 03xx | data stage starts with the report ID on Windows (Linux may omit it) | `DSC:…/VirtualHidDevice.cs:314-320` |
| SET_REPORT output | 21 09 0202 | same 48-byte output report as the interrupt OUT path | `DSC:…/VirtualHidDevice.cs:273-276` |

Feature reads seen from host software (DSClient's reference prefetches these:
`DSC:DualSenseClient.Emulation/Devices/DualSense/DualSenseFeatureReports.cs:10-30`):

| ID | Who reads it | What to answer |
|---|---|---|
| `0x05` (41) | SDL, Steam, Linux driver, games: IMU calibration. On BT it also switches the pad to full reports | forward the real pad's bytes (protocol.md) |
| `0x09` (20) | pairing info, MAC at bytes 1-6 reversed. Steam and SDL use it as the controller's identity | real MAC, or a stable fake. Bytes 7-8 = `08 25` in DSClient's stub (`…/DualSenseFeatureReports.cs:105-114`) |
| `0x20` (64) | firmware info; byte 22 = `0x44` means "Edge" to some consumers | forward the real pad's bytes; never `0x44` for a non-Edge |
| `0x22` (64) | second hardware-info page; one third-party driver reads it once `0x20` looks real and checks byte 0 = ID | zero stub with ID byte |
| `0x80` write → `0x81` read | factory command/response (serial number sub-command 0x01, status 0x03) | DSClient answers a stub; low priority |
| others listed in 3.3 | rarely read | DSClient: exact-length zero stub with ID set. An ID not in the descriptor stalls |

Over BT the real pad's feature replies carry a CRC and Windows reports a
547-byte length (protocol.md). The virtual USB device must return the USB-shaped
report with no CRC and the length from the table above.

### 3.6 Input and output data path

- **Input `0x01` (64 B) on EP 0x84 IN**: byte 0 = `0x01`, bytes 1-63 = the 63-byte
  state block. BT `0x31` has the state block at byte 2. So
  `usb[1..63] = bt[2..64]`, an offset shift of 1. BT bytes 65-73 (padding) and
  74-77 (CRC) are dropped. The state block's own counter/timestamp fields
  (protocol.md) pass through unchanged.
- The host keeps one or more interrupt IN URBs pending. The server completes one per
  interval tick with the latest report, or leaves it pending until new data
  (DSClient: pacer per IN endpoint, 1 ms default tick,
  `DSC:DualSenseClient.Emulation/UsbIp/UsbIpServer.cs:35-36`, `:1464-1501`).
  Answering an IN URB with a 0-byte success is a short packet and is legal, but
  hidclass treats it as an empty read. Keep URBs pending instead.
- **Output `0x02`**: arrives as CMD_SUBMIT OUT on EP 3 (direction 0, ep 3) with
  48 bytes when a game uses `WriteFile`, or as SET_REPORT(Output, ID 2) on EP0 when it
  uses `HidD_SetOutputReport`. Both carry the ID byte first. Answer the OUT URB with
  RET_SUBMIT status 0 and `actual_length` = bytes consumed, no payload
  (`DSC:…/UsbIpServer.cs:1563-1565`). Forward bytes 1-47 as the 47-byte state block
  into a BT `0x31` output report (protocol.md).
- Edge output report `0x02` is 64 bytes (63 + ID) (`DSC:…/DualSenseDevice.cs:510`).

---

## 4. Virtual Xbox 360 wired controller

Sources: `VG:sys/XusbPdo.cpp` (BSD-3, captured-from-hardware values) and
`DSC:DualSenseClient.Emulation/Devices/Xbox360/*`.

### 4.1 Device descriptor

`12 01 00 02 FF FF FF 08 5E 04 8E 02 14 01 01 02 03 01`

| Field | Value |
|---|---|
| bcdUSB | 2.00 |
| class/sub/proto | FF/FF/FF (vendor; Windows does **not** load usbccgp, xusb22 binds the whole device) |
| bMaxPacketSize0 | 8 |
| VID/PID/bcdDevice | 045E / 028E / 0x0114 |
| Strings | 1 "©Microsoft Corporation", 2 "Controller", 3 serial (DSClient uses "296013F"). `VG:sys/XusbPdo.cpp:436-451`; `DSC:…/Xbox360DescriptorSet.cs:1-12` |
| Speed | full speed (2) in DSClient's export; ViGEm declares high speed through its bus interface, which is not a wire fact (`DSC:…/Xbox360Device.cs:16-20`) |

### 4.2 Configuration descriptor (full, 153 bytes)

`VG:sys/XusbPdo.cpp:297-431`:

```
09 02 99 00 04 01 00 A0 FA
-- if 0: gamepad, FF/5D/01, 2 endpoints
09 04 00 00 02 FF 5D 01 00
11 21 00 01 01 25 81 14 00 00 00 00 13 01 08 00 00
07 05 81 03 20 00 04
07 05 01 03 20 00 08
-- if 1: audio/headset, FF/5D/03, 4 endpoints
09 04 01 00 04 FF 5D 03 00
1B 21 00 01 01 01 82 40 01 02 20 16 83 00 00 00 00 00 00 16 03 00 00 00 00 00 00
07 05 82 03 20 00 02
07 05 02 03 20 00 04
07 05 83 03 20 00 40
07 05 03 03 20 00 10
-- if 2: expansion port / chatpad, FF/5D/02, 1 endpoint
09 04 02 00 01 FF 5D 02 00
09 21 00 01 01 22 84 07 00
07 05 84 03 20 00 10
-- if 3: security, FF/FD/13, no endpoints, iInterface 4
09 04 03 00 00 FF FD 13 04
06 41 00 01 01 03
```

- bmAttributes `0xA0` (bus powered, remote wakeup), 500 mA.
- The `0x21` blocks are vendor descriptors that reuse the HID type number but are not
  HID. The 17-byte interface-0 block declares types `0x81` (len 20 = input packet),
  `0x00`, `0x13` (len 0x0801) and is truncated. Copy it as-is.
- Interval units are 1 ms frames at full speed.
- String 4 (security interface) on real pads reads "Xbox Security Method 3,
  Version 1.00, © 2005 Microsoft Corporation. All rights reserved." (**unverified**;
  commonly reported in lsusb dumps, not in the sources read here).

**Minimal variant** (DSClient bring-up, 49 bytes, interface 0 only; works with
xusb22 according to DSClient, `DSC:…/Xbox360DescriptorSet.cs:112-160`):
`09 02 31 00 01 01 00 A0 FA` + interface-0 block + both EP 0x81/0x01 descriptors.
Whether xusb22 needs interfaces 1-3 for anything (headset, security handshake) is
**unverified**. DSClient ships the minimal one.

### 4.3 Input packet (EP 0x81 IN, 20 bytes)

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | message type `0x00` |
| 1 | 1 | length `0x14` |
| 2 | 2 | buttons, LE: `0001` up, `0002` down, `0004` left, `0008` right, `0010` Start, `0020` Back, `0040` LS, `0080` RS, `0100` LB, `0200` RB, `0400` Guide, `1000` A, `2000` B, `4000` X, `8000` Y |
| 4 | 1 | LT 0-255 |
| 5 | 1 | RT 0-255 |
| 6 | 2 | LX int16 LE |
| 8 | 2 | LY int16 LE (positive = up) |
| 10 | 2 | RX int16 LE |
| 12 | 2 | RY int16 LE (positive = up) |
| 14 | 6 | zero |

Sources: `DSC:DualSenseClient.Emulation/Devices/Xbox360/Xbox360InputReport.cs:3-9,62-92`;
`VG:sys/XusbPdo.hpp:40-51`. "Positive = up" is the XInput convention and the
opposite of the DualSense byte axes (**verify** with XInput after the first build).

### 4.4 Startup packets on the interrupt IN pipe

ViGEm answers the first IN URBs on EP 0x81 with this sequence before any real
input, and the first IN on EP 0x83 with one capability packet
(`VG:sys/XusbPdo.cpp:249-268`, `:713-800`):

| # | Pipe | Bytes |
|---|---|---|
| 0 | 0x81 | `01 03 0E` (LED status) |
| 1 | 0x81 | `02 03 00` |
| 2 | 0x81 | `03 03 03` |
| 3 | 0x81 | `08 03 00` (headset status) |
| 4 | 0x81 | 20-byte input packet `00 14 00 00 00 00 E4 F2 B3 F8 49 F3 B0 FC 00 00 00 00 00 00` (captured idle sticks) |
| 5 | 0x81 | `01 03 03` |
| – | 0x83 | `05 03 00` (once, then pending) |

DSClient sends none of these, only 20-byte input packets, and reports that xusb22
works. Whether the status packets matter (for example for XInput battery or headset
state) is **unverified**.

### 4.5 Output packets (EP 0x01 OUT)

| Packet | Bytes | Meaning |
|---|---|---|
| Rumble (8) | `00 08 00 LL SS 00 00 00` | LL = large/low-frequency motor 0-255, SS = small/high-frequency 0-255 |
| LED (3) | `01 03 NN` | LED pattern. xusb22 sends NN = 2..5 to mean "flash, then player 1..4 on" |

Rumble: `DSC:…/Xbox360OutputReport.cs:1-16`; `VG:sys/XusbPdo.cpp:846-889`.
LED: `VG:sys/XusbPdo.cpp:817-837` (2→player 1 … 5→player 4). Other NN values
(0 off, 1 all blink, 6-9 steady 1-4, 10 rotate, 11-13 blink modes) are community
documentation (**unverified**). The XInput user index comes from xusb22's LED
choice. The device does not pick it.

### 4.6 Vendor control requests

ViGEm decides by wLength of the control transfer (`VG:sys/XusbPdo.cpp:933-972`):

| wLength | Reply |
|---|---|
| 4 | `31 3F CF DC` ("Xenon magic") |
| 20 (0x14) | stall (must fail) |
| 8 | stall (must fail) |
| other | success, no data |

The exact bmRequestType/bRequest/wValue of these requests were not recorded in the
source. DSClient stalls all class and vendor requests and still works
(`DSC:…/Xbox360Device.cs:6-8`). Log the first setup packets on hardware
(**unverified**).

---

## 5. HidHide control interface

Sources: `HH:DEVELOPER.md`; `HH:Shared/HidHideIoctlContract.h`;
`HH:HidHideCLI/src/FilterDriverProxy.cpp`; `HH:HidHide/src/ControlDevice.c`.

| Item | Value |
|---|---|
| Control device | `\\.\HidHide` (or interface GUID `{0C320FF7-BD9B-42B6-BDAF-49FEB9C91649}`) |
| Open | `CreateFileW(GENERIC_READ, share R/W/D, OPEN_EXISTING)` |
| **Exclusive** | the control device is created exclusive: only one handle at a time system-wide. If the HidHide config UI or another feeder app holds it, opening fails. Open, act, close at once (`HH:HidHide/src/ControlDevice.c:40`) |
| ACL | world RWX; no admin needed (`HH:HidHide/src/ControlDevice.c:25`) |

IOCTLs: `CTL_CODE(32769, fn, METHOD_BUFFERED, FILE_READ_DATA)`:

| Name | fn | Code | In | Out |
|---|---|---|---|---|
| GET_WHITELIST | 2048 | `0x80016000` | – | MULTI_SZ |
| SET_WHITELIST | 2049 | `0x80016004` | MULTI_SZ | – |
| GET_BLACKLIST | 2050 | `0x80016008` | – | MULTI_SZ |
| SET_BLACKLIST | 2051 | `0x8001600C` | MULTI_SZ | – |
| GET_ACTIVE | 2052 | `0x80016010` | – | 1-byte BOOLEAN |
| SET_ACTIVE | 2053 | `0x80016014` | 1-byte BOOLEAN | – |
| GET_WLINVERSE | 2054 | `0x80016018` | – | BOOLEAN |
| SET_WLINVERSE | 2055 | `0x8001601C` | BOOLEAN | – |
| ADD_SESSION_BLACKLIST | 2056 | `0x80016020` | (MULTI_SZ, **unverified**) | – |
| CLR_SESSION_BLACKLIST | 2057 | `0x80016024` | – | – |

Buffer rules:

- MULTI_SZ of UTF-16 strings, each NUL-terminated, plus a final NUL. The byte count
  must be even and include all terminators.
- GET lists: call once with no buffer; `BytesReturned` gives the needed size. Call
  again with that size (`HH:HidHideCLI/src/FilterDriverProxy.cpp:52-80`).
- SET replaces the **whole** list. Read-modify-write, and keep entries owned by
  other tools ("don't assume exclusive ownership", `HH:README.md` Package integration).
- **Blacklist** entries are device instance IDs as from
  `SetupDiGetDeviceInstanceId` / `CM_Get_Device_ID`, for example
  `HID\VID_054C&PID_0CE6&MI_03\…` (USB) or
  `HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\…` (BT).
  Blacklist the HID-class child (the filter sits on HIDClass), not the USB/BT parent.
  The entries persist in the registry across reboots.
- **Whitelist** entries are full image paths in **NT device form**:
  `\Device\HarddiskVolumeN\Program Files\…\Tactile.exe`, not `C:\…`. The CLI converts
  with `QueryDosDevice` on the volume (`HH:HidHideCLI/src/Volume.cpp:17-34`). The
  NuGet wrapper `Nefarius.Drivers.HidHide` 3.4.0 used by DSClient does the conversion
  itself (`DSC:DualSenseClient.HidHide/HidHideService.cs`; the csproj).
- Hiding only takes effect while ACTIVE = true. DSClient turns ACTIVE on when it hides
  the first device and off when its last one is unhidden
  (`DSC:DualSenseClient.HidHide/HidHideService.cs:80-101`). That can conflict with
  the user's own HidHide setup.

Getting the instance ID from a HID device path (what hidapi/HidSharp returns,
`\\?\HID#VID_054C&PID_0CE6&MI_03#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}`):

1. Preferred: `CM_Get_Device_Interface_Property(path, DEVPKEY_Device_InstanceId)`
   (DSClient uses `PnPDevice.GetInstanceIdFromInterfaceId`,
   `DSC:DualSenseClient.HidHide/HidHideService.cs:132-141`).
2. Fallback: drop `\\?\`, drop the trailing `#{guid}`, replace `#` with `\`
   (`DSC:…/HidHideService.cs:148-160`). Instance IDs compare case-insensitively.

What to whitelist: Tactile's own exe (it must keep reading the hidden real pad).
Do **not** whitelist Steam if the aim is to stop Steam double-reading the real pad.

Gotchas:

- Changes apply to new opens. A game that already has the pad open keeps its handle
  until it reopens (**unverified**, but follows from a filter that checks at
  create time).
- Session blacklist (2056/2057): entries are per process, kept only in kernel memory
  and removed automatically when the process exits, including on a crash. This is
  ideal for us, but it exists only on `master` (added in 2026). It is **not** in the
  latest GitHub release 1.5.230, and not in tag v1.7.346.0. Detect support by trying
  the IOCTL; older drivers return invalid-parameter for unknown codes
  (`HH:HidHide/src/ControlDevice.c:367-368`).
- Registry markers: `HKCR\Installer\Dependencies\NSS.Drivers.HidHide.x64\Version` =
  installed version; `HKCR\SOFTWARE\Nefarius Software Solutions e.U.\Nefarius Software Solutions e.U. HidHide\Path`
  = install path (`HH:README.md` Package integration).
- The filter is an upper class filter on HIDClass, XnaComposite and XboxComposite
  (`HH:Installer/Program.cs:17-24,142-144`). Our virtual USB DualSense (HID) and
  Xbox 360 (XnaComposite) devices fall under it too. Never put their instance IDs on
  the list.

---

## 6. Crash cleanup facts

| Fact | Source |
|---|---|
| If Tactile dies, its listener socket closes, so the driver detaches the virtual device by itself (no ghost device stays plugged). | 1.8 |
| The driver then queues reattach attempts to the same host:port/busid (30 s first, up to 480 s apart, 20 tries ≈ 2 h). A new Tactile on the same port and busid would be reconnected to by the driver without asking. | 1.8 |
| Cleanup on startup: STOP_ATTACH_ATTEMPTS for each location we may have used (or all), then GET_IMPORTED_DEVICES and PLUGOUT any entry with host `127.0.0.1` and our service/port. Note that "all" also touches other users' USB/IP devices. | 2.3, 2.4 |
| Alternative: bind a fresh random port per run so stale retries hit nothing (they fail with connection refused and keep retrying until the limit). | derived |
| The installer registers a scheduled task "USBip Detach All On Reboot Or Shutdown" that runs `usbip.exe detach --all=closeonly` on shutdown/reboot events. | `UW:userspace/innosetup/setup.iss:57,252-290` |
| Persistent devices (SET_PERSISTENT, registry value `PersistentDevices`) are re-attached at boot. Never use them for a virtual device. | `UW:include/usbip/consts.h`; `UW:drivers/ude/vhci_ioctl.cpp:900-960` |
| DSClient keeps the VHCI port per busid in memory only. It does **not** enumerate imported devices, does not call STOP_ATTACH_ATTEMPTS and does no startup cleanup. After a crash it cannot detach a leftover. | `DSC:DualSenseClient.Emulation/Windows/VhciIoctlAttachStrategy.cs:241,346-360` |
| DSClient waits 150 ms after removal before re-creating a device, retries creation once after 600 ms, and watches 2 s after detach to tell its own phantom from real hardware. | `DSC:DualSenseClient.Controllers/Emulation/EmulationService.cs:80-101` |
| DSClient uses HidHide's persistent blacklist. After a crash the real pad stays hidden until the app runs again (or the user fixes it in HidHide). | `DSC:DualSenseClient.HidHide/HidHideService.cs:80-101` |
| Old instance entries of the virtual device stay in the PnP cache as non-present devices. They can be removed with `pnputil /remove-device <id>` (Windows 11 21H2+) or `CM_Uninstall_DevNode`; this is cosmetic. | `UW:README.md:155-160` (same mechanism used for the root device) |

---

## 7. Steam Input facts

Little in the sources. What exists:

| Fact | Source |
|---|---|
| Steam applies its own lightbar colour when a DualSense connects. DSClient reapplies the profile 5 s after connection as a workaround. | `DSC:DualSenseClient.GUI/ViewModels/MainViewModel.cs:374-380` |
| DSClient has no Steam process or registry detection. | grep over `DSC:` |
| A game sees double input when both the physical and the virtual pad are visible; the cure is HidHide. | DSClient `docs/troubleshooting.md:92-99` |
| DSClient hides DualSense emulation when the real pad is on USB (it already is a DualSense). | DSClient `docs/guides/virtual-controller.md` |

Well-known but **unverified** here (not from the sources above):

- Process name `steam.exe`. `HKCU\Software\Valve\Steam\ActiveProcess` value `pid`
  (Steam client PID, 0 when not running) and `HKCU\Software\Valve\Steam` value
  `RunningAppID` (0 when no game is running).
- Steam Input PlayStation support is a client setting stored in
  `<Steam>\config\config.vdf` (key name believed to be `SteamController_PSSupport`;
  not confirmed by search).
- Steam opens HID devices itself. Unless HidHide hides the real pad from Steam,
  Steam Input sees both the real and the virtual DualSense.

---

## 8. Distribution

### 8.1 usbip-win2

| Item | Value |
|---|---|
| Latest release | 0.9.8.1, published 2026-09-27 |
| URL (x64) | https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe |
| Size / SHA-256 (x64) | 26 083 632 B / `38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a` (GitHub asset digest) |
| URL (arm64) | …/USBip-0.9.8.1-arm64.exe, 19 060 408 B, `cab7ff97f79275eeb5c5b8bb2eb3111ff6b6c4eead07d9bec9be5bd1e3a35800` |
| Source of numbers | `gh api repos/vadimgrn/usbip-win2/releases` |
| Signing | drivers "WHLK certified (or attestation signed)", via the Open Source Codesigning Initiative (OSSign). The release links a Microsoft Hardware Certification Report (`UW:README.md:10-11`; release notes). The publisher of the installer's Authenticode signature was not checked (**verify** with `Get-AuthenticodeSignature` after download) |
| License | BSD-2-Clause (`UW:LICENSE.txt`). The installer also carries Microsoft's VC++ redistributable (`UW:userspace/innosetup/setup.iss:157,170`) |
| Minimum OS | Windows 10 1903 (build 18362) x64, Windows 11 ARM64 (`UW:README.md:16-17`; `setup.iss:121`) |
| Installer type | Inno Setup, AppId `{199505b0-b93d-4521-a8c7-897818e0205a}`, admin required, `AlwaysRestart=yes` (`setup.iss:56,101,112,115`) |
| Silent install | `USBip-0.9.8.1-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` (standard Inno switches; winget lists the installer as type inno). Optional `/COMPONENTS="main,client"` to skip GUI/SDK/PDB, `/TASKS="vcredist"` |
| Silent uninstall | `"C:\Program Files\USBip\unins000.exe" /VERYSILENT /NORESTART /SUPPRESSMSGBOXES` (the installer itself uses these switches for the previous version, `setup.iss:500`; the uninstaller's exe name is the Inno default, **unverified**) |
| Reboot | the installer always asks for a restart (`AlwaysRestart=yes`), and uninstall says a restart is needed (`setup.iss:427-430`). Whether the driver works before the reboot is **unverified** |
| Side effects | **all USB 3.0 hubs restart during install**: every USB device drops out briefly (`UW:README.md:120-123`), because an upper filter `usbip2_filter` is added on `USB\ROOT_HUB30` (`drivers/ude_filter/usbip2_filter.inf`). It installs a root device `ROOT\USBIP_WIN2\UDE`, registers a shutdown scheduled task, and installs the VC++ runtime. An upgrade uninstalls the old version first. The README asks users to create a restore point |
| Winget | `vadimgrn.usbip-win2`, but the newest manifest is only 0.9.7.8 (microsoft/winget-pkgs) |
| Bundling | BSD-2 allows shipping the unmodified installer, provided the copyright notice and license text go with it (put them in our third-party notices). A bootstrapper that downloads the pinned URL and checks the SHA-256 also works |

### 8.2 HidHide

| Item | Value |
|---|---|
| Latest GitHub release | v1.5.230.0, published 2024-05-11 (later tags v1.6.297.0 … v1.7.346.0 exist without GitHub release assets; v1.7.346.0 tagged 2026-05-02) |
| URL | https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe |
| Size / SHA-256 | 8 078 016 B / `F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6` (from the winget manifest `Nefarius.HidHide` 1.5.230; GitHub publishes no digest for this asset) |
| Platform | Windows 10/11 x64 only (release notes) |
| Installer | Advanced Installer EXE wrapping an MSI (`Setup/HidHide.aip` at the tag). `master` moved to a WixSharp/WiX 5 MSI, unreleased |
| Silent install | `HidHide_1.5.230_x64.exe /exenoui /qn /norestart` (winget manifest `InstallerSwitches.Silent`) |
| Silent uninstall | via MSI: `msiexec /x {ProductCode} /qn /norestart`. The product code is in the Uninstall key (**unverified**, not looked up) |
| Reboot | "Reboot when prompted". An update uninstalls the old version and needs a reboot, then installs and needs another (release notes; `HH:README.md` "A system reboot may be triggered") |
| Signing | kernel driver must be Microsoft-signed to load on 64-bit Windows; the installer publisher is presumably "Nefarius Software Solutions e.U." (**verify** with `Get-AuthenticodeSignature`) |
| License | MIT (`HH:LICENSE`; the driver package's `LICENSE.rtf` is MIT as well) |
| Bundling | MIT allows redistribution with the notice. The README warns that only github.com/nefarius and `*.nefarius.at` are official. Download from the GitHub URL with a pinned hash rather than mirroring |
| Side effects | adds HidHide as an upper class filter to HIDClass, XnaComposite and XboxComposite. Every HID device is restacked, and a reboot may be needed |

---

## 9. Open questions (need hardware or a capture)

1. Exact Windows 11 enumeration sequence over usbip-win2 for a full-speed vs
   high-speed export: which descriptor types get requested (qualifier, BOS, `0xEE`),
   and whether SET_ADDRESS reaches the server. Log every setup packet in the first run.
2. HID-only DualSense: does Windows bind HID cleanly with the interface renumbered to
   0? Do SDL, Steam and DS-aware games (haptics, adaptive triggers) accept a DualSense
   without audio interfaces?
3. Report rate: is a high-speed export with bInterval 6 (4 ms) enough, or should we
   use bInterval 4 (1 ms)? Measure the actual URB cadence from the host.
4. Does a game using `HidD_SetOutputReport` send 48 bytes (descriptor length) or
   63? Accept both.
5. Xbox 360: are the ViGEm init packets (4.4) and the vendor requests (4.6) needed for
   XInput features (battery, headset, player LED)? Log what xusb22 sends.
6. Xbox 360 player LED: confirm the `01 03 NN` values xusb22 sends for slots 1-4.
7. usbip-win2 version detection: confirm the uninstall `DisplayVersion` and driver
   version strings, and lock the struct layout per version (2.6). The next release
   will likely change `plugin_hardware` to the 1120-byte composition layout.
8. `device_state` ReadFile record size (1144 computed) for live attach and detach
   events.
9. Does usbip-win2's `serial` override give Windows a stable instance ID for a device
   whose iSerialNumber is 0?
10. HidHide: does a running game lose access to a pad that is blacklisted after it
    opened the handle? Is the session blacklist usable in any shipped build?
11. Signer names on both installers (Authenticode) and whether usbip-win2 needs the
    reboot before the first attach works.
12. Steam: confirm the registry values and the `config.vdf` key for PlayStation
    support, and whether Steam Input grabs the virtual DualSense.

## 10. Installer signatures (verified 2026-11-19)

Both files downloaded from the URLs in §8; size and SHA-256 match; `Get-AuthenticodeSignature` = Valid.

| File | Signer CN | Issuer |
|---|---|---|
| `USBip-0.9.8.1-x64.exe` | `Cloudyne Systems (Scheibling Consulting AB)` (OSSign) | GlobalSign GCC R45 EV CodeSigning CA 2020 |
| `HidHide_1.5.230_x64.exe` | `Nefarius Software Solutions e.U.` | DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1 |
