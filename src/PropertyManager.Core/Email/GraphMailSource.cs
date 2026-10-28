using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PropertyManager.Core.Email;

/// <summary>
/// Reads mail folders, messages, attachments, and calendars with the Microsoft Graph REST API, and adds calendar events.
/// Nothing else in the mailbox is changed.
/// </summary>
public class GraphMailSource(HttpClient http, IMailTokenProvider tokens) : IMailSource
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/me/";
    private const string MessageFields = "subject,from,toRecipients,ccRecipients,sentDateTime,receivedDateTime,body,hasAttachments,conversationId,isDraft";

    public async Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken ct = default)
    {
        var folders = new List<MailFolderInfo>();
        await AddFoldersAsync(BaseUrl + "mailFolders?$top=250&$select=id,displayName,childFolderCount,totalItemCount", "", folders, ct);
        return folders;
    }

    private async Task AddFoldersAsync(string url, string parentPath, List<MailFolderInfo> folders, CancellationToken ct)
    {
        await foreach (var folder in PagesAsync(url, ct))
        {
            var id = folder.GetProperty("id").GetString()!;
            var path = parentPath.Length == 0 ? folder.GetProperty("displayName").GetString()! : $"{parentPath} / {folder.GetProperty("displayName").GetString()}";
            folders.Add(new MailFolderInfo(id, path, folder.TryGetProperty("totalItemCount", out var count) ? count.GetInt32() : 0));
            if (folder.TryGetProperty("childFolderCount", out var children) && children.GetInt32() > 0)
            {
                await AddFoldersAsync($"{BaseUrl}mailFolders/{Uri.EscapeDataString(id)}/childFolders?$top=250&$select=id,displayName,childFolderCount,totalItemCount", path, folders, ct);
            }
        }
    }

    public async Task<MailChanges> GetChangesAsync(string folderId, string? deltaLink, CancellationToken ct = default)
    {
        var url = deltaLink ?? $"{BaseUrl}mailFolders/{Uri.EscapeDataString(folderId)}/messages/delta?$select={MessageFields}";
        var changed = new List<MailItem>();
        var removed = new List<string>();
        while (true)
        {
            using var page = await GetJsonAsync(url, ct, isDelta: true);
            foreach (var item in page.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                if (item.TryGetProperty("@removed", out _))
                {
                    removed.Add(id);
                }
                else
                {
                    changed.Add(ToMailItem(id, item));
                }
            }
            if (page.RootElement.TryGetProperty("@odata.nextLink", out var next))
            {
                if (next.GetString() == url)
                {
                    throw new HttpRequestException("Microsoft Graph returned the same page twice; try syncing again later.");
                }
                url = next.GetString()!;
            }
            else
            {
                return new MailChanges(changed, removed, page.RootElement.GetProperty("@odata.deltaLink").GetString()!);
            }
        }
    }

    public async Task<IReadOnlyList<MailAttachmentInfo>> GetAttachmentsAsync(string messageId, CancellationToken ct = default)
    {
        var attachments = new List<MailAttachmentInfo>();
        await foreach (var a in PagesAsync($"{BaseUrl}messages/{Uri.EscapeDataString(messageId)}/attachments?$select=id,name,contentType,size,isInline", ct))
        {
            // Only file attachments have downloadable content; skip attached emails and calendar items.
            if (a.TryGetProperty("@odata.type", out var type) && type.GetString() != "#microsoft.graph.fileAttachment")
            {
                continue;
            }
            attachments.Add(new MailAttachmentInfo(
                a.GetProperty("id").GetString()!,
                a.TryGetProperty("name", out var name) ? name.GetString() ?? "attachment" : "attachment",
                a.TryGetProperty("contentType", out var contentType) ? contentType.GetString() ?? "" : "",
                a.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                a.TryGetProperty("isInline", out var inline) && inline.GetBoolean()));
        }
        return attachments;
    }

    public async Task<byte[]> DownloadAttachmentAsync(string messageId, string attachmentId, CancellationToken ct = default)
    {
        using var request = await RequestAsync($"{BaseUrl}messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}/$value", ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken ct = default)
    {
        var calendars = new List<CalendarInfo>();
        await foreach (var c in PagesAsync(BaseUrl + "calendars?$top=100&$select=id,name", ct))
        {
            calendars.Add(new CalendarInfo(c.GetProperty("id").GetString()!, c.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""));
        }
        return calendars;
    }

    public async Task<IReadOnlyList<CalendarItem>> GetEventsAsync(string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}calendars/{Uri.EscapeDataString(calendarId)}/calendarView"
            + $"?startDateTime={from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}&endDateTime={to.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"
            + "&$top=100&$select=subject,location,bodyPreview,start,end,isAllDay,isCancelled";
        var events = new List<CalendarItem>();
        await foreach (var e in PagesAsync(url, ct))
        {
            events.Add(ToCalendarItem(e));
        }
        return events;
    }

    public async Task<CalendarItem> CreateEventAsync(string calendarId, NewCalendarItem item, CancellationToken ct = default)
    {
        static object Time(DateTime t, string zone) => new { dateTime = t.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), timeZone = zone };
        var body = new Dictionary<string, object?>
        {
            ["subject"] = item.Subject,
            ["body"] = new { contentType = "text", content = item.Notes ?? "" },
            ["start"] = Time(item.Start, item.TimeZone),
            ["end"] = Time(item.End, item.TimeZone),
            ["isAllDay"] = item.IsAllDay,
            ["isReminderOn"] = item.ReminderMinutes is not null,
            ["showAs"] = item.IsAllDay ? "free" : "busy",
        };
        if (item.ReminderMinutes is { } minutes)
        {
            body["reminderMinutesBeforeStart"] = minutes;
        }
        if (!string.IsNullOrWhiteSpace(item.Location))
        {
            body["location"] = new { displayName = item.Location };
        }

        var url = $"{BaseUrl}calendars/{Uri.EscapeDataString(calendarId)}/events";
        using var request = await RequestAsync(url, ct, HttpMethod.Post, write: true);
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        using var created = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return ToCalendarItem(created.RootElement);
    }

    private static CalendarItem ToCalendarItem(JsonElement e)
    {
        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        // With the outlook.timezone="UTC" preference, times come back as UTC without an offset.
        static DateTimeOffset Time(JsonElement e, string name) =>
            new(DateTime.SpecifyKind(DateTime.Parse(Text(e.GetProperty(name), "dateTime")!, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc));

        return new CalendarItem(
            e.GetProperty("id").GetString()!,
            Text(e, "subject") ?? "",
            e.TryGetProperty("location", out var location) ? Text(location, "displayName") is { Length: > 0 } place ? place : null : null,
            Text(e, "bodyPreview") is { Length: > 0 } preview ? preview : null,
            Time(e, "start"),
            Time(e, "end"),
            e.TryGetProperty("isAllDay", out var allDay) && allDay.ValueKind == JsonValueKind.True,
            e.TryGetProperty("isCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True);
    }

    private static MailItem ToMailItem(string id, JsonElement item)
    {
        static MailAddress? Address(JsonElement recipient) =>
            recipient.TryGetProperty("emailAddress", out var e) && e.TryGetProperty("address", out var a) && a.GetString() is { Length: > 0 } address
                ? new MailAddress(address, e.TryGetProperty("name", out var n) ? n.GetString() : null)
                : null;
        static IReadOnlyList<MailAddress> Addresses(JsonElement item, string name) =>
            item.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(Address).OfType<MailAddress>().ToList()
                : [];
        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var sent = Text(item, "sentDateTime") ?? Text(item, "receivedDateTime");
        var body = item.TryGetProperty("body", out var b) ? Text(b, "content") ?? "" : "";
        return new MailItem(
            id,
            Text(item, "conversationId"),
            Text(item, "subject") ?? "",
            item.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object ? Address(from) : null,
            Addresses(item, "toRecipients"),
            Addresses(item, "ccRecipients"),
            sent is null ? DateTimeOffset.MinValue : DateTimeOffset.Parse(sent, System.Globalization.CultureInfo.InvariantCulture),
            body,
            item.TryGetProperty("hasAttachments", out var has) && has.ValueKind == JsonValueKind.True,
            item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True);
    }

    private async IAsyncEnumerable<JsonElement> PagesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (string? next = url; next is not null;)
        {
            using var page = await GetJsonAsync(next, ct);
            foreach (var item in page.RootElement.GetProperty("value").EnumerateArray())
            {
                yield return item.Clone();
            }
            next = page.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool isDelta = false)
    {
        using var request = await RequestAsync(url, ct);
        // Plain-text bodies instead of HTML, event times in UTC, 50 messages per page.
        request.Headers.Add("Prefer", ["outlook.body-content-type=\"text\"", "outlook.timezone=\"UTC\"", "odata.maxpagesize=50"]);
        using var response = await http.SendAsync(request, ct);
        if (isDelta && response.StatusCode == HttpStatusCode.Gone)
        {
            throw new MailDeltaExpiredException("The saved sync position has expired.");
        }
        await EnsureSuccessAsync(response, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<HttpRequestMessage> RequestAsync(string url, CancellationToken ct, HttpMethod? method = null, bool write = false)
    {
        if (!url.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
        {
            // Links come back from Graph; never send the token anywhere else.
            throw new InvalidOperationException($"Unexpected Microsoft Graph link: {url}");
        }
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        var token = write ? await tokens.GetCalendarWriteTokenAsync(ct) : await tokens.GetAccessTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var message = "";
        try
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            message = error.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        throw new HttpRequestException($"Microsoft Graph returned {(int)response.StatusCode} {response.ReasonPhrase}. {message}".Trim(), null, response.StatusCode);
    }
}
