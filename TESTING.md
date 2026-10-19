# Help test CS2 Mod Verifier

Thanks for trying CS2 Mod Verifier! This is an early version, and testing on many different PCs and mod collections is the most useful help there is.

## Get started

1. Download the latest zip from [Releases](https://github.com/KDanielK/CS2ModVerifier/releases), check it (see the README), extract it to a folder you keep (for example `Documents\CS2ModVerifier`) and run `CS2ModVerifier.exe`.
   - **Windows SmartScreen will most likely warn you** with a blue "Windows protected your PC" window, because the exe is not code-signed. Choose **More info** and **Run anyway**. Windows only asks once.
2. Read the explanation on first start and accept it. The window opens and checks your installed code mods.
3. Afterwards it runs in the tray (the shield icon). Double-click it to open the window.

## What to try

- **Your own mod list.** Do the results make sense? Hover over anything you do not understand: is the explanation clear?
- **Expand a mod** (the arrow) and look at its findings. For a risky call marked IN SOURCE, right-click and choose "View the code on GitHub".
- **Ignore a finding** you have looked into, and check that the mod's badge changes.
- **Subscribe to or update a mod** while the app runs (in the game or on Paradox Mods). It should be checked within seconds, with a Windows notification. "Do not disturb" in Windows hides notifications.
- **Start with Windows:** turn it on in the tray menu, restart your PC, and check that the app starts in the tray.
- **Updates:** when a newer version is released, the app shows a notification (about a minute after it starts, then once a day) and an "Update available" item in the tray menu.

## Known limits

- Windows 10 and 11 (64-bit) only.
- The exe is about 130 MB, because it includes the .NET runtime.
- It is not code-signed, so Windows SmartScreen warns about an unknown publisher on the first start (see above).
- Asset mods (buildings, maps, props) are not checked, for performance. Only mods tagged "Code Mod" on Paradox Mods, or with a `.dll` in their main folder, are checked and listed.
- GitHub allows 60 requests per hour without an account. Each mod usually needs one or none, and results are remembered. If the limit is reached, mods show "Waiting for GitHub" and are compared automatically after it resets.
- Mods without a GitHub link on their Paradox Mods page cannot be compared with source, and are reported as such.

## Report what you find

- **Something wrong or unclear:** open an [issue](https://github.com/KDanielK/CS2ModVerifier/issues/new/choose) using the bug report form.
- **A security problem in this tool:** report it privately, see [SECURITY.md](SECURITY.md).
- **A finding that you think is a false alarm:** please report it too, with the mod's name and version. That helps tune the checks.

The log is in `%LOCALAPPDATA%\CS2ModVerifier\app.log`. It contains folder paths with your Windows user name; replace it before sharing if you prefer.
