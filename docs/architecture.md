# Architecture

What runs where, where data is stored, and which outside services the app uses.

```mermaid
flowchart LR
    subgraph Devices["Devices"]
        Browser["Browser on the computer<br/>Safari · Edge · Chrome"]
        Phone["Phone<br/>same Wi-Fi or Tailscale"]
    end

    subgraph Host["Owner's computer · Mac (dotnet run) or Windows (packaged, .NET included)"]
        subgraph Web["PropertyManager.Web · Blazor Server"]
            Pages["Pages<br/>Dashboard · Properties · Tenants · Leases · Rent · Expenses<br/>Maintenance · Vendors · Import · Messages · Email & calendar<br/>Ask · Reports · Documents · Settings · first-run Setup"]
            Endpoints["File endpoints<br/>uploads: leases · receipts · business documents · house photos<br/>downloads: documents · attachments · receipts · photos · P&L PDF"]
            Config["Settings<br/>Features · Business · Setup · Assistant · Email<br/>DatabaseSettingsProvider · SettingsService · ConfigurationCheck"]
        end

        subgraph Core["PropertyManager.Core · class library"]
            Money["Money & reports<br/>RentSchedule · ReportService · OperatingReports<br/>RecurringExpense · ExpenseSplit · VendorService · ReceiptService<br/>BusinessDocumentService · ProfitAndLossPdf · QuestPDF"]
            Import["Bank & mortgage import<br/>BankCsvReader · TransactionClassifier · rules<br/>Mortgage CSV · Lakeview PDF · PdfPig"]
            Messaging["Texts · Mac only<br/>MessagesDatabaseReader · AddressBookReader<br/>TextMessageImportService · attachments<br/>MessageRetentionService · SipsImageConverter"]
            EmailCal["Email & calendar<br/>EmailSignIn · MSAL · GraphMailSource<br/>EmailSyncService · CalendarSyncService · EventMatcher"]
            Docs["Documents<br/>DocumentTextExtractor<br/>PDF and Word text"]
            Photos["House photos<br/>PropertyPhotoService"]
            Assistant["Ask assistant<br/>AssistantService · AssistantTools<br/>ChatClientFactory · Microsoft.Extensions.AI"]
        end

        subgraph Storage["Owner's data · never in git"]
            Db[("propertymanager.db · SQLite via EF Core<br/>records · texts · emails · document text<br/>settings chosen in the app")]
            Files[("Documents folder<br/>leases · receipts · business documents<br/>attachments · previews · house photos")]
            Secrets[["Keys & sign-in<br/>user secrets or appsettings.Local.json<br/>Keychain / Windows-encrypted email sign-in"]]
            FileConfig[["appsettings.json<br/>defaults · no secrets"]]
        end

        subgraph MacSources["macOS data · read-only copies"]
            ChatDb[("~/Library/Messages<br/>chat.db + Attachments")]
            Contacts[("Contacts<br/>AddressBook")]
            Sips["sips<br/>HEIC to JPEG"]
        end
    end

    subgraph Cloud["Outside services"]
        Graph["Microsoft Graph<br/>Outlook.com mail & calendar<br/>Mail.Read · Calendars.Read<br/>Calendars.ReadWrite (add appointments)"]
        Entra["Microsoft sign-in<br/>app registration"]
        AI["AI provider, as configured<br/>Azure AI Foundry · OpenAI · Anthropic<br/>Claude on Foundry · Ollama (local)"]
        Web2["Websites<br/>e.g. the LLC's site, for house photos"]
        GitHub["GitHub<br/>code only"]
    end

    BankFiles[/"Bank CSVs · Rocket CSV<br/>Lakeview PDF · lease PDFs"/]

    Browser --> Pages
    Phone --> Pages
    Browser --> Endpoints
    Pages --> Money & Import & Messaging & EmailCal & Photos & Assistant
    Pages --> Config
    Config --> Db
    Config --> FileConfig
    Config -.-> Secrets
    Endpoints --> Files
    Endpoints --> Money
    Endpoints --> Photos
    BankFiles --> Import

    Money --> Db
    Money --> Files
    Money --> Docs
    Import --> Db
    Import --> Docs
    Messaging --> Db
    Messaging --> Files
    Messaging --> Docs
    Messaging --> ChatDb
    Messaging --> Contacts
    Messaging --> Sips
    EmailCal --> Db
    EmailCal --> Files
    EmailCal --> Docs
    EmailCal --> Graph
    EmailCal --> Entra
    EmailCal -.-> Secrets
    Photos --> Files
    Photos --> Db
    Photos --> Web2
    Photos --> Sips
    Assistant --> Db
    Assistant --> Files
    Assistant -.-> Secrets
    Assistant -->|"question + looked-up records,<br/>texts, emails, documents, photos"| AI
    Db ~~~ GitHub
```

Notes:

- **Features** in the settings turn parts off (texts, email, calendar, the assistant, receipts, photos); a part that's off isn't shown, synced, or offered to the assistant.
- **Settings precedence**, lowest to highest: `appsettings.json` → user secrets → environment variables → `appsettings.Local.json` → choices saved on the Settings page.
- **Mac vs Windows:** text import and HEIC conversion use macOS (Messages database, `sips`); everything else is the same. The Windows package keeps the owner's data in `Documents\Property Manager`.
- **Only the AI provider** receives the owner's data, and only what's looked up for each question. Microsoft Graph is read-only.
