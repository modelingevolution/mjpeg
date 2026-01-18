# MJPEG CLI

A command-line tool for extracting frames and converting MJPEG recordings to MP4 with HDR support.

## Installation

```bash
dotnet tool install --global ModelingEvolution.Mjpeg.Cli
```

## Commands

### Extract - Extract JPEG frames from MJPEG recording

```bash
mjpeg-cli extract <input-path> --output-dir=<dir> [--hdr-window=N] [--hdr-algorithm=<mode>]
```

### Convert - Convert MJPEG recording to MP4

```bash
mjpeg-cli convert <input-path> --output=<file.mp4> [--hdr-window=N] [--fps=N] [--codec=<fourcc>]
```

## HDR Processing

Supports exposure bracketing HDR with automatic format detection (Gray8/I420).
