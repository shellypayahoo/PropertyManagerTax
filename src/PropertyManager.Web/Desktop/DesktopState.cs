namespace PropertyManager.Web.Desktop;

/// <summary>How the installed app was started, for comparing with the current settings (a change to phone access needs a restart).</summary>
public sealed record DesktopState(bool PhoneAccess, string LogPath);
