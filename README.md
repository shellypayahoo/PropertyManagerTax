# PropertyManagerTax

Property management code that generates tax reports and keeps track of expenses and income.

A Blazor (ASP.NET Core, .NET 10) app for managing rental properties: units, tenants, leases, expenses, maintenance requests, vendors, and an annual profit and loss report laid out by IRS Schedule E line.

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

## Notes

- Rent income on the report is the *scheduled* rent from leases, prorated by day. It is not the rent actually collected; payment tracking isn't built yet.
- Back up `propertymanager.db`. It is excluded from git.
