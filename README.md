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

- `src/PropertyManager.Core`: entities, `PropertyManagerDbContext`, migrations, seed data, and report services
- `src/PropertyManager.Web`: Blazor Server UI (`Components/Pages`)
- `tests/PropertyManager.Tests`: xUnit tests for rent proration, the P&L report, and seeding

## Leases with more than one tenant

A lease can have several tenants (roommates, a couple, a co-signer) who share one rent. Check everyone when creating the lease, or add and remove people later on the lease's page. Rent payments from any of them, including Zelle payments matched during an import, count toward the shared lease.

## Lease documents

Open a lease from the **Leases** page to edit its terms and upload the signed lease, addenda, or inspection reports (PDF, Word, JPG, PNG, HEIC; up to 25 MB each).

The database stores only each file's name, size, and upload time. The files themselves are saved under `src/PropertyManager.Web/App_Data/documents` (set `DocumentStorage:LocalPath` in `appsettings.json` to use another folder, such as one in iCloud Drive or OneDrive). File access goes through `IDocumentStorage`, so moving to Azure Blob Storage later means adding one class and changing one line in `Program.cs`.

## Importing financial information

**Bank and Zelle CSV files** (**Import** page): download transactions as CSV from your bank's website and upload them. Common layouts (Chase, Bank of America, Capital One, Wells Fargo, and others) are detected automatically; otherwise you pick the date, description, and amount columns once per account.

Every row is reviewed before it is saved, and each is recorded as one of:

- **Rent received**: deposits whose description contains a tenant's first and last name (as Zelle descriptions do) are matched to that tenant's lease.
- **Expense**: assigned to a property (and optionally a unit) and a Schedule E category.
- **Transfer**: money moved between your own accounts, such as Zelle rent swept into the business account. Skipped, so rent isn't counted twice.
- **Ignore**: anything unrelated to the rentals.

Use **Remember** on a row to save a rule (for example "PEPCO" → Utilities, 1354 Taylor St NW) that fills in future imports. Rows already imported into an account are skipped, so overlapping files are safe. Rows that look like an expense or payment you already entered by hand are suggested as ignored.

**Vendor text messages** (**Expenses → Add from a text message**): paste a text such as "it's $180 for the lawn and $40 for the hedges" to create one expense per amount, with the date, vendor, property, category, and how it was paid (cash, Zelle, …). The original text is kept in the expense notes.

**Cash or check rent**: use **Mark paid** on the Rent page or a lease's page.

## Tracking rent

The **Rent** page lists every lease for a month with its rent, what's been paid, and a status (Paid, short, Not paid). **Mark paid** records the payment toward that month; change the amount for a partial payment. Each payment records both the date it was received and the month it's for, so October rent paid on November 2 still counts for October.

To fix a payment, click **Edit** next to it to change the date, amount, method, or the month it counts for, or to delete it. Before a payment is saved, the app warns if it looks like a double entry (the month is already paid, or the same amount on the same date is already recorded). Months that look paid twice are flagged **Paid twice?** and listed at the top of the Rent page and the lease's page. Imports also skip Zelle rent for a month you already marked paid by hand.

A lease's page shows the same thing month by month for a year. Rent isn't prorated: the full monthly rent is due for every month a lease covers.

## Notes

- The report's income is rent actually received in the year (by date received). Scheduled rent from leases, in whole months, is shown beside it for comparison.
- Back up `propertymanager.db` and `App_Data/documents`. Both are excluded from git.
