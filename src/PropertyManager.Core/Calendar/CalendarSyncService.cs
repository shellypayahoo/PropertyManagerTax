using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Calendar;

public record CalendarSyncSummary(int Added, int Updated, int Removed);

/// <summary>Copies events from the chosen calendars, from a year back to a year ahead.</summary>
public class CalendarSyncService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IMailSource mail, TimeProvider clock)
{
    public static readonly TimeSpan Back = TimeSpan.FromDays(365);
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(365);

    /// <summary>Makes the given calendars the synced ones; events from calendars no longer chosen are removed.</summary>
    public async Task SetCalendarsAsync(IEnumerable<CalendarInfo> calendars, CancellationToken ct = default)
    {
        var chosen = calendars.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.CalendarSources.ToListAsync(ct);
        db.CalendarSources.RemoveRange(existing.Where(c => chosen.All(x => x.Id != c.GraphId)));
        foreach (var calendar in chosen)
        {
            if (existing.FirstOrDefault(c => c.GraphId == calendar.Id) is { } found)
            {
                found.Name = calendar.Name;
            }
            else
            {
                db.CalendarSources.Add(new CalendarSource { GraphId = calendar.Id, Name = calendar.Name });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Refreshes events in the sync window. Events keep their ids across syncs (so links from maintenance requests
    /// stay), and ones cancelled or deleted in Outlook are removed. Older events outside the window are left as they are.
    /// </summary>
    public async Task<CalendarSyncSummary> SyncAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var (from, to) = (now - Back, now + Ahead);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        int added = 0, updated = 0, removed = 0;

        foreach (var calendar in await db.CalendarSources.ToListAsync(ct))
        {
            var items = (await mail.GetEventsAsync(calendar.GraphId, from, to, ct)).Where(e => !e.IsCancelled).ToList();
            var stored = await db.CalendarEvents.Where(e => e.CalendarSourceId == calendar.Id && e.StartsAt >= from && e.StartsAt <= to).ToListAsync(ct);
            var ids = items.Select(i => i.Id).ToList();
            var elsewhere = await db.CalendarEvents.Where(e => ids.Contains(e.GraphId) && e.CalendarSourceId != calendar.Id).ToDictionaryAsync(e => e.GraphId, ct);
            var byId = stored.ToDictionary(e => e.GraphId);

            foreach (var item in items)
            {
                var isNew = !byId.TryGetValue(item.Id, out var ev) && !elsewhere.TryGetValue(item.Id, out ev);
                ev ??= new CalendarEvent { GraphId = item.Id };
                var before = (ev.Subject, ev.Location, ev.Preview, ev.StartsAt, ev.EndsAt, ev.IsAllDay, ev.CalendarSourceId);
                ev.CalendarSource = calendar;
                ev.Subject = Truncate(item.Subject, 500);
                ev.Location = item.Location is null ? null : Truncate(item.Location, 500);
                ev.Preview = item.Preview is null ? null : Truncate(item.Preview, 1000);
                ev.StartsAt = item.Start;
                ev.EndsAt = item.End;
                ev.IsAllDay = item.IsAllDay;
                if (isNew)
                {
                    db.CalendarEvents.Add(ev);
                    byId[item.Id] = ev;
                    added++;
                }
                else if (before != (ev.Subject, ev.Location, ev.Preview, ev.StartsAt, ev.EndsAt, ev.IsAllDay, calendar.Id))
                {
                    updated++;
                }
            }

            var gone = stored.Where(e => !ids.Contains(e.GraphId)).ToList();
            db.CalendarEvents.RemoveRange(gone);
            removed += gone.Count;
            calendar.LastSyncedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return new CalendarSyncSummary(added, updated, removed);
    }

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
