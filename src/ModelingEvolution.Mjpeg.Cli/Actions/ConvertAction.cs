using System.Buffers;
using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using ModelingEvolution.Mjpeg.Cli.Commands;

namespace ModelingEvolution.Mjpeg.Cli.Actions;

/// <summary>
/// Executes the 'convert' command - converts MJPEG recording to MP4.
/// </summary>
public static class ConvertAction
{
    private const int DefaultFps = 25;
    private const int HdrJpegQuality = 100; // Lossless intermediate for HDR

    public static int Execute(ParseResult parseResult)
    {
        var inputPath = parseResult.GetValue(ConvertCommand.InputPathArgument)!;
        var outputFile = parseResult.GetValue(ConvertCommand.OutputFileOption)!;
        var hdrWindow = parseResult.GetValue(ConvertCommand.HdrWindowOption);
        var hdrAlgorithm = parseResult.GetValue(ConvertCommand.HdrAlgorithmOption) ?? "avg";
        var fps = parseResult.GetValue(ConvertCommand.FpsOption);
        var codec = parseResult.GetValue(ConvertCommand.CodecOption) ?? "mp4v";

        try
        {
            return ExecuteCoreAsync(inputPath, outputFile, hdrWindow, hdrAlgorithm, fps, codec).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 3;
        }
    }

    private static async Task<int> ExecuteCoreAsync(
        DirectoryInfo inputPath,
        FileInfo outputFile,
        int? hdrWindow,
        string hdrAlgorithm,
        int fps,
        string codec)
    {
        if (!inputPath.Exists)
        {
            Console.Error.WriteLine($"Input path not found: {inputPath.FullName}");
            return 2;
        }

        // Find data file
        var dataPath = FindDataFile(inputPath.FullName);
        if (dataPath == null)
        {
            Console.Error.WriteLine($"Data file not found in: {inputPath.FullName}");
            return 2;
        }

        // Load or scan index
        var indexPath = FindIndexFile(inputPath.FullName);
        SortedList<ulong, FrameIndex> index;
        PixelFormat detectedFormat;

        if (indexPath != null)
        {
            var json = File.ReadAllText(indexPath);
            var metadata = JsonSerializer.Deserialize<RecordingMetadata>(json) ?? new RecordingMetadata();

            if (metadata.Index.Count == 0)
            {
                Console.Error.WriteLine("Invalid or empty recording index.");
                return 3;
            }

            index = metadata.Index;

            // Detect format from caps or first frame
            var formatStr = metadata.GetFormat();
            if (formatStr != null)
            {
                detectedFormat = formatStr.ToLowerInvariant() == "gray8" ? PixelFormat.Gray8 : PixelFormat.I420;
            }
            else
            {
                detectedFormat = await DetectFormatFromFirstFrameAsync(dataPath, index);
            }

            Console.Error.WriteLine($"Recording: {index.Count} frames (from index)");
        }
        else
        {
            Console.Error.WriteLine("No index file found, scanning MJPEG for frame boundaries...");
            (index, detectedFormat) = await MjpegScanner.ScanFileAsync(dataPath, fps);

            if (index.Count == 0)
            {
                Console.Error.WriteLine("No JPEG frames found in file.");
                return 3;
            }

            Console.Error.WriteLine($"Recording: {index.Count} frames (scanned)");
        }

        // Get dimensions from first frame
        var firstFrame = index.Values.First();
        var firstBuffer = new byte[firstFrame.Size];
        await using (var fs = File.OpenRead(dataPath))
        {
            fs.Seek((long)firstFrame.Start, SeekOrigin.Begin);
            await fs.ReadExactlyAsync(firstBuffer);
        }

        var (width, height, components) = GetJpegInfo(firstBuffer);

        Console.Error.WriteLine($"Dimensions: {width}x{height}");
        Console.Error.WriteLine($"Format: {detectedFormat} (components={components})");
        Console.Error.WriteLine($"Output: {outputFile.FullName}");
        Console.Error.WriteLine($"Codec: {codec}, FPS: {fps}");

        // Validate HDR
        if (hdrWindow.HasValue && (hdrWindow.Value < 2 || hdrWindow.Value > 10))
        {
            Console.Error.WriteLine("HDR window must be between 2 and 10.");
            return 1;
        }

        var sw = Stopwatch.StartNew();

        if (hdrWindow.HasValue)
        {
            await ConvertWithHdrAsync(dataPath, index, outputFile.FullName, width, height, detectedFormat, hdrWindow.Value, hdrAlgorithm, fps, codec);
        }
        else
        {
            await ConvertRawAsync(dataPath, index, outputFile.FullName, width, height, detectedFormat, fps, codec);
        }

        sw.Stop();
        Console.Error.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    private static async Task ConvertWithHdrAsync(
        string dataPath,
        SortedList<ulong, FrameIndex> index,
        string outputPath,
        int width, int height,
        PixelFormat pixelFormat,
        int hdrWindow,
        string algorithm,
        int fps,
        string codec)
    {
        Console.Error.WriteLine($"HDR: window={hdrWindow}, algorithm={algorithm}");

        var blendMode = algorithm.ToLowerInvariant() switch
        {
            "weighted" => HdrBlendMode.Weighted,
            _ => HdrBlendMode.Average
        };

        var rawFrameCount = index.Count;
        var logicalFrameCount = rawFrameCount / hdrWindow;

        Console.Error.WriteLine($"Raw frames: {rawFrameCount}, Output frames: {logicalFrameCount}");

        using var reader = new RecordingFrameReader(dataPath, index);
        using var engine = new MjpegHdrEngine(reader.ReadFrameAsync, width, height, quality: HdrJpegQuality)
        {
            PixelFormat = pixelFormat,
            HdrFrameWindowCount = hdrWindow,
            HdrMode = blendMode,
            Weights = blendMode == HdrBlendMode.Weighted ? HdrWeights.CreateEqual(hdrWindow) : null
        };

        // Determine output color mode
        var isColor = pixelFormat != PixelFormat.Gray8;

        using var writer = new VideoWriter(
            outputPath,
            VideoWriter.Fourcc(codec[0], codec[1], codec[2], codec[3]),
            fps,
            new System.Drawing.Size(width, height),
            isColor);

        if (!writer.IsOpened)
        {
            throw new InvalidOperationException($"Failed to open video writer for: {outputPath}");
        }

        for (int logicalFrame = 0; logicalFrame < logicalFrameCount; logicalFrame++)
        {
            var rawFrameIndex = (ulong)((logicalFrame + 1) * hdrWindow - 1);

            using var result = await engine.GetAsync(rawFrameIndex);

            // Decode JPEG to Mat
            using var mat = new Mat();
            CvInvoke.Imdecode(result.Data.ToArray(), isColor ? ImreadModes.AnyColor : ImreadModes.Grayscale, mat);

            writer.Write(mat);

            if ((logicalFrame + 1) % 100 == 0)
                Console.Error.WriteLine($"Processed {logicalFrame + 1} frames...");
        }

        Console.Error.WriteLine($"Converted {logicalFrameCount} HDR frames to MP4.");
    }

    private static async Task ConvertRawAsync(
        string dataPath,
        SortedList<ulong, FrameIndex> index,
        string outputPath,
        int width, int height,
        PixelFormat pixelFormat,
        int fps,
        string codec)
    {
        var isColor = pixelFormat != PixelFormat.Gray8;

        using var writer = new VideoWriter(
            outputPath,
            VideoWriter.Fourcc(codec[0], codec[1], codec[2], codec[3]),
            fps,
            new System.Drawing.Size(width, height),
            isColor);

        if (!writer.IsOpened)
        {
            throw new InvalidOperationException($"Failed to open video writer for: {outputPath}");
        }

        await using var fileStream = File.OpenRead(dataPath);
        var frameIndex = 0;

        foreach (var (sequence, frame) in index)
        {
            var buffer = new byte[frame.Size];
            fileStream.Seek((long)frame.Start, SeekOrigin.Begin);
            await fileStream.ReadExactlyAsync(buffer);

            using var mat = new Mat();
            CvInvoke.Imdecode(buffer, isColor ? ImreadModes.AnyColor : ImreadModes.Grayscale, mat);

            writer.Write(mat);

            frameIndex++;
            if (frameIndex % 100 == 0)
                Console.Error.WriteLine($"Processed {frameIndex} frames...");
        }

        Console.Error.WriteLine($"Converted {frameIndex} frames to MP4.");
    }

    private static async Task<PixelFormat> DetectFormatFromFirstFrameAsync(string dataPath, SortedList<ulong, FrameIndex> index)
    {
        var firstFrame = index.Values.First();
        var buffer = new byte[Math.Min((int)firstFrame.Size, 1024)];

        await using var stream = File.OpenRead(dataPath);
        stream.Seek((long)firstFrame.Start, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer);

        var (_, _, components) = GetJpegInfo(buffer);
        return components == 1 ? PixelFormat.Gray8 : PixelFormat.I420;
    }

    private static string? FindIndexFile(string directory)
    {
        var candidates = new[] { "index.json", "stream.json", "metadata.json" };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static string? FindDataFile(string directory)
    {
        var candidates = new[] { "stream.mjpeg", "data.mjpeg", "data", "recording.mjpeg" };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
                return path;
        }

        var mjpegFiles = Directory.GetFiles(directory, "*.mjpeg");
        return mjpegFiles.Length > 0 ? mjpegFiles[0] : null;
    }

    private static (int width, int height, int components) GetJpegInfo(ReadOnlySpan<byte> jpegData)
    {
        for (int i = 0; i < jpegData.Length - 9; i++)
        {
            if (jpegData[i] == 0xFF && (jpegData[i + 1] == 0xC0 || jpegData[i + 1] == 0xC2))
            {
                var height = (jpegData[i + 5] << 8) | jpegData[i + 6];
                var width = (jpegData[i + 7] << 8) | jpegData[i + 8];
                var components = jpegData[i + 9];
                return (width, height, components);
            }
        }
        return (0, 0, 0);
    }
}

