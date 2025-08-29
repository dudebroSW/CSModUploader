# Contractor$ Mod Uploader

## What does the app do?

Performs mod uploads for ContractorsVR to mod.io. It uploads three platform builds (**Server**, **Windows/PC**, **Android**), updates metadata properties, and manages mod tags.

## What’s the purpose?

C$ modkit's current upload process uses deprecated endpoints with strict file-size limits. This tool leverages **mod.io’s multi-part upload** process so you can upload larger mod files.

## How to use

When you run the app, it will prompt for:
- **Mod ID** (numeric, from mod.io)
- **OAuth Access Token** (bearer-token, from mod.io)
- **Folder containing packaged .zip files** (file-path, from C$ modkit):
  - `*_server.zip`
  - `*_pc.zip`
  - `*_android.zip`
- **Changelog** (string, optional upload notes)

Uploading begins once user-inputs are validated. Logs are output to the console window to indicate actions/requests the application is currently making. After processing is complete, the text "[MODNAME] successfully updated!" is displayed in the console window.

## Troubleshooting

### Errors / Exceptions

- You may encounter **errors or exceptions** while using this app. These are generally logged to the console window and will explain the problem in more detail.

### Code signing / Antivirus note

- This app **is not code-signed**. As a result, it will likley be flagged by **Windows Defender** or other antivirus as unknown or potentially unwanted.
- You may need to **allow** the file in Windows Security or add an **exclusion** for the folder to run it.
- Only proceed if you **trust the source** and the specific binary you downloaded.

### Support

For issues or problems, please reach out to **DudebroSW** (and/or open an issue on this repository).