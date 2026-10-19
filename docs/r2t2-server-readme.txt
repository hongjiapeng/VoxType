VoxType R2T2 Server @VERSION@ — Windows x64
https://github.com/hongjiapeng/VoxType

This optional package serves other R2T2 clients over HTTP. For local voice
typing, use the desktop app's R2T2 本机 option; this package is not needed.
Extract the entire ZIP. .NET and ASP.NET Core runtimes are included.
Deployment scripts require PowerShell 7; no .NET SDK is needed.

From the extracted folder:
  pwsh -NoProfile -File scripts/prepare-r2t2.ps1 -Backend Cpu
  pwsh -NoProfile -File scripts/start-r2t2.ps1 -Backend Cpu
For Vulkan, use -Backend Vulkan in both commands.
Models (about 1.63 GiB) and the llama runtime download only during preparation.
The script starts the HTTP adapter on 127.0.0.1:8272 and llama on 127.0.0.1:8273.
Keep the terminal open; Ctrl+C stops both child processes.

In VoxType select R2T2 服务器, URL http://127.0.0.1:8272/v1, model r2t2-q4.
LAN binding requires R2T2_API_KEY. See docs/r2t2-server.md for deployment,
authentication, ports and the HTTP contract. No firewall rules are changed.
Do not run a local desktop engine and this server on the same llama port.
Use -LlamaPort to select a different server port if both are needed.

No models, personal settings, keys or audio history are included in this ZIP.
See ThirdPartyNotices.md and licenses/ for third-party notices.
