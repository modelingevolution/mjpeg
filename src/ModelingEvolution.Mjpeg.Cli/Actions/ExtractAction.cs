using System.Buffers;
using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelingEvolution.Mjpeg.Cli.Commands;

namespace ModelingEvolution.Mjpeg.Cli.Actions;

/// <summary>
/// Executes the 'extract' command - extracts JPEG frames from MJPEG recording.
/// </summary>
public static class ExtractAction
{
    private const int DefaultFps = 25;
    public static int Execute(ParseResult parseResult)
    {
        var inputPath = parseResult.GetValue(ExtractCommand.InputPathArgument)!;
        var outputDir = parseResult.GetValue(ExtractCommand.OutputDirOption)!;
        var hdrWindow = parseResult.GetValue(ExtractCommand.HdrWindowOption);
        var hdrAlgorithm = parseResult.GetValue(ExtractCommand.HdrAlgorithmOption) ?? "avg";

        try
        {
            return ExecuteCore(inputPath, outputDir, hdrWindow, hdrAlgorithm);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 3;
        }
    }

    private static int ExecuteCore(DirectoryInfo inputPath, DirectoryInfo outputDir, int? hdrWindow, string hdrAlgorithm)
    {
        return ExecuteCoreAsync(inputPath, outputDir, hdrWindow, hdrAlgorithm).GetAwaiter().GetResult();
    }

    private static async Task<int> ExecuteCoreAsync(DirectoryInfo inputPath, DirectoryInfo outputDir, int? hdrWindow, string hdrAlgorithm)
    {
        // Validate input
        if (!inputPath.Exists)
        {
            Console.Error.WriteLine($"Input path not found: {inputPath.FullName}");
            return 2;
        }

        // Find data file (.mjpeg or just 'data')
        var dataPath = FindDataFile(inputPath.FullName);
        if (dataPath == null)
        {
            Console.Error.WriteLine($"Data file not found in: {inputPath.FullName}");
            return 2;
        }

        // Create output directory
        outputDir.Create();

        // Try to load index from JSON, or scan MJPEG as fallback
        var indexPath = FindIndexFile(inputPath.FullName);
        RecordingMetadata metadata;
        PixelFormat? detectedFormat = null;

        if (indexPath != null)
        {
            var json = File.ReadAllText(indexPath);
            metadata = JsonSerializer.Deserialize<RecordingMetadata>(json) ?? new RecordingMetadata();

            if (metadata.Index.Count == 0)
            {
                Console.Error.WriteLine("Invalid or empty recording index.");
                return 3;
            }

            Console.Error.WriteLine($"Recording: {metadata.Index.Count} frames (from index)");
        }
        else
        {
            // Fallback: scan MJPEG for frame boundaries
            Console.Error.WriteLine("No index file found, scanning MJPEG for frame boundaries...");

            var (index, format) = await MjpegScanner.ScanFileAsync(dataPath, DefaultFps);

            if (index.Count == 0)
            {
                Console.Error.WriteLine("No JPEG frames found in file.");
                return 3;
            }

            metadata = new RecordingMetadata { Index = index };
            detectedFormat = format;

            Console.Error.WriteLine($"Recording: {index.Count} frames (scanned, detected format: {format})");
        }

        // Validate HDR window
        if (hdrWindow.HasValue)
        {
            if (hdrWindow.Value < 2 || hdrWindow.Value > 10)
            {
                Console.Error.WriteLine("HDR window must be between 2 and 10.");
                return 1;
            }
        }

        var sw = Stopwatch.StartNew();

        if (hdrWindow.HasValue)
        {
            await ExtractWithHdrAsync(dataPath, metadata, outputDir.FullName, hdrWindow.Value, hdrAlgorithm, detectedFormat);
        }
        else
        {
            ExtractRaw(dataPath, metadata, outputDir.FullName);
        }

        sw.Stop();
        Console.Error.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    private static string? FindIndexFile(string directory)
    {
        // Try common index file names
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
        // Try common data file names
        var candidates = new[] { "stream.mjpeg", "data.mjpeg", "data", "recording.mjpeg" };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
                return path;
        }

        // Try any .mjpeg file
        var mjpegFiles = Directory.GetFiles(directory, "*.mjpeg");
        return mjpegFiles.Length > 0 ? mjpegFiles[0] : null;
    }

    private static void ExtractRaw(string dataPath, RecordingMetadata metadata, string outputDir)
    {
        using var fileStream = File.OpenRead(dataPath);
        var frameIndex = 0;

        foreach (var (sequence, frame) in metadata.Index)
        {
            var buffer = new byte[frame.Size];
            fileStream.Seek((long)frame.Start, SeekOrigin.Begin);
            fileStream.ReadExactly(buffer);

            var outputPath = Path.Combine(outputDir, $"{frameIndex}.jpeg");
            File.WriteAllBytes(outputPath, buffer);

            if (frameIndex == 0)
            {
                var dims = GetJpegDimensions(buffer);
                Console.Error.WriteLine($"Dimensions: {dims.width}x{dims.height}");
            }

            frameIndex++;
            if (frameIndex % 100 == 0)
                Console.Error.WriteLine($"Extracted {frameIndex} frames...");
        }

        Console.Error.WriteLine($"Extracted {frameIndex} frames.");
    }

    private static async Task ExtractWithHdrAsync(string dataPath, RecordingMetadata metadata, string outputDir, int hdrWindow, string algorithm, PixelFormat? preDetectedFormat = null)
    {
        // Get dimensions and format from first frame
        var firstFrame = metadata.Index.Values.First();
        await using var fileStream = File.OpenRead(dataPath);
        var firstBuffer = new byte[firstFrame.Size];
        fileStream.Seek((long)firstFrame.Start, SeekOrigin.Begin);
        await fileStream.ReadExactlyAsync(firstBuffer);
        var (width, height, components) = GetJpegInfo(firstBuffer);

        // Determine pixel format: pre-detected > Caps metadata > auto-detect from JPEG
        PixelFormat pixelFormat;
        string formatSource;

        if (preDetectedFormat.HasValue)
        {
            pixelFormat = preDetectedFormat.Value;
            formatSource = "scanned";
        }
        else
        {
            var formatStr = metadata.GetFormat();
            if (formatStr != null)
            {
                pixelFormat = formatStr.ToLowerInvariant() switch
                {
                    "gray8" => PixelFormat.Gray8,
                    "i420" => PixelFormat.I420,
                    _ => components == 1 ? PixelFormat.Gray8 : PixelFormat.I420
                };
                formatSource = $"caps={formatStr}";
            }
            else
            {
                pixelFormat = components == 1 ? PixelFormat.Gray8 : PixelFormat.I420;
                formatSource = "auto-detected";
            }
        }

        Console.Error.WriteLine($"Dimensions: {width}x{height}");
        Console.Error.WriteLine($"Format: {pixelFormat} (components={components}, {formatSource})");
        Console.Error.WriteLine($"HDR: window={hdrWindow}, algorithm={algorithm}");

        var blendMode = algorithm.ToLowerInvariant() switch
        {
            "weighted" => HdrBlendMode.Weighted,
            _ => HdrBlendMode.Average
        };

        // Calculate logical frame count
        var rawFrameCount = metadata.Index.Count;
        var logicalFrameCount = rawFrameCount / hdrWindow;

        Console.Error.WriteLine($"Raw frames: {rawFrameCount}, Output frames: {logicalFrameCount}");

        // Create frame reader with thread-safe file access
        using var reader = new RecordingFrameReader(dataPath, metadata);

        // Create HDR engine (uses pooled encoders/decoders - thread-safe)
        using var engine = new MjpegHdrEngine(reader.ReadFrameAsync, width, height)
        {
            PixelFormat = pixelFormat,
            HdrFrameWindowCount = hdrWindow,
            HdrMode = blendMode,
            Weights = blendMode == HdrBlendMode.Weighted ? HdrWeights.CreateEqual(hdrWindow) : null
        };

        for (int logicalFrame = 0; logicalFrame < logicalFrameCount; logicalFrame++)
        {
            // Calculate raw frame index (last frame in window)
            var rawFrameIndex = (ulong)((logicalFrame + 1) * hdrWindow - 1);

            // GetAsync internally parallelizes fetch+decode of N frames
            using var result = await engine.GetAsync(rawFrameIndex);

            var outputPath = Path.Combine(outputDir, $"{logicalFrame}.jpeg");
            await using var outputStream = File.Create(outputPath);
            await outputStream.WriteAsync(result.Data);

            if ((logicalFrame + 1) % 100 == 0)
                Console.Error.WriteLine($"Processed {logicalFrame + 1} frames...");
        }

        Console.Error.WriteLine($"Extracted {logicalFrameCount} HDR frames.");
    }

    private static (int width, int height) GetJpegDimensions(ReadOnlySpan<byte> jpegData)
    {
        var (width, height, _) = GetJpegInfo(jpegData);
        return (width, height);
    }

    private static (int width, int height, int components) GetJpegInfo(ReadOnlySpan<byte> jpegData)
    {
        // Parse JPEG SOF0/SOF2 marker for dimensions and component count
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
