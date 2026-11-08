# AboEzzat TV Desktop

This is an early Electron receiver prototype for Windows, macOS, and Linux.

## Run locally

Install Node.js 20 or newer, then from this directory:

```bash
npm install
npm start
```

The receiver currently attempts to listen on HTTP port **8081** and announce itself through UDP multicast **239.255.42.99:37020**, matching the current Android receiver protocol draft. Allow the app through your computer's firewall on your trusted local network.

## Build installers

The GitHub Actions workflow `.github/workflows/build-desktop.yml` builds:

- Windows: NSIS installer
- macOS: DMG
- Linux: AppImage

Open the repository's **Actions** tab, select **Build AboEzzat TV Desktop**, and choose **Run workflow**. Download the platform artifact from the completed run.

## Current limitations — prototype, not feature-complete

- The build workflow and packages have not yet been verified by a real Actions run.
- Video playback may fail for streams that require HTTP headers supplied by AboEzzat Cast. Header-aware playback/proxy support is not implemented yet.
- Browser media support varies; HLS and other streaming formats need additional testing.
- Only a subset of receiver commands is currently implemented.
- UDP multicast discovery and Cast-to-desktop compatibility still need testing on a real local network.
- This prototype has no pairing/authentication. Use only on a trusted LAN; do not expose its port to the public internet.

LG webOS and Samsung Tizen require separate TV app packages and platform-specific networking work. This desktop Electron app is not an installable LG or Samsung TV app.
