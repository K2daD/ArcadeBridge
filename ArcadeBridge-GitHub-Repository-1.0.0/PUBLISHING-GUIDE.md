# Publish ArcadeBridge on GitHub

This repository package is ready to upload to `https://github.com/K2daD/ArcadeBridge`.

## 1. Create the empty repository

On the GitHub screen shown during setup, use:

- **Repository name:** `ArcadeBridge`
- **Description:** `ArcadeBridge lets remote players use controllers or keyboards through a browser while a Windows host receives up to four virtual gamepads for emulators and PC games.`
- **Visibility:** Public
- **Add README:** Off
- **Add .gitignore:** No `.gitignore`
- **Add license:** No license

The last three choices are intentional because this package already contains the finished README, `.gitignore`, and MIT `LICENSE`. Select **Create repository**.

## 2. Upload the source package

The easiest non-command-line method is:

1. Open the new empty repository.
2. Choose **uploading an existing file**.
3. Drag the contents of this folder into the upload area. Preserve the `src`, `docs`, and `.github` folders.
4. Use the commit message `ArcadeBridge 1.0.0`.
5. Commit directly to the `main` branch.

If the browser does not preserve all nested folders, use GitHub Desktop instead: add this folder as a local repository, publish it to the existing `K2daD/ArcadeBridge` repository, and push the initial commit.

## 3. Add repository details

From the repository home page, use the gear beside **About** and set:

- Description: use the same text above.
- Website: `https://controller.rommserver.org/`
- Topics: `remote-controller`, `gamepad`, `xinput`, `vigem`, `windows`, `websocket`, `emulation`, `local-multiplayer`, `controller-mapping`
- Enable **Releases** and **Issues**.

## 4. Create release 1.0.0

1. Open **Releases > Draft a new release**.
2. Create tag `v1.0.0` targeting `main`.
3. Title it `ArcadeBridge 1.0.0`.
4. Paste the contents of `RELEASE-NOTES-1.0.0.md`.
5. Attach these existing files from the ArcadeBridge output package:
   - `ArcadeBridge-Master-1.0.0-Final.zip` — recommended complete download.
   - `ArcadeBridgeHost-1.0.0.exe` — optional standalone Host download.
   - `ArcadeBridge-1.0.0-SHA256SUMS.txt` — checksum.
6. Mark it as the latest release and publish it.

Do not upload signing certificates, private keys, room passwords, diagnostic reports, personal backups, or production server credentials.

## 5. Optional repository settings

- Under **Settings > General**, enable Issues and Discussions if you want community questions.
- Under **Settings > Code security**, enable Dependabot alerts and private vulnerability reporting.
- Add `docs/assets/arcadebridge-social-preview.png` as the repository social preview image.
- Branch protection can be added later if more people begin contributing.

GitHub Pages is not required for the repository README. The existing player website can remain at `controller.rommserver.org`.
