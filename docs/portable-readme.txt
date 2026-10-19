VoxType @VERSION@ — Windows 11 x64
https://github.com/hongjiapeng/VoxType

Extract the entire ZIP to a local folder, then run VoxType.App.exe.
To open settings directly: VoxType.App.exe --settings
The app stays in the system tray. Use the tray menu to open settings or exit.
Focus a text field, hold Ctrl + Shift + Space to speak, and release to transcribe.
Press Esc to cancel. Configure a speech provider before your first recording.

The desktop app includes .NET and Windows App SDK runtimes.
Speech models are not included. Download or import Whisper base in settings,
or connect to your own R2T2 / OpenAI-compatible transcription server.
Local Whisper requires AVX/AVX2/FMA/F16C and the x64 VC++ 2022 runtime.
Vulkan additionally requires a compatible GPU and driver.
Local R2T2 includes a compiled service and its .NET runtime; no SDK or PowerShell
is needed from settings. Prepare resources once (about 1.63 GiB of models), then
start. Settings show download/loading status and provide stop/cancel.
The service stops when VoxType exits. Verified existing resources are reused.
Command-line deployment scripts remain available for advanced use.

Settings and models live under %LOCALAPPDATA%\VoxType and are not removed
when you delete this portable folder or uninstall the desktop application.
An existing MSIX development installation has its own virtualized data directory.
The release is unsigned. No certificate is installed and no system policy changed.

No audio/transcription history is written to disk by VoxType. Online providers
receive audio at the endpoint you configure. See ThirdPartyNotices.md for notices.
This is an initial preview release; see the repository's verification documents
for the distinction between automated checks and manual microphone testing.
