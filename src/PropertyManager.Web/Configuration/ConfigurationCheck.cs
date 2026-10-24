using Microsoft.Extensions.Configuration;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Email;

using PropertyManager.Core.Configuration;

namespace PropertyManager.Web.Configuration;

/// <summary>A setting that's wrong (the app can't use it) or worth knowing about.</summary>
public record ConfigurationProblem(bool IsError, string Message);

/// <summary>Checks the settings at startup so mistakes show up as plain messages instead of failures later.</summary>
public static class ConfigurationCheck
{
    public static IReadOnlyList<ConfigurationProblem> Run(IConfiguration configuration)
    {
        var problems = new List<ConfigurationProblem>();
        void Error(string message) => problems.Add(new(true, message));
        void Note(string message) => problems.Add(new(false, message));

        var features = Bind<FeatureOptions>(configuration, FeatureOptions.Section, Error) ?? new FeatureOptions();
        var business = Bind<BusinessOptions>(configuration, BusinessOptions.Section, Error) ?? new BusinessOptions();
        if (business.RentDueDay is < 1 or > 28)
        {
            Error($"Business:RentDueDay is {business.RentDueDay}; use a day from 1 to 28.");
        }
        if (business.LateAfterDays is < 0 or > 31)
        {
            Error($"Business:LateAfterDays is {business.LateAfterDays}; use 0 to 31.");
        }

        if (features.Assistant)
        {
            // Bind each provider separately so one bad entry names itself.
            var names = configuration.GetSection("Assistant:Providers").GetChildren().Select(c => c.Key).ToList();
            foreach (var name in names)
            {
                var section = configuration.GetSection($"Assistant:Providers:{name}");
                var type = section["Type"];
                if (!Enum.TryParse<AssistantProviderType>(type, ignoreCase: true, out _))
                {
                    Error($"Assistant provider \"{name}\" has Type \"{type}\"; use one of {string.Join(", ", Enum.GetNames<AssistantProviderType>())}.");
                }
            }
            var defaultProvider = configuration["Assistant:DefaultProvider"];
            if (!string.IsNullOrWhiteSpace(defaultProvider) && !names.Contains(defaultProvider))
            {
                Error($"Assistant:DefaultProvider is \"{defaultProvider}\", but there's no provider with that name under Assistant:Providers.");
            }
            if (names.Count == 0)
            {
                Note("The Ask page is on but no AI providers are listed under Assistant:Providers.");
            }
        }

        if ((features.Email || features.Calendar) && !(Bind<EmailOptions>(configuration, EmailOptions.Section, Error)?.IsConfigured ?? false))
        {
            Note("Email and calendar are on but Email:ClientId isn't set; the Email & calendar page explains the one-time setup.");
        }
        if (features.TextMessages && !OperatingSystem.IsMacOS())
        {
            Note("Text message import only works on a Mac; texts already imported still show. Set Features:TextMessages to false to hide the Messages page.");
        }

        var setup = Bind<SetupOptions>(configuration, SetupOptions.Section, Error);
        foreach (var p in setup?.Properties ?? [])
        {
            var duplicates = p.Units.GroupBy(u => u.Label.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                Error($"Setup property \"{p.Name}\" lists unit {string.Join(", ", duplicates)} more than once.");
            }
        }
        return problems;
    }

    private static T? Bind<T>(IConfiguration configuration, string section, Action<string> error) where T : class, new()
    {
        try
        {
            return configuration.GetSection(section).Get<T>() ?? new T();
        }
        catch (InvalidOperationException ex)
        {
            error($"The \"{section}\" settings couldn't be read: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }
}
