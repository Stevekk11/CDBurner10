namespace CDBurner10.Upscaler.Engine;

public enum UpscalerBackendKind
{
    MpvGlslShader,
    OnnxDirectMl
}

public sealed record UpscalerPreset(
    string Id,
    string DisplayName,
    string FilePrefix,
    string Family,
    string Architecture,
    string ResidualBlocksOrLayers,
    string Filters,
    string ParameterCount,
    string RecommendedUsage,
    string EstimatedRuntimePer10Min,
    UpscalerBackendKind Backend,
    IReadOnlyList<string> HqShaders,
    IReadOnlyList<string> FastShaders,
    string? OnnxModelFileName = null)
{
    public string BackendLabel => Backend switch
    {
        UpscalerBackendKind.MpvGlslShader => "libmpv + GLSL Shader",
        UpscalerBackendKind.OnnxDirectMl => "ONNX Runtime (DirectML / CPU)",
        _ => Backend.ToString()
    };

    public IReadOnlyList<string> GetShadersForTier(string tier) =>
        string.Equals(tier, "Fast", StringComparison.OrdinalIgnoreCase) && FastShaders.Count > 0
            ? FastShaders
            : HqShaders;
}

public static class UpscalerPresets
{
    public static IReadOnlyList<UpscalerPreset> All { get; } =
    [
        // Anime4K v4 Modes (HQ = GLSL_Windows_High-end, Fast = GLSL_Windows_Low-end)
        new(
            Id: "Anime4K_A",
            DisplayName: "Anime4K A (Restore + Upscale)",
            FilePrefix: "Anime4K_A",
            Family: "Anime4K v4",
            Architecture: "CNN",
            ResidualBlocksOrLayers: "4-stage chain",
            Filters: "Clamp + Restore VL + 2× VL/M",
            ParameterCount: "~450k",
            RecommendedUsage: "Balanced restoration & upscaling for 1080p anime / web sources",
            EstimatedRuntimePer10Min: "~8–14 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_VL.glsl",
                "Anime4K_Upscale_CNN_x2_VL.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl"
            ],
            FastShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl"
            ]),

        new(
            Id: "Anime4K_B",
            DisplayName: "Anime4K B (Soft Restore + Upscale)",
            FilePrefix: "Anime4K_B",
            Family: "Anime4K v4",
            Architecture: "CNN (Soft)",
            ResidualBlocksOrLayers: "4-stage chain",
            Filters: "Clamp + Restore Soft VL + 2× VL/M",
            ParameterCount: "~450k",
            RecommendedUsage: "Soft line-art reconstruction; lowers ringing/aliasing on 720p anime",
            EstimatedRuntimePer10Min: "~8–14 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_Soft_VL.glsl",
                "Anime4K_Upscale_CNN_x2_VL.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl"
            ],
            FastShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_Soft_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl"
            ]),

        new(
            Id: "Anime4K_C",
            DisplayName: "Anime4K C (Upscale + Denoise)",
            FilePrefix: "Anime4K_C",
            Family: "Anime4K v4",
            Architecture: "CNN (Denoise)",
            ResidualBlocksOrLayers: "3-stage chain",
            Filters: "Clamp + Upscale Denoise VL + 2× M",
            ParameterCount: "~400k",
            RecommendedUsage: "High perceptual quality with denoising for clean or compressed animation",
            EstimatedRuntimePer10Min: "~8–13 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Upscale_Denoise_CNN_x2_VL.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl"
            ],
            FastShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Upscale_Denoise_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl"
            ]),

        new(
            Id: "Anime4K_A_Plus",
            DisplayName: "Anime4K A+ (Dual Restore + Upscale)",
            FilePrefix: "Anime4K_A+",
            Family: "Anime4K v4",
            Architecture: "CNN (A+)",
            ResidualBlocksOrLayers: "5-stage chain",
            Filters: "Clamp + Restore VL/M + 2× VL/M",
            ParameterCount: "~500k",
            RecommendedUsage: "Maximum restoration strength for blurry, low-bitrate, or artifacted anime",
            EstimatedRuntimePer10Min: "~10–16 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_VL.glsl",
                "Anime4K_Upscale_CNN_x2_VL.glsl",
                "Anime4K_Restore_CNN_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl"
            ],
            FastShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K_Restore_CNN_S.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl"
            ]),

        new(
            Id: "Anime4K_B_Plus",
            DisplayName: "Anime4K B+ (Soft Dual Restore + Upscale)",
            FilePrefix: "Anime4K_B+",
            Family: "Anime4K v4",
            Architecture: "CNN (B+)",
            ResidualBlocksOrLayers: "5-stage chain",
            Filters: "Clamp + Restore Soft VL/M + 2× VL/M",
            ParameterCount: "~500k",
            RecommendedUsage: "Strong soft restoration for DVD / SD / 720p anime with halo reduction",
            EstimatedRuntimePer10Min: "~10–16 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_Soft_VL.glsl",
                "Anime4K_Upscale_CNN_x2_VL.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Restore_CNN_Soft_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl"
            ],
            FastShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_Soft_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Restore_CNN_Soft_S.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl"
            ]),

        new(
            Id: "Anime4K_C_Plus",
            DisplayName: "Anime4K C+ (Denoise + Restore + Upscale)",
            FilePrefix: "Anime4K_C+",
            Family: "Anime4K v4",
            Architecture: "CNN (C+)",
            ResidualBlocksOrLayers: "4-stage chain",
            Filters: "Clamp + Denoise VL + Restore M + 2× M",
            ParameterCount: "~480k",
            RecommendedUsage: "High-detail upscaling with denoising & secondary line refinement",
            EstimatedRuntimePer10Min: "~9–15 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Upscale_Denoise_CNN_x2_VL.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Restore_CNN_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl"
            ],
            FastShaders:
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Upscale_Denoise_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Restore_CNN_S.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl"
            ]),

        // ArtCNN v1.6.2 Models (R16F96, R8F64 via ONNX; C4F32, C4F16 via libmpv GLSL)
        new(
            Id: "ArtCNN_R16F96",
            DisplayName: "ArtCNN R16F96",
            FilePrefix: "ArtCNN_R16F96",
            Family: "ArtCNN",
            Architecture: "R",
            ResidualBlocksOrLayers: "16",
            Filters: "96",
            ParameterCount: "~4m",
            RecommendedUsage: "Highest-quality reconstruction for non real-time tasks",
            EstimatedRuntimePer10Min: "~2–4 hours",
            Backend: UpscalerBackendKind.OnnxDirectMl,
            HqShaders: [],
            FastShaders: [],
            OnnxModelFileName: "ArtCNN_R16F96.onnx"),

        new(
            Id: "ArtCNN_R8F64",
            DisplayName: "ArtCNN R8F64",
            FilePrefix: "ArtCNN_R8F64",
            Family: "ArtCNN",
            Architecture: "R",
            ResidualBlocksOrLayers: "8",
            Filters: "64",
            ParameterCount: "~926k",
            RecommendedUsage: "Balanced option for non real-time tasks",
            EstimatedRuntimePer10Min: "~45–90 min",
            Backend: UpscalerBackendKind.OnnxDirectMl,
            HqShaders: [],
            FastShaders: [],
            OnnxModelFileName: "ArtCNN_R8F64.onnx"),

        new(
            Id: "ArtCNN_C4F32",
            DisplayName: "ArtCNN C4F32",
            FilePrefix: "ArtCNN_C4F32",
            Family: "ArtCNN",
            Architecture: "C",
            ResidualBlocksOrLayers: "4",
            Filters: "32",
            ParameterCount: "~48k",
            RecommendedUsage: "Real-time tasks if hardware allows",
            EstimatedRuntimePer10Min: "~14–20 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders: ["ArtCNN_C4F32.glsl"],
            FastShaders: ["ArtCNN_C4F32.glsl"]),

        new(
            Id: "ArtCNN_C4F16",
            DisplayName: "ArtCNN C4F16",
            FilePrefix: "ArtCNN_C4F16",
            Family: "ArtCNN",
            Architecture: "C",
            ResidualBlocksOrLayers: "4",
            Filters: "16",
            ParameterCount: "~12k",
            RecommendedUsage: "Lightweight option for real-time tasks",
            EstimatedRuntimePer10Min: "~8–12 min",
            Backend: UpscalerBackendKind.MpvGlslShader,
            HqShaders: ["ArtCNN_C4F16.glsl"],
            FastShaders: ["ArtCNN_C4F16.glsl"])
    ];

    public static UpscalerPreset FindById(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var found = All.FirstOrDefault(p =>
                p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                p.FilePrefix.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }

        return All[0];
    }

    public static string ResolveShaderPath(string fileName)
    {
        string baseShader = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", fileName);
        if (File.Exists(baseShader)) return baseShader;

        // Fallback to project source tree during dev runs
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            string candidate = Path.Combine(dir, "src", "CDBurner10.Upscaler", "Assets", "Shaders", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Shader file not found: {fileName}", baseShader);
    }

    public static string ResolveModelPath(string fileName)
    {
        string baseModel = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", fileName);
        if (File.Exists(baseModel)) return baseModel;

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            string candidate = Path.Combine(dir, "src", "CDBurner10.Upscaler", "Assets", "Models", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"ONNX model file not found: {fileName}", baseModel);
    }

    public static string BuildOutputFilePath(string sourceVideoPath, UpscalerPreset preset, string outputFolder,
        string outputFormat)
    {
        string baseName = Path.GetFileNameWithoutExtension(sourceVideoPath);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "video";
        string ext = outputFormat.Trim().TrimStart('.').ToLowerInvariant();
        if (ext is not ("mp4" or "mkv")) ext = "mp4";
        string fileName = $"{preset.FilePrefix}_{baseName}.{ext}";
        return Path.Combine(outputFolder, fileName);
    }
}