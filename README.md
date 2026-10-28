# PropertyManagerTax

Property management code that generates tax reports and keeps track of expenses and income.

A Blazor (ASP.NET Core, .NET 10) app for managing rental properties: units, tenants, leases, rent payments, expenses, maintenance requests, vendors, bank imports, and an annual profit and loss report laid out by IRS Schedule E line.

Data is stored in a local SQLite file (`src/PropertyManager.Web/propertymanager.db`) with EF Core. On first run the database is created and a short setup asks for the business name, the properties and units, and which parts of the app to use (see **First-run setup and Settings**). The owner's portfolio:

| Property | Unit | Market rent |
| --- | --- | --- |
| 2228 1st St NW | A | $4,400 |
| 2228 1st St NW | B | $4,500 |
| 1354 Taylor St NW | A | $4,500 |
| 1354 Taylor St NW | B | $1,000 |

## Run on macOS

1. Install the .NET 10 SDK: `brew install --cask dotnet-sdk`, or use the installer from https://dotnet.microsoft.com/download.
2. From the repository root:

   ```sh
   dotnet dev-certs https --trust   # one time, so the browser trusts https://localhost
   dotnet run --project src/PropertyManager.Web
   ```

3. Open the URL printed in the terminal (for example `https://localhost:7xxx`).

To work in an IDE, open `PropertyManager.slnx` in JetBrains Rider, or open the folder in VS Code with the C# Dev Kit extension.

## Run on Windows

Everything works on Windows except importing text messages, which needs the Mac's Messages app.

1. Install the .NET 10 SDK and Git. In PowerShell:

   ```powershell
   winget install Microsoft.DotNet.SDK.10
   winget install Git.Git
   ```

   Close and reopen PowerShell afterwards so the new commands are found.

2. Get the code and start the app:

   ```powershell
   git clone https://github.com/shellypayahoo/PropertyManagerTax.git
   cd PropertyManagerTax
   dotnet dev-certs https --trust   # one time, so the browser trusts https://localhost
   dotnet run --project src\PropertyManager.Web
   ```

3. Open the URL printed in PowerShell (for example `https://localhost:7xxx`). Stop the app with Ctrl+C.

To work in an IDE, open `PropertyManager.slnx` in Visual Studio 2022 or later (or Rider) and press F5. To update later, run `git pull` in the `PropertyManagerTax` folder and start the app again.

### Settings

User secrets are stored per computer (on Windows in `%APPDATA%\Microsoft\UserSecrets`), so set them again on the Windows machine, from the `PropertyManagerTax` folder:

```powershell
dotnet user-secrets set "Assistant:Providers:AzureFoundry:Model" "<deployment name>" --project src\PropertyManager.Web
dotnet user-secrets set "Assistant:Providers:AzureFoundry:Endpoint" "https://<resource>.cognitiveservices.azure.com/openai/v1/" --project src\PropertyManager.Web
dotnet user-secrets set "Assistant:Providers:AzureFoundry:ApiKey" "<key>" --project src\PropertyManager.Web
dotnet user-secrets set "Email:ClientId" "<Application (client) ID>" --project src\PropertyManager.Web
```

Then sign in once on **Email & calendar**. On Windows the email sign-in is kept encrypted for your Windows user (instead of in the Keychain).

### Your data

A new copy on Windows starts with an empty database and the first-run setup. The two computers don't sync, so to use your existing data instead:

1. Stop the app on the Mac.
2. Copy `src/PropertyManager.Web/propertymanager.db` and the `src/PropertyManager.Web/App_Data` folder (lease files, receipts, attachments, photos) from the Mac into the same places on Windows, replacing what's there.
3. Start the app on Windows.

From then on, changes made on one computer don't appear on the other, so treat one as the main machine and copy the files again when you switch. Don't run both against the same database in a shared OneDrive or iCloud folder; SQLite databases can be damaged that way.

### What's different on Windows

- **Text messages:** importing needs the Mac's Messages app. Texts already imported (in the copied database) still show on the Messages page and can be searched on the Ask page; to bring in new ones, import on the Mac and copy the database over again.
- **iPhone (HEIC) photos uploaded on Windows:** the original is saved, but no JPEG copy is made (that uses a macOS tool), and Edge and Chrome can't show HEIC. Upload JPG/PNG photos on Windows, or upload HEIC photos from the Mac. Photos already converted on the Mac show fine.
- Email, calendar, the Ask page, receipts, documents, rent, expenses, and reports work the same.

## Install for someone else on Windows (no .NET needed)

To give the app to an owner who isn't technical, build a package that includes .NET, so they install nothing else.

**1. Build it** (on your Mac, from the repository root):

```sh
./scripts/package-windows.sh
```

This creates `dist/PropertyManager-Windows.zip` (about 70 MB) containing:

- **Start Property Manager.cmd**: starts the app and opens it in the browser.
- **README.txt**: plain instructions for the owner.
- **app/**: the app with .NET included, and settings for a new owner (none of your properties or keys; email, calendar, the assistant, and text messages start off).

**2. Install it on their computer:** copy the zip over (OneDrive, USB drive, email), right-click it → **Extract All**, and put the `PropertyManager-Windows` folder somewhere permanent, such as their Documents folder. Right-click **Start Property Manager** → **Show more options** → **Send to** → **Desktop (create shortcut)** for an icon.

**3. First start:** double-click **Start Property Manager**. The app isn't signed, so Windows may show "Windows protected your PC": click **More info**, then **Run anyway**. A black window opens (keep it open while using the app; closing it stops the app) and then the browser shows the first-run setup.

**4. Optional extras for them:** copy `app/appsettings.Local.example.json` to `app/appsettings.Local.json`, fill in their AI service key and/or email app registration ID (see **Ask** and **Email**), restart the app, and turn those on in **Settings**. That file holds their secrets, so it stays on their computer only.

**Their data** is kept in **Documents\Property Manager** (the database and a `documents` folder), not in the app folder. Back up that folder. **To update** them, build a new package and replace the `PropertyManager-Windows` folder; their data and `appsettings.Local.json` (copy it into the new `app` folder) carry over.

The app is reachable only on their computer (`http://localhost:5220`). To use it from their phone, see **Use it from your phone**; on Windows that means editing `ASPNETCORE_URLS` in the start file to `http://0.0.0.0:5220`.


While the app runs on your Mac or Windows computer, your phone can open it over the same Wi-Fi:

1. Start the app so other devices can reach it, from the repository folder:

   ```sh
   dotnet run --project src/PropertyManager.Web --urls http://0.0.0.0:5220
   ```

2. Find the computer's address: on a Mac run `ipconfig getifaddr en0`; on Windows run `ipconfig` and use the IPv4 address (for example `10.0.0.154`).
3. On the phone, open `http://<that address>:5220` (type the `http://`). Allow the connection if macOS or Windows Defender Firewall asks. In Safari, Share → **Add to Home Screen** gives it an icon.

The app has no sign-in, so anyone on your Wi-Fi who knows the address can open it while it runs this way; stop it with Ctrl+C when you're done. To reach it away from home, install [Tailscale](https://tailscale.com) on the computer and the phone and use the computer's Tailscale name or address instead.

## First-run setup and Settings

When the app starts with no properties, it opens a three-step **setup**: the business name and when rent is due and late; each property's address and units with their monthly rent; and which parts of the app to use (receipts, property photos, email, calendar, the Ask assistant, and, on a Mac, text messages). No files need editing.

The **Settings** page (bottom of the menu) changes the same things later, plus which AI model the Ask page starts on and whether it can look at photos. It shows whether email, calendar, and the assistant are set up; their keys and IDs (see **Ask** and **Email**) are still added by whoever installs the app. Choices made in the app are saved in the database and override `appsettings.json`; **Undo all changes made here** goes back to the file's values.

## Configuration

Settings live in `src/PropertyManager.Web/appsettings.json`; the ones on the Settings page can also be changed there in the app, which takes precedence. Secrets (AI keys) never go in that file: set them with `dotnet user-secrets set "<setting>" "<value>" --project src/PropertyManager.Web`, in an `appsettings.Local.json` next to it (not in git; used by packaged installs, where user secrets aren't read), or as environment variables (use `__` for `:`, e.g. `Assistant__Providers__OpenAI__ApiKey`). Every setting below has a default that matches how the app works out of the box, so you only add what you want to change. [docs/appsettings.example.json](docs/appsettings.example.json) shows a complete example for a new installation.

| Section | Setting | Default | What it does |
| --- | --- | --- | --- |
| `Features` | `TextMessages`, `Email`, `Calendar`, `Assistant`, `Receipts`, `PropertyPhotos` | `true` | `false` turns that part off: its page and menu item are hidden (or show a note), nothing is imported or synced for it, and the Ask assistant gets no lookups for it. Data already in the app stays. |
| `Business` | `Name` | none | Shown in the menu and at the top of the P&L PDF. |
| | `RentDueDay` | `1` | Day rent is due (1–28); the Rent collection report counts days late from it. |
| | `LateAfterDays` | `5` | The Rent collection report's starting grace period. |
| `Setup` | `Properties` | none | Properties (name, street, city, state, ZIP) and units (label, market rent) created when the app starts with an empty database, skipping the first-run setup's property step. Ignored once any property exists, so it never changes an existing portfolio. |
| `Assistant` | `DefaultProvider` | first one set up | Which provider the Ask page starts on. |
| | `AllowPhotos` | `true` | Whether "Let it look at photos" starts ticked. |
| | `Providers:<Name>` | four examples | `Type` (`AzureFoundry`, `OpenAI`, `Anthropic`, `AnthropicFoundry`, or `Ollama`), `Model`, and `Endpoint`/`Resource` where needed; `ApiKey` in secrets. `OpenAI` also reads `OPENAI_API_KEY`, and `Endpoint` lets it use any OpenAI-compatible service. `AzureFoundry` and `OpenAI` use OpenAI's Responses API (`/openai/v1/responses` on Azure), which newer reasoning models need for the assistant's lookups; set `"Api": "ChatCompletions"` for a service that only offers the older chat completions API. Add as many as you like under any names. |
| `Email` | `ClientId`, `Tenant`, `MyAddresses` | not set | Outlook.com / Hotmail sign-in (see **Email**). |
| `ConnectionStrings` | `PropertyManager` | `Data Source=propertymanager.db` | The SQLite database file. |
| `DocumentStorage` | `LocalPath` | `App_Data/documents` | Where uploaded and imported files are kept. |

When the app starts it checks these settings. Mistakes, such as an unknown provider type, a `DefaultProvider` that isn't listed, or a rent due day of 31, are logged in the terminal and listed at the top of the Dashboard in plain words. Change settings while the app is stopped, then start it again.

## Tests

```sh
dotnet test
```

## Database changes

After changing an entity or `PropertyManagerDbContext`, add a migration. The app applies pending migrations at startup.

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/PropertyManager.Core --startup-project src/PropertyManager.Web --output-dir Data/Migrations
```

## Layout

An architecture diagram (what runs where, where data is stored, and which cloud services are used) is in [docs/architecture.md](docs/architecture.md); GitHub draws it when you open the file.

- `src/PropertyManager.Core`: entities, `PropertyManagerDbContext`, migrations, seed data, report services, imports, and the Ask assistant (`Assistant/`)
- `src/PropertyManager.Web`: Blazor Server UI (`Components/Pages`)
- `tests/PropertyManager.Tests`: xUnit tests for rent proration, the P&L report, and seeding

## Leases with more than one tenant

A lease can have several tenants (roommates, a couple, a co-signer) who share one rent. Check everyone when creating the lease, or add and remove people later on the lease's page. Rent payments from any of them, including Zelle payments matched during an import, count toward the shared lease.

## Lease documents

Open a lease from the **Leases** page to edit its terms and upload the signed lease, addenda, or inspection reports (PDF, Word, JPG, PNG, HEIC; up to 25 MB each).

Files are read in the browser before they're sent. If a file can't be read, typically an online-only OneDrive or iCloud file that hasn't been downloaded to the Mac, the page says so instead of failing. In Finder, right-click the file and choose **Always Keep on This Device**, or download it from onedrive.com, then upload again.

The text of PDF and Word (.docx) files is read when they're uploaded, so the **Ask** page can search and quote them (files uploaded earlier are read the next time the app starts). Photos, scanned PDFs without a text layer, and old .doc files have no readable text; the lease page marks them.

Besides that text, the database stores each file's name, size, and upload time. The files themselves are saved under `src/PropertyManager.Web/App_Data/documents` (set `DocumentStorage:LocalPath` in `appsettings.json` to use another folder, such as one in iCloud Drive or OneDrive). File access goes through `IDocumentStorage`, so moving to Azure Blob Storage later means adding one class and changing one line in `Program.cs`.

## Business documents

The **Documents** page holds papers about the business rather than a tenant or an expense: LLC formation papers, tax returns, 1099s and W-9s, insurance policies, deeds and closing papers, mortgage and loan papers, and bank statements.

1. Type a **Description** such as "Closing", "Tax 2026", or "Articles of organization" (leave it blank to use the file name).
2. Pick the **Type**, the **Property** it's about (or **Whole business** for LLC papers), and optionally the **Year**.
3. Choose one or more files (PDF, Word, JPG, PNG, HEIC; up to 25 MB each) and click **Upload**. Every file in one upload is filed the same way.

Filter the list by type, property, or year, or type in the search box to match descriptions. Click a description to open the file; **Edit** changes its description, type, property, year, or privacy; **Delete** asks once more before removing it.

**Keep out of Ask:** the Ask page can search and read these documents ("What's my LLC's EIN?", "When does the Taylor insurance renew?", "What were my closing costs on 1st St?"), which sends their text to your AI model. Tick **Keep out of Ask** on any document to store it here but never send it. Tax returns and 1099s/W-9s start out ticked because they hold Social Security and tax ID numbers; untick it (with **Edit**) if you want Ask to read one. Ask can also save a file you attach there to the Documents page; like other changes, it shows a card and waits for **Apply**.

The files are stored with the other documents (see [Lease documents](#lease-documents)).

## Importing financial information

**Bank and Zelle CSV files** (**Import** page): download transactions as CSV from your bank's website and upload them. Common layouts (Chase, Bank of America, Capital One, Wells Fargo, and others) are detected automatically, including banks whose amounts are all positive with a separate Debit/Credit column; otherwise you pick the columns once per account.

Every row is reviewed before it is saved, and each is recorded as one of:

- **Rent received**: deposits whose description contains a tenant's first and last name (as Zelle descriptions do) are matched to that tenant's lease.
- **Expense**: assigned to a property (and optionally a unit), or to **All properties (shared)**, and a Schedule E category.
- **Transfer**: money moved between your own accounts, such as Zelle rent swept into the business account. Skipped, so rent isn't counted twice.
- **Ignore**: anything unrelated to the rentals.

Use **Remember** on a row to save a rule (for example "PEPCO" → Utilities, 1354 Taylor St NW) that fills in future imports. Rows already imported into an account are skipped, so overlapping files are safe. Rows that look like an expense or payment you already entered by hand are suggested as ignored.

**Cash or check rent**: use **Mark paid** on the Rent page or a lease's page.

## Shared expenses

Costs of the whole rental business, such as software, cloud hosting, or accounting, can be recorded as **All properties (shared)** when importing or on the Expenses page. Schedule E needs expenses per property, so the annual report splits each shared expense across properties by number of units and notes how much was shared.

On the Expenses page, **Edit** changes any expense, and checking several expenses lets you change their property, category, or vendor at once.

**Receipts**: click 📎 on an expense to attach receipts or invoices: photos (JPG, PNG, or iPhone HEIC; on a phone you can take the photo with the camera), PDFs, or Word files, up to 25 MB each. The button turns green and shows how many are attached; click it again to view or remove them. After adding an expense, its receipt box opens so you can attach one right away. Tick **Without a receipt only** to see which expenses still need one. Receipt text (PDF/Word) and photos are available to the **Ask** page, and files are kept in `App_Data/documents` with the other documents. When an expense is split, its receipts stay on the first part.

**By vendor**: pick a vendor from the vendor dropdown (next to the year and property) to see only that vendor's expenses for the year, with a summary of the total and how it breaks down by category and property. Click a vendor's name in the list, or on the **Vendors** page, to jump straight there. **No vendor** shows expenses without one.

**Split** divides one expense into parts, each with its own property, unit, category, and amount. **Divide across units** spreads a bill evenly over the units you tick (rounding pennies go to the last one), e.g. a cleaning crew that does three of the four units.

## Recurring expenses

**Expenses → Recurring** sets up a monthly bill once, such as an HVAC service plan at $30 per unit: name, vendor, category, how it's paid, the amount per unit, the day of the month, a start (and optional end) date, and the units it applies to (or all properties). The app adds one expense per unit each month, catching up on months it missed whenever it starts or the Expenses page is opened. Each month is added only once; deleting or editing an added expense doesn't bring it back, and changing the amount affects only months added afterwards. If the same charges show up later in a card or bank import, they're flagged as likely duplicates.

## Mortgage payments

Only the interest in a mortgage payment is deductible (line 12). Principal repays the loan, and money paid into escrow isn't deductible until the lender pays your property taxes (line 16) or insurance (line 9) out of it. So there are two not-deductible categories, **Mortgage principal** and **Escrow deposit**, which are tracked but kept off the Schedule E totals and noted under the report. Record each property tax or insurance payment the lender makes from escrow as a **Taxes** or **Insurance** expense on the date paid; the escrow statement and Form 1098 list them.

Click **Split** on an expense to divide it by category; **Mortgage payment** fills in interest, principal, and escrow deposit, and you enter the amounts from the statement. Alternatively, mark the monthly payments as **Transfer** and once a year add, for each property, one interest expense from Form 1098 plus the taxes and insurance paid from escrow.

### Importing a lender's payment history

If the mortgage company offers a CSV payment history, use **Import → Import a mortgage payment history**. Create the loan once (name and property); the date, principal, interest, escrow, and total columns are detected automatically or chosen once per loan.

- Each payment is matched to the bank expense for the same amount within 10 days and that expense is **split** into interest, principal, and escrow deposit, so the payment isn't counted twice. Any amount not covered by those (such as a late fee) goes to Other.
- A payment with no matching bank expense is added as new expenses; if that bank payment is imported later, it's suggested as **Ignore**.
- Property tax and insurance the lender paid from escrow are added as **Taxes** and **Insurance** on the date paid.
- Rows already imported for the loan are skipped on later imports. Lenders often include years of history, so the review screen starts from the most recent year; earlier years can be imported later.

**Lakeview** doesn't offer CSV downloads. Save its Payment History page as a PDF (in Safari: File → Export as PDF) and upload the PDF instead. The app reads the payments and the tax and insurance disbursements from it (using [PdfPig](https://github.com/UglyToad/PdfPig)) and shows them on the same review screen, with a warning if a row's parts don't add up.

## Tracking rent

The **Rent** page lists every lease for a month with its rent, what's been paid, and a status (Paid, short, Not paid). **Mark paid** records the payment toward that month; change the amount for a partial payment. Each payment records both the date it was received and the month it's for, so October rent paid on November 2 still counts for October.

To fix a payment, click **Edit** next to it to change the date, amount, method, or the month it counts for, or to delete it. Before a payment is saved, the app warns if it looks like a double entry (the month is already paid, or the same amount on the same date is already recorded). Months that look paid twice are flagged **Paid twice?** and listed at the top of the Rent page and the lease's page. Imports also skip Zelle rent for a month you already marked paid by hand.

A lease's page shows the same thing month by month for a year. Rent isn't prorated: the full monthly rent is due for every month a lease covers.

## Text messages

The **Messages** page brings in text conversations from the Mac's Messages app (which includes your iPhone's texts when Messages in iCloud is on, and SMS when Text Message Forwarding is on).

1. **Find contacts in Messages** lists everyone you've texted, with names from the Contacts app, how many texts you've exchanged, and when you last texted. No message text is read at this step.
2. Tick the people whose conversations you want. Tenants and vendors (matched by the phone or email on their Tenants/Vendors page) are ticked automatically; everyone else starts unticked. Use the **Is** column to link a contact to a tenant or vendor.
3. **Import messages for ticked contacts** copies those conversations into the app. Run it again any time to pick up new texts; messages already imported are skipped. Unticking someone removes their messages from the app.

The app reads a copy of `~/Library/Messages/chat.db` (and the Contacts databases under `~/Library/Application Support/AddressBook`); it never changes Messages or Contacts. macOS protects these folders, so give **Full Disk Access** to Terminal (or whatever you start the app from) in System Settings → Privacy & Security. Tapback reactions and group-chat events are skipped.

Photos, videos, and files sent in those conversations are copied too (up to 50 MB each; stickers and link previews are skipped) and shown in the conversation, newest messages first: tap a photo to open it full size. iPhone photos are HEIC, so the app also saves a JPEG copy (using macOS's built-in `sips`) that every browser can show, plus a thumbnail. PDFs and Word files have their text read so the **Ask** page can search them. With Messages in iCloud and **Optimize Mac Storage** on, older attachments may only be in iCloud; those are marked "not downloaded to this Mac" and copied on a later import once you open the conversation in Messages so it downloads them. Messages imported before attachment support get their attachments on the next import.

**Former tenants and vendors.** If you still text or email someone who's no longer your tenant (or a vendor you no longer use), set **Keep texts & emails until** for them, on the Tenants or Vendors page (it also appears under their name in the Messages contact list for former tenants and anyone with a date set). Texts and emails with them after that date are removed from the app, with their photos and files, and skipped by later imports and email syncs; everything up to the date stays and remains searchable on the Ask page. For a tenant whose leases have all ended, the app suggests the last lease end plus a month, to cover the deposit and move-out. **Keep everything** clears the date; skipped texts come back on the next import and skipped emails on the next sync. Nothing is changed on your phone or in your mailbox. Unticking a contact still removes all of their texts. Paths can be overridden with `Messages:DatabasePath` and `Messages:AddressBookFolder` in `appsettings.json`.

## Email

The **Email** page copies emails from the Hotmail / Outlook.com folders you choose (for example a "Tenants" folder) into the app, so the **Ask** page can search them alongside texts and records. Mail access is read-only (Microsoft Graph `Mail.Read`); nothing in the mailbox is changed. The only thing the app ever writes is calendar appointments you add (see [Calendar](#calendar)).

- Emails are linked to a tenant or vendor by the email address on their Tenants/Vendors page.
- Each email's text is stored twice: the newly written part (quoted earlier replies removed), which is what search uses, and the full body.
- PDF and Word attachments are saved and their text is read so they can be searched like lease documents. Pictures are saved and can be opened from the email; the assistant sees only their names. Inline images (logos, signatures) and other file types are skipped.
- **Get new emails** fetches only what changed since the last sync: new emails, edits, and emails deleted or moved out of the folder. Unticking a folder removes its emails from the app.

### One-time setup: app registration

Microsoft requires an app registration for any app that reads mail. It's free:

1. Sign in to the [Azure portal](https://portal.azure.com) with an account in your own (personal) directory, not a work directory, and open **Microsoft Entra ID → App registrations → New registration**.
2. Name: `Property Manager`. Supported account types: **Personal Microsoft accounts only** (or "accounts in any organizational directory and personal Microsoft accounts"). Leave the redirect URI empty. Click **Register**.
3. On the app's **Authentication** page, turn on **Allow public client flows** and save.
4. On **API permissions**, add **Microsoft Graph → Delegated permissions → Mail.Read**, **Calendars.Read**, and **Calendars.ReadWrite**. (No admin consent is needed for a personal account.)
5. Copy the **Application (client) ID** from the **Overview** page and, from the repository root:

   ```sh
   dotnet user-secrets set "Email:ClientId" "<Application (client) ID>" --project src/PropertyManager.Web
   ```

6. Restart the app, open **Email & calendar**, click **Sign in to Microsoft**, and enter the code shown at microsoft.com/devicelogin with your Hotmail account. Then **Choose folders**, tick the tenant folder, **Save**, and **Get new emails**.

The sign-in is remembered in the macOS Keychain (macOS may ask to allow access the first time), so you don't sign in on every start; **Sign out** removes it. The app never sees your password. If you send email from more than one address, list the others with `Email:MyAddresses` so they count as sent by you.

### Calendar

The same page (**Email & calendar**) copies appointments from the Outlook.com calendars you choose, for example one you keep for the rentals. **Get calendar events** refreshes everything from a year ago to a year ahead (recurring appointments are expanded into each occurrence); appointments cancelled or deleted in Outlook are removed.

- The **Dashboard** lists the next two weeks of appointments, with the property each one mentions (by street number or street name, e.g. "1354" or "Taylor") and the maintenance request it's linked to.
- On **Maintenance**, open requests show appointments that look like the visit: within a few days before to four months after the request was reported, mentioning its property or its vendor, and ideally its unit or words from its title. **Use as scheduled date** links the appointment, fills in the scheduled date, and marks the request Scheduled; **Not this** hides a suggestion.

**Adding appointments:** the app can add appointments to your Outlook calendar, so they show on your phone and in Outlook like any other:

- **Dashboard** and **Email & calendar**: **+ Add appointment** for showings, inspections, a meeting with your accountant. Pick a property under **Fill in an address** to put its address in the appointment.
- **Maintenance**: **📅 Add to calendar** on an open request fills in the vendor, issue, unit address, and the vendor's and tenant's phone numbers. Adding it links the appointment to the request and marks it Scheduled.
- **Leases**: on a lease with an end date, **30 / 60 / 90 days before** adds an all-day reminder before the lease ends (reminding you at 9 AM that day), and the lease page shows it.
- **Ask**: "put the plumber on my calendar Tuesday at 10" or "remind me 60 days before the Taylor basement lease ends" shows a proposed appointment with **Apply**, like other changes.

Each form lets you change the title, date, time, length, place, notes, and reminder, and choose which synced calendar it goes in (the one you used last is picked first). Under the date it shows the day of the week, so a wrong date stands out.

**Time zones:** the app uses the business's time zone (**Settings → Time zone**; this repository's `appsettings.json` sets Eastern, `America/New_York`, for the DC rentals) rather than the computer's, so a Mac on Mountain time still shows tenant appointments, texts, and emails in Eastern time. New appointments start out in that zone, and the form's **Time zone** box lets you pick another one for an appointment where you are (Ask understands "1 PM Mountain" too). Appointments made in another zone show that zone's time with its label, e.g. "1:00 PM MT", with the Eastern time under it. Choose **This computer's** in Settings to follow the computer instead. On Windows the app always uses the computer's time zone.

The **Ask** page can also look up your synced appointments ("what's on my calendar next week?", "when is the dishwasher delivery?"). Appointments go only into calendars you've chosen above. The app never changes or deletes appointments in Outlook.

Calendar access uses the **Calendars.Read** permission, and adding appointments uses **Calendars.ReadWrite**. If you signed in before these were added, the page asks you to sign in again once (email and calendar syncing keep working until you do). You can also add those Microsoft Graph delegated permissions under the app registration's API permissions, though personal accounts are asked for them at sign-in either way.

## Ask (AI assistant)

The **Ask** page answers questions about your imported texts and the app's records, for example "When did the Taylor basement tenant first mention the leak?", "Has everyone paid March rent?", "What did I pay Ace Plumbing this year?", or "What does the Taylor basement lease say about pets?". The assistant looks things up with read-only tools (text message and email search, conversations, contacts, uploaded lease documents and email attachments, properties/leases/tenants, rent status, expenses, maintenance requests, and the annual profit and loss), cites the dates and senders of texts it relies on, and lists the lookups it made under each answer. It can't change any data.

**Attaching files:** click **📎 Attach files** under the question box to send photos (including iPhone HEIC), PDFs, Word, CSV, or text files (up to 20 MB each) with a question, e.g. "here's the plumber's invoice; what did they charge for parts?". The text of documents and the photos go to the model with that question; later in the conversation it can read them again.

**Changes, with your approval:** the assistant can propose adding or editing an expense (and attaching an uploaded receipt), recording a rent payment, adding or updating a maintenance request, adding a vendor, updating a tenant's phone, email, or notes, and saving an uploaded file as a receipt or lease document. Each proposal appears as a card under its answer listing exactly what will change (with a warning if, say, that month's rent looks already paid), with **Apply** and **Dismiss**. Nothing changes until you click **Apply**; the card then says what was done. It never deletes anything.

Pick the model from the dropdown; you can switch mid-conversation. Under the dropdown the page says where the looked-up texts and records are sent. Only what's needed to answer each question is sent, not the whole database.

| Name | Model | Where data goes |
| --- | --- | --- |
| `Claude` | Claude through the Anthropic API (`claude-opus-5-5`) | Anthropic |
| `AzureFoundry` | Any model deployed in your Azure AI Foundry (or Azure OpenAI) resource, e.g. a GPT deployment | Your Azure resource |
| `ClaudeOnFoundry` | Claude deployed in your Azure AI Foundry resource | Your Azure resource |
| `Ollama` | A local model in [Ollama](https://ollama.com) (`llama3.1`) | Stays on the Mac |

The non-secret settings are in `appsettings.json` under `Assistant:Providers`. **API keys go in user secrets, never in `appsettings.json`**, so they stay out of git. From the repository root on the Mac:

```sh
cd src/PropertyManager.Web

# Claude (key from console.anthropic.com)
dotnet user-secrets set "Assistant:Providers:Claude:ApiKey" "<your Anthropic API key>"

# A model deployed in Azure AI Foundry. In the Foundry portal, open the deployment: use its
# deployment name, the resource's endpoint ending in /openai/v1/, and the key.
dotnet user-secrets set "Assistant:Providers:AzureFoundry:Model" "<deployment name>"
dotnet user-secrets set "Assistant:Providers:AzureFoundry:Endpoint" "https://<resource>.openai.azure.com/openai/v1/"
dotnet user-secrets set "Assistant:Providers:AzureFoundry:ApiKey" "<key>"

# Claude deployed in Azure AI Foundry (resource name is the <resource> part of its URL)
dotnet user-secrets set "Assistant:Providers:ClaudeOnFoundry:Resource" "<resource>"
dotnet user-secrets set "Assistant:Providers:ClaudeOnFoundry:ApiKey" "<key>"
```

To add another deployment, set any new name with a `Type` (`Anthropic`, `AzureFoundry`, `AnthropicFoundry`, or `Ollama`), `Model`, and the same settings, e.g. `Assistant:Providers:Foundry-mini:Type`. User secrets are stored in `~/.microsoft/usersecrets` and are read when the app runs with `dotnet run` (the Development environment). Environment variables also work, e.g. `Assistant__Providers__Claude__ApiKey`, or `ANTHROPIC_API_KEY` for `Claude`.

For Ollama, install it, run `ollama pull llama3.1` (or another model that supports tool calling; change `Model` to match), and keep the Ollama app running.

Notes:

- Each question is billed by the provider you choose. Questions that read many texts cost more.
- With **Let it look at photos** ticked, the assistant can look at photos sent by text or email when a message's text isn't enough (for example "what did the leak look like?"). Each photo is a separate request with the image to the same model, which uses noticeably more of your rate limit; untick it to keep questions to text only.
- Azure deployments have a tokens-per-minute limit (student subscriptions' are small). If the page says the limit was reached, wait a minute or raise the deployment's rate limit in Azure AI Foundry (Deployments → the model → Edit). The app already waits and retries a few times before giving up, and only questions and answers (not earlier lookups) are re-sent with follow-up questions.
- If Claude declines to answer a request, it's automatically retried once on Claude Opus 4.8, and that conversation stays on Opus 4.8. If both decline, the page says so.
- Rent, expenses, leases, tenants, maintenance, and documents are looked up when you ask, so new entries and uploads show up right away. New texts need **Import messages for ticked contacts** on the Messages page first, and new emails need **Get new emails** on the Email page.
- The conversation is saved in the app's database, so it's still there when you go to another page, refresh, restart the app, or open Ask on your phone. **Clear conversation** deletes it and starts over. Attached files can be read again only until the app restarts (their names stay), and a proposal you haven't applied yet becomes "Not applied" after a restart; ask again to make it. One question is answered at a time, even across devices.

## Other reports

The **Reports** page has tabs next to **Profit & loss**, each for the chosen year:

- **Cash flow**: rent received and money paid out each month, with net and a running total. Unlike the P&L, mortgage principal and escrow deposits count as money out, since they leave your account. Pick one property to see just its numbers (shared expenses count by its share of units). Click a month (or **See payments** under a month in the red) to list everything paid out that month, largest first.
- **Rent collection**: a grid of every lease by month: paid on time, paid late (with how many days after the 1st), partly paid, not paid, or due. Rent is treated as due on the 1st; choose the grace period (none, 3, 5, 7, or 10 days). Each row totals rent due so far, paid, the balance owed, and the number of late and missed months.
- **Vendors**: total paid to each vendor across all categories, largest first, with its share of spending, categories, and properties. Click a vendor to list the items. Expenses without a vendor are grouped as "No vendor recorded"; set vendors on the Expenses page.

## PDF for your accountant

On **Reports → Profit & loss**, the **Period** choice shows the P&L for the full year, year to date, one quarter (Q1–Q4 of the selected year), the **last 3 completed quarters** (on Oct 26, 2026 that's Jan 1 – Sep 30, 2026), or any **custom dates**. The table, the item lists behind each line, and the PDF all use the chosen period; scheduled rent counts every month the period touches. The Ask page can answer for a period too ("what was my net income for the first three quarters?").

On **Reports**, pick the year (or period) and click **Download PDF** to get `Rental P&L <year>.pdf` (or `Rental P&L <from> to <to>.pdf` for another period, titled with its dates): the property addresses, the Schedule E summary by property (rents received, expenses by line, total expenses, net income), and notes on shared expenses split by unit count, mortgage principal and escrow deposits left out, and depreciation. With **Include every item behind each line** ticked (the default), it's followed by an itemized list for each line (date, property, description, vendor or how paid, amount) with totals that match the summary. Losses are shown in parentheses.

PDFs are made with [QuestPDF](https://www.questpdf.com) under its free Community license (individuals and businesses with under $1M in annual revenue).

## Property photos

Open a property from **Properties** to give it a photo: **Upload** a picture (JPG, PNG, or an iPhone HEIC photo, which is converted to JPEG), or type a web page address such as your LLC's website and click **Find photos** to pick one of the pictures on that page (or paste a picture's own address, from right-click → Copy Image Address). The photo shows on the property's page, in the Properties list, and on the Dashboard's unit cards. Photos are saved in `App_Data/documents`; the app reads the website from your Mac, so it needs an internet connection only when you find or download a photo.

## Maintenance

The **Dashboard** shows a card for each unit listing its open Emergency, High ("urgent"), and Normal requests as reminders, most urgent first (the card is outlined red or yellow when something urgent is open; units with nothing open say "All clear"), with scheduled date (red once it has passed), vendor, and how long ago it was reported (red after two weeks); Low-priority requests stay on the Maintenance page; each card shows how many it has ("+ 2 low priority"), and the Open maintenance box at the top counts the urgent and normal ones with the low-priority ones noted under it. Add a request at the top of the **Maintenance** page (unit, issue, priority, date reported, vendor, details). Vendor and status can be changed right in the list; **Edit** opens the whole request below its row to change the unit, issue, priority, status, reported/scheduled/completed dates, vendor, or details, or to delete it (expenses recorded for it are kept).

## Vendors

Add and edit vendors at the top of the **Vendors** page; a second vendor with the same name is refused. To fix a vendor entered twice, click **Merge into…** on the duplicate and choose the one to keep: its expenses, recurring expenses, maintenance requests, import rules, texts, and emails move over, the kept vendor gets any phone, email, trade, or notes it was missing, and the duplicate is deleted. **Delete** removes a vendor after saying what uses it; those items are kept with no vendor.

## Notes

- On the report, click a line name (e.g. Cleaning and maintenance, or Rents received) to list every item behind it, or click one property's amount to see just that property's items, including its share of shared expenses. Each list ends with a total that matches the report.
- The report's income is rent actually received in the year (by date received). Scheduled rent from leases, in whole months, is shown beside it for comparison.
- Back up `propertymanager.db` and `App_Data/documents` (which also holds email attachments). Both are excluded from git.
