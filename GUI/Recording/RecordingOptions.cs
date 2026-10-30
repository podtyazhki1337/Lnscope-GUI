namespace GUI.Recording;

public sealed class RecordingOptions
{
    public string OutputDirectory { get; set; } = "";

    /// <summary>File name template: {camera}, {timestamp}, {width}, {height} and {fps} are supported.</summary>
    public string FileNameTemplate { get; set; } = "{camera}_{timestamp}.mp4";

    /// <summary>Encoding arguments. The default is visually lossless CRF 18. For mathematically lossless encoding, use "-c:v libx264 -preset veryfast -qp 0".</summary>
    public string CodecArguments { get; set; } = "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p";

    /// <summary>Frame queue capacity. Overflow indicates that the recording pipeline cannot keep up.</summary>
    public int QueueCapacity { get; set; } = 90;
}
