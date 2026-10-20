using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Import;

namespace PropertyManager.Tests;

public sealed class CoTenantTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly DbContextOptions<PropertyManagerDbContext> options;

    public CoTenantTests()
    {
        connection.Open();
        options = new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options;
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public void UpgradingExistingDatabase_KeepsEachLeasesTenant()
    {
        using (var db = new PropertyManagerDbContext(options))
        {
            db.GetService<IMigrator>().Migrate("AddFinancialImport");
            db.Database.ExecuteSqlRaw("""
                INSERT INTO Properties (Id, Name, Street, City, State) VALUES (1, '1354 Taylor St NW', '1354 Taylor St NW', 'Washington', 'DC');
                INSERT INTO Units (Id, PropertyId, Label, MarketRent) VALUES (1, 1, 'A', '4500');
                INSERT INTO Tenants (Id, FirstName, LastName) VALUES (7, 'Jane', 'Sample');
                INSERT INTO Leases (Id, UnitId, TenantId, StartDate, MonthlyRent, SecurityDeposit) VALUES (3, 1, 7, '2024-01-01', '4500', '0');
                INSERT INTO RentPayments (Id, LeaseId, Date, Amount, Method) VALUES (1, 3, '2026-01-02', '4500', 'Zelle');
                """);
        }

        using (var db = new PropertyManagerDbContext(options))
        {
            db.Database.Migrate();
            var lease = db.Leases.Include(l => l.Tenants).Include(l => l.Payments).Single();
            Assert.Equal("Jane Sample", Assert.Single(lease.Tenants).FullName);
            Assert.Single(lease.Payments);
        }
    }

    [Fact]
    public void EitherCoTenantsZellePayment_IsMatchedToTheSharedLease()
    {
        var jane = new Tenant { Id = 1, FirstName = "Jane", LastName = "Sample" };
        var john = new Tenant { Id = 2, FirstName = "John", LastName = "Roommate" };
        var lease = new Lease { Id = 10, StartDate = new(2025, 1, 1), MonthlyRent = 4500m, Tenants = [jane, john] };
        var context = new ClassifierContext([], [lease], [], new Dictionary<int, Expense>(), [], []);
        var account = new BankAccount { Name = "Zelle", IsRentalOnly = false };

        var fromJohn = new ImportRow(new BankRow(2, new(2026, 3, 1), "Zelle payment from JOHN ROOMMATE Conf# 9", 2250m, "x"));
        var fromJane = new ImportRow(new BankRow(3, new(2026, 3, 1), "Zelle payment from JANE SAMPLE Conf# 8", 2250m, "y"));
        TransactionClassifier.Classify(fromJohn, account, context);
        TransactionClassifier.Classify(fromJane, account, context);

        Assert.Equal((TransactionKind.RentPayment, 10), (fromJohn.Kind, fromJohn.LeaseId));
        Assert.Equal((TransactionKind.RentPayment, 10), (fromJane.Kind, fromJane.LeaseId));
        Assert.Equal("Sender matches John Roommate, on the lease with Jane Sample", fromJohn.Reason);
    }

    [Fact]
    public void TenantNames_ListsEveryoneOnTheLease()
    {
        var lease = new Lease { Tenants = [new() { FirstName = "John", LastName = "Roommate" }, new() { FirstName = "Jane", LastName = "Sample" }] };

        Assert.Equal("John Roommate & Jane Sample", lease.TenantNames);
    }
}
