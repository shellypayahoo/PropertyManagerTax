# Property Manager

A Blazor Server (.NET 10) app for managing a small portfolio of DC rental properties: leases, rent, expenses,
maintenance, documents, texts and emails, tax reports, and an AI assistant. It runs on the owner's Mac mini.

Read [docs/developer-guide.md](docs/developer-guide.md) before changing code: it covers the layout, how to run and
test, database changes, and the conventions to keep.

The essentials:

- `dotnet build`, `dotnet test`, `dotnet run --project src/PropertyManager.Web`.
- Database changes need an EF Core migration (command in the guide); read the generated migration before keeping it.
- Never commit secrets. API keys live in user secrets or the git-ignored `appsettings.Local.json`.
- Anything an AI suggests must wait for the owner's Apply or Approve; documents marked "Keep out of Ask" never go to a model.
- Use `TimeProvider` for dates; the app runs in the business's time zone (Eastern).
- Keep page text in plain English for a non-technical owner.
- Work on a branch and let the owner decide when to merge to `main`.
