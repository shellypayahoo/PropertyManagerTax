# Installing Property Manager on a Mac

About 10 minutes. You don't need to be technical, and nothing needs to be installed first.

## What you need

- A Mac with macOS 13 (Ventura) or newer.
- To know which kind of Mac it is: click the **Apple menu  → About This Mac**. If it says **Chip: Apple M1** (or M2,
  M3, M4…), it's **Apple silicon**. If it says **Processor: Intel**, it's **Intel**.

## What you get

Everything for running your rentals: properties, tenants, leases, rent, expenses with receipts, bank imports,
maintenance, vendors, documents, and the tax-time reports (profit and loss, 1099s, depreciation, and a packet for
your accountant), plus automatic backups. On a Mac you can also bring in **text messages** with tenants and vendors,
and the app reads the words in **scanned papers and photos**.

Optional, if you set them up: the **AI assistant** (needs an account with an AI company; the app shows you how) and
**email and calendar** from Outlook.com or Hotmail.

## 1. Download

1. Open the [download page](https://github.com/shellypayahoo/propertymanagertax/releases/latest).
2. Under **Assets**, click the file for your Mac:
   - Apple silicon (M1, M2, M3, M4…): **PropertyManager-…-arm64.dmg**
   - Intel: **PropertyManager-…-x64.dmg**

## 2. Install

1. Open your **Downloads** folder and double-click the **.dmg** file. A window opens with **Property Manager** and
   an **Applications** folder.
2. Drag **Property Manager** onto **Applications**. Then close the window and eject the disk in the Finder sidebar.

## 3. Open it the first time

The app isn't sold through Apple's App Store, so the first time macOS asks you to confirm it:

1. Open your **Applications** folder and double-click **Property Manager**.
2. macOS says it **can't be opened** or **can't check it for malicious software**. Click **Done** (or **Cancel**).
3. Open **System Settings → Privacy & Security**, scroll down to the message about **Property Manager**, and click
   **Open Anyway**. Enter your Mac password if asked.
4. Click **Open** in the box that follows.

On older macOS versions you can instead **Control-click** (right-click) Property Manager in Applications, choose
**Open**, then **Open** again. You only do this once.

## 4. The setup guide

Property Manager opens in your web browser (Safari, Chrome, or whichever you use). It looks like a website, but it
runs only on your Mac. The first time, a **setup guide** walks you through:

1. **Your business:** its name, and when rent is due and late.
2. **Your properties:** each address, its units, and their rent. After this the app is ready to use.
3. **AI assistant** (optional): the guide explains what it is and shows how to sign up, step by step. You can **Skip for now**.
4. **Email and calendar** (optional): a one-time registration with Microsoft, step by step. You can **Skip for now**.
5. **Text messages** (optional): see below.
6. **Backups** (recommended): pick your **iCloud Drive** folder so a copy of everything is kept off the Mac.
7. **Your phone** (optional): how to open the app from your phone.

You can come back to the optional steps any time: **Settings → Open the setup guide**.

## Text messages (optional)

macOS protects your messages, so the app needs your permission once:

1. Open **System Settings → Privacy & Security → Full Disk Access**.
2. Click **+**, choose **Applications → Property Manager**, and turn it on.
3. Stop Property Manager (**Settings → This computer → Stop Property Manager**) and open it again from Applications.
4. On the **Messages** page, click **Find contacts in Messages** and tick your tenants and vendors.

## Using it day to day

- **To open it:** double-click **Property Manager** in Applications (or keep it in the Dock). If it's already running,
  this just opens it in your browser.
- **Bookmark it:** the address is `http://localhost:5220`.
- **Start it with the Mac:** turn on **Settings → This computer → Start Property Manager when I sign in**.
- **To stop it:** **Settings → This computer → Stop Property Manager**.
- **Where your records are kept:** in your home folder's `Library/Application Support/Property Manager`. In the
  Finder, choose **Go → Go to Folder…** and paste `~/Library/Application Support/Property Manager`. Updating or
  deleting the app never deletes them. Turn on **Backups** so a copy is kept elsewhere too.

## Using it from your phone (optional)

1. Install **Tailscale** on the Mac from the App Store and sign in (a Microsoft, Google, or Apple account works;
   choose **Personal**). Allow what it asks for.
2. Install **Tailscale** on your phone and sign in with the **same** account.
3. In Property Manager, turn on **Settings → This computer → Let my phone reach it**, then click **Restart now**.
   This also keeps the Mac from going to sleep while the app runs.
4. In the Tailscale menu at the top of the screen, find the Mac's name. On your phone's browser, open
   `http://<that name>:5220`.

The Mac needs to be on, with Property Manager running, for the phone to reach it. The app only answers the Mac itself
and your own Tailscale devices.

## Updating

Download the newest **.dmg** from the [download page](https://github.com/shellypayahoo/propertymanagertax/releases/latest),
stop Property Manager (**Settings → This computer → Stop**), and drag the new one onto **Applications**, choosing
**Replace**. Your records and settings are kept. macOS may ask you to confirm it once more, as in step 3.

## Uninstalling

Stop it, then drag **Property Manager** from Applications to the Trash. Your records stay in
`~/Library/Application Support/Property Manager`; delete that folder too if you want them gone.

## If something goes wrong

- **The browser says it can't reach `localhost:5220`:** open Property Manager from Applications and wait a few seconds.
- **"Property Manager is damaged and can't be opened":** download it again; the download may have been cut short.
- **Anything else:** the app keeps a log at `~/Library/Application Support/Property Manager/logs/app.log`. Send it to
  whoever helps you with the app.
