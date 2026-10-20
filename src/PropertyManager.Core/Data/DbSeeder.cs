using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Data;

/// <summary>Seeds the owner's current portfolio into an empty database.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(PropertyManagerDbContext db, CancellationToken ct = default)
    {
        if (await db.Properties.AnyAsync(ct))
        {
            return;
        }

        db.Properties.AddRange(
            new Property
            {
                Name = "2228 1st St NW",
                Street = "2228 1st St NW",
                Units =
                [
                    new Unit { Label = "A", MarketRent = 4400m },
                    new Unit { Label = "B", MarketRent = 4500m },
                ],
            },
            new Property
            {
                Name = "1354 Taylor St NW",
                Street = "1354 Taylor St NW",
                Units =
                [
                    new Unit { Label = "A", MarketRent = 4500m },
                    new Unit { Label = "B", MarketRent = 1000m },
                ],
            });

        await db.SaveChangesAsync(ct);
    }
}
