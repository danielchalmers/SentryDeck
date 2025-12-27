namespace SentryReplay;

/// <summary>
/// Represents GPU capabilities for hardware-accelerated video processing.
/// </summary>
public sealed class GpuCapabilities
{
    public bool HasCuda { get; set; }
    public bool HasD3D11VA { get; set; }
    public bool HasDXVA2 { get; set; }
    public bool HasQSV { get; set; }
    public bool HasVulkan { get; set; }
    public bool HasNvenc { get; set; }
    public bool HasAmf { get; set; }
    public bool HasQsvEnc { get; set; }

    public bool HasAnyGpuDecoding => HasCuda || HasD3D11VA || HasDXVA2 || HasQSV || HasVulkan;
    public bool HasAnyGpuEncoding => HasNvenc || HasAmf || HasQsvEnc;

    public string BestDecoder
    {
        get
        {
            if (HasD3D11VA)
                return "d3d11va";
            if (HasCuda)
                return "cuda";
            if (HasDXVA2)
                return "dxva2";
            if (HasQSV)
                return "qsv";
            return "auto";
        }
    }

    public string BestEncoder
    {
        get
        {
            if (HasNvenc)
                return "h264_nvenc";
            if (HasAmf)
                return "h264_amf";
            if (HasQsvEnc)
                return "h264_qsv";
            return "libx264";
        }
    }
}
