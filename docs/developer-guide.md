# Developer guide

How the code is organized and the conventions to keep when changing it. For running and using the app, see the
[README](../README.md); for a picture of how the pieces connect, see [architecture.md](architecture.md).

## The stack

- **.NET 10**, **Blazor Server** (interactive server rendering: pages run on the server and update the browser over a live
  connection). C# throughout.
- **SQLite** through **Entity Framework Core**. The database file is `src/PropertyManager.Web/propertymanager.db`.
- **Microsoft.Extensions.AI** for the AI features, with adapters for Azure AI Foundry / OpenAI (Responses API), Anthropic,
  Claude on Foundry, and Ollama.
- **Microsoft Graph** (MSAL device-code sign-in) for Outlook.com mail and calendars.
- **QuestPDF** for the profit and loss PDF, **PdfPig** for reading PDF text.
- **xUnit** tests.

## Layout

```
src/PropertyManager.Core/      Everything that isn't a web page: data, rules, reports, sync, AI
  Entities/                    The database tables as classes (Property, Unit, Lease, Expense, ...)
  Data/                        PropertyManagerDbContext (table rules), DbSeeder, Migrations/
  Services/                    Rent schedule, reports, receipts, documents, backups, vendors, ...
  Reports/                     P&L PDF, operating reports, 1099s, depreciation, accountant packet, CSV
  Assistant/                   The Ask page's AI: AssistantService, AssistantTools (read-only lookups),
                               AssistantActions (propose_* changes), ExpenseFinder, ModelCatalog, ChatClientFactory
  Import/                      Bank/Zelle and mortgage CSV import, rules
  Email/  Calendar/            Microsoft Graph sign-in, mail and calendar sync, adding appointments
  Messaging/                   The Mac's Messages database reader and text import, image conversion, retention
  Documents/                   PDF/Word text, Mac text recognition (OCR)
  Storage/                     Where uploaded files live (IDocumentStorage, LocalDocumentStorage), safe file types
  Configuration/               Options classes for settings, business time zone, access policy, relative times

src/PropertyManager.Web/       The web app
  Program.cs                   Startup: services, settings sources, migrations, access check, file endpoints
  Components/Pages/            One .razor file per page (route at the top)
  Components/Shared/           Pieces used by several pages, and report tabs
  Components/Layout/           Menu and page frame
  Agents/                      Background jobs: ExpenseAgent, MailSyncAgent, BackupAgent (on ScheduledAgent)
  Configuration/               Settings saved in the database, startup configuration check
  wwwroot/                     CSS, the upload helper script

tests/PropertyManager.Tests/   xUnit tests, mostly against Core with an in-memory SQLite database
packaging/  scripts/           Windows package, Mac auto-start script
docs/                          This guide, the architecture diagram, an example settings file
```

Every class in Core has a one-line `<summary>` saying what it's for, and every page starts with an `@* ... *@`
comment saying what the page does. Keep both up to date when changing them.

## Running, testing, building

```sh
dotnet build                                   # whole solution
dotnet test                                    # all tests (about 30 seconds)
dotnet run --project src/PropertyManager.Web   # the app on http://localhost:5220
```

The Mac runs the app in the background with `scripts/mac/autostart.sh` (a LaunchAgent). After pulling changes there,
run `scripts/mac/autostart.sh restart`.

## Changing the database

1. Change the entity in `Core/Entities/` and, if needed, its rules in `PropertyManagerDbContext.OnModelCreating`.
2. Add a migration:
   ```sh
   dotnet ef migrations add <Name> --project src/PropertyManager.Core --startup-project src/PropertyManager.Web --output-dir Data/Migrations
   ```
3. Migrations are applied automatically when the app starts. Read the generated migration: it must only add or change
   what you meant to (the owner's data lives in this database).

SQLite things to know:
- **Decimals** are stored as text, so SQLite can't sum, compare, or sort them. Load the rows, then total them in C#.
- **DateTimeOffset** columns that need sorting or comparing use `DateTimeOffsetToBinaryConverter` (see the existing
  ones in `OnModelCreating`).
- Enums are stored as their names (`HasConversion<string>()`).

## Conventions

These come from how the owner uses the app; keep them.

- **Never put secrets in the repository.** API keys and the email client id go in user secrets (Development) or
  `appsettings.Local.json` (git-ignored). `appsettings.json` holds only placeholders and non-secret defaults.
- **Nothing the AI suggests changes data without the owner's click.** The Ask page's `propose_*` tools
  (`AssistantActions`) only create a card; `Apply` runs the change. The expense checker (`ExpenseFinder`) only creates
  suggestions; `Approve` adds the expense. The assistant never deletes anything.
- **Documents marked "Keep out of Ask" are never sent to an AI model.** `AssistantTools` filters them out; tax returns
  and 1099s/W-9s start out marked.
- **The only change the app makes to the mailbox is adding calendar appointments.** Mail is read-only.
- **Times follow the business's time zone** (`Business:TimeZone`, Eastern for the DC rentals), applied process-wide at
  startup by `BusinessTimeZone.Apply`. Use `TimeProvider` and `ToLocalTime()`; appointments keep the zone they were made
  in (`EventTimes`). "Last synced" style times are shown as "25 min ago" (`RelativeTime`).
- **Who can open the app:** only the computer itself and Tailscale addresses, unless Settings allows the local
  network (`AccessPolicy`, checked first thing in `Program.cs`). The app has no sign-in.
- **Files people send can be anything.** Only pictures, video, audio, and PDFs are served inline (`SafeFiles`);
  everything else downloads. Stored files get random names (`LocalDocumentStorage`); the original name is only for display.
- **Leave the owner's setup alone** when adding features: new settings default to the current behaviour, and features
  that need setup can be turned off under `Features` in Settings.
- **Write for the owner.** Page text and messages are plain English, without technical terms, and say what to do next.

## How the main pieces fit

**Settings.** Read in order, later ones winning: `appsettings.json` → user secrets (Development only) → environment
variables → `appsettings.Local.json` → settings saved on the Settings page (the `AppSettings` table, via
`DatabaseSettingsProvider`). Options classes live in `Core/Configuration/AppOptions.cs` (and `AssistantOptions`,
`EmailOptions`); pages read them through `IOptionsMonitor<T>.CurrentValue` so changes apply without a restart.
`ConfigurationCheck` reports bad values at startup and on the Dashboard.

**Pages and the database.** Blazor Server circuits live a long time, so pages never hold a `DbContext`: they inject
`IDbContextFactory<PropertyManagerDbContext>` and create a short-lived one per operation (`await using var db = ...`).

**Uploads.** Large files are posted with a regular HTML form (`data-file-upload`, handled by `wwwroot/js/upload.js`)
to a minimal API endpoint in `Program.cs`, not over the live connection; this is more reliable in Safari and with
OneDrive/iCloud files.

**Background jobs.** `ScheduledAgent` runs work on an interval read from Settings (rechecked every minute) and on
demand. `MailSyncAgent` gets emails and appointments; `ExpenseAgent` imports texts and runs `ExpenseFinder`;
`BackupAgent` makes the daily backup. Pages subscribe to their `Changed` event to refresh.

**The Ask page.** `AssistantService.AskAsync` sends the system prompt, the recent conversation, and the question to
the chosen model with `AssistantTools` (read-only lookups) and `AssistantActions` (proposals) as tools.
`ChatClientFactory` builds the client for each provider type. The conversation is saved by `AskHistory`.

**Reports.** `ReportService` builds the profit and loss: rent received, expenses by Schedule E line (shared expenses
split by unit count), and calculated depreciation unless depreciation was entered by hand. `ProfitAndLossPdf`,
`OperatingReports` (cash flow, rent collection, vendors), `Form1099Report`, `Depreciation`, and `AccountantPacket`
build on the same data.

## Tests

- Tests live in `tests/PropertyManager.Tests`, one file per area (e.g. `DepreciationTests.cs`, `ExpenseFinderTests.cs`).
- Most use an in-memory SQLite connection, run the real migrations, and seed the sample properties with `DbSeeder`.
- External services are faked: a scripted `IChatClient` for AI, a fake `HttpMessageHandler` or `IMailSource` for
  Microsoft Graph, a fixed `TimeProvider` for dates.
- Add tests for any rule that affects money, taxes, or what's sent to an AI model. Run `dotnet test` before
  pushing; all tests should pass.
- Some things can only be checked on the owner's Mac (Messages import, text recognition, Keychain sign-in, the
  LaunchAgent). Say so when a change depends on them.

## Workflow

Work on a branch, push it, and describe the change in plain terms; the owner reviews and says when to merge to
`main`. After merging, the Mac needs `git pull` and `scripts/mac/autostart.sh restart`.
