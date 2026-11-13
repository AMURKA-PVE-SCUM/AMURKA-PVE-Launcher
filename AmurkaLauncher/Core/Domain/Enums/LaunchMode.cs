namespace AmurkaLauncher.Core.Domain.Enums;

public enum LaunchMode
{
    Dx11,
    Dx12,
    Vulkan
}

public static class LaunchModeExtensions
{
    public static string ToKey(this LaunchMode mode) => mode switch
    {
        LaunchMode.Dx11 => "dx11",
        LaunchMode.Dx12 => "dx12",
        LaunchMode.Vulkan => "vulkan",
        _ => "dx11"
    };

    public static LaunchMode FromKey(string? key) => key?.ToLowerInvariant() switch
    {
        "dx12" => LaunchMode.Dx12,
        "vulkan" => LaunchMode.Vulkan,
        _ => LaunchMode.Dx11
    };
}
