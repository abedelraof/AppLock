# AppLock

Lock Windows programs behind a password. When someone opens a locked program, AppLock asks for the password first; the program only starts if it's right.

- One **master password**, plus an optional **separate password per app**
- Pick apps from a list of **installed apps with icons**, browse for an `.exe`, or type its name
- Works with apps that run many processes (Discord, Chrome, Steam…): one prompt per launch
- Wrong passwords trigger an increasing wait

## Install

Run `AppLock-Setup-x.y.z.exe` (build it with `build-installer.ps1`, see below). Then open **AppLock** from the Start menu, create your master password, and click **Lock an app…**.

Uninstalling removes all locks and the background service.

## How it works

- **Lock:** AppLock registers itself as the program's "debugger" under
  `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<app>.exe`,
  so Windows runs AppLock's password prompt instead of the program.
- **Unlock:** after the right password, the **AppLock service** (runs as SYSTEM) checks the password again,
  lifts the lock, and the program starts. When every copy of the program has closed, the service locks it again.
- **Settings** live in `C:\ProgramData\AppLock\config.json` (passwords are stored as salted PBKDF2 hashes;
  only administrators can change the file).

## Limits

- Locks by file name: renaming or copying the `.exe` gets around the lock.
- Anyone with administrator rights can remove locks.
- Microsoft Store apps can't be locked.
- While a program is unlocked (still running, including in the tray), opening it again doesn't ask.

## Build

Requires the .NET 9 SDK.

```powershell
dotnet publish -c Release -o publish          # framework-dependent exe in publish\
.\build-installer.ps1                         # setup in dist\ (needs Inno Setup 6)
```
