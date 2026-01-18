using System.CommandLine;

namespace ModelingEvolution.Mjpeg.Cli.Commands;

/// <summary>
/// Defines the 'extract' subcommand for extracting JPEG frames from MJPEG recordings.
/// </summary>
public static class ExtractCommand
{
    public static readonly Argument<DirectoryInfo> InputPathArgument = new("input-path")
    {
        Description = "Path to MJPEG recording directory (contains index.json and frame files)"
    };

    public static readonly Option<DirectoryInfo> OutputDirOption = new("--output-dir")
    {
        Description = "Output directory for extracted frames (created if not exists)",
        Required = true
    };

    public static readonly Option<int?> HdrWindowOption = new("--hdr-window")
    {
        Description = "Number of frames to blend (2-10). Default: disabled (no HDR)"
    };

    public static readonly Option<string> HdrAlgorithmOption = new("--hdr-algorithm")
    {
        Description = "HDR blending mode: avg, weighted. Default: avg",
        DefaultValueFactory = _ => "avg"
    };

    public static Command Create(Func<ParseResult, int> handler)
    {
        var command = new Command("extract", "Extract JPEG frames from MJPEG recording")
        {
            InputPathArgument,
            OutputDirOption,
            HdrWindowOption,
            HdrAlgorithmOption
        };

        command.SetAction(handler);
        return command;
    }
}
