using BenchmarkDotNet.Running;
using ModelingEvolution.Mjpeg.Benchmarks;

// Run all HDR blend benchmarks
BenchmarkSwitcher.FromAssembly(typeof(HdrBlendBenchmarks).Assembly).Run(args);
