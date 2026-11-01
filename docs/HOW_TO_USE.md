# How to use TrayDrop

TrayDrop is a portable Windows tray app for two-way file transfers over trusted local Wi-Fi. Your phone uses a browser; it does not need a companion app or account.

Project: https://github.com/istartedcoding/TrayDrop

## Download and open

1. Download **TrayDrop-0.1.0-win-x64.zip** from the [0.1.0 release](https://github.com/istartedcoding/TrayDrop/releases/tag/v0.1.0).
2. Right-click the ZIP and choose **Extract All**.
3. Open the extracted **TrayDrop** folder, then run **TrayDrop.exe** as a normal user.
4. Look for its rose-colored icon in the Windows notification area. Expand the hidden-icons menu if necessary.

This release is for Windows x64 systems compatible with the .NET 10 desktop runtime. The runtime is included, so a separate .NET or Node.js installation is not required.

The application is an **unsigned pre-release**. Review its source, release origin, and checksum if Windows displays a security or reputation warning. Do not disable system security broadly or run the app as administrator to work around a warning.

The portable folder also contains this guide, the project license, and third-party notices. Keep those files with the application when redistributing it.

## Connect your phone

1. Put Windows and your phone on the same trusted local Wi-Fi network.
2. Click the TrayDrop tray icon to open its lower-right panel.
3. Choose **Connect a device**.
4. Select the Wi-Fi or Ethernet adapter used by the phone's network.
5. Scan the QR code with the phone camera, or open the copied private connection link in its browser.
6. The browser pairs automatically and appears in the Windows **Send to** list.

You can rename the device from the phone page. Keep that page open and the phone awake while sending files.

If Windows asks for firewall access, allow TrayDrop on **Private networks only**. Guest Wi-Fi or access-point/client isolation may block communication between devices.

## Windows to phone

1. Select the receiving device in the Windows panel.
2. Drag individual files from Explorer into **Drop files here**, or click the drop zone to select files.
3. On the phone page, find the files under **From your PC**.
4. Tap **Download** for the files you want.

Dropping a file creates a download link for the selected device. It does not push the bytes until the phone requests a download. The Windows original stays in its original location; keep it unchanged until the download finishes.

The phone browser controls its own download destination.

## Phone to Windows

1. On the phone page, choose **Choose files to send**.
2. Select one or more files.
3. Watch progress and the saved-file result.
4. In Windows, choose **Open received files** to find the completed uploads.

Received files default to your actual Windows **Downloads** folder. Existing filenames are not overwritten; duplicates receive a numbered suffix. Zero-byte files are supported.

**Cancel sending** aborts the active upload and skips remaining files. Files that completed before cancellation remain saved.

## Change the received-file folder

1. Click **Save to: Downloads (change)** at the bottom of the panel, or **Save location...** in the tray menu.
2. Choose **Browse...** and select a writable folder.
3. Click **Save**.

The destination is remembered across restarts. **Use Downloads** restores the Windows known-folder default.

Changing the folder affects new uploads only. Existing received files are not moved, active uploads finish in their original destination, and outgoing Windows originals are not copied or moved.

If you choose a cloud-synced folder, its separate sync service can upload the saved files. TrayDrop itself transfers only over the local network.

## Tray controls and shutdown

- **x** or Escape: hide the panel without stopping the server.
- **Remove share**: revoke the selected future download link without deleting the original.
- **Forget device**: disconnect the device and revoke its shares without deleting received files.
- **Clear shared links**: revoke all outgoing links.
- **Exit** in the tray menu: stop TrayDrop.

Opening another copy activates the running instance. Connections and outgoing links are session-only; after a restart, pair again using the new QR code. Completed uploads remain on disk.

## Troubleshooting

| Problem | Check |
| --- | --- |
| No tray icon | Expand hidden notification icons. Confirm the app is running. |
| Phone cannot connect | Same local network, correct adapter, Private firewall access, and no guest/client isolation. |
| Device is offline | Bring its browser tab to the foreground. Re-pair after an app restart or a forgotten session. |
| Drag/drop does not work | Run TrayDrop and Explorer at the same normal-user elevation. Select an online receiving device first. |
| Upload fails | Check disk space, folder permissions, and the phone's network connection. |
| Download fails | Confirm the original still exists, is unchanged, and is readable; share it again if needed. |
| Port already in use | Exit the existing app, then start `TrayDrop.exe --port 45824` and use its newly displayed link. |

The tray menu can open the log folder. Settings are stored at `%LOCALAPPDATA%\TrayDrop\settings.json`; logs are stored at `%LOCALAPPDATA%\TrayDrop\Logs`.

## Security and limitations

**Use trusted local networks only. HTTP traffic is not encrypted.** Anyone with a live pairing invitation can connect. Do not publish pairing links, enable router port forwarding, or expose the server to the internet.

This version supports individual-file transfers and IPv4 browser pairing. It does not include native mobile apps, discovery of unpaired devices, folder transfers, upload resumption, automatic startup, an installer, or automatic updates.

For detailed features, screenshots, architecture, and API information, see the [project documentation](https://github.com/istartedcoding/TrayDrop). Report problems at the [issue tracker](https://github.com/istartedcoding/TrayDrop/issues), without private QR codes, tokens, personal files, or unreviewed logs.
