using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PropertyManager.Core.Assistant;

/// <summary>A deployment found on an Azure AI Foundry resource: its name and the model behind it.</summary>
public record FoundDeployment(string Name, string? Model);

/// <summary>Finds the deployments on a Foundry resource, and checks that a model name answers.</summary>
public class ModelCatalog(HttpClient http, Func<AssistantProviderOptions, IChatClient>? clientFactory = null)
{
    private readonly Func<AssistantProviderOptions, IChatClient> createClient = clientFactory ?? (p => ChatClientFactory.Create(p));

    /// <summary>
    /// Lists the resource's deployments with its endpoint and key, through the Azure OpenAI deployments listing that
    /// some resources still offer. Returns null when the resource doesn't offer it (the names can still be typed in).
    /// </summary>
    public async Task<IReadOnlyList<FoundDeployment>?> FindDeploymentsAsync(AssistantProviderOptions provider, CancellationToken ct = default)
    {
        if (provider.Type != AssistantProviderType.AzureFoundry || string.IsNullOrWhiteSpace(provider.Endpoint) || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            return null;
        }
        var host = ChatClientFactory.FoundryEndpoint(provider.Endpoint).GetLeftPart(UriPartial.Authority);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{host}/openai/deployments?api-version=2022-12-01");
        request.Headers.Add("api-key", provider.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return data.EnumerateArray()
                .Where(d => d.TryGetProperty("id", out _))
                .Where(d => !d.TryGetProperty("status", out var status) || status.GetString() is null or "succeeded")
                .Select(d => new FoundDeployment(d.GetProperty("id").GetString()!, d.TryGetProperty("model", out var m) ? m.GetString() : null))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Sends a one-word question to the model. Returns null when it answered, else what went wrong.</summary>
    public async Task<string?> TestAsync(AssistantProviderOptions provider, string model, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var client = createClient(model == provider.Model ? provider : provider.WithModel(model));
            var response = await client.GetResponseAsync("Reply with the single word OK.", cancellationToken: timeout.Token);
            return string.IsNullOrWhiteSpace(response.Text) ? "It answered with nothing." : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "It didn't answer within a minute.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message.Contains("404") || ex.Message.Contains("DeploymentNotFound", StringComparison.OrdinalIgnoreCase)
                ? $"There's no deployment named \"{model}\" on this resource. Check the name on the Foundry Deployments page."
                : ex.Message;
        }
    }
}
