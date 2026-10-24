# PropertyManagerTax

Property management code that generates tax reports and keeps track of expenses and income.

A Blazor (ASP.NET Core, .NET 10) app for managing rental properties: units, tenants, leases, rent payments, expenses, maintenance requests, vendors, bank imports, and an annual profit and loss report laid out by IRS Schedule E line.

Data is stored in a local SQLite file (`src/PropertyManager.Web/propertymanager.db`) with EF Core. On first run the database is created and seeded with:

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

Photos, videos, and files sent in those conversations are copied too (up to 50 MB each; stickers and link previews are skipped) and shown in the conversation, newest messages first: tap a photo to open it full size. iPhone photos are HEIC, so the app also saves a JPEG copy (using macOS's built-in `sips`) that every browser can show, plus a thumbnail. PDFs and Word files have their text read so the **Ask** page can search them. With Messages in iCloud and **Optimize Mac Storage** on, older attachments may only be in iCloud; those are marked "not downloaded to this Mac" and copied on a later import once you open the conversation in Messages so it downloads them. Messages imported before attachment support get their attachments on the next import. Paths can be overridden with `Messages:DatabasePath` and `Messages:AddressBookFolder` in `appsettings.json`.

## Email

The **Email** page copies emails from the Hotmail / Outlook.com folders you choose (for example a "Tenants" folder) into the app, so the **Ask** page can search them alongside texts and records. Access is read-only (Microsoft Graph `Mail.Read`); nothing in the mailbox is changed.

- Emails are linked to a tenant or vendor by the email address on their Tenants/Vendors page.
- Each email's text is stored twice: the newly written part (quoted earlier replies removed), which is what search uses, and the full body.
- PDF and Word attachments are saved and their text is read so they can be searched like lease documents. Pictures are saved and can be opened from the email; the assistant sees only their names. Inline images (logos, signatures) and other file types are skipped.
- **Get new emails** fetches only what changed since the last sync: new emails, edits, and emails deleted or moved out of the folder. Unticking a folder removes its emails from the app.

### One-time setup: app registration

Microsoft requires an app registration for any app that reads mail. It's free:

1. Sign in to the [Azure portal](https://portal.azure.com) with an account in your own (personal) directory, not a work directory, and open **Microsoft Entra ID → App registrations → New registration**.
2. Name: `Property Manager`. Supported account types: **Personal Microsoft accounts only** (or "accounts in any organizational directory and personal Microsoft accounts"). Leave the redirect URI empty. Click **Register**.
3. On the app's **Authentication** page, turn on **Allow public client flows** and save.
4. On **API permissions**, add **Microsoft Graph → Delegated permissions → Mail.Read** and **Calendars.Read**. (No admin consent is needed for a personal account.)
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

Calendar access uses the **Calendars.Read** permission. If you signed in before calendar support was added, the page asks you to sign in again once. You can also add Microsoft Graph → Delegated → **Calendars.Read** under the app registration's API permissions, though personal accounts are asked for it at sign-in either way.

## Ask (AI assistant)

The **Ask** page answers questions about your imported texts and the app's records, for example "When did the Taylor basement tenant first mention the leak?", "Has everyone paid March rent?", "What did I pay Ace Plumbing this year?", or "What does the Taylor basement lease say about pets?". The assistant looks things up with read-only tools (text message and email search, conversations, contacts, uploaded lease documents and email attachments, properties/leases/tenants, rent status, expenses, maintenance requests, and the annual profit and loss), cites the dates and senders of texts it relies on, and lists the lookups it made under each answer. It can't change any data.

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
- A conversation is kept only while the page is open; **New conversation** starts over.

## Other reports

The **Reports** page has tabs next to **Profit & loss**, each for the chosen year:

- **Cash flow**: rent received and money paid out each month, with net and a running total. Unlike the P&L, mortgage principal and escrow deposits count as money out, since they leave your account. Pick one property to see just its numbers (shared expenses count by its share of units). Months in the red list everything paid out that month, largest first; click any month to see or hide its payments.
- **Rent collection**: a grid of every lease by month: paid on time, paid late (with how many days after the 1st), partly paid, not paid, or due. Rent is treated as due on the 1st; choose the grace period (none, 3, 5, 7, or 10 days). Each row totals rent due so far, paid, the balance owed, and the number of late and missed months.
- **Vendors**: total paid to each vendor across all categories, largest first, with its share of spending, categories, and properties. Click a vendor to list the items. Expenses without a vendor are grouped as "No vendor recorded"; set vendors on the Expenses page.

## PDF for your accountant

On **Reports**, pick the year and click **Download PDF** to get `Rental P&L <year>.pdf`: the property addresses, the Schedule E summary by property (rents received, expenses by line, total expenses, net income), and notes on shared expenses split by unit count, mortgage principal and escrow deposits left out, and depreciation. With **Include every item behind each line** ticked (the default), it's followed by an itemized list for each line (date, property, description, vendor or how paid, amount) with totals that match the summary. Losses are shown in parentheses.

PDFs are made with [QuestPDF](https://www.questpdf.com) under its free Community license (individuals and businesses with under $1M in annual revenue).

## Vendors

Add and edit vendors at the top of the **Vendors** page; a second vendor with the same name is refused. To fix a vendor entered twice, click **Merge into…** on the duplicate and choose the one to keep: its expenses, recurring expenses, maintenance requests, import rules, texts, and emails move over, the kept vendor gets any phone, email, trade, or notes it was missing, and the duplicate is deleted. **Delete** removes a vendor after saying what uses it; those items are kept with no vendor.

## Notes

- On the report, click a line name (e.g. Cleaning and maintenance, or Rents received) to list every item behind it, or click one property's amount to see just that property's items, including its share of shared expenses. Each list ends with a total that matches the report.
- The report's income is rent actually received in the year (by date received). Scheduled rent from leases, in whole months, is shown beside it for comparison.
- Back up `propertymanager.db` and `App_Data/documents` (which also holds email attachments). Both are excluded from git.
