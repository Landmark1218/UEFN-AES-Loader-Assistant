# UEFN-AES-Loader-Assistant

**UEFN-AES-Loader-Assistant** is an assistant tool that makes setting up [UEFN-AES-Loader](https://github.com/Aleman-sein-Vater/UEFN-AES-Loader) easier.

This tool does not include AES decryption functionality itself. Its purpose is to make it easier to use **UEFN-AES-Loader** published by [Deutsche Alman](https://github.com/Aleman-sein-Vater).

## Requirements

* [Python](https://www.python.org/downloads/)
* [PIE](https://discord.gg/zjurusqhpD)
* [UEFN-AES-Loader](https://github.com/Aleman-sein-Vater/UEFN-AES-Loader)

## Setup

1. Download `UEFNContentKey.dll` from the [UEFN-AES-Loader](https://github.com/Aleman-sein-Vater/UEFN-AES-Loader) repository.
2. Rename the downloaded DLL to `amfrt64.dll`.
3. Place the DLL in the `FortniteGame/Binaries/Win64` folder.
4. Launch PIE once, then close it.

## Usage

1. Launch the **UEFN-AES-Loader-Assistant** exe.
2. Enter the map code of the map you want to load (e.g. 1234-5678-9012).
3. Wait for the process to complete.
4. Launch **PIE** in UEFN again.

## How it works

When you launch PIE after the process completes, the target map is automatically loaded into PIE.
The Content Drawer location where the map data is placed is also opened automatically, giving you immediate access to the assets.

## Credit

Uses [FNJPNews](https://github.com/FNJPNews)'s [UEFNDownloader](https://github.com/FNJPNews/UEFNDownloader) as the core of this tool.
