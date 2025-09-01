# Contractor$VR Mod Uploader

## What is it?

This console app performs mod uploads to mod.io for ContractorsVR. It uploads three platform builds (**Server**, **Windows/PC**, **Android**), patches metadata properties, and manages mod tags. 

The game's current modkit uses deprecated endpoints with strict file-size limits; causing uploads to regularly fail. This tool leverages **mod.io’s multi-part upload** process so you can upload larger mod files.

## Current features

- [x] Multipart file upload
- [x] Email authentication
- [x] Tag management
- [x] Metadata builder/updater
- [x] Update mod (with existing Mod ID)
- [ ] New mod creation (without existing Mod ID) — _planned_

## How to use

1) **Download & run**
    - Grab the [latest release ZIP](https://github.com/dudebroSW/CSModUploader/releases/download/v0.0.3/CSModUploader-win-x64-v0.0.3.zip) from GitHub, extract it, and run the `CSModUploader.exe`.

2) **Authenticate (access token or email code)**
    - When prompted `Access Token (leave blank to login by email):`
      - **Option A — Paste an existing access token:** paste a valid mod.io OAuth access token and press Enter.
      - **Option B — Email code login (recommended):** press Enter to leave it blank, then:
        - Enter your **email address** registered with mod.io to receive a **5-digit security code**.
        - Enter the **5-digit security code**.  
        - On future runs, you won’t need to log in again until it expires.

3) **Enter the Mod ID**
    - When prompted `Mod ID (numeric):`, paste the mod’s ID from mod.io.
      - This ID represents the mod to update.
      - Located on a mod’s page underneath the download / subscriber counts.

4) **Select the packaged mod folder**
    - When prompted `Folder containing packaged .zip files:`, paste the full path to the folder that contains your three packaged zips:
      - `*_server.zip`
      - `*_pc.zip`
      - `*_android.zip`
    - These are the zip files the modkit creates when you package your mod for upload.
    - The app auto-detects these by filename suffix. It will print exactly which files it found.

5) **Enter a changelog (optional)**
    - When prompted `Changelog (upload notes):`, type any notes you want associated with this upload (or leave blank).

6) **Upload**
    - Uploading begins immediately after user-inputs are validated and does not require additional input. 
    - Logs are output to the console window to indicate which actions / requests the application is making. 
    - After processing is complete, the text `"[MODNAME] successfully updated!"` is displayed in the console window.

## Troubleshooting

### Errors / Exceptions

- You may encounter **errors or exceptions** while using this app. These are generally logged to the console window and will explain the problem in more detail.

### Code signing / Antivirus note

- This app **is not code-signed**. As a result, it will likley be flagged by **Windows Defender** or other antivirus as unknown or potentially unwanted.
- You may need to **allow** the file in Windows Security or add an **exclusion** for the folder to run it.
- Only proceed if you **trust the source** and the specific binary you downloaded.

### Support

- For issues or problems, please reach out to **dudebroSW** (and/or open an issue on this repository).