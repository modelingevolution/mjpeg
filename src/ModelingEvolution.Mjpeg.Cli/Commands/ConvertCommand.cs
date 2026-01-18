using System.CommandLine;

namespace ModelingEvolution.Mjpeg.Cli.Commands;

/// <summary>
/// Defines the 'convert' subcommand for converting MJPEG recordings to MP4.
/// </summary>
public static class ConvertCommand
{
    public static readonly Argument<DirectoryInfo> InputPathArgument = new("input-path")
    {
        Description = "Path to MJPEG recording directory (contains stream.mjpeg and optionally stream.json)"
    };

    public static readonly Option<FileInfo> OutputFileOption = new("--output")
    {
        Description = "Output MP4 file path",
        Required = true
    };

    public static readonly Option<int?> HdrWindowOption = new("--hdr-window")
    {
        Description = "Number of frames to blend for HDR (2-10). Default: disabled (no HDR)"
    };

    public static readonly Option<string> HdrAlgorithmOption = new("--hdr-algorithm")
    {
        Description = "HDR blending mode: avg, weighted. Default: avg",
        DefaultValueFactory = _ => "avg"
    };

    public static readonly Option<int> FpsOption = new("--fps")
    {
        Description = "Output video framerate. Default: 25",
        DefaultValueFactory = _ => 25
    };

    public static readonly Option<string> CodecOption = new("--codec")
    {
        Description = "Video codec fourcc (e.g., mp4v, avc1, X264). Default: mp4v",
        DefaultValueFactory = _ => "mp4v"
    };

    public static Command Create(Func<ParseResult, int> handler)
    {
        var command = new Command("convert", "Convert MJPEG recording to MP4 video")
        {
            InputPathArgument,
            OutputFileOption,
            HdrWindowOption,
            HdrAlgorithmOption,
            FpsOption,
            CodecOption
        };

        command.SetAction(handler);
        return command;
    }
}
