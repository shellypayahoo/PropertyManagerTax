using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Data;

/// <summary>Creates the starting properties and units in an empty database.</summary>
public static class DbSeeder
{
    /// <summary>A sample portfolio of two duplexes, used by tests.</summary>
    public static readonly IReadOnlyList<PropertySetup> Sample =
    [
        new() { Name = "2228 1st St NW", Units = [new() { Label = "A", MarketRent = 4400m }, new() { Label = "B", MarketRent = 4500m }] },
        new() { Name = "1354 Taylor St NW", Units = [new() { Label = "A", MarketRent = 4500m }, new() { Label = "B", MarketRent = 1000m }] },
    ];

    public static Task SeedAsync(PropertyManagerDbContext db, CancellationToken ct = default) => SeedAsync(db, Sample, ct);

    /// <summary>Adds the given properties if the database has none yet; an existing portfolio is never changed.</summary>
    public static async Task SeedAsync(PropertyManagerDbContext db, IEnumerable<PropertySetup> properties, CancellationToken ct = default)
    {
        if (await db.Properties.AnyAsync(ct))
        {
            return;
        }

        foreach (var p in properties.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            var property = new Property
            {
                Name = p.Name.Trim(),
                Street = string.IsNullOrWhiteSpace(p.Street) ? p.Name.Trim() : p.Street.Trim(),
                PostalCode = p.PostalCode,
                Units = p.Units.Where(u => !string.IsNullOrWhiteSpace(u.Label)).Select(u => new Unit { Label = u.Label.Trim(), MarketRent = u.MarketRent }).ToList(),
            };
            if (!string.IsNullOrWhiteSpace(p.City))
            {
                property.City = p.City;
            }
            if (!string.IsNullOrWhiteSpace(p.State))
            {
                property.State = p.State;
            }
            db.Properties.Add(property);
        }
        await db.SaveChangesAsync(ct);
    }
}
