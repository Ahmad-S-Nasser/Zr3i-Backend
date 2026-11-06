namespace Zr3i.Kernel.Chips;

/// <summary>
/// Where picture files go when <c>Imagery:ChipRoot</c> is not set: a folder under the machine's temp area. The web app and the worker are separate
/// programs, so they must resolve the same folder; this default is the same for both on one machine. Set the setting for anything shared.
/// </summary>
public static class ChipRoot
{
    public static string From(string? configured) => string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "zr3i-chips") : configured;
}
