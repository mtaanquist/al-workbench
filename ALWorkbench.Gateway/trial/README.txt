AL Workbench Gateway - trial package
====================================

This package is for trying the gateway on a test server. The gateway runs in a
command prompt window instead of as a Windows service, so you can watch what it
does and stop it with Ctrl+C. It is a preview build and is not code-signed, so
Windows may warn about it when it first runs.

What is in it
  ALWorkbench.Gateway.exe          the gateway
  ALWorkbench.Gateway.exe.sha256   its SHA-256, to check the download
  gateway.toml                     settings for one test service tier: edit this
  gateway.example.toml             every setting, explained
  README.txt                       this file

You need
  - A Windows server with a Business Central service tier (BC 14 or later).
  - An administrator account on it.
  - HTTPS access from it to your AL Workbench address.
  - An Admin account in AL Workbench.

Steps
  1. Right-click the zip file, choose Properties, tick Unblock and click OK.
     Then extract it to:
         C:\Program Files\AL Workbench Gateway
     The gateway refuses to run from a folder that ordinary users can change,
     and C:\Program Files is safe by default.

  2. Open gateway.toml in Notepad (as administrator) and follow the steps at the
     top: put in this computer's name and your service tier's name.

  3. Open a command prompt with "Run as administrator" and go to the folder:
         cd "C:\Program Files\AL Workbench Gateway"

  4. Check the settings:
         ALWorkbench.Gateway check
     Fix anything it lists, and run it again until it shows your service tier
     and its state.

  5. In AL Workbench, go to Administration -> Gateways, choose Add gateway and
     copy the registration code.

  6. Register this computer, with your AL Workbench address and the code:
         ALWorkbench.Gateway register --url https://your-al-workbench --code THE-CODE
     It prints a key fingerprint. Ignore the advice to run "install".

  7. Start the gateway in this window:
         ALWorkbench.Gateway run
     Leave the window open. Ctrl+C stops the gateway.

  8. In AL Workbench, on Administration -> Gateways, approve the gateway. Check
     that the fingerprint shown there matches the one from step 6.

  9. Within a few minutes the gateway reports the service tier. Link it to an
     on-premises solution from that solution's Environments tab. You can then
     deploy to it from a deployment pipeline, upload a licence, and restart,
     stop and start the tier from its environment page.

Logs
  The window shows what the gateway does. Daily log files are kept in
  C:\ProgramData\AL Workbench Gateway\logs.

Removing it
  Press Ctrl+C, then run "ALWorkbench.Gateway uninstall" from the same folder.
  It deletes the gateway's key and settings. Then revoke the gateway in
  AL Workbench under Administration -> Gateways.
