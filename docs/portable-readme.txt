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
Local R2T2 uses a shared library and one background llama-server process.
No HTTP adapter, SDK or PowerShell is needed from settings. Select R2T2 本机,
prepare resources once (about 1.63 GiB of models), then enable the local engine.
Settings show progress and provide stop/cancel; the engine stops on app exit.
To deploy an HTTP server, download the separate VoxType-R2T2-Server ZIP.
Existing R2T2 server settings remain server connections until you select local.

Settings and models live under %LOCALAPPDATA%\VoxType and are not removed
when you delete this portable folder or uninstall the desktop application.
An existing MSIX development installation has its own virtualized data directory.
The release is unsigned. No certificate is installed and no system policy changed.

No audio/transcription history is written to disk by VoxType. Online providers
receive audio at the endpoint you configure. See ThirdPartyNotices.md for notices.
This is an initial preview release; see the repository's verification documents
for the distinction between automated checks and manual microphone testing.
