# Installing Property Manager on a Windows PC

About 10 minutes. You don't need to be technical, and nothing needs to be installed first.

## What you need

- A Windows 10 or 11 PC (64-bit, which nearly all are).
- An internet connection for the download.

## What you get on a PC

Everything for running your rentals: properties, tenants, leases, rent, expenses with receipts, bank imports,
maintenance, vendors, documents, and the tax-time reports (profit and loss, 1099s, depreciation, and a packet for
your accountant), plus automatic backups.

Optional, if you set them up: the **AI assistant** (needs an account with an AI company; the app shows you how) and
**email and calendar** from Outlook.com or Hotmail.

**Only on a Mac:** bringing in text messages (they come from Apple's Messages app), reading the words in scanned
papers and photos, and previews of iPhone photos.

## 1. Download

1. Open the [download page](https://github.com/shellypayahoo/propertymanagertax/releases/latest).
2. Under **Assets**, click **PropertyManager-Setup-…exe**. It saves to your **Downloads** folder.

## 2. Install

1. Open your **Downloads** folder and double-click **PropertyManager-Setup**.
2. Windows may show a blue box saying **"Windows protected your PC"**. That's because the app is new and isn't from a
   big software company. Click **More info**, then **Run anyway**.
3. The installer asks two questions. We suggest leaving both ticked:
   - **Put a Property Manager icon on the desktop.**
   - **Start Property Manager when I sign in to Windows.** It then runs quietly in the background, so it's always ready.
4. Click **Install**, then **Finish**. Leave **Open Property Manager now** ticked.

No administrator password is needed; it installs just for you.

## 3. The setup guide

Property Manager opens in your web browser (Edge, Chrome, or whichever you use). It looks like a website, but it runs
only on your PC. The first time, a **setup guide** walks you through:

1. **Your business:** its name, and when rent is due and late.
2. **Your properties:** each address, its units, and their rent. After this the app is ready to use.
3. **AI assistant** (optional): the guide explains what it is and shows how to sign up, step by step. You can **Skip for now**.
4. **Email and calendar** (optional): a one-time registration with Microsoft, step by step. You can **Skip for now**.
5. **Backups** (recommended): pick your **OneDrive** folder so a copy of everything is kept off the PC.
6. **Your phone** (optional): how to open the app from your phone.

You can come back to the optional steps any time: **Settings → Open the setup guide**.

## Using it day to day

- **To open it:** click the **Property Manager** icon on the desktop or in the Start menu. If it's already running,
  this just opens it in your browser.
- **Bookmark it:** the address is `http://localhost:5220`. Bookmarking it in your browser works too.
- **To stop it:** **Settings → This computer → Stop Property Manager**. You rarely need to.
- **Where your records are kept:** in `%LOCALAPPDATA%\Property Manager` (paste that into the File Explorer address bar
  to open it). Updating or uninstalling the app never deletes them. Turn on **Backups** so a copy is kept elsewhere too.

## Using it from your phone (optional)

1. Install **Tailscale** on the PC from [tailscale.com/download](https://tailscale.com/download) and sign in (a
   Microsoft, Google, or Apple account works; choose **Personal**).
2. Install **Tailscale** on your phone from the app store and sign in with the **same** account.
3. In Property Manager, turn on **Settings → This computer → Let my phone reach it**, then click **Restart now**.
   If Windows asks whether to allow Property Manager through the firewall, click **Allow**.
4. In the Tailscale app on the PC, find the PC's name. On your phone's browser, open `http://<that name>:5220`.

The PC needs to be on, with Property Manager running, for the phone to reach it. The app only answers the PC itself
and your own Tailscale devices.

## Updating

Download the newest **PropertyManager-Setup** from the [download page](https://github.com/shellypayahoo/propertymanagertax/releases/latest)
and run it. It replaces the app and keeps your records and settings.

## Uninstalling

**Settings → Apps → Installed apps → Property Manager → Uninstall** (Windows 11), or **Control Panel → Programs**.
Your records stay in `%LOCALAPPDATA%\Property Manager`; delete that folder too if you want them gone.

## If something goes wrong

- **The browser says it can't reach `localhost:5220`:** open Property Manager from its icon and wait a few seconds.
- **It worked, then stopped after Windows restarted:** turn on **Settings → This computer → Start Property Manager when
  I sign in**.
- **Anything else:** the app keeps a log at `%LOCALAPPDATA%\Property Manager\logs\app.log`. Send it to whoever helps
  you with the app.
